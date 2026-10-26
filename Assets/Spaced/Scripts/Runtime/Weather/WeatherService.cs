using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>Launch-day weather at a pad: the numbers from the forecast and the launch director's call.</summary>
    public struct LaunchWeather
    {
        /// <summary>False when there is no forecast for that time (far from today, or still downloading).</summary>
        public bool Known;
        public double TemperatureC, WindMs, GustMs, CloudPercent, PrecipitationMm, WaveM;
        public int Code;
        public bool Go;
        public string Reason;

        public string Summary => !Known ? "no forecast for this date"
            : $"{TemperatureC:0}°C, wind {WindMs:0} m/s (gusts {GustMs:0}), cloud {CloudPercent:0}%" + (PrecipitationMm > 0.05 ? $", rain {PrecipitationMm:0.0} mm/h" : "")
              + (WaveM > 0 ? $", waves {WaveM:0.0} m" : "");
    }

    /// <summary>
    /// Real weather. The cloud layer is rebuilt from today's satellite pictures: the geostationary infrared mosaic
    /// (GOES-East, GOES-West and Himawari, every 10 minutes, day and night) and, where those can't see (Europe,
    /// Africa, the Indian Ocean), the latest daily VIIRS true-colour pass. Pads get an hourly Open-Meteo forecast, and
    /// droneship landing areas a wave forecast, for the launch go/no-go. Far from today (no data) the game falls back
    /// to the classic cloud map and always launches.
    /// </summary>
    [DefaultExecutionOrder(-25)]
    public sealed class WeatherService : MonoBehaviour
    {
        const string Gibs = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&CRS=EPSG:4326&BBOX=-90,-180,90,180";
        const string IrLayers = "GOES-West_ABI_Band13_Clean_Infrared,GOES-East_ABI_Band13_Clean_Infrared,Himawari_AHI_Band13_Clean_Infrared";
        const string TrueColorLayer = "VIIRS_NOAA20_CorrectedReflectance_TrueColor";
        public const string CloudAttribution = "Clouds: NASA GIBS (GOES, Himawari, VIIRS)";
        public const string ForecastAttribution = "Weather: Open-Meteo.com";
        const int Width = 2048, Height = 1024;
        /// <summary>Live clouds only within this many days of the real date (that's where pictures exist).</summary>
        const double LiveRangeDays = 3;

        // Falcon 9-style launch commit criteria.
        public const double MaxGustMs = 15;
        public const double MaxPrecipitationMm = 2;
        public const double MaxWaveM = 4;

        /// <summary>Bad weather at the pad scrubs launches.</summary>
        public bool ScrubsEnabled = true;
        public bool LiveClouds { get; private set; }
        public DateTime CloudTimeUtc { get; private set; }
        public string Status { get; private set; } = "";

        SpaceSimulation _sim;
        EarthRenderer _earth;
        byte[] _cover;          // cloud density 0–255, Width × Height, row 0 = north
        byte[] _staticCover;
        bool _building;
        float _cloudTimer;
        DateTime _loadedIrTime = DateTime.MinValue;

        sealed class Forecast
        {
            public double StartJd;
            public double[] Temperature, Wind, Gust, Cloud, Precipitation, Waves;
            public int[] Code;
            public float FetchedAt = -1e6f;
            public bool Loading;
        }

        readonly Dictionary<LaunchSite, Forecast> _forecasts = new Dictionary<LaunchSite, Forecast>();

        public void Init(SpaceSimulation sim, EarthRenderer earth)
        {
            _sim = sim;
            _earth = earth;
            StartCoroutine(TextureCache.Load(EarthRenderer.CloudTextureUrl, Destroy, bytes => _staticCover = DecodeStatic(bytes)));
        }

        // ───────────────────────────── Queries ─────────────────────────────

        /// <summary>Cloud cover (0–1) at a place right now, from the live map when there is one.</summary>
        public float CloudCover(double latDeg, double lonDeg)
        {
            var cover = _cover ?? _staticCover;
            if (cover == null) return 0;
            int x = (int)math.floor((lonDeg + 180.0) / 360.0 * Width);
            int y = (int)math.floor((90.0 - latDeg) / 180.0 * Height);
            x = ((x % Width) + Width) % Width;
            y = math.clamp(y, 0, Height - 1);
            return cover[y * Width + x] / 255f;
        }

        /// <summary>The forecast at a pad for a moment (Julian date), and whether the launch is go.</summary>
        public LaunchWeather At(LaunchSite site, double jd, bool droneship)
        {
            var w = new LaunchWeather { Go = true, Reason = "" };
            if (!_forecasts.TryGetValue(site, out var f) || f.Temperature == null) return w;
            double hours = (jd - f.StartJd) * 24.0;
            if (hours < 0 || hours > f.Temperature.Length - 1) return w;
            int i = (int)math.round(hours);
            w.Known = true;
            w.TemperatureC = f.Temperature[i];
            w.WindMs = f.Wind[i];
            w.GustMs = f.Gust[i];
            w.CloudPercent = f.Cloud[i];
            w.PrecipitationMm = f.Precipitation[i];
            w.Code = f.Code[i];
            w.WaveM = droneship && f.Waves != null && i < f.Waves.Length ? f.Waves[i] : 0;
            if (w.Code >= 95) { w.Go = false; w.Reason = "thunderstorms near the pad"; }
            else if (w.GustMs > MaxGustMs) { w.Go = false; w.Reason = $"ground winds gusting {w.GustMs:0} m/s"; }
            else if (w.PrecipitationMm > MaxPrecipitationMm) { w.Go = false; w.Reason = $"heavy rain ({w.PrecipitationMm:0.0} mm/h)"; }
            else if (w.WaveM > MaxWaveM) { w.Go = false; w.Reason = $"{w.WaveM:0.0} m waves at the droneship"; }
            return w;
        }

        // ───────────────────────────── Frame ─────────────────────────────

        void Update()
        {
            if (_sim == null) return;
            foreach (var site in LaunchSite.All)
            {
                if (!_forecasts.TryGetValue(site, out var f)) _forecasts[site] = f = new Forecast();
                // An hourly forecast is good for a while; refresh every half hour of real time.
                if (!f.Loading && Time.unscaledTime - f.FetchedAt > 1800f) StartCoroutine(FetchForecast(site, f));
            }

            _cloudTimer -= Time.unscaledDeltaTime;
            if (_cloudTimer > 0 || _building) return;
            _cloudTimer = 20f;
            DateTime now = DateTime.UtcNow;
            DateTime sim = _sim.Clock.Utc;
            if (math.abs((sim - now).TotalDays) > LiveRangeDays)
            {
                if (LiveClouds) { _earth.UseStaticClouds(); _cover = null; LiveClouds = false; Status = "classic cloud map (no satellite pictures for this date)"; }
                return;
            }
            // Pictures appear about 40 minutes after they are taken; use the newest one up to the simulated time.
            DateTime latest = now.AddMinutes(-50);
            DateTime t = sim < latest ? sim : latest;
            t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute / 10 * 10, 0, DateTimeKind.Utc);
            // Refresh when the simulated clock has moved half an hour past the picture on screen.
            if (LiveClouds && math.abs((t - _loadedIrTime).TotalMinutes) < 30) return;
            StartCoroutine(BuildClouds(t));
        }

        // ───────────────────────────── Clouds ─────────────────────────────

        IEnumerator BuildClouds(DateTime time)
        {
            _building = true;
            Status = "downloading satellite pictures…";
            byte[] ir = null, color = null;
            string irUrl = $"{Gibs}&LAYERS={IrLayers}&WIDTH={Width}&HEIGHT={Height}&FORMAT=image/png&TRANSPARENT=TRUE&TIME={time:yyyy-MM-ddTHH:mm:00}Z";
            yield return Fetch(irUrl, $"ir_{time:yyyyMMdd_HHmm}.png", b => ir = b);
            // The daily mosaic of the day before is complete; today's is still filling in.
            DateTime day = time.Date.AddDays(-1);
            string colorUrl = $"{Gibs}&LAYERS={TrueColorLayer}&WIDTH={Width}&HEIGHT={Height}&FORMAT=image/jpeg&TIME={day:yyyy-MM-dd}";
            yield return Fetch(colorUrl, $"viirs_{day:yyyyMMdd}.jpg", b => color = b);
            if (ir == null && color == null)
            {
                Status = "satellite pictures unavailable — classic cloud map";
                _building = false;
                _loadedIrTime = time; // don't retry every few seconds
                yield break;
            }

            Color32[] irPixels = Decode(ir), colorPixels = Decode(color);
            var staticCover = _staticCover;
            var task = Task.Run(() => Compose(irPixels, colorPixels, staticCover));
            while (!task.IsCompleted) yield return null;
            if (task.Exception != null)
            {
                Debug.LogWarning("[Spaced] Cloud map: " + task.Exception.InnerException?.Message);
                _building = false;
                yield break;
            }
            _cover = task.Result;
            var texture = new Texture2D(Width, Height, TextureFormat.R8, true)
            {
                name = $"Live clouds {time:u}",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            // Textures start at the bottom row; the map starts at the north.
            var flipped = new byte[_cover.Length];
            for (int y = 0; y < Height; y++) Buffer.BlockCopy(_cover, y * Width, flipped, (Height - 1 - y) * Width, Width);
            texture.SetPixelData(flipped, 0);
            texture.Apply(true, true);
            _earth.SetClouds(texture, Astro.JulianDate(time));
            LiveClouds = true;
            CloudTimeUtc = time;
            _loadedIrTime = time;
            Status = $"live clouds from {time:d MMM HH:mm} UTC";
            _building = false;
        }

        static Color32[] Decode(byte[] bytes)
        {
            if (bytes == null) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!t.LoadImage(bytes, false) || t.width != Width || t.height != Height) return null;
                // Flip to row 0 = north.
                var pixels = t.GetPixels32();
                var flipped = new Color32[pixels.Length];
                for (int y = 0; y < Height; y++) Array.Copy(pixels, y * Width, flipped, (Height - 1 - y) * Width, Width);
                return flipped;
            }
            finally { Destroy(t); }
        }

        /// <summary>The classic cloud map as cover values (resampled to the live map's size, row 0 = north).</summary>
        static byte[] DecodeStatic(byte[] bytes)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!t.LoadImage(bytes, false)) return null;
                var pixels = t.GetPixels32();
                int w = t.width, h = t.height;
                var cover = new byte[Width * Height];
                for (int y = 0; y < Height; y++)
                {
                    int row = (h - 1 - y * h / Height) * w;
                    for (int x = 0; x < Width; x++) cover[y * Width + x] = pixels[row + x * w / Width].r;
                }
                return cover;
            }
            finally { Destroy(t); }
        }

        /// <summary>
        /// Infrared where the geostationary satellites see (cold, bright tops are cloud; the colour-enhanced coldest
        /// tops are thick cloud), true colour elsewhere (white and grey, not tan or blue, is cloud), and the classic
        /// map at the poles where true colour can't tell cloud from ice.
        /// </summary>
        /// <summary>Sub-satellite longitudes of GOES-West, GOES-East and Himawari.</summary>
        static readonly double[] GeostationaryLongitudes = { -137.2, -75.2, 140.7 };

        static byte[] Compose(Color32[] ir, Color32[] color, byte[] fallback)
        {
            var cover = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                double lat = 90.0 - (y + 0.5) * 180.0 / Height;
                double cosLat = math.cos(lat * MathConst.Deg2Rad);
                float polar = Mathf.Clamp01(((float)math.abs(lat) - 58f) / 10f);
                for (int x = 0; x < Width; x++)
                {
                    int i = y * Width + x;
                    double lon = (x + 0.5) * 360.0 / Width - 180.0;
                    float baseCover = fallback != null ? fallback[i] / 255f : 0f;

                    // Daily true colour: white and neutral grey is cloud; tan desert, green land and blue sea are not.
                    float v = baseCover;
                    if (color != null)
                    {
                        var c = color[i];
                        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                        float min = Mathf.Min(r, Mathf.Min(g, b)), max = Mathf.Max(r, Mathf.Max(g, b));
                        if (max > 0.02f)
                        {
                            float whiteness = Mathf.SmoothStep(0.32f, 0.78f, min);
                            float neutral = 1f - Mathf.SmoothStep(0.12f, 0.30f, (max - min) / Mathf.Max(max, 0.05f));
                            v = Mathf.Lerp(whiteness * neutral, baseCover, polar);
                        }
                    }

                    // Live infrared: cold (bright) tops are cloud, the colour-enhanced coldest ones thick cloud. Near the
                    // edge of a satellite's view everything looks colder, so trust it only well inside its disc.
                    if (ir != null && ir[i].a > 128)
                    {
                        double best = -1;
                        foreach (double satLon in GeostationaryLongitudes)
                            best = math.max(best, cosLat * math.cos((lon - satLon) * MathConst.Deg2Rad));
                        float trust = Mathf.SmoothStep(0.42f, 0.64f, (float)best); // 65° → 50° from the sub-satellite point
                        if (trust > 0)
                        {
                            var c = ir[i];
                            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                            float min = Mathf.Min(r, Mathf.Min(g, b)), max = Mathf.Max(r, Mathf.Max(g, b));
                            float fromIr = max - min > 0.18f ? 1f : Mathf.SmoothStep(0.62f, 0.80f, max);
                            v = Mathf.Lerp(v, fromIr, trust);
                        }
                    }
                    // A little contrast: thin veils thin out, thick decks become crisp, like the classic map.
                    v = Mathf.SmoothStep(0.12f, 0.88f, Mathf.Clamp01(v));
                    cover[i] = (byte)(v * 255f);
                }
            }
            return cover;
        }

        static IEnumerator Fetch(string url, string fileName, Action<byte[]> done)
        {
            string folder = Path.Combine(Application.persistentDataPath, "weather");
            string path = Path.Combine(folder, fileName);
            if (File.Exists(path)) { done(File.ReadAllBytes(path)); yield break; }
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 90;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success || request.downloadHandler.data == null
                    || !(request.GetResponseHeader("Content-Type") ?? "").StartsWith("image"))
                {
                    Debug.LogWarning($"[Spaced] Weather picture unavailable: {request.error}");
                    yield break;
                }
                var bytes = request.downloadHandler.data;
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, bytes);
                Prune(folder);
                done(bytes);
            }
        }

        /// <summary>Keeps the weather cache to the newest few dozen pictures.</summary>
        static void Prune(string folder)
        {
            var files = new DirectoryInfo(folder).GetFiles();
            if (files.Length <= 40) return;
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Length - 40; i++)
            {
                try { files[i].Delete(); } catch (IOException) { }
            }
        }

        // ───────────────────────────── Forecasts ─────────────────────────────

        IEnumerator FetchForecast(LaunchSite site, Forecast f)
        {
            f.Loading = true;
            var inv = CultureInfo.InvariantCulture;
            string lat = site.LatitudeDeg.ToString("0.###", inv), lon = site.LongitudeDeg.ToString("0.###", inv);
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat + "&longitude=" + lon +
                         "&hourly=temperature_2m,wind_speed_10m,wind_gusts_10m,cloud_cover,precipitation,weather_code" +
                         "&wind_speed_unit=ms&past_days=3&forecast_days=7&timezone=UTC";
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 30;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) Parse(request.downloadHandler.text, f);
                else Debug.LogWarning($"[Spaced] Forecast for {site.ShortName} unavailable: {request.error}");
            }
            if (!string.IsNullOrEmpty(site.DroneshipName))
            {
                DroneshipArea(site, out double dLat, out double dLon);
                string marine = "https://marine-api.open-meteo.com/v1/marine?latitude=" + dLat.ToString("0.##", inv) + "&longitude=" + dLon.ToString("0.##", inv) +
                                "&hourly=wave_height&past_days=3&forecast_days=7&timezone=UTC";
                using (var request = UnityWebRequest.Get(marine))
                {
                    request.timeout = 30;
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        MarineResponse m = null;
                        try { m = JsonUtility.FromJson<MarineResponse>(request.downloadHandler.text.Replace("null", "0")); }
                        catch (ArgumentException) { }
                        if (m?.hourly?.wave_height != null) f.Waves = Array.ConvertAll(m.hourly.wave_height, v => (double)v);
                    }
                }
            }
            f.FetchedAt = Time.unscaledTime;
            f.Loading = false;
        }

        /// <summary>Where the droneship waits: ~600 km downrange (north-east of Florida, south of California).</summary>
        static void DroneshipArea(LaunchSite site, out double lat, out double lon)
        {
            if (site.PrefersSouth) { lat = site.LatitudeDeg - 5; lon = site.LongitudeDeg - 1; }
            else { lat = site.LatitudeDeg + 3.5; lon = site.LongitudeDeg + 4.5; }
        }

        static void Parse(string json, Forecast f)
        {
            ForecastResponse r;
            try { r = JsonUtility.FromJson<ForecastResponse>(json.Replace("null", "0")); }
            catch (ArgumentException) { return; }
            var h = r?.hourly;
            if (h?.time == null || h.time.Length == 0) return;
            if (!DateTime.TryParse(h.time[0] + ":00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start)) return;
            f.StartJd = Astro.JulianDate(DateTime.SpecifyKind(start, DateTimeKind.Utc));
            f.Temperature = Array.ConvertAll(h.temperature_2m, v => (double)v);
            f.Wind = Array.ConvertAll(h.wind_speed_10m, v => (double)v);
            f.Gust = Array.ConvertAll(h.wind_gusts_10m, v => (double)v);
            f.Cloud = Array.ConvertAll(h.cloud_cover, v => (double)v);
            f.Precipitation = Array.ConvertAll(h.precipitation, v => (double)v);
            f.Code = h.weather_code;
        }

        // Open-Meteo JSON (nulls read as 0).
        [Serializable] sealed class ForecastResponse { public Hourly hourly; }
        [Serializable] sealed class Hourly
        {
            public string[] time;
            public float[] temperature_2m, wind_speed_10m, wind_gusts_10m, cloud_cover, precipitation;
            public int[] weather_code;
        }
        [Serializable] sealed class MarineResponse { public MarineHourly hourly; }
        [Serializable] sealed class MarineHourly { public float[] wave_height; }
    }
}
