using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Verification;

namespace RockSkippingEditor
{
    // Same shape as RunVerification but leaves Bootstrap enabled and must run without -nographics, or
    // the capture is a blank image. doc-ref 94d2 docs/4-systems/Verification.md
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
