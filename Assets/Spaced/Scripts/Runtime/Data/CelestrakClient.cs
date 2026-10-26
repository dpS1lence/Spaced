using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>One CCSDS OMM record as served by CelesTrak's JSON API. Field names must match the JSON keys.</summary>
    [Serializable]
    public struct OmmRecord
    {
        public string OBJECT_NAME;
        public string OBJECT_ID;
        public string EPOCH;
        public double MEAN_MOTION;
        public double ECCENTRICITY;
        public double INCLINATION;
        public double RA_OF_ASC_NODE;
        public double ARG_OF_PERICENTER;
        public double MEAN_ANOMALY;
        public int NORAD_CAT_ID;
        public double BSTAR;
        public double MEAN_MOTION_DOT;

        public double EpochJulianDate =>
            Astro.JulianDate(DateTime.Parse(EPOCH, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
    }

    /// <summary>
    /// Downloads GP element sets from CelesTrak (https://celestrak.org/NORAD/documentation/gp-data-formats.php).
    /// CelesTrak refreshes data every ~2 hours and blocks clients that poll harder than that, so each
    /// group is cached on disk and only re-downloaded once the cache is older than <see cref="CacheLifetime"/>.
    /// </summary>
    public sealed class CelestrakClient
    {
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(2);
        const string UrlFormat = "https://celestrak.org/NORAD/elements/gp.php?GROUP={0}&FORMAT=json";

        /// <summary>
        /// Smaller groups that together cover most of a big one. Used when the big group is refused, e.g. when it
        /// was already downloaded from this network since CelesTrak's last update and nothing is cached here.
        /// </summary>
        static readonly Dictionary<string, string[]> Fallbacks = new Dictionary<string, string[]>
        {
            ["active"] = new[]
            {
                "starlink", "oneweb", "kuiper", "stations", "gnss", "geo", "weather", "science", "resource",
                "cubesat", "planet", "spire", "iridium-NEXT", "orbcomm", "globalstar", "military", "last-30-days",
            },
        };

#pragma warning disable 0649 // assigned by JsonUtility
        [Serializable]
        struct Wrapper { public OmmRecord[] items; }
#pragma warning restore 0649

        public string Status { get; private set; } = "";

        static string CacheDirectory => Path.Combine(Application.persistentDataPath, "celestrak");
        static string CachePath(string group) => Path.Combine(CacheDirectory, group + ".json");

        /// <summary>Fetches all groups, de-duplicated by NORAD id, then calls <paramref name="onComplete"/>.</summary>
        public IEnumerator FetchGroups(IReadOnlyList<string> groups, Action<List<OmmRecord>> onComplete)
        {
            var records = new List<OmmRecord>();
            var seen = new HashSet<int>();
            var failed = new List<string>();
            var queue = new List<string>(groups);

            for (int g = 0; g < queue.Count; g++)
            {
                string group = queue[g];
                Status = $"Loading '{group}' ({g + 1}/{queue.Count})…";
                string json = null;
                yield return LoadGroupJson(group, result => json = result);

                if (json == null)
                {
                    if (Fallbacks.TryGetValue(group, out var parts))
                    {
                        Debug.Log($"[Spaced] Loading '{group}' from {parts.Length} smaller CelesTrak groups instead.");
                        foreach (var part in parts) if (!queue.Contains(part)) queue.Add(part);
                    }
                    else
                    {
                        failed.Add(group);
                    }
                    continue;
                }

                OmmRecord[] parsed;
                try
                {
                    parsed = JsonUtility.FromJson<Wrapper>("{\"items\":" + json + "}").items;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Spaced] Could not parse CelesTrak group '{group}': {e.Message}");
                    failed.Add(group);
                    continue;
                }

                if (parsed == null) continue;
                foreach (var record in parsed)
                {
                    if (record.MEAN_MOTION <= 0 || !seen.Add(record.NORAD_CAT_ID)) continue;
                    records.Add(record);
                }
            }

            Status = failed.Count == 0
                ? $"{records.Count:N0} objects from CelesTrak"
                : $"{records.Count:N0} objects (failed: {string.Join(", ", failed)})";
            onComplete(records);
        }

        IEnumerator LoadGroupJson(string group, Action<string> onResult)
        {
            string path = CachePath(group);
            bool cached = File.Exists(path);
            if (cached && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheLifetime)
            {
                onResult(File.ReadAllText(path));
                yield break;
            }

            using (var request = UnityWebRequest.Get(string.Format(UrlFormat, UnityWebRequest.EscapeURL(group))))
            {
                request.timeout = 90;
                yield return request.SendWebRequest();

                string body = request.downloadHandler?.text;
                // CelesTrak answers throttled or invalid queries with a plain-text message, not a JSON array.
                if (request.result == UnityWebRequest.Result.Success && body != null && body.TrimStart().StartsWith("["))
                {
                    Directory.CreateDirectory(CacheDirectory);
                    File.WriteAllText(path, body);
                    onResult(body);
                    yield break;
                }

                string reason = string.IsNullOrWhiteSpace(body) ? request.error : $"{request.error}: {body.Trim()}";
                if (reason != null && reason.Length > 240) reason = reason.Substring(0, 240);
                if (cached)
                {
                    Debug.LogWarning($"[Spaced] CelesTrak '{group}' unavailable ({reason}); using cached copy from {File.GetLastWriteTimeUtc(path):u}.");
                    onResult(File.ReadAllText(path));
                }
                else
                {
                    Debug.LogWarning($"[Spaced] CelesTrak '{group}' unavailable and not cached: {reason}");
                    onResult(null);
                }
            }
        }
    }
}
