using System.Collections;
using System.IO;
using UnityEngine;

namespace Verification
{
    /// <summary>
    /// Plays the game headlessly and writes a PNG of what the TV would show.
    /// </summary>
    /// <remarks>
    /// Exists so a change to the LOOK of the game can be checked without a human pressing
    /// Play and describing the result. Four rounds of art changes were shipped blind and
    /// every one was wrong in a new way; a screenshot the author can actually look at is
    /// the difference between tuning and guessing.
    ///
    /// Same shape as <see cref="EventSystemVerification"/>: driven headlessly by an editor
    /// entry point (<c>SceneCaptureRunner.Capture</c>), writes a file at the project root,
    /// and the caller polls for that file rather than waiting on the process — exiting the
    /// editor from the domain reload after play mode is unreliable in batchmode.
    ///
    /// <b>Must run WITHOUT <c>-nographics</c>.</b> That flag is right for the event
    /// verification, which touches no renderer, and fatal here: with no graphics device
    /// there is nothing to read pixels back from and the capture is a blank image.
    /// </remarks>
    public class SceneCapture : MonoBehaviour
    {
        /// <summary>Where the PNG lands. Project root, beside verification-result.txt.</summary>
        public static string ImagePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "scene-capture.png"));

        /// <summary>
        /// Written after the PNG, and it is what the runner polls for.
        /// </summary>
        /// <remarks>
        /// A separate marker because a PNG appears on disk the moment writing starts. A
        /// runner watching the image alone races the write and reads a truncated file —
        /// which decodes as a corrupt or half-grey image and looks like a rendering bug.
        /// </remarks>
        public static string DoneMarkerPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "scene-capture.done"));

        /// <summary>Seconds of play before the shot. Overridden by -captureDelay.</summary>
        private float _delaySeconds = 4f;

        private int _width = 1280;
        private int _height = 720;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ReadCommandLine();
        }

        /// <summary>
        /// Picks up <c>-captureDelay</c> and <c>-captureSize</c> so the harness can be
        /// re-aimed without a recompile — a recompile in batchmode costs a whole run.
        /// </summary>
        private void ReadCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-captureDelay" && float.TryParse(args[i + 1], out var delay))
                {
                    _delaySeconds = delay;
                }

                if (args[i] == "-captureSize" && int.TryParse(args[i + 1], out var width))
                {
                    _width = width;
                    _height = Mathf.RoundToInt(width * 9f / 16f);
                }
            }
        }

        private IEnumerator Start()
        {
            // Real seconds, not scaled. Nothing here changes Time.timeScale today, but a
            // presentation change that does would otherwise stretch or skip the wait —
            // and a capture harness that reacts to the thing it is measuring is useless.
            yield return new WaitForSecondsRealtime(_delaySeconds);

            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogError("[SceneCapture] No Camera.main — nothing to capture.");
                WriteDoneMarker("no camera");
                yield break;
            }

            var texture = new RenderTexture(_width, _height, 24)
            {
                antiAliasing = 1
            };

            // Assigning targetTexture rather than calling camera.Render(): under URP a
            // direct Render() call is not supported and silently produces nothing. With a
            // target texture assigned the pipeline renders the camera into it as part of
            // its normal frame, so post-processing, fog and the skybox all apply exactly as
            // they would on screen — which is the entire point of capturing at all.
            var previousTarget = camera.targetTexture;
            camera.targetTexture = texture;

            // Several frames, not one. The first frame after re-targeting can land before
            // the pipeline has rendered into the new texture, which reads back as an empty
            // image and looks identical to "the scene is broken".
            for (var i = 0; i < 3; i++) yield return null;

            var image = new Texture2D(_width, _height, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, _width, _height), 0, 0);
            image.Apply();
            RenderTexture.active = previousActive;

            camera.targetTexture = previousTarget;

            File.WriteAllBytes(ImagePath, image.EncodeToPNG());

            Destroy(image);
            texture.Release();
            Destroy(texture);

            Debug.Log($"[SceneCapture] Wrote {ImagePath}");
            WriteDoneMarker("ok");
        }

        private static void WriteDoneMarker(string status)
        {
            File.WriteAllText(DoneMarkerPath, status);
        }
    }
}
