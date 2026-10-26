using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>What a satellite is for. Each role has its own tools in the My Satellites panel.</summary>
    public enum PayloadRole
    {
        None,
        /// <summary>Earth observation: takes pictures of the ground below.</summary>
        Imaging,
        /// <summary>Broadband / relay: covers the ground within its footprint.</summary>
        Communications,
        /// <summary>GNSS: a position fix needs four satellites in view.</summary>
        Navigation,
    }

    /// <summary>A set of your satellites launched together, controlled as one.</summary>
    public sealed class SatelliteGroup
    {
        public int Id;
        public string Name;
        public SatelliteClass Class;
        public PayloadRole Role;
        public double CreatedJd;
        public bool AutoCapture;
        public double LastCaptureJd;
        public int Alive;
        public int Maneuvering;
        public double MeanAltitudeKm;
        public bool StationKeeping = true;
        /// <summary>Stable ids of the group's live satellites (refreshed twice a second, capped).</summary>
        public readonly List<uint> MemberIds = new List<uint>();
        public bool AvoidsCollisions = true;

        /// <summary>Ground sampling distance of the camera at 500 km, metres per pixel.</summary>
        public double CameraResolutionAt500Km => Class.Model == ModelKind.CubeSat ? 4.0 : Class.Model == ModelKind.Smallsat ? 1.0 : 0.5;

        /// <summary>Camera field of view (degrees): wide for CubeSats, narrow for big telescopes.</summary>
        public double CameraFovDeg => Class.Model == ModelKind.CubeSat ? 2.4 : Class.Model == ModelKind.Smallsat ? 1.4 : 0.9;
    }

    /// <summary>A place you want photographed; the first imaging satellite to pass within reach in daylight takes it.</summary>
    public sealed class ImagingTarget
    {
        public string Name;
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double RequestedJd;
        public bool Captured;
        public CapturedImage Image;
        /// <summary>The next daylight pass within reach (clouds aside), or NaN if there's none; see <see cref="FleetManager.NextPass"/>.</summary>
        public double NextPassJd = double.NaN;
        public string NextPassSatellite;
        /// <summary>False until the first pass prediction is done.</summary>
        public bool PassPredicted;
        internal float PassPredictedAt = -1e6f;
        internal Task PassTask;
    }

    /// <summary>A picture taken by one of your imaging satellites.</summary>
    public sealed class CapturedImage
    {
        public double SwathKm;
        public double OffNadirDeg;
        public double AltitudeKm;
        public ImagingTarget Target;
        public Texture2D Texture;
        public string Satellite;
        public double JulianDate;
        public double LatitudeDeg;
        public double LongitudeDeg;
        public double ResolutionM;
        public bool Night;
        /// <summary>Cloud cover over the spot when the picture was taken (0–1).</summary>
        public float Cloud;
        public string SavedPath;
    }

    /// <summary>
    /// Your satellites as fleets: per-group counts and commands, communications/navigation coverage of the
    /// globe, and the imaging gallery.
    /// </summary>
    [DefaultExecutionOrder(-35)]
    public sealed class FleetManager : MonoBehaviour
    {
        public const int CoverageWidth = 360, CoverageHeight = 180;
        const int MaxGallery = 24;
        const double MinElevationCommsDeg = 25, MinElevationNavDeg = 10;

        public readonly List<SatelliteGroup> Groups = new List<SatelliteGroup>();
        public readonly List<CapturedImage> Gallery = new List<CapturedImage>();
        public readonly List<ImagingTarget> Targets = new List<ImagingTarget>();
        /// <summary>Furthest a camera may point away from straight down to reach a target.</summary>
        public double MaxOffNadirDeg = 30;
        /// <summary>Real cloud cover at a place (latitude, longitude → 0–1), if weather is available.</summary>
        public Func<double, double, float> CloudCover;
        /// <summary>Requested pictures wait for a pass with less cloud than this.</summary>
        public const float MaxTargetCloud = 0.5f;
        public event Action<CapturedImage> ImageCaptured;

        /// <summary>Equirectangular map (Earth-fixed): R = communications coverage, G = navigation fix available.</summary>
        public Texture2D CoverageTexture { get; private set; }
        public bool ShowCoverage = true;
        public double CommsCoveragePercent { get; private set; }
        public double NavCoveragePercent { get; private set; }

        SpaceSimulation _sim;
        float _timer;
        NativeArray<int> _commsCount, _navCount;
        NativeList<double3> _commsSats, _navSats;
        Color32[] _coveragePixels;
        bool _capturing;

        public void Init(SpaceSimulation sim)
        {
            _sim = sim;
            _commsCount = new NativeArray<int>(CoverageWidth * CoverageHeight, Allocator.Persistent);
            _navCount = new NativeArray<int>(CoverageWidth * CoverageHeight, Allocator.Persistent);
            _commsSats = new NativeList<double3>(256, Allocator.Persistent);
            _navSats = new NativeList<double3>(64, Allocator.Persistent);
            _coveragePixels = new Color32[CoverageWidth * CoverageHeight];
            CoverageTexture = new Texture2D(CoverageWidth, CoverageHeight, TextureFormat.RGBA32, false)
            {
                name = "Coverage",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            CoverageTexture.SetPixels32(_coveragePixels);
            CoverageTexture.Apply();
        }

        void OnDestroy()
        {
            if (_commsCount.IsCreated) _commsCount.Dispose();
            if (_navCount.IsCreated) _navCount.Dispose();
            if (_commsSats.IsCreated) _commsSats.Dispose();
            if (_navSats.IsCreated) _navSats.Dispose();
        }

        public SatelliteGroup CreateGroup(string name, SatelliteClass satClass, PayloadRole role, bool maneuverable)
        {
            var group = new SatelliteGroup
            {
                Id = Groups.Count,
                Name = name,
                Class = satClass,
                Role = role,
                CreatedJd = _sim.Clock.JulianDate,
                StationKeeping = maneuverable,
                AvoidsCollisions = maneuverable,
            };
            Groups.Add(group);
            return group;
        }

        public SatelliteGroup Group(int id) => id >= 0 && id < Groups.Count ? Groups[id] : null;

        public static PayloadRole DefaultRole(SatelliteClass satClass)
        {
            switch (satClass.Model)
            {
                case ModelKind.CubeSat:
                case ModelKind.Smallsat: return PayloadRole.Imaging;
                case ModelKind.Starlink:
                case ModelKind.Comsat: return PayloadRole.Communications;
                default: return PayloadRole.None;
            }
        }

        /// <summary>Catalog indices of a group's live satellites.</summary>
        public List<int> Members(SatelliteGroup group, int limit = int.MaxValue)
        {
            var result = new List<int>();
            var catalog = _sim.Catalog;
            for (int i = 0; i < catalog.Count && result.Count < limit; i++)
                if (catalog.Groups[i] == group.Id && catalog.Physics[i].IsAlive) result.Add(i);
            return result;
        }

        // ───────────────────────────── Commands ─────────────────────────────

        /// <summary>Moves every satellite of the group to a circular orbit at a new altitude (electric propulsion, days).</summary>
        public int ChangeAltitude(SatelliteGroup group, double altitudeKm)
        {
            var catalog = _sim.Catalog;
            int count = 0;
            foreach (int i in Members(group))
            {
                var el = catalog.Elements[i];
                double current = el.A - EarthConstants.RadiusKm;
                el.A = EarthConstants.RadiusKm + altitudeKm;
                el.E = 0;
                el.N = OrbitMath.KeplerMeanMotion(el.A);
                // Low-thrust spiral: roughly 100 km of altitude change per day.
                double days = math.max(1.0, math.abs(altitudeKm - current) / 100.0);
                catalog.SetManeuver(i, el, days * MathConst.SecondsPerDay);
                count++;
            }
            _sim.Log($"{group.Name}: {count:N0} satellites moving to {altitudeKm:N0} km");
            return count;
        }

        public void SetStationKeeping(SatelliteGroup group, bool on)
        {
            group.StationKeeping = on;
            SetFlag(group, ObjectFlags.StationKeeping, on);
        }

        public void SetCollisionAvoidance(SatelliteGroup group, bool on)
        {
            group.AvoidsCollisions = on;
            SetFlag(group, ObjectFlags.Maneuverable, on);
        }

        void SetFlag(SatelliteGroup group, ObjectFlags flag, bool on)
        {
            var catalog = _sim.Catalog;
            foreach (int i in Members(group))
            {
                var physics = catalog.Physics[i];
                physics.Flags = on ? physics.Flags | flag : physics.Flags & ~flag;
                catalog.Physics[i] = physics;
            }
        }

        public int Deorbit(SatelliteGroup group)
        {
            var catalog = _sim.Catalog;
            var rng = new Unity.Mathematics.Random((uint)(group.Id * 7919 + 17));
            int count = 0;
            foreach (int i in Members(group))
            {
                var target = catalog.Elements[i];
                double apogee = target.A * (1 + target.E), perigee = EarthConstants.RadiusKm + 60.0;
                target.A = 0.5 * (apogee + perigee);
                target.E = (apogee - perigee) / (apogee + perigee);
                target.N = OrbitMath.KeplerMeanMotion(target.A);
                var physics = catalog.Physics[i];
                physics.Flags &= ~ObjectFlags.StationKeeping;
                catalog.Physics[i] = physics;
                catalog.SetManeuver(i, target, MathConst.SecondsPerDay * rng.NextDouble(0.5, 2.0));
                count++;
            }
            _sim.Log($"{group.Name}: deorbit burns for {count:N0} satellites");
            return count;
        }

        public int Remove(SatelliteGroup group)
        {
            int count = 0;
            foreach (int i in Members(group))
            {
                _sim.Catalog.Kill(i);
                count++;
            }
            _sim.Log($"{group.Name}: removed {count:N0} satellites");
            return count;
        }

        // ───────────────────────────── Imaging ─────────────────────────────

        /// <summary>
        /// Takes a picture of the ground under one satellite. Detail follows altitude and camera class, limited by the
        /// 10 m Sentinel-2 source imagery. Over the night side you get a (nearly) black frame, like the real thing.
        /// </summary>
        public bool Capture(int index, ImagingTarget target = null, double? atJd = null, double3? satEcefKm = null)
        {
            if (_capturing || index < 0) return false;
            var catalog = _sim.Catalog;
            var group = Group(catalog.Groups[index]);
            // A pass found between two frames at high time warp is taken as of its own moment.
            double jd = atJd ?? _sim.Clock.JulianDate;
            double3 satEcef = satEcefKm ?? Astro.EciToEcef(catalog.PositionsKm[index], jd);
            Astro.EcefToGeo(satEcef, out double subLat, out double subLon, out double altitude);
            double lat = target?.LatitudeDeg ?? subLat, lon = target?.LongitudeDeg ?? subLon;
            if (math.abs(lat) > 84) return false;

            // Looking at a point off to the side makes the slant range longer and the footprint bigger.
            double3 ground = Astro.GeoToEcef(lat, lon, 0);
            double slantKm = math.distance(satEcef, ground);
            double offNadir = math.acos(math.clamp(math.dot(math.normalize(-satEcef), math.normalize(ground - satEcef)), -1, 1)) * MathConst.Rad2Deg;
            double fov = group?.CameraFovDeg ?? 1.5;
            double swathKm = 2.0 * slantKm * math.tan(fov * 0.5 * MathConst.Deg2Rad);

            bool night = math.dot(math.normalize(ground), Astro.EciToEcef(Astro.SunDirection(jd), jd)) < -0.05;
            float cloud = CloudCover?.Invoke(lat, lon) ?? 0f;
            // Frame the swath with 3 × 3 tiles; detail is limited by the 10 m source imagery.
            double tileKmAtZ0 = 40075.0 * math.cos(lat * MathConst.Deg2Rad);
            int zoom = (int)math.clamp(math.floor(math.log2(3.0 * tileKmAtZ0 / math.max(swathKm, 0.1))), 5, 14);
            double sensor = (group?.CameraResolutionAt500Km ?? 2.0) * slantKm / 500.0;
            double framePixel = swathKm * 1000.0 / 768.0;

            var image = new CapturedImage
            {
                Satellite = catalog.Names[index],
                JulianDate = jd,
                LatitudeDeg = lat,
                LongitudeDeg = lon,
                ResolutionM = math.max(math.max(sensor, framePixel), 10.0),
                Night = night,
                Cloud = cloud,
                SwathKm = swathKm,
                OffNadirDeg = offNadir,
                AltitudeKm = altitude,
                Target = target,
            };
            _capturing = true;
            StartCoroutine(new ImageryMosaic().Load(lat, lon, zoom, 3, 3, mosaic =>
            {
                _capturing = false;
                image.Texture = mosaic.Texture;
                if (night) Darken(image.Texture);
                else if (cloud > 0.12f) AddClouds(image.Texture, cloud, (int)(jd * 1440) ^ index);
                Gallery.Insert(0, image);
                while (Gallery.Count > MaxGallery)
                {
                    Destroy(Gallery[Gallery.Count - 1].Texture);
                    Gallery.RemoveAt(Gallery.Count - 1);
                }
                if (group != null) group.LastCaptureJd = jd;
                if (target != null)
                {
                    target.Captured = true;
                    target.Image = image;
                }
                ImageCaptured?.Invoke(image);
                string what = target != null ? target.Name : $"{lat:0.00}°, {lon:0.00}°";
                _sim.Log($"{image.Satellite}: imaged {what}{(night ? " (night pass)" : "")}");
            }, keepReadable: true));
            return true;
        }

        /// <summary>Real clouds were over the spot: lay soft, lumpy cloud over the picture in proportion to the cover.</summary>
        static void AddClouds(Texture2D texture, float cover, int seed)
        {
            var pixels = texture.GetPixels32();
            int w = texture.width, h = texture.height;
            float ox = (seed & 1023) * 0.37f, oy = ((seed >> 10) & 1023) * 0.53f;
            float threshold = 1f - cover;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float n = ProceduralTextures.Fbm(x / 90f + ox, y / 90f + oy, 5);
                    float a = Mathf.SmoothStep(threshold - 0.12f, threshold + 0.18f, n * 0.55f + cover * 0.45f);
                    if (a <= 0.001f) continue;
                    int i = y * w + x;
                    var c = pixels[i];
                    byte v = (byte)(225 + 30 * n);
                    pixels[i] = new Color32((byte)Mathf.Lerp(c.r, v, a), (byte)Mathf.Lerp(c.g, v, a), (byte)Mathf.Lerp(c.b, v + 4, a), 255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
        }

        static void Darken(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            var random = new System.Random(1);
            for (int i = 0; i < pixels.Length; i++)
            {
                byte noise = (byte)random.Next(0, 6);
                pixels[i] = new Color32((byte)(pixels[i].r / 14 + noise), (byte)(pixels[i].g / 14 + noise), (byte)(pixels[i].b / 12 + noise), 255);
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
        }

        public ImagingTarget AddTarget(double latDeg, double lonDeg, string name = null)
        {
            var target = new ImagingTarget
            {
                Name = name ?? $"Target {Targets.Count + 1}",
                LatitudeDeg = latDeg,
                LongitudeDeg = lonDeg,
                RequestedJd = _sim.Clock.JulianDate,
            };
            Targets.Add(target);
            _sim.Log($"Imaging request: {target.Name} at {latDeg:0.000}°, {lonDeg:0.000}°");
            return target;
        }

        /// <summary>Time step for pass searches: a pass in reach lasts a minute or more even from a low orbit.</summary>
        const double PassStepSeconds = 20;
        double _servedJd = double.NaN;
        readonly List<int> _imagers = new List<int>();
        readonly List<bool> _clearSky = new List<bool>();

        /// <summary>
        /// Hands each open target to the first imaging satellite that can see it in daylight under a clear-enough sky.
        /// At high time warp a satellite crosses a whole pass between two frames, so the time since the last check
        /// (up to a day) is searched in short steps instead of looking only at "now".
        /// </summary>
        void ServeTargets()
        {
            double jd = _sim.Clock.JulianDate;
            if (Targets.Count == 0 || double.IsNaN(_servedJd) || jd < _servedJd) _servedJd = jd;
            if (_capturing || Targets.Count == 0) return;
            double from = math.max(_servedJd, jd - 1.0);
            _servedJd = jd;
            var sats = ImagingSatellites();
            if (sats.Count == 0) return;

            _clearSky.Clear();
            foreach (var target in Targets)
                _clearSky.Add(!target.Captured && (CloudCover?.Invoke(target.LatitudeDeg, target.LongitudeDeg) ?? 0f) <= MaxTargetCloud);
            if (!_clearSky.Contains(true)) return;

            var catalog = _sim.Catalog;
            int steps = math.max(1, (int)math.ceil((jd - from) * MathConst.SecondsPerDay / PassStepSeconds));
            for (int k = 1; k <= steps; k++)
            {
                double t = from + (jd - from) * k / steps;
                for (int n = 0; n < Targets.Count; n++)
                {
                    if (!_clearSky[n]) continue;
                    var target = Targets[n];
                    double3 ground = Astro.GeoToEcef(target.LatitudeDeg, target.LongitudeDeg, 0);
                    if (!Daylight(ground, t)) continue;
                    foreach (int i in sats)
                    {
                        double3 sat = EcefAt(catalog.Elements[i], jd, t);
                        if (!InReach(sat, ground, MaxOffNadirDeg)) continue;
                        Capture(i, target, t, sat);
                        _servedJd = t; // other targets passed later in this frame are found next frame
                        return;
                    }
                }
            }
        }

        /// <summary>Catalog indices of your live imaging satellites.</summary>
        List<int> ImagingSatellites()
        {
            _imagers.Clear();
            var catalog = _sim.Catalog;
            foreach (var group in Groups)
            {
                if (group.Role != PayloadRole.Imaging || group.Alive == 0) continue;
                foreach (uint id in group.MemberIds)
                {
                    int i = catalog.IndexOf(id);
                    if (i >= 0 && catalog.Physics[i].IsAlive) _imagers.Add(i);
                }
            }
            return _imagers;
        }

        public bool HasImagingSatellites => _sim != null && _sim.Catalog != null && ImagingSatellites().Count > 0;

        /// <summary>
        /// When the next daylight pass within reach of a target comes (clouds aside), searched up to
        /// <paramref name="days"/> ahead on a worker thread and refreshed every few seconds. NaN if there's none.
        /// </summary>
        public double NextPass(ImagingTarget target, out string satellite, double days = 14)
        {
            satellite = target.NextPassSatellite;
            double jd = _sim.Clock.JulianDate;
            bool stale = Time.unscaledTime - target.PassPredictedAt > 10 || (!double.IsNaN(target.NextPassJd) && target.NextPassJd < jd);
            if (stale && !target.Captured && (target.PassTask == null || target.PassTask.IsCompleted))
            {
                target.PassPredictedAt = Time.unscaledTime;
                var sats = ImagingSatellites();
                var elements = new OrbitElements[sats.Count];
                var names = new string[sats.Count];
                for (int k = 0; k < sats.Count; k++)
                {
                    elements[k] = _sim.Catalog.Elements[sats[k]];
                    names[k] = _sim.Catalog.Names[sats[k]];
                }
                double lat = target.LatitudeDeg, lon = target.LongitudeDeg, maxOffNadir = MaxOffNadirDeg;
                target.PassTask = Task.Run(() =>
                {
                    double pass = FindPass(elements, jd, lat, lon, maxOffNadir, days, out int which);
                    target.NextPassSatellite = which >= 0 ? names[which] : null;
                    target.NextPassJd = pass;
                    target.PassPredicted = true;
                });
            }
            return target.NextPassJd;
        }

        /// <summary>
        /// First daylight moment within <paramref name="days"/> of <paramref name="nowJd"/> that one of the orbits
        /// (elements valid at <paramref name="nowJd"/>) has the spot in camera reach; NaN if never. Thread-safe.
        /// </summary>
        public static double FindPass(OrbitElements[] sats, double nowJd, double lat, double lon, double maxOffNadirDeg, double days, out int which)
        {
            which = -1;
            double3 ground = Astro.GeoToEcef(lat, lon, 0);
            double step = PassStepSeconds / MathConst.SecondsPerDay;
            for (double t = nowJd; t < nowJd + days; t += step)
            {
                if (!Daylight(ground, t)) continue;
                for (int k = 0; k < sats.Length; k++)
                    if (InReach(EcefAt(sats[k], nowJd, t), ground, maxOffNadirDeg))
                    {
                        which = k;
                        return t;
                    }
            }
            return double.NaN;
        }

        /// <summary>Earth-fixed position (km) of a satellite at a time near now, drifting its mean elements with J2.</summary>
        static double3 EcefAt(OrbitElements el, double nowJd, double atJd)
        {
            double dt = (atJd - nowJd) * MathConst.SecondsPerDay;
            if (dt != 0)
            {
                OrbitMath.SecularRates(el, out double raanDot, out double argPDot, out double mDot);
                el.Raan += raanDot * dt;
                el.ArgP += argPDot * dt;
                el.M = OrbitMath.WrapAngle(el.M + mDot * dt);
            }
            OrbitMath.ToStateVector(el, out double3 eci, out _);
            return Astro.EciToEcef(eci, atJd);
        }

        /// <summary>The camera can point at the spot: within its off-nadir range and well above the spot's horizon.</summary>
        static bool InReach(double3 sat, double3 ground, double maxOffNadirDeg)
        {
            double3 toGround = math.normalize(ground - sat);
            double offNadir = math.acos(math.clamp(math.dot(math.normalize(-sat), toGround), -1, 1)) * MathConst.Rad2Deg;
            return offNadir <= maxOffNadirDeg && math.dot(math.normalize(ground), -toGround) >= 0.2;
        }

        static bool Daylight(double3 ground, double jd) => math.dot(math.normalize(ground), Astro.EciToEcef(Astro.SunDirection(jd), jd)) >= 0.05;

        public string Save(CapturedImage image)
        {
            string folder = Path.Combine(Application.persistentDataPath, "captures");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{image.Satellite}_{Astro.ToDateTime(image.JulianDate):yyyyMMdd_HHmmss}.png");
            File.WriteAllBytes(path, image.Texture.EncodeToPNG());
            image.SavedPath = path;
            return path;
        }

        // ───────────────────────────── Saving ─────────────────────────────

        public void WriteSave(SaveData data, List<byte[]> images)
        {
            foreach (var g in Groups)
                data.Groups.Add(new GroupSave
                {
                    Name = g.Name, Class = g.Class, Role = g.Role, CreatedJd = g.CreatedJd, LastCaptureJd = g.LastCaptureJd,
                    AutoCapture = g.AutoCapture, StationKeeping = g.StationKeeping, AvoidsCollisions = g.AvoidsCollisions,
                });
            foreach (var image in Gallery)
            {
                int png = -1;
                if (image.Texture != null)
                {
                    try { images.Add(image.Texture.EncodeToPNG()); png = images.Count - 1; }
                    catch (Exception) { } // not readable: the picture is lost, its record stays
                }
                data.Gallery.Add(new ImageSave
                {
                    Satellite = image.Satellite, SavedPath = image.SavedPath, JulianDate = image.JulianDate,
                    LatitudeDeg = image.LatitudeDeg, LongitudeDeg = image.LongitudeDeg, ResolutionM = image.ResolutionM,
                    SwathKm = image.SwathKm, OffNadirDeg = image.OffNadirDeg, AltitudeKm = image.AltitudeKm, Night = image.Night,
                    TargetIndex = image.Target != null ? Targets.IndexOf(image.Target) : -1, Png = png,
                });
            }
            foreach (var t in Targets)
                data.Targets.Add(new TargetSave
                {
                    Name = t.Name, LatitudeDeg = t.LatitudeDeg, LongitudeDeg = t.LongitudeDeg, RequestedJd = t.RequestedJd,
                    Captured = t.Captured, ImageIndex = t.Image != null ? Gallery.IndexOf(t.Image) : -1,
                });
        }

        public void RestoreSave(SaveData data, List<byte[]> images)
        {
            foreach (var image in Gallery)
                if (image.Texture != null) Destroy(image.Texture);
            Groups.Clear();
            Gallery.Clear();
            Targets.Clear();
            foreach (var g in data.Groups)
                Groups.Add(new SatelliteGroup
                {
                    Id = Groups.Count, Name = g.Name, Class = g.Class, Role = g.Role, CreatedJd = g.CreatedJd, LastCaptureJd = g.LastCaptureJd,
                    AutoCapture = g.AutoCapture, StationKeeping = g.StationKeeping, AvoidsCollisions = g.AvoidsCollisions,
                });
            foreach (var t in data.Targets)
                Targets.Add(new ImagingTarget { Name = t.Name, LatitudeDeg = t.LatitudeDeg, LongitudeDeg = t.LongitudeDeg, RequestedJd = t.RequestedJd, Captured = t.Captured });
            foreach (var i in data.Gallery)
            {
                Texture2D texture = null;
                if (i.Png >= 0 && i.Png < images.Count)
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Capture " + i.Satellite };
                    texture.LoadImage(images[i.Png], false);
                }
                var image = new CapturedImage
                {
                    Satellite = i.Satellite, SavedPath = string.IsNullOrEmpty(i.SavedPath) ? null : i.SavedPath, JulianDate = i.JulianDate,
                    LatitudeDeg = i.LatitudeDeg, LongitudeDeg = i.LongitudeDeg, ResolutionM = i.ResolutionM, SwathKm = i.SwathKm,
                    OffNadirDeg = i.OffNadirDeg, AltitudeKm = i.AltitudeKm, Night = i.Night, Texture = texture,
                    Target = i.TargetIndex >= 0 && i.TargetIndex < Targets.Count ? Targets[i.TargetIndex] : null,
                };
                Gallery.Add(image);
            }
            for (int k = 0; k < data.Targets.Count; k++)
            {
                int index = data.Targets[k].ImageIndex;
                if (index >= 0 && index < Gallery.Count) Targets[k].Image = Gallery[index];
            }
            RefreshGroups();
        }

        // ───────────────────────────── Frame ─────────────────────────────

        static readonly int CoverageTex = Shader.PropertyToID("_SpacedCoverageTex");
        static readonly int CoverageStrength = Shader.PropertyToID("_SpacedCoverageStrength");

        void Update()
        {
            if (_sim == null || _sim.Catalog == null) return;
            ServeTargets();
            bool visible = ShowCoverage && !Cinematic.Active && !Cinematic.SatellitesHidden && (CommsCoveragePercent > 0 || NavCoveragePercent > 0);
            Shader.SetGlobalTexture(CoverageTex, CoverageTexture);
            Shader.SetGlobalFloat(CoverageStrength, visible ? 1f : 0f);
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0) return;
            _timer = 0.5f;
            RefreshGroups();
            if (ShowCoverage) UpdateCoverage();
            AutoCapture();
        }

        void RefreshGroups()
        {
            foreach (var g in Groups) { g.Alive = 0; g.Maneuvering = 0; g.MeanAltitudeKm = 0; g.MemberIds.Clear(); }
            var catalog = _sim.Catalog;
            for (int i = 0; i < catalog.Count; i++)
            {
                int id = catalog.Groups[i];
                if (id < 0 || id >= Groups.Count || !catalog.Physics[i].IsAlive) continue;
                var g = Groups[id];
                g.Alive++;
                if (g.MemberIds.Count < 4096) g.MemberIds.Add(catalog.Ids[i]);
                if (catalog.Maneuvers[i].Active != 0) g.Maneuvering++;
                g.MeanAltitudeKm += catalog.Elements[i].A - EarthConstants.RadiusKm;
            }
            foreach (var g in Groups) if (g.Alive > 0) g.MeanAltitudeKm /= g.Alive;
        }

        void AutoCapture()
        {
            if (_capturing) return;
            double jd = _sim.Clock.JulianDate;
            foreach (var group in Groups)
            {
                if (!group.AutoCapture || group.Role != PayloadRole.Imaging || group.Alive == 0) continue;
                // One picture per group every ~10 sim-minutes, only over the day side.
                if ((jd - group.LastCaptureJd) * 1440.0 < 10) continue;
                foreach (int i in Members(group, 64))
                {
                    double3 up = math.normalize(_sim.Catalog.PositionsKm[i]);
                    if (math.dot(up, Astro.SunDirection(jd)) < 0.1) continue;
                    group.LastCaptureJd = jd;
                    Capture(i);
                    return;
                }
            }
        }

        void UpdateCoverage()
        {
            var catalog = _sim.Catalog;
            double jd = _sim.Clock.JulianDate;
            _commsSats.Clear();
            _navSats.Clear();
            for (int i = 0; i < catalog.Count; i++)
            {
                int id = catalog.Groups[i];
                if (id < 0 || id >= Groups.Count || !catalog.Physics[i].IsAlive) continue;
                var role = Groups[id].Role;
                if (role == PayloadRole.Communications) _commsSats.Add(Astro.EciToEcef(catalog.PositionsKm[i], jd));
                else if (role == PayloadRole.Navigation) _navSats.Add(Astro.EciToEcef(catalog.PositionsKm[i], jd));
            }

            var comms = new FootprintJob { Satellites = _commsSats.AsArray(), Counts = _commsCount, MinElevationDeg = MinElevationCommsDeg }.Schedule();
            new FootprintJob { Satellites = _navSats.AsArray(), Counts = _navCount, MinElevationDeg = MinElevationNavDeg }.Schedule(comms).Complete();

            double total = 0, commsCovered = 0, navCovered = 0;
            for (int y = 0; y < CoverageHeight; y++)
            {
                double weight = math.cos(((y + 0.5) / CoverageHeight - 0.5) * math.PI_DBL);
                for (int x = 0; x < CoverageWidth; x++)
                {
                    int p = y * CoverageWidth + x;
                    bool c = _commsCount[p] > 0, n = _navCount[p] >= 4;
                    total += weight;
                    if (c) commsCovered += weight;
                    if (n) navCovered += weight;
                    _coveragePixels[p] = new Color32((byte)(c ? math.min(255, 120 + _commsCount[p] * 30) : 0), (byte)(n ? 255 : 0), 0, 255);
                }
            }
            CommsCoveragePercent = _commsSats.Length > 0 ? commsCovered / total * 100.0 : 0;
            NavCoveragePercent = _navSats.Length > 0 ? navCovered / total * 100.0 : 0;
            CoverageTexture.SetPixels32(_coveragePixels);
            CoverageTexture.Apply(false, false);
        }

        /// <summary>
        /// Counts, per 1° cell of an Earth-fixed lat/lon map, how many satellites are above the minimum elevation.
        /// Footprint half-angle λ = acos(R·cos ε / (R + h)) − ε; only the bounding box of each footprint is visited.
        /// </summary>
        [BurstCompile]
        struct FootprintJob : IJob
        {
            [ReadOnly] public NativeArray<double3> Satellites;
            public NativeArray<int> Counts;
            public double MinElevationDeg;

            public void Execute()
            {
                for (int p = 0; p < Counts.Length; p++) Counts[p] = 0;
                double re = EarthConstants.RadiusKm;
                double elevation = MinElevationDeg * MathConst.Deg2Rad;
                for (int s = 0; s < Satellites.Length; s++)
                {
                    double3 sat = Satellites[s];
                    double r = math.length(sat);
                    if (r <= re) continue;
                    double halfAngle = math.acos(math.clamp(re * math.cos(elevation) / r, -1, 1)) - elevation;
                    if (halfAngle <= 0) continue;
                    double3 dir = sat / r;
                    double cosHalf = math.cos(halfAngle);
                    double lat = math.asin(dir.z), lon = math.atan2(dir.y, dir.x);
                    double halfDeg = halfAngle * MathConst.Rad2Deg;

                    int y0 = (int)math.floor((lat * MathConst.Rad2Deg - halfDeg + 90.0));
                    int y1 = (int)math.ceil((lat * MathConst.Rad2Deg + halfDeg + 90.0));
                    double maxAbsLat = math.min(89.0, math.abs(lat * MathConst.Rad2Deg) + halfDeg);
                    double lonSpan = halfDeg / math.max(math.cos(maxAbsLat * MathConst.Deg2Rad), 1e-3);
                    bool wholeBand = lonSpan >= 180.0;
                    int x0 = wholeBand ? 0 : (int)math.floor(lon * MathConst.Rad2Deg - lonSpan + 180.0);
                    int x1 = wholeBand ? CoverageWidth - 1 : (int)math.ceil(lon * MathConst.Rad2Deg + lonSpan + 180.0);

                    for (int y = math.max(y0, 0); y <= math.min(y1, CoverageHeight - 1); y++)
                    {
                        double cellLat = ((y + 0.5) - 90.0) * MathConst.Deg2Rad;
                        double cosLat = math.cos(cellLat), sinLat = math.sin(cellLat);
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            int x = ((xx % CoverageWidth) + CoverageWidth) % CoverageWidth;
                            double cellLon = ((x + 0.5) - 180.0) * MathConst.Deg2Rad;
                            var cell = new double3(cosLat * math.cos(cellLon), cosLat * math.sin(cellLon), sinLat);
                            if (math.dot(cell, dir) >= cosHalf) Counts[y * CoverageWidth + x]++;
                        }
                    }
                }
            }
        }
    }
}
