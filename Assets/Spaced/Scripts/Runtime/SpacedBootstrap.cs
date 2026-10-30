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
            sim.AutoLoadCatalog = false; // the main menu picks a new game or a save

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
            var preview = new GameObject("Launch Preview").AddComponent<LaunchPreviewRenderer>();
            preview.Init(sim, rig, FindShader("Spaced/UnlitColor"));

            var materials = new MaterialLibrary(FindShader("Spaced/SpacecraftLit"));
            var fx = new Fx(FindShader("Spaced/Plume"), FindShader("Spaced/Smoke"));
            gameObject.AddComponent<SatelliteModelRenderer>().Init(sim, rig, materials, SatelliteModels);

            var lunar = gameObject.AddComponent<LunarMissionManager>();
            lunar.Init(sim, rig, satellites, materials, fx, FindShader("Spaced/UnlitColor"));
            var launches = gameObject.AddComponent<LaunchManager>();
            launches.Init(sim, rig, satellites, materials, fx, FindShader("Spaced/Ground"), lunar);
            var weather = gameObject.AddComponent<WeatherService>();
            weather.Init(sim, earth);
            launches.Weather = weather;

            var fleet = gameObject.AddComponent<FleetManager>();
            fleet.Init(sim);
            fleet.CloudCover = weather.CloudCover;

            hud.Init(sim, rig, launches, lunar, satellites, fleet);
            hud.Preview = preview;

            var career = gameObject.AddComponent<CareerManager>();
            career.Init(sim, launches, lunar, fleet, rig);
            var session = gameObject.AddComponent<GameSession>();
            session.Init(sim, rig, launches, lunar, fleet, career, hud);
            hud.AttachCareer(career, session);
            hud.Weather = weather;

            var audio = gameObject.AddComponent<SpacedAudio>();
            audio.Init(sim, rig, launches, fleet, hud.IsPointerOverUI);
            hud.Audio = audio;

            var hero = new GameObject("Superhero Mode").AddComponent<HeroController>();
            hero.Init(sim, rig, launches, lunar, materials, audio, hud.IsPointerOverUI);
            hud.Hero = hero;
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
