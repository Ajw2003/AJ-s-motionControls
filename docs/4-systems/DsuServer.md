# DSU server

**Source:** `relay/dsu.js`, started from `relay/server.js`; checked by `relay/test-dsu.js`,
`tools/dsu-client.js` and `tools/verify-dsu.js`. Built for issue #3; buttons are #5.

## What it owns

Turning the controller stream's samples into DSU (CemuHook) packets and serving them over UDP to
emulators on this PC: Dolphin, Cemu, Ryujinx, the Citra forks. It owns the packet format, the
phone-to-DSU axis mapping and the list of emulators reading. Getting samples off the phones is
[`ControllerStream.md`](ControllerStream.md).

## How it works

```
pad hub 'sample' (slot, sample) ──▶ padDataPayload ──▶ every subscribed client (UDP)
emulator request ──▶ parsePacket ──▶ version / port info / subscribe
```

- **Where it listens.** UDP `127.0.0.1:26760` by default
  ([`relay/server.js:698`](../../relay/server.js)). `DSU_PORT=0` turns it off; `DSU_BIND_HOST`
  widens the bind address for an emulator on another machine.
- **Framing.** Every packet is a 16-byte header (magic `DSUS` from the server or `DSUC` from a
  client, protocol 1001, payload length, CRC-32, sender ID) then the message type and body. The CRC
  covers the whole packet with its own field zeroed ([`dsu.js:45`](../../relay/dsu.js)). Anything
  with the wrong magic, version, length or CRC is dropped and counted as malformed.
- **Requests.** *Version* gets protocol 1001. *Port info* gets one reply per requested slot,
  connected or not, with a stable made-up MAC per slot. *Pad data* subscribes the sender, to every
  slot (flags 0, or by MAC) or one slot (flag 1) ([`dsu.js:272`](../../relay/dsu.js)).
- **Pad data.** Each phone sample becomes one 100-byte packet (84-byte payload,
  [`dsu.js:132`](../../relay/dsu.js)) per subscribed client, with that client's own increasing
  packet number. Sticks sit at centre (128) and buttons at zero until #5 fills them.
- **Timeouts.** Emulators renew their subscription about once a second. A client quiet for 5 s is
  dropped ([`dsu.js:28`](../../relay/dsu.js)).
- **Status.** `/api/pads` carries `dsu: { listening, host, port, clients, … }`, and
  `monitor.html` shows it as the "Emulator link (DSU)" line.

### Axis mapping

<!-- ref:46ec -->

The phone page sends device axes: x to the right of the screen, y up the screen, z out of the
screen. DSU follows the DualShock 4, held like a gamepad. Taking "phone flat on its back, top edge
away from the player" as "gamepad held normally" gives: DSU x = phone x, DSU y = phone z (up out
of the face), DSU z = −phone y (towards the player). The gyro's pitch, yaw and roll are rotations
about those same three axes, so `gyro = [x, z, −y]` too
([`dsu.js:125`](../../relay/dsu.js)).

**Unverified.** These signs are reasoned, not checked in an emulator. Dolphin's DSU input view on
the Windows PC is the check ([`plans/dsu-server.md`](../plans/dsu-server.md)). If a sign is wrong,
it is fixed in `phoneToDsuAxes` and nowhere else.

## Invariants

- **Every axis decision lives in `phoneToDsuAxes`.** The phone sends device axes; nothing else
  flips or swaps them.
- **Loopback by default.** The controllers are readable by anything that can reach the port, so it
  stays on 127.0.0.1 unless someone sets `DSU_BIND_HOST` on purpose.
- **A DSU failure never takes the relay down.** A port already in use logs `[dsu] disabled: …`,
  shows "not running" on the monitor and leaves phones connected
  ([`dsu.js:312`](../../relay/dsu.js)).
- **Packet numbers are per client.** Emulators use them to spot drops and reordering, so each
  client's count starts at 0 and only goes up.

## Traps

- **Only one program can own UDP 26760.** DS4Windows, BetterJoy and other DSU servers use the same
  default port. If one is running, the relay's DSU server is disabled, and the monitor says so.
- **Headless checks prove the protocol, not an emulator's reading of it.**
  `tools/verify-dsu.js` speaks DSU as documented. Whether Dolphin agrees, including the axis signs,
  is only known once Dolphin has been pointed at it.
