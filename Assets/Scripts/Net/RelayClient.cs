using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventSystems;
using EventTypes.MatchEvents;
using Singleton;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// Host-side connection to the relay. Creates the room, receives phone input,
    /// and broadcasts match state back out.
    /// </summary>
    /// <remarks>
    /// MUST live in the Bootstrap scene as a persistent manager. If it is created
    /// per-scene instead, the socket is torn down on every transition and every
    /// phone bounces back to the join screen — a lifecycle bug that presents as a
    /// networking one.
    ///
    /// DEVIATION FROM PLAN: uses System.Net.WebSockets.ClientWebSocket rather than
    /// the NativeWebSocket package the plan named. ClientWebSocket ships with .NET,
    /// so there is no external dependency to vendor, and it works fine for a native
    /// desktop host. The tradeoff is that it does NOT work in WebGL builds — which
    /// is acceptable because the plan already rejected WebGL as a target. Swap to
    /// NativeWebSocket if a browser build ever becomes a requirement.
    ///
    /// ClientWebSocket is thread-affine for sends, so all sends funnel through a
    /// semaphore, and all received messages are marshalled onto the main thread via
    /// a queue drained in Update — Unity APIs cannot be touched from the socket task.
    /// </remarks>
    public class RelayClient : SingletonBase<RelayClient>
    {
        /// <summary>
        /// Overrides <see cref="relayUrl"/> when present beside the executable. One line,
        /// e.g. ws://localhost:3000. Exists so pointing a shipped build at a different
        /// service never needs a rebuild — see HostingPlan.md §12.6.
        /// </summary>
        private const string OverrideFileName = "relay-url.txt";

        [Tooltip("The service every host connects out to. Override per-machine with a " +
                 "relay-url.txt beside the executable rather than editing this.")]
        [SerializeField] private string relayUrl = "wss://rockskipping-relay.fly.dev";

        [SerializeField] private bool verboseLogging;

        public string RoomCode { get; private set; }
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

        /// <summary>The address actually in use, after any override file.</summary>
        public string RelayUrl { get; private set; }

        /// <summary>
        /// Where a phone loads the controller page: the same origin as the socket, over
        /// http(s) instead of ws(s). Deriving it rather than configuring it separately is
        /// what guarantees the page and the socket can never drift onto different hosts —
        /// the single-origin property the whole design depends on.
        /// </summary>
        public string PublicOrigin { get; private set; }

        /// <summary>Cosmetic aim frames. Safe to drop; never feed these to the simulation.</summary>
        public event Action<AimMessage> AimReceived;

        /// <summary>The authoritative throw for a turn.</summary>
        public event Action<ThrowMessage> ThrowReceived;

        public event Action<string> RoomCodeAssigned;

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<string> _inbound = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private bool _connecting;
        private float _nextRetryTime;
        private float _retryDelay = 1f;
        private const float MaxRetryDelay = 10f;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            RelayUrl = ResolveRelayUrl(relayUrl);
            PublicOrigin = DeriveOrigin(RelayUrl);
        }

        private void Start()
        {
            _ = ConnectAsync();
        }

        /// <summary>The address a phone should open, with the room code pre-filled.</summary>
        public string JoinUrlFor(string roomCode)
        {
            if (string.IsNullOrEmpty(PublicOrigin)) return null;
            return string.IsNullOrEmpty(roomCode) ? PublicOrigin : $"{PublicOrigin}/?room={roomCode}";
        }

        /// <summary>
        /// Reads the override file if it exists, otherwise keeps the inspector value.
        /// Application.dataPath resolves to Assets/ in the editor and &lt;exe&gt;_Data in a
        /// build, so its parent is the right place to look in both cases.
        /// </summary>
        private static string ResolveRelayUrl(string configured)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OverrideFileName));
                if (File.Exists(path))
                {
                    var line = File.ReadAllText(path).Trim();
                    if (line.Length > 0)
                    {
                        Debug.Log($"[RelayClient] Using {OverrideFileName}: {line}");
                        return line;
                    }
                }
            }
            catch (Exception e)
            {
                // An unreadable override must not stop the game booting against the default.
                Debug.LogWarning($"[RelayClient] Could not read {OverrideFileName}: {e.Message}");
            }

            return configured;
        }

        /// <summary>ws://host -> http://host, wss://host -> https://host.</summary>
        private static string DeriveOrigin(string socketUrl)
        {
            try
            {
                var uri = new Uri(socketUrl);
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
                return $"{scheme}://{authority}";
            }
            catch (Exception e)
            {
                Debug.LogError($"[RelayClient] Relay URL '{socketUrl}' is not a valid URI: {e.Message}");
                return null;
            }
        }

        private void Update()
        {
            // Everything that touches Unity happens here, on the main thread.
            while (_inbound.TryDequeue(out var json))
            {
                Dispatch(json);
            }

            if (!IsConnected && !_connecting && Time.time >= _nextRetryTime)
            {
                _ = ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            if (_connecting) return;
            _connecting = true;

            try
            {
                _cts?.Cancel();
                _socket?.Dispose();

                _cts = new CancellationTokenSource();
                _socket = new ClientWebSocket();

                await _socket.ConnectAsync(new Uri(RelayUrl), _cts.Token);
                Debug.Log($"[RelayClient] Connected to {RelayUrl}");

                _retryDelay = 1f;
                await SendRawAsync($"{{\"type\":\"{MessageType.CreateRoom}\"}}");

                _ = ReceiveLoopAsync(_socket, _cts.Token);
            }
            catch (Exception e)
            {
                // Backoff rather than hammering a relay that is down or still booting.
                _retryDelay = Mathf.Min(_retryDelay * 2f, MaxRetryDelay);
                _nextRetryTime = Time.time + _retryDelay;
                Debug.LogWarning($"[RelayClient] Connect failed ({e.Message}); retrying in {_retryDelay:F0}s");
            }
            finally
            {
                _connecting = false;
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            var builder = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    // A single message can arrive across several frames.
                    if (!result.EndOfMessage) continue;

                    _inbound.Enqueue(builder.ToString());
                    builder.Clear();
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception e)
            {
                Debug.LogWarning($"[RelayClient] Receive loop ended: {e.Message}");
            }

            _nextRetryTime = Time.time + _retryDelay;
        }

        private void Dispatch(string json)
        {
            MessageEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<MessageEnvelope>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RelayClient] Unparseable message dropped: {e.Message}");
                return;
            }

            if (envelope == null || string.IsNullOrEmpty(envelope.type)) return;
            if (verboseLogging) Debug.Log($"[RelayClient] <- {json}");

            switch (envelope.type)
            {
                case MessageType.RoomCreated:
                {
                    RoomCode = JsonUtility.FromJson<RoomCreatedMessage>(json).roomCode;
                    Debug.Log($"[RelayClient] Room code: {RoomCode}");
                    RoomCodeAssigned?.Invoke(RoomCode);
                    break;
                }

                case MessageType.PlayerJoined:
                {
                    var joined = JsonUtility.FromJson<PlayerJoinedMessage>(json);
                    EventManager.Instance?.Publish(new PlayerJoinedEvent(joined.playerId, joined.reconnected));
                    break;
                }

                case MessageType.PlayerLeft:
                {
                    var left = JsonUtility.FromJson<PlayerLeftMessage>(json);
                    EventManager.Instance?.Publish(new PlayerLeftEvent(left.playerId));
                    break;
                }

                case MessageType.Aim:
                    AimReceived?.Invoke(JsonUtility.FromJson<AimMessage>(json));
                    break;

                case MessageType.Throw:
                    ThrowReceived?.Invoke(JsonUtility.FromJson<ThrowMessage>(json));
                    break;

                case MessageType.Error:
                {
                    var error = JsonUtility.FromJson<ErrorMessage>(json);
                    Debug.LogWarning($"[RelayClient] Relay error {error.code}: {error.message}");
                    break;
                }
            }
        }///FIX switch statement and whole function likely taking up ms and adding to lag

        /// <summary>Fire-and-forget send. Safe to call every frame from the main thread.</summary>
        public void Send(string json)
        {
            if (!IsConnected) return;
            _ = SendRawAsync(json);
        }

        private async Task SendRawAsync(string json)
        {
            // ClientWebSocket forbids overlapping sends; the semaphore serialises them.
            await _sendLock.WaitAsync();
            try
            {
                if (_socket?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RelayClient] Send failed: {e.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            _socket?.Dispose();
            _sendLock.Dispose();
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
