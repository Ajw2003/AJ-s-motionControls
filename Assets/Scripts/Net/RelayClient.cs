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

        /// <summary>A phone confirming its highlighted stone. See PickRockMessage.</summary>
        public event Action<PickRockMessage> PickRockReceived;

        public event Action<string> RoomCodeAssigned;

        /// <summary>
        /// Every inbound message, as (type, raw json), fired before the typed dispatch below.
        /// </summary>
        /// <remarks>
        /// Exists so signalling this class does not model — WebRTC offer/answer/ICE — can ride
        /// the relay without teaching RelayClient about it. The relay routes generically for
        /// the same reason; this is the client-side half of that decision.
        /// </remarks>
        public event Action<string, string> RawMessageReceived;

        /// <summary>
        /// A message waiting for the main thread, and who it is known to be from.
        /// </summary>
        /// <remarks>
        /// TrustedPlayerId is null for relay traffic, because the relay already stamps
        /// identity server-side. It is set for peer traffic, where there is no relay in
        /// the path to do it — see IngestFromPeer.
        /// </remarks>
        private readonly struct Inbound
        {
            public readonly string Json;
            public readonly string TrustedPlayerId;

            public Inbound(string json, string trustedPlayerId)
            {
                Json = json;
                TrustedPlayerId = trustedPlayerId;
            }
        }

        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<Inbound> _inbound = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private bool _connecting;
        private float _connectStartedAt;
        private float _nextRetryTime;
        private float _retryDelay = 1f;

        /// <summary>
        /// True from Awake until LocalServices reports a settled state. While true, Start
        /// must not connect: LocalServices.Awake runs before LocalServices.Start, so at
        /// Awake time it has not begun obtaining a certificate yet, even on the fast
        /// cached-certificate path — resolving RelayUrl here would lock in a value from
        /// before the derived address was known. See Findings-PhoneUnreachable.md's
        /// Phase 3.1 amendment.
        /// </summary>
        private bool _waitingForLocalServices;

        /// <summary>
        /// Capped low on purpose. The relay holds a room for 60s after its host drops and
        /// the phones stop waiting after 45s, so a host that backs off to ten-second
        /// intervals can burn that whole budget on two failed attempts and lose the room
        /// it was about to reclaim. Four seconds is still gentle on a relay that is down.
        /// </summary>
        private const float MaxRetryDelay = 4f;

        /// <summary>
        /// How long a single connect attempt may take before it is abandoned.
        /// </summary>
        /// <remarks>
        /// Without this the client can wedge permanently. A TCP connect against a
        /// half-torn-down network — Wi-Fi toggled off and back on, a laptop waking, a
        /// relay being restarted — can sit unresolved far longer than any useful
        /// retry interval, and while it does, `_connecting` stays true and Update
        /// refuses to start another attempt. The symptom is a host that never comes
        /// back even though the network is fine again.
        /// </remarks>
        private const float ConnectTimeout = 8f;

        /// <summary>Belt and braces: if a connect somehow outlives its own timeout, reset anyway.</summary>
        private const float ConnectWatchdog = ConnectTimeout + 5f;

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();

            if (LocalServices.HasInstance)
            {
                // LocalServices may still be obtaining a certificate; wait for it to settle
                // rather than resolving against a value it hasn't produced yet.
                _waitingForLocalServices = true;
                LocalServices.Instance.StateChanged += OnLocalServicesStateChanged;
            }
            else
            {
                Resolve(null);
            }
        }

        private void Start()
        {
            if (_waitingForLocalServices) return; // OnLocalServicesStateChanged connects once settled.
            _ = ConnectAsync();
        }

        /// <summary>
        /// Fires once LocalServices reaches a state that either has a derived address to
        /// offer (Ready) or definitively does not (Failed, or Idle because it was never
        /// asked to run a local relay). Any other state is still in progress.
        /// </summary>
        private void OnLocalServicesStateChanged(ServiceState state)
        {
            if (state is ServiceState.ObtainingCertificate or ServiceState.InstallingDependencies
                or ServiceState.StartingRelay) return;

            LocalServices.Instance.StateChanged -= OnLocalServicesStateChanged;
            _waitingForLocalServices = false;

            Resolve(state == ServiceState.Ready ? LocalServices.Instance.SecureSocketUrl : null);
            _ = ConnectAsync();
        }

        /// <summary>
        /// Settles RelayUrl and PublicOrigin. relay-url.txt, the manual escape hatch, always
        /// wins when present. Otherwise prefers <paramref name="derivedUrl"/> — the local
        /// relay's certificate-derived address, when LocalServices supplied one — and falls
        /// back to the configured default.
        /// </summary>
        private void Resolve(string derivedUrl)
        {
            var overridden = ReadOverride();
            if (overridden != null)
            {
                Debug.Log($"[RelayClient] Using {OverrideFileName}: {overridden}");
                RelayUrl = overridden;
            }
            else if (!string.IsNullOrEmpty(derivedUrl))
            {
                Debug.Log($"[RelayClient] Using the local relay's derived address: {derivedUrl}");
                RelayUrl = derivedUrl;
            }
            else
            {
                RelayUrl = relayUrl;
            }

            PublicOrigin = DeriveOrigin(RelayUrl);
        }

        /// <summary>The address a phone should open, with the room code pre-filled.</summary>
        public string JoinUrlFor(string roomCode)
        {
            if (string.IsNullOrEmpty(PublicOrigin)) return null;
            return string.IsNullOrEmpty(roomCode) ? PublicOrigin : $"{PublicOrigin}/?room={roomCode}";
        }

        /// <summary>
        /// The one line of <see cref="OverrideFileName"/>, or null when there is no usable
        /// override. Application.dataPath resolves to Assets/ in the editor and
        /// &lt;exe&gt;_Data in a build, so its parent is the right place to look both times.
        /// </summary>
        /// <remarks>
        /// Internal because LocalServices needs the same answer: a relay started by hand on
        /// a non-default port is only reachable at the address this file names, so a health
        /// probe that ignores it checks the wrong thing.
        /// </remarks>
        internal static string ReadOverride()
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", OverrideFileName));
                if (File.Exists(path))
                {
                    var line = File.ReadAllText(path).Trim();
                    if (line.Length > 0) return line;
                }
            }
            catch (Exception e)
            {
                // An unreadable override must not stop the game booting against the default.
                Debug.LogWarning($"[RelayClient] Could not read {OverrideFileName}: {e.Message}");
            }

            return null;
        }

        /// <summary>ws://host -> http://host, wss://host -> https://host.</summary>
        internal static string DeriveOrigin(string socketUrl)
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
            while (_inbound.TryDequeue(out var message))
            {
                Dispatch(message.Json, message.TrustedPlayerId);
            }

            // A connect that has run past its own timeout is not going to finish. Clear
            // the flag so the retry loop starts again rather than waiting on it forever.
            if (_connecting && Time.time - _connectStartedAt > ConnectWatchdog)
            {
                Debug.LogWarning("[RelayClient] Connect attempt wedged past its timeout — retrying.");
                _connecting = false;
                _nextRetryTime = Time.time + 1f;
            }

            if (!_waitingForLocalServices && !IsConnected && !_connecting && Time.time >= _nextRetryTime)
            {
                _ = ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            if (_connecting) return;
            _connecting = true;
            _connectStartedAt = Time.time;

            try
            {
                _cts?.Cancel();
                _socket?.Dispose();

                _cts = new CancellationTokenSource();
                _socket = new ClientWebSocket();

                // Bounded separately from _cts: the timeout must abandon this attempt
                // without cancelling the token the receive loop and sends will use if it
                // succeeds. Cancelling a linked source never cancels its parent.
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    attempt.CancelAfter(TimeSpan.FromSeconds(ConnectTimeout));
                    await _socket.ConnectAsync(new Uri(RelayUrl), attempt.Token);
                }

                Debug.Log($"[RelayClient] Connected to {RelayUrl}");

                _retryDelay = 1f;

                // Ask for the previous code back. Every phone on screen is still holding
                // it, so reclaiming means they reconnect on their own instead of the room
                // silently becoming unreachable and everyone retyping. The relay refuses
                // if a live host already owns it, and simply issues a fresh one.
                var preferred = string.IsNullOrEmpty(RoomCode)
                    ? string.Empty
                    : $",\"preferredCode\":\"{RoomCode}\"";

                await SendRawAsync($"{{\"type\":\"{MessageType.CreateRoom}\"{preferred}}}");

                _ = ReceiveLoopAsync(_socket, _cts.Token);
            }
            catch (Exception e)
            {
                // Backoff rather than hammering a relay that is down or still booting.
                _retryDelay = Mathf.Min(_retryDelay * 2f, MaxRetryDelay);
                _nextRetryTime = Time.time + _retryDelay;

                var why = e is OperationCanceledException
                    ? $"timed out after {ConnectTimeout:F0}s"
                    : e.Message;

                Debug.LogWarning($"[RelayClient] Connect to {RelayUrl} failed ({why}); retrying in {_retryDelay:F0}s");
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

                    _inbound.Enqueue(new Inbound(builder.ToString(), null));
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

        /// <summary>
        /// Hand a message that arrived over a peer connection into the same dispatch path
        /// as relay traffic, so nothing downstream can tell the two apart.
        /// </summary>
        /// <param name="trustedPlayerId">
        /// Which peer it came from. This <b>overrides</b> any playerId in the payload,
        /// reproducing the server-side stamping in the relay's relayToHost — without it a
        /// phone could label its own throws with somebody else's name, which is exactly
        /// the spoof the relay was hardened against. The peer connection is per-player, so
        /// the caller always knows the real answer.
        /// </param>
        /// <remarks>Safe to call from any thread; the queue is drained on the main one.</remarks>
        public void IngestFromPeer(string json, string trustedPlayerId)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(trustedPlayerId)) return;
            _inbound.Enqueue(new Inbound(json, trustedPlayerId));
        }

        private void Dispatch(string json, string trustedPlayerId)
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

            RawMessageReceived?.Invoke(envelope.type, json);

            switch (envelope.type)
            {
                case MessageType.RoomCreated:
                {
                    var created = JsonUtility.FromJson<RoomCreatedMessage>(json);
                    var sameCode = created.roomCode == RoomCode;
                    RoomCode = created.roomCode;

                    // `resumed` means the relay still had the room and its players; nothing
                    // was lost. A reclaimed code without resume means the relay restarted,
                    // so the phones must rejoin — but at least the code on the TV is right.
                    Debug.Log(created.resumed
                        ? $"[RelayClient] Resumed room {RoomCode} with {created.players?.Length ?? 0} players still in it"
                        : sameCode
                            ? $"[RelayClient] Reclaimed room code {RoomCode} — phones can reconnect"
                            : $"[RelayClient] Room code: {RoomCode}");

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
                {
                    var aim = JsonUtility.FromJson<AimMessage>(json);
                    if (trustedPlayerId != null) aim.playerId = trustedPlayerId;
                    AimReceived?.Invoke(aim);
                    break;
                }

                case MessageType.Throw:
                {
                    var thrown = JsonUtility.FromJson<ThrowMessage>(json);
                    if (trustedPlayerId != null) thrown.playerId = trustedPlayerId;
                    ThrowReceived?.Invoke(thrown);
                    break;
                }

                case MessageType.PickRock:
                {
                    var picked = JsonUtility.FromJson<PickRockMessage>(json);
                    if (trustedPlayerId != null) picked.playerId = trustedPlayerId;
                    PickRockReceived?.Invoke(picked);
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
            if (_waitingForLocalServices && LocalServices.HasInstance)
                LocalServices.Instance.StateChanged -= OnLocalServicesStateChanged;

            _cts?.Cancel();
            _socket?.Dispose();
            _sendLock.Dispose();
            base.OnDestroy();
        }
    }
}
