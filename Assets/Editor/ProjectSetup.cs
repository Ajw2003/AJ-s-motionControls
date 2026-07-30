using System.IO;
using System.Linq;
using Managers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Verification;

namespace RockSkippingEditor
{
    /// <summary>
    /// Creates the four scenes, registers them in build order, and runs the runtime
    /// event-system verification. Batchmode entry points are SetupAll and RunVerification.
    /// </summary>
    public static class ProjectSetup
    {
        private const string SceneFolder = "Assets/Scenes";

        // Bootstrap must be index 0 — it creates the persistent managers.
        private static readonly string[] SceneOrder = { "Bootstrap", "Lobby", "Game", "Results" };

        private static string PathFor(string sceneName) => $"{SceneFolder}/{sceneName}.unity";

        [MenuItem("RockSkipping/Setup/Create Scenes And Build Settings")]
        public static void SetupAll()
        {
            Directory.CreateDirectory(SceneFolder);

            foreach (var sceneName in SceneOrder)
            {
                var path = PathFor(sceneName);
                if (File.Exists(path))
                {
                    Debug.Log($"[ProjectSetup] {path} already exists, leaving it alone.");
                    continue;
                }

                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

                if (sceneName == "Bootstrap")
                {
                    var go = new GameObject("Bootstrap");
                    go.AddComponent<Bootstrap>();
                }

                EditorSceneManager.SaveScene(scene, path);
                Debug.Log($"[ProjectSetup] Created {path}");
            }

            EditorBuildSettings.scenes = SceneOrder
                .Select(n => new EditorBuildSettingsScene(PathFor(n), true))
                .ToArray();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ProjectSetup] Build settings: " + string.Join(" -> ", SceneOrder));
        }

        [MenuItem("RockSkipping/Verify/Run Event System Verification")]
        public static void RunVerification()
        {
            EditorSceneManager.OpenScene(PathFor("Bootstrap"), OpenSceneMode.Single);

            // Disable the real Bootstrap so its automatic Lobby load doesn't race the
            // checks — the verification drives its own scene transitions.
            var bootstrap = Object.FindFirstObjectByType<Bootstrap>();
            if (bootstrap != null) bootstrap.enabled = false;

            new GameObject("EventSystemVerification").AddComponent<EventSystemVerification>();

            var resultPath = EventSystemVerification.ResultPath;
            if (File.Exists(resultPath)) File.Delete(resultPath);

            EditorApplication.EnterPlaymode();

            // The run writes verification-result.txt at the project root and stops play
            // mode. In the GUI the result is also in the Console. Headless callers should
            // poll for that file rather than wait on the process — exiting the editor from
            // the domain reload that follows play mode is unreliable in batchmode.
        }
    }
}
