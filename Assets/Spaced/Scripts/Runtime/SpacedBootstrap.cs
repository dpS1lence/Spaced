using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Builds the whole game at runtime: simulation, Earth, sky, satellites and their models, launch sites and
    /// rockets, the camera rig and the HUD. Drop it on an empty GameObject in an empty scene
    /// (Spaced → Set Up Project does this for you).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class SpacedBootstrap : MonoBehaviour
    {
        [Tooltip("CelesTrak GP groups to load.")]
        public string[] CelestrakGroups = SpaceSimulation.DefaultGroups;

        [Header("Optional texture overrides (otherwise NASA imagery is downloaded and cached)")]
        public Texture2D EarthDayTexture;
        public Texture2D EarthNightTexture;

        [Header("Optional satellite models (Asset Store, NASA 3D Resources, …) replacing the procedural ones")]
        public SatelliteModelOverride[] SatelliteModels = new SatelliteModelOverride[0];

        void Awake()
        {
            var sim = gameObject.AddComponent<SpaceSimulation>();
            sim.CelestrakGroups = CelestrakGroups;

            var hud = gameObject.AddComponent<SpacedHud>();
            var satellites = gameObject.AddComponent<SatelliteRenderer>();
            var rig = new GameObject("Camera Rig").AddComponent<CameraRig>();
            rig.Init(sim, satellites, hud.IsPointerOverUI);
            satellites.Init(sim, rig, FindShader("Spaced/Satellites"));

            var earth = new GameObject("Earth").AddComponent<EarthRenderer>();
            earth.Init(sim, rig, FindShader("Spaced/Earth"), FindShader("Spaced/Clouds"), FindShader("Spaced/Atmosphere"),
                EarthDayTexture, EarthNightTexture);
            // Streams Sentinel-2 imagery and terrain for the whole planet as you zoom in.
            new GameObject("Globe Tiles").AddComponent<GlobeTiles>().Init(sim, rig, FindShader("Spaced/EarthTile"));
            Starfield.Create(FindShader("Spaced/Stars"));
            Starfield.CreateSun(FindShader("Spaced/Sun"));
            new GameObject("Moon").AddComponent<MoonRenderer>().Init(sim, FindShader("Spaced/Moon"));

            var orbitPath = new GameObject("Selected Orbit", typeof(LineRenderer)).AddComponent<OrbitPathRenderer>();
            orbitPath.Init(sim, FindShader("Spaced/UnlitColor"), rig.Far);
            var groundTrack = new GameObject("Ground Track", typeof(LineRenderer)).AddComponent<GroundTrackRenderer>();
            groundTrack.Init(sim, rig, FindShader("Spaced/UnlitColor"));

            var materials = new MaterialLibrary(FindShader("Spaced/SpacecraftLit"));
            var fx = new Fx(FindShader("Spaced/Plume"), FindShader("Spaced/Smoke"));
            gameObject.AddComponent<SatelliteModelRenderer>().Init(sim, rig, materials, SatelliteModels);

            var launches = gameObject.AddComponent<LaunchManager>();
            launches.Init(sim, rig, satellites, materials, fx, FindShader("Spaced/Ground"));

            var fleet = gameObject.AddComponent<FleetManager>();
            fleet.Init(sim);

            hud.Init(sim, rig, launches, satellites, fleet);

            var audio = gameObject.AddComponent<SpacedAudio>();
            audio.Init(sim, rig, launches, fleet, hud.IsPointerOverUI);
            hud.Audio = audio;
        }

        static Shader FindShader(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                Debug.LogError($"[Spaced] Shader '{shaderName}' not found. It should live in Assets/Spaced/Resources/Shaders.");
            return shader;
        }
    }
}
