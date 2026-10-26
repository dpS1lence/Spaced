using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// Draws every catalog object (plus rockets and other markers) as a dot in one call. Dots dim in
    /// Earth's shadow and fade out up close, where <see cref="SatelliteModelRenderer"/> shows the 3D model.
    /// </summary>
    public sealed class SatelliteRenderer : MonoBehaviour
    {
        public const int MarkerCategory = 9;

        public float PointRadiusPx = 2.2f;

        public static readonly Color[] CategoryColors =
        {
            new Color(0.55f, 0.85f, 1.00f), // Payload
            new Color(0.85f, 0.90f, 1.00f), // Starlink
            new Color(0.75f, 0.55f, 1.00f), // OneWeb
            new Color(1.00f, 0.80f, 0.30f), // Station
            new Color(0.45f, 1.00f, 0.55f), // Navigation
            new Color(1.00f, 0.60f, 0.25f), // Rocket body
            new Color(0.95f, 0.35f, 0.30f), // Debris
            new Color(0.30f, 1.00f, 0.75f), // User satellite
            new Color(1.00f, 0.25f, 0.60f), // Generated debris
            new Color(1.00f, 0.95f, 0.70f), // Rockets in flight
        };

        public static readonly string[] CategoryNames =
        {
            "Payloads", "Starlink", "OneWeb", "Stations", "Navigation", "Rocket bodies", "Debris (catalog)",
            "Your satellites", "New debris",
        };

        /// <summary>Per-category visibility; hidden categories are neither drawn nor pickable.</summary>
        public readonly bool[] CategoryVisible = { true, true, true, true, true, true, true, true, true, true };

        /// <summary>Extra points this frame (inertial km), e.g. rockets in flight. Cleared after drawing.</summary>
        public readonly List<double3> Markers = new List<double3>();

        static readonly int ProjectionScale = Shader.PropertyToID("_ProjScale");

        SpaceSimulation _sim;
        CameraRig _rig;
        Material _material;
        GraphicsBuffer _buffer;
        NativeArray<float4> _markerData;
        readonly Vector4[] _palette = new Vector4[16];

        public void Init(SpaceSimulation sim, CameraRig rig, Shader shader)
        {
            _sim = sim;
            _rig = rig;
            _material = new Material(shader) { name = "Satellites" };
            _markerData = new NativeArray<float4>(64, Allocator.Persistent);
        }

        void LateUpdate()
        {
            if (_sim == null || _sim.Catalog == null || _material == null) return;
            var catalog = _sim.Catalog;
            int count = catalog.Count;
            if (_material == null) return;

            if (count > 0)
            {
                new RenderDataJob
                {
                    OriginKm = WorldFrame.OriginKm,
                    PositionsKm = catalog.PositionsKm.AsArray(),
                    Physics = catalog.Physics.AsArray(),
                    RenderData = catalog.RenderData.AsArray(),
                }.Schedule(count, 1024).Complete();
            }

            int markers = Math.Min(Markers.Count, _markerData.Length);
            for (int i = 0; i < markers; i++)
            {
                Vector3 p = WorldFrame.ToFar(Markers[i]);
                _markerData[i] = new float4(p.x, p.y, p.z, MarkerCategory + 0.05f);
            }
            Markers.Clear();

            int total = count + markers;
            if (total == 0 || Cinematic.Active) return;

            if (_buffer == null || _buffer.count < total)
            {
                _buffer?.Release();
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Math.Max(total, 1024)), 16);
            }
            if (count > 0) _buffer.SetData(catalog.RenderData.AsArray(), 0, 0, count);
            if (markers > 0) _buffer.SetData(_markerData, 0, count, markers);

            for (int i = 0; i < CategoryColors.Length; i++)
            {
                Color c = CategoryColors[i].linear;
                _palette[i] = new Vector4(c.r, c.g, c.b, CategoryVisible[i] ? 1f : 0f);
            }

            float dpiScale = Mathf.Max(1f, Screen.height / 1080f);
            var cam = _rig.Far;
            _material.SetBuffer(ShaderIds.Positions, _buffer);
            _material.SetVectorArray(ShaderIds.Palette, _palette);
            _material.SetFloat(ShaderIds.PointSize, PointRadiusPx * dpiScale);
            _material.SetInt(ShaderIds.SelectedIndex, _sim.SelectedIndex);
            _material.SetFloat(ProjectionScale, 0.5f * Screen.height / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));

            var renderParams = new RenderParams(_material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 5000f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = 0,
            };
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, total);
        }

        void OnDestroy()
        {
            _buffer?.Release();
            _buffer = null;
            if (_markerData.IsCreated) _markerData.Dispose();
        }
    }
}
