using System.Collections;
using System.IO;
using System.Text;
using EventSystems;
using EventTypes.SkipEvents;
using UnityEngine;
using UnityEngine.SceneManagement;
using Utils;

namespace Verification
{
    /// <summary>
    /// Proves the multi-scene fixes to <see cref="EventManager"/> actually hold at
    /// runtime: the manager survives scene loads, persistent subscribers keep
    /// receiving, and subscribers destroyed by a transition are pruned silently
    /// instead of throwing MissingReferenceException on every publish.
    /// </summary>
    /// <remarks>
    /// Run headlessly via <c>ProjectSetup.RunVerification</c>, or drop this on a
    /// GameObject in the Bootstrap scene and press Play. Remove before shipping.
    /// </remarks>
    public class EventSystemVerification : MonoBehaviour
    {
        public static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "verification-result.txt"));

        private readonly StringBuilder _report = new();
        private bool _passed = true;
        private int _persistentReceived;
        private int _errorsLogged;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private IEnumerator Start()
        {
            Application.logMessageReceived += OnLog;

            // 1. Baseline: the hub delivers at all.
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, _ => _persistentReceived++);
            EventManager.Instance?.Publish(new SkipOccurredEvent(1, 10f, Vector3.zero));
            Check("publish/subscribe delivers", _persistentReceived == 1);

            // 1b. Delivery order. Bootstrap creates ScoreManager before TurnManager so a
            //     throw's points are banked before the match-end handler reads the
            //     standings, which only holds if publish runs subscribers in the order
            //     they subscribed. It ran backwards once: the match ended and broadcast
            //     before the final throw was scored, so the phones showed a total one
            //     throw short of the host's. Unsubscribed immediately so the subscription
            //     count captured below is unaffected.
            var orderLog = string.Empty;
            var firstProbe = new object();
            var secondProbe = new object();

            EventManager.Instance?.Subscribe<SkipOccurredEvent>(firstProbe, _ => orderLog += "A");
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(secondProbe, _ => orderLog += "B");
            EventManager.Instance?.Publish(new SkipOccurredEvent(1, 10f, Vector3.zero));
            Check($"subscribers run in subscription order (got \"{orderLog}\", want \"AB\")", orderLog == "AB");

            EventManager.Instance?.UnsubscribeFromAllEvents(firstProbe);
            EventManager.Instance?.UnsubscribeFromAllEvents(secondProbe);

            // 2. A scene-local subscriber, which the next load will destroy while it
            //    is still subscribed.
            var localProbe = new GameObject("SceneLocalProbe").AddComponent<EventProbe>();
            localProbe.Listen();
            EventManager.Instance?.Publish(new SkipOccurredEvent(2, 10f, Vector3.zero));
            Check("scene-local subscriber receives", localProbe.Received == 1);

            // Relative, not absolute: ScoreManager also subscribes to this event, so the
            // absolute count depends on which managers Bootstrap happened to create.
            var countWithProbe = EventManager.Instance?.GetSubscriptionCount<SkipOccurredEvent>() ?? -1;

            // 3. Bootstrap -> Lobby. This is where persistBetweenScenes = true earns
            //    its keep; with the original false the manager dies here.
            yield return LoadAndSettle(SceneNames.Lobby);
            Check("EventManager survived Bootstrap -> Lobby", EventManager.HasInstance);

            var before = _persistentReceived;
            EventManager.Instance?.Publish(new SkipOccurredEvent(3, 10f, Vector3.zero));
            Check("persistent subscriber still receives after Lobby load", _persistentReceived == before + 1);

            // 4. Lobby -> Game.
            yield return LoadAndSettle(SceneNames.Game);
            Check("EventManager survived Lobby -> Game", EventManager.HasInstance);

            before = _persistentReceived;
            EventManager.Instance?.Publish(new SkipOccurredEvent(4, 10f, Vector3.zero));
            Check("persistent subscriber still receives after Game load", _persistentReceived == before + 1);

            // 5. The probe from step 2 was destroyed by the Lobby load while still
            //    subscribed, and has been published to twice since. Without the
            //    UnityEngine.Object null check in EventManager.IsDead it is never
            //    recognised as dead, so it stays in the list and throws
            //    MissingReferenceException into the per-subscriber catch every publish.
            //    Assert on the subscription list directly rather than on log noise.
            var countAfter = EventManager.Instance?.GetSubscriptionCount<SkipOccurredEvent>() ?? -1;
            Check($"destroyed subscriber pruned ({countWithProbe} -> {countAfter})", countAfter == countWithProbe - 1);
            Check("no publish errors logged", _errorsLogged == 0);

            CheckPhoneAttitude();

            Application.logMessageReceived -= OnLog;
            Finish();
        }

        // Checks PhoneAttitude.ToUnity on all three axes headlessly, because a sign error silently
        // mirrors one axis. doc-ref 5eae docs/4-systems/Verification.md
        private void CheckPhoneAttitude()
        {
            // A rotation of `deg` about one of the device's own axes, built the way the
            // phone builds it — see quaternionFromRotationVector in sensors.js.
            static Quaternion Device(float deg, Vector3 axis)
            {
                var half = deg * Mathf.Deg2Rad * 0.5f;
                var s = Mathf.Sin(half);
                return Game.PhoneAttitude.ToUnity(
                    Mathf.Cos(half), axis.x * s, axis.y * s, axis.z * s);
            }

            var flat = Game.PhoneAttitude.ToUnity(1f, 0f, 0f, 0f);
            Check("a phone at the tap pose is a flat stone facing forward",
                Vector3.Angle(flat * Vector3.forward, Vector3.forward) < 0.01f &&
                Vector3.Angle(flat * Vector3.up, Vector3.up) < 0.01f);

            // Tilting the top edge up is +x in the device frame, and must raise the
            // stone's nose by the same angle. This is the face angle the bounce reads.
            var noseUp = Device(10f, Vector3.right);
            Check("tilting the top edge up 10 degrees raises the face 10 degrees",
                Mathf.Abs(Game.PhoneAttitude.FaceAngleDeg(noseUp) - 10f) < 0.01f);

            var noseDown = Device(-10f, Vector3.right);
            Check("tilting the top edge down 10 degrees lowers the face 10 degrees",
                Mathf.Abs(Game.PhoneAttitude.FaceAngleDeg(noseDown) + 10f) < 0.01f);

            // Turning your body left is +z in the device frame while the phone is flat,
            // and must aim left — a negative heading about Unity's up.
            var turnedLeft = Device(25f, Vector3.forward);
            Check("turning the body left 25 degrees aims 25 degrees left",
                Mathf.Abs(Game.PhoneAttitude.HeadingDeg(turnedLeft) + 25f) < 0.01f);

            // Aiming must not leak into the face angle, or turning to line a shot up
            // would quietly change how well it skips.
            Check("turning to aim does not change the face angle",
                Mathf.Abs(Game.PhoneAttitude.FaceAngleDeg(turnedLeft)) < 0.01f);

            // Rolling is +y in the device frame: the right edge drops, so the stone's own
            // up vector leans right. Cosmetic, but it must not leak into the other two.
            var rolledRight = Device(20f, Vector3.up);
            Check("rolling the phone does not change the face angle or the heading",
                Mathf.Abs(Game.PhoneAttitude.FaceAngleDeg(rolledRight)) < 0.01f &&
                Mathf.Abs(Game.PhoneAttitude.HeadingDeg(rolledRight)) < 0.01f);
            Check("rolling the phone right rolls the stone right",
                (rolledRight * Vector3.up).x > 0f);
        }

        private static IEnumerator LoadAndSettle(string sceneName)
        {
            var op = SceneManager.LoadSceneAsync(sceneName);
            while (op != null && !op.isDone) yield return null;
            yield return null; // let destruction flush before the next publish
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            // Scoped to failures this test is actually about. The editor logs unrelated
            // errors of its own in batchmode (Unity's SearchDatabase indexing throws on
            // startup), and counting those would fail the run for no reason.
            if (condition.Contains("Error publishing event") || condition.Contains("MissingReference"))
                _errorsLogged++;
        }

        private void Check(string label, bool ok)
        {
            if (!ok) _passed = false;
            _report.AppendLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
        }

        private void Finish()
        {
            var result = $"{(_passed ? "PASS" : "FAIL")}\n{_report}";
            Debug.Log($"[EventSystemVerification]\n{result}");

            try { File.WriteAllText(ResultPath, result); }
            catch (IOException e) { Debug.LogWarning($"Could not write verification result: {e.Message}"); }

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
