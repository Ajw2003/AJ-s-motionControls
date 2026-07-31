using System;
using System.Collections.Concurrent;
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
    ///
    /// The receive loop MUST stay off the Unity SynchronizationContext (Task.Run +
    /// ConfigureAwait(false)). When its awaits resumed on the main thread, intake was
    /// capped at ~one message per rendered frame — against a 60Hz aim stream at
    /// vsync that is zero headroom, so any hitch built a backlog that never drained
    /// and the aim preview fell progressively behind the phone.
    /// </remarks>
    [DefaultExecutionOrder(-100)] // drain the inbound queue before AimPreview reads it
    public class RelayClient : SingletonBase<RelayClient>
    {
        [Tooltip("ws://localhost:3000 for local dev; wss://<your-app>.fly.dev in production.")]
        [SerializeField] private string relayUrl = "ws://localhost:3000";
        [SerializeField] private bool verboseLogging;

        public string RoomCode { get; private set; }
        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;

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

        // The receive loop runs on the thread pool where Time.time would throw, so it
        // raises this flag and Update stamps the retry clock on the main thread.
        private volatile bool _receiveLoopEnded;

        /// <summary>Messages waiting for the main-thread drain. Diagnostic: should sit at 0–2.</summary>
        public int PendingInbound => _inbound.Count;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            _ = ConnectAsync();
        }

        private void Update()
        {
            if (_receiveLoopEnded)
            {
                _receiveLoopEnded = false;
                _nextRetryTime = Time.time + _retryDelay;
            }

            // Everything that touches Unity happens here, on the main thread. Each
            // message is guarded individually: one bad payload or throwing subscriber
            // must not abandon the rest of the queue mid-drain.
            while (_inbound.TryDequeue(out var json))
            {
                try
                {
                    Dispatch(json);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
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

                await _socket.ConnectAsync(new Uri(relayUrl), _cts.Token);
                Debug.Log($"[RelayClient] Connected to {relayUrl}");

                _retryDelay = 1f;
                await SendRawAsync($"{{\"type\":\"{MessageType.CreateRoom}\"}}");

                // Off the Unity sync context, or every ReceiveAsync resumption waits
                // for the player loop and intake caps at one message per frame.
                var socket = _socket;
                var token = _cts.Token;
                _ = Task.Run(() => ReceiveLoopAsync(socket, token));
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
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token)
                        .ConfigureAwait(false);

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

            // This runs on a pool thread — Unity APIs (Time.time) would throw here.
            _receiveLoopEnded = true;
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

                case MessageType.RoomClosed:
                {
                    // The relay idle-sweeps rooms; it also clears this connection's
                    // registration when it does, so a fresh CREATE_ROOM succeeds and
                    // the TV gets a new code instead of sitting bricked on a dead one.
                    Debug.LogWarning("[RelayClient] Room closed by relay — creating a new one");
                    RoomCode = null;
                    _ = SendRawAsync($"{{\"type\":\"{MessageType.CreateRoom}\"}}");
                    break;
                }

                case MessageType.Error:
                {
                    var error = JsonUtility.FromJson<ErrorMessage>(json);
                    Debug.LogWarning($"[RelayClient] Relay error {error.code}: {error.message}");
                    break;
                }
            }
        }

        /// <summary>Fire-and-forget send. Safe to call every frame from the main thread.</summary>
        public void Send(string json)
        {
            if (!IsConnected) return;
            _ = SendRawAsync(json);
        }

        private async Task SendRawAsync(string json)
        {
            // ClientWebSocket forbids overlapping sends; the semaphore serialises them.
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_socket?.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
                    .ConfigureAwait(false);
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
            // _sendLock is deliberately NOT disposed: an in-flight SendRawAsync may
            // still be awaiting it, and SemaphoreSlim only needs disposal when its
            // AvailableWaitHandle has been touched (it never is here).
            EventManager.Instance?.UnsubscribeFromAllEvents(this);
            base.OnDestroy();
        }
    }
}
