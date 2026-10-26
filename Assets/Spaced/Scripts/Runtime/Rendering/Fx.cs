using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>Shared materials and builders for plumes and particle effects (local scene).</summary>
    public sealed class Fx
    {
        static readonly int Length = Shader.PropertyToID("_Length");
        static readonly int Radius = Shader.PropertyToID("_Radius");
        static readonly int Expansion = Shader.PropertyToID("_Expansion");
        static readonly int Intensity = Shader.PropertyToID("_Intensity");
        static readonly int Glow = Shader.PropertyToID("_Glow");

        public readonly Material Plume;
        public readonly Material Smoke;
        public readonly Material FlameSmoke;
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        public Fx(Shader plume, Shader smoke)
        {
            Plume = new Material(plume) { name = "Plume" };
            var puff = ProceduralTextures.SmokePuff();
            Smoke = new Material(smoke) { name = "Smoke", mainTexture = puff };
            FlameSmoke = new Material(smoke) { name = "Flame Smoke", mainTexture = puff };
            FlameSmoke.SetFloat(Glow, 0f); // flame light now comes from the engine glow light
        }

        public static void SetLayer(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = WorldFrame.LocalLayer;
        }

        public MeshRenderer CreatePlume(Transform parent, Vector3 localPosition, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.layer = WorldFrame.LocalLayer;
            go.AddComponent<MeshFilter>().sharedMesh = RocketModels.Plume().Mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = Plume;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;
            return meshRenderer;
        }

        /// <summary>Shapes a plume for the ambient air density (kg/m³) and throttle; hides it when off.</summary>
        public void UpdatePlume(MeshRenderer plume, float nozzleRadius, double airDensity, double throttle, float seaLevelLength)
        {
            bool on = throttle > 0.01;
            plume.enabled = on;
            if (!on) return;
            // Pressure falls off exponentially; the plume shape tracks it on a compressed scale.
            float expansion = Mathf.Clamp01(1f - Mathf.Pow((float)(airDensity / 1.225), 0.25f));
            _block.SetFloat(Length, Mathf.Lerp(seaLevelLength, seaLevelLength * 5f, expansion) * (0.75f + 0.25f * (float)throttle));
            _block.SetFloat(Radius, nozzleRadius);
            _block.SetFloat(Expansion, expansion);
            _block.SetFloat(Intensity, (float)throttle);
            plume.SetPropertyBlock(_block);
        }

        /// <summary>
        /// Exhaust/steam particles simulated in <paramref name="space"/> (an Earth-fixed frame), so the cloud stays
        /// put in the air while the floating origin moves around.
        /// </summary>
        public ParticleSystem CreateSmoke(Transform parent, Transform space, Material material, float startSize, float lifetime,
            float speed, int maxParticles, Color color, float growth)
        {
            var go = new GameObject("Smoke");
            go.transform.SetParent(parent, false);
            go.layer = WorldFrame.LocalLayer;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.duration = 10f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.5f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(startSize * 0.7f, startSize * 1.3f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = color;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = space;
            main.scalingMode = ParticleSystemScalingMode.Shape;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 12f;
            shape.radius = startSize * 0.2f;
            shape.rotation = new Vector3(90f, 0f, 0f); // emit down the exhaust (-Y)

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(0.3f, growth * 0.6f), new Keyframe(1f, growth)));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.85f, 0.6f), 0f), new GradientColorKey(Color.white, 0.15f), new GradientColorKey(new Color(0.85f, 0.85f, 0.85f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.9f, 0.04f), new GradientAlphaKey(0.45f, 0.5f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = speed * 0.08f;
            limit.dampen = 0.15f;

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = startSize * 0.25f;
            noise.frequency = 0.08f;
            noise.scrollSpeed = 0.15f;
            noise.octaveCount = 2;

            var rotation = ps.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.2f, 0.2f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.maxParticleSize = 4f;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            ps.Play();
            return ps;
        }

        /// <summary>
        /// A particle system that only emits when told to (<see cref="ParticleSystem.Emit(int)"/>): cold-gas puffs,
        /// the condensation collar at max-Q. <paramref name="space"/> null simulates in the emitter's own frame.
        /// </summary>
        public ParticleSystem CreateBurst(Transform parent, Vector3 localPosition, Transform space, float size, float lifetime,
            float speed, int maxParticles, Color color, float growth, float shapeRadius)
        {
            var go = new GameObject("Burst");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.layer = WorldFrame.LocalLayer;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifetime * 0.6f, lifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.4f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size * 1.2f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = color;
            main.maxParticles = maxParticles;
            main.simulationSpace = space != null ? ParticleSystemSimulationSpace.Custom : ParticleSystemSimulationSpace.Local;
            if (space != null) main.customSimulationSpace = space;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = shapeRadius;
            shape.radiusThickness = 0.1f;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, growth)));

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0.4f, 0.4f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = speed * 0.2f;
            limit.dampen = 0.3f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Smoke;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.maxParticleSize = 3f;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ps.Play();
            return ps;
        }

        /// <summary>Sets an emission rate and makes particle time follow sim time (capped so warp stays watchable).</summary>
        public static void Drive(ParticleSystem ps, float rate, double warp)
        {
            var emission = ps.emission;
            emission.rateOverTime = rate;
            var main = ps.main;
            main.simulationSpeed = Mathf.Clamp((float)System.Math.Abs(warp), 0.05f, 30f);
        }
    }
}
