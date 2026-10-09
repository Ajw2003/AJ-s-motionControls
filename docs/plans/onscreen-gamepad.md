# On-screen gamepad with switchable layouts

**Issue:** [#4](https://github.com/Ajw2003/AJ-s-motionControls/issues/4) · **Milestone:** 3 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
Touch buttons, D-pad, sticks and triggers on the phone page, with layouts for a sideways Wii
Remote, GameCube, Switch-style dual stick and a general Xbox layout.

## Approach
- Fire on touch-down with pointer events; turn off tap delay, scrolling, zoom and text selection
  over the pad area.
- Multi-touch: several buttons and a stick at once.
- Buttons sent when they change; sticks at a steady rate. Motion keeps streaming alongside.
- Big targets, since there is no physical feel. Vibration on Android only; iPhone Safari does not
  allow it.

## Done when
Every layout works on a real iPhone and a real Android phone, with simultaneous presses reaching
the PC in a debug readout (screenshot of each layout).
