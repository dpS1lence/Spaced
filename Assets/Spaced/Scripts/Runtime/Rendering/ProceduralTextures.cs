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

        /// <summary>
        /// Starship stainless steel: brushed rings welded every ~1.8 m with darker weld lines, faint heat tint and
        /// streaks. Alpha carries smoothness (welds and streaks are duller).
        /// </summary>
        public static Texture2D Stainless()
        {
            const int width = 256, height = 1024, rings = 38;
            var t = New(width, height, false);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = y / (float)height;
                float ring = v * rings;
                float weld = Mathf.Clamp01(1f - Mathf.Abs(ring - Mathf.Round(ring)) * 90f);
                float ringShade = 0.94f + 0.08f * Hash(Mathf.FloorToInt(ring), 7);
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)width;
                    float brush = Fbm(u * 2f, v * 400f, 2);
                    float streak = Fbm(u * 30f, v * 3f, 3);
                    float tint = Mathf.Clamp01((0.25f - v) * 2.5f) * (0.6f + streak * 0.6f); // heat/soot towards the engines
                    var c = new Color(0.78f, 0.77f, 0.75f) * ringShade * (0.93f + brush * 0.12f);
                    c = Color.Lerp(c, new Color(0.45f, 0.38f, 0.30f), tint * 0.6f);
                    c *= 1f - weld * 0.35f;
                    c.a = Mathf.Lerp(0.72f, 0.4f, Mathf.Max(weld, tint * 0.7f + (streak - 0.5f) * 0.3f));
                    pixels[y * width + x] = c;
                }
            }
            return Finish(t, pixels);
        }

        /// <summary>Starship's black hexagonal heat-shield tiles with fine gaps and the odd lighter, newer tile.</summary>
        public static Texture2D HeatTiles()
        {
            const int size = 512;
            const float tilesAcross = 16f;
            var t = New(size, size, false);
            var pixels = new Color32[size * size];
            float h = Mathf.Sqrt(3f) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x / (float)size * tilesAcross, py = y / (float)size * tilesAcross / h;
                    // Nearest hex centre on a staggered grid.
                    int row = Mathf.FloorToInt(py);
                    float best = float.MaxValue, second = float.MaxValue;
                    int bestId = 0;
                    for (int r = row - 1; r <= row + 1; r++)
                    {
                        float offset = (r & 1) == 0 ? 0f : 0.5f;
                        int col = Mathf.FloorToInt(px - offset);
                        for (int c = col - 1; c <= col + 1; c++)
                        {
                            float cx = c + offset + 0.5f, cy = (r + 0.5f);
                            float dx = px - cx, dy = (py - cy) * h;
                            float d = dx * dx + dy * dy;
                            if (d < best) { second = best; best = d; bestId = c * 131 + r * 71; }
                            else if (d < second) second = d;
                        }
                    }
                    float edge = Mathf.Sqrt(second) - Mathf.Sqrt(best);
                    float gap = Mathf.Clamp01(1f - edge * 14f);
                    float tone = 0.06f + 0.035f * Hash(bestId, 3) + (Hash(bestId, 9) > 0.97f ? 0.05f : 0f);
                    var color = new Color(tone, tone, tone * 1.05f) * (1f - gap * 0.6f);
                    color.a = 0.35f - gap * 0.2f;
                    pixels[y * size + x] = color;
                }
            }
            return Finish(t, pixels);
        }

        /// <summary>Lunar regolith: grey dust with darker and lighter patches and scattered rocks.</summary>
        /// <summary>
        /// Lunar soil, seamless: fine grey dust with darker and lighter patches, pebbles and small craters (bright
        /// rims, shaded bowls). Wraps in both directions so a big ground disc can repeat it without visible seams.
        /// </summary>
        public static Texture2D Regolith()
        {
            const int size = 512;
            var t = New(size, size, false);
            var shade = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float n = TileableFbm(x / 64f, y / 64f, size / 64, 5);
                    float fine = TileableFbm(x / 4f, y / 4f, size / 4, 2);
                    shade[y * size + x] = 0.42f + (n - 0.5f) * 0.30f + (fine - 0.5f) * 0.10f;
                }

            var random = new System.Random(1969);
            for (int k = 0; k < 34; k++)
            {
                float cx = (float)random.NextDouble() * size, cy = (float)random.NextDouble() * size;
                float r = 3f + 40f * Mathf.Pow((float)random.NextDouble(), 3f);
                float depth = 0.5f + (float)random.NextDouble();
                int reach = Mathf.CeilToInt(r * 1.6f);
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                        if (d > 1.6f) continue;
                        // Bowl: shaded on one side and lit on the other as if by a low sun; a soft rim just outside
                        // the edge; ejecta fading out around it.
                        float side = (dx - dy) / (r * 1.414f);
                        float bowl = d < 1f ? depth * (-0.035f * (1f - d * d) + 0.07f * side * (1f - d)) : 0f;
                        float rim = depth * 0.035f * Mathf.Exp(-Mathf.Pow((d - 1.05f) / 0.15f, 2f));
                        float ejecta = d > 1f ? 0.015f * (1.6f - d) : 0f;
                        int px = ((int)cx + dx + size * 4) % size, py = ((int)cy + dy + size * 4) % size;
                        shade[py * size + px] += bowl + rim + ejecta;
                    }
            }
            for (int k = 0; k < 600; k++)
            {
                int px = random.Next(size), py = random.Next(size);
                float v = random.NextDouble() < 0.5 ? -0.07f : 0.07f;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        shade[((py + dy + size) % size) * size + (px + dx + size) % size] += v * (dx == 0 && dy == 0 ? 1f : 0.4f);
            }

            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                float g = Mathf.Clamp01(shade[i]);
                pixels[i] = new Color(g, g * 0.98f, g * 0.95f, 0.12f);
            }
            return Finish(t, pixels);
        }

        /// <summary>Fractal value noise that repeats every <paramref name="period"/> lattice cells (at the base octave).</summary>
        static float TileableFbm(float x, float y, int period, int octaves)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += TileableNoise(x, y, period) * amplitude;
                total += amplitude;
                x *= 2f;
                y *= 2f;
                period *= 2;
                amplitude *= 0.5f;
            }
            return sum / total;
        }

        static float TileableNoise(float x, float y, int period)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            int ax = ((x0 % period) + period) % period, bx = (ax + 1) % period;
            int ay = ((y0 % period) + period) % period, by = (ay + 1) % period;
            float a = Hash(ax, ay), b = Hash(bx, ay), c = Hash(ax, by), d = Hash(bx, by);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
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
