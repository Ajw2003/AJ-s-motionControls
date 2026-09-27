using System.IO;
using Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RockSkippingEditor
{
    /// <summary>
    /// Makes Assets/Scenes/LakeLouise.unity: a copy of Bootstrap with a LakeLouiseEnvironment
    /// in it. Open that scene and press Play. Batchmode entry point: CreateScene.
    /// </summary>
    /// <remarks>
    /// A copy of Bootstrap rather than a new scene because Bootstrap is what creates the
    /// persistent managers and the AimPreview that builds the lake; the environment only
    /// restyles what AimPreview builds. Not added to the build settings: Bootstrap stays at
    /// index 0 and this scene is reached by opening it (docs/plans/alpine-lake-level.md).
    /// </remarks>
    public static class LakeLouiseSetup
    {
        private const string SourceScene = "Assets/Scenes/Bootstrap.unity";
        private const string TargetScene = "Assets/Scenes/LakeLouise.unity";
        private const string ModelPath = "Assets/Environments/LakeLouise/LakeLouise.fbx";
        private const string LookPath = "Assets/Environments/LakeLouise/LakeLouiseLook.json";

        [MenuItem("RockSkipping/Environments/Create Lake Louise Scene")]
        public static void CreateScene()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var look = AssetDatabase.LoadAssetAtPath<TextAsset>(LookPath);
            if (model == null || look == null)
            {
                Debug.LogError($"[LakeLouiseSetup] Missing {(model == null ? ModelPath : LookPath)}. " +
                               "The FBX is made by tools/art/lake-louise/build_lake_louise.py.");
                return;
            }

            if (!File.Exists(TargetScene) && !AssetDatabase.CopyAsset(SourceScene, TargetScene))
            {
                Debug.LogError($"[LakeLouiseSetup] Could not copy {SourceScene} to {TargetScene}.");
                return;
            }

            var scene = EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Single);

            // Re-running fills in the existing component rather than adding a second one.
            var environment = Object.FindFirstObjectByType<LakeLouiseEnvironment>();
            if (environment == null)
            {
                environment = new GameObject("LakeLouiseEnvironment").AddComponent<LakeLouiseEnvironment>();
            }

            var serialized = new SerializedObject(environment);
            serialized.FindProperty("backdropModel").objectReferenceValue = model;
            serialized.FindProperty("look").objectReferenceValue = look;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[LakeLouiseSetup] {TargetScene} is ready - press Play. The Console should then " +
                      "show '[LakeLouiseEnvironment] Orientation OK'.");
        }
    }

    /// <summary>
    /// Import settings for everything under Assets/Environments/LakeLouise, applied on every
    /// (re)import so a re-exported FBX can never pick up defaults.
    /// </summary>
    /// <remarks>
    /// No materials (LakeLouiseEnvironment assigns its own), no colliders (the stone skips off
    /// the WaterY plane, never off scenery), not readable (nothing reads the vertices), and the
    /// FBX's own flat normals kept: recalculating them would smooth the low-poly facets away.
    /// </remarks>
    public class LakeLouiseModelImport : AssetPostprocessor
    {
        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith("Assets/Environments/LakeLouise/")) return;

            var importer = (ModelImporter)assetImporter;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.addCollider = false;
            importer.isReadable = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.useFileScale = true;
        }
    }
}
