using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>Random background stars, fixed in the inertial frame, and the Sun.</summary>
    public static class Starfield
    {
        /// <summary>A single camera-facing quad; the shader places it in the Sun's direction.</summary>
        public static GameObject CreateSun(Shader shader)
        {
            var mesh = new Mesh { name = "Sun" };
            mesh.vertices = new Vector3[4];
            mesh.uv = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

            var go = new GameObject("Sun");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = new Material(shader) { name = "Sun" };
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go;
        }

        public static GameObject Create(Shader shader, int starCount = 6000, int seed = 7)
        {
            var random = new System.Random(seed);
            var vertices = new Vector3[starCount * 4];
            var corners = new Vector2[starCount * 4];
            var colors = new Color[starCount * 4];
            var indices = new int[starCount * 6];
            Vector2[] quad = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };

            for (int s = 0; s < starCount; s++)
            {
                // Uniform direction on the sphere.
                float z = (float)(random.NextDouble() * 2 - 1);
                float phi = (float)(random.NextDouble() * Mathf.PI * 2);
                float r = Mathf.Sqrt(1 - z * z);
                var direction = new Vector3(r * Mathf.Cos(phi), z, r * Mathf.Sin(phi));

                // Few bright stars, many faint ones; slight blue-white-orange spread.
                float brightness = Mathf.Pow((float)random.NextDouble(), 4f) * 0.9f + 0.1f;
                float temperature = (float)random.NextDouble();
                var tint = Color.Lerp(new Color(1f, 0.82f, 0.65f), new Color(0.7f, 0.82f, 1f), temperature);
                var color = new Color(tint.r, tint.g, tint.b, brightness);

                for (int c = 0; c < 4; c++)
                {
                    vertices[s * 4 + c] = direction;
                    corners[s * 4 + c] = quad[c];
                    colors[s * 4 + c] = color;
                }

                int v = s * 4, i = s * 6;
                indices[i] = v; indices[i + 1] = v + 2; indices[i + 2] = v + 1;
                indices[i + 3] = v; indices[i + 4] = v + 3; indices[i + 5] = v + 2;
            }

            var mesh = new Mesh { name = "Stars", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.uv = corners;
            mesh.colors = colors;
            mesh.triangles = indices;
            // Stars are drawn relative to the camera, so never let them be culled.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

            var go = new GameObject("Stars");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = new Material(shader) { name = "Stars" };
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go;
        }
    }
}
