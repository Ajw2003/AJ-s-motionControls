# DSU server: phone motion to emulators

**Issue:** [#3](https://github.com/Ajw2003/AJ-s-motionControls/issues/3) · **Milestone:** 2 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
A DSU (CemuHook) server in the Node PC app on UDP 26760, so Dolphin, Cemu, Ryujinx and the Citra
forks read the phone's motion with no driver.

## Approach
- Answer the three client requests: protocol version, controller (port) info, and pad-data
  subscription; then push pad data for each subscribed slot as samples arrive.
- Packet layout as commonly documented (confirm against the published protocol description while
  building, before relying on it): magic `DSUS` from server / `DSUC` from client, protocol version
  1001, payload length, CRC32 over the whole packet with the CRC field zeroed, sender ID, message
  type. Pad data carries accelerometer in g, gyro in deg/s and a motion timestamp in microseconds.
- Up to four phones map to the four DSU slots.
- Browser axes differ from DSU's; the conversion is pure code with unit tests.
- A scripted DSU client in Node checks packets without an emulator.

## Done when
The scripted client receives valid, CRC-correct pad data driven by a fake phone, and Dolphin's DSU
client on the Windows PC shows a real phone's motion moving (screenshot or video).
