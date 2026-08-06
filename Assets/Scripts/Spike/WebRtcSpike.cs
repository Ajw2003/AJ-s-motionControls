using System;
using System.Collections;
using System.Collections.Generic;
using Net;
using Unity.WebRTC;
using UnityEngine;

namespace Spike
{
    /// <summary>
    /// Answers one question and then gets deleted: <b>can a Unity peer reach a phone browser
    /// over the LAN when the browser refuses to tell it an IP address?</b>
    /// </summary>
    /// <remarks>
    /// This is scaffolding for HostingPlan.md §5, not shipping code. Phase 2 moves AIM and
    /// THROW onto a WebRTC DataChannel so gameplay stops routing through our service. The one
    /// assumption that could invalidate that design:
    ///
    /// Browsers obfuscate local addresses as randomised `.local` mDNS candidates (RFC 8828).
    /// A real offer measured against the deployed page produced nothing else — every candidate
    /// was a UUID hostname. So the browser never hands Unity a dialable address.
    ///
    /// The theory for why that is survivable: Unity is native and does NOT obfuscate, so it
    /// advertises its real LAN address. The browser dials that, and Unity learns the phone's
    /// address from the source of the incoming connectivity checks — a peer-reflexive
    /// candidate. Ordinary ICE, but unproven here.
    ///
    /// WHAT TO READ: the winning candidate pair, printed on connect. A remote candidate of
    /// type `prflx` means the theory held exactly as stated. `host` means Unity resolved the
    /// mDNS name itself, which is also a pass — by a different route. Failure to connect at
    /// all is the finding that would send Phase 2 back to the drawing board.
    ///
    /// HOW TO RUN: put this on a GameObject in a scene that also has RelayClient (Bootstrap
    /// creates one), press Play, then open the printed URL on a phone on the same Wi-Fi.
    /// </remarks>
    public class WebRtcSpike : MonoBehaviour
    {
        [Tooltip("Seconds to wait for a DataChannel before calling it a failure.")]
        [SerializeField] private float connectTimeout = 20f;

        [SerializeField] private bool verboseLogging = true;

        private RelayClient _relay;
        private RTCPeerConnection _pc;
        private RTCDataChannel _channel;
        private string _peerId;
        private bool _reported;

        // Held in fields, not assigned as inline lambdas. These are marshalled to native
        // code, and once assigned the only strong reference lives on the native side — so a
        // lambda can be collected and the callback silently never fires. Every sample in the
        // package does it this way; the first version of this file did not, and OnDataChannel
        // was the casualty.
        private DelegateOnIceCandidate _onIceCandidate;
        private DelegateOnIceConnectionChange _onIceConnectionChange;
        private DelegateOnConnectionStateChange _onConnectionStateChange;
        private DelegateOnDataChannel _onDataChannel;
        private DelegateOnOpen _onChannelOpen;
        private DelegateOnMessage _onChannelMessage;

        private string _status = "waiting for a room code…";
        private string _result;
        private readonly List<string> _log = new();
        private float _deadline;
        private bool _finished;

        // ------------------------------------------------------------------ wire shapes

        // Inbound: the relay stamps playerId server-side, so it is trustworthy.
        [Serializable] private class SdpMessage { public string type; public string playerId; public string sdp; }

        [Serializable]
        private class IceMessage
        {
            public string type;
            public string playerId;
            public string candidate;
            public string sdpMid;
            public int sdpMLineIndex;
        }

        // Outbound: host->player routing keys off targetPlayerId, so these are separate
        // types rather than the same shape with a renamed field. Hand-editing the JSON to
        // rename it worked and was a trap waiting for whoever touched it next.
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

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            if (!RelayClient.HasInstance)
            {
                Fail("No RelayClient — run this from the Bootstrap scene.");
                return;
            }

            _relay = RelayClient.Instance;
            _relay.RawMessageReceived += OnRelayMessage;
            _deadline = Time.time + connectTimeout;
        }

        private void OnDestroy()
        {
            if (_relay != null) _relay.RawMessageReceived -= OnRelayMessage;
            _channel?.Close();
            _pc?.Close();
            _pc?.Dispose();
        }

        private void Update()
        {
            if (_finished) return;

            if (string.IsNullOrEmpty(_peerId))
            {
                _status = string.IsNullOrEmpty(_relay?.RoomCode)
                    ? "waiting for a room code…"
                    : $"On an Android phone on this same Wi-Fi, open:\n\n" +
                      $"    {_relay.PublicOrigin}/spike.html\n\n" +
                      $"and type the code:   {_relay.RoomCode}";
                _deadline = Time.time + connectTimeout; // the clock starts when a phone shows up
                return;
            }

            // Backstop. The spike's entire job is to produce a reading, so it must not depend
            // on a callback arriving — the first version did, lost OnDataChannel to the GC,
            // and reported FAIL for a connection that had in fact succeeded. Polling the
            // connection state cannot be missed the same way.
            if (!_reported && _pc != null && _pc.ConnectionState == RTCPeerConnectionState.Connected)
            {
                Log("peer connection reported Connected — reading stats");
                Report();
                return;
            }

            if (Time.time > _deadline)
            {
                var state = _pc == null ? "no peer connection" : $"pc={_pc.ConnectionState}, ice={_pc.IceConnectionState}";
                Fail($"timed out waiting for the DataChannel to open ({state})");
            }
        }

        /// <summary>Guarded so the callback and the poll cannot both fire it.</summary>
        private void Report()
        {
            if (_reported) return;
            _reported = true;
            StartCoroutine(ReportWinningPair());
        }

        // ------------------------------------------------------------------ signalling

        private void OnRelayMessage(string type, string json)
        {
            switch (type)
            {
                case "RTC_OFFER":
                    StartCoroutine(HandleOffer(JsonUtility.FromJson<SdpMessage>(json)));
                    break;

                case "RTC_ICE":
                    HandleRemoteIce(JsonUtility.FromJson<IceMessage>(json));
                    break;
            }
        }

        /// <summary>
        /// The phone offers and Unity answers, because the phone knows when it has joined.
        /// </summary>
        private IEnumerator HandleOffer(SdpMessage message)
        {
            _peerId = message.playerId;
            _deadline = Time.time + connectTimeout;
            Log($"offer from {_peerId} ({message.sdp?.Length ?? 0} bytes)");

            // No ICE servers on purpose: a LAN peer connection gathers host candidates
            // locally, so STUN would only add a dependency this design does not need.
            var config = new RTCConfiguration { iceServers = Array.Empty<RTCIceServer>() };
            _pc = new RTCPeerConnection(ref config);

            _onIceCandidate = candidate =>
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.Candidate)) return;
                Log($"local candidate: {Describe(candidate.Candidate)}");
                _relay.Send(JsonUtility.ToJson(new IceOut
                {
                    type = "RTC_ICE",
                    targetPlayerId = _peerId,
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
                }));
            };

            _onIceConnectionChange = state => Log($"ice: {state}");
            _onConnectionStateChange = state => Log($"pc: {state}");

            // The phone creates the channel, so Unity receives it rather than making one.
            _onDataChannel = channel =>
            {
                _channel = channel;
                Log($"data channel received, state={channel.ReadyState}");

                _onChannelOpen = () => { Log("DATACHANNEL OPEN"); Report(); };
                _onChannelMessage = bytes => Log($"phone says: {System.Text.Encoding.UTF8.GetString(bytes)}");

                channel.OnOpen = _onChannelOpen;
                channel.OnMessage = _onChannelMessage;

                // The channel is frequently ALREADY open by the time this fires, in which
                // case OnOpen never will — waiting on it is a guaranteed timeout on a
                // connection that actually succeeded.
                if (channel.ReadyState == RTCDataChannelState.Open)
                {
                    Log("channel was already open when handed over");
                    Report();
                }
            };

            _pc.OnIceCandidate = _onIceCandidate;
            _pc.OnIceConnectionChange = _onIceConnectionChange;
            _pc.OnConnectionStateChange = _onConnectionStateChange;
            _pc.OnDataChannel = _onDataChannel;

            var offer = new RTCSessionDescription { type = RTCSdpType.Offer, sdp = message.sdp };
            var setRemote = _pc.SetRemoteDescription(ref offer);
            yield return setRemote;
            if (setRemote.IsError) { Fail($"SetRemoteDescription: {setRemote.Error.message}"); yield break; }

            var createAnswer = _pc.CreateAnswer();
            yield return createAnswer;
            if (createAnswer.IsError) { Fail($"CreateAnswer: {createAnswer.Error.message}"); yield break; }

            var answer = createAnswer.Desc;
            var setLocal = _pc.SetLocalDescription(ref answer);
            yield return setLocal;
            if (setLocal.IsError) { Fail($"SetLocalDescription: {setLocal.Error.message}"); yield break; }

            _status = "answered — waiting for the DataChannel";
            _relay.Send(JsonUtility.ToJson(new AnswerOut
            {
                type = "RTC_ANSWER",
                targetPlayerId = _peerId,
                sdp = answer.sdp,
            }));
        }

        private void HandleRemoteIce(IceMessage message)
        {
            if (string.IsNullOrEmpty(message.candidate) || _pc == null) return;

            Log($"remote candidate: {Describe(message.candidate)}");

            var init = new RTCIceCandidateInit
            {
                candidate = message.candidate,
                sdpMid = message.sdpMid,
                sdpMLineIndex = message.sdpMLineIndex,
            };

            _pc.AddIceCandidate(new RTCIceCandidate(init));
        }

        // ------------------------------------------------------------------ the answer

        /// <summary>
        /// The whole point of the spike: which candidate pair actually carried the connection,
        /// and what type the remote side of it was.
        /// </summary>
        private IEnumerator ReportWinningPair()
        {
            var stats = _pc.GetStats();
            yield return stats;

            if (stats.IsError) { Fail($"GetStats: {stats.Error.message}"); yield break; }

            var candidates = new Dictionary<string, RTCIceCandidateStats>();
            RTCIceCandidatePairStats winner = null;

            foreach (var report in stats.Value.Stats.Values)
            {
                if (report is RTCIceCandidateStats candidate) candidates[candidate.Id] = candidate;
                if (report is RTCIceCandidatePairStats pair && pair.state == "succeeded") winner = pair;
            }

            if (winner == null)
            {
                _result = $"CONNECTED (pc={_pc.ConnectionState}, ice={_pc.IceConnectionState}), but no\n" +
                          "succeeded candidate pair in stats — inconclusive, try again.";
            }
            else
            {
                var local = candidates.TryGetValue(winner.localCandidateId, out var l) ? l.candidateType : "?";
                var remote = candidates.TryGetValue(winner.remoteCandidateId, out var r) ? r.candidateType : "?";

                _result =
                    $"PASS — DataChannel open.\n" +
                    $"winning pair: local={local}  remote={remote}\n" +
                    (remote == "prflx"
                        ? "remote is PEER-REFLEXIVE: Unity learned the phone's address from the\n" +
                          "incoming connectivity check, exactly as HostingPlan §5 predicted."
                        : $"remote is '{remote}': Unity reached the phone without needing a\n" +
                          "peer-reflexive candidate. Also a pass, by a different route.");
            }

            _finished = true;
            _status = "done";
            Debug.Log($"[WebRtcSpike] {_result}");

            if (_channel != null && _channel.ReadyState == RTCDataChannelState.Open)
            {
                _channel.Send("unity-ack");
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Pulls the readable part out of a candidate line: type and whether it is mDNS.</summary>
        private static string Describe(string candidateLine)
        {
            var parts = candidateLine.Split(' ');
            var type = Array.IndexOf(parts, "typ");
            var kind = type >= 0 && type + 1 < parts.Length ? parts[type + 1] : "?";
            var address = parts.Length > 4 ? parts[4] : "?";
            return $"{kind} {address}{(address.EndsWith(".local") ? "  (mDNS — no IP given)" : "")}";
        }

        private void Log(string line)
        {
            _log.Add(line);
            if (_log.Count > 14) _log.RemoveAt(0);
            if (verboseLogging) Debug.Log($"[WebRtcSpike] {line}");
        }

        private void Fail(string why)
        {
            _finished = true;
            _result = $"FAIL — {why}";
            _status = "done";
            Debug.LogError($"[WebRtcSpike] {_result}");
        }

        private void OnGUI()
        {
            var box = new Rect(20, 20, Mathf.Min(Screen.width - 40, 900), 460);
            GUI.Box(box, "WebRTC spike — HostingPlan §5");

            GUILayout.BeginArea(new Rect(box.x + 14, box.y + 30, box.width - 28, box.height - 44));

            var big = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            GUILayout.Label(_status, big);

            if (!string.IsNullOrEmpty(_result))
            {
                var previous = GUI.color;
                GUI.color = _result.StartsWith("PASS") ? new Color(0.6f, 1f, 0.7f) : new Color(1f, 0.55f, 0.5f);
                GUILayout.Label(_result, new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true });
                GUI.color = previous;
            }

            GUILayout.Space(8);
            foreach (var line in _log) GUILayout.Label(line);

            GUILayout.EndArea();
        }
    }
}
