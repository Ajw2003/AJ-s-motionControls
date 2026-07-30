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

            GUILayout.Space(6);
            GUILayout.Label($"<b>Aim</b>   {_aimHz:F0} Hz (target 20)", rich);

            if (_latestAim != null)
            {
                GUILayout.Label($"  seq {_latestAim.seq}   aim {_latestAim.aimDeg,6:F1}°   " +
                                $"attack {_latestAim.attackAngleDeg,6:F1}°   windup {_latestAim.windup:F2}");
            }
            else
            {
                GUILayout.Label("  no aim frames yet — grip on the phone");
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
                    GUILayout.Label($"  pow {t.power:F2}  tim {t.timingQuality:F2}  aim {t.aimDeg,5:F0}°  " +
                                    $"atk {t.attackAngleDeg,5:F0}°  spin {t.spinRate,5:F0}  peak {t.peakAccel:F1}");
                }

                GUILayout.Space(6);
                GUILayout.Label("<b>Repeatability</b> (spread across the above; want under 10%)",
                                rich);
                GUILayout.Label($"  power {Spread(_throws.Select(t => t.power))}   " +
                                $"peak {Spread(_throws.Select(t => t.peakAccel))}   " +
                                $"attack {Spread(_throws.Select(t => t.attackAngleDeg))}");
            }

            GUILayout.Space(6);
            GUILayout.Label($"<i>{toggleKey} toggles this readout</i>", rich);

            GUILayout.EndArea();
        }
    }
}
