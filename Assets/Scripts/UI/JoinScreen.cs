using System;
using System.Collections;
using System.Text;
using Managers;
using Net;
using UnityEngine;
using UnityEngine.Networking;

// Namespace is `Ui`, not `UI`, matching the FactoryTM convention. `using UnityEngine;`
// pulls UnityEngine.UI into scope, so a namespace literally named UI makes every
// `UI.Something` reference elsewhere an ambiguous one.
namespace Ui
{
    /// <summary>
    /// The screen guests actually look at: how to join, and the code to type. Shows the
    /// launch progress first, so a slow tunnel reads as "working" rather than "broken".
    /// </summary>
    /// <remarks>
    /// Scaffolding in the same spirit as RelayDebugHud — OnGUI, no Canvas, no prefab, so
    /// it works in whatever scene is loaded. Replace with authored UI when the real Lobby
    /// scene exists; the QR fetch and the join-URL construction are the parts worth
    /// keeping.
    ///
    /// The QR matrix comes from the relay rather than being generated here. The relay is
    /// already a required running process and already has a QR library, so this stays a
    /// texture blit instead of a Reed-Solomon implementation.
    /// </remarks>
    public class JoinScreen : MonoBehaviour
    {
        [SerializeField] private bool visible = true;
        [SerializeField] private KeyCode toggleKey = KeyCode.F2;

        [Tooltip("Shrink to a corner card once the match is under way, so the lake is visible. " +
                 "The card stays up so latecomers can still scan in.")]
        [SerializeField] private bool compactDuringMatch = true;

        [Header("QR")]
        [Tooltip("White border around the code, in modules. Below 4 many scanners fail to lock on.")]
        [SerializeField] private int quietZoneModules = 4;

        private class QrSlot
        {
            public Texture2D Texture;
            public string SourceData;
            public bool RequestInFlight;

            // Failure state. Without these, a failed fetch leaves SourceData unchanged, so the
            // "does the URL differ from what is on screen?" test is still true next frame and
            // the request is retried immediately — at frame rate, logging every time. In Hotspot
            // mode that is guaranteed to happen at least briefly: PublicOrigin points at
            // 192.168.137.1, which does not exist until tethering finishes coming up.
            public float NextAttemptTime;
            public int ConsecutiveFailures;
            public string LastLoggedError;
        }

        /// <summary>First retry delay, doubling to <see cref="QrRetryMaxSeconds"/>.</summary>
        private const float QrRetryBaseSeconds = 0.5f;
        private const float QrRetryMaxSeconds = 5f;

        private readonly QrSlot _gameQr = new();
        private readonly QrSlot _wifiQr = new();

        private LocalServices _services;
        private RelayClient _client;

        private GUIStyle _title, _status, _code, _url, _body, _hint;
        private Texture2D _panelTexture;

        [Serializable]
        private class QrMatrix
        {
            public int size;
            public string[] rows;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;

            if (_services == null && LocalServices.HasInstance) _services = LocalServices.Instance;
            if (_client == null && RelayClient.HasInstance) _client = RelayClient.Instance;

            EnsureQrMatchesJoinUrl();
            EnsureWifiQrMatchesHotspot();
        }

        /// <summary>
        /// The room code and the tunnel URL arrive from different systems at different
        /// times, and either can change (a reconnect issues a new room). Rather than
        /// coordinating both, this re-derives the join URL each frame and refetches only
        /// when it actually differs from what is on screen.
        /// </summary>
        private void EnsureQrMatchesJoinUrl()
        {
            // LocalServices is optional now — a normal host has none. Only the relay
            // connection is required, and it is what knows the address.
            if (_gameQr.RequestInFlight || _client == null) return;
            if (Time.realtimeSinceStartup < _gameQr.NextAttemptTime) return;

            var joinUrl = _client.JoinUrlFor(_client.RoomCode);
            if (string.IsNullOrEmpty(joinUrl) || joinUrl == _gameQr.SourceData) return;

            StartCoroutine(FetchQr(_gameQr, joinUrl));
        }

        /// <summary>
        /// Same re-derive-and-refetch-on-change shape as the game QR, gated on HotspotService
        /// actually being up — a normal LAN host never has one, and never fetches this.
        /// </summary>
        private void EnsureWifiQrMatchesHotspot()
        {
            if (_wifiQr.RequestInFlight || _client == null) return;
            if (Time.realtimeSinceStartup < _wifiQr.NextAttemptTime) return;
            if (!HotspotService.HasInstance || HotspotService.Instance.State != HotspotState.Ready) return;

            var payload = BuildWifiPayload(HotspotService.Instance.Ssid, HotspotService.Instance.Passphrase);
            if (payload == _wifiQr.SourceData) return;

            StartCoroutine(FetchQr(_wifiQr, payload));
        }

        /// <summary>
        /// WIFI:T:WPA;S:ssid;P:passphrase;; — the payload both Android and iOS cameras
        /// recognise as "join this network". Escaping ported from deploy/spike/wifi-qr.js's
        /// escapeValue(); a passphrase containing one of these five characters is rare but
        /// not impossible, and an unescaped one silently truncates the field.
        /// </summary>
        private static string BuildWifiPayload(string ssid, string passphrase) =>
            $"WIFI:T:WPA;S:{EscapeWifiValue(ssid)};P:{EscapeWifiValue(passphrase)};;";

        private static string EscapeWifiValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c is '\\' or ';' or ',' or ':' or '"') builder.Append('\\');
                builder.Append(c);
            }
            return builder.ToString();
        }

        private IEnumerator FetchQr(QrSlot slot, string data)
        {
            slot.RequestInFlight = true;

            // Asked of the same service that serves the page, not of a local process —
            // a normal host is not running one.
            var endpoint = $"{_client.PublicOrigin}/qr?data={UnityWebRequest.EscapeURL(data)}";
            using (var request = UnityWebRequest.Get(endpoint))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        BuildQrTexture(slot, JsonUtility.FromJson<QrMatrix>(request.downloadHandler.text));
                        slot.SourceData = data;

                        if (slot.ConsecutiveFailures > 0)
                        {
                            // Say so explicitly. Otherwise the console's last word on the subject
                            // is a warning, and a transient startup race reads like a real fault.
                            Debug.Log($"[JoinScreen] QR fetched after {slot.ConsecutiveFailures} " +
                                      "failed attempt(s) — the relay became reachable.");
                        }
                        slot.ConsecutiveFailures = 0;
                        slot.LastLoggedError = null;
                        slot.NextAttemptTime = 0f;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[JoinScreen] Could not read the QR matrix: {e.Message}");
                    }
                }
                else
                {
                    slot.ConsecutiveFailures++;

                    // Back off, so a relay that is not up yet is not hammered once per frame.
                    var delay = Mathf.Min(QrRetryMaxSeconds,
                        QrRetryBaseSeconds * Mathf.Pow(2f, slot.ConsecutiveFailures - 1));
                    slot.NextAttemptTime = Time.realtimeSinceStartup + delay;

                    // Log each DISTINCT error once. Repeating an identical message while retrying
                    // adds no information and buries everything else — and this failure is
                    // expected during startup in Hotspot mode, where the address the QR is
                    // fetched from does not exist until tethering completes.
                    if (request.error != slot.LastLoggedError)
                    {
                        slot.LastLoggedError = request.error;
                        // Not fatal — the URL and code are on screen and can be typed.
                        Debug.LogWarning($"[JoinScreen] QR request failed: {request.error} " +
                                         "(retrying quietly with backoff; the code and URL are " +
                                         "still on screen and can be typed)");
                    }
                }
            }

            slot.RequestInFlight = false;
        }

        private void BuildQrTexture(QrSlot slot, QrMatrix matrix)
        {
            if (matrix == null || matrix.rows == null || matrix.size <= 0)
                throw new InvalidOperationException("empty matrix");

            var quiet = Mathf.Max(0, quietZoneModules);
            var dimension = matrix.size + quiet * 2;

            if (slot.Texture != null) Destroy(slot.Texture);
            slot.Texture = new Texture2D(dimension, dimension, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,   // nearest-neighbour keeps module edges hard
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[dimension * dimension];
            var light = new Color32(255, 255, 255, 255);
            var dark = new Color32(0, 0, 0, 255);

            for (var i = 0; i < pixels.Length; i++) pixels[i] = light;

            for (var row = 0; row < matrix.size; row++)
            {
                var bits = matrix.rows[row];

                for (var col = 0; col < matrix.size && col < bits.Length; col++)
                {
                    if (bits[col] != '1') continue;

                    // Texture origin is bottom-left; the matrix is written top-down.
                    var y = dimension - 1 - (row + quiet);
                    pixels[y * dimension + col + quiet] = dark;
                }
            }

            slot.Texture.SetPixels32(pixels);
            slot.Texture.Apply();
        }

        private void OnDestroy()
        {
            if (_gameQr.Texture != null) Destroy(_gameQr.Texture);
            if (_wifiQr.Texture != null) Destroy(_wifiQr.Texture);
            if (_panelTexture != null) Destroy(_panelTexture);
        }

        // ------------------------------------------------------------------ drawing

        private void EnsureStyles()
        {
            if (_title != null) return;

            // Scaled off screen height so the code stays legible from a couch whether
            // this is running in a small editor Game view or full screen on a TV.
            var scale = Mathf.Clamp(Screen.height / 1080f, 0.5f, 3f);

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(42 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _status = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(28 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _code = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(110 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _url = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _body = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(22 * scale),
                alignment = TextAnchor.MiddleCenter,
            };
            _hint = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(20 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };

            _panelTexture = new Texture2D(1, 1);
            _panelTexture.SetPixel(0, 0, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            _panelTexture.Apply();
        }

        private void OnGUI()
        {
            if (!visible) return;

            EnsureStyles();

            var turns = TurnManager.HasInstance ? TurnManager.Instance : null;
            var joined = turns?.Players.Count ?? 0;

            // Driven by phase, not by headcount: during the lobby the code and the start
            // prompt are the whole point of the screen, however many people have joined.
            var compact = compactDuringMatch && turns != null && turns.Phase != MatchPhase.Lobby &&
                          _client != null && _client.IsConnected;

            if (compact) DrawCompactCard();
            else DrawFullScreen(joined);
        }

        private void DrawFullScreen(int joined)
        {
            var panel = new Rect(Screen.width * 0.5f - Screen.width * 0.3f, Screen.height * 0.08f,
                                 Screen.width * 0.6f, Screen.height * 0.84f);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 20, panel.y + 20, panel.width - 40, panel.height - 40));

            GUILayout.Label("ROCK SKIPPING", _title);
            GUILayout.Space(10);

            // Driven by the relay connection, not by LocalServices: a normal host runs no
            // local process, so keying the screen off one would leave it stuck on
            // "starting up" forever on every machine except a developer's.
            if (_client == null)
            {
                GUILayout.Label("No RelayClient in the scene.", _status);
                GUILayout.Label("Play from the Bootstrap scene — it creates the managers.", _hint);
                GUILayout.EndArea();
                return;
            }

            if (_services != null && _services.State == ServiceState.Failed) DrawFailure();
            else if (!string.IsNullOrEmpty(_client.RoomCode)) DrawJoinDetails(joined);
            else DrawProgress();

            GUILayout.EndArea();
        }

        private void DrawProgress()
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(_services?.StatusMessage ?? "Connecting to the game service…", _status);
            GUILayout.Space(20);

            // Named explicitly so a long wait is legible as progress rather than a hang.
            DrawStep("Connected", _client.IsConnected);
            DrawStep("Room code", !string.IsNullOrEmpty(_client.RoomCode));

            GUILayout.Space(20);
            GUILayout.Label(_client.RelayUrl, _hint);
            GUILayout.Label("Retrying automatically — check this machine's internet connection.", _hint);
            GUILayout.FlexibleSpace();
        }

        private void DrawStep(string label, bool done)
        {
            GUILayout.Label($"{(done ? "✓" : "…")}  {label}", _body);
        }

        private void DrawFailure()
        {
            GUILayout.FlexibleSpace();

            var previous = GUI.color;
            GUI.color = new Color(1f, 0.5f, 0.45f);
            GUILayout.Label(_services.StatusMessage, _status);
            GUI.color = previous;

            GUILayout.Space(16);
            GUILayout.Label(_services.FailureHint ?? string.Empty, _hint);
            GUILayout.FlexibleSpace();
        }

        private void DrawJoinDetails(int joined)
        {
            var code = _client?.RoomCode;

            if (string.IsNullOrEmpty(code))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Waiting for a room code from the relay…", _status);
                GUILayout.FlexibleSpace();
                return;
            }

            var hotspotReady = HotspotService.HasInstance && HotspotService.Instance.State == HotspotState.Ready;

            GUILayout.Label(hotspotReady
                ? "Scan 1: join Wi-Fi     Scan 2: open the game"
                : "Scan to play — or open the address and type the code", _status);
            GUILayout.Space(12);

            if (hotspotReady)
            {
                // Two scans, not one — a WIFI: payload joins a network, a URL payload opens
                // a page, and no single QR does both. Smaller than the single-QR size below
                // so the pair fits the panel side by side.
                var side = Mathf.Min(Screen.height * 0.24f, Screen.width * 0.16f);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                DrawQr(_wifiQr, side);
                GUILayout.Space(20);
                DrawQr(_gameQr, side);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else if (_gameQr.Texture != null)
            {
                // Square, sized off the shorter axis so it never overflows the panel.
                var side = Mathf.Min(Screen.height * 0.34f, Screen.width * 0.22f);
                GUI.DrawTexture(CenteredSquare(side), _gameQr.Texture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("(building QR code…)", _hint);
            }

            GUILayout.Space(8);
            GUILayout.Label(_client.PublicOrigin ?? string.Empty, _url);
            GUILayout.Label(code, _code);

            var turns = TurnManager.HasInstance ? TurnManager.Instance : null;

            GUILayout.Label(joined == 0
                ? "No players yet"
                : $"{joined} player{(joined == 1 ? "" : "s")}: {string.Join(", ", turns.Players)}",
                _body);

            GUILayout.Space(10);

            // The match never starts on its own — somebody walking in mid-lobby should not
            // begin it for everyone else, so it waits for a deliberate press.
            // Suppressed while the settings modal has focus, so pressing Space there doesn't
            // also start the match out from under the panel.
            if (!MatchSettingsScreen.IsOpen && turns != null && turns.CanStartMatch)
            {
                var previous = GUI.color;
                GUI.color = new Color(0.6f, 1f, 0.7f);
                GUILayout.Label($"Press {turns.StartMatchKey} to start — " +
                                $"{turns.RoundsPerMatch} round{(turns.RoundsPerMatch == 1 ? "" : "s")}", _status);
                GUI.color = previous;
            }
            else if (turns != null && !MatchSettingsScreen.IsOpen)
            {
                GUILayout.Label("Waiting for at least one player…", _hint);
            }

            GUILayout.FlexibleSpace();
            if (turns != null)
            {
                var random = RockSelection.HasInstance && RockSelection.Instance.RandomRocks;
                var tuning = RockSelection.HasInstance ? RockSelection.Instance.ActiveTuningSet.ToString() : "Authored";
                GUILayout.Label($"Rocks: {(random ? "Random" : "Pointed")} · Tuning: {tuning} · " +
                                $"{turns.RoundsPerMatch} round{(turns.RoundsPerMatch == 1 ? "" : "s")} — " +
                                "Press F4 for match settings", _hint);
            }
            GUILayout.Label($"{toggleKey} hides this screen", _hint);
        }

        /// <summary>Corner card once play has started, so latecomers can still get in.</summary>
        private void DrawCompactCard()
        {
            var width = Mathf.Min(Screen.width * 0.2f, 340f);
            var height = width * 1.15f;
            var panel = new Rect(Screen.width - width - 20, 20, width, height);

            GUI.DrawTexture(panel, _panelTexture);
            GUILayout.BeginArea(new Rect(panel.x + 10, panel.y + 10, panel.width - 20, panel.height - 20));

            if (_gameQr.Texture != null)
                GUI.DrawTexture(CenteredSquare(panel.width - 40f), _gameQr.Texture, ScaleMode.ScaleToFit);

            GUILayout.Label(_client?.RoomCode ?? "----", _body);
            GUILayout.EndArea();
        }

        /// <summary>
        /// Reserves a full-width row and returns a centred square inside it. Asking
        /// GUILayout for a fixed-width rect would left-align it, because the rect it
        /// hands back is exactly the width requested with no slack to centre within.
        /// </summary>
        private static Rect CenteredSquare(float side)
        {
            var row = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(true));
            return new Rect(row.x + (row.width - side) * 0.5f, row.y, side, side);
        }

        /// <summary>
        /// A fixed-size square inside a horizontal group — unlike CenteredSquare, which
        /// claims the whole row's width to center a single item, this reserves exactly
        /// `side` so two of these can sit side by side inside one GUILayout.BeginHorizontal.
        /// </summary>
        private void DrawQr(QrSlot slot, float side)
        {
            var rect = GUILayoutUtility.GetRect(side, side, GUILayout.Width(side), GUILayout.Height(side));
            if (slot.Texture != null) GUI.DrawTexture(rect, slot.Texture, ScaleMode.ScaleToFit);
            else GUI.Label(rect, "(building…)", _hint);
        }
    }
}
