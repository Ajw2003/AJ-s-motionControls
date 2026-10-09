# Raw motion stream from the phone

**Issue:** [#2](https://github.com/Ajw2003/AJ-s-motionControls/issues/2) · **Milestone:** 1 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
The phone page sends raw gyro (deg/s) and accelerometer (g) readings at the rate the browser
delivers them (about 60 Hz on iPhone, often higher on Android), instead of Rock Skipping's
processed 20 Hz attitude (`controller/js/app.js:388`) and single THROW event.

## Approach
- Reuse `requestMotionPermission` (`controller/js/sensors.js:355`) and the platform readers
  `readAcceleration` (`sensors.js:291`) and `readRotationRate` (`sensors.js:307`). Only the output
  changes.
- Each sample carries a phone-side timestamp in microseconds (DSU needs it; so does #8).
- A new endpoint on the PC side, separate from the game relay, whose rate limit (`relay/server.js:41`)
  is sized for 20 Hz game messages.
- The sample format is pure code with Node unit tests, following `controller/test-sensors.js`.

## Done when
Unit tests pass for the sample format, and a real iPhone and a real Android phone each show a
steady stream at their native rate in a PC debug readout (screenshot of each).
