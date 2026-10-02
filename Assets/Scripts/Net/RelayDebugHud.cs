using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// On-screen readout of live aim and recent throws. This is the M1 acceptance
    /// instrument: it shows the spread across recent throws so "the same gesture
    /// twice lands within ~10%" can be read off the screen rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Scaffolding, not shipping UI. Delete once AimPreview and the real HUD exist.
    /// OnGUI is used deliberately — it needs no Canvas setup and works in any scene.
    /// </remarks>
    public class RelayDebugHud : MonoBehaviour
    {
        // Hidden until F1: it sits top-left, under the lobby's Wi-Fi QR and the match
        // leaderboard, and a party only ever needs those. Still built, so F1 works mid-game.
        [SerializeField] private bool isVisible = false;
        [SerializeField] private int historySize = 6;
        [SerializeField] private KeyCode toggleKey = KeyCode.F1;

        private readonly List<ThrowMessage> _throws = new();
        private AimMessage _latestAim;
        private int _aimFrames;
        private float _aimRateWindowStart;
        private float _aimHz;
        private RelayClient _client;
        private Game.AimPreview _preview;

        // Every dynamic line OnGUI draws, built by RefreshText so OnGUI only draws strings.
        // Rebuilt at most RefreshInterval apart (a debug readout at 4 Hz is plenty), plus at
        // once when a throw lands or the HUD is toggled on. Null preview/aim lines mean "not
        // shown", mirroring the conditions OnGUI used to test.
        private const float RefreshInterval = 0.25f;
        private float _nextRefresh;
        private bool _refreshNow = true;
        private string _relayText, _turnText, _aimHzText, _aimText, _previewText;
        private string _throwsHeaderText, _repeatText, _toggleText;
        private readonly List<string> _throwTexts = new();

        private void OnDestroy()
        {
            if (_client == null) return;
            _client.AimReceived -= OnAim;
            _client.ThrowReceived -= OnThrow;
        }

        /// <summary>
        /// Binds once from Start, after Bootstrap's Awake has built everything. Checks
        /// HasInstance rather than touching Instance, because Instance would *create* a
        /// RelayClient — and a diagnostic that conjures the thing it is diagnosing
        /// would hide the very failure worth seeing.
        /// </summary>
        private void Start()
        {
            // Cached rather than looked up in OnGUI, which runs several times a frame.
            // Optional: absent when Bootstrap's showAimPreview is off.
            _preview = FindFirstObjectByType<Game.AimPreview>();

            if (!RelayClient.HasInstance)
            {
                Debug.LogWarning("[RelayDebugHud] No RelayClient at Start; the HUD will show nothing live.");
                return;
            }

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
            _client.ThrowReceived += OnThrow;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                isVisible = !isVisible;
                _refreshNow = true;
            }

            // Measured, not assumed: if this reads well under 20 the phone or the
            // network is dropping aim frames and the preview will look choppy.
            if (Time.time - _aimRateWindowStart >= 1f)
            {
                _aimHz = _aimFrames / (Time.time - _aimRateWindowStart);
                _aimFrames = 0;
                _aimRateWindowStart = Time.time;
            }

            // Nothing is drawn while hidden, so nothing is formatted either.
            if (!isVisible) return;
            if (!_refreshNow && Time.unscaledTime < _nextRefresh) return;

            _refreshNow = false;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            RefreshText();
        }

        /// <summary>Builds every readout string from current values. Called from Update, throttled.</summary>
        private void RefreshText()
        {
            if (_client == null) return;

            _relayText = $"<b>Relay</b>  {(_client.IsConnected ? "connected" : "offline — is the relay running?")}   " +
                         $"room <b>{(string.IsNullOrEmpty(_client.RoomCode) ? "----" : _client.RoomCode)}</b>";

            var turns = Managers.TurnManager.HasInstance ? Managers.TurnManager.Instance : null;
            var activeName = turns?.ActivePlayer == null ? "nobody"
                : Managers.PlayerNames.HasInstance
                    ? Managers.PlayerNames.Instance.DisplayNameFor(turns.ActivePlayer)
                    : turns.ActivePlayer;
            _turnText = $"<b>Turn</b>   {activeName}   " +
                        $"players {turns?.Players.Count ?? 0}   (press N for next)";

            _aimHzText = $"<b>Aim</b>   {_aimHz:F0} Hz (target 20)";

            if (_latestAim != null)
            {
                // Face angle and heading rather than the raw quaternion: these are the two
                // numbers the physics actually reads, and reading them back through
                // PhoneAttitude is what proves the conversion is doing what it claims.
                // With the phone flat, both should sit near zero.
                var attitude = Game.PhoneAttitude.ToUnity(
                    _latestAim.qw, _latestAim.qx, _latestAim.qy, _latestAim.qz);

                _aimText = $"  seq {_latestAim.seq}   " +
                           $"face {Game.PhoneAttitude.FaceAngleDeg(attitude),6:F1}°   " +
                           $"heading {Game.PhoneAttitude.HeadingDeg(attitude),6:F1}°   " +
                           $"spin {_latestAim.spinRate,5:F0}   " +
                           $"windup {_latestAim.swingSpeed:F2} m/s";
            }
            else
            {
                _aimText = "  no aim frames yet — tap to aim on the phone";
            }

            // The arc is also hidden while a rock is in the air, so a hidden arc with
            // a live signal is expected mid-throw rather than a fault.
            _previewText = _preview == null ? null
                : _preview.HasSignal
                    ? $"  predicted {_preview.PredictedSkips} skips over {_preview.PredictedDistance:F1} m"
                    : "  arc hidden (no recent aim frames)";

            _throwsHeaderText = $"<b>Throws</b> (newest first, {_throws.Count})";

            _throwTexts.Clear();
            foreach (var t in _throws)
            {
                _throwTexts.Add($"  face {FaceAngleOf(t),5:F0}°  " +
                                $"spin {t.spinRate,5:F0}  spd {t.swingSpeed:F2}");
            }

            _repeatText = $"  speed {Spread(_throws, SwingSpeedOf)}   " +
                          $"face {Spread(_throws, FaceAngleSelector)}";

            _toggleText = $"<i>{toggleKey} toggles this readout</i>";
        }

        private void OnAim(AimMessage aim)
        {
            _latestAim = aim;
            _aimFrames++;
        }

        private void OnThrow(ThrowMessage thrown)
        {
            _throws.Insert(0, thrown);
            while (_throws.Count > historySize) _throws.RemoveAt(_throws.Count - 1);
            _refreshNow = true;
            Debug.Log($"[Throw] {thrown}");
        }

        /// <summary>
        /// The face angle a throw was released at, in degrees above horizontal.
        /// </summary>
        /// <remarks>
        /// Recovered through PhoneAttitude rather than read off a field, because there is
        /// no such field any more — the wire carries the rotation itself. Which is the
        /// point: this readout and SkipPhysics get the number from the same function, so
        /// the HUD cannot report an angle the water did not use.
        /// </remarks>
        private static float FaceAngleOf(ThrowMessage thrown) =>
            Game.PhoneAttitude.FaceAngleDeg(
                Game.PhoneAttitude.ToUnity(thrown.qw, thrown.qx, thrown.qy, thrown.qz));

        /// <summary>Reads the swing speed off a throw. Cached so OnGUI allocates no delegate.</summary>
        private static readonly Func<ThrowMessage, float> SwingSpeedOf = t => t.swingSpeed;

        /// <summary>Reads the face angle off a throw. Cached so OnGUI allocates no delegate.</summary>
        private static readonly Func<ThrowMessage, float> FaceAngleSelector = FaceAngleOf;

        /// <summary>Spread as a percentage of the mean — the repeatability number.</summary>
        /// <remarks>
        /// Takes the list and a selector rather than an IEnumerable&lt;float&gt; because this
        /// runs inside OnGUI, twice a frame, for as long as the HUD is up. The LINQ form
        /// allocated an iterator, a closure and a backing List per call to produce three
        /// numbers that one pass already has in hand. The selectors are cached statics for
        /// the same reason — a method group or lambda written at the call site would
        /// allocate a fresh delegate every frame.
        /// </remarks>
        private static string Spread(List<ThrowMessage> throws, Func<ThrowMessage, float> selector)
        {
            if (throws.Count < 2) return "n/a";

            var min = float.MaxValue;
            var max = float.MinValue;
            var sum = 0f;

            foreach (var throwMessage in throws)
            {
                var value = selector(throwMessage);
                if (value < min) min = value;
                if (value > max) max = value;
                sum += value;
            }

            var mean = sum / throws.Count;
            if (Mathf.Abs(mean) < 0.0001f) return "n/a";

            return $"{(max - min) / Mathf.Abs(mean) * 100f:F0}%";
        }

        private void OnGUI()
        {
            if (!isVisible) return;

            GUILayout.BeginArea(new Rect(10, 10, 460, 460), GUI.skin.box);

            var rich = new GUIStyle(GUI.skin.label) { richText = true };

            if (_client == null)
            {
                GUILayout.Label("<b>Relay</b>  no RelayClient in the scene", rich);
                GUILayout.Label("  Play from the Bootstrap scene — it creates the managers.");
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label(_relayText, rich);
            GUILayout.Label(_turnText, rich);

            GUILayout.Space(6);
            GUILayout.Label(_aimHzText, rich);
            GUILayout.Label(_aimText);

            if (_previewText != null) GUILayout.Label(_previewText);

            GUILayout.Space(6);
            GUILayout.Label(_throwsHeaderText, rich);

            if (_throwTexts.Count == 0)
            {
                GUILayout.Label("  none yet");
            }
            else
            {
                foreach (var text in _throwTexts) GUILayout.Label(text);

                GUILayout.Space(6);
                GUILayout.Label("<b>Repeatability</b> (spread across the above; want under 10%)",
                                rich);
                GUILayout.Label(_repeatText);
            }

            GUILayout.Space(6);
            GUILayout.Label(_toggleText, rich);

            GUILayout.EndArea();
        }
    }
}
