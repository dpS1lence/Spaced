using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>
    /// A block of Web-Mercator imagery tiles stitched into one texture.
    /// Source: Sentinel-2 cloudless 2016 by EOX IT Services GmbH (CC BY 4.0, contains modified Copernicus
    /// Sentinel data), ~10 m resolution, cloud-free. Tiles are cached on disk and only fetched once.
    /// </summary>
    public sealed class ImageryMosaic
    {
        public const string Attribution = "Imagery: Sentinel-2 cloudless 2016 by EOX IT Services GmbH (contains modified Copernicus Sentinel data)";
        const int TileSize = 256;
        const int ParallelRequests = 6;

        public Texture2D Texture { get; private set; }
        public int Zoom { get; private set; }
        /// <summary>Top-left tile of the block.</summary>
        public int TileX0 { get; private set; }
        public int TileY0 { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public double CenterLatDeg { get; private set; }
        public double CenterLonDeg { get; private set; }

        public static double TileX(double lonDeg, int zoom) => (lonDeg + 180.0) / 360.0 * (1 << zoom);

        public static double TileY(double latDeg, int zoom)
        {
            double lat = Math.Max(-85.05, Math.Min(85.05, latDeg)) * MathConst.Deg2Rad;
            return (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0 * (1 << zoom);
        }

        /// <summary>Downloads (or loads from cache) a block of tiles centred on a point and stitches them.</summary>
        public IEnumerator Load(double latDeg, double lonDeg, int zoom, int columns, int rows, Action<ImageryMosaic> onDone, bool keepReadable = false)
        {
            Zoom = zoom;
            Columns = columns;
            Rows = rows;
            CenterLatDeg = latDeg;
            CenterLonDeg = lonDeg;
            int maxTile = (1 << zoom) - 1;
            TileX0 = (int)Math.Floor(TileX(lonDeg, zoom) - columns * 0.5 + 0.5);
            TileY0 = Math.Max(0, Math.Min(maxTile - rows + 1, (int)Math.Floor(TileY(latDeg, zoom) - rows * 0.5 + 0.5)));

            var pixels = new Color32[columns * TileSize * rows * TileSize];
            int pending = 0, next = 0, total = columns * rows;
            var decoder = new Texture2D(2, 2, TextureFormat.RGB24, false);

            while (next < total || pending > 0)
            {
                while (pending < ParallelRequests && next < total)
                {
                    int index = next++;
                    int column = index % columns, row = index / columns;
                    int x = ((TileX0 + column) % (maxTile + 1) + maxTile + 1) % (maxTile + 1);
                    int y = TileY0 + row;
                    pending++;
                    CoroutineHost.Run(LoadTile(zoom, x, y, bytes =>
                    {
                        pending--;
                        if (bytes == null || !decoder.LoadImage(bytes) || decoder.width != TileSize || decoder.height != TileSize) return;
                        var tile = decoder.GetPixels32();
                        // Tile rows run top to bottom; texture rows bottom to top.
                        int destX = column * TileSize, destY = (rows - 1 - row) * TileSize, stride = columns * TileSize;
                        for (int ty = 0; ty < TileSize; ty++)
                            Array.Copy(tile, ty * TileSize, pixels, (destY + ty) * stride + destX, TileSize);
                    }));
                }
                yield return null;
            }
            UnityEngine.Object.Destroy(decoder);

            Texture = new Texture2D(columns * TileSize, rows * TileSize, TextureFormat.RGBA32, true)
            {
                name = $"Imagery z{zoom} {latDeg:0.0},{lonDeg:0.0}",
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 8,
            };
            Texture.SetPixels32(pixels);
            Texture.Apply(true, false);
            if (!keepReadable)
            {
                Texture.Compress(false);
                Texture.Apply(false, true);
            }
            onDone(this);
        }

        /// <summary>
        /// Local mapping for a flat patch around (lat, lon): texture uv = (u0 + east·du, v0 + north·dv), metres.
        /// Exact enough over tens of kilometres.
        /// </summary>
        public Vector4 LocalMapping(double latDeg, double lonDeg)
        {
            double width = Columns * TileSize, height = Rows * TileSize;
            double px = (TileX(lonDeg, Zoom) - TileX0) * TileSize;
            double py = (TileY(latDeg, Zoom) - TileY0) * TileSize;
            double pixelsPerMetre = TileSize * (1 << Zoom) / (2.0 * Math.PI * EarthConstants.RadiusKm * 1000.0 * Math.Cos(latDeg * MathConst.Deg2Rad));
            return new Vector4((float)(px / width), (float)(1.0 - py / height), (float)(pixelsPerMetre / width), (float)(pixelsPerMetre / height));
        }

        static IEnumerator LoadTile(int z, int x, int y, Action<byte[]> onDone) => TileDownloads.Imagery(new TileId(z, x, y), onDone);
    }

    /// <summary>Runs coroutines for plain C# classes.</summary>
    public sealed class CoroutineHost : MonoBehaviour
    {
        static CoroutineHost _instance;

        public static Coroutine Run(IEnumerator routine)
        {
            if (_instance == null)
            {
                var go = new GameObject("Coroutine Host") { hideFlags = HideFlags.HideInHierarchy };
                _instance = go.AddComponent<CoroutineHost>();
            }
            return _instance.StartCoroutine(routine);
        }
    }
}
