using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>
    /// Fetches map tiles once and keeps them on disk under persistentDataPath.
    /// <list type="bullet">
    /// <item>Imagery: Sentinel-2 cloudless 2016, EOX IT Services GmbH (CC BY 4.0, modified Copernicus Sentinel data).</item>
    /// <item>Elevation: Terrain Tiles on AWS (Mapzen "Terrarium" encoding; SRTM, GMTED2010, ETOPO1 and others).</item>
    /// <item>Night lights: NASA Black Marble 2016 (VIIRS), served by NASA GIBS, to zoom 8 (~600 m).</item>
    /// </list>
    /// </summary>
    public static class TileDownloads
    {
        public const string ImageryUrl = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{0}/{1}/{2}.jpg"; // z / y / x
        public const string TerrainUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";          // z / x / y
        public const string NightUrl = "https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/VIIRS_Black_Marble/default/2016-01-01/GoogleMapsCompatible_Level8/{0}/{1}/{2}.png"; // z / y / x
        public const int MaxNightZoom = 8;
        public const string TerrainAttribution = "Elevation: Terrain Tiles on AWS (Mapzen; SRTM, GMTED2010, ETOPO1)";
        public const string NightAttribution = "Night lights: NASA Black Marble 2016 (Earth Observatory, GIBS)";

        /// <summary>
        /// Calls <paramref name="onDone"/> with the tile's bytes. On failure, <paramref name="onFailed"/> (if given)
        /// learns whether the tile doesn't exist (true) or the network failed (false, worth retrying); without it
        /// <paramref name="onDone"/> gets null.
        /// </summary>
        public static IEnumerator Imagery(TileId t, Action<byte[]> onDone, Action<bool> onFailed = null) =>
            Fetch(string.Format(ImageryUrl, t.Z, t.Y, t.X),
                Path.Combine(Application.persistentDataPath, "imagery", "s2cloudless", t.Z.ToString(), t.Y.ToString(), t.X + ".jpg"), onDone, onFailed);

        public static IEnumerator Terrain(TileId t, Action<byte[]> onDone, Action<bool> onFailed = null) =>
            Fetch(string.Format(TerrainUrl, t.Z, t.X, t.Y),
                Path.Combine(Application.persistentDataPath, "terrain", "terrarium", t.Z.ToString(), t.X.ToString(), t.Y + ".png"), onDone, onFailed);

        public static IEnumerator Night(TileId t, Action<byte[]> onDone, Action<bool> onFailed = null) =>
            Fetch(string.Format(NightUrl, t.Z, t.Y, t.X),
                Path.Combine(Application.persistentDataPath, "night", "blackmarble2016", t.Z.ToString(), t.Y.ToString(), t.X + ".png"), onDone, onFailed);

        static IEnumerator Fetch(string url, string path, Action<byte[]> onDone, Action<bool> onFailed)
        {
            if (File.Exists(path))
            {
                byte[] cached = null;
                try { cached = File.ReadAllBytes(path); }
                catch (IOException) { }
                onDone(cached);
                yield break;
            }

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    bool notFound = request.responseCode == 404 || request.responseCode == 400;
                    if (onFailed != null) onFailed(notFound);
                    else onDone(null);
                    yield break;
                }
                byte[] bytes = request.downloadHandler.data;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, bytes);
                }
                catch (IOException) { }
                onDone(bytes);
            }
        }
    }
}
