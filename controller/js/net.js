/**
 * WebSocket client for the phone controller.
 *
 * The relay serves this page, so the socket URL is derived from the page origin
 * rather than configured. That is deliberate: a hardcoded ws:// address would
 * break the moment the page is served over HTTPS (mixed content), which is
 * exactly the setup motion sensors require.
 */

export const ConnectionStatus = {
  CONNECTING: 'connecting',
  CONNECTED: 'connected',
  JOINED: 'joined',
  RECONNECTING: 'reconnecting',
  ERROR: 'error',
};

// How long to keep trying to get back into the same room before admitting defeat.
// A host that dropped is usually back within seconds — a redeploy, a Wi-Fi blip,
// the machine restarting underneath it — and the relay holds the room open for a
// grace period precisely so this can succeed. Giving up on the first refusal is
// what made a two-second hiccup look like the game had ended.
const REJOIN_WINDOW_MS = 45_000;
const REJOIN_RETRY_MS = 1500;

/** Same host, protocol upgraded to wss when the page is secure. */
export function deriveSocketUrl(loc = window.location) {
  const protocol = loc.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${protocol}//${loc.host}`;
}

const BACKOFF_START_MS = 500;
const BACKOFF_MAX_MS = 8000;

export class RelayConnection {
  constructor({ onMessage, onStatus, url } = {}) {
    this.url = url ?? deriveSocketUrl();
    this.onMessage = onMessage ?? (() => {});
    this.onStatus = onStatus ?? (() => {});

    this.socket = null;
    this.status = null;
    this.backoff = BACKOFF_START_MS;
    this.closedByUs = false;

    // Kept so a dropped connection can re-join the same slot without the player
    // retyping anything — phones sleep and lose Wi-Fi constantly at a party.
    this.credentials = null;

    // Set while we are trying to get back into a room we were already in. Until it
    // expires, a "no such room" answer is treated as "not yet" rather than "no".
    this.rejoinUntil = 0;
    this.rejoinTimer = null;
  }

  /** True while a refusal to join should be retried rather than surfaced. */
  get isRejoining() {
    return this.credentials !== null && Date.now() < this.rejoinUntil;
  }

  /**
   * Begin tolerating join refusals for a while — called when the host drops, since
   * the room is expected to come back under the same code.
   */
  expectHostBack() {
    if (!this.credentials) return;
    this.rejoinUntil = Date.now() + REJOIN_WINDOW_MS;
    this._setStatus(ConnectionStatus.RECONNECTING);
  }

  connect() {
    this.closedByUs = false;
    this._setStatus(this.credentials ? ConnectionStatus.RECONNECTING : ConnectionStatus.CONNECTING);

    this.socket = new WebSocket(this.url);

    this.socket.onopen = () => {
      this.backoff = BACKOFF_START_MS;
      this._setStatus(ConnectionStatus.CONNECTED);
      if (this.credentials) this._sendJoin();
    };

    this.socket.onmessage = (event) => {
      let message;
      try {
        message = JSON.parse(event.data);
      } catch {
        return; // the relay only ever sends JSON; anything else is noise
      }
      if (message.type === 'JOINED') {
        this.rejoinUntil = 0; // we are back in; stop tolerating refusals
        this._setStatus(ConnectionStatus.JOINED);
      }

      // The host is expected back under the same code, so hold the seat rather than
      // dropping the player to the join screen for what is usually a two-second gap.
      if (message.type === 'HOST_LEFT' || message.type === 'ROOM_CLOSED') {
        this.expectHostBack();
      }

      // A refusal during that window is "not yet", not "no". Swallow it and retry;
      // surfacing it is what sent players back to the join screen mid-match.
      if (message.type === 'ERROR' && this.isRejoining) {
        this._scheduleRejoin();
        return;
      }

      this.onMessage(message);
    };

    this.socket.onclose = () => {
      if (this.closedByUs) return;

      // A socket that dies without a HOST_LEFT or ROOM_CLOSED first is the relay
      // itself going away — a redeploy, a crash, the machine being restarted. That
      // is exactly the case worth tolerating, and the one where no warning message
      // can possibly arrive, so the rejoin window has to open on the close itself.
      if (this.credentials && !this.isRejoining) this.expectHostBack();

      this._setStatus(ConnectionStatus.RECONNECTING);
      setTimeout(() => this.connect(), this.backoff);
      this.backoff = Math.min(this.backoff * 2, BACKOFF_MAX_MS);
    };

    this.socket.onerror = () => this._setStatus(ConnectionStatus.ERROR);
  }

  joinRoom(roomCode, playerId) {
    this.credentials = { roomCode, playerId };
    this._sendJoin();
  }

  _sendJoin() {
    if (!this.credentials) return;
    this.send({ type: 'JOIN_ROOM', ...this.credentials });
  }

  _scheduleRejoin() {
    if (this.rejoinTimer) return;

    this._setStatus(ConnectionStatus.RECONNECTING);
    this.rejoinTimer = setTimeout(() => {
      this.rejoinTimer = null;
      if (!this.isRejoining) return;

      if (this.socket?.readyState === WebSocket.OPEN) this._sendJoin();
      else this.connect();
    }, REJOIN_RETRY_MS);
  }

  /** Fire-and-forget. Dropped silently while disconnected — AIM frames are lossy by design. */
  send(payload) {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify(payload));
    return true;
  }

  /**
   * Latest-wins send for cosmetic streams. Skipped whenever bytes are still queued
   * on the socket: TCP delivers everything in order, so once the radio stalls,
   * every buffered AIM frame arrives late and replays a swing the player already
   * finished. The newest sample is the only one worth sending — a skipped frame
   * costs nothing because a fresher one is at most a sensor tick away.
   */
  sendIfClear(payload) {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    if (this.socket.bufferedAmount > 0) return false;
    this.socket.send(JSON.stringify(payload));
    return true;
  }

  close() {
    this.closedByUs = true;
    this.rejoinUntil = 0;
    if (this.rejoinTimer) {
      clearTimeout(this.rejoinTimer);
      this.rejoinTimer = null;
    }
    this.socket?.close();
  }

  _setStatus(status) {
    if (this.status === status) return;
    this.status = status;
    this.onStatus(status);
  }
}
