using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Spaced
{
    public enum ViewMode
    {
        /// <summary>Globe/map camera: pan, zoom to the cursor, rotate and tilt over an Earth-fixed focus point.</summary>
        Earth,
        /// <summary>Orbit around a catalog object, close enough to see its model.</summary>
        Satellite,
        /// <summary>Chase a rocket, booster or other vehicle.</summary>
        Vehicle,
        /// <summary>Stand on the ground and track a vehicle with a zoom lens, like a launch broadcast.</summary>
        GroundTracking,
        /// <summary>A camera bolted to the vehicle, like the onboard views on SpaceX webcasts.</summary>
        Onboard,
    }

    /// <summary>Where an onboard camera is and where it looks (inertial frame, km for position).</summary>
    public struct CameraPose
    {
        public double3 PositionKm;
        public double3 Forward;
        public double3 Up;
    }

    /// <summary>
    /// Two stacked cameras sharing one pose: the base camera draws the far scene (1 unit = 1000 km), the overlay
    /// camera draws the local scene (1 unit = 1 m). Each frame it picks the floating origin (the thing being
    /// looked at), places both cameras around it and publishes the per-frame lighting globals.
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public sealed class CameraRig : MonoBehaviour
    {
        public float Yaw = 20f;
        public float Pitch = 25f;
        /// <summary>Distance from the pivot, metres.</summary>
        public double Distance = 28e6;
        /// <summary>Globe camera turns with the Earth (map-like). Off: it stays fixed against the stars.</summary>
        public bool LockToEarth = true;
        public float RotateDegreesPerPixel = 0.25f;
        public float PickRadiusPx = 12f;
        public float FieldOfView = 50f;
        public float SunIntensity = 1.7f;
        /// <summary>Camera shake in degrees (rocket rumble), set by the launch director.</summary>
        public float Shake;

        public Camera Far { get; private set; }
        public Camera Near { get; private set; }
        public ViewMode Mode { get; private set; } = ViewMode.Earth;
        public string TargetLabel { get; private set; } = "Earth";
        /// <summary>Camera position this frame (inertial km).</summary>
        public double3 CameraEciKm { get; private set; }

        // ── Globe camera state (Earth-fixed). Targets are what input changes; the shown values ease towards them.
        public double FocusLatDeg => _lat;
        public double FocusLonDeg => _lon;
        public double HeadingDeg => _heading;
        public double TiltDeg => _tilt;
        public double CameraAltitudeKm => math.length(CameraEciKm) - EarthConstants.RadiusKm;
        /// <summary>Point under the mouse cursor on the globe, if any.</summary>
        public bool CursorOnGlobe { get; private set; }
        public double CursorLatDeg { get; private set; }
        public double CursorLonDeg { get; private set; }
        /// <summary>Ground metres per screen pixel at the focus point.</summary>
        public double MetersPerPixel { get; private set; }

        double _lat = 20, _lon = -40, _distance = 2.2e7, _heading, _tilt;
        double _latTarget = 20, _lonTarget = -40, _distanceTarget = 2.2e7, _headingTarget, _tiltTarget;
        double2 _panVelocity;   // deg/s of lat/lon, for momentum after a drag
        float _lastClickTime = -1f;
        Vector2 _lastClickPosition;
        bool _rotating;

        // Orbit-mode smoothing (satellites and vehicles).
        float _yawTarget = 20f, _pitchTarget = 25f;
        double _orbitDistanceTarget = -1;

        const double MinGlobeDistanceM = 150;
        const double MaxGlobeDistanceM = 1.6e8;

        static readonly int SunDir = Shader.PropertyToID("_SpacedSunDir");
        static readonly int SunIntensityId = Shader.PropertyToID("_SpacedSunIntensity");
        static readonly int EarthCenterLocal = Shader.PropertyToID("_SpacedEarthCenterLocal");
        static readonly int EarthRadiusLocal = Shader.PropertyToID("_SpacedEarthRadiusLocal");
        static readonly int CamEarthCLocal = Shader.PropertyToID("_SpacedCamEarthCLocal");
        static readonly int EarthCenterFar = Shader.PropertyToID("_SpacedEarthCenterFar");
        static readonly int EarthRadiusFar = Shader.PropertyToID("_SpacedEarthRadiusFar");
        static readonly int CamPlanetCFar = Shader.PropertyToID("_SpacedCamPlanetCFar");
        static readonly int CamAtmosphereCFar = Shader.PropertyToID("_SpacedCamAtmosphereCFar");
        static readonly int MoonDir = Shader.PropertyToID("_SpacedMoonDir");
        static readonly int MoonLight = Shader.PropertyToID("_SpacedMoonLight");
        static readonly int WorldToEarth = Shader.PropertyToID("_SpacedWorldToEarth");
        static readonly int MagneticAxis = Shader.PropertyToID("_SpacedMagneticAxis");
        static readonly int AuroraActivity = Shader.PropertyToID("_SpacedAuroraActivity");

        public const double AtmosphereTopKm = 100.0;
        /// <summary>The atmosphere shell is drawn this high so the aurora (up to ~300 km) has pixels to light.</summary>
        public const double AuroraTopKm = 320.0;
        /// <summary>North geomagnetic pole (IGRF dipole, 2025): the aurora ovals circle it and its southern twin.</summary>
        public const double MagneticPoleLatDeg = 80.8, MagneticPoleLonDeg = -72.6;

        /// <summary>Moonlight on the night side at full moon, relative to sunlight (artistic: the eye adapts).</summary>
        public float FullMoonLight = 0.08f;
        /// <summary>0 = no aurora, 1 = a typical active night (Kp ~4–5), 2 = a geomagnetic storm.</summary>
        public float AuroraActivityLevel = 1f;

        SpaceSimulation _sim;
        Light _sunLight;
        SatelliteRenderer _satellites;
        Func<Vector2, bool> _isPointerOverUi;

        uint _satelliteId;
        Func<double3?> _vehicle;
        Func<CameraPose?> _onboard;
        double3 _spectatorEcefKm;
        float _trackingZoom = 1f;

        double3 _pivotKm;
        double3 _transitionFromKm;
        double _distanceFrom, _distanceTo;
        float _transition = 1f;
        bool _pressed, _dragging;
        Vector2 _pressPosition;
        double _lastGmst;
        double _focusGroundKm;

        static double EarthRadiusM => EarthConstants.RadiusKm * 1000.0;

        public void Init(SpaceSimulation sim, SatelliteRenderer satellites, Func<Vector2, bool> isPointerOverUi)
        {
            _sim = sim;
            _satellites = satellites;
            _isPointerOverUi = isPointerOverUi;
            _lastGmst = Astro.Gmst(sim.Clock.JulianDate);
            _distanceTo = Distance;

            Far = Camera.main;
            if (Far == null) Far = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            Far.transform.SetParent(transform, false);
            Far.orthographic = false;
            Far.clearFlags = CameraClearFlags.SolidColor;
            Far.backgroundColor = Color.black;
            Far.cullingMask = ~(1 << WorldFrame.LocalLayer);
            Far.nearClipPlane = 0.0005f;
            Far.farClipPlane = 20000f;

            Near = new GameObject("Local Camera").AddComponent<Camera>();
            Near.transform.SetParent(transform, false);
            Near.cullingMask = 1 << WorldFrame.LocalLayer;
            Near.clearFlags = CameraClearFlags.Depth;

            var farData = Far.GetUniversalAdditionalCameraData();
            var nearData = Near.GetUniversalAdditionalCameraData();
            nearData.renderType = CameraRenderType.Overlay;
            farData.cameraStack.Add(Near);
            farData.renderPostProcessing = true;
            // The atmosphere stops its ray at the terrain (mountains, the streamed tiles) using the scene depth.
            farData.requiresDepthTexture = true;
            nearData.renderPostProcessing = true;
            farData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            CreatePostProcessing();

            // A real directional light as well, so imported models with standard URP materials are lit by the Sun.
            _sunLight = new GameObject("Sun Light").AddComponent<Light>();
            _sunLight.type = LightType.Directional;
            _sunLight.color = new Color(1f, 0.97f, 0.92f);
            _sunLight.intensity = 1.6f;
            _sunLight.shadows = LightShadows.None;
        }

        static void CreatePostProcessing()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.05f);
            bloom.intensity.Override(0.75f);
            bloom.scatter.Override(0.72f);
            profile.Add<Tonemapping>(true).mode.Override(TonemappingMode.ACES);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.35f);
            color.contrast.Override(8f);
            profile.Add<Vignette>(true).intensity.Override(0.22f);

            var volume = new GameObject("Post Processing").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
        }

        // ───────────────────────────── Targets ─────────────────────────────

        /// <summary>Back to the globe, looking straight down at the point under the current camera.</summary>
        public void ViewEarth()
        {
            double jd = _sim != null ? _sim.Clock.JulianDate : 0;
            if (math.lengthsq(CameraEciKm) > 1)
            {
                Astro.EcefToGeo(Astro.EciToEcef(CameraEciKm, jd), out double lat, out double lon, out double altitude);
                _lat = _latTarget = lat;
                _lon = _lonTarget = lon;
                _distance = _distanceTarget = math.clamp(math.max(altitude * 1000.0, 2.0e6), MinGlobeDistanceM, MaxGlobeDistanceM);
                _heading = _headingTarget = 0;
                _tilt = _tiltTarget = 0;
            }
            BeginTransition(_distance);
            Mode = ViewMode.Earth;
            TargetLabel = "Earth";
        }

        /// <summary>Glides the globe camera to look at a place from the given distance.</summary>
        public void FlyTo(double latDeg, double lonDeg, double distanceM)
        {
            if (Mode != ViewMode.Earth) ViewEarth();
            _latTarget = latDeg;
            _lonTarget = _lon + OrbitMath.ShortestAngle((lonDeg - _lon) * MathConst.Deg2Rad) * MathConst.Rad2Deg;
            _distanceTarget = math.clamp(distanceM, MinGlobeDistanceM, MaxGlobeDistanceM);
            _panVelocity = 0;
        }

        public void ResetNorth()
        {
            _headingTarget = math.round(_heading / 360.0) * 360.0;
            _tiltTarget = 0;
        }

        public void ViewSatellite(uint id)
        {
            int index = _sim.Catalog.IndexOf(id);
            if (index < 0) return;
            float radius = _sim.Catalog.Physics[index].RadiusM;
            BeginTransition(math.clamp(radius * 9.0, 8.0, 900.0));
            Mode = ViewMode.Satellite;
            _satelliteId = id;
            TargetLabel = _sim.Catalog.Names[index];
            Pitch = _pitchTarget = 12f;
            _yawTarget = Yaw;
        }

        public void ViewVehicle(Func<double3?> positionKm, string label, double distanceM)
        {
            BeginTransition(distanceM);
            Mode = ViewMode.Vehicle;
            _vehicle = positionKm;
            TargetLabel = label;
            Pitch = _pitchTarget = 8f;
            _yawTarget = Yaw;
        }

        /// <summary>Stand at an Earth-fixed spot and keep a vehicle framed with an automatic zoom lens.</summary>
        public void ViewFromGround(double3 spectatorEcefKm, Func<double3?> targetKm, string label)
        {
            BeginTransition(0);
            Mode = ViewMode.GroundTracking;
            _spectatorEcefKm = spectatorEcefKm;
            _vehicle = targetKm;
            TargetLabel = label;
            _trackingZoom = 1f;
        }

        /// <summary>Ride along on a vehicle; the pose is re-evaluated every frame.</summary>
        public void ViewOnboard(Func<CameraPose?> pose, string label)
        {
            _transition = 1f;
            Mode = ViewMode.Onboard;
            _onboard = pose;
            TargetLabel = label;
            Distance = 0;
            _distanceTo = 0;
        }

        void BeginTransition(double newDistance)
        {
            _transitionFromKm = _pivotKm;
            _distanceFrom = Distance;
            _distanceTo = newDistance;
            _orbitDistanceTarget = newDistance;
            _transition = 0f;
        }

        // ───────────────────────────── Frame ─────────────────────────────

        void LateUpdate()
        {
            if (_sim == null) return;
            double jd = _sim.Clock.JulianDate;
            HandleInput();

            double gmst = Astro.Gmst(jd);
            if (!LockToEarth && Mode == ViewMode.Earth)
            {
                // Keep the view fixed against the stars: counter-rotate the Earth-fixed focus.
                double delta = OrbitMath.ShortestAngle(gmst - _lastGmst) * MathConst.Rad2Deg;
                _lon -= delta;
                _lonTarget -= delta;
            }
            _lastGmst = gmst;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            DampGlobe(dt);
            DampOrbit(dt);
            UpdateFocusGround(dt);

            double3 target = TargetPivot(jd, out bool valid);
            if (!valid)
            {
                ViewEarth();
                target = double3.zero;
            }

            _transition = Mathf.Min(1f, _transition + Time.unscaledDeltaTime / 1.4f);
            float s = Mathf.SmoothStep(0, 1, _transition);
            _pivotKm = _transition < 1f ? math.lerp(_transitionFromKm, target, s) : target;
            if (_transition < 1f)
                Distance = math.exp(math.lerp(math.log(math.max(_distanceFrom, 0.5)), math.log(math.max(_distanceTo, 0.5)), s));
            ClampDistance();

            WorldFrame.OriginKm = _pivotKm;
            WorldFrame.JulianDate = jd;

            Quaternion rotation;
            Vector3 offsetM;
            if (Mode == ViewMode.Earth)
            {
                KeepAboveTerrain(jd);
                GlobePose(jd, out _, out double3 offsetEciM, out double3 look, out double3 up);
                rotation = Quaternion.LookRotation(Astro.EciToUnity(look), Astro.EciToUnity(up));
                offsetM = Astro.EciToUnity(offsetEciM);
                Distance = _distance;
                SetFieldOfView(FieldOfView);
            }
            else if (Mode == ViewMode.Onboard)
            {
                var pose = _onboard?.Invoke() ?? default;
                rotation = Quaternion.LookRotation(Astro.EciToUnity(pose.Forward), Astro.EciToUnity(pose.Up));
                offsetM = Vector3.zero;
                SetFieldOfView(70f);
            }
            else if (Mode == ViewMode.GroundTracking)
            {
                double3 up = math.normalize(_pivotKm);
                Vector3 toTarget = _vehicle?.Invoke() is double3 v ? WorldFrame.ToLocal(v) : Astro.EciToUnity(up);
                rotation = Quaternion.LookRotation(toTarget.normalized, Astro.EciToUnity(up));
                offsetM = Vector3.zero;
                // Zoom so a ~70 m rocket fills a good part of the frame, like a long tracking lens.
                float distanceToTarget = Mathf.Max(toTarget.magnitude, 1f);
                float fov = 2f * Mathf.Atan(140f / (distanceToTarget * _trackingZoom)) * Mathf.Rad2Deg;
                SetFieldOfView(Mathf.Clamp(fov, 0.4f, 60f));
            }
            else
            {
                rotation = Basis() * Quaternion.Euler(Pitch, Yaw, 0f);
                offsetM = rotation * new Vector3(0, 0, -(float)Distance);
                SetFieldOfView(FieldOfView);
            }

            if (Shake > 0)
            {
                float t = Time.unscaledTime * 7f;
                rotation *= Quaternion.Euler((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2f * Shake, (Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2f * Shake, 0f);
            }

            Far.transform.SetPositionAndRotation(offsetM / (float)WorldFrame.MetersPerFarUnit, rotation);
            Near.transform.SetPositionAndRotation(offsetM, rotation);

            double nearPlane = Mode == ViewMode.GroundTracking ? 0.5 : Mode == ViewMode.Onboard ? 0.1 : math.clamp(Distance * 0.004, 0.03, 50.0);
            Near.nearClipPlane = (float)nearPlane;
            Near.farClipPlane = 4.0e6f;

            CameraEciKm = _pivotKm + Astro.UnityToEci(offsetM) / 1000.0;
            // The far camera's near plane follows altitude so you can get close to the ground without clipping it.
            double altitudeM = math.max(CameraAltitudeKm * 1000.0, 1.0);
            Far.nearClipPlane = (float)(math.clamp(altitudeM * 0.3, 1.0, 500000.0) / WorldFrame.MetersPerFarUnit);
            MetersPerPixel = Distance * 2.0 * math.tan(Far.fieldOfView * 0.5 * MathConst.Deg2Rad) / math.max(Screen.height, 1);
            UpdateCursorGeo(jd);
            PublishGlobals(jd);
        }

        double3 TargetPivot(double jd, out bool valid)
        {
            valid = true;
            switch (Mode)
            {
                case ViewMode.Satellite:
                    int index = _sim.Catalog.IndexOf(_satelliteId);
                    if (index < 0 || !_sim.Catalog.Physics[index].IsAlive) { valid = false; return default; }
                    return _sim.Catalog.PositionsKm[index];
                case ViewMode.Vehicle:
                    double3? vehicle = _vehicle?.Invoke();
                    valid = vehicle.HasValue;
                    return vehicle ?? default;
                case ViewMode.GroundTracking:
                    if (_vehicle?.Invoke() == null) { valid = false; return default; }
                    return Astro.EcefToEci(_spectatorEcefKm, jd);
                case ViewMode.Onboard:
                    var pose = _onboard?.Invoke();
                    valid = pose.HasValue;
                    return pose?.PositionKm ?? default;
                default:
                    GlobePose(jd, out double3 focus, out _, out _, out _);
                    return focus;
            }
        }

        // ───────────────────────────── Globe camera ─────────────────────────────

        /// <summary>
        /// Focus point on the surface and camera placement: the camera sits <see cref="_distance"/> from the focus,
        /// tilted from straight-down towards the horizon by <see cref="_tilt"/>, looking along <see cref="_heading"/>.
        /// </summary>
        void GlobePose(double jd, out double3 focusEciKm, out double3 offsetEciM, out double3 look, out double3 up)
        {
            focusEciKm = Astro.EcefToEci(Astro.GeoToEcef(_lat, _lon, _focusGroundKm), jd);
            Astro.EnuBasis(focusEciKm, out double3 east, out double3 north, out double3 zenith);
            double heading = _heading * MathConst.Deg2Rad, tilt = _tilt * MathConst.Deg2Rad;
            double3 forward = north * math.cos(heading) + east * math.sin(heading);
            double3 towardsCamera = zenith * math.cos(tilt) - forward * math.sin(tilt);
            offsetEciM = towardsCamera * _distance;
            look = -towardsCamera;
            up = forward * math.cos(tilt) + zenith * math.sin(tilt);
        }

        /// <summary>Terrain height (km) under a point, once its elevation tile has streamed in.</summary>
        static double GroundKm(double latDeg, double lonDeg) => (GlobeTiles.Instance?.HeightAt(latDeg, lonDeg) ?? 0) / 1000.0;

        /// <summary>Eases the focus height towards newly streamed elevation, so the view doesn't jump when a finer tile arrives.</summary>
        void UpdateFocusGround(float dt) =>
            _focusGroundKm = math.lerp(_focusGroundKm, GroundKm(_lat, _lon), 1.0 - math.exp(-dt * 6.0));

        /// <summary>
        /// Keeps the camera at least ~40 m above the terrain under it (mountains, cliffs): if it would sink in, it is
        /// pushed back along the view ray.
        /// </summary>
        void KeepAboveTerrain(double jd)
        {
            GlobePose(jd, out double3 focus, out double3 offsetM, out _, out _);
            double3 cameraEcef = Astro.EciToEcef(focus + offsetM / 1000.0, jd);
            Astro.EcefToGeo(cameraEcef, out double lat, out double lon, out double altitudeKm);
            double clearanceM = (altitudeKm - GroundKm(lat, lon)) * 1000.0;
            if (clearanceM >= 40.0) return;
            double cosTilt = math.max(math.cos(_tilt * MathConst.Deg2Rad), 0.2);
            double push = (40.0 - clearanceM) / cosTilt;
            _distance += push;
            _distanceTarget = math.max(_distanceTarget, _distance);
        }

        /// <summary>The steepest tilt that keeps the camera above ~50 m and the view sensible at this distance.</summary>
        double MaxTilt(double distanceM)
        {
            double byDistance = distanceM < 20e3 ? 84 : distanceM < 300e3 ? 75 : distanceM < 3e6 ? 60 : 30;
            // Camera height above the focus point ≈ d·cos(tilt) (+ curvature, which only helps).
            double minCos = math.saturate(50.0 / math.max(distanceM, 1.0));
            return math.min(byDistance, math.acos(minCos) * MathConst.Rad2Deg);
        }

        void DampGlobe(float dt)
        {
            // Momentum from a flick of the map.
            if (math.lengthsq(_panVelocity) > 1e-12 && !(_pressed && _dragging))
            {
                _latTarget += _panVelocity.x * dt;
                _lonTarget += _panVelocity.y * dt;
                _panVelocity *= math.exp(-4.0 * dt);
            }
            _latTarget = math.clamp(_latTarget, -89.5, 89.5);
            _distanceTarget = math.clamp(_distanceTarget, MinGlobeDistanceM, MaxGlobeDistanceM);
            _tiltTarget = math.clamp(_tiltTarget, 0, MaxTilt(_distanceTarget));

            double k = 1.0 - math.exp(-12.0 * dt);
            _lat += (_latTarget - _lat) * k;
            _lon += (_lonTarget - _lon) * k;
            _heading += (_headingTarget - _heading) * k;
            _tilt += (_tiltTarget - _tilt) * k;
            _distance = math.exp(math.lerp(math.log(_distance), math.log(_distanceTarget), k));
            _tilt = math.min(_tilt, MaxTilt(_distance));
        }

        void DampOrbit(float dt)
        {
            float k = 1f - Mathf.Exp(-14f * dt);
            Yaw = Mathf.LerpAngle(Yaw, _yawTarget, k);
            Pitch = Mathf.Lerp(Pitch, _pitchTarget, k);
            if (Mode != ViewMode.Earth && _transition >= 1f && _orbitDistanceTarget > 0)
                Distance = math.exp(math.lerp(math.log(math.max(Distance, 0.01)), math.log(_orbitDistanceTarget), k));
        }

        /// <summary>Finds where a screen point's ray meets the globe (double precision), as latitude/longitude.</summary>
        bool ScreenToGeo(Vector2 screen, double jd, out double latDeg, out double lonDeg)
        {
            latDeg = lonDeg = 0;
            Ray ray = Far.ScreenPointToRay(screen);
            double3 origin = WorldFrame.OriginKm + Astro.UnityToEci(ray.origin) * WorldScale.KmPerUnit;
            double3 direction = math.normalize(Astro.UnityToEci(ray.direction));
            double r = EarthConstants.RadiusKm;
            double b = math.dot(origin, direction);
            double c = math.lengthsq(origin) - r * r;
            double discriminant = b * b - c;
            if (discriminant < 0 || c < 0) return false;
            double t = c / (-b + math.sqrt(discriminant)); // stable near root
            if (t < 0 || b > 0) return false;
            Astro.EcefToGeo(Astro.EciToEcef(origin + direction * t, jd), out latDeg, out lonDeg, out _);
            return true;
        }

        void UpdateCursorGeo(double jd)
        {
            var mouse = Mouse.current;
            if (mouse == null) { CursorOnGlobe = false; return; }
            CursorOnGlobe = ScreenToGeo(mouse.position.ReadValue(), jd, out double lat, out double lon);
            CursorLatDeg = lat;
            CursorLonDeg = lon;
        }

        /// <summary>Moves the focus towards (or away from) a point by a fraction, the short way round.</summary>
        void MoveFocusTowards(double latDeg, double lonDeg, double fraction)
        {
            _latTarget += (latDeg - _latTarget) * fraction;
            double dLon = OrbitMath.ShortestAngle((lonDeg - _lonTarget) * MathConst.Deg2Rad) * MathConst.Rad2Deg;
            _lonTarget += dLon * fraction;
        }

        void HandleGlobeInput(Mouse mouse, Keyboard keyboard, Vector2 position, bool overUi, float dt)
        {
            double jd = _sim.Clock.JulianDate;
            double metresPerPixel = _distanceTarget * 2.0 * math.tan(FieldOfView * 0.5 * MathConst.Deg2Rad) / math.max(Screen.height, 1);
            double degreesPerMetre = MathConst.Rad2Deg / (EarthConstants.RadiusKm * 1000.0);
            double cosLat = math.max(math.cos(_latTarget * MathConst.Deg2Rad), 0.05);
            double heading = _headingTarget * MathConst.Deg2Rad;

            void Pan(double right, double up)
            {
                // Screen right/up in north/east, then into degrees. Capped so a far-out view doesn't spin wildly.
                double dNorth = (-right * math.sin(heading) + up * math.cos(heading)) * metresPerPixel * degreesPerMetre;
                double dEast = (right * math.cos(heading) + up * math.sin(heading)) * metresPerPixel * degreesPerMetre;
                dNorth = math.clamp(dNorth, -15, 15);
                dEast = math.clamp(dEast, -15, 15);
                _latTarget -= dNorth;
                _lonTarget -= dEast / cosLat;
            }

            if (_pressed && _dragging)
            {
                Vector2 delta = mouse.delta.ReadValue();
                bool rotate = _rotating || mouse.rightButton.isPressed || mouse.middleButton.isPressed
                              || (keyboard != null && (keyboard.leftCtrlKey.isPressed || keyboard.leftAltKey.isPressed));
                _rotating = rotate;
                if (rotate)
                {
                    _headingTarget += delta.x * 0.3;
                    _tiltTarget += delta.y * 0.25;
                }
                else
                {
                    double latBefore = _latTarget, lonBefore = _lonTarget;
                    Pan(delta.x, delta.y);
                    if (dt > 0) _panVelocity = math.lerp(_panVelocity, new double2(_latTarget - latBefore, _lonTarget - lonBefore) / dt, 0.5);
                }
            }

            // Smooth zoom towards whatever is under the cursor.
            float scroll = overUi ? 0 : mouse.scroll.ReadValue().y;
            if (scroll != 0)
            {
                float notches = Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll;
                double factor = math.pow(0.8, notches);
                double before = _distanceTarget;
                _distanceTarget = math.clamp(_distanceTarget * factor, MinGlobeDistanceM, MaxGlobeDistanceM);
                if (ScreenToGeo(position, jd, out double lat, out double lon))
                    MoveFocusTowards(lat, lon, 1.0 - _distanceTarget / before);
                _panVelocity = 0;
            }

            if (keyboard != null && GUIUtility.keyboardControl == 0)
            {
                double speed = Screen.height * 0.6 * dt; // pixels per frame equivalent
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) Pan(0, -speed);
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) Pan(0, speed);
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) Pan(speed, 0);
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) Pan(-speed, 0);
                if (keyboard.qKey.isPressed) _headingTarget -= 70 * dt;
                if (keyboard.eKey.isPressed) _headingTarget += 70 * dt;
                if (keyboard.zKey.isPressed) _tiltTarget += 40 * dt;
                if (keyboard.xKey.isPressed) _tiltTarget -= 40 * dt;
                if (keyboard.pageUpKey.isPressed) _distanceTarget *= math.pow(0.25, dt);
                if (keyboard.pageDownKey.isPressed) _distanceTarget *= math.pow(4.0, dt);
                if (keyboard.homeKey.wasPressedThisFrame) ResetNorth();
            }
        }

        /// <summary>Frame the orbit controls rotate in: world axes around Earth, local horizon elsewhere.</summary>
        Quaternion Basis()
        {
            if (Mode == ViewMode.Earth || math.lengthsq(_pivotKm) < 1) return Quaternion.identity;
            double3 up = math.normalize(_pivotKm);
            double3 north = new double3(0, 0, 1) - up * up.z;
            if (math.lengthsq(north) < 1e-8) north = new double3(1, 0, 0);
            return WorldFrame.LookRotation(math.normalize(north), up);
        }

        void ClampDistance()
        {
            if (Mode != ViewMode.Earth && _transition >= 1f) Distance = math.clamp(Distance, 3.0, 3e7);
        }

        void SetFieldOfView(float fov)
        {
            Far.fieldOfView = fov;
            Near.fieldOfView = fov;
        }

        void PublishGlobals(double jd)
        {
            Vector3 sun = Astro.EciToUnity(Astro.SunDirection(jd)).normalized;
            Shader.SetGlobalVector(SunDir, sun);
            _sunLight.transform.rotation = Quaternion.LookRotation(-sun);
            Shader.SetGlobalFloat(SunIntensityId, SunIntensity);

            // Moonlight follows the phase steeply (a half moon gives ~1/10 of full-moon light).
            Shader.SetGlobalVector(MoonDir, Astro.EciToUnity(math.normalize(Astro.MoonPosition(jd))).normalized);
            Shader.SetGlobalFloat(MoonLight, FullMoonLight * (float)math.pow(Astro.MoonIllumination(jd), 3.0));
            // World → Earth-fixed (the base globe's local axes), for shaders that look up maps by direction.
            Shader.SetGlobalMatrix(WorldToEarth, Matrix4x4.Rotate(Quaternion.Inverse(Astro.EarthRotation(jd))));
            double3 pole = Astro.EcefToEci(math.normalize(Astro.GeoToEcef(MagneticPoleLatDeg, MagneticPoleLonDeg, 0)), jd);
            Shader.SetGlobalVector(MagneticAxis, Astro.EciToUnity(pole).normalized);
            Shader.SetGlobalFloat(AuroraActivity, AuroraActivityLevel);

            double radiusM = EarthRadiusM;
            double cameraRadiusM = math.length(CameraEciKm) * 1000.0;
            // Occlusion uses a sphere 50 m below sea level: pads, flame trenches and decks sit slightly under it.
            double occluderM = radiusM - 50.0;
            Shader.SetGlobalVector(EarthCenterLocal, WorldFrame.ToLocal(double3.zero));
            Shader.SetGlobalFloat(EarthRadiusLocal, (float)radiusM);
            Shader.SetGlobalFloat(CamEarthCLocal, (float)((cameraRadiusM - occluderM) * (cameraRadiusM + occluderM)));

            double unit = WorldScale.KmPerUnit;
            double cameraRadius = cameraRadiusM / 1000.0 / unit;
            double planet = EarthConstants.RadiusKm / unit;
            double top = (EarthConstants.RadiusKm + AtmosphereTopKm) / unit;
            Shader.SetGlobalVector(EarthCenterFar, WorldFrame.ToFar(double3.zero));
            Shader.SetGlobalFloat(EarthRadiusFar, (float)planet);
            Shader.SetGlobalFloat(CamPlanetCFar, (float)((cameraRadius - planet) * (cameraRadius + planet)));
            Shader.SetGlobalFloat(CamAtmosphereCFar, (float)((cameraRadius - top) * (cameraRadius + top)));
        }

        // ───────────────────────────── Input ─────────────────────────────

        void HandleInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var keyboard = Keyboard.current;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);

            Vector2 position = mouse.position.ReadValue();
            bool overUi = _isPointerOverUi != null && _isPointerOverUi(position);

            if ((mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame) && !overUi)
            {
                _pressed = true;
                _dragging = false;
                _rotating = !mouse.leftButton.wasPressedThisFrame;
                _pressPosition = position;
                _panVelocity = 0;
            }

            bool held = mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed;
            if (_pressed && held && (position - _pressPosition).sqrMagnitude > 25f) _dragging = true;

            if (Mode == ViewMode.Earth)
            {
                HandleGlobeInput(mouse, keyboard, position, overUi, dt);
            }
            else if (_pressed && held && _dragging && Mode != ViewMode.GroundTracking && Mode != ViewMode.Onboard)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _yawTarget += delta.x * RotateDegreesPerPixel;
                _pitchTarget = Mathf.Clamp(_pitchTarget - delta.y * RotateDegreesPerPixel, -89f, 89f);
            }

            if (_pressed && !held)
            {
                if (!_dragging && !_rotating)
                {
                    bool doubleClick = Time.unscaledTime - _lastClickTime < 0.35f && (position - _lastClickPosition).sqrMagnitude < 100f;
                    _lastClickTime = Time.unscaledTime;
                    _lastClickPosition = position;
                    if (doubleClick && Mode == ViewMode.Earth && ScreenToGeo(position, _sim.Clock.JulianDate, out double lat, out double lon))
                        FlyTo(lat, lon, _distanceTarget * 0.3);
                    else
                        Pick(position);
                }
                _pressed = false;
                _dragging = false;
                _rotating = false;
            }

            if (Mode == ViewMode.Earth) return;
            float scroll = overUi ? 0 : mouse.scroll.ReadValue().y;
            if (scroll == 0) return;
            // Windows reports 120 per wheel notch; other platforms ~1.
            float notches = Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll;
            float factor = Mathf.Pow(0.85f, notches);
            if (Mode == ViewMode.GroundTracking) _trackingZoom = Mathf.Clamp(_trackingZoom / factor, 0.1f, 20f);
            else _orbitDistanceTarget = math.clamp((_orbitDistanceTarget > 0 ? _orbitDistanceTarget : Distance) * factor, 3.0, 3e7);
            _distanceTo = _orbitDistanceTarget;
        }

        void Pick(Vector2 screenPosition)
        {
            var catalog = _sim.Catalog;
            var data = catalog.RenderData;
            Vector3 cameraPosition = Far.transform.position;
            Vector3 earthCenter = WorldFrame.ToFar(double3.zero);
            float earthRadius = (float)(EarthConstants.RadiusKm / WorldScale.KmPerUnit);
            float maxDistanceSq = PickRadiusPx * Mathf.Max(1f, Screen.height / 1080f);
            maxDistanceSq *= maxDistanceSq;

            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < data.Length; i++)
            {
                Vector4 d = data[i];
                if (d.w < 0 || !_satellites.CategoryVisible[(int)d.w]) continue;

                var world = new Vector3(d.x, d.y, d.z);
                Vector3 screen = Far.WorldToScreenPoint(world);
                if (screen.z <= 0) continue;

                float distanceSq = ((Vector2)screen - screenPosition).sqrMagnitude;
                if (distanceSq > maxDistanceSq) continue;
                if (HiddenByEarth(cameraPosition - earthCenter, world - earthCenter, earthRadius)) continue;

                float score = distanceSq + screen.z * 0.001f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best >= 0) _sim.SelectedId = catalog.Ids[best];
            else if (Mode == ViewMode.Earth) _sim.SelectedId = 0;
        }

        static bool HiddenByEarth(Vector3 from, Vector3 to, float radius)
        {
            Vector3 direction = to - from;
            float length = direction.magnitude;
            direction /= length;
            float b = Vector3.Dot(from, direction);
            float c = from.sqrMagnitude - radius * radius;
            float discriminant = b * b - c;
            if (discriminant < 0) return false;
            float hit = -b - Mathf.Sqrt(discriminant);
            return hit > 0 && hit < length;
        }
    }
}
