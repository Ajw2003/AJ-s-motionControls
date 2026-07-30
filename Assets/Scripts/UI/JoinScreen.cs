using System;
using System.Collections;
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

        [Tooltip("Shrink to a corner card once somebody has joined, so the lake is visible.")]
        [SerializeField] private bool compactAfterFirstJoin = true;

        [Header("QR")]
        [Tooltip("White border around the code, in modules. Below 4 many scanners fail to lock on.")]
        [SerializeField] private int quietZoneModules = 4;

        private Texture2D _qr;
        private string _qrSourceUrl;
        private bool _qrRequestInFlight;

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
        }

        /// <summary>
        /// The room code and the tunnel URL arrive from different systems at different
        /// times, and either can change (a reconnect issues a new room). Rather than
        /// coordinating both, this re-derives the join URL each frame and refetches only
        /// when it actually differs from what is on screen.
        /// </summary>
        private void EnsureQrMatchesJoinUrl()
        {
            if (_qrRequestInFlight || _services == null || _client == null) return;

            var joinUrl = _services.JoinUrlFor(_client.RoomCode);
            if (string.IsNullOrEmpty(joinUrl) || joinUrl == _qrSourceUrl) return;

            StartCoroutine(FetchQr(joinUrl));
        }

        private IEnumerator FetchQr(string joinUrl)
        {
            _qrRequestInFlight = true;

            var endpoint = $"http://127.0.0.1:{_services.RelayPort}/qr?data={UnityWebRequest.EscapeURL(joinUrl)}";
            using (var request = UnityWebRequest.Get(endpoint))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        BuildQrTexture(JsonUtility.FromJson<QrMatrix>(request.downloadHandler.text));
                        _qrSourceUrl = joinUrl;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[JoinScreen] Could not read the QR matrix: {e.Message}");
                    }
                }
                else
                {
                    // Not fatal — the URL and code are on screen and can be typed.
                    Debug.LogWarning($"[JoinScreen] QR request failed: {request.error}");
                }
            }

            _qrRequestInFlight = false;
        }

        private void BuildQrTexture(QrMatrix matrix)
        {
            if (matrix == null || matrix.rows == null || matrix.size <= 0)
                throw new InvalidOperationException("empty matrix");

            var quiet = Mathf.Max(0, quietZoneModules);
            var dimension = matrix.size + quiet * 2;

            if (_qr != null) Destroy(_qr);
            _qr = new Texture2D(dimension, dimension, TextureFormat.RGBA32, false)
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

            _qr.SetPixels32(pixels);
            _qr.Apply();
        }

        private void OnDestroy()
        {
            if (_qr != null) Destroy(_qr);
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

            var joined = TurnManager.HasInstance ? TurnManager.Instance.Players.Count : 0;
            var compact = compactAfterFirstJoin && joined > 0 &&
                          _services != null && _services.State == ServiceState.Ready;

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

            if (_services == null)
            {
                GUILayout.Label("No LocalServices in the scene.", _status);
                GUILayout.Label("Play from the Bootstrap scene — it creates the managers.", _hint);
                GUILayout.EndArea();
                return;
            }

            switch (_services.State)
            {
                case ServiceState.Failed:
                    DrawFailure();
                    break;

                case ServiceState.Ready:
                    DrawJoinDetails(joined);
                    break;

                default:
                    DrawProgress();
                    break;
            }

            GUILayout.EndArea();
        }

        private void DrawProgress()
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(_services.StatusMessage, _status);
            GUILayout.Space(20);

            // Named explicitly so a long wait is legible as progress rather than a hang.
            DrawStep("Relay", _services.State > ServiceState.StartingRelay);
            DrawStep("Public tunnel", _services.State > ServiceState.OpeningTunnel);
            DrawStep("Room code", !string.IsNullOrEmpty(_client?.RoomCode));

            GUILayout.Space(20);
            GUILayout.Label("The first launch takes longer — dependencies and a fresh tunnel.", _hint);
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

            GUILayout.Label("Scan to play — or open the address and type the code", _status);
            GUILayout.Space(12);

            if (_qr != null)
            {
                // Square, sized off the shorter axis so it never overflows the panel.
                var side = Mathf.Min(Screen.height * 0.34f, Screen.width * 0.22f);
                GUI.DrawTexture(CenteredSquare(side), _qr, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label("(building QR code…)", _hint);
            }

            GUILayout.Space(8);
            GUILayout.Label(_services.PublicUrl ?? string.Empty, _url);
            GUILayout.Label(code, _code);

            GUILayout.Label(joined == 0
                ? "No players yet"
                : $"{joined} player{(joined == 1 ? "" : "s")}: {string.Join(", ", TurnManager.Instance.Players)}",
                _body);

            GUILayout.FlexibleSpace();
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

            if (_qr != null) GUI.DrawTexture(CenteredSquare(panel.width - 40f), _qr, ScaleMode.ScaleToFit);

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
    }
}
