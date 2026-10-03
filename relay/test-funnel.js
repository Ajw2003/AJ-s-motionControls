'use strict';

// Funnel tests; each case is a way that out-of-order, IP-keyed signals can lie.
// doc-ref 044b docs/4-systems/Verification.md

const { test, describe } = require('node:test');
const assert = require('node:assert/strict');
const {
  createFunnel,
  normalizeAddress,
  Stage,
  IDLE_TIMEOUT_MS,
  ASSOCIATED_IDLE_TIMEOUT_MS,
} = require('./funnel');

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

    assert.equal(funnel.counts().playing, 1);
  });

  test('a device that reaches PLAYING without a DNS query still counts as associated', () => {
    const { funnel } = makeFunnel();
    funnel.record('192.168.137.9', Stage.PLAYING);
    assert.deepEqual(funnel.counts(), { associated: 1, pageOpen: 1, playing: 1 });
  });
});

describe('expiry', () => {
  test('a device that only ever associated is dropped after the short timeout', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(ASSOCIATED_IDLE_TIMEOUT_MS + 1);
    assert.equal(funnel.counts().associated, 0);
  });

  test('activity keeps a device alive past its timeout', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(ASSOCIATED_IDLE_TIMEOUT_MS - 1);
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(2);
    assert.equal(funnel.counts().associated, 1);
  });

  // The asymmetry Defect 1 turned on: a phone mid-match sends nothing for minutes at a time,
  // so holding it for ten is right; a phone seen only in a DNS log re-probes every 30-60s, so
  // silence for two means it left, and holding it makes the TV nag for a phone that is gone.
  test('a device that opened the controller survives the short timeout', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.PAGE_OPEN);
    advance(ASSOCIATED_IDLE_TIMEOUT_MS + 1);
    assert.equal(funnel.counts().pageOpen, 1, 'a player mid-match must not be swept');
  });

  test('a playing device is still dropped once it passes the long timeout', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.PLAYING);
    advance(IDLE_TIMEOUT_MS + 1);
    assert.equal(funnel.counts().playing, 0);
  });

  test('reaching PAGE_OPEN extends a device that was about to expire', () => {
    const { funnel, advance } = makeFunnel();
    funnel.record('192.168.137.5', Stage.ASSOCIATED);
    advance(ASSOCIATED_IDLE_TIMEOUT_MS - 1);
    funnel.record('192.168.137.5', Stage.PAGE_OPEN);
    advance(ASSOCIATED_IDLE_TIMEOUT_MS + 1);
    assert.equal(funnel.counts().pageOpen, 1, 'the longer timeout applies from the new stage');
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
