using System.Collections.Generic;
using UnityEngine;

namespace Spaced
{
    /// <summary>One shared, instancing-enabled material per <see cref="Surface"/>.</summary>
    public sealed class MaterialLibrary
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int NormalMap = Shader.PropertyToID("_NormalMap");
        static readonly int NormalStrength = Shader.PropertyToID("_NormalStrength");
        static readonly int Metallic = Shader.PropertyToID("_Metallic");
        static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        static readonly int NightEmission = Shader.PropertyToID("_NightEmission");

        readonly Dictionary<Surface, Material> _materials = new Dictionary<Surface, Material>();

        public Shader Shader { get; }

        public MaterialLibrary(Shader shader)
        {
            Shader = shader;
            var foil = ProceduralTextures.FoilNormal();
            var cells = ProceduralTextures.SolarCells();
            var concrete = ProceduralTextures.Concrete();

            Add(Surface.WhitePaint, new Color(0.88f, 0.88f, 0.86f), 0f, 0.55f);
            Add(Surface.BoosterPaint, Color.white, 0f, 1f, ProceduralTextures.BoosterPaint());
            Add(Surface.BlackComposite, new Color(0.035f, 0.035f, 0.04f), 0f, 0.45f);
            Add(Surface.Aluminium, new Color(0.80f, 0.80f, 0.82f), 1f, 0.6f);
            Add(Surface.Titanium, new Color(0.42f, 0.40f, 0.38f), 1f, 0.35f);
            Add(Surface.EngineBell, new Color(0.13f, 0.12f, 0.11f), 1f, 0.4f);
            Add(Surface.GoldFoil, new Color(1.0f, 0.70f, 0.28f), 1f, 0.6f, null, foil, 0.8f);
            Add(Surface.SilverFoil, new Color(0.86f, 0.86f, 0.9f), 1f, 0.6f, null, foil, 0.8f);
            Add(Surface.SolarCells, Color.white, 0.25f, 0.95f, cells);
            Add(Surface.DarkPanel, new Color(0.10f, 0.10f, 0.12f), 0.6f, 0.5f);
            Add(Surface.Radiator, new Color(0.93f, 0.93f, 0.93f), 0f, 0.75f);
            Add(Surface.Concrete, Color.white, 0f, 1f, concrete);
            Add(Surface.Steel, new Color(0.52f, 0.53f, 0.55f), 0.8f, 0.35f);
            Add(Surface.DeckSteel, Color.white, 0.6f, 1f, ProceduralTextures.DroneshipDeck());
            Add(Surface.HullPaint, new Color(0.20f, 0.22f, 0.25f), 0.2f, 0.4f);
            Add(Surface.Glass, new Color(0.05f, 0.07f, 0.09f), 0.9f, 0.95f);
            var light = Add(Surface.WarningLight, new Color(0.3f, 0.05f, 0.05f), 0f, 0.5f);
            light.SetVector(Emission, new Vector4(6f, 0.4f, 0.2f, 1f)); // HDR, so no gamma conversion
            light.SetFloat(NightEmission, 1f);
        }

        public Material this[Surface surface] => _materials[surface];

        public Material[] For(SurfaceMesh mesh)
        {
            var result = new Material[mesh.Surfaces.Length];
            for (int i = 0; i < result.Length; i++) result[i] = _materials[mesh.Surfaces[i]];
            return result;
        }

        Material Add(Surface surface, Color color, float metallic, float smoothness,
            Texture2D baseMap = null, Texture2D normalMap = null, float normalStrength = 1f)
        {
            var material = new Material(Shader) { name = surface.ToString(), enableInstancing = true };
            material.SetColor(BaseColor, color); // Unity converts material colours to linear itself
            material.SetFloat(Metallic, metallic);
            material.SetFloat(Smoothness, smoothness);
            if (baseMap != null) material.SetTexture(BaseMap, baseMap);
            if (normalMap != null)
            {
                material.SetTexture(NormalMap, normalMap);
                material.SetFloat(NormalStrength, normalStrength);
            }
            _materials[surface] = material;
            return material;
        }
    }
}
