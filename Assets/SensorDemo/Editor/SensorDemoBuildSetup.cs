using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SensorSdk.ExampleUnity
{
    // The demo is scene-driven (Sample_Android) and looks up the
    // SensorDemo/CubeFace shader with Shader.Find at runtime, so a player
    // build needs the scene in Build Settings and the shader in the
    // always-included list (nothing else references it and stripping
    // would drop it). Both are enforced here before every build.
    public class SensorDemoBuildSetup : IPreprocessBuildWithReport
    {
        private const string ScenePath = "Assets/Scenes/Sample_Android.unity";
        private const string ShaderPath = "Assets/SensorDemo/Shader/SensorDemoCubeFace.shader";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            EnsureSceneInBuildSettings();
            EnsureShaderAlwaysIncluded();
        }

        private static void EnsureSceneInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (EditorBuildSettingsScene scene in scenes)
            {
                if (scene.path == ScenePath)
                {
                    if (!scene.enabled)
                    {
                        scene.enabled = true;
                        EditorBuildSettings.scenes = scenes.ToArray();
                    }
                    return;
                }
            }
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void EnsureShaderAlwaysIncluded()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
                return;
            var graphicsSettings =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.GraphicsSettings>(
                    "ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings == null)
                return;
            var so = new SerializedObject(graphicsSettings);
            SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray)
                return;
            for (int i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    return;
            }
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
