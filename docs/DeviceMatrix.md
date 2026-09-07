# Device matrix

Which phones this has actually been tested on, and what was exercised. This file is the
authoritative record of cross-device verification — `README.md` and the source comments in
`controller/js/` point here rather than restating it, so it only has to be right in one place.

**Why it exists.** The source used to carry `UNVERIFIED (iOS)` markers written before any iPhone
had run the code. They were never updated, and for a long time the repository argued against its
own strongest practical claim while the README simultaneously claimed a verified iOS session.
Cross-device verification is exactly what separates a sensor demo from something another
developer can ship, so it needs a maintained record rather than a memory.

> **⚠ INCOMPLETE — fill this in.** The rows below are placeholders. iOS is confirmed working
> across multiple iPhone generations and Android across multiple Samsung and Pixel generations,
> but the specific models and OS versions live only in the developer's head. Replace every
> `TODO` with a real value; delete rows that never happened. Do not leave invented entries here
> — this file is cited as evidence in grant applications, and a wrong row is worse than a
> missing one.

## What "verified" means here

A device is **Verified** when someone has, on that physical device, completed all of:

1. Joined the host's hotspot and loaded the controller page over HTTPS by name
2. Granted motion permission where the platform prompts for it
3. Aimed — the on-screen preview tracks the phone continuously, with no staircase or drift
4. Thrown — a hard throw and a gentle throw produce visibly different results
5. Played a full match through turn rotation and scoring

Anything less is **Partial**, and the row says which step failed or was not reached.

## iOS

| Device | iOS version | Browser | Joined | Motion permission | Aim | Throw | Full match | Date | Notes |
|---|---|---|---|---|---|---|---|---|---|
| TODO iPhone model | TODO | Safari | TODO | TODO | TODO | TODO | TODO | TODO | |
| TODO iPhone model | TODO | Safari | TODO | TODO | TODO | TODO | TODO | TODO | |
| TODO iPhone model | TODO | Safari | TODO | TODO | TODO | TODO | TODO | TODO | |

**iOS-specific behaviour that these devices confirm** (each was previously marked unverified in
source and is now known from real runs):

- `DeviceMotionEvent.requestPermission()` exists and must be called synchronously inside the user
  gesture — deferring it behind any other `await` loses gesture context and iOS rejects the
  request (`controller/js/app.js`, join handler).
- `event.acceleration` is absent, so `readLinearAcceleration()` returns null throughout and the
  pipeline falls to exact quaternion subtraction. `event.accelerationIncludingGravity` is
  present, which is why `readAcceleration()` is the only permitted read site
  (`controller/js/sensors.js`).
- Absolute heading disagrees with Android, which is why orientation is sent as a delta from a
  calibration reference captured at arm time and `webkitCompassHeading` is ignored.

Record the `gravitySource` value reported per sample when testing a new device — it says which
of the three gravity-removal tiers that hardware actually exercises, and it is the fastest way
to tell whether a device behaves like the ones already in this table.

## Android

| Device | Android version | Browser | Joined | Motion permission | Aim | Throw | Full match | Date | Notes |
|---|---|---|---|---|---|---|---|---|---|
| TODO Samsung model | TODO | Chrome | TODO | n/a | TODO | TODO | TODO | TODO | |
| TODO Samsung model | TODO | Chrome | TODO | n/a | TODO | TODO | TODO | TODO | |
| TODO Pixel model | TODO | Chrome | TODO | n/a | TODO | TODO | TODO | TODO | |
| TODO Pixel model | TODO | Chrome | TODO | n/a | TODO | TODO | TODO | TODO | |

**Android-specific behaviour:** no permission API exists at all, so `requestMotionPermission()`
feature-detects rather than using a try/catch or a user-agent sniff — calling `requestPermission`
unconditionally throws here. Both `acceleration` and `accelerationIncludingGravity` are exposed,
which is why an Android-only test can never catch a regression that reintroduces a direct
`event.acceleration` read; that read is confined to one function as a deliberate guard.

## Multi-device sessions

Single-device testing does not exercise turn rotation, late join, rejoin, or roster handling.

| Date | Players | Device mix | What was exercised | Result |
|---|---|---|---|---|
| TODO | 5 | iOS + Android | scoring, turn rotation, late-join, lobby-rejoin | passed |
| TODO | TODO | TODO | TODO | TODO |

The five-player iOS + Android session is referenced in `README.md`; fill in its date and the
specific devices so the claim is checkable.

## Tablets

`rock-throws.csv` contains throws logged from a tablet as well as phones. Record it here.

| Device | OS | Browser | Result | Date | Notes |
|---|---|---|---|---|---|
| TODO tablet | TODO | TODO | TODO | TODO | logged throws present in `rock-throws.csv` |

## Known non-targets

- **Desktop browsers** have no motion sensors. A `SimulatedThrow` path lets a desktop seat
  join and take turns so it cannot deadlock a match; it is gated to devices that have never
  emitted a motion event and reports `sampleCount: 0` so the host can distinguish it. This is a
  testing affordance, not a supported way to play.
- **Insecure origins.** Motion sensors are unavailable without a secure context, by design in
  every browser. `isSecureContextOk()` checks this and the controller reports it as a clear
  message rather than as silently dead sensors.

## Adding a device

1. Run through the five steps under "What `verified` means here" in order.
2. Note the `gravitySource` reported per sample.
3. Add a row. If any step failed, mark the row Partial and say which step — a recorded failure
   is more useful than an absent row, and this file is only worth citing if it records both.
