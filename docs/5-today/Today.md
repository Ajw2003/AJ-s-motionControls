# Today — 2026-10-09

**Tier 5.** Rewritten at the start of each working session.

## What today is

**A documentation day.** The repo was forked from Rock Skipping yesterday and every tier still
described the game. Today the tiers were rebuilt around the seven GitHub issues under
[#1](https://github.com/Ajw2003/AJ-s-motionControls/issues/1). This maps to no milestone: it is
the groundwork the milestones are written against.

## Done

- New tiers 1, 2, 3 and 5 and `docs/README.md` for AJ's Motion Controls; the game's versions moved
  to [`archive/rock-skipping/`](../archive/rock-skipping/), nothing deleted.
- The systems index split into systems this product reuses, game-only systems, and planned ones.
- One plan per issue in [`plans/`](../plans/).
- A Decisions entry recording the restructure.
- **#2 raw motion stream built** and checked headless (tests twice, two emulated phones at
  62-63 Hz, screenshots in [`media/verify/`](../media/verify/)). Real-phone check outstanding.

## Deliberately not done

- No systems docs for the DSU server, on-screen gamepad or virtual controller: none of them exists
  yet, and a systems doc for unbuilt code would be invented. Each gets one when it is built.
- No game code or game docs removed. That is an open decision.

## Surfaced, not today's job

- Packaging for a stranger's PC has no issue and no milestone.

## What to do next, in order

1. **#2 Raw motion stream** — everything else consumes it.
2. **#3 DSU server** — the first point the product does something an emulator can use.
3. **#4 On-screen gamepad**, then **#5 buttons over DSU**.
4. **#6 Virtual Xbox controller** — starts by checking ViGEmBus still installs on the Windows PC.
5. **#8 Latency measurement**, then **#7 WebRTC**, measured against it.
