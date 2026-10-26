using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>Surface types shared by every procedural model. Each becomes one material and one submesh.</summary>
    public enum Surface
    {
        WhitePaint,
        BoosterPaint,
        BlackComposite,
        Aluminium,
        Titanium,
        EngineBell,
        GoldFoil,
        SilverFoil,
        SolarCells,
        DarkPanel,
        Radiator,
        Concrete,
        Steel,
        DeckSteel,
        HullPaint,
        Glass,
        WarningLight,
        /// <summary>Starship's 304L stainless steel.</summary>
        Stainless,
        /// <summary>Starship's black hexagonal heat-shield tiles.</summary>
        HeatTile,
        /// <summary>Lunar regolith (landing sites).</summary>
        Regolith,
    }

    /// <summary>A mesh whose submeshes map to <see cref="Surface"/>s.</summary>
    public sealed class SurfaceMesh
    {
        public Mesh Mesh;
        public Surface[] Surfaces;
    }

    /// <summary>Small procedural modelling kit: boxes, frustums, lathe profiles, dishes and panels, in metres.</summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly List<Vector2> _uvs = new List<Vector2>();
        readonly Dictionary<Surface, List<int>> _triangles = new Dictionary<Surface, List<int>>();

        List<int> Tris(Surface surface)
        {
            if (!_triangles.TryGetValue(surface, out var list)) _triangles[surface] = list = new List<int>();
            return list;
        }

        int Vertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            _vertices.Add(position);
            _normals.Add(normal);
            _uvs.Add(uv);
            return _vertices.Count - 1;
        }

        /// <summary>Quad with corners a, b, c, d (clockwise seen from the front, Unity's left-handed convention).</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Surface surface, Vector2 uvScale)
        {
            Vector3 normal = Vector3.Cross(d - a, b - a).normalized;
            int i0 = Vertex(a, normal, new Vector2(0, 0));
            int i1 = Vertex(b, normal, new Vector2(0, uvScale.y));
            int i2 = Vertex(c, normal, new Vector2(uvScale.x, uvScale.y));
            int i3 = Vertex(d, normal, new Vector2(uvScale.x, 0));
            var t = Tris(surface);
            t.Add(i0); t.Add(i1); t.Add(i2);
            t.Add(i0); t.Add(i2); t.Add(i3);
        }

        /// <summary>Axis-aligned (then rotated) box. UVs repeat once per <paramref name="uvMetres"/>.</summary>
        public void Box(Vector3 center, Vector3 size, Quaternion rotation, Surface surface, float uvMetres = 0)
        {
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + rotation * new Vector3(x * h.x, y * h.y, z * h.z);
            Vector2 S(float u, float v) => uvMetres > 0 ? new Vector2(u / uvMetres, v / uvMetres) : Vector2.one;
            Quad(P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1), P(1, -1, 1), surface, S(size.x, size.y));     // +Z
            Quad(P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), P(-1, -1, -1), surface, S(size.x, size.y)); // -Z
            Quad(P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), P(1, -1, -1), surface, S(size.z, size.y));     // +X
            Quad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), surface, S(size.z, size.y)); // -X
            Quad(P(-1, 1, 1), P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), surface, S(size.x, size.z));     // +Y
            Quad(P(-1, -1, -1), P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1), surface, S(size.x, size.z)); // -Y
        }

        public void Box(Vector3 center, Vector3 size, Surface surface, float uvMetres = 0) =>
            Box(center, size, Quaternion.identity, surface, uvMetres);

        /// <summary>
        /// Surface of revolution along <paramref name="axis"/> from <paramref name="origin"/>.
        /// <paramref name="profile"/> holds (distance along axis, radius) pairs. V runs along the profile.
        /// </summary>
        public void Lathe(Vector3 origin, Vector3 axis, IList<Vector2> profile, int segments, Surface surface,
            bool capStart = false, bool capEnd = false, float uRepeat = 1f, float arcDegrees = 360f, float arcStartDegrees = 0f)
        {
            axis.Normalize();
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 side2 = Vector3.Cross(axis, side);
            var t = Tris(surface);

            float totalLength = 0;
            for (int p = 1; p < profile.Count; p++) totalLength += Vector2.Distance(profile[p], profile[p - 1]);

            int ringStart = _vertices.Count;
            float travelled = 0;
            for (int p = 0; p < profile.Count; p++)
            {
                if (p > 0) travelled += Vector2.Distance(profile[p], profile[p - 1]);
                // Profile slope for the normal.
                Vector2 prev = profile[Mathf.Max(p - 1, 0)], next = profile[Mathf.Min(p + 1, profile.Count - 1)];
                Vector2 tangent = (next - prev).normalized;
                for (int s = 0; s <= segments; s++)
                {
                    float angle = (arcStartDegrees + arcDegrees * s / segments) * Mathf.Deg2Rad;
                    Vector3 radial = side * Mathf.Cos(angle) + side2 * Mathf.Sin(angle);
                    Vector3 position = origin + axis * profile[p].x + radial * profile[p].y;
                    Vector3 normal = (radial * tangent.x - axis * tangent.y).normalized;
                    Vertex(position, normal, new Vector2(uRepeat * s / segments, totalLength > 0 ? travelled / totalLength : 0));
                }
            }

            int columns = segments + 1;
            for (int p = 0; p < profile.Count - 1; p++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = ringStart + p * columns + s, b = a + 1, c = a + columns, d = c + 1;
                    t.Add(a); t.Add(c); t.Add(d);
                    t.Add(a); t.Add(d); t.Add(b);
                }
            }

            if (capStart) Cap(origin + axis * profile[0].x, -axis, side, side2, profile[0].y, segments, surface);
            if (capEnd) Cap(origin + axis * profile[profile.Count - 1].x, axis, side, side2, profile[profile.Count - 1].y, segments, surface);
        }

        void Cap(Vector3 center, Vector3 normal, Vector3 side, Vector3 side2, float radius, int segments, Surface surface)
        {
            if (radius <= 0) return;
            var t = Tris(surface);
            int c = Vertex(center, normal, new Vector2(0.5f, 0.5f));
            int first = _vertices.Count;
            for (int s = 0; s <= segments; s++)
            {
                float angle = 2f * Mathf.PI * s / segments;
                Vector3 radial = side * Mathf.Cos(angle) + side2 * Mathf.Sin(angle);
                Vertex(center + radial * radius, normal, new Vector2(0.5f + 0.5f * Mathf.Cos(angle), 0.5f + 0.5f * Mathf.Sin(angle)));
            }
            for (int s = 0; s < segments; s++)
            {
                t.Add(c); t.Add(first + s); t.Add(first + s + 1);
            }
        }

        /// <summary>Straight cylinder (or frustum) between two points.</summary>
        public void Cylinder(Vector3 from, Vector3 to, float radiusFrom, float radiusTo, int segments, Surface surface,
            bool caps = true, float uRepeat = 1f)
        {
            float length = Vector3.Distance(from, to);
            Lathe(from, to - from, new[] { new Vector2(0, radiusFrom), new Vector2(length, radiusTo) }, segments, surface, caps, caps, uRepeat);
        }

        /// <summary>Parabolic dish of the given aperture radius and depth, opening along <paramref name="axis"/>.</summary>
        public void Dish(Vector3 vertex, Vector3 axis, float radius, float depth, int segments, Surface surface)
        {
            var profile = new List<Vector2>();
            for (int i = 0; i <= 6; i++)
            {
                float r = radius * i / 6f;
                profile.Add(new Vector2(depth * (r * r) / (radius * radius), r));
            }
            Lathe(vertex, axis, profile, segments, surface);
        }

        /// <summary>Thin rectangular panel (e.g. a solar array) centred on <paramref name="center"/>.</summary>
        public void Panel(Vector3 center, Vector2 size, float thickness, Quaternion rotation, Surface front, Surface back, float uvMetres)
        {
            Vector3 hx = rotation * new Vector3(size.x * 0.5f, 0, 0);
            Vector3 hz = rotation * new Vector3(0, 0, size.y * 0.5f);
            Vector3 up = rotation * new Vector3(0, thickness * 0.5f, 0);
            var uv = new Vector2(size.x / uvMetres, size.y / uvMetres);
            Quad(center + up - hx - hz, center + up + hx - hz, center + up + hx + hz, center + up - hx + hz, front, uv);
            Quad(center - up - hx - hz, center - up - hx + hz, center - up + hx + hz, center - up + hx - hz, back, uv);
            Box(center, new Vector3(size.x, thickness * 0.9f, size.y), rotation, back, 0);
        }

        public SurfaceMesh Build(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            var surfaces = new List<Surface>(_triangles.Keys);
            mesh.subMeshCount = surfaces.Count;
            for (int i = 0; i < surfaces.Count; i++) mesh.SetTriangles(_triangles[surfaces[i]], i);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return new SurfaceMesh { Mesh = mesh, Surfaces = surfaces.ToArray() };
        }
    }
}
