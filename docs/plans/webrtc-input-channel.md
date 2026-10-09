# Live input over a WebRTC data channel

**Issue:** [#7](https://github.com/Ajw2003/AJ-s-motionControls/issues/7) · **Milestone:** 7 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
Move motion, sticks and buttons from the WebSocket to a WebRTC data channel set to unordered with
no retransmits, so one lost packet on a busy radio does not hold up everything behind it. The
WebSocket stays for setup and as a fallback.

## Approach
- Same radio problem as `plans/aim-preview-lag-on-shared-radio.md`.
- Send full state every time, not changes, so a lost packet is corrected by the next one.
- Pick a free, pinned Node WebRTC implementation and check it works on Windows.
- Rock Skipping once had a WebRTC transport and removed it
  (`archive/plans/remove-direct-transport.md`, `archive/Findings-DirectTransportFlapping.md`).
  Read why before building this.
- Only after #8, so the gain is measured rather than assumed.

## Done when
The #8 measurement, rerun, shows the data channel against the WebSocket on the same phone and
hotspot.
