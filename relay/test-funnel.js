'use strict';

/**
 * Tests for the per-device join funnel. Run with: npm test
 *
 * The funnel decides two user-visible things — what the TV says while people are joining,
 * and whether a phone's OS pops its captive-portal sheet — off signals that arrive out of
 * order, from three different layers, keyed only by IP. Every case below is one of the ways
 * that combination can lie:
 *
 *   - a stage regressing would make the TV nag someone mid-throw,
 *   - an untracked address being counted would invent a phone on a developer's machine,
 *   - a device never expiring would leave the TV nagging about a guest who went home.
 */

const { test, describe } = require('node:test');
const assert = require('node:assert/strict');
const { createFunnel, normalizeAddress, Stage, IDLE_TIMEOUT_MS } = require('./funnel');

/** Tracks the hotspot subnet only, exactly as server.js does via makeSubnetTest. */
const isTracked = (ip) => ip.startsWith('192.168.137.');

/** A funnel with a clock under the test's control, so expiry needs no waiting. */
function makeFunnel() {
  let clock = 1_000_000;
  const changes = [];
  const funnel = createFunnel({
    isTracked,
    onChange: (counts) => changes.push(counts),
    now: () => clock,
  });
  return { funnel, changes, advance: (ms) => (clock += ms) };
}

describe('address normalization', () => {
  test('an IPv4-mapped IPv6 address is the same device as its bare IPv4 form', () => {
    assert.equal(normalizeAddress('::ffff:192.168.137.5'), '192.168.137.5');
    assert.equal(normalizeAddress('192.168.137.5'), '192.168.137.5');
  });

  test('junk becomes null rather than a phantom device', () => {
    assert.equal(normalizeAddress(''), null);
    assert.equal(normalizeAddress('   '), null);
    assert.equal(normalizeAddress(undefined), null);
    assert.equal(normalizeAddress(null), null);
  });
});

describe('funnel counting', () => {
  test('a device on the Wi-Fi counts as associated but not as page-open or playing', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    assert.deepEqual(funnel.counts(), { associated: 1, pageOpen: 0, playing: 0 });
  });

  test('counts are cumulative — a playing device is still associated', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('192.168.137.5', Stage.PAGE_OPEN);
    funnel.record('192.168.137.5', Stage.PLAYING);
    assert.deepEqual(funnel.counts(), { associated: 1, pageOpen: 1, playing: 1 });
  });

  test('the gap the TV nags about: two on the Wi-Fi, one of them in the game', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('192.168.137.6', Stage.ASSOCIATED);
    funnel.record('192.168.137.6', Stage.PAGE_OPEN);

    const counts = funnel.counts();
    assert.equal(counts.associated - counts.pageOpen, 1);
  });

  test('the same device seen twice is one device', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('::ffff:192.168.137.5', Stage.ASSOCIATED);
    assert.equal(funnel.counts().associated, 1);
  });

  test('an address outside the hotspot subnet is ignored entirely', () => {
    const { funnel, changes } = makeFunnel();
    funnel.record('127.0.0.1', Stage.PAGE_OPEN);
    funnel.record('10.0.0.4', Stage.PLAYING);
    assert.deepEqual(funnel.counts(), { associated: 0, pageOpen: 0, playing: 0 });
    assert.equal(changes.length, 0, 'an untracked address must not wake the host');
  });
});

describe('stages never regress', () => {
  test('a late ASSOCIATED does not pull a playing device back down', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.PLAYING);
    funnel.record('192.168.137.5', Stage.ASSOCIATED);

    assert.equal(funnel.stageOf('192.168.137.5'), Stage.PLAYING);
    assert.equal(funnel.counts().playing, 1);
  });

  test('a device that reaches PLAYING without a DNS query still counts as associated', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.9', Stage.PLAYING);
    assert.deepEqual(funnel.counts(), { associated: 1, pageOpen: 1, playing: 1 });
  });
});

describe('captive-portal branch', () => {
  test('an unknown device is below PAGE_OPEN, so it gets nudged', () => {
    const { funnel } = makeFunnel();
    assert.ok(funnel.stageOf('192.168.137.5') < Stage.PAGE_OPEN);
  });

  test('a device that opened the page is not nudged again', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.5', Stage.PAGE_OPEN);
    assert.ok(funnel.stageOf('192.168.137.5') >= Stage.PAGE_OPEN);
  });

  test('an untracked address reads as UNKNOWN rather than throwing', () => {
    const { funnel } = makeFunnel();
    assert.equal(funnel.stageOf(undefined), Stage.UNKNOWN);
  });
});

describe('expiry', () => {
  test('a device that goes quiet stops being counted', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(IDLE_TIMEOUT_MS + 1);
    assert.equal(funnel.counts().associated, 0);
  });

  test('activity keeps a device alive past the timeout', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(IDLE_TIMEOUT_MS - 1);
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(2);
    assert.equal(funnel.counts().associated, 1);
  });
});

describe('change notifications', () => {
  test('the host is woken only when the numbers actually move', () => {
    const { funnel, changes } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('192.168.137.5', Stage.ASSOCIATED);

    assert.equal(changes.length, 1, 'a re-probe from a known device is not news');
    assert.deepEqual(changes[0], { associated: 1, pageOpen: 0, playing: 0 });
  });

  test('advancing a stage is news', () => {
    const { funnel, changes } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    funnel.record('192.168.137.5', Stage.PAGE_OPEN);

    assert.equal(changes.length, 2);
    assert.deepEqual(changes[1], { associated: 1, pageOpen: 1, playing: 0 });
  });
});
