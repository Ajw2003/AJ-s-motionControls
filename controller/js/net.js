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
      if (message.type === 'JOINED') this._setStatus(ConnectionStatus.JOINED);
      this.onMessage(message);
    };

    this.socket.onclose = () => {
      if (this.closedByUs) return;
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

  /** Fire-and-forget. Dropped silently while disconnected — AIM frames are lossy by design. */
  send(payload) {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify(payload));
    return true;
  }

  close() {
    this.closedByUs = true;
    this.socket?.close();
  }

  _setStatus(status) {
    if (this.status === status) return;
    this.status = status;
    this.onStatus(status);
  }
}
