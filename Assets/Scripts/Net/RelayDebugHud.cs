using System.Collections.Generic;
using System.Linq;
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
        [SerializeField] private bool visible = true;
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
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
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

        /// <summary>Spread as a percentage of the mean — the repeatability number.</summary>
        private static string Spread(IEnumerable<float> values)
        {
            var list = values.ToList();
            if (list.Count < 2) return "n/a";

            var mean = list.Average();
            if (Mathf.Abs(mean) < 0.0001f) return "n/a";

            var spread = (list.Max() - list.Min()) / Mathf.Abs(mean) * 100f;
            return $"{spread:F0}%";
        }

        private void OnGUI()
        {
            if (!visible) return;

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
            GUILayout.Label($"<b>Turn</b>   {turns?.ActivePlayer ?? "nobody"}   " +
                            $"players {turns?.Players.Count ?? 0}   (press N for next)", rich);

            // Which transport each phone's input is on. Direct means peer-to-peer over the
            // LAN; relay means it is still round-tripping through the hosted service, which
            // is a working state and not an error — see HostingPlan §5.
            if (PeerLink.HasInstance && turns != null && turns.Players.Count > 0)
            {
                var link = PeerLink.Instance;
                var players = new System.Text.StringBuilder();

                foreach (var player in turns.Players)
                {
                    var direct = link.IsDirect(player);
                    players.Append(direct ? "<b>" : "").Append(player).Append(direct ? "</b>:direct  " : ":relay  ");
                }

                GUILayout.Label($"<b>Input</b>  {link.DirectPeerCount}/{turns.Players.Count} direct   {players}", rich);
            }

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
                GUILayout.Label($"  speed {Spread(_throws.Select(t => t.swingSpeed))}   " +
                                $"face {Spread(_throws.Select(FaceAngleOf))}");
            }

            GUILayout.Space(6);
            GUILayout.Label($"<i>{toggleKey} toggles this readout</i>", rich);

            GUILayout.EndArea();
        }
    }
}
