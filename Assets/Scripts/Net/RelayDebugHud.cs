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

        private void OnDestroy()
        {
            if (_client == null) return;
            _client.AimReceived -= OnAim;
            _client.ThrowReceived -= OnThrow;
        }

        /// <summary>
        /// Binds whenever the client shows up, in either order. Checks HasInstance
        /// rather than touching Instance, because Instance would *create* a
        /// RelayClient — and a diagnostic that conjures the thing it is diagnosing
        /// would hide the very failure worth seeing.
        /// </summary>
        private void EnsureBound()
        {
            // Cached rather than looked up in OnGUI, which runs several times a frame.
            if (_preview == null) _preview = FindFirstObjectByType<Game.AimPreview>();

            if (_client != null || !RelayClient.HasInstance) return;

            _client = RelayClient.Instance;
            _client.AimReceived += OnAim;
            _client.ThrowReceived += OnThrow;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) isVisible = !isVisible;
            EnsureBound();

            // Measured, not assumed: if this reads well under 20 the phone or the
            // network is dropping aim frames and the preview will look choppy.
            if (Time.time - _aimRateWindowStart >= 1f)
            {
                _aimHz = _aimFrames / (Time.time - _aimRateWindowStart);
                _aimFrames = 0;
                _aimRateWindowStart = Time.time;
            }
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

            GUILayout.Label($"<b>Relay</b>  {(_client.IsConnected ? "connected" : "offline — is the relay running?")}   " +
                            $"room <b>{(string.IsNullOrEmpty(_client.RoomCode) ? "----" : _client.RoomCode)}</b>", rich);

            var turns = Managers.TurnManager.HasInstance ? Managers.TurnManager.Instance : null;
            var activeName = turns?.ActivePlayer == null ? "nobody"
                : Managers.PlayerNames.HasInstance
                    ? Managers.PlayerNames.Instance.DisplayNameFor(turns.ActivePlayer)
                    : turns.ActivePlayer;
            GUILayout.Label($"<b>Turn</b>   {activeName}   " +
                            $"players {turns?.Players.Count ?? 0}   (press N for next)", rich);

            GUILayout.Space(6);
            GUILayout.Label($"<b>Aim</b>   {_aimHz:F0} Hz (target 20)", rich);

            if (_latestAim != null)
            {
                // Face angle and heading rather than the raw quaternion: these are the two
                // numbers the physics actually reads, and reading them back through
                // PhoneAttitude is what proves the conversion is doing what it claims.
                // With the phone flat, both should sit near zero.
                var attitude = Game.PhoneAttitude.ToUnity(
                    _latestAim.qw, _latestAim.qx, _latestAim.qy, _latestAim.qz);

                GUILayout.Label($"  seq {_latestAim.seq}   " +
                                $"face {Game.PhoneAttitude.FaceAngleDeg(attitude),6:F1}°   " +
                                $"heading {Game.PhoneAttitude.HeadingDeg(attitude),6:F1}°   " +
                                $"spin {_latestAim.spinRate,5:F0}   " +
                                $"windup {_latestAim.swingSpeed:F2} m/s");
            }
            else
            {
                GUILayout.Label("  no aim frames yet — tap to aim on the phone");
            }

            if (_preview != null)
            {
                // The arc is also hidden while a rock is in the air, so a hidden arc with
                // a live signal is expected mid-throw rather than a fault.
                GUILayout.Label(_preview.HasSignal
                    ? $"  predicted {_preview.PredictedSkips} skips over {_preview.PredictedDistance:F1} m"
                    : "  arc hidden (no recent aim frames)");
            }

            GUILayout.Space(6);
            GUILayout.Label($"<b>Throws</b> (newest first, {_throws.Count})", rich);

            if (_throws.Count == 0)
            {
                GUILayout.Label("  none yet");
            }
            else
            {
                foreach (var t in _throws)
                {
                    GUILayout.Label($"  face {FaceAngleOf(t),5:F0}°  " +
                                    $"spin {t.spinRate,5:F0}  spd {t.swingSpeed:F2}");
                }

                GUILayout.Space(6);
                GUILayout.Label("<b>Repeatability</b> (spread across the above; want under 10%)",
                                rich);
                GUILayout.Label($"  speed {Spread(_throws, SwingSpeedOf)}   " +
                                $"face {Spread(_throws, FaceAngleSelector)}");
            }

            GUILayout.Space(6);
            GUILayout.Label($"<i>{toggleKey} toggles this readout</i>", rich);

            GUILayout.EndArea();
        }
    }
}
