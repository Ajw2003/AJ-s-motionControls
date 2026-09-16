/**
 * Persistent per-device identity, stored in localStorage so a returning phone can rejoin
 * its own seat silently instead of the relay treating a retyped name as a brand-new player.
 *
 * The stored record is `{ deviceId, name, room, tutorialSeen }`. `deviceId` is what actually
 * goes over the wire as `playerId` — it never changes for this browser/device, so renaming
 * mid-match is a cosmetic SET_NAME, not a new player. `room` records which room code this
 * identity last joined, so app.js can tell "the URL's room code matches what we joined last
 * time" from "this is a different room, the join form should still ask". `tutorialSeen` is
 * whether the pre-round walkthrough has already auto-shown once on this device — it stays
 * reachable afterward through the on-screen "?" button regardless.
 *
 * Deliberate constraint (see docs/plans/seamless-rejoin.md Workstream C): deviceId is drawn
 * from the same uppercase A-Z2-9 alphabet the relay already generates room codes from, at 12
 * characters. The relay's sanitizePlayerId uppercases and caps at 16 chars, and relayToPlayers
 * uppercases targetPlayerId the same way — an id already uppercase and 12 chars long passes
 * both untouched, so relay/server.js needs no changes to support this.
 */

const STORAGE_KEY = 'rockSkipping.identity';

// Same alphabet as the relay's own room-code generator (server.js) and
// HotspotCredentialsStore's ambiguous-character exclusion isn't needed here — this string is
// never hand-typed, only ever read back out of localStorage by the same device that wrote it.
const ID_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ23456789';
const ID_LENGTH = 12;

function generateDeviceId() {
  // crypto.getRandomValues is available in every secure context this page requires anyway
  // (motion sensors already demand one), so there is no non-crypto fallback to maintain.
  const bytes = new Uint8Array(ID_LENGTH);
  crypto.getRandomValues(bytes);

  let id = '';
  for (let i = 0; i < ID_LENGTH; i++) id += ID_ALPHABET[bytes[i] % ID_ALPHABET.length];
  return id;
}

/**
 * Reads the stored identity, or null if there is none / storage is unavailable / the record
 * is corrupt. Every access goes through try/catch: private-mode Safari throws on
 * localStorage access rather than returning null, and a controller that crashes on that is
 * strictly worse than one that just re-asks for a name.
 */
export function loadIdentity() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;

    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed.deviceId !== 'string' || !parsed.deviceId) return null;

    return {
      deviceId: parsed.deviceId,
      name: typeof parsed.name === 'string' ? parsed.name : '',
      room: typeof parsed.room === 'string' ? parsed.room : '',
      tutorialSeen: parsed.tutorialSeen === true,
    };
  } catch {
    return null;
  }
}

/** Returns the stored identity's deviceId, minting and persisting a fresh one on first run. */
export function ensureDeviceId() {
  const existing = loadIdentity();
  if (existing) return existing.deviceId;

  const deviceId = generateDeviceId();
  saveIdentity({ deviceId, name: '', room: '' });
  return deviceId;
}

/** Merges `patch` into the stored identity (creating one from ensureDeviceId if needed). */
export function updateIdentity(patch) {
  const current = loadIdentity() ?? { deviceId: ensureDeviceId(), name: '', room: '' };
  saveIdentity({ ...current, ...patch });
}

function saveIdentity(identity) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(identity));
  } catch {
    /* private-mode Safari or a full quota — the identity just won't survive a reload */
  }
}
