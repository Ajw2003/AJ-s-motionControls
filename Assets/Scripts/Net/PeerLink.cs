using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using EventTypes.MatchEvents;
using EventSystems;
using Singleton;
using Unity.WebRTC;
using UnityEngine;

namespace Net
{
    /// <summary>
    /// Holds a direct WebRTC DataChannel to each phone, so aim frames and throws travel
    /// straight across the LAN instead of round-tripping through the hosted relay.
    /// </summary>
    /// <remarks>
    /// This is HostingPlan.md §5. The relay still carries the controller page, the room
    /// code, turn state and scores — all low-rate, all needing reliability. What moves here
    /// is the only traffic where latency is felt: AIM at 20Hz and the THROW that ends it.
    ///
    /// <b>Nothing downstream knows this exists.</b> Received messages go into
    /// RelayClient.IngestFromPeer, which drops them into the same queue and the same
    /// dispatch as relay traffic, so AimPreview, RockLauncher and RelayDebugHud keep
    /// subscribing to RelayClient and never learn which transport delivered a frame.
    ///
    /// <b>Failure is not fatal by design.</b> A peer connection that never establishes is
    /// simply never used: the phone keeps sending over the WebSocket, which is Phase 1 and
    /// is proven. That is why Phase 1 was built first.
    ///
    /// Two hard-won details, both from the spike that preceded this:
    ///
    /// 1. <b>Every native callback is held in a field.</b> These delegates are marshalled to
    ///    native code, and once assigned the only strong reference lives on the native side,
    ///    so a lambda can be collected and the callback silently stops firing. The symptom is
    ///    a working connection that appears never to open.
    /// 2. <b>A received channel is often already open.</b> OnDataChannel frequently fires with
    ///    ReadyState == Open, in which case OnOpen never will. Waiting for it is a guaranteed
    ///    timeout on a connection that succeeded.
    /// </remarks>
    public class PeerLink : SingletonBase<PeerLink>
    {
        [Tooltip("Give up on a peer connection after this long and leave that phone on the relay.")]
        [SerializeField] private float establishTimeout = 8f;

        [SerializeField] private bool verboseLogging;

        /// <summary>How many phones are currently on a direct connection.</summary>
        public int DirectPeerCount { get; private set; }

        /// <summary>Every phone that has ever been offered a peer connection this session.</summary>
        public int KnownPeerCount => _peers.Count;

        /// <summary>True when this player's traffic is arriving directly rather than via the relay.</summary>
        public bool IsDirect(string playerId) =>
            playerId != null && _peers.TryGetValue(playerId, out var peer) && peer.IsOpen;

        private readonly Dictionary<string, Peer> _peers = new();
        private RelayClient _relay;

        // ------------------------------------------------------------------ wire shapes

        [Serializable] private class SdpIn { public string playerId; public string sdp; }

        [Serializable]
        private class IceIn
        {
            public string playerId;
            public string candidate;
            public string sdpMid;
            public int sdpMLineIndex;
        }

        [Serializable] private class AnswerOut { public string type; public string targetPlayerId; public string sdp; }

        [Serializable]
        private class IceOut
        {
            public string type;
            public string targetPlayerId;
            public string candidate;
            public string sdpMid;
            public int sdpMLineIndex;
        }

        // ------------------------------------------------------------------ per-phone state

        private sealed class Peer
        {
            public string PlayerId;
            public RTCPeerConnection Connection;
            public RTCDataChannel Channel;
            public float Deadline;
            public bool GaveUp;

            // Held so the GC cannot collect them out from under the native side.
            public DelegateOnIceCandidate OnIceCandidate;
            public DelegateOnIceConnectionChange OnIceConnectionChange;
            public DelegateOnConnectionStateChange OnConnectionStateChange;
            public DelegateOnDataChannel OnDataChannel;
            public DelegateOnOpen OnChannelOpen;
            public DelegateOnClose OnChannelClose;
            public DelegateOnMessage OnChannelMessage;

            public bool IsOpen => Channel != null && Channel.ReadyState == RTCDataChannelState.Open;

            public void Dispose()
            {
                try { Channel?.Close(); } catch { /* already gone */ }
                try { Connection?.Close(); Connection?.Dispose(); } catch { /* already gone */ }
                Channel = null;
                Connection = null;
            }
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Awake()
        {
            persistBetweenScenes = true;
            base.Awake();
        }

        private void Start()
        {
            if (!RelayClient.HasInstance)
            {
                Debug.LogWarning("[PeerLink] No RelayClient — peer connections disabled, relay only.");
                enabled = false;
                return;
            }

            _relay = RelayClient.Instance;
            _relay.RawMessageReceived += OnRelayMessage;

            EventManager.Instance?.Subscribe<PlayerLeftEvent>(this, e => Drop(e.PlayerId));
        }

        private void Update()
        {
            var open = 0;

            foreach (var peer in _peers.Values)
            {
                if (peer.IsOpen) { open++; continue; }
                if (peer.GaveUp || peer.Connection == null) continue;

                if (Time.time > peer.Deadline)
                {
                    peer.GaveUp = true;
                    Debug.LogWarning($"[PeerLink] {peer.PlayerId}: no direct connection after " +
                                     $"{establishTimeout:F0}s (pc={peer.Connection.ConnectionState}) — " +
                                     "staying on the relay.");
                }
            }

            DirectPeerCount = open;
        }

        protected override void OnDestroy()
        {
            if (_relay != null) _relay.RawMessageReceived -= OnRelayMessage;
            EventManager.Instance?.UnsubscribeFromAllEvents(this);

            foreach (var peer in _peers.Values) peer.Dispose();
            _peers.Clear();

            base.OnDestroy();
        }

        // ------------------------------------------------------------------ signalling

        private void OnRelayMessage(string type, string json)
        {
            switch (type)
            {
                case MessageType.RtcOffer:
                    StartCoroutine(AcceptOffer(JsonUtility.FromJson<SdpIn>(json)));
                    break;

                case MessageType.RtcIce:
                    AddRemoteCandidate(JsonUtility.FromJson<IceIn>(json));
                    break;
            }
        }

        /// <summary>
        /// The phone offers and the PC answers. That direction is deliberate: the phone
        /// knows when it has finished joining, and it is the side that wants the channel.
        /// </summary>
        private IEnumerator AcceptOffer(SdpIn offer)
        {
            if (offer == null || string.IsNullOrEmpty(offer.playerId) || string.IsNullOrEmpty(offer.sdp))
            {
                yield break;
            }

            // A phone that reconnects renegotiates from scratch; the old one is dead.
            Drop(offer.playerId);

            var peer = new Peer { PlayerId = offer.playerId, Deadline = Time.time + establishTimeout };
            _peers[offer.playerId] = peer;

            // No ICE servers. Host candidates are gathered locally and, per the spike, are
            // enough on a LAN — so there is no STUN dependency to add back.
            var config = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() };
            peer.Connection = new RTCPeerConnection(ref config);

            WireCallbacks(peer);

            var remote = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = offer.sdp };
            var setRemote = peer.Connection.SetRemoteDescription(ref remote);
            yield return setRemote;
            if (setRemote.IsError) { FailPeer(peer, $"SetRemoteDescription: {setRemote.Error.message}"); yield break; }

            var createAnswer = peer.Connection.CreateAnswer();
            yield return createAnswer;
            if (createAnswer.IsError) { FailPeer(peer, $"CreateAnswer: {createAnswer.Error.message}"); yield break; }

            var answer = createAnswer.Desc;
            var setLocal = peer.Connection.SetLocalDescription(ref answer);
            yield return setLocal;
            if (setLocal.IsError) { FailPeer(peer, $"SetLocalDescription: {setLocal.Error.message}"); yield break; }

            _relay.Send(JsonUtility.ToJson(new AnswerOut
            {
                type = MessageType.RtcAnswer,
                targetPlayerId = peer.PlayerId,
                sdp = answer.sdp,
            }));

            Log($"{peer.PlayerId}: answered, negotiating");
        }

        private void WireCallbacks(Peer peer)
        {
            peer.OnIceCandidate = candidate =>
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;

                _relay.Send(JsonUtility.ToJson(new IceOut
                {
                    type = MessageType.RtcIce,
                    targetPlayerId = peer.PlayerId,
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                }));
            };

            peer.OnIceConnectionChange = state => Log($"{peer.PlayerId}: ice {state}");

            peer.OnConnectionStateChange = state =>
            {
                Log($"{peer.PlayerId}: pc {state}");

                // Losing the peer connection mid-match is survivable: the phone notices too
                // and reverts to the relay, so the game continues with a softer preview.
                if (state == RTCPeerConnectionState.Failed || state == RTCPeerConnectionState.Closed)
                {
                    peer.GaveUp = true;
                }
            };

            peer.OnDataChannel = channel =>
            {
                peer.Channel = channel;

                peer.OnChannelOpen = () => Debug.Log($"[PeerLink] {peer.PlayerId}: DIRECT — input is now peer-to-peer");
                peer.OnChannelClose = () => Log($"{peer.PlayerId}: channel closed, back on the relay");
                peer.OnChannelMessage = bytes =>
                {
                    // Identity comes from which channel this arrived on, never from the
                    // payload — the same guarantee the relay provides by stamping it.
                    _relay.IngestFromPeer(Encoding.UTF8.GetString(bytes), peer.PlayerId);
                };

                channel.OnOpen = peer.OnChannelOpen;
                channel.OnClose = peer.OnChannelClose;
                channel.OnMessage = peer.OnChannelMessage;

                // Frequently already open by the time it is handed over, in which case
                // OnOpen will never fire.
                if (channel.ReadyState == RTCDataChannelState.Open) peer.OnChannelOpen();
            };

            peer.Connection.OnIceCandidate = peer.OnIceCandidate;
            peer.Connection.OnIceConnectionChange = peer.OnIceConnectionChange;
            peer.Connection.OnConnectionStateChange = peer.OnConnectionStateChange;
            peer.Connection.OnDataChannel = peer.OnDataChannel;
        }

        private void AddRemoteCandidate(IceIn message)
        {
            if (message == null || string.IsNullOrEmpty(message.candidate)) return;
            if (!_peers.TryGetValue(message.playerId ?? string.Empty, out var peer)) return;
            if (peer.Connection == null) return;

            peer.Connection.AddIceCandidate(new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate = message.candidate,
                sdpMid = message.sdpMid,
                sdpMLineIndex = message.sdpMLineIndex,
            }));
        }

        // ------------------------------------------------------------------ teardown

        private void Drop(string playerId)
        {
            if (playerId == null || !_peers.TryGetValue(playerId, out var peer)) return;

            peer.Dispose();
            _peers.Remove(playerId);
            Log($"{playerId}: peer dropped");
        }

        private void FailPeer(Peer peer, string why)
        {
            Debug.LogWarning($"[PeerLink] {peer.PlayerId}: {why} — staying on the relay.");
            peer.GaveUp = true;
            peer.Dispose();
            _peers.Remove(peer.PlayerId);
        }

        private void Log(string line)
        {
            if (verboseLogging) Debug.Log($"[PeerLink] {line}");
        }
    }
}
