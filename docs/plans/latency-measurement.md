# Measure end-to-end latency

**Issue:** [#8](https://github.com/Ajw2003/AJ-s-motionControls/issues/8) · **Milestone:** 6 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
Real numbers for how long a touch or a motion change takes to reach the PC's output, replacing the
guessed 25-65 ms.

## Approach
- Network part: round-trip pings with clock-offset estimation between phone and PC, reported as
  median and 95th percentile.
- Touch part: browser event timestamps only partly cover touch-to-event, so a slow-motion phone
  video of a tap against the PC screen gives the true total. Write the method down so anyone can
  repeat it.
- Results per phone in a table modelled on `docs/DeviceMatrix.md`.

## Done when
Numbers for at least one iPhone and one Android phone on the Windows PC's hotspot are in the docs,
with the method beside them.
