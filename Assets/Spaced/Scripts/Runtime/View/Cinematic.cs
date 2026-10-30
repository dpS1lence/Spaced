using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Cinematic launch presentation. While <see cref="Active"/> the satellite dots, orbit lines and most panels are
    /// hidden, and the watched rocket is lit by a key light (pad floodlights / camera fill) and its own engine glow.
    /// </summary>
    public static class Cinematic
    {
        /// <summary>Set each frame by the launch manager: a launch is being watched with cinematic mode on.</summary>
        public static bool Active;

        /// <summary>
        /// Set each frame by the lunar mission manager: a Moon mission is being followed (or the camera is out near
        /// the Moon), so the Earth-orbit satellites aren't drawn and the simulation runs them in coarser steps.
        /// </summary>
        public static bool SatellitesHidden;

        /// <summary>Set each frame by the launch preview (globe view only): the catalogue's dots are left out so its lines stand out.</summary>
        public static bool PreviewHidesSatellites;

        /// <summary>Lights something other than a watched launch (the superhero); called each frame once the lights are cleared.</summary>
        public static System.Action PublishOtherLights;

        static readonly int KeyLight = Shader.PropertyToID("_SpacedKeyLight");
        static readonly int KeyLightColor = Shader.PropertyToID("_SpacedKeyLightColor");
        static readonly int Glow0 = Shader.PropertyToID("_SpacedGlow0");
        static readonly int Glow1 = Shader.PropertyToID("_SpacedGlow1");
        static readonly int GlowColor = Shader.PropertyToID("_SpacedGlowColor");

        /// <summary>Turns every cinematic light off (done each frame before the watched rocket sets its own).</summary>
        public static void ClearLights()
        {
            Shader.SetGlobalVector(KeyLight, Vector4.zero);
            Shader.SetGlobalVector(Glow0, Vector4.zero);
            Shader.SetGlobalVector(Glow1, Vector4.zero);
            Shader.SetGlobalVector(GlowColor, new Vector4(1f, 0.55f, 0.22f, 0f));
        }

        public static void SetKeyLight(Vector3 towardsLight, float intensity, Color color)
        {
            Shader.SetGlobalVector(KeyLight, new Vector4(towardsLight.x, towardsLight.y, towardsLight.z, intensity));
            Shader.SetGlobalVector(KeyLightColor, new Vector4(color.r, color.g, color.b, 0f));
        }

        public static void SetGlows(Vector3 position0, float intensity0, Vector3 position1, float intensity1)
        {
            Shader.SetGlobalVector(Glow0, new Vector4(position0.x, position0.y, position0.z, intensity0));
            Shader.SetGlobalVector(Glow1, new Vector4(position1.x, position1.y, position1.z, intensity1));
        }
    }
}
