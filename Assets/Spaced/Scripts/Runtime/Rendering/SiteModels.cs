using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Procedural launch complexes and the droneship, in metres. Pad frame: origin on top of the launch mount
    /// (where the rocket stands), +Y up, +Z north, +X east. The ground is <see cref="GroundLevel"/> below.
    /// </summary>
    public static class SiteModels
    {
        public const float GroundLevel = -4f;

        public static SurfaceMesh Pad(PadStyle style)
        {
            var b = new MeshBuilder();
            float g = GroundLevel;

            // Concrete hardstand, launch mount and the flame trench running off the pad.
            b.Box(new Vector3(0, g - 0.5f, 0), new Vector3(140f, 1f, 140f), Surface.Concrete, 16f);
            foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
                b.Box(new Vector3(corner.x * 3.6f, g * 0.5f, corner.y * 3.6f), new Vector3(1.6f, -g, 1.6f), Surface.Steel);
            b.Box(new Vector3(0, -0.4f, 0), new Vector3(9.5f, 0.8f, 9.5f), Surface.Steel);
            b.Box(new Vector3(0, g + 0.05f, 30f), new Vector3(9f, 0.1f, 52f), Surface.BlackComposite);
            b.Box(new Vector3(-5.2f, g + 1.2f, 30f), new Vector3(1.2f, 2.4f, 52f), Surface.Concrete, 4f);
            b.Box(new Vector3(5.2f, g + 1.2f, 30f), new Vector3(1.2f, 2.4f, 52f), Surface.Concrete, 4f);

            // Transporter-erector base and rail (the strongback itself is a separate, moving part).
            b.Box(new Vector3(0, g + 0.4f, -22f), new Vector3(10f, 0.8f, 30f), Surface.Steel);

            switch (style)
            {
                case PadStyle.Slc40:
                    LatticeTower(b, new Vector3(-22f, g, -6f), 9f, 80f, 13);
                    CrewArm(b, new Vector3(-17.5f, g + 62f, -6f), 15f);
                    LightningMasts(b, 95f, 120f);
                    break;
                case PadStyle.Lc39A:
                    LatticeTower(b, new Vector3(-24f, g, 0f), 12f, 82f, 14);
                    CrewArm(b, new Vector3(-18f, g + 64f, 0f), 16f);
                    b.Cylinder(new Vector3(-24f, g + 82f, 0f), new Vector3(-24f, g + 110f, 0f), 0.6f, 0.15f, 8, Surface.Steel); // lightning mast on top
                    Light(b, new Vector3(-24f, g + 110.5f, 0f));
                    break;
                case PadStyle.Slc4E:
                    LightningMasts(b, 80f, 100f);
                    break;
            }

            // Propellant farm and the sound-suppression water tower.
            b.Lathe(new Vector3(-140f, g, 70f), Vector3.up, Sphere(9f, 12, 9f), 24, Surface.WhitePaint);               // LOX sphere
            b.Cylinder(new Vector3(-150f, g + 4f, -40f), new Vector3(-110f, g + 4f, -40f), 3.5f, 3.5f, 20, Surface.WhitePaint); // RP-1
            b.Cylinder(new Vector3(-150f, g + 4f, -52f), new Vector3(-110f, g + 4f, -52f), 3.5f, 3.5f, 20, Surface.WhitePaint);
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + Mathf.PI * 0.25f;
                var leg = new Vector3(160f + Mathf.Cos(a) * 6f, g, -120f + Mathf.Sin(a) * 6f);
                b.Cylinder(leg, new Vector3(160f, g + 70f, -120f), 0.5f, 0.5f, 6, Surface.Steel, false);
            }
            b.Lathe(new Vector3(160f, g + 70f, -120f), Vector3.up, Sphere(7f, 10, 7f), 20, Surface.WhitePaint);
            b.Box(new Vector3(-130f, g + 3f, 0f), new Vector3(30f, 6f, 14f), Surface.Radiator, 4f); // pad support building
            return b.Build($"Pad {style}");
        }

        /// <summary>
        /// Transporter-erector ("strongback"): lattice columns with clamp arms, pivoting at its base on the
        /// south side of the rocket. Built in its own frame with the hinge at the origin.
        /// </summary>
        public static SurfaceMesh Strongback()
        {
            var b = new MeshBuilder();
            float height = 66f;
            foreach (float x in new[] { -1.6f, 1.6f })
            {
                b.Box(new Vector3(x, height * 0.5f, -3.4f), new Vector3(0.7f, height, 0.7f), Surface.Steel);
                b.Box(new Vector3(x, height * 0.5f, -6.2f), new Vector3(0.7f, height, 0.7f), Surface.Steel);
            }
            for (float y = 3f; y < height; y += 4.5f)
            {
                b.Box(new Vector3(0, y, -3.4f), new Vector3(3.9f, 0.3f, 0.3f), Surface.Steel);
                b.Box(new Vector3(0, y, -6.2f), new Vector3(3.9f, 0.3f, 0.3f), Surface.Steel);
                b.Box(new Vector3(-1.6f, y + 2.25f, -4.8f), new Vector3(0.25f, 0.25f, 5.2f), Quaternion.Euler(57f, 0, 0), Surface.Steel);
                b.Box(new Vector3(1.6f, y + 2.25f, -4.8f), new Vector3(0.25f, 0.25f, 5.2f), Quaternion.Euler(-57f, 0, 0), Surface.Steel);
            }
            foreach (float y in new[] { 20f, 40f, 58f })
                b.Box(new Vector3(0, y, -2.4f), new Vector3(2.5f, 0.6f, 1.6f), Surface.Steel); // clamp arms
            b.Box(new Vector3(0, 0.5f, -4.8f), new Vector3(5f, 1f, 4f), Surface.Steel);
            return b.Build("Strongback");
        }

        /// <summary>Autonomous spaceport droneship: 91 × 52 m deck, blast walls aft, four thruster pods.</summary>
        public static SurfaceMesh Droneship()
        {
            var b = new MeshBuilder();
            b.Box(new Vector3(0, -0.4f, 0), new Vector3(52f, 0.8f, 91f), Surface.DeckSteel);
            b.Box(new Vector3(0, -4.5f, 0), new Vector3(52f, 7.4f, 91f), Surface.HullPaint, 6f);
            b.Box(new Vector3(0, 2.5f, -43.5f), new Vector3(52f, 6f, 1.2f), Surface.Radiator, 3f);
            b.Box(new Vector3(-25f, 2f, -36f), new Vector3(1.2f, 5f, 16f), Surface.Radiator, 3f);
            b.Box(new Vector3(25f, 2f, -36f), new Vector3(1.2f, 5f, 16f), Surface.Radiator, 3f);
            b.Box(new Vector3(-18f, 3f, -38f), new Vector3(6f, 6f, 6f), Surface.WhitePaint); // equipment houses
            b.Box(new Vector3(18f, 3f, -38f), new Vector3(6f, 6f, 6f), Surface.WhitePaint);
            foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
            {
                b.Cylinder(new Vector3(corner.x * 21f, 0.0f, corner.y * 40f), new Vector3(corner.x * 21f, 2.5f, corner.y * 40f), 2.5f, 2.5f, 16, Surface.HullPaint);
                Light(b, new Vector3(corner.x * 25.5f, 0.3f, corner.y * 45f));
            }
            return b.Build("Droneship");
        }

        /// <summary>Ground disc that follows the Earth's curvature, for the ground or ocean shader (one submesh).</summary>
        public static Mesh GroundDisc(float radius, int rings, int segments, float yOffset)
        {
            var vertices = new Vector3[(rings + 1) * segments + 1];
            var uvs = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];
            float earthRadius = (float)(EarthConstants.RadiusKm * 1000.0);
            vertices[0] = new Vector3(0, yOffset, 0);
            normals[0] = Vector3.up;
            for (int r = 0; r <= rings; r++)
            {
                // Rings get denser towards the centre, where the camera usually is.
                float d = radius * Mathf.Pow((r + 1f) / (rings + 1f), 2.2f);
                for (int s = 0; s < segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    int i = 1 + r * segments + s;
                    vertices[i] = new Vector3(Mathf.Cos(a) * d, yOffset - d * d / (2f * earthRadius), Mathf.Sin(a) * d);
                    normals[i] = new Vector3(vertices[i].x, earthRadius, vertices[i].z).normalized;
                    uvs[i] = new Vector2(vertices[i].x, vertices[i].z);
                }
            }

            var triangles = new System.Collections.Generic.List<int>();
            for (int s = 0; s < segments; s++)
            {
                triangles.Add(0);
                triangles.Add(1 + (s + 1) % segments);
                triangles.Add(1 + s);
            }
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = 1 + r * segments + s, b = 1 + r * segments + (s + 1) % segments;
                    int c = a + segments, d = b + segments;
                    triangles.Add(a); triangles.Add(b); triangles.Add(d);
                    triangles.Add(a); triangles.Add(d); triangles.Add(c);
                }
            }

            var mesh = new Mesh { name = "Ground Disc", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(new Vector3(0, yOffset, 0), new Vector3(radius * 2f, radius * 0.5f, radius * 2f));
            return mesh;
        }

        static void LatticeTower(MeshBuilder b, Vector3 baseCenter, float width, float height, int levels)
        {
            float h = width * 0.5f;
            var corners = new[] { new Vector3(h, 0, h), new Vector3(h, 0, -h), new Vector3(-h, 0, -h), new Vector3(-h, 0, h) };
            foreach (var c in corners)
                b.Box(baseCenter + c + Vector3.up * height * 0.5f, new Vector3(0.8f, height, 0.8f), Surface.Steel);
            float step = height / levels;
            for (int l = 1; l <= levels; l++)
            {
                float y = l * step;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 a = corners[i], c = corners[(i + 1) % 4];
                    Vector3 mid = (a + c) * 0.5f;
                    Vector3 along = (c - a).normalized;
                    b.Box(baseCenter + mid + Vector3.up * y, new Vector3(0.4f, 0.4f, width), Quaternion.LookRotation(along), Surface.Steel);
                    // Diagonal brace across the face below this level.
                    Vector3 from = baseCenter + a + Vector3.up * (y - step), to = baseCenter + c + Vector3.up * y;
                    b.Box((from + to) * 0.5f, new Vector3(0.25f, 0.25f, Vector3.Distance(from, to)), Quaternion.LookRotation(to - from), Surface.Steel);
                }
                if (l % 3 == 0) b.Box(baseCenter + Vector3.up * y, new Vector3(width, 0.3f, width), Surface.Steel, 2f); // decks
            }
            b.Box(baseCenter + Vector3.up * 4f, new Vector3(width * 0.6f, 8f, width * 0.6f), Surface.Radiator, 3f); // base building
            Light(b, baseCenter + Vector3.up * (height + 0.5f));
        }

        static void CrewArm(MeshBuilder b, Vector3 root, float length)
        {
            b.Box(root + new Vector3(length * 0.5f, 0, 0), new Vector3(length, 2.6f, 2.4f), Surface.Steel);
            b.Box(root + new Vector3(length * 0.5f, 0, 0), new Vector3(length * 0.9f, 2.2f, 2.5f), Surface.Glass);
            b.Box(root + new Vector3(length + 0.8f, 0, 0), new Vector3(1.6f, 3.4f, 3.4f), Surface.WhitePaint); // white room
        }

        static void LightningMasts(MeshBuilder b, float spacing, float height)
        {
            foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
            {
                var basePoint = new Vector3(corner.x * spacing, GroundLevel, corner.y * spacing);
                b.Cylinder(basePoint, basePoint + Vector3.up * height, 1.4f, 0.35f, 10, Surface.Steel);
                Light(b, basePoint + Vector3.up * (height + 0.4f));
            }
            // Catenary wires between the mast tops.
            for (int i = 0; i < 4; i++)
            {
                var c1 = new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, -1), new Vector2(-1, 1) }[i];
                var c2 = new[] { new Vector2(1, -1), new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, 1) }[i];
                var a = new Vector3(c1.x * spacing, GroundLevel + height, c1.y * spacing);
                var c = new Vector3(c2.x * spacing, GroundLevel + height, c2.y * spacing);
                var sag = (a + c) * 0.5f + Vector3.down * height * 0.12f;
                b.Cylinder(a, sag, 0.06f, 0.06f, 4, Surface.Steel, false);
                b.Cylinder(sag, c, 0.06f, 0.06f, 4, Surface.Steel, false);
            }
        }

        static void Light(MeshBuilder b, Vector3 position) => b.Box(position, Vector3.one * 0.8f, Surface.WarningLight);

        /// <summary>Lathe profile of a sphere of the given radius resting on the origin.</summary>
        static Vector2[] Sphere(float radius, int steps, float lift)
        {
            var profile = new Vector2[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.PI * i / steps - Mathf.PI * 0.5f;
                profile[i] = new Vector2(lift + Mathf.Sin(a) * radius, Mathf.Cos(a) * radius);
            }
            return profile;
        }
    }
}
