using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Textures generated at startup, so the project needs no art assets: solar cells, crinkled foil,
    /// sooty booster paint, concrete, droneship deck and smoke puffs. Alpha carries smoothness where used.
    /// </summary>
    public static class ProceduralTextures
    {
        public static Texture2D SolarCells()
        {
            const int size = 256, cells = 8;
            var t = New(size, size, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float cx = x * cells / (float)size, cy = y * cells / (float)size;
                    float fx = cx - Mathf.Floor(cx), fy = cy - Mathf.Floor(cy);
                    bool gap = fx < 0.04f || fy < 0.04f;
                    bool busBar = Mathf.Abs(fx - 0.33f) < 0.012f || Mathf.Abs(fx - 0.66f) < 0.012f;
                    bool finger = (int)(fy * 40) % 4 == 0;
                    float shade = 0.85f + 0.3f * Fbm(Mathf.Floor(cx) * 3.1f, Mathf.Floor(cy) * 1.7f, 1);
                    Color c = gap ? new Color(0.75f, 0.76f, 0.78f, 0.6f)
                        : busBar ? new Color(0.70f, 0.72f, 0.75f, 0.8f)
                        : finger ? new Color(0.10f, 0.14f, 0.32f, 0.9f) * shade
                        : new Color(0.04f, 0.07f, 0.22f, 0.95f) * shade;
                    c.a = gap ? 0.6f : 0.95f;
                    pixels[y * size + x] = c;
                }
            }
            return Finish(t, pixels);
        }

        /// <summary>Normal map of crinkled multi-layer insulation foil (linear, RGB-packed, alpha 1).</summary>
        public static Texture2D FoilNormal()
        {
            const int size = 256;
            var heights = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    heights[y * size + x] = Ridged(x / 32f, y / 32f, 4);
            return HeightToNormal(heights, size, 4f);
        }

        /// <summary>Flight-proven booster paint: white with panel seams, soot thickening towards the engines.</summary>
        public static Texture2D BoosterPaint()
        {
            const int width = 256, height = 1024;
            var t = New(width, height, false);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = y / (float)height; // 0 = engines, 1 = top
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)width;
                    float streak = Fbm(u * 40f, v * 2f, 3);
                    float soot = Mathf.Clamp01((0.75f - v) * 1.6f + (streak - 0.5f) * 0.9f);
                    soot *= Mathf.Clamp01(1.2f - Mathf.Abs(Mathf.Sin(u * Mathf.PI)) * 0.3f); // patchier on one side
                    float seam = (Mathf.Abs(Mathf.Repeat(v * 14f, 1f) - 0.5f) > 0.49f) ? 0.85f : 1f;
                    var paint = new Color(0.86f, 0.86f, 0.84f) * seam;
                    var sooty = new Color(0.14f, 0.12f, 0.11f) * (0.8f + streak * 0.4f);
                    Color c = Color.Lerp(paint, sooty, soot * 0.9f);
                    c.a = Mathf.Lerp(0.75f, 0.25f, soot);
                    pixels[y * width + x] = c;
                }
            }
            return Finish(t, pixels);
        }

        public static Texture2D Concrete()
        {
            const int size = 512;
            var t = New(size, size, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = Fbm(x / 40f, y / 40f, 5);
                    float stain = Mathf.Clamp01((Fbm(x / 150f + 7, y / 150f, 3) - 0.55f) * 3f);
                    bool joint = x % 128 < 2 || y % 128 < 2;
                    var c = new Color(0.58f, 0.57f, 0.53f) * (0.85f + n * 0.3f) * (1f - stain * 0.45f) * (joint ? 0.6f : 1f);
                    c.a = 0.25f;
                    pixels[y * size + x] = c;
                }
            }
            return Finish(t, pixels);
        }

        /// <summary>Droneship deck: scorched steel plates with the landing target circle and X.</summary>
        public static Texture2D DroneshipDeck()
        {
            const int size = 1024;
            var t = New(size, size, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size - 0.5f, v = y / (float)size - 0.5f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float n = Fbm(x / 30f, y / 30f, 4);
                    bool plate = x % 64 < 1 || y % 64 < 1;
                    var c = new Color(0.20f, 0.20f, 0.21f) * (0.85f + n * 0.3f) * (plate ? 0.7f : 1f);
                    // Scorch from past landings.
                    c *= 1f - Mathf.Clamp01(0.25f - r) * 2.4f * (0.6f + n * 0.4f);
                    bool ring = Mathf.Abs(r - 0.32f) < 0.012f;
                    bool cross = (Mathf.Abs(u - v) < 0.02f || Mathf.Abs(u + v) < 0.02f) && r < 0.2f;
                    if (ring) c = new Color(0.92f, 0.78f, 0.12f) * (0.8f + n * 0.2f);
                    if (cross) c = new Color(0.9f, 0.9f, 0.88f) * (0.75f + n * 0.25f);
                    c.a = 0.35f;
                    pixels[y * size + x] = c;
                }
            }
            return Finish(t, pixels);
        }

        /// <summary>Soft, lumpy smoke sprite with alpha.</summary>
        public static Texture2D SmokePuff()
        {
            const int size = 128;
            var t = New(size, size, false);
            t.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (size - 1f) * 2f - 1f, v = y / (size - 1f) * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float n = Fbm(x / 18f, y / 18f, 4);
                    float a = Mathf.Clamp01(1f - r * (1.1f - n * 0.5f));
                    a = a * a * (3f - 2f * a);
                    float shade = 0.75f + n * 0.35f;
                    pixels[y * size + x] = new Color(shade, shade, shade, a);
                }
            }
            return Finish(t, pixels);
        }

        // ───────────────────────────── Noise ─────────────────────────────

        static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        static float ValueNoise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            float a = Mathf.Lerp(Hash(xi, yi), Hash(xi + 1, yi), fx);
            float b = Mathf.Lerp(Hash(xi, yi + 1), Hash(xi + 1, yi + 1), fx);
            return Mathf.Lerp(a, b, fy);
        }

        public static float Fbm(float x, float y, int octaves)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += ValueNoise(x, y) * amplitude;
                total += amplitude;
                x *= 2.03f;
                y *= 2.03f;
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        static float Ridged(float x, float y, int octaves)
        {
            float sum = 0, amplitude = 0.5f;
            for (int o = 0; o < octaves; o++)
            {
                sum += (1f - Mathf.Abs(ValueNoise(x, y) * 2f - 1f)) * amplitude;
                x *= 2.1f;
                y *= 2.1f;
                amplitude *= 0.5f;
            }
            return sum;
        }

        static Texture2D HeightToNormal(float[] heights, int size, float strength)
        {
            var t = New(size, size, true);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = heights[y * size + (x + 1) % size] - heights[y * size + (x + size - 1) % size];
                    float dy = heights[((y + 1) % size) * size + x] - heights[((y + size - 1) % size) * size + x];
                    var n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                    pixels[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }
            return Finish(t, pixels);
        }

        static Texture2D New(int width, int height, bool linear) =>
            new Texture2D(width, height, TextureFormat.RGBA32, true, linear)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };

        static Texture2D Finish(Texture2D texture, Color32[] pixels)
        {
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
