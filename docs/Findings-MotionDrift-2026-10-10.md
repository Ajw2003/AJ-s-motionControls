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

## Result with pacing (rc.4, 17 ms buffer), measured live

A second 180 s capture through the installed rc.4, same routine. "Motion spacing error" compares
each motion packet's arrival gap with the phone's own gap.

| | Spacing error median / 95% / 99% / worst | Relay CPU |
|---|---|---|
| capture 1, unpaced | 0.38 / 2.81 / 7.46 / 69.8 ms | — |
| capture 2, paced | **0.02 / 0.15 / 0.46** / 22.2 ms | 97.7% of one core (8.1% of 12) |

Both captures also held 720 extra packets (exactly 4 a second): the phone repeats its controller
state every 250 ms (`PAD_REPEAT_MS`, `controller/js/pad.js:20`), and the relay forwarded each as
a packet carrying the last motion again. In Sudachi's arrival-timed maths each one splits an
interval. On capture 2, Sudachi-style error was 4.3 / −0.9 / 7.5° with the repeats and
−0.3 / 0.9 / 2.6° without. Dolphin was unaffected (−3.8 / 2.4 / 13.0° either way; that capture
had heavier roll, peaking at 1,770°/s, and the remainder is its own 200 Hz ticks). Since then the
relay forwards controller state only when a button or stick changed (`relay/dsu.js`, `onPad`).

## Direction check on rc.5 (open)

aj reported the Sudachi picture turning opposite to the phone on all three axes, with the phone
on the table and Player 1 set to **Right Joycon**. A capture of the same three moves showed the
relay sending each in the direction Dolphin's DSU client names it: turn right gave yaw +68°
("Gyro Yaw Right"), lifting the top edge gave pitch +79° ("Gyro Pitch Up"), and lifting the left
edge gave roll +95° ("Gyro Roll Right"). On the table at rest, the gyro read about 0.005°/s,
below Sudachi's dead zone, and the replay drifted 0.00° in 55 s. Open question: whether a single
Joy-Con's own frame explains Sudachi's picture, or whether Sudachi's signs differ from Eden's. Next
test: the same moves with Player 1 as Pro Controller.

**Resolved (#30).** As Pro Controller it was still wrong on several axes. The labels-free check
(`tools/check-gyro-axes.js`: turn the starting gravity by the gyro, compare with measured gravity,
score all 48 axis orderings) found the phone page's rotation mapping crossed: 76.7° disagreement
as sent, 1.0° with `alpha` = x, `beta` = y, `gamma` = z. That is what browsers implement, confirmed
in source, not recalled: the current W3C spec §6.3.2, Chromium `device_motion_event_pump.cc`
(gyro x, y, z into `alpha`, `beta`, `gamma`), and WebKit `DeviceMotionClientIOS.mm` / `DeviceMotionData.h`
(CoreMotion x, y, z, likewise). The page followed the older spec text (`beta` about x). The relay's
DSU mapping was right: with the page corrected, the capture's tilts match gravity and Dolphin's
axis names. The earlier direction test only looked right because two errors cancelled.

**rc.6 on aj's phone:** `tools/check-gyro-axes.js` gives OK, 1.2° (3 tilts). **Sudachi's controller
preview box cannot show tilt direction:** `Draw3dCube` (`configure_input_player_widget.cpp`) draws
a see-through 1.4 × 2 box, both faces alike, from above, so lifting the top edge and lifting the
bottom edge draw the same picture, and so do left and right. A flat, level controller draws as a
tall rectangle. Check direction in a game, or against gravity with the tool, not with that box.

## The obstacle to pacing

Pacing sends each packet to the emulator at the phone's own sample spacing, so arrival time
equals sample time. On this PC, `setTimeout(16)` fires every **31 ms** (median; min 15.5 ms), at
Windows' default 15.6 ms timer resolution, so Node timers cannot pace a 60 Hz stream. Precise
pacing needs a busy-wait (one CPU core in use while a phone is connected). The alternative is
rescaling each gyro value by phone time over arrival time, which needs no timers or delay, but
changes the rate values every DSU client sees, Dolphin included.
