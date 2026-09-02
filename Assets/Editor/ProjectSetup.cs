// Deliberately no `using System;` — it makes the bare `Object` used below ambiguous
// between System.Object and UnityEngine.Object. System types are qualified instead.
using System.IO;
using System.Linq;
using System.Reflection;
using Managers;
using Unity.CodeEditor;
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

        /// <summary>
        /// Writes the .sln and .csproj files Rider and Visual Studio need. These are
        /// generated artifacts, not source — they are gitignored, and every machine
        /// regenerates its own. Requires com.unity.ide.rider or com.unity.ide.visualstudio.
        /// </summary>
        [MenuItem("RockSkipping/Setup/Regenerate Solution And Project Files")]
        public static void GenerateProjectFiles()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            Debug.Log($"[ProjectSetup] External script editor: '{CodeEditor.CurrentEditorInstallation}'");

            try
            {
                CodeEditor.CurrentEditor.SyncAll();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ProjectSetup] CodeEditor.SyncAll failed: {e.Message}");
            }

            // Fallback for when no external editor is configured yet — SyncAll can
            // no-op in that case, but the built-in generator still works.
            if (Directory.GetFiles(projectRoot, "*.sln").Length == 0)
            {
                var syncVs = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.SyncVS");
                syncVs?.GetMethod("SyncSolution", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, null);
            }

            var solutions = Directory.GetFiles(projectRoot, "*.sln").Select(Path.GetFileName).ToArray();
            var projects = Directory.GetFiles(projectRoot, "*.csproj").Length;

            Debug.Log(solutions.Length > 0
                ? $"[ProjectSetup] Generated {string.Join(", ", solutions)} and {projects} .csproj file(s)."
                : "[ProjectSetup] No .sln produced. Set Edit > Preferences > External Tools > External Script Editor first.");
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

        /// <summary>
        /// Measure what the current tuning actually does. Writes skip-curve.txt at the
        /// project root; see <see cref="SkipCurve"/> for how to read it.
        /// </summary>
        /// <remarks>
        /// No play mode and no scene: SkipPhysics is pure and the tuning comes from
        /// Resources, so this runs and finishes inside the menu click. That is the point —
        /// retuning should cost a click, not a session.
        /// </remarks>
        [MenuItem("RockSkipping/Verify/Print Skip Curve")]
        public static void PrintSkipCurve() => SkipCurve.Run();
    }
}
