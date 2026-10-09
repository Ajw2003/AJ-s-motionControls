# On-screen gamepad

**Source:** `controller/js/gamepad-state.js`, `controller/js/layouts.js`,
`controller/js/gamepad-view.js`, wired in `controller/js/pad.js` and `controller/pad.html`; the PC
side is `parsePadMessage` in `relay/pad-hub.js`. Checked by `controller/test-gamepad-state.js`,
`relay/test-pad-hub.js` and `tools/verify-gamepad.js`. Built for issue #4.

## What it owns

Turning thumbs on a touchscreen into gamepad state: which buttons are down and where the two
sticks are. It also owns the four layouts and the `PAD` message that carries the state to the PC.
Motion is [`ControllerStream.md`](ControllerStream.md). Handing buttons to emulators is #5 (DSU)
and #6 (virtual Xbox controller).

## How it works

```
layouts.js (data) ──▶ gamepad-view.js (draws, owns pointers) ──▶ GamepadState ──▶ PAD over /pad
                                                                       └▶ pad hub 'pad' event ──▶ monitor
```

- **Buttons by position, not label.** State uses Xbox positions (`south`, `east`, `west`, `north`,
  `lb`, `rb`, `lt`, `rt`, `back`, `start`, `home`, `ls`, `rs`, the D-pad), one bit each
  ([`gamepad-state.js:13`](../../controller/js/gamepad-state.js)). A layout only labels them: the
  Switch layout's "A" is `east`, its "B" is `south`. Emulators and virtual pads map by position,
  so this is what they need.
- **Layouts are data** ([`layouts.js:30`](../../controller/js/layouts.js)): Xbox, Switch,
  GameCube, and a sideways Wii Remote. Each control has a centre in percent of screen width and
  height and a size in percent of screen height, so controls keep their shape on any phone.
  `controlSize` is the one sizing rule. The page and the overlap test both use it.
- **Pointers.** Each control captures the pointers that start on it
  ([`gamepad-view.js:67`](../../controller/js/gamepad-view.js)). Two thumbs never fight, and a
  thumb sliding off a button keeps it held until it lifts, like a real button. Sticks read the
  offset from their centre ([`stickFromOffset`](../../controller/js/gamepad-state.js), 12%
  deadzone, rescaled past it, clamped to the unit circle, up is +y). The D-pad is eight-way with a
  centre dead spot ([`dpadFromOffset`](../../controller/js/gamepad-state.js)).
- **Sending.** Any change sends a `PAD` message right away: `{ type, seq, buttons, sticks:[lx, ly,
  rx, ry] }` ([`gamepad-state.js:119`](../../controller/js/gamepad-state.js)). The whole state is
  repeated every 250 ms in case one was lost ([`pad.js:20`](../../controller/js/pad.js)).
- **PC side.** The pad hub validates `PAD` messages (bits beyond the last button rejected, sticks
  clamped to −1..1, [`pad-hub.js:43`](../../relay/pad-hub.js)), keeps the newest by `seq`, and
  emits `pad` events. `/api/pads` carries it, and the monitor shows the buttons held and both
  sticks.
- **Screen.** The setup screen picks a layout (remembered when storage allows) and scrolls. The
  gamepad screen is full screen and landscape, with the status chip at the top doubling as the
  way back to the picker.

## Invariants

- **The button bit order is wire format: append, never reorder.** The test "wire order never
  moves" guards the first four and the last.
- **Every `PAD` message is the whole state,** never a change. A lost message is corrected by the
  next one, and #7's unreliable channel needs nothing extra.
- **No two controls overlap and none leaves the screen** at 16:9, 2:1 and 2.17:1, with the status
  chip reserved top centre. `controller/test-gamepad-state.js` checks it for every layout.
- **Only the gamepad screen locks touch** (`touch-action: none` on `#pad`). The setup screen must
  scroll.

## Traps

- **`touch-action: none` on `body` silently blocks scrolling the setup screen.** In landscape
  that hid the Start button below the fold. Headless Chromium's synthetic touch swipe scrolls
  nothing, not even a plain page, so `tools/verify-gamepad.js` checks the computed `touch-action`
  of Start's ancestors instead of swiping.
- **Layouts that look right on one phone overlap on another.** Percent positions spread with the
  screen's width while sizes follow its height. The overlap test, not a screenshot, is the check.
- **Vibration is Android only.** iPhone Safari has no `navigator.vibrate`; the call is guarded.
