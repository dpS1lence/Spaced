using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// The visible Falcon 9 for one <see cref="RocketFlight"/>: stack, booster and second stage after staging,
    /// fairing halves, plumes, exhaust trail, legs and grid fins. Attitude follows the guidance with a finite
    /// turn rate, so the booster visibly flips after separation.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class RocketView : MonoBehaviour
    {
        const float TurnRateDegPerSecond = 9f;
        static readonly int Emission = Shader.PropertyToID("_EmissionColor");

        RocketFlight _flight;
        SpaceSimulation _sim;
        LaunchSiteView _site;
        DroneshipView _droneship;
        Fx _fx;

        Transform _booster, _second, _payload;
        readonly Transform[] _fairings = new Transform[2];
        readonly Transform[] _legs = new Transform[4];
        readonly Transform[] _fins = new Transform[4];
        MeshRenderer _boosterPlume, _secondPlume, _nozzle;
        ParticleSystem _trail;
        // Unity objects can't be created in field initializers of a MonoBehaviour; made in Init.
        MaterialPropertyBlock _block;

        double3 _boosterAxis, _secondAxis;
        float _legs01, _fins01;
        bool _fairingsDetached;

        public RocketFlight Flight => _flight;
        /// <summary>Set by the launch manager when this rocket is the one being watched in cinematic mode.</summary>
        public bool CinematicLighting;
        public CameraRig Rig;

        ParticleSystem _coldGas, _vaporCone;
        bool _separationPuffed;
        double3 _lastBoosterAxis;

        public void Init(RocketFlight flight, SpaceSimulation sim, LaunchSiteView site, MaterialLibrary materials, Fx fx)
        {
            _flight = flight;
            _sim = sim;
            _site = site;
            _fx = fx;
            _block = new MaterialPropertyBlock();
            _boosterAxis = _secondAxis = math.normalize(flight.Position);

            _booster = Part("Booster", RocketModels.Booster(), materials, transform);
            for (int i = 0; i < 4; i++)
            {
                float angle = i * 90f + 45f;
                var hinge = new GameObject($"Leg {i}").transform;
                hinge.SetParent(_booster, false);
                var dir = Quaternion.Euler(0, -angle, 0) * Vector3.right;
                hinge.localPosition = dir * 1.95f + Vector3.up * RocketModels.LegHingeHeight;
                hinge.localRotation = Quaternion.LookRotation(dir);
                Part("Leg", RocketModels.Leg(), materials, hinge);
                _legs[i] = hinge;

                var fin = new GameObject($"Grid Fin {i}").transform;
                fin.SetParent(_booster, false);
                var finDir = Quaternion.Euler(0, -i * 90f, 0) * Vector3.right;
                fin.localPosition = finDir * 1.83f + Vector3.up * RocketModels.GridFinHeight;
                fin.localRotation = Quaternion.LookRotation(finDir);
                Part("Fin", RocketModels.GridFin(), materials, fin);
                _fins[i] = fin;
            }
            _boosterPlume = fx.CreatePlume(_booster, Vector3.zero, "Booster Plume");

            _second = Part("Second Stage", RocketModels.SecondStage(), materials, transform);
            _nozzle = Part("MVac", RocketModels.VacuumNozzle(), materials, _second).GetComponent<MeshRenderer>();
            _payload = Part("Payload", RocketModels.PayloadStack(), materials, _second);
            _payload.gameObject.SetActive(false);
            for (int s = 0; s < 2; s++)
            {
                _fairings[s] = Part($"Fairing {s}", RocketModels.FairingHalf(s == 0 ? 1 : -1), materials, _second);
                _fairings[s].localPosition = Vector3.up * (RocketModels.FairingBase + RocketModels.FairingLength * 0.5f);
            }
            _secondPlume = fx.CreatePlume(_second, Vector3.zero, "MVac Plume");

            // Exhaust trail, simulated in the pad's Earth-fixed frame.
            var emitter = new GameObject("Trail Emitter").transform;
            emitter.SetParent(_booster, false);
            emitter.localPosition = Vector3.down * 6f;
            _trail = fx.CreateSmoke(emitter, site.Frame, fx.FlameSmoke, 9f, 32f, 45f, 5000, new Color(0.92f, 0.92f, 0.92f, 0.85f), 7f);

            // Nitrogen cold-gas thrusters on the interstage (stage separation, the booster's flip), and the
            // condensation collar that forms around the vehicle as it goes transonic near max-Q.
            _coldGas = fx.CreateBurst(_booster, Vector3.up * 44.5f, site.Frame, 1.6f, 1.6f, 22f, 400, new Color(1f, 1f, 1f, 0.9f), 5f, 1.9f);
            _vaporCone = fx.CreateBurst(_booster, Vector3.up * 40f, null, 4.5f, 0.35f, 4f, 900, new Color(1f, 1f, 1f, 0.55f), 2.2f, 2.6f);
            Fx.SetLayer(gameObject);
        }

        public void AttachDroneship(DroneshipView droneship) => _droneship = droneship;

        Transform Part(string name, SurfaceMesh mesh, MaterialLibrary materials, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh.Mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.For(mesh);
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Where the camera should look at the booster or the upper stage (inertial km).</summary>
        public double3 BoosterCenterKm => BoosterBaseM() / 1000.0 + _boosterAxis * 0.022;
        public double3 UpperStageCenterKm => SecondBaseM() / 1000.0 + _secondAxis * 0.007;
        public double3 StackCenterKm => _flight.BoosterAttached ? _flight.RenderPosition / 1000.0 + _boosterAxis * 0.035 : UpperStageCenterKm;
        public bool BoosterVisible => _flight.Booster != null || _flight.BoosterAttached;

        /// <summary>Camera on the second stage just above the interstage, looking back down the vehicle.</summary>
        public CameraPose? SecondStageOnboard => Onboard(_second, SecondBaseM(), _secondAxis, 9.0, 2.15);

        /// <summary>Camera on the booster's interstage looking down past the legs (the landing view).</summary>
        public CameraPose? BoosterOnboard => _flight.Booster != null && _flight.Booster.Phase == BoosterPhase.Lost
            ? (CameraPose?)null
            : Onboard(_booster, BoosterBaseM(), _boosterAxis, 44.0, 2.1);

        CameraPose? Onboard(Transform part, double3 baseEciM, double3 axisEci, double height, double outward)
        {
            if (part == null) return null;
            double3 radial = math.normalize(Astro.UnityToEci(part.right));
            return new CameraPose
            {
                PositionKm = (baseEciM + axisEci * height + radial * outward) / 1000.0,
                Forward = math.normalize(-axisEci + radial * 0.12),
                Up = radial,
            };
        }

        double3 BoosterBaseM()
        {
            var booster = _flight.Booster;
            if (booster == null) return _flight.RenderPosition;
            if (booster.Phase == BoosterPhase.Landed) return Astro.EcefToEci(booster.TouchdownEcefKm, WorldFrame.JulianDate) * 1000.0;
            // A landed booster is already Earth-fixed; in flight it steps with the stack, so extrapolate the same way.
            return _flight.Extrapolate(booster.Position, booster.Velocity);
        }

        double3 SecondBaseM() => _flight.BoosterAttached
            ? _flight.RenderPosition + _boosterAxis * RocketModels.SecondStageBase
            : _flight.RenderPosition + _secondAxis * RocketModels.SecondStageBase;

        void LateUpdate()
        {
            if (_flight == null || _block == null || _sim == null) return; // not (fully) initialised
            double dt = math.max(_sim.LastDeltaSeconds, 0);
            double warp = _sim.Clock.Paused ? 0 : _sim.Clock.Warp;
            var booster = _flight.Booster;

            // Attitude: thrust direction while burning, prograde/retrograde otherwise, with a finite turn rate.
            double3 up = math.normalize(_flight.Position);
            double3 stackTarget = _flight.Phase == FlightPhase.Countdown ? up
                : _flight.EnginesLit > 0 ? _flight.ThrustDirection
                : math.normalize(_flight.Velocity);
            double maxTurn = TurnRateDegPerSecond * MathConst.Deg2Rad * dt;
            if (_flight.BoosterAttached)
            {
                _boosterAxis = Turn(_boosterAxis, stackTarget, maxTurn);
                _secondAxis = _boosterAxis;
            }
            else
            {
                _secondAxis = Turn(_secondAxis, stackTarget, maxTurn);
                if (booster != null && booster.Phase != BoosterPhase.Landed)
                    _boosterAxis = Turn(_boosterAxis, booster.ThrustDirection, maxTurn * 0.8);
                else if (booster != null)
                    _boosterAxis = math.normalize(BoosterBaseM());
            }

            Place(_booster, BoosterBaseM(), _boosterAxis);
            _booster.gameObject.SetActive(booster == null || booster.Phase != BoosterPhase.Lost);
            Place(_second, SecondBaseM(), _secondAxis);

            UpdateFairings();
            _payload.gameObject.SetActive(!_flight.FairingAttached && _flight.PayloadAttached);

            // Plumes.
            double boosterAltitude = booster != null ? booster.AltitudeM : _flight.AltitudeKm * 1000.0;
            double boosterDensity = Atmosphere.Density(boosterAltitude / 1000.0);
            if (_flight.BoosterAttached)
                _fx.UpdatePlume(_boosterPlume, 1.9f, boosterDensity, _flight.EnginesLit > 0 ? _flight.Throttle : 0, 34f);
            else if (booster != null)
                _fx.UpdatePlume(_boosterPlume, booster.EnginesLit >= 3 ? 1.3f : 0.55f, boosterDensity, booster.EnginesLit > 0 ? booster.Throttle : 0, booster.EnginesLit >= 3 ? 26f : 14f);
            else
                _fx.UpdatePlume(_boosterPlume, 1.9f, 0, 0, 0);

            bool secondFiring = !_flight.BoosterAttached && _flight.EnginesLit > 0;
            _fx.UpdatePlume(_secondPlume, 1.55f, Atmosphere.Density(_flight.AltitudeKm), secondFiring ? _flight.Throttle : 0, 30f);
            // The niobium nozzle extension glows red-orange while it fires.
            _block.SetVector(Emission, secondFiring ? new Vector4(2.2f, 0.55f, 0.12f, 1f) : Vector4.zero);
            _nozzle.SetPropertyBlock(_block);

            // Exhaust trail: thick near the ground, gone above ~40 km.
            float throttle = _flight.BoosterAttached ? (float)_flight.Throttle * (_flight.EnginesLit > 0 ? 1 : 0)
                : booster != null && booster.EnginesLit > 0 ? (float)booster.Throttle * 0.5f : 0f;
            float airFactor = Mathf.Clamp01((float)(boosterDensity / 0.02));
            Fx.Drive(_trail, 260f * throttle * airFactor, warp);

            // Liftoff steam at the pad; the strongback leans back during the final minute.
            float t = (float)_flight.MissionTime;
            _site.SetStrongback(Mathf.InverseLerp(-55f, -40f, t));
            _site.Steam(t > -3f && t < 14f ? 220f : t >= 14f && t < 25f ? 40f : 0f, warp);

            // Landing: legs out, deck spray during the landing burn.
            if (booster != null)
            {
                _fins01 = Mathf.MoveTowards(_fins01, booster.GridFinsDeployed ? 1f : 0f, (float)dt * 0.8f);
                _legs01 = Mathf.MoveTowards(_legs01, booster.LegsDeployed ? 1f : 0f, (float)dt * 0.5f);
                if (_droneship != null)
                    _droneship.Spray(booster.Phase == BoosterPhase.LandingBurn && booster.AltitudeM < 250 ? 200f : 0f, warp);
            }
            for (int i = 0; i < 4; i++)
            {
                _legs[i].GetChild(0).localRotation = Quaternion.Euler(Mathf.Lerp(0f, 152f, _legs01), 0, 0);
                _fins[i].GetChild(0).localRotation = Quaternion.Euler(Mathf.Lerp(-90f, 0f, _fins01), 0, 0); // folded up → out
            }

            UpdateEffects(booster, dt);
        }

        void UpdateEffects(BoosterDescent booster, double dt)
        {
            // Puffs at separation and while the booster flips around.
            if (!_flight.BoosterAttached && !_separationPuffed)
            {
                _separationPuffed = true;
                _coldGas.Emit(60);
            }
            if (booster != null && booster.Phase == BoosterPhase.Coasting && dt > 0)
            {
                double turned = math.acos(math.clamp(math.dot(_lastBoosterAxis, _boosterAxis), -1, 1));
                if (turned > 1e-4) _coldGas.Emit(Mathf.Clamp((int)(turned * 2000), 1, 6));
            }
            _lastBoosterAxis = _boosterAxis;

            // Condensation collar: humid air flashing to vapour around Mach 1 in the lower atmosphere.
            if (_flight.BoosterAttached && _flight.Phase == FlightPhase.FirstStage && dt > 0)
            {
                double mach = math.length(_flight.AirVelocity) / 320.0;
                double altitudeKm = _flight.AltitudeKm;
                if (mach > 0.92 && mach < 1.3 && altitudeKm < 14) _vaporCone.Emit(Mathf.Clamp((int)(dt * 900), 2, 40));
            }
        }

        /// <summary>Key light from the camera's side (brighter at night, like pad floodlights) and engine glows.</summary>
        public void PublishLights()
        {
            if (_flight == null || _booster == null) return;
            var booster = _flight.Booster;
            var camera = Rig != null ? Rig.Near : null;
            Vector3 center = _flight.BoosterAttached || booster == null ? _booster.position + _booster.up * 30f : _second.position;
            Vector3 up = Astro.EciToUnity(math.normalize(_flight.Position));
            Vector3 toCamera = camera != null ? (camera.transform.position - center).normalized : up;
            Vector3 side = Vector3.Cross(up, toCamera);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            Vector3 key = (toCamera + up * 0.55f + side.normalized * 0.6f).normalized;

            float sunElevation = Vector3.Dot(up, Astro.EciToUnity(Astro.SunDirection(WorldFrame.JulianDate)).normalized);
            float night = Mathf.Clamp01(0.3f - sunElevation * 4f);
            Cinematic.SetKeyLight(key, Mathf.Lerp(0.6f, 2.6f, night), Color.Lerp(new Color(1f, 0.96f, 0.9f), new Color(0.8f, 0.88f, 1f), night));

            // Engine glows: bright with nine engines in thick air, dimmer for one engine in vacuum.
            float boosterGlow = 0f, secondGlow = 0f;
            if (_flight.BoosterAttached) boosterGlow = _flight.EnginesLit > 0 ? (float)_flight.Throttle * 9f : 0f;
            else if (booster != null && booster.EnginesLit > 0) boosterGlow = (float)booster.Throttle * (booster.EnginesLit >= 3 ? 6f : 3.5f);
            if (!_flight.BoosterAttached && _flight.EnginesLit > 0) secondGlow = (float)_flight.Throttle * 2.5f;
            Cinematic.SetGlows(_booster.position - _booster.up * 5f, boosterGlow, _second.position - _second.up * 4f, secondGlow);
        }

        void UpdateFairings()
        {
            if (_flight.FairingAttached || _flight.Fairings.Count < 2) return;
            if (!_fairingsDetached)
            {
                _fairingsDetached = true;
                foreach (var f in _fairings) f.SetParent(transform, true);
            }
            double time = _flight.MissionTime;
            for (int s = 0; s < 2; s++)
            {
                var half = _flight.Fairings[s];
                _fairings[s].gameObject.SetActive(!half.Splashed);
                if (half.Splashed) continue;
                var spin = Quaternion.AngleAxis((float)(half.Age * 14.0), Astro.EciToUnity(half.SpinAxis));
                _fairings[s].SetPositionAndRotation(WorldFrame.ToLocalMeters(_flight.Extrapolate(half.Position, half.Velocity)), spin * AxisRotation(_secondAxis));
            }
        }

        static double3 Turn(double3 from, double3 to, double maxAngle)
        {
            double angle = math.acos(math.clamp(math.dot(from, to), -1, 1));
            if (angle <= maxAngle || angle < 1e-9) return to;
            double3 axis = math.cross(from, to);
            if (math.lengthsq(axis) < 1e-12) axis = math.cross(from, new double3(0, 0, 1));
            axis = math.normalize(axis);
            // Rodrigues rotation of `from` about `axis` by maxAngle.
            double c = math.cos(maxAngle), s = math.sin(maxAngle);
            return math.normalize(from * c + math.cross(axis, from) * s + axis * math.dot(axis, from) * (1 - c));
        }

        static Quaternion AxisRotation(double3 axisEci)
        {
            Vector3 axis = Astro.EciToUnity(axisEci).normalized;
            // Roll reference: world "north" (Earth's axis), so the roll stays steady through the flight.
            Vector3 reference = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (reference.sqrMagnitude < 1e-6f) reference = Vector3.ProjectOnPlane(Vector3.forward, axis);
            return Quaternion.LookRotation(reference.normalized, axis);
        }

        static void Place(Transform part, double3 baseEciM, double3 axisEci) =>
            part.SetPositionAndRotation(WorldFrame.ToLocalMeters(baseEciM), AxisRotation(axisEci));
    }
}
