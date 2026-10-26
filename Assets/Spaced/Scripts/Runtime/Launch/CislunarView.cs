using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// What you see of a <see cref="LunarMission"/>: the spacecraft (on the spent Falcon 9 upper stage until
    /// separation, or the Ship), its engine plume, the coming trajectory drawn through space, and — once it gets low
    /// over the Moon — a patch of regolith and boulders under the landing site.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public sealed class CislunarView : MonoBehaviour
    {
        const float TurnRateDegPerSecond = 6f;
        const float StageOffset = 13.5f; // craft sits on the payload adapter
        const int OrbitSegments = 128;

        LunarMission _mission;
        SpaceSimulation _sim;
        Fx _fx;
        Camera _far;
        Transform _craft, _stage;
        MeshRenderer _plume, _stagePlume;
        LineRenderer _path;
        Vector3[] _points = new Vector3[700];
        double3 _axis;
        float _plumeRadius, _plumeLength;
        Transform _ground;
        double _groundLat, _groundLon;
        bool _groundFrozen;
        MaterialLibrary _materials;
        float _orbitTimer;
        readonly double3[] _orbit = new double3[OrbitSegments];

        public LunarMission Mission => _mission;
        /// <summary>Where the camera should look (inertial km): the craft's middle.</summary>
        public double3 CenterKm => _mission.State.R + _axis * (_mission.Craft.IsShip ? 0.026 : 0.0015);

        public void Init(LunarMission mission, SpaceSimulation sim, MaterialLibrary materials, Fx fx, Shader lineShader, Camera far)
        {
            _mission = mission;
            _sim = sim;
            _fx = fx;
            _far = far;
            _materials = materials;
            _axis = math.normalize(mission.State.V);

            if (mission.Craft.IsShip)
            {
                _craft = Part("Ship", StarshipModels.Ship(), transform);
                _plumeRadius = 2.6f; _plumeLength = 45f;
            }
            else
            {
                _craft = Part(mission.Craft.Name, LunarModels.For(mission.Craft), transform);
                _plumeRadius = mission.Kind == LunarMissionKind.Landing ? 0.45f : mission.Kind == LunarMissionKind.Orbit ? 0.25f : 0.12f;
                _plumeLength = _plumeRadius * 18f;
                _stage = Part("Upper Stage", RocketModels.SecondStage(), transform);
                Part("MVac", RocketModels.VacuumNozzle(), _stage);
                _stagePlume = fx.CreatePlume(_stage, Vector3.zero, "MVac Plume");
            }
            _plume = fx.CreatePlume(_craft, Vector3.zero, "Plume");
            Fx.SetLayer(gameObject);

            var line = new GameObject("Trajectory");
            line.transform.SetParent(transform, false);
            _path = line.AddComponent<LineRenderer>();
            _path.useWorldSpace = true;
            _path.numCapVertices = 0;
            _path.shadowCastingMode = ShadowCastingMode.Off;
            _path.receiveShadows = false;
            _path.material = new Material(lineShader) { name = "Trajectory" };
            _path.startColor = _path.endColor = new Color(0.45f, 0.85f, 1f, 0.7f);
            _path.positionCount = 0;
        }

        Transform Part(string name, SurfaceMesh mesh, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh.Mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = _materials.For(mesh);
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go.transform;
        }

        void LateUpdate()
        {
            if (_mission == null || _sim == null) return;
            var m = _mission;
            double jd = m.State.Jd;
            // Nothing left to show after a crash or once the capsule is home.
            bool gone = m.Phase == LunarPhase.Lost || m.Phase == LunarPhase.Splashdown;
            _craft.gameObject.SetActive(!gone);
            if (gone)
            {
                if (_stage != null) _stage.gameObject.SetActive(false);
                _path.enabled = false;
                return;
            }
            double dt = math.max(_sim.LastDeltaSeconds, 0);

            // Attitude: along the thrust while burning, upright once landed, otherwise prograde (relative to the
            // nearer body), turning at a finite rate.
            double3 target;
            if (m.Burning && m.Throttle > 0) target = m.ThrustDirection;
            else if (m.Phase == LunarPhase.Landed) target = math.normalize(m.State.R - Astro.MoonPosition(jd));
            else if (m.MoonAltitudeKm < m.EarthAltitudeKm)
            {
                Cislunar.Moon(jd, out _, out double3 moonVelocity);
                target = math.normalizesafe(m.State.V - moonVelocity, _axis);
            }
            else target = math.normalize(m.State.V);
            _axis = Turn(_axis, target, TurnRateDegPerSecond * MathConst.Deg2Rad * math.min(dt, 30));
            if (m.Phase == LunarPhase.Landed) _axis = target;

            Quaternion rotation = AxisRotation(_axis);
            double3 baseM = m.State.R * 1000.0;
            if (_stage != null)
            {
                _stage.gameObject.SetActive(m.StageAttached);
                _stage.SetPositionAndRotation(WorldFrame.ToLocalMeters(baseM), rotation);
                _fx.UpdatePlume(_stagePlume, 1.55f, 0, m.StageAttached && m.Burning ? m.Throttle : 0, 30f);
                if (m.StageAttached) baseM += _axis * StageOffset;
            }
            _craft.SetPositionAndRotation(WorldFrame.ToLocalMeters(baseM), rotation);
            _fx.UpdatePlume(_plume, _plumeRadius, 0, !m.StageAttached && m.Burning ? m.Throttle : 0, _plumeLength);

            UpdatePath(jd);
            UpdateGround(jd);
        }

        void UpdatePath(double jd)
        {
            var m = _mission;
            int count = 0;
            bool aroundMoon = m.Phase == LunarPhase.LunarOrbit || m.Phase == LunarPhase.DescentCoast;
            if (aroundMoon)
            {
                // One revolution of the current lunar orbit, Moon-relative, re-sampled twice a second.
                _orbitTimer -= Time.unscaledDeltaTime;
                if (_orbitTimer <= 0)
                {
                    _orbitTimer = 0.5f;
                    Cislunar.Moon(jd, out double3 moon, out double3 moonVelocity);
                    double3 r = m.State.R - moon, v = m.State.V - moonVelocity;
                    double period = MathConst.TwoPi * math.sqrt(math.pow(math.length(r), 3) / MoonConstants.Mu);
                    for (int i = 0; i < OrbitSegments; i++)
                    {
                        double3 ri = r, vi = v;
                        Cislunar.Kepler(ref ri, ref vi, MoonConstants.Mu, period * i / OrbitSegments);
                        _orbit[i] = ri;
                    }
                }
                double3 moonNow = Astro.MoonPosition(jd);
                for (int i = 0; i < OrbitSegments; i++) _points[count++] = WorldFrame.ToFar(moonNow + _orbit[i]);
                _path.loop = true;
            }
            else
            {
                _path.loop = false;
                foreach (var p in m.PredictedPath)
                {
                    if (count >= _points.Length) break;
                    _points[count++] = WorldFrame.ToFar(p);
                }
            }
            _path.enabled = count > 1 && !Cinematic.Active && m.Phase != LunarPhase.Landed && m.Phase != LunarPhase.Splashdown && m.Phase != LunarPhase.Lost;
            _path.positionCount = count;
            if (count > 0) _path.SetPositions(_points);
            float distance = _far != null ? Vector3.Distance(_far.transform.position, WorldFrame.ToFar(m.State.R)) : 20f;
            _path.widthMultiplier = Mathf.Clamp(distance * 0.002f, 1e-6f, 2f);
        }

        /// <summary>A disc of regolith under the craft once it is low over the Moon, frozen where it touches down.</summary>
        void UpdateGround(double jd)
        {
            var m = _mission;
            bool low = m.MoonAltitudeKm < 60 && (m.Phase == LunarPhase.DescentCoast || m.Phase == LunarPhase.PoweredDescent || m.Phase == LunarPhase.Landed);
            if (!low)
            {
                if (_ground != null) _ground.gameObject.SetActive(false);
                return;
            }
            if (_ground == null) CreateGround();
            _ground.gameObject.SetActive(true);
            if (m.Phase == LunarPhase.Landed) { _groundLat = m.LandedLatDeg; _groundLon = m.LandedLonDeg; _groundFrozen = true; }
            else if (!_groundFrozen)
            {
                Cislunar.MoonLatLon(m.State.R, jd, out _groundLat, out _groundLon);
                if (m.MoonAltitudeKm < 4) _groundFrozen = true;
            }
            double3 site = Cislunar.MoonSurfacePoint(_groundLat, _groundLon, 0, jd);
            double3 up = math.normalize(site - Astro.MoonPosition(jd));
            Cislunar.MoonAxes(jd, out _, out double3 moonNorth, out _);
            double3 east = math.normalizesafe(math.cross(moonNorth, up), new double3(1, 0, 0));
            double3 north = math.cross(up, east);
            _ground.SetPositionAndRotation(WorldFrame.ToLocalMeters(site * 1000.0), WorldFrame.LookRotation(north, up));
        }

        void CreateGround()
        {
            _ground = new GameObject("Lunar Surface").transform;
            _ground.SetParent(transform, false);
            var disc = new GameObject("Regolith");
            disc.transform.SetParent(_ground, false);
            disc.AddComponent<MeshFilter>().sharedMesh = SiteModels.GroundDisc(30000f, 90, 128, 0f, (float)(MoonConstants.RadiusKm * 1000.0));
            var meshRenderer = disc.AddComponent<MeshRenderer>();
            var material = new Material(_materials[Surface.Regolith]) { name = "Regolith Ground" };
            material.SetTextureScale("_BaseMap", new Vector2(0.03f, 0.03f));
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            var boulders = Part("Boulders", LunarModels.Boulders(400, 600f, 11), _ground);
            boulders.localPosition = Vector3.zero;
            Fx.SetLayer(_ground.gameObject);
        }

        static double3 Turn(double3 from, double3 to, double maxAngle)
        {
            double angle = math.acos(math.clamp(math.dot(from, to), -1, 1));
            if (angle <= maxAngle || angle < 1e-9) return to;
            double3 axis = math.cross(from, to);
            if (math.lengthsq(axis) < 1e-12) axis = math.cross(from, new double3(0, 0, 1));
            axis = math.normalize(axis);
            double c = math.cos(maxAngle), s = math.sin(maxAngle);
            return math.normalize(from * c + math.cross(axis, from) * s + axis * math.dot(axis, from) * (1 - c));
        }

        static Quaternion AxisRotation(double3 axisEci)
        {
            Vector3 axis = Astro.EciToUnity(axisEci).normalized;
            Vector3 reference = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (reference.sqrMagnitude < 1e-6f) reference = Vector3.ProjectOnPlane(Vector3.forward, axis);
            return Quaternion.LookRotation(reference.normalized, axis);
        }
    }
}
