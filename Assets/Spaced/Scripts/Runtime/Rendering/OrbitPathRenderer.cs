using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>Draws one full revolution of the selected object's current orbit.</summary>
    [RequireComponent(typeof(LineRenderer))]
    public sealed class OrbitPathRenderer : MonoBehaviour
    {
        const int Segments = 256;

        SpaceSimulation _sim;
        Camera _camera;
        LineRenderer _line;
        readonly Vector3[] _points = new Vector3[Segments];

        public void Init(SpaceSimulation sim, Shader shader, Camera viewCamera)
        {
            _sim = sim;
            _camera = viewCamera;
            _line = GetComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.loop = true;
            _line.positionCount = Segments;
            _line.numCapVertices = 0;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.material = new Material(shader) { name = "Orbit Path" };
            _line.startColor = _line.endColor = new Color(1f, 0.85f, 0.3f, 0.65f);
            _line.enabled = false;
        }

        void LateUpdate()
        {
            if (_sim == null || _line == null) return;
            int index = Cinematic.Active || Cinematic.SatellitesHidden ? -1 : _sim.SelectedIndex;
            _line.enabled = index >= 0;
            if (index < 0) return;

            var elements = _sim.Catalog.Elements[index];
            for (int k = 0; k < Segments; k++)
            {
                var sample = elements;
                sample.M = elements.M + MathConst.TwoPi * k / Segments;
                OrbitMath.ToStateVector(sample, out double3 r, out _);
                _points[k] = WorldFrame.ToFar(r);
            }
            _line.SetPositions(_points);

            Vector3 anchor = _points[0];
            float distance = _camera != null ? Vector3.Distance(_camera.transform.position, anchor) : 20f;
            _line.widthMultiplier = Mathf.Clamp(distance * 0.0025f, 1e-7f, 0.5f);
        }
    }
}
