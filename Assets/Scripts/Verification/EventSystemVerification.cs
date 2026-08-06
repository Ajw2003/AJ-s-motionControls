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
            EventManager.Instance?.Publish(new SkipOccurredEvent(1, 10f));
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
            EventManager.Instance?.Publish(new SkipOccurredEvent(1, 10f));
            Check($"subscribers run in subscription order (got \"{orderLog}\", want \"AB\")", orderLog == "AB");

            EventManager.Instance?.UnsubscribeFromAllEvents(firstProbe);
            EventManager.Instance?.UnsubscribeFromAllEvents(secondProbe);

            // 2. A scene-local subscriber, which the next load will destroy while it
            //    is still subscribed.
            var localProbe = new GameObject("SceneLocalProbe").AddComponent<EventProbe>();
            localProbe.Listen();
            EventManager.Instance?.Publish(new SkipOccurredEvent(2, 10f));
            Check("scene-local subscriber receives", localProbe.Received == 1);

            // Relative, not absolute: ScoreManager also subscribes to this event, so the
            // absolute count depends on which managers Bootstrap happened to create.
            var countWithProbe = EventManager.Instance?.GetSubscriptionCount<SkipOccurredEvent>() ?? -1;

            // 3. Bootstrap -> Lobby. This is where persistBetweenScenes = true earns
            //    its keep; with the original false the manager dies here.
            yield return LoadAndSettle(SceneNames.Lobby);
            Check("EventManager survived Bootstrap -> Lobby", EventManager.HasInstance);

            var before = _persistentReceived;
            EventManager.Instance?.Publish(new SkipOccurredEvent(3, 10f));
            Check("persistent subscriber still receives after Lobby load", _persistentReceived == before + 1);

            // 4. Lobby -> Game.
            yield return LoadAndSettle(SceneNames.Game);
            Check("EventManager survived Lobby -> Game", EventManager.HasInstance);

            before = _persistentReceived;
            EventManager.Instance?.Publish(new SkipOccurredEvent(4, 10f));
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

            Application.logMessageReceived -= OnLog;
            Finish();
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
