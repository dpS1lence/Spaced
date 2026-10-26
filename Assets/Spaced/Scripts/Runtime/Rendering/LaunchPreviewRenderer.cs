using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// While a launch is being set up: the orbit (or orbit planes) it will reach, and the ground track the first
    /// satellite will trace over the rotating Earth, starting with the rocket's climb from the pad.
    /// The HUD calls <see cref="Show"/> every frame it wants the preview; it disappears as soon as it stops.
    /// </summary>
    public sealed class LaunchPreviewRenderer : MonoBehaviour
    {
        public const int MaxPlanes = 24, MaxTrackOrbits = 16;
        /// <summary>Leave the other satellites' dots out while the preview is up.</summary>
        public bool HideOthers;
        const int RingSegments = 160, PointsPerOrbit = 160, AscentPoints = 400;
        const double LiftKm = 4.0;
        static readonly Color TrackColor = new Color(0.45f, 1f, 0.55f);

        SpaceSimulation _sim;
        CameraRig _rig;
        LineRenderer _track;
        readonly List<LineRenderer> _rings = new List<LineRenderer>();
        readonly double3[] _trackEcef = new double3[AscentPoints + MaxTrackOrbits * PointsPerOrbit];
        readonly Vector3[] _trackPositions = new Vector3[AscentPoints + MaxTrackOrbits * PointsPerOrbit];
        readonly double3[][] _ringEci = new double3[MaxPlanes][];
        readonly Vector3[] _ringPositions = new Vector3[RingSegments];
        readonly List<OrbitElements> _orbits = new List<OrbitElements>();
        int _trackCount, _ringCount, _shownFrame = -10;
        float _timer;
        bool _dirty;

        // What was asked for, so the samples are only rebuilt when it changes (or the clock moves on).
        double _epochJd;
        int _trackOrbits;
        double3[] _ascent;

        public void Init(SpaceSimulation sim, CameraRig rig, Shader shader)
        {
            _sim = sim;
            _rig = rig;
            var material = new Material(shader) { name = "Launch Preview" };
            _track = NewLine("Preview Ground Track", material, false);
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(TrackColor, 0f), new GradientColorKey(TrackColor, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.3f), new GradientAlphaKey(0.2f, 1f) });
            _track.colorGradient = gradient;
            for (int i = 0; i < MaxPlanes; i++)
            {
                var ring = NewLine($"Preview Orbit {i + 1}", material, true);
                ring.positionCount = RingSegments;
                ring.startColor = ring.endColor = new Color(TrackColor.r, TrackColor.g, TrackColor.b, 0.55f);
                _rings.Add(ring);
                _ringEci[i] = new double3[RingSegments];
            }
        }

        LineRenderer NewLine(string name, Material material, bool loop)
        {
            var line = new GameObject(name, typeof(LineRenderer)).GetComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.useWorldSpace = true;
            line.loop = loop;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = material;
            line.enabled = false;
            return line;
        }

        /// <summary>
        /// Shows the orbits (elements at <paramref name="epochJd"/>; the first one also gets the ground track) for
        /// <paramref name="trackOrbits"/> revolutions. The track can start with the rocket's own path over the ground
        /// (<paramref name="ascentEcef"/>, Earth-fixed km), which ends where the orbit starts.
        /// </summary>
        public void Show(List<OrbitElements> orbits, double epochJd, int trackOrbits, double3[] ascentEcef = null)
        {
            _shownFrame = Time.frameCount;
            bool changed = orbits.Count != _orbits.Count || trackOrbits != _trackOrbits || ascentEcef != _ascent
                           || math.abs(epochJd - _epochJd) * MathConst.SecondsPerDay > 1;
            for (int i = 0; !changed && i < orbits.Count; i++) changed = !Same(orbits[i], _orbits[i]);
            if (!changed) return;
            _orbits.Clear();
            _orbits.AddRange(orbits);
            _epochJd = epochJd;
            _trackOrbits = math.clamp(trackOrbits, 1, MaxTrackOrbits);
            _ascent = ascentEcef;
            _dirty = true;
        }

        static bool Same(in OrbitElements a, in OrbitElements b) =>
            math.abs(a.A - b.A) < 0.01 && math.abs(a.E - b.E) < 1e-6 && math.abs(a.I - b.I) < 1e-6 && math.abs(a.Raan - b.Raan) < 1e-6
            && math.abs(a.ArgP - b.ArgP) < 1e-6 && math.abs(a.M - b.M) < 1e-6;

        void LateUpdate()
        {
            if (_sim == null || _track == null) return;
            // Only over the globe (or flying free): never in a close-up of a satellite or a rocket.
            bool globe = _rig.Mode == ViewMode.Earth || _rig.Mode == ViewMode.Free;
            bool visible = Time.frameCount - _shownFrame <= 2 && _orbits.Count > 0 && globe && !Cinematic.Active && !Cinematic.SatellitesHidden;
            Cinematic.PreviewHidesSatellites = visible && HideOthers;
            if (!visible)
            {
                if (_track.enabled)
                {
                    _track.enabled = false;
                    foreach (var ring in _rings) ring.enabled = false;
                }
                return;
            }

            _timer -= Time.unscaledDeltaTime;
            if (_dirty || _timer <= 0) Rebuild();

            double jd = _sim.Clock.JulianDate;
            Vector3 camera = _rig.Far.transform.position;
            // The ground track is Earth-fixed: it turns with the planet. The orbits stay put in space.
            for (int k = 0; k < _trackCount; k++) _trackPositions[k] = WorldFrame.ToFar(Astro.EcefToEci(_trackEcef[k], jd));
            _track.positionCount = _trackCount;
            _track.SetPositions(_trackPositions);
            float trackDistance = Vector3.Distance(camera, _trackPositions[_trackCount / 2]);
            _track.widthMultiplier = Mathf.Clamp(trackDistance * 0.0035f, 1e-6f, 0.12f);
            _track.enabled = true;

            for (int i = 0; i < _rings.Count; i++)
            {
                var ring = _rings[i];
                ring.enabled = i < _ringCount;
                if (i >= _ringCount) continue;
                for (int k = 0; k < RingSegments; k++) _ringPositions[k] = WorldFrame.ToFar(_ringEci[i][k]);
                ring.SetPositions(_ringPositions);
                float distance = Vector3.Distance(camera, _ringPositions[0]);
                ring.widthMultiplier = Mathf.Clamp(distance * 0.0025f, 1e-7f, 0.5f);
            }
        }

        void Rebuild()
        {
            _dirty = false;
            _timer = 0.5f;
            var first = _orbits[0];

            _trackCount = 0;
            if (_ascent != null && _ascent.Length > 1)
            {
                int count = math.min(_ascent.Length, AscentPoints);
                for (int k = 0; k < count; k++)
                {
                    // Lifted just clear of the ground at the pad; the rocket's real height further on.
                    double3 p = _ascent[(int)((long)k * (_ascent.Length - 1) / math.max(count - 1, 1))];
                    _trackEcef[_trackCount++] = math.normalize(p) * math.max(math.length(p), EarthConstants.RadiusKm + LiftKm);
                }
            }
            double period = first.PeriodSeconds;
            int points = _trackOrbits * PointsPerOrbit;
            for (int k = 0; k < points; k++)
                _trackEcef[_trackCount + k] = LaunchPreview.GroundPoint(first, _epochJd, period * _trackOrbits * k / (points - 1.0), LiftKm);
            _trackCount += points;

            _ringCount = math.min(_orbits.Count, MaxPlanes);
            for (int i = 0; i < _ringCount; i++)
            {
                var el = _orbits[i];
                for (int k = 0; k < RingSegments; k++)
                {
                    var sample = el;
                    sample.M = el.M + MathConst.TwoPi * k / RingSegments;
                    OrbitMath.ToStateVector(sample, out double3 r, out _);
                    _ringEci[i][k] = r;
                }
            }
        }
    }
}
