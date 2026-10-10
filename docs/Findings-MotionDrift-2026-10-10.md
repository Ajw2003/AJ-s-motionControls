# Findings: why the phone's heading drifts in Sudachi

**Date:** 2026-10-10. **Prompted by:** aj, after the axis fix: the phone reads level when put
back flat, but its heading (the direction it points while flat) ends up turned after play.
**Method:** read the Yuzu-family motion code (Eden, a Yuzu fork like Sudachi), port it to
`tools/sim-sudachi-motion.js`, capture real DSU packets with `tools/dsu-capture.js`, replay.

## How Sudachi turns DSU packets into orientation

From Eden's source (2026-10-10):

- `src/input_common/drivers/udp_client.cpp`: gyro is divided by **312**, not 360, so every turn
  reads about **15% large** (a 90° turn shows as 103°). The integration time is the gap between
  packet **arrivals** at the PC (`now - last_update`). The packet's own timestamp is ignored.
- `src/hid_core/frontend/motion_input.cpp`: a Mahony filter. Gravity corrects tilt; nothing
  corrects heading. Rotation slower than **0.007 turns/s (about 2.2°/s of DSU input)** is zeroed.
  The gyro is clamped at 5 turns/s (**1,560°/s of DSU input**). It re-levels itself only after
  900 still, flat samples, and only if its tilt estimate is off by more than 0.1.

## Measurement: one 180 s session on 5 GHz, aj's PC

`capture1` (scratchpad, not kept): 11,519 packets; arrival gaps median 16.6 ms, 95% 18.6 ms,
99% 20.8 ms, max 86.4 ms. Still for about 10 s, about 100 s of vigorous play (peaks
1,945°/s), then still.

Replayed with Sudachi's auto-reset disabled, so only the timing differs:

| Integration time | Heading drift at end |
|---|---|
| Packet arrival (what Sudachi does) | +26.5° |
| Phone's own sample timestamps | −11.7° |
| Phone timestamps, no dead zone | −11.2° |

**Arrival timing alone moved the heading 38° in 100 s of play**, even on a clean 5 GHz link.
The dead zone barely mattered here. The −11.7° that remains with perfect timing is the phone's
own gyro error, the 4 samples above the clamp, and how exactly the phone was put back (the true
end heading was not measured).

## Dolphin has the same weakness, mirrored

From Dolphin's source (2026-10-10):

- `InputCommon/ControllerInterface/DualShockUDPClient/DualShockUDPClient.cpp` keeps only the
  latest packet and ignores its timestamp. Gyro scale is `360/τ` (correct degrees to radians), so
  there's no 15% over-rotation.
- `Core/HW/WiimoteEmu/Dynamics.cpp` (`EmulateIMUCursor`) runs at a fixed 200 Hz and applies the
  latest reading for 5 ms per tick. So a reading counts for as long as it is **held**: the gap
  **after** it. Sudachi counts the gap **before** it.
- Dolphin has a mappable **Recenter** button and clamps the pointer's yaw to a configured range,
  so its heading drift cannot run away.

Total rotation integrated from the same capture (degrees, pitch / yaw / roll):

| | Error against phone timing |
|---|---|
| Sudachi-style, as received | +6.8 / +5.9 / +16.6 |
| Dolphin-style, as received | −23.9 / +2.4 / −3.0 |
| Sudachi-style, paced (17 ms buffer) | 0.0 / 0.0 / 0.0 |
| Dolphin-style, paced (17 ms buffer) | +5.4 / −0.8 / −1.1 (its 5 ms ticks) |

With a 17 ms buffer, 118 of 11,519 readings (1%) would still arrive after their slot and go out
late. **Rescaling the gyro cannot serve both clients** (one needs the gap before, the other the
gap after); pacing serves both.

## The obstacle to pacing

Pacing sends each packet to the emulator at the phone's own sample spacing, so arrival time
equals sample time. On this PC, `setTimeout(16)` fires every **31 ms** (median; min 15.5 ms), at
Windows' default 15.6 ms timer resolution, so Node timers cannot pace a 60 Hz stream. Precise
pacing needs a busy-wait (one CPU core in use while a phone is connected). The alternative is
rescaling each gyro value by phone time over arrival time, which needs no timers or delay, but
changes the rate values every DSU client sees, Dolphin included.
