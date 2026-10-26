using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// The Moon at its real position and size (<see cref="Astro.MoonPosition"/>), lit by the Sun so the phase is
    /// right, with the near side facing Earth. Its light on the night side is published by <see cref="CameraRig"/>.
    /// Imagery: NASA LRO / LROC colour map (NASA Scientific Visualization Studio).
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public sealed class MoonRenderer : MonoBehaviour
    {
        public const string TextureUrl = "https://svs.gsfc.nasa.gov/vis/a000000/a004700/a004720/lroc_color_poles_1k.jpg";
        const double RadiusKm = 1737.4;

        SpaceSimulation _sim;

        public void Init(SpaceSimulation sim, Shader shader)
        {
            _sim = sim;
            var material = new Material(shader) { name = "Moon" };
            gameObject.AddComponent<MeshFilter>().sharedMesh = EarthRenderer.BuildSphere(128, 64);
            var meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            transform.localScale = Vector3.one * (float)(RadiusKm / WorldScale.KmPerUnit);
            StartCoroutine(TextureCache.Load(TextureUrl, t => material.SetTexture(ShaderIds.MainTex, t)));
        }

        void LateUpdate()
        {
            if (_sim == null) return;
            double3 moon = Astro.MoonPosition(_sim.Clock.JulianDate);
            // Tidally locked: the sphere's +X (longitude 0 of the map, the centre of the near side) faces Earth,
            // with celestial north up (the Moon's axis is within ~7° of it).
            Vector3 toEarth = Astro.EciToUnity(-math.normalize(moon)).normalized;
            Vector3 forward = Vector3.Cross(toEarth, Vector3.up).normalized;
            transform.SetPositionAndRotation(WorldFrame.ToFar(moon), Quaternion.LookRotation(forward, Vector3.Cross(forward, toEarth)));
        }
    }
}
