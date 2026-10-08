'use strict';

// Per-device join funnel keyed by IP, so its counts are a hint and never a roster of people.
// doc-ref 9607 docs/4-systems/Networking.md

/** Funnel stages, ordered. A device's stage only ever increases — see `record`. */
const Stage = {
  UNKNOWN: 0,
  ASSOCIATED: 1,
  PAGE_OPEN: 2,
  PLAYING: 3,
};

// Stale timeouts differ per stage, so a device that only ever sent DNS expires in about two minutes.
// doc-ref 8b00 docs/4-systems/Networking.md
const IDLE_TIMEOUT_MS = 10 * 60 * 1000;
const ASSOCIATED_IDLE_TIMEOUT_MS = 2 * 60 * 1000;

/** The idle timeout that applies to a device at `stage`. */
function idleTimeoutFor(stage) {
  return stage >= Stage.PAGE_OPEN ? IDLE_TIMEOUT_MS : ASSOCIATED_IDLE_TIMEOUT_MS;
}

/**
 * Strips the IPv4-mapped IPv6 prefix Node reports on a dual-stack socket
 * (`::ffff:192.168.137.5`), so the same phone is one entry whether it was seen over a
 * UDP DNS packet or a TCP socket rather than two.
 */
function normalizeAddress(address) {
  if (typeof address !== 'string') return null;
  const trimmed = address.trim();
  if (trimmed.length === 0) return null;
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(trimmed);
  return mapped ? mapped[1] : trimmed;
}

// Options: isTracked confines tracking to the hotspot subnet, onChange wakes the host, now is an injectable clock.
// doc-ref 6311 docs/4-systems/Networking.md
function createFunnel(options) {
  const { isTracked, onChange = () => {}, now = () => Date.now() } = options;

  if (typeof isTracked !== 'function') throw new Error('isTracked is required');

  /** @type {Map<string, {stage: number, lastSeen: number}>} */
  const devices = new Map();
  let lastReported = null;

  function sweep() {
    const at = now();
    for (const [ip, device] of devices) {
      if (device.lastSeen < at - idleTimeoutFor(device.stage)) devices.delete(ip);
    }
  }

  function counts() {
    sweep();
    let associated = 0;
    let pageOpen = 0;
    let playing = 0;
    for (const [, device] of devices) {
      associated += 1;
      if (device.stage >= Stage.PAGE_OPEN) pageOpen += 1;
      if (device.stage >= Stage.PLAYING) playing += 1;
    }
    return { associated, pageOpen, playing };
  }

  function notifyIfChanged() {
    const current = counts();
    const key = `${current.associated}/${current.pageOpen}/${current.playing}`;
    if (key === lastReported) return;
    lastReported = key;
    onChange(current);
  }

  /**
   * Records that `address` reached `stage`. Safe to call on every packet — it is a Map
   * write and a comparison, on a path that already parses a DNS query or serves a file.
   *
   * A stage never regresses. The signals arrive out of order in practice (a phone that
   * rejoins a known network sends its connectivity probes *after* the browser has already been
   * restored), and letting a late ASSOCIATED pull a playing device back down would make
   * the TV nag someone who is mid-throw.
   */
  function record(address, stage) {
    const ip = normalizeAddress(address);
    if (!ip || !isTracked(ip)) return;

    const existing = devices.get(ip);
    if (existing) {
      existing.lastSeen = now();
      if (stage <= existing.stage) return;
      existing.stage = stage;
    } else {
      devices.set(ip, { stage, lastSeen: now() });
    }

    notifyIfChanged();
  }

  // Deliberately no reset(): a host restart (stopping and starting the Unity Editor, the
  // common case) does not move any phone off the hotspot, so the stages recorded for those
  // devices are still true and clearing them would make the TV re-nag people who are
  // already playing. Devices leave the funnel by going quiet for IDLE_TIMEOUT_MS, which is
  // the only event that actually means "that phone is gone".
  return { record, counts };
}

module.exports = {
  createFunnel,
  normalizeAddress,
  Stage,
  IDLE_TIMEOUT_MS,
  ASSOCIATED_IDLE_TIMEOUT_MS,
};
