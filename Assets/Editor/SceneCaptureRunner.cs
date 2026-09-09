using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Verification;

namespace RockSkippingEditor
{
    /// <summary>
    /// Batchmode entry point for <see cref="SceneCapture"/> — plays the game and writes a
    /// PNG of the TV screen, so a change to the look can be checked without a human.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <c>ProjectSetup.RunVerification</c>, which is the
    /// pattern in this repo that is known to work headlessly: open Bootstrap, add a driver
    /// component, enter play mode, and let the CALLER poll for the output file. Exiting the
    /// editor from the domain reload that follows play mode is unreliable in batchmode, so
    /// the runner script kills the process once the marker appears.
    ///
    /// <b>Run without <c>-nographics</c>.</b> The event verification uses that flag because
    /// it renders nothing; here it produces a blank image, because there is no graphics
    /// device to read pixels back from.
    ///
    /// Unlike RunVerification this leaves Bootstrap ENABLED. The whole point is to
    /// photograph the game as a player sees it, which means the real boot path, the real
    /// Lobby load and the real managers — a disabled Bootstrap would capture an empty scene
    /// and prove nothing about the thing being changed.
    /// </remarks>
    public static class SceneCaptureRunner
    {
        [MenuItem("RockSkipping/Verify/Capture Scene Screenshot")]
        public static void Capture()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Bootstrap.unity", OpenSceneMode.Single);

            // Cleared first so a caller polling for the marker cannot be satisfied by the
            // previous run's leftovers — which would hand back a stale screenshot of the
            // very bug the current run was meant to check.
            if (File.Exists(SceneCapture.ImagePath)) File.Delete(SceneCapture.ImagePath);
            if (File.Exists(SceneCapture.DoneMarkerPath)) File.Delete(SceneCapture.DoneMarkerPath);

            new GameObject("SceneCapture").AddComponent<SceneCapture>();

            EditorApplication.EnterPlaymode();
        }
    }
}
