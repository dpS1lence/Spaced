using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Spaced
{
    /// <summary>
    /// Superhero mode (sandbox): a third-person flyer who starts on the ground where you were looking and can go
    /// anywhere, from rooftop height to the Moon. Mouse to look, WASD to fly where you look, Space / Ctrl up and down,
    /// Shift for super speed, scroll to move the camera in or out. Speed grows with height above the ground (a jog at
    /// street level, the Moon in seconds) and every motion eases in and out.
    /// <para>He is kept relative to whatever is below him: the turning Earth near it (so the ground stays put), the
    /// stars further out, the Moon inside its sphere of influence.</para>
    /// </summary>
    [DefaultExecutionOrder(-32)] // flies before the camera rig (-30) puts the camera behind him
    public sealed class HeroController : MonoBehaviour
    {
        enum Anchor
        {
            EarthFixed,
            Inertial,
            Moon,
        }

        /// <summary>Hips above the ground when standing.</summary>
        const double StandHeightM = 0.97;
        const double MinSpeedMs = 14, SpeedPerMetre = 0.4, BoostFactor = 5, SpeedOfSoundMs = 343;
        /// <summary>Top speed (the Moon is still only seconds away) and how far out he may go.</summary>
        const double MaxSpeedMs = 6.0e7, EdgeKm = 1.5e6;
        const double EarthFixedWithinKm = 120000;
        const float LookDegreesPerPixel = 0.11f;
        public const string Label = "Superhero";

        public bool Active { get; private set; }
        public double SpeedMs { get; private set; }
        /// <summary>Height of his feet above the ground (or the Moon), metres.</summary>
        public double AltitudeM { get; private set; }
        public bool NearMoon => _anchor == Anchor.Moon;
        public bool Grounded { get; private set; }
        public bool Boosting { get; private set; }
        /// <summary>The spot on the Earth below him.</summary>
        public double LatDeg { get; private set; }
        public double LonDeg { get; private set; }
        public bool MouseCaptured => Active && Cursor.lockState == CursorLockMode.Locked;
        /// <summary>What M turns him towards: the Moon, or the Earth when he is already by the Moon.</summary>
        public string FaceTargetName => NearMoon ? "the Earth" : "the Moon";

        SpaceSimulation _sim;
        CameraRig _rig;
        LaunchManager _launches;
        LunarMissionManager _lunar;
        SpacedAudio _audio;
        Func<Vector2, bool> _isPointerOverUi;
        HeroModel _model;

        Anchor _anchor;
        double3 _position, _velocity; // km and km/s in the anchor's frame
        Quaternion _look = Quaternion.identity, _body = Quaternion.identity; // Unity world axes (inertial)
        Vector3 _up = Vector3.up, _lastFlightDirection;
        float _fly, _bank, _fov = 62f, _distance = 6f, _distanceTarget = 6f;
        bool _supersonic, _launchDirector, _lunarDirector;
        bool _facing; // turning the view towards the Moon (or home)

        public void Init(SpaceSimulation sim, CameraRig rig, LaunchManager launches, LunarMissionManager lunar, MaterialLibrary materials,
            SpacedAudio audio, Func<Vector2, bool> isPointerOverUi)
        {
            _sim = sim;
            _rig = rig;
            _launches = launches;
            _lunar = lunar;
            _audio = audio;
            _isPointerOverUi = isPointerOverUi;
            _model = new GameObject("Superhero").AddComponent<HeroModel>();
            _model.Init(materials);
            _model.gameObject.SetActive(false);
        }

        /// <summary>Starts standing on the ground where the globe camera is looking.</summary>
        public void Enter()
        {
            if (Active || _sim == null || !_sim.IsLoaded) return;
            double jd = _sim.Clock.JulianDate;
            double lat = _rig.FocusLatDeg, lon = _rig.FocusLonDeg;
            _anchor = Anchor.EarthFixed;
            _position = Astro.GeoToEcef(lat, lon, (GroundM(lat, lon) + StandHeightM) / 1000.0);
            _velocity = double3.zero;

            _up = Up(Eci(jd), jd);
            Vector3 north = North(_up);
            _look = Quaternion.LookRotation(Quaternion.AngleAxis(12f, Vector3.Cross(_up, north)) * north, _up);
            _body = Quaternion.LookRotation(north, _up);
            _lastFlightDirection = Vector3.zero;
            _fly = _bank = 0f;
            _distance = _distanceTarget = 6f;
            _supersonic = false;

            // Nobody else steers the camera or the clock while he flies.
            _launchDirector = _launches.Director;
            _lunarDirector = _lunar.Director;
            _launches.StopWatching();
            _lunar.StopWatching();
            _launches.Director = false;
            _lunar.Director = false;
            _sim.Clock.Paused = false;
            _sim.Clock.Reverse = false;
            _sim.Clock.SetWarpIndex(0);

            Active = true;
            _model.gameObject.SetActive(true);
            _rig.ViewOnboard(CameraPose, Label);
            Cinematic.PublishOtherLights = PublishLight;
            Capture(true);
            Update();
        }

        /// <summary>Back to the globe, looking down on where he was.</summary>
        public void Leave()
        {
            if (!Active) return;
            Active = false;
            Capture(false);
            _model.gameObject.SetActive(false);
            _audio?.SetWind(0f, 0f);
            if (Cinematic.PublishOtherLights == PublishLight) Cinematic.PublishOtherLights = null;
            _launches.Director = _launchDirector;
            _lunar.Director = _lunarDirector;
            if (_rig.Mode == ViewMode.Onboard && _rig.TargetLabel == Label) _rig.ViewEarth();
            if (_rig.Mode == ViewMode.Earth) _rig.FlyTo(LatDeg, LonDeg, math.clamp(AltitudeM * 4 + 3000, 3000, 2.0e7));
        }

        static void Capture(bool on)
        {
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }

        void OnDisable()
        {
            if (Active) Capture(false);
        }

        void Update()
        {
            if (!Active || _sim == null) return;
            // Something else took the camera (Earth view, a launch): stop being the hero.
            if (_rig.Mode != ViewMode.Onboard || _rig.TargetLabel != Label)
            {
                Leave();
                return;
            }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            double jd = _sim.Clock.JulianDate;
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            // Low down, the view follows the curve of the planet, as when flying round it; out in space he flies
            // straight, and "up" turns smoothly (e.g. when the Moon takes over from the Earth).
            double3 eci = Eci(jd);
            float follow = Mathf.Clamp01(1f - (float)((AltitudeAbove(eci, jd, out _) / 1000.0 - 50.0) / 450.0));
            Vector3 up = Vector3.Slerp(_up, Up(eci, jd), Mathf.Max(follow, 1f - Mathf.Exp(-dt * 2f))).normalized;
            Quaternion transport = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(_up, up), follow);
            _look = Level(transport * _look, up);
            _body = transport * _body;
            _up = up;

            if (mouse != null)
            {
                Vector2 pointer = mouse.position.ReadValue();
                bool overUi = _isPointerOverUi != null && _isPointerOverUi(pointer);
                // Click the view to take the controls, Esc to give the mouse back.
                if (!MouseCaptured && mouse.leftButton.wasPressedThisFrame && !overUi) Capture(true);
                if (MouseCaptured)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    if (delta.sqrMagnitude > 4f) _facing = false; // steering yourself cancels the turn
                    Look(delta.x * LookDegreesPerPixel, delta.y * LookDegreesPerPixel);
                }
                float scroll = MouseCaptured || !overUi ? mouse.scroll.ReadValue().y : 0f;
                if (scroll != 0)
                {
                    float notches = Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll; // Windows: 120 per notch
                    _distanceTarget = Mathf.Clamp(_distanceTarget * Mathf.Pow(0.87f, notches), 2.5f, 120f);
                }
            }
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Capture(false);
            if (keyboard != null && keyboard.mKey.wasPressedThisFrame) FaceTarget();
            if (_facing) TurnTowardsTarget(eci, jd, dt);

            // Fly where you look: W/S along the view, A/D sideways, Space/Ctrl straight up/down.
            Vector3 forward = _look * Vector3.forward, right = _look * Vector3.right;
            Vector3 move = Vector3.zero;
            if (keyboard != null)
            {
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move += forward;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move -= forward;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move += right;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move -= right;
                if (keyboard.spaceKey.isPressed || keyboard.eKey.isPressed) move += up;
                if (keyboard.leftCtrlKey.isPressed || keyboard.qKey.isPressed) move -= up;
            }
            // Flying low he keeps a little height instead of scraping along the ground, so moving off is a take-off;
            // Ctrl still brings him down to land.
            bool descending = keyboard != null && (keyboard.leftCtrlKey.isPressed || keyboard.qKey.isPressed);
            if (move.sqrMagnitude > 0.01f && !descending)
            {
                float low = 1f - Mathf.Clamp01((float)AltitudeM / 3f);
                float rise = Vector3.Dot(move, up);
                if (rise < 0.35f * low) move += up * (0.35f * low - rise);
            }
            if (move.sqrMagnitude > 1f) move.Normalize();
            bool moving = move.sqrMagnitude > 0.01f;
            // Let go just above the ground and he settles onto it.
            if (!moving && !Grounded && AltitudeM < 1.5) move = -up * 0.1f;
            Boosting = moving && keyboard != null && keyboard.shiftKey.isPressed;

            double altitude = AltitudeAbove(eci, jd, out _);
            double cruiseMs = math.min(math.max(MinSpeedMs, altitude * SpeedPerMetre) * (Boosting ? BoostFactor : 1), MaxSpeedMs);
            double3 targetEci = Astro.UnityToEci(move) * (cruiseMs / 1000.0);
            // The edge of the Earth–Moon system: no further out.
            if (math.length(eci) > EdgeKm)
            {
                double3 outward = math.normalize(eci);
                targetEci -= outward * math.max(0, math.dot(targetEci, outward));
                _velocity -= DirectionToAnchor(outward, jd) * math.max(0, math.dot(DirectionToEci(_velocity, jd), outward));
            }
            double3 target = DirectionToAnchor(targetEci, jd);
            _velocity = math.lerp(_velocity, target, 1.0 - math.exp(-dt * (moving ? 3.2 : 2.4)));
            _position += _velocity * dt;
            ChooseAnchor(jd);

            // Land on the ground instead of going through it.
            eci = Eci(jd);
            altitude = AltitudeAbove(eci, jd, out double3 center);
            Grounded = altitude <= StandHeightM + 0.02;
            if (altitude < StandHeightM)
            {
                double3 outward = math.normalize(eci - center);
                eci += outward * ((StandHeightM - altitude) / 1000.0);
                _position = FromEci(eci, jd);
                double3 outwardHere = DirectionToAnchor(outward, jd);
                double inward = math.dot(_velocity, outwardHere);
                if (inward < 0) _velocity -= outwardHere * inward;
                altitude = StandHeightM;
            }
            AltitudeM = altitude - StandHeightM;
            SpeedMs = math.length(_velocity) * 1000.0;
            Astro.EcefToGeo(Astro.EciToEcef(eci, jd), out double lat, out double lon, out _);
            LatDeg = lat;
            LonDeg = lon;

            Pose(dt, forward, up, jd);
            Sound();

            // Camera: pulls back and widens a little at super speed.
            _distance = Mathf.Lerp(_distance, _distanceTarget * (Boosting ? 1.35f : 1f), 1f - Mathf.Exp(-dt * 4f));
            _fov = Mathf.Lerp(_fov, Boosting ? 80f : 62f + 6f * _fly, 1f - Mathf.Exp(-dt * 3f));

            _model.PositionKm = eci;
            _model.Rotation = _body;
        }

        /// <summary>Turns the view smoothly towards the Moon (or towards the Earth when he is at the Moon).</summary>
        public void FaceTarget()
        {
            if (Active) _facing = true;
        }

        void TurnTowardsTarget(double3 eci, double jd, float dt)
        {
            double3 target = NearMoon ? double3.zero : Astro.MoonPosition(jd);
            Vector3 wanted = Astro.EciToUnity(math.normalize(target - eci)).normalized;
            Vector3 forward = _look * Vector3.forward;
            Vector3 turned = Vector3.Slerp(forward, wanted, 1f - Mathf.Exp(-dt * 4f));
            if (Mathf.Abs(Vector3.Dot(turned, _up)) > 0.995f) turned = Vector3.Slerp(forward, Vector3.ProjectOnPlane(wanted, _up), 0.5f);
            _look = Quaternion.LookRotation(turned, _up);
            if (Vector3.Angle(turned, wanted) < 0.05f) _facing = false;
        }

        /// <summary>Turns the view: yaw about the local vertical, pitch up or down short of straight up or down.</summary>
        void Look(float yawDeg, float pitchUpDeg)
        {
            Vector3 forward = Quaternion.AngleAxis(yawDeg, _up) * (_look * Vector3.forward);
            float elevation = Mathf.Asin(Mathf.Clamp(Vector3.Dot(forward, _up), -1f, 1f)) * Mathf.Rad2Deg;
            float wanted = Mathf.Clamp(elevation + pitchUpDeg, -85f, 85f);
            Vector3 right = Vector3.Cross(_up, forward).normalized;
            forward = Quaternion.AngleAxis(elevation - wanted, right) * forward;
            _look = Quaternion.LookRotation(forward, _up);
        }

        static Quaternion Level(Quaternion look, Vector3 up)
        {
            Vector3 forward = look * Vector3.forward;
            if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.999f) forward = look * Vector3.up;
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>
        /// Upright when hovering or walking pace; head-first and face-down at speed, banking into turns. Flying
        /// backwards keeps him upright rather than flipping round.
        /// </summary>
        void Pose(float dt, Vector3 forward, Vector3 up, double jd)
        {
            Vector3 velocity = Astro.EciToUnity(DirectionToEci(_velocity, jd));
            Vector3 direction = velocity.sqrMagnitude > 1e-14f ? velocity.normalized : forward;
            float speed01 = Mathf.Clamp01((float)(SpeedMs - 3.0) / 12f);
            float ahead = Mathf.Clamp01(Vector3.Dot(direction, forward) * 1.5f + 0.6f);
            float flyTarget = Grounded ? 0f : speed01 * ahead;
            _fly = Mathf.Lerp(_fly, flyTarget, 1f - Mathf.Exp(-dt * 3f));

            Vector3 flat = Vector3.ProjectOnPlane(forward, up);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.ProjectOnPlane(_body * Vector3.forward, up);
            Quaternion hover = Quaternion.LookRotation(flat.normalized, up);

            Vector3 chest = Vector3.ProjectOnPlane(-up + flat.normalized * 0.3f, direction);
            if (chest.sqrMagnitude < 1e-6f) chest = flat;
            Quaternion flying = Quaternion.LookRotation(chest.normalized, direction);

            float turnRate = _lastFlightDirection == Vector3.zero ? 0f : Vector3.SignedAngle(_lastFlightDirection, direction, up) / Mathf.Max(dt, 1e-4f);
            _lastFlightDirection = direction;
            float bankTarget = Mathf.Clamp(-turnRate * 0.35f, -60f, 60f) * _fly;
            _bank = Mathf.Lerp(_bank, bankTarget, 1f - Mathf.Exp(-dt * 4f));

            Quaternion target = Quaternion.Slerp(hover, flying * Quaternion.AngleAxis(_bank, Vector3.up), _fly);
            _body = Quaternion.Slerp(_body, target, 1f - Mathf.Exp(-dt * 9f));

            _model.Fly = _fly;
            _model.Speed01 = speed01;
            _model.CapeFlowWorld = Vector3.Slerp(-up, -direction, speed01).normalized;
        }

        /// <summary>Wind in the air, and a sonic boom when he breaks the sound barrier.</summary>
        void Sound()
        {
            if (_audio == null) return;
            double air = NearMoon ? 0 : math.exp(-AltitudeM / 8500.0);
            bool supersonic = air > 0.05 && SpeedMs > (_supersonic ? 300 : SpeedOfSoundMs);
            if (supersonic && !_supersonic) _audio.PlayBoom(0.9f);
            _supersonic = supersonic;
            _audio.SetWind((float)(air * math.saturate(SpeedMs / 200.0)), (float)math.saturate(SpeedMs / 600.0));
        }

        /// <summary>Third person: behind and a little above him, looking just over his head.</summary>
        CameraPose? CameraPose()
        {
            if (!Active) return null;
            double jd = _sim.Clock.JulianDate;
            double3 hero = Eci(jd);
            Vector3 forward = _look * Vector3.forward;
            Vector3 offset = -forward * _distance + _up * (0.25f + 0.18f * _distance);
            double3 camera = hero + Astro.UnityToEci(offset) / 1000.0;
            double cameraHeight = AltitudeAbove(camera, jd, out double3 center);
            if (cameraHeight < 0.5) camera += math.normalize(camera - center) * ((0.5 - cameraHeight) / 1000.0);
            double3 aim = hero + Astro.UnityToEci(_up * (0.35f + 0.05f * _distance)) / 1000.0;
            return new CameraPose
            {
                PositionKm = camera,
                Forward = math.normalize(aim - camera),
                Up = Astro.UnityToEci(_up),
                FovDeg = _fov,
            };
        }

        /// <summary>A soft light from the camera's side at night, so he isn't a silhouette.</summary>
        void PublishLight()
        {
            if (!Active) return;
            Vector3 sun = Astro.EciToUnity(Astro.SunDirection(_sim.Clock.JulianDate)).normalized;
            float night = Mathf.Clamp01(-Vector3.Dot(_up, sun) * 4f + 0.3f);
            Vector3 key = (-(_look * Vector3.forward) + _up * 0.6f).normalized;
            Cinematic.SetKeyLight(key, Mathf.Lerp(0.12f, 1.1f, night), Color.Lerp(new Color(1f, 0.97f, 0.92f), new Color(0.8f, 0.87f, 1f), night));
        }

        // ───────────────────────────── Frames ─────────────────────────────

        void ChooseAnchor(double jd)
        {
            double3 eci = Eci(jd);
            var wanted = math.distance(eci, Astro.MoonPosition(jd)) < MoonConstants.SoiKm ? Anchor.Moon
                : math.length(eci) < EarthFixedWithinKm ? Anchor.EarthFixed : Anchor.Inertial;
            if (wanted == _anchor) return;
            double3 velocity = DirectionToEci(_velocity, jd);
            _anchor = wanted;
            _position = FromEci(eci, jd);
            _velocity = DirectionToAnchor(velocity, jd);
        }

        double3 Eci(double jd) =>
            _anchor == Anchor.EarthFixed ? Astro.EcefToEci(_position, jd)
            : _anchor == Anchor.Moon ? Astro.MoonPosition(jd) + _position
            : _position;

        double3 FromEci(double3 eci, double jd) =>
            _anchor == Anchor.EarthFixed ? Astro.EciToEcef(eci, jd)
            : _anchor == Anchor.Moon ? eci - Astro.MoonPosition(jd)
            : eci;

        double3 DirectionToAnchor(double3 v, double jd) => _anchor == Anchor.EarthFixed ? Astro.EciToEcef(v, jd) : v;
        double3 DirectionToEci(double3 v, double jd) => _anchor == Anchor.EarthFixed ? Astro.EcefToEci(v, jd) : v;

        /// <summary>Height (m) of a point above the ground of the Earth, or of the Moon when inside its sphere of influence.</summary>
        static double AltitudeAbove(double3 eci, double jd, out double3 center)
        {
            double3 moon = Astro.MoonPosition(jd);
            if (math.distance(eci, moon) < MoonConstants.SoiKm)
            {
                center = moon;
                return (math.distance(eci, moon) - MoonConstants.RadiusKm) * 1000.0;
            }
            center = double3.zero;
            Astro.EcefToGeo(Astro.EciToEcef(eci, jd), out double lat, out double lon, out _);
            return (math.length(eci) - EarthConstants.RadiusKm) * 1000.0 - GroundM(lat, lon);
        }

        /// <summary>Terrain height (m) once its tile has streamed in; the sea surface over water.</summary>
        static double GroundM(double latDeg, double lonDeg) => math.max(GlobeTiles.Instance?.HeightAt(latDeg, lonDeg) ?? 0, 0);

        static Vector3 Up(double3 eci, double jd)
        {
            double3 moon = Astro.MoonPosition(jd);
            double3 center = math.distance(eci, moon) < MoonConstants.SoiKm ? moon : double3.zero;
            return Astro.EciToUnity(math.normalize(eci - center)).normalized;
        }

        static Vector3 North(Vector3 up)
        {
            Vector3 north = Vector3.ProjectOnPlane(Astro.EciToUnity(new double3(0, 0, 1)), up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Astro.EciToUnity(new double3(1, 0, 0)), up);
            return north.normalized;
        }
    }
}
