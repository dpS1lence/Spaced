using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// Replaces a procedural model with your own mesh, e.g. from the Asset Store or NASA 3D Resources.
    /// Model axes: +Y away from Earth, +Z along the velocity; use <see cref="Rotation"/> and <see cref="Scale"/>
    /// to line an imported mesh up. Materials need "Enable GPU Instancing" (it is switched on at startup).
    /// </summary>
    [System.Serializable]
    public sealed class SatelliteModelOverride
    {
        public ModelKind Kind;
        public Mesh Mesh;
        public Material[] Materials;
        public Vector3 Rotation;
        public float Scale = 1f;
    }

    /// <summary>
    /// Draws real-scale 3D models (GPU-instanced, local scene) for every object near the camera.
    /// Satellites point at the Earth and turn their arrays to the Sun; debris and spent stages tumble.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class SatelliteModelRenderer : MonoBehaviour
    {
        public double RangeKm = 250;
        public int MaxModels = 400;

        SpaceSimulation _sim;
        CameraRig _rig;
        MaterialLibrary _materials;
        NativeList<int> _nearby;
        readonly Dictionary<ModelKind, List<Matrix4x4>> _bodies = new Dictionary<ModelKind, List<Matrix4x4>>();
        readonly Dictionary<ModelKind, List<Matrix4x4>> _arrays = new Dictionary<ModelKind, List<Matrix4x4>>();
        readonly List<(double distance, int index)> _sorted = new List<(double, int)>();
        readonly Dictionary<ModelKind, SatelliteModelOverride> _overrides = new Dictionary<ModelKind, SatelliteModelOverride>();
        readonly Dictionary<ModelKind, List<Matrix4x4>> _custom = new Dictionary<ModelKind, List<Matrix4x4>>();

        public void Init(SpaceSimulation sim, CameraRig rig, MaterialLibrary materials, IEnumerable<SatelliteModelOverride> overrides = null)
        {
            if (overrides != null)
            {
                foreach (var o in overrides)
                {
                    if (o?.Mesh == null || o.Materials == null || o.Materials.Length == 0) continue;
                    foreach (var m in o.Materials) if (m != null) m.enableInstancing = true;
                    _overrides[o.Kind] = o;
                }
            }
            _sim = sim;
            _rig = rig;
            _materials = materials;
            _nearby = new NativeList<int>(256, Allocator.Persistent);
        }

        void OnDestroy()
        {
            if (_nearby.IsCreated) _nearby.Dispose();
        }

        void LateUpdate()
        {
            if (_sim == null || _sim.Catalog == null || _rig == null) return;
            var catalog = _sim.Catalog;
            if (catalog.Count == 0) return;

            _nearby.Clear();
            new NearbyJob
            {
                CameraKm = _rig.CameraEciKm,
                RangeSq = RangeKm * RangeKm,
                Positions = catalog.PositionsKm.AsArray(),
                Physics = catalog.Physics.AsArray(),
                Result = _nearby,
                Limit = 8192,
            }.Run();
            if (_nearby.Length == 0) return;

            _sorted.Clear();
            foreach (int index in _nearby)
                _sorted.Add((math.distancesq(catalog.PositionsKm[index], _rig.CameraEciKm), index));
            if (_sorted.Count > MaxModels) _sorted.Sort((a, b) => a.distance.CompareTo(b.distance));

            foreach (var list in _bodies.Values) list.Clear();
            foreach (var list in _arrays.Values) list.Clear();
            foreach (var list in _custom.Values) list.Clear();

            Vector3 sun = Astro.EciToUnity(Astro.SunDirection(_sim.Clock.JulianDate)).normalized;
            float time = (float)((_sim.Clock.JulianDate - Astro.JdJ2000) * MathConst.SecondsPerDay % 100000.0);

            for (int k = 0; k < _sorted.Count && k < MaxModels; k++)
            {
                int index = _sorted[k].index;
                var physics = catalog.Physics[index];
                var model = SpacecraftModels.Get(physics.Model);
                Vector3 position = WorldFrame.ToLocal(catalog.PositionsKm[index]);

                Quaternion rotation;
                Vector3 scale = Vector3.one;
                if (model.Tumbles || physics.Model == ModelKind.RocketBody)
                {
                    uint id = catalog.Ids[index];
                    var rng = new Unity.Mathematics.Random(id * 747796405u + 2891336453u);
                    Vector3 axis = ((Vector3)(float3)rng.NextFloat3Direction()).normalized;
                    float rate = physics.Model == ModelKind.RocketBody ? rng.NextFloat(0.5f, 3f) : rng.NextFloat(20f, 120f);
                    rotation = Quaternion.AngleAxis(time * rate % 360f, axis);
                    if (model.Tumbles) scale = Vector3.one * Mathf.Max(physics.RadiusM * 2f, 0.05f);
                }
                else
                {
                    double3 r = catalog.PositionsKm[index];
                    double3 v = catalog.VelocitiesKmS[index];
                    rotation = WorldFrame.LookRotation(math.normalize(v), math.normalize(r));
                }

                if (_overrides.TryGetValue(physics.Model, out var custom))
                {
                    List(_custom, physics.Model).Add(Matrix4x4.TRS(position, rotation * Quaternion.Euler(custom.Rotation), scale * custom.Scale));
                    continue;
                }

                var matrix = Matrix4x4.TRS(position, rotation, scale);
                List(_bodies, physics.Model).Add(matrix);

                if (model.Arrays != null)
                {
                    Vector3 sunInBody = Quaternion.Inverse(rotation) * sun;
                    float angle = Mathf.Atan2(sunInBody.z, sunInBody.y) * Mathf.Rad2Deg;
                    List(_arrays, physics.Model).Add(Matrix4x4.TRS(position, rotation * Quaternion.AngleAxis(angle, Vector3.right), scale));
                }
            }

            foreach (var pair in _bodies) Draw(SpacecraftModels.Get(pair.Key).Body, pair.Value);
            foreach (var pair in _arrays) Draw(SpacecraftModels.Get(pair.Key).Arrays, pair.Value);
            foreach (var pair in _custom) DrawCustom(_overrides[pair.Key], pair.Value);
        }

        void DrawCustom(SatelliteModelOverride custom, List<Matrix4x4> matrices)
        {
            if (matrices.Count == 0) return;
            for (int sub = 0; sub < custom.Mesh.subMeshCount; sub++)
            {
                var material = custom.Materials[Mathf.Min(sub, custom.Materials.Length - 1)];
                if (material == null) continue;
                var renderParams = new RenderParams(material)
                {
                    layer = WorldFrame.LocalLayer,
                    shadowCastingMode = ShadowCastingMode.Off,
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * (float)(RangeKm * 2500.0)),
                };
                Graphics.RenderMeshInstanced(renderParams, custom.Mesh, sub, matrices);
            }
        }

        void Draw(SurfaceMesh mesh, List<Matrix4x4> matrices)
        {
            if (mesh == null || matrices.Count == 0) return;
            for (int sub = 0; sub < mesh.Surfaces.Length; sub++)
            {
                var renderParams = new RenderParams(_materials[mesh.Surfaces[sub]])
                {
                    layer = WorldFrame.LocalLayer,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * (float)(RangeKm * 2500.0)),
                };
                Graphics.RenderMeshInstanced(renderParams, mesh.Mesh, sub, matrices);
            }
        }

        static List<Matrix4x4> List(Dictionary<ModelKind, List<Matrix4x4>> lists, ModelKind kind)
        {
            if (!lists.TryGetValue(kind, out var list)) lists[kind] = list = new List<Matrix4x4>();
            return list;
        }

        [BurstCompile]
        struct NearbyJob : IJob
        {
            public double3 CameraKm;
            public double RangeSq;
            public int Limit;
            [ReadOnly] public NativeArray<double3> Positions;
            [ReadOnly] public NativeArray<ObjectPhysics> Physics;
            public NativeList<int> Result;

            public void Execute()
            {
                for (int i = 0; i < Positions.Length && Result.Length < Limit; i++)
                    if (Physics[i].IsAlive && math.distancesq(Positions[i], CameraKm) < RangeSq)
                        Result.Add(i);
            }
        }
    }
}
