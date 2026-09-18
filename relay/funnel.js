'use strict';

/**
 * Per-device join funnel.
 *
 * Joining is three events, not one, and until now only the last of them reached Unity —
 * so every earlier drop-off looked identical on the TV: the player count simply did not
 * go up. See docs/plans/join-failure-reduction.md §2.
 *
 *   ASSOCIATED  the phone is on the hotspot Wi-Fi     (a DNS query arrives from its IP)
 *   PAGE_OPEN   the phone loaded the controller       (index.html served to its IP)
 *   PLAYING     the phone joined a room               (a JOIN_ROOM websocket from its IP)
 *
 * The TV depends on the distinction, so it can say "phone connected — now scan the second
 * code" instead of saying nothing.
 *
 * Keyed by IP, which is the only identifier available at the DNS layer — there is no
 * cookie, no user agent worth trusting, and no session. That makes a
 * count of devices, never a roster of people: two phones behind one address would count
 * once, and a phone that changes address counts twice. Callers must treat it as a hint.
 */

/** Funnel stages, ordered. A device's stage only ever increases — see `record`. */
const Stage = {
  UNKNOWN: 0,
  ASSOCIATED: 1,
  PAGE_OPEN: 2,
  PLAYING: 3,
};

/**
 * How long a silent device stays counted, per stage it reached.
 *
 * One timeout cannot serve both ends of the funnel. A device that reached PAGE_OPEN or
 * PLAYING may legitimately go quiet for a long time — someone mid-match is not sending DNS
 * queries, and dropping them would make the TV nag a player who is holding a rock. A device
 * that only ever appeared in a DNS log is different: both platforms re-probe every 30–60s
 * while associated, so one that has said nothing for two minutes has almost certainly left
 * the network, and every second it stays counted is a second the TV tells the room to scan a
 * code for a phone that is not there.
 *
 * That asymmetry is what makes the stale-count symptom self-correct in about two minutes
 * instead of ten. It does not make the count trustworthy — IP keying rules that out, which is
 * why the TV no longer prints it — it just stops the count outliving the phone that caused it.
 */
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

/**
 * @param {object}    options
 * @param {function}  options.isTracked  (ip) => boolean. Confines tracking to the hotspot
 *                                       subnet: on a developer's machine every request
 *                                       comes from 127.0.0.1 and none of this means
 *                                       anything, so the funnel must stay silent there
 *                                       rather than report a phantom device.
 * @param {function} [options.onChange]  called with `counts()` whenever they change, which
 *                                       is the only thing worth waking the host for.
 * @param {function} [options.now]       injectable clock, for tests.
 */
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
