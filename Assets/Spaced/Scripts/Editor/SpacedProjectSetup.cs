using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Spaced.Editor
{
    /// <summary>
    /// One-click project setup. The project started from Unity's 2D template, whose URP asset uses the
    /// 2D Renderer; Spaced is 3D, so this adds a Universal (3D) Renderer and makes it the default.
    /// It then creates the main scene with a <see cref="SpacedBootstrap"/>.
    /// </summary>
    public static class SpacedProjectSetup
    {
        const string ScenePath = "Assets/Scenes/Spaced.unity";
        const string RendererPath = "Assets/Settings/UniversalRenderer.asset";
        const string UrpPackagePath = "Packages/com.unity.render-pipelines.universal";

        [MenuItem("Spaced/Set Up Project", priority = 0)]
        public static void SetUpProject()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            int converted = UseUniversalRenderer();
            NameLocalLayer();
            CreateMainScene();
            Debug.Log($"[Spaced] Setup complete. {converted} URP asset(s) switched to the Universal Renderer; scene at {ScenePath}. Press Play.");
        }

        [MenuItem("Spaced/Open Main Scene", priority = 1)]
        public static void OpenMainScene()
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                SetUpProject();
                return;
            }
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("Spaced/Clear Downloaded Data Cache", priority = 20)]
        public static void ClearCache()
        {
            foreach (var folder in new[] { "celestrak", "textures" })
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, folder);
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            }
            Debug.Log($"[Spaced] Cleared cache in {Application.persistentDataPath}");
        }

        static int UseUniversalRenderer()
        {
            var pipelines = new HashSet<UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset defaultAsset) pipelines.Add(defaultAsset);
            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset levelAsset) pipelines.Add(levelAsset);

            if (pipelines.Count == 0)
            {
                Debug.LogError("[Spaced] No URP asset is assigned in Graphics or Quality settings.");
                return 0;
            }

            int converted = 0;
            foreach (var pipeline in pipelines)
            {
                var serialized = new SerializedObject(pipeline);
                var list = serialized.FindProperty("m_RendererDataList");
                if (list.arraySize > 0 && list.GetArrayElementAtIndex(0).objectReferenceValue is UniversalRendererData) continue;

                // Keep the existing renderers; the 3D one goes in front as the default.
                list.InsertArrayElementAtIndex(0);
                list.GetArrayElementAtIndex(0).objectReferenceValue = GetOrCreateUniversalRenderer();
                serialized.FindProperty("m_DefaultRendererIndex").intValue = 0;
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(pipeline);
                converted++;
            }

            AssetDatabase.SaveAssets();
            return converted;
        }

        /// <summary>Names the layer the metre-scale scene (rockets, pads, models) renders on, for readability.</summary>
        static void NameLocalLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var slot = layers.GetArrayElementAtIndex(WorldFrame.LocalLayer);
            if (string.IsNullOrEmpty(slot.stringValue))
            {
                slot.stringValue = "SpacedLocal";
                tagManager.ApplyModifiedProperties();
            }
            else if (slot.stringValue != "SpacedLocal")
            {
                Debug.LogWarning($"[Spaced] Layer {WorldFrame.LocalLayer} is already used as '{slot.stringValue}'; Spaced renders its local scene on it.");
            }
        }

        static UniversalRendererData GetOrCreateUniversalRenderer()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (data != null) return data;

            // Mirrors URP's own "Create > Rendering > URP Universal Renderer" menu.
            data = ScriptableObject.CreateInstance<UniversalRendererData>();
            data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(UrpPackagePath + "/Runtime/Data/PostProcessData.asset");
            AssetDatabase.CreateAsset(data, RendererPath);
            ResourceReloader.ReloadAllNullIn(data, UrpPackagePath);
            return data;
        }

        static void CreateMainScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Spaced").AddComponent<SpacedBootstrap>();
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            SceneManager.SetActiveScene(scene);
        }
    }
}
