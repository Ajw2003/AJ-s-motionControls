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
    // OnGUI scaffolding; the QR matrix comes from the relay, so this stays a texture blit.
    // doc-ref 17f0 docs/4-systems/Presentation.md
    public class JoinScreen : MonoBehaviour
    {
        [SerializeField] private bool isVisible = true;
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

        private GUIStyle _title, _status, _body, _hint;
        private Texture2D _panelTexture;

        [Serializable]
        private class QrMatrix
        {
            public int size;
            public string[] rows;
        }

        private void Start()
        {
            // LocalServices is optional (a normal host has none), so its absence is not logged.
            if (LocalServices.HasInstance) _services = LocalServices.Instance;

            if (RelayClient.HasInstance) _client = RelayClient.Instance;
            else Debug.LogWarning("[JoinScreen] No RelayClient at Start; no QR code or room code will be shown.");

            StartCoroutine(KeepQrsCurrent());
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey)) isVisible = !isVisible;
        }

        /// <summary>
        /// Once a second rather than every frame (#64): the checks below build the join URL
        /// and Wi-Fi payload strings just to compare them, and both only change when the
        /// room code or hotspot does.
        /// </summary>
        private IEnumerator KeepQrsCurrent()
        {
            var wait = new WaitForSecondsRealtime(1f);
            while (true)
            {
                EnsureQrMatchesJoinUrl();
                EnsureWifiQrMatchesHotspot();
                yield return wait;
            }
        }

        /// <summary>
        /// The room code and the tunnel URL arrive from different systems at different
        /// times, and either can change (a reconnect issues a new room). Rather than
        /// coordinating both, this re-derives the join URL once a second and refetches only
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
            if (!isVisible) return;

            EnsureStyles();

            var turns = TurnManager.HasInstance ? TurnManager.Instance : null;

            // Driven by phase, not by headcount: during the lobby the two QR codes are the
            // whole point of the screen, however many people have joined.
            var compact = compactDuringMatch && turns != null && turns.Phase != MatchPhase.Lobby &&
                          _client != null && _client.IsConnected;

            if (compact) DrawCompactCard();
            else if (IsJoinable()) DrawLobbyCorners();
            else DrawFullScreen();
        }

        /// <summary>
        /// True once there is a room to join and nothing has failed — the point at which the
        /// lobby is only the two QR codes. Before that (connecting) and on a failure, the full
        /// panel stays, because there is nothing to scan yet and the status is the screen.
        /// </summary>
        private bool IsJoinable()
        {
            if (_client == null || string.IsNullOrEmpty(_client.RoomCode)) return false;
            if (HotspotService.HasInstance && HotspotService.Instance.State == HotspotState.Failed) return false;
            return _services == null || _services.State != ServiceState.Failed;
        }

        /// <summary>
        /// The lobby: the Wi-Fi code top-left, the game code top-right, and nothing else, so the
        /// shore and its six stones are the screen. Both stay at full size and full opacity for
        /// the whole lobby — a player arriving late needs the first code as much as the first
        /// player did.
        /// </summary>
        /// <remarks>
        /// The Wi-Fi code only exists in Hotspot mode: on a shared network there is no network
        /// for this machine to hand out, so the top-left corner is simply empty there.
        /// </remarks>
        private void DrawLobbyCorners()
        {
            var side = Mathf.Min(Screen.height * 0.24f, Screen.width * 0.16f);
            var margin = Mathf.Round(Screen.height * 0.025f);

            var hotspotInUse = HotspotService.HasInstance && HotspotService.Instance.State != HotspotState.Failed;
            if (hotspotInUse) DrawCornerQr(_wifiQr, new Vector2(margin, margin), side, "1  Wi-Fi");

            DrawCornerQr(_gameQr, new Vector2(Screen.width - side - margin, margin), side,
                         hotspotInUse ? "2  Play" : "Play");
        }

        /// <summary>One corner code on its own dark card, with a short label under it.</summary>
        /// <remarks>
        /// With no texture yet — the relay's /qr endpoint unreachable, say — the card shows the
        /// address and room code as text instead, so a QR fetch failure still leaves a way in.
        /// </remarks>
        private void DrawCornerQr(QrSlot slot, Vector2 topLeft, float side, string label)
        {
            var labelHeight = _body.fontSize * 1.8f;
            var pad = Mathf.Round(side * 0.05f);
            var card = new Rect(topLeft.x, topLeft.y, side + pad * 2, side + pad * 2 + labelHeight);
            var square = new Rect(card.x + pad, card.y + pad, side, side);

            GUI.DrawTexture(card, _panelTexture);

            if (slot.Texture != null)
            {
                GUI.DrawTexture(square, slot.Texture, ScaleMode.ScaleToFit);
            }
            else if (slot == _gameQr && _client != null)
            {
                GUI.Label(square, $"{_client.PublicOrigin}\n\n{_client.RoomCode}", _hint);
            }
            else
            {
                GUI.Label(square, "(building…)", _hint);
            }

            GUI.Label(new Rect(card.x, square.yMax, card.width, labelHeight), label, _body);
        }

        private void DrawFullScreen()
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

            // Hotspot failures are checked first and separately. A hotspot that never came up
            // leaves LocalServices sitting at Ready — its relay really is running — so keying
            // this only off LocalServices showed the ordinary "connecting…" progress forever,
            // under a hint blaming the internet connection, while the actual cause (no hosts
            // entry, :53 taken, UAC declined) was sitting in HotspotService.FailureHint with
            // nothing on the TV to say so.
            var hotspotFailed = HotspotService.HasInstance &&
                                HotspotService.Instance.State == HotspotState.Failed;

            if (hotspotFailed)
            {
                DrawFailure(HotspotService.Instance.StatusMessage,
                            HotspotService.Instance.FailureHint);
            }
            else if (_services != null && _services.State == ServiceState.Failed)
            {
                DrawFailure(_services.StatusMessage, _services.FailureHint);
            }
            else DrawProgress();

            GUILayout.EndArea();
        }

        private void DrawProgress()
        {
            GUILayout.FlexibleSpace();

            // Prefer whichever service is still working. Once LocalServices reaches Ready its
            // message stops changing, and the wait that remains belongs to HotspotService —
            // showing the stale one made a live hotspot start look like a hang.
            var status = HotspotService.HasInstance &&
                         HotspotService.Instance.State == HotspotState.Starting
                ? HotspotService.Instance.StatusMessage
                : _services?.StatusMessage;

            GUILayout.Label(status ?? "Connecting to the game service…", _status);
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

        private void DrawFailure(string message, string hint)
        {
            GUILayout.FlexibleSpace();

            var previous = GUI.color;
            GUI.color = new Color(1f, 0.5f, 0.45f);
            GUILayout.Label(message ?? string.Empty, _status);
            GUI.color = previous;

            GUILayout.Space(16);
            GUILayout.Label(hint ?? string.Empty, _hint);
            GUILayout.FlexibleSpace();
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

            // "Play", as in the lobby's corner: the QR is the way in, and a bare room code read
            // as a stray word. The code only shows when there is no QR to scan.
            GUILayout.Label(_gameQr.Texture != null ? "Play" : _client?.RoomCode ?? "----", _body);
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
    }
}
