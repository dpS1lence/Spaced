using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// Streams the whole planet as a quadtree of Web-Mercator tiles, like a map client. Every frame it refines the
    /// visible tiles blurriest-first (horizon- and frustum-culled) until each imagery texel is about a screen pixel or
    /// the tile budget is spent, downloads imagery (Sentinel-2, down to ~2.4 m/px rendering) and elevation (to ~5 m
    /// spacing) — plus NASA Black Marble city lights for tiles near or past the terminator — and draws them as
    /// terrain meshes over the base globe. A tile is only replaced by its children once
    /// all of them are ready, so loading never opens holes or overlaps.
    /// <para>Also answers "how high is the ground here?" (<see cref="HeightAt"/>) for anything that lands or takes off.</para>
    /// </summary>
    [DefaultExecutionOrder(-15)]
    public sealed class GlobeTiles : MonoBehaviour
    {
        public const int RootZoom = 3;
        /// <summary>Coarser tiles add nothing over the 5400 px base map; they are only drawn while finer ones load.</summary>
        public const int MinDrawZoom = 5;
        public const int MaxImageryZoom = 16;
        public const int MaxTerrainZoom = 15;
        /// <summary>The tiles fade in over the base map as the camera descends between these altitudes (km).</summary>
        public const double FadeStartKm = 14000, FadeEndKm = 10000;
        const int HeightGrid = 65;              // samples per side kept from each 256 px elevation tile
        const int MeshGrid = 33;                // vertices per side of a tile mesh
        const double MaxTerrainKm = 9.0;        // Everest, for culling bounds
        const float MaxRetryDelay = 30f;        // seconds between attempts after repeated network errors

        public static GlobeTiles Instance { get; private set; }

        public float PixelsPerTexel = 1.3f;
        public float TerrainExaggeration = 1f;
        public int MaxConcurrentDownloads = 8;
        /// <summary>Decoding a tile costs ~1–2 ms, so even cached tiles are started a few per frame.</summary>
        public int MaxStartsPerFrame = 8;
        public int MaxMeshBuildsPerFrame = 16;
        public int MaxDrawTiles = 900;
        public int MaxCachedTiles = 2000;
        public bool Terrain = true;

        public int DrawnTiles { get; private set; }
        public int WantedTiles { get; private set; }
        public int PendingDownloads => _pending.Count + _inFlight;

        /// <summary>
        /// Download state of one layer of a tile. Network errors (servers reset busy connections) are retried with
        /// a growing delay; only a tile that doesn't exist is given up on.
        /// </summary>
        sealed class Download
        {
            public bool InFlight, Done, GaveUp;
            int _failures;
            float _retryAt;

            public bool CanStart(float now) => !InFlight && !Done && !GaveUp && now >= _retryAt;

            public void Failed(bool notFound, float now)
            {
                InFlight = false;
                if (notFound) { GaveUp = true; return; }
                _failures++;
                _retryAt = now + Mathf.Min(MaxRetryDelay, Mathf.Pow(2f, _failures));
            }
        }

        sealed class Tile
        {
            public TileId Id;
            public double3 CenterEcefKm;
            public int LastUsedFrame;
            public readonly Download Imagery = new Download(), Elevation = new Download(), Night = new Download();
            public Texture2D Texture;
            /// <summary>Black Marble city lights (tiles up to <see cref="TileDownloads.MaxNightZoom"/> only).</summary>
            public Texture2D NightTexture;
            /// <summary>Night-light texture currently bound to <see cref="Properties"/> (null: the global map).</summary>
            public Texture2D NightBound;
            public bool NightBindValid;
            /// <summary>Per tile: RenderMesh keeps a reference to the block until the frame renders, so one shared block would give every tile the last texture set.</summary>
            public MaterialPropertyBlock Properties;
            public float[] Heights;
            public Mesh Mesh;
            public int MeshHeightZ = -2;
        }

        /// <summary>Static geometry of a tile, cached because the selection measures thousands per frame.</summary>
        struct TileShape
        {
            public double3 CenterUnit;
            public double AngularRadius, WidthM;
        }

        struct Node
        {
            public TileId Id;
            public double Error;        // screen pixels per imagery texel
            public double Key;          // distance / tile width: smaller = bigger on screen = download first
        }

        enum Layer { Imagery, Terrain, Night }

        struct Request
        {
            public TileId Id;
            public Layer Layer;
            public double Priority;
        }

        SpaceSimulation _sim;
        CameraRig _rig;
        Material _material;
        Texture2D _decoder;
        readonly Dictionary<TileId, Tile> _tiles = new Dictionary<TileId, Tile>();
        readonly Dictionary<TileId, TileShape> _shapes = new Dictionary<TileId, TileShape>();

        // This frame's selection: a quadtree from the root tiles, where each split tile maps to its visible children.
        readonly List<TileId> _roots = new List<TileId>(64);
        readonly Dictionary<TileId, byte> _children = new Dictionary<TileId, byte>();
        readonly Dictionary<TileId, double> _keys = new Dictionary<TileId, double>();
        readonly List<Node> _heap = new List<Node>(1024);
        readonly Node[] _kids = new Node[4];
        readonly List<TileId> _stack = new List<TileId>(64);
        readonly List<TileId> _draw = new List<TileId>(1024);
        readonly List<Request> _pending = new List<Request>(1024);
        readonly Plane[] _frustum = new Plane[6];
        int _inFlight, _meshBudget;

        // Per-frame view parameters.
        double3 _cameraEcefKm;
        double _cameraRadiusKm, _cameraLat, _cameraLon, _groundUnderCameraKm;
        double _horizonAngle, _metersPerPixelPerMeter, _jd;
        double3 _sunEcef;

        static readonly int MainTex = Shader.PropertyToID("_MainTex");
        static readonly int NightTex = Shader.PropertyToID("_NightTex");
        static readonly int NightRect = Shader.PropertyToID("_NightRect");
        static readonly int TileZoom = Shader.PropertyToID("_TileZoom");
        static readonly int TileFade = Shader.PropertyToID("_SpacedTileFade");

        public void Init(SpaceSimulation sim, CameraRig rig, Shader shader)
        {
            _sim = sim;
            _rig = rig;
            _material = new Material(shader) { name = "Globe Tiles" };
            _decoder = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var t in _tiles.Values)
            {
                if (t.Texture != null) Destroy(t.Texture);
                if (t.NightTexture != null) Destroy(t.NightTexture);
                if (t.Mesh != null) Destroy(t.Mesh);
            }
        }

        // ───────────────────────────── Terrain queries ─────────────────────────────

        /// <summary>Ground height above sea level (metres) at a point, from the finest elevation tile loaded so far.</summary>
        public double HeightAt(double latDeg, double lonDeg)
        {
            if (!Terrain || math.abs(latDeg) > TileMath.MaxLatitude) return 0;
            for (int z = MaxTerrainZoom; z >= 0; z--)
            {
                var id = TileMath.TileAt(latDeg, lonDeg, z);
                if (_tiles.TryGetValue(id, out var tile) && tile.Heights != null)
                    return SampleHeights(tile, TileMath.TileX(lonDeg, z), TileMath.TileY(latDeg, z)) * TerrainExaggeration;
            }
            return 0;
        }

        static double SampleHeights(Tile source, double tileX, double tileY)
        {
            double fx = math.clamp((tileX - source.Id.X) * (HeightGrid - 1), 0, HeightGrid - 1.001);
            double fy = math.clamp((tileY - source.Id.Y) * (HeightGrid - 1), 0, HeightGrid - 1.001);
            int x0 = (int)fx, y0 = (int)fy;
            double tx = fx - x0, ty = fy - y0;
            float[] h = source.Heights;
            double top = math.lerp(h[y0 * HeightGrid + x0], h[y0 * HeightGrid + x0 + 1], tx);
            double bottom = math.lerp(h[(y0 + 1) * HeightGrid + x0], h[(y0 + 1) * HeightGrid + x0 + 1], tx);
            return math.lerp(top, bottom, ty);
        }

        // ───────────────────────────── Frame ─────────────────────────────

        void LateUpdate()
        {
            if (_sim == null || _rig == null) return;
            _jd = _sim.Clock.JulianDate;
            _cameraEcefKm = Astro.EciToEcef(_rig.CameraEciKm, _jd);
            _cameraRadiusKm = math.length(_cameraEcefKm);
            double altitudeKm = _cameraRadiusKm - EarthConstants.RadiusKm;
            DrawnTiles = 0;
            WantedTiles = 0;

            double fade = math.saturate((FadeStartKm - altitudeKm) / (FadeStartKm - FadeEndKm));
            Shader.SetGlobalFloat(TileFade, (float)fade);
            if (fade <= 0) return; // the base map is as sharp as the screen from up here

            Astro.EcefToGeo(_cameraEcefKm, out _cameraLat, out _cameraLon, out _);
            _sunEcef = Astro.EciToEcef(Astro.SunDirection(_jd), _jd);
            _groundUnderCameraKm = HeightAt(_cameraLat, _cameraLon) / 1000.0;
            GeometryUtility.CalculateFrustumPlanes(_rig.Far, _frustum);
            _horizonAngle = math.acos(math.clamp(EarthConstants.RadiusKm / math.max(_cameraRadiusKm, EarthConstants.RadiusKm + 0.001), -1, 1));
            _metersPerPixelPerMeter = 2.0 * math.tan(_rig.Far.fieldOfView * 0.5 * MathConst.Deg2Rad) / math.max(Screen.height, 1);

            Select();
            QueueDownloads();
            StartDownloads();

            _meshBudget = MaxMeshBuildsPerFrame;
            _draw.Clear();
            foreach (var root in _roots) Cover(root);
            Draw();
            if (Time.frameCount % 30 == 0) Evict();
        }

        // ───────────────────────────── Selection ─────────────────────────────

        /// <summary>
        /// Builds this frame's quadtree: starting from the visible root tiles, keeps splitting whichever visible tile
        /// looks blurriest until every tile is sharp enough or the draw budget is reached. Because the worst tile
        /// is always refined first, the detail goes where you look, whatever the budget.
        /// </summary>
        void Select()
        {
            _roots.Clear();
            _children.Clear();
            _keys.Clear();
            _heap.Clear();
            if (_shapes.Count > 60000) _shapes.Clear();

            int leaves = 0;
            int n = 1 << RootZoom;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    if (!Measure(new TileId(RootZoom, x, y), out var node)) continue;
                    _roots.Add(node.Id);
                    _keys[node.Id] = node.Key;
                    Push(node);
                    leaves++;
                }
            }

            while (_heap.Count > 0)
            {
                var node = Pop();
                bool forced = node.Id.Z < MinDrawZoom;
                if (!forced && (node.Error <= PixelsPerTexel || node.Id.Z >= MaxImageryZoom)) continue;

                int visible = 0;
                byte mask = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (!Measure(node.Id.Child(i), out var child)) continue;
                    mask |= (byte)(1 << i);
                    _kids[visible++] = child;
                }
                if (!forced && leaves - 1 + visible > MaxDrawTiles) continue;

                _children[node.Id] = mask;
                leaves += visible - 1;
                for (int i = 0; i < visible; i++)
                {
                    _keys[_kids[i].Id] = _kids[i].Key;
                    Push(_kids[i]);
                }
            }
            WantedTiles = leaves;
        }

        /// <summary>Is the tile visible, and how blurry would it look? Distances are to the tile's nearest point.</summary>
        bool Measure(TileId t, out Node node)
        {
            node = default;
            var shape = Shape(t);

            // Beyond the horizon (with a little allowance for mountains)?
            double angleToCamera = math.acos(math.clamp(math.dot(shape.CenterUnit, _cameraEcefKm / _cameraRadiusKm), -1, 1));
            if (angleToCamera - shape.AngularRadius > _horizonAngle + 0.003) return false;

            // Outside the view frustum?
            double radiusKm = shape.AngularRadius * EarthConstants.RadiusKm + MaxTerrainKm;
            Vector3 centerFar = WorldFrame.ToFar(Astro.EcefToEci(shape.CenterUnit * EarthConstants.RadiusKm, _jd));
            float sizeFar = (float)(radiusKm * 2.0 / WorldScale.KmPerUnit);
            if (!GeometryUtility.TestPlanesAABB(_frustum, new Bounds(centerFar, Vector3.one * sizeFar))) return false;

            TileMath.ClosestPoint(t, _cameraLat, _cameraLon, out double nearLat, out double nearLon);
            double3 nearest = Astro.GeoToEcef(nearLat, nearLon, _groundUnderCameraKm);
            double distanceM = math.max(math.distance(_cameraEcefKm, nearest) * 1000.0, 1.0);
            node = new Node
            {
                Id = t,
                Error = TileMath.TexelError(t, distanceM, _metersPerPixelPerMeter),
                Key = distanceM / shape.WidthM,
            };
            return true;
        }

        TileShape Shape(TileId t)
        {
            if (_shapes.TryGetValue(t, out var shape)) return shape;
            TileMath.Center(t, out double lat, out double lon);
            shape = new TileShape
            {
                CenterUnit = math.normalize(Astro.GeoToEcef(lat, lon, 0)),
                AngularRadius = TileMath.AngularRadius(t),
                WidthM = TileMath.WidthMeters(t),
            };
            _shapes[t] = shape;
            return shape;
        }

        // Binary max-heap on refinement priority: tiles coarser than MinDrawZoom always first, then the blurriest.
        static double Priority(in Node n) => n.Id.Z < MinDrawZoom ? 1e30 - n.Id.Z : n.Error;

        void Push(Node node)
        {
            _heap.Add(node);
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (Priority(_heap[parent]) >= Priority(_heap[i])) break;
                (_heap[parent], _heap[i]) = (_heap[i], _heap[parent]);
                i = parent;
            }
        }

        Node Pop()
        {
            var top = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = left + 1, best = i;
                if (left < _heap.Count && Priority(_heap[left]) > Priority(_heap[best])) best = left;
                if (right < _heap.Count && Priority(_heap[right]) > Priority(_heap[best])) best = right;
                if (best == i) break;
                (_heap[best], _heap[i]) = (_heap[i], _heap[best]);
                i = best;
            }
            return top;
        }

        Tile Get(TileId id, bool create)
        {
            if (_tiles.TryGetValue(id, out var tile) || !create) return tile;
            tile = new Tile { Id = id, CenterEcefKm = Shape(id).CenterUnit * EarthConstants.RadiusKm };
            _tiles[id] = tile;
            return tile;
        }

        // ───────────────────────────── Downloads ─────────────────────────────

        /// <summary>
        /// Lists what this frame's tree still needs, biggest-on-screen first: imagery for every tile in it (the
        /// coarser ones stand in while the finer ones load) and elevation for the leaves.
        /// </summary>
        void QueueDownloads()
        {
            _pending.Clear();
            float now = Time.unscaledTime;
            int frame = Time.frameCount;
            _stack.Clear();
            _stack.AddRange(_roots);
            while (_stack.Count > 0)
            {
                var id = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                var tile = Get(id, true);
                tile.LastUsedFrame = frame;
                double key = _keys[id];
                if (tile.Texture == null && tile.Imagery.CanStart(now))
                    _pending.Add(new Request { Id = id, Layer = Layer.Imagery, Priority = key });

                // City lights, only where it is (nearly) night: the terminator plus a margin.
                var shape = Shape(id);
                if (math.dot(shape.CenterUnit, _sunEcef) < 0.25 + math.sin(shape.AngularRadius))
                {
                    var nightId = id.AncestorAt(math.min(id.Z, TileDownloads.MaxNightZoom));
                    var night = Get(nightId, true);
                    night.LastUsedFrame = frame;
                    if (night.NightTexture == null && night.Night.CanStart(now))
                        _pending.Add(new Request { Id = nightId, Layer = Layer.Night, Priority = key * 1.1 });
                }

                if (_children.TryGetValue(id, out byte mask))
                {
                    for (int i = 0; i < 4; i++)
                        if ((mask & (1 << i)) != 0) _stack.Add(id.Child(i));
                }
                else if (Terrain)
                {
                    // One elevation tile a level up has 64 samples per side: plenty for the 33-vertex meshes of its four children.
                    var terrainId = id.AncestorAt(math.min(id.Z - 1, MaxTerrainZoom));
                    var terrain = Get(terrainId, true);
                    terrain.LastUsedFrame = frame;
                    if (terrain.Heights == null && terrain.Elevation.CanStart(now))
                        _pending.Add(new Request { Id = terrainId, Layer = Layer.Terrain, Priority = key * 0.9 });
                }
            }
        }

        void StartDownloads()
        {
            if (_pending.Count == 0 || _inFlight >= MaxConcurrentDownloads) return;
            _pending.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            float now = Time.unscaledTime;
            int started = 0;
            foreach (var request in _pending)
            {
                if (_inFlight >= MaxConcurrentDownloads || started >= MaxStartsPerFrame) break;
                var tile = Get(request.Id, true);
                var state = request.Layer == Layer.Terrain ? tile.Elevation : request.Layer == Layer.Night ? tile.Night : tile.Imagery;
                if (!state.CanStart(now)) continue; // already started for another leaf this frame
                state.InFlight = true;
                _inFlight++;
                started++;
                switch (request.Layer)
                {
                    case Layer.Terrain:
                        StartCoroutine(TileDownloads.Terrain(request.Id, bytes => OnTerrain(tile, bytes), notFound => OnFailed(tile.Elevation, notFound)));
                        break;
                    case Layer.Night:
                        StartCoroutine(TileDownloads.Night(request.Id, bytes => OnNight(tile, bytes), notFound => OnFailed(tile.Night, notFound)));
                        break;
                    default:
                        StartCoroutine(TileDownloads.Imagery(request.Id, bytes => OnImagery(tile, bytes), notFound => OnFailed(tile.Imagery, notFound)));
                        break;
                }
            }
        }

        void OnFailed(Download state, bool notFound)
        {
            _inFlight--;
            state.Failed(notFound, Time.unscaledTime);
        }

        void OnImagery(Tile tile, byte[] bytes)
        {
            _inFlight--;
            tile.Imagery.InFlight = false;
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Tile " + tile.Id, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            if (bytes == null || !texture.LoadImage(bytes, false))
            {
                Destroy(texture);
                tile.Imagery.GaveUp = true;
                return;
            }
            texture.Compress(false);
            texture.Apply(false, true);
            tile.Texture = texture;
            tile.Properties = new MaterialPropertyBlock();
            tile.Properties.SetTexture(MainTex, texture);
            tile.Properties.SetFloat(TileZoom, tile.Id.Z);
            tile.NightBindValid = false;
            tile.Imagery.Done = true;
        }

        void OnNight(Tile tile, byte[] bytes)
        {
            _inFlight--;
            tile.Night.InFlight = false;
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true) { name = "Night " + tile.Id, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            if (bytes == null || !texture.LoadImage(bytes, false))
            {
                Destroy(texture);
                tile.Night.GaveUp = true;
                return;
            }
            texture.Compress(false);
            texture.Apply(false, true);
            tile.NightTexture = texture;
            tile.Night.Done = true;
        }

        /// <summary>Binds the finest loaded night-light tile covering this one (or the global map) to its properties.</summary>
        void BindNight(Tile tile)
        {
            var id = tile.Id;
            Tile source = null;
            for (int z = math.min(id.Z, TileDownloads.MaxNightZoom); z >= 0 && source == null; z--)
            {
                var candidate = Get(id.AncestorAt(z), false);
                if (candidate?.NightTexture != null) source = candidate;
            }
            var texture = source?.NightTexture;
            if (tile.NightBindValid && ReferenceEquals(texture, tile.NightBound)) return; // not ==: a destroyed texture equals null
            tile.NightBound = texture;
            tile.NightBindValid = true;
            if (source == null)
            {
                tile.Properties.SetVector(NightRect, Vector4.zero);
                return;
            }
            source.LastUsedFrame = Time.frameCount;
            TileMath.AncestorUv(id, source.Id, out double scale, out double offsetU, out double offsetV);
            tile.Properties.SetTexture(NightTex, source.NightTexture);
            tile.Properties.SetVector(NightRect, new Vector4((float)scale, (float)scale, (float)offsetU, (float)offsetV));
        }

        void OnTerrain(Tile tile, byte[] bytes)
        {
            _inFlight--;
            tile.Elevation.InFlight = false;
            if (bytes == null || !_decoder.LoadImage(bytes, false) || _decoder.width != TileMath.TileSize)
            {
                tile.Elevation.GaveUp = true;
                return;
            }
            var pixels = _decoder.GetPixels32();
            var heights = new float[HeightGrid * HeightGrid];
            int size = _decoder.width;
            for (int row = 0; row < HeightGrid; row++)
            {
                // Grid row 0 is the tile's north edge; Unity's image row 0 is the bottom (south).
                int py = size - 1 - math.min(size - 1, row * (size / (HeightGrid - 1)));
                for (int col = 0; col < HeightGrid; col++)
                {
                    int px = math.min(size - 1, col * (size / (HeightGrid - 1)));
                    var c = pixels[py * size + px];
                    heights[row * HeightGrid + col] = math.max(0f, TileMath.TerrariumHeight(c.r, c.g, c.b)); // sea surface, not sea floor
                }
            }
            tile.Heights = heights;
            tile.Elevation.Done = true;
        }

        // ───────────────────────────── Meshes ─────────────────────────────

        /// <summary>
        /// Adds to the draw list the ready tiles that cover this subtree. A tile gives way to its children only when
        /// every one of them is covered; otherwise it is drawn itself (if ready) and the children wait.
        /// </summary>
        bool Cover(TileId id)
        {
            if (!_children.TryGetValue(id, out byte mask))
            {
                if (!Ready(id)) return false;
                _draw.Add(id);
                return true;
            }

            int mark = _draw.Count;
            bool all = true;
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0 && !Cover(id.Child(i))) all = false;
            if (all) return true;

            if (!Ready(id)) return false; // whatever children are ready stay; the base globe fills the rest
            _draw.RemoveRange(mark, _draw.Count - mark);
            _draw.Add(id);
            return true;
        }

        /// <summary>Has imagery and a mesh (building or improving the mesh within this frame's budget).</summary>
        bool Ready(TileId id)
        {
            var tile = Get(id, false);
            if (tile?.Texture == null) return false;
            var source = HeightSource(id);
            int sourceZ = source?.Id.Z ?? -1;
            if ((tile.Mesh == null || tile.MeshHeightZ < sourceZ) && _meshBudget > 0)
            {
                _meshBudget--;
                if (tile.Mesh != null) Destroy(tile.Mesh);
                tile.Mesh = BuildMesh(tile, source);
                tile.MeshHeightZ = sourceZ;
            }
            return tile.Mesh != null;
        }

        /// <summary>Finest loaded elevation tile covering this one (or null).</summary>
        Tile HeightSource(TileId id)
        {
            if (!Terrain) return null;
            for (int z = math.min(id.Z, MaxTerrainZoom); z >= 0; z--)
            {
                var t = Get(id.AncestorAt(z), false);
                if (t?.Heights != null) return t;
            }
            return null;
        }

        Mesh BuildMesh(Tile tile, Tile heights)
        {
            var id = tile.Id;
            const int n = MeshGrid;
            int gridCount = n * n, skirtCount = 4 * (n - 1);
            var vertices = new Vector3[gridCount + skirtCount];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var uv2 = new Vector2[vertices.Length];
            var ecef = new double3[gridCount];
            double3 center = tile.CenterEcefKm * 1000.0;
            // Vertices in far-scene units around the tile centre, so the draw matrix has unit scale: a metres → far
            // scale (1e-6) in the matrix makes it numerically singular and Unity's inverse (used for normals) zero.
            const double toFar = 1.0 / WorldFrame.MetersPerFarUnit;

            for (int j = 0; j < n; j++)
            {
                double ty = id.Y + j / (n - 1.0);
                double lat = TileMath.Lat(ty, id.Z);
                for (int i = 0; i < n; i++)
                {
                    double tx = id.X + i / (n - 1.0);
                    double lon = TileMath.Lon(tx, id.Z);
                    double h = 0;
                    if (heights != null)
                    {
                        int dz = id.Z - heights.Id.Z;
                        h = SampleHeights(heights, tx / (1 << dz), ty / (1 << dz)) * TerrainExaggeration;
                    }
                    int k = j * n + i;
                    ecef[k] = Astro.GeoToEcef(lat, lon, h / 1000.0) * 1000.0;
                    vertices[k] = Swap((ecef[k] - center) * toFar);
                    uv[k] = new Vector2(i / (n - 1f), 1f - j / (n - 1f));
                    uv2[k] = new Vector2((float)((lon + 180.0) / 360.0), (float)((lat + 90.0) / 180.0));
                }
            }

            // Normals from the terrain surface (hillshading), oriented away from the Earth's centre.
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int k = j * n + i;
                    double3 east = ecef[j * n + math.min(i + 1, n - 1)] - ecef[j * n + math.max(i - 1, 0)];
                    double3 south = ecef[math.min(j + 1, n - 1) * n + i] - ecef[math.max(j - 1, 0) * n + i];
                    double3 normal = math.normalize(math.cross(east, south));
                    if (math.dot(normal, ecef[k]) < 0) normal = -normal;
                    normals[k] = Swap(normal);
                }
            }

            // Skirts hang down from the edges and hide cracks between neighbouring tiles of different detail.
            double skirt = TileMath.WidthMeters(id) * 0.02 + 30.0;
            int s = gridCount;
            var edge = new List<int>(skirtCount);
            for (int i = 0; i < n - 1; i++) edge.Add(i);                      // north, west → east
            for (int j = 0; j < n - 1; j++) edge.Add(j * n + n - 1);          // east, north → south
            for (int i = n - 1; i > 0; i--) edge.Add((n - 1) * n + i);        // south, east → west
            for (int j = n - 1; j > 0; j--) edge.Add(j * n);                  // west, south → north
            foreach (int k in edge)
            {
                vertices[s] = Swap((ecef[k] - math.normalize(ecef[k]) * skirt - center) * toFar);
                normals[s] = normals[k];
                uv[s] = uv[k];
                uv2[s] = uv2[k];
                s++;
            }

            var triangles = new List<int>((n - 1) * (n - 1) * 6 + skirtCount * 6);
            for (int j = 0; j < n - 1; j++)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    triangles.Add(a); triangles.Add(b); triangles.Add(d);
                    triangles.Add(a); triangles.Add(d); triangles.Add(c);
                }
            }
            for (int e = 0; e < edge.Count; e++)
            {
                int top0 = edge[e], top1 = edge[(e + 1) % edge.Count];
                int bottom0 = gridCount + e, bottom1 = gridCount + (e + 1) % edge.Count;
                triangles.Add(top0); triangles.Add(bottom0); triangles.Add(top1);
                triangles.Add(top1); triangles.Add(bottom0); triangles.Add(bottom1);
            }

            var mesh = new Mesh { name = "Tile " + id };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.uv2 = uv2;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Earth-fixed (ECEF) axes → the Earth object's local axes (Y = north pole), as the base globe uses.</summary>
        static Vector3 Swap(double3 v) => new Vector3((float)v.x, (float)v.z, (float)v.y);

        // ───────────────────────────── Drawing ─────────────────────────────

        void Draw()
        {
            Quaternion earth = Astro.EarthRotation(_jd);
            var renderParams = new RenderParams(_material)
            {
                layer = 0,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };

            foreach (var id in _draw)
            {
                var tile = Get(id, false);
                BindNight(tile);
                renderParams.matProps = tile.Properties;

                Vector3 position = WorldFrame.ToFar(Astro.EcefToEci(tile.CenterEcefKm, _jd));
                var matrix = Matrix4x4.TRS(position, earth, Vector3.one);
                renderParams.worldBounds = new Bounds(position, Vector3.one * (float)(TileMath.WidthMeters(id) * 2.5 / WorldFrame.MetersPerFarUnit + 0.02));
                Graphics.RenderMesh(renderParams, tile.Mesh, 0, matrix);
                DrawnTiles++;
            }
        }

        /// <summary>Frees the least recently used tiles once the cache is over budget.</summary>
        void Evict()
        {
            if (_tiles.Count <= MaxCachedTiles) return;
            var candidates = new List<Tile>(_tiles.Values);
            candidates.Sort((a, b) => a.LastUsedFrame.CompareTo(b.LastUsedFrame));
            int remove = _tiles.Count - MaxCachedTiles * 9 / 10;
            for (int i = 0; i < remove && i < candidates.Count; i++)
            {
                var t = candidates[i];
                if (t.LastUsedFrame >= Time.frameCount - 1) break; // in use this frame
                if (t.Imagery.InFlight || t.Elevation.InFlight || t.Night.InFlight) continue;
                if (t.Texture != null) Destroy(t.Texture);
                if (t.NightTexture != null) Destroy(t.NightTexture);
                if (t.Mesh != null) Destroy(t.Mesh);
                _tiles.Remove(t.Id);
            }
        }
    }
}
