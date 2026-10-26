using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// The selected object's ground track: where it will pass over the rotating Earth during the next 1.5 orbits
    /// (and where it came from over the last quarter), drawn just above the surface.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class GroundTrackRenderer : MonoBehaviour
    {
        const int Points = 400;
        const double PastFraction = 0.25, FutureFraction = 1.5;

        SpaceSimulation _sim;
        CameraRig _rig;
        LineRenderer _line;
        readonly double3[] _ecef = new double3[Points];
        readonly Vector3[] _positions = new Vector3[Points];
        uint _id;
        float _timer;

        public void Init(SpaceSimulation sim, CameraRig rig, Shader shader)
        {
            _sim = sim;
            _rig = rig;
            _line = GetComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = Points;
            _line.numCapVertices = 0;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.material = new Material(shader) { name = "Ground Track" };
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.4f, 0.9f, 1f), 0f), new GradientColorKey(new Color(0.4f, 0.9f, 1f), 1f) },
                new[] { new GradientAlphaKey(0.05f, 0f), new GradientAlphaKey(0.9f, (float)(PastFraction / (PastFraction + FutureFraction))), new GradientAlphaKey(0.15f, 1f) });
            _line.colorGradient = gradient;
            _line.enabled = false;
        }

        void LateUpdate()
        {
            if (_sim == null || _line == null) return;
            int index = Cinematic.Active || Cinematic.SatellitesHidden ? -1 : _sim.SelectedIndex;
            _line.enabled = index >= 0;
            if (index < 0) return;

            double jd = _sim.Clock.JulianDate;
            uint id = _sim.Catalog.Ids[index];
            _timer -= Time.unscaledDeltaTime;
            if (id != _id || _timer <= 0)
            {
                _id = id;
                _timer = 0.2f;
                Recompute(index, jd);
            }

            // Earth-fixed samples follow the planet's rotation into this frame.
            for (int k = 0; k < Points; k++) _positions[k] = WorldFrame.ToFar(Astro.EcefToEci(_ecef[k], jd));
            _line.SetPositions(_positions);
            float distance = Vector3.Distance(_rig.Far.transform.position, WorldFrame.ToFar(Astro.EcefToEci(_ecef[Points / 6], jd)));
            _line.widthMultiplier = Mathf.Clamp(distance * 0.002f, 1e-6f, 0.08f);
        }

        void Recompute(int index, double jd)
        {
            var start = _sim.Catalog.Elements[index];
            double period = start.PeriodSeconds;
            double lift = EarthConstants.RadiusKm + 3.0; // a little above the ground so it isn't hidden by the sphere's facets
            for (int k = 0; k < Points; k++)
            {
                double offset = (-PastFraction + (PastFraction + FutureFraction) * k / (Points - 1.0)) * period;
                var el = start;
                OrbitMath.Advance(ref el, 0, offset);
                OrbitMath.ToStateVector(el, out double3 r, out _);
                _ecef[k] = math.normalize(Astro.EciToEcef(r, jd + offset / MathConst.SecondsPerDay)) * lift;
            }
        }
    }
}
