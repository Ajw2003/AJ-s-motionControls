/**
 * Direct WebRTC DataChannel to the host PC, for the traffic where latency is felt.
 *
 * AIM at 20Hz and the THROW that ends it go straight across the LAN. Everything else —
 * joining, turn state, scores, SPECTATE — stays on the WebSocket to the relay, where
 * ordering and reliability matter more than a few milliseconds.
 *
 * The whole class is an optimisation with a guaranteed floor: `send()` uses the data
 * channel when it is open and the relay socket otherwise, so a peer connection that never
 * establishes costs nothing but latency. Callers do not branch, and must not — the
 * fallback has to be invisible or it will rot.
 *
 * This side creates the offer, because it is the side that knows when it has joined.
 */

export const Transport = {
  RELAY: 'relay',
  DIRECT: 'direct',
};

export class PeerLink {
  /**
   * @param {object}   options
   * @param {object}   options.connection    RelayConnection, used for signalling and fallback.
   * @param {function} [options.onTransportChange] Called with a Transport value when it flips.
   */
  constructor({ connection, onTransportChange } = {}) {
    this.connection = connection;
    this.onTransportChange = onTransportChange ?? (() => {});

    this.pc = null;
    this.channel = null;
    this.transport = Transport.RELAY;

    // Candidates can arrive before setRemoteDescription has been applied; adding one
    // then throws. Queue until the answer lands.
    this.pendingCandidates = [];
    this.remoteReady = false;
  }

  get isDirect() {
    return this.channel !== null && this.channel.readyState === 'open';
  }

  /**
   * Send an AIM or THROW payload by the fastest transport currently available.
   * @returns {string} which transport carried it
   */
  send(payload) {
    if (this.isDirect) {
      try {
        this.channel.send(JSON.stringify(payload));
        return Transport.DIRECT;
      } catch {
        // A channel that fails mid-send is a channel that is closing. Fall through
        // rather than dropping the message.
      }
    }

    this.connection?.send(payload);
    return Transport.RELAY;
  }

  /** Begin negotiation. Safe to call again; the previous attempt is torn down. */
  async start() {
    this.close();

    // No ICE servers: host candidates are gathered locally and are enough on a LAN,
    // so there is no STUN service to depend on.
    this.pc = new RTCPeerConnection({ iceServers: [] });

    this.pc.onicecandidate = (event) => {
      if (!event.candidate) return;
      this.connection?.send({
        type: 'RTC_ICE',
        candidate: event.candidate.candidate,
        sdpMid: event.candidate.sdpMid,
        sdpMLineIndex: event.candidate.sdpMLineIndex,
      });
    };

    this.pc.onconnectionstatechange = () => {
      if (
        this.pc &&
        (this.pc.connectionState === 'failed' || this.pc.connectionState === 'closed')
      ) {
        this._setTransport(Transport.RELAY);
      }
    };

    // Ordered and reliable. AIM is lossy by design, but SCTP retransmits are cheap on a
    // LAN and out-of-order aim frames would undo the seq check on the Unity side.
    this.channel = this.pc.createDataChannel('input', { ordered: true });
    this.channel.onopen = () => this._setTransport(Transport.DIRECT);
    this.channel.onclose = () => this._setTransport(Transport.RELAY);
    this.channel.onerror = () => this._setTransport(Transport.RELAY);

    const offer = await this.pc.createOffer();
    await this.pc.setLocalDescription(offer);
    this.connection?.send({ type: 'RTC_OFFER', sdp: offer.sdp });
  }

  /** Feed signalling in from the relay. Returns true if the message was ours. */
  handleSignal(message) {
    if (message.type === 'RTC_ANSWER') {
      this._applyAnswer(message.sdp);
      return true;
    }

    if (message.type === 'RTC_ICE') {
      this._addCandidate(message);
      return true;
    }

    return false;
  }

  close() {
    try {
      this.channel?.close();
    } catch {
      /* already gone */
    }
    try {
      this.pc?.close();
    } catch {
      /* already gone */
    }

    this.channel = null;
    this.pc = null;
    this.pendingCandidates = [];
    this.remoteReady = false;
    this._setTransport(Transport.RELAY);
  }

  // ---------------------------------------------------------------- internals

  async _applyAnswer(sdp) {
    if (!this.pc || !sdp) return;

    try {
      await this.pc.setRemoteDescription({ type: 'answer', sdp });
      this.remoteReady = true;

      for (const candidate of this.pendingCandidates) {
        await this.pc.addIceCandidate(candidate).catch(() => {});
      }
      this.pendingCandidates = [];
    } catch {
      this._setTransport(Transport.RELAY); // stay on the relay; nothing else breaks
    }
  }

  async _addCandidate(message) {
    if (!this.pc || !message.candidate) return;

    const candidate = {
      candidate: message.candidate,
      sdpMid: message.sdpMid,
      sdpMLineIndex: message.sdpMLineIndex,
    };

    if (!this.remoteReady) {
      this.pendingCandidates.push(candidate);
      return;
    }

    await this.pc.addIceCandidate(candidate).catch(() => {});
  }

  _setTransport(next) {
    if (this.transport === next) return;
    this.transport = next;
    this.onTransportChange(next);
  }
}
