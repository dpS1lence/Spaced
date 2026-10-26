using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// The Earth: a high-resolution sphere in Earth-fixed coordinates rotated by sidereal time, with NASA Blue
    /// Marble imagery for the current month (snow and vegetation follow the seasons), Black Marble city lights,
    /// a drifting cloud layer that casts shadows, and a single-scattering atmosphere. Close up, the streamed
    /// Sentinel-2 terrain tiles of <see cref="GlobeTiles"/> are drawn over it.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public sealed class EarthRenderer : MonoBehaviour
    {
        // NASA Visible Earth imagery (public domain). Blue Marble Next Generation, one image per month of 2004.
        static readonly int[] BlueMarbleRecordIds = { 73580, 73605, 73630, 73655, 73701, 73726, 73751, 73776, 73801, 73826, 73884, 73909 };
        public const string NightTextureUrl = "https://eoimages.gsfc.nasa.gov/images/imagerecords/144000/144898/BlackMarble_2016_01deg.jpg";
        public const string CloudTextureUrl = "https://eoimages.gsfc.nasa.gov/images/imagerecords/57000/57747/cloud_combined_2048.jpg";

        public static string DayTextureUrl(int month) =>
            $"https://eoimages.gsfc.nasa.gov/images/imagerecords/73000/{BlueMarbleRecordIds[month - 1]}/world.topo.bathy.2004{month:00}.3x5400x2700.jpg";

        const float CloudScale = 1.0016f;   // ~10 km up
        /// <summary>
        /// The base sphere sits 1 km low so the streamed terrain tiles, whose flat facets sag up to a few hundred
        /// metres below the true sphere, always draw in front of it instead of flickering against it.
        /// </summary>
        const float SurfaceSink = 1f - 1f / 6371f;
        const float CloudDriftDegPerDay = 4f;
        static readonly int CloudTex = Shader.PropertyToID("_CloudTex");
        static readonly int CloudRotation = Shader.PropertyToID("_CloudRotation");
        static readonly int CloudFade = Shader.PropertyToID("_SpacedCloudFade");

        static readonly int GlobalNightTex = Shader.PropertyToID("_SpacedNightTex");
        static readonly int GlobalCloudTex = Shader.PropertyToID("_SpacedCloudTex");
        static readonly int GlobalCloudRotation = Shader.PropertyToID("_SpacedCloudRotation");
        static readonly int GlobalDayBrightness = Shader.PropertyToID("_SpacedDayBrightness");

        SpaceSimulation _sim;
        CameraRig _rig;
        Material _surface, _clouds;
        Transform _cloudLayer, _atmosphere;
        bool _hasDayOverride;
        int _loadedMonth, _requestedMonth;

        public void Init(SpaceSimulation sim, CameraRig rig, Shader earthShader, Shader cloudShader, Shader atmosphereShader,
            Texture2D dayOverride, Texture2D nightOverride)
        {
            _sim = sim;
            _rig = rig;
            var mesh = BuildSphere(768, 384);
            float radius = (float)(EarthConstants.RadiusKm / WorldScale.KmPerUnit);

            _surface = new Material(earthShader) { name = "Earth Surface" };
            _surface.SetTexture(ShaderIds.DayTex, dayOverride != null ? dayOverride : BuildGraticule());
            _surface.SetTexture(CloudTex, Texture2D.blackTexture);
            Shader.SetGlobalTexture(GlobalNightTex, Texture2D.blackTexture);
            Shader.SetGlobalTexture(GlobalCloudTex, Texture2D.blackTexture);
            Shader.SetGlobalFloat(GlobalDayBrightness, 1.25f);
            if (nightOverride != null) SetNight(nightOverride);
            CreatePart("Surface", mesh, _surface, radius * SurfaceSink);

            _clouds = new Material(cloudShader) { name = "Earth Clouds" };
            _clouds.SetTexture(CloudTex, Texture2D.blackTexture);
            _cloudLayer = CreatePart("Clouds", mesh, _clouds, radius * CloudScale).transform;

            var atmosphere = new Material(atmosphereShader) { name = "Atmosphere" };
            // The shell reaches up to the aurora; the scattering itself stops at CameraRig.AtmosphereTopKm.
            float top = (float)((EarthConstants.RadiusKm + CameraRig.AuroraTopKm) / WorldScale.KmPerUnit);
            // The atmosphere is not rotated, so keep it outside the spinning hierarchy.
            _atmosphere = CreatePart("Atmosphere", mesh, atmosphere, top).transform;
            _atmosphere.SetParent(null, false);

            _hasDayOverride = dayOverride != null;
            if (nightOverride == null) StartCoroutine(TextureCache.Load(NightTextureUrl, SetNight));
            StartCoroutine(TextureCache.Load(CloudTextureUrl, t =>
            {
                _staticClouds = t;
                if (!_liveClouds) ApplyClouds(t, Astro.JdJ2000);
            }));
        }

        Texture2D _staticClouds, _liveTexture;
        bool _liveClouds;
        double _cloudReferenceJd = Astro.JdJ2000;

        /// <summary>Shows a cloud map made from real satellite pictures taken at <paramref name="takenJd"/>.</summary>
        public void SetClouds(Texture2D texture, double takenJd)
        {
            if (_liveTexture != null && _liveTexture != texture) Destroy(_liveTexture);
            _liveTexture = texture;
            _liveClouds = true;
            ApplyClouds(texture, takenJd);
        }

        /// <summary>Back to the classic (climatological) cloud map, drifting slowly.</summary>
        public void UseStaticClouds()
        {
            _liveClouds = false;
            if (_staticClouds != null) ApplyClouds(_staticClouds, Astro.JdJ2000);
        }

        void ApplyClouds(Texture2D t, double referenceJd)
        {
            _cloudReferenceJd = referenceJd;
            _surface.SetTexture(CloudTex, t);
            _clouds.SetTexture(CloudTex, t);
            Shader.SetGlobalTexture(GlobalCloudTex, t);
        }

        void LateUpdate()
        {
            if (_sim == null) return;
            double jd = _sim.Clock.JulianDate;
            Vector3 center = WorldFrame.ToFar(Unity.Mathematics.double3.zero);
            transform.SetPositionAndRotation(center, Astro.EarthRotation(jd));
            _atmosphere.position = center;

            // Real pictures stay put (a few hours either side of when they were taken); the classic map drifts.
            float drift = (float)((jd - _cloudReferenceJd) * (_liveClouds ? 0 : CloudDriftDegPerDay) % 360.0);
            var cloudRotation = Quaternion.AngleAxis(-drift, Vector3.up);
            _cloudLayer.localRotation = cloudRotation;
            var toClouds = Matrix4x4.Rotate(Quaternion.Inverse(cloudRotation));
            _surface.SetMatrix(CloudRotation, toClouds);
            Shader.SetGlobalMatrix(GlobalCloudRotation, toClouds);

            if (!_hasDayOverride) UpdateSeason(Astro.ToDateTime(jd).Month);

            // Clouds (20 km per pixel) would blur close inspection of the ground; fade them out below ~400 km.
            double altitudeKm = _rig != null ? _rig.CameraAltitudeKm : 1e5;
            Shader.SetGlobalFloat(CloudFade, (float)math.saturate((altitudeKm - 40.0) / 400.0));
        }

        void SetNight(Texture2D texture)
        {
            _surface.SetTexture(ShaderIds.NightTex, texture);
            Shader.SetGlobalTexture(GlobalNightTex, texture);
        }

        void UpdateSeason(int month)
        {
            if (month == _requestedMonth) return;
            _requestedMonth = month;
            StartCoroutine(TextureCache.Load(DayTextureUrl(month), t =>
            {
                if (month != _requestedMonth && _loadedMonth != 0) return;
                var previous = _surface.GetTexture(ShaderIds.DayTex);
                _surface.SetTexture(ShaderIds.DayTex, t);
                _loadedMonth = month;
                if (previous is Texture2D old && old != t && old.name.StartsWith("world.topo")) Destroy(old);
            }));
        }

        GameObject CreatePart(string partName, Mesh mesh, Material material, float scale)
        {
            var part = new GameObject(partName);
            part.transform.SetParent(transform, false);
            part.transform.localScale = Vector3.one * scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = part.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return part;
        }

        /// <summary>
        /// Unit sphere. Local axes follow Earth-fixed coordinates mapped to Unity: +X = (0°N, 0°E),
        /// +Y = north pole, +Z = (0°N, 90°E). UVs are equirectangular, u = 0 at 180°W.
        /// </summary>
        public static Mesh BuildSphere(int lonSegments, int latSegments)
        {
            int columns = lonSegments + 1;
            var vertices = new Vector3[columns * (latSegments + 1)];
            var uvs = new Vector2[vertices.Length];
            for (int j = 0; j <= latSegments; j++)
            {
                float lat = Mathf.PI * j / latSegments - Mathf.PI / 2f;
                for (int k = 0; k <= lonSegments; k++)
                {
                    float lon = 2f * Mathf.PI * k / lonSegments - Mathf.PI;
                    int index = j * columns + k;
                    vertices[index] = new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
                    uvs[index] = new Vector2((float)k / lonSegments, (float)j / latSegments);
                }
            }

            var triangles = new int[lonSegments * latSegments * 6];
            int t = 0;
            for (int j = 0; j < latSegments; j++)
            {
                for (int k = 0; k < lonSegments; k++)
                {
                    int bottomLeft = j * columns + k;
                    int bottomRight = bottomLeft + 1;
                    int topLeft = bottomLeft + columns;
                    int topRight = topLeft + 1;
                    // Clockwise as seen from outside (Unity's front faces).
                    triangles[t++] = topLeft;
                    triangles[t++] = topRight;
                    triangles[t++] = bottomRight;
                    triangles[t++] = topLeft;
                    triangles[t++] = bottomRight;
                    triangles[t++] = bottomLeft;
                }
            }

            var mesh = new Mesh { name = "Earth Sphere", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.normals = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Placeholder until the NASA texture arrives: dark ocean with a 15° grid.</summary>
        static Texture2D BuildGraticule()
        {
            const int width = 512, height = 256;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = "Graticule", wrapModeU = TextureWrapMode.Repeat };
            var pixels = new Color32[width * height];
            var ocean = new Color32(14, 34, 64, 255);
            var line = new Color32(40, 90, 140, 255);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = x % (width / 24) == 0 || y % (height / 12) == 0 ? line : ocean;
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }
    }
}
