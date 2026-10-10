/**
 * The on-screen gamepad's state and its wire message (issue #4). Pure: no DOM, so it is
 * tested in Node.
 *
 * Buttons are named by **position on an Xbox-style pad**, not by label, because position is
 * what emulators and virtual controllers map: a Switch layout's "A" sits where an Xbox pad's
 * B is, so it is `east`. Each layout (layouts.js) labels these positions its own way.
 */

export const PAD_MESSAGE_TYPE = 'PAD';

/** One bit per button. The order is part of the wire format: append, never reorder. */
export const Button = Object.freeze({
  south: 1 << 0,
  east: 1 << 1,
  west: 1 << 2,
  north: 1 << 3,
  lb: 1 << 4,
  rb: 1 << 5,
  back: 1 << 6,
  start: 1 << 7,
  home: 1 << 8,
  ls: 1 << 9,
  rs: 1 << 10,
  up: 1 << 11,
  down: 1 << 12,
  left: 1 << 13,
  right: 1 << 14,
  lt: 1 << 15,
  rt: 1 << 16,
  // Wii layout's Centre: Dolphin maps Recenter to it (#32). Sent as DSU's touchpad button.
  centre: 1 << 17,
});

export const BUTTON_NAMES = Object.freeze(Object.keys(Button));

// Inside this fraction of the stick's travel reads as centred: a thumb resting on a stick
// is never exactly at its middle.
export const STICK_DEADZONE = 0.12;

function round3(value) {
  return Math.round(value * 1000) / 1000 || 0; // `|| 0` turns -0 into 0
}

/**
 * A touch offset from a stick's centre, in pixels, to a stick value in -1..1 per axis.
 * Up is positive y, as gamepads report it (screen y grows downward). Outside the deadzone
 * the travel is rescaled so the edge of the deadzone is 0, not 0.12; past the radius it
 * clamps to the circle.
 */
export function stickFromOffset(dx, dy, radius, deadzone = STICK_DEADZONE) {
  const distance = Math.hypot(dx, dy);
  if (radius <= 0 || distance <= deadzone * radius) return { x: 0, y: 0 };
  const travel = Math.min(1, (distance / radius - deadzone) / (1 - deadzone));
  return { x: round3((dx / distance) * travel), y: round3((-dy / distance) * travel) };
}

/**
 * Which D-pad directions a touch at (dx, dy) from the pad's centre presses. Eight-way:
 * diagonals press two. Inside the centre dead spot, nothing.
 */
export function dpadFromOffset(dx, dy, radius) {
  const pressed = [];
  if (Math.hypot(dx, dy) < radius * 0.2) return pressed;
  const angle = Math.atan2(-dy, dx); // radians, counter-clockwise from right
  const sector = Math.round(angle / (Math.PI / 4)); // -4..4, each 45°
  if (sector === 0 || sector === 1 || sector === -1) pressed.push('right');
  if (sector === 1 || sector === 2 || sector === 3) pressed.push('up');
  if (sector === 3 || sector === 4 || sector === -4 || sector === -3) pressed.push('left');
  if (sector === -1 || sector === -2 || sector === -3) pressed.push('down');
  return pressed;
}

/** The gamepad as the PC sees it. Mutated by the page, encoded on each send. */
export class GamepadState {
  constructor() {
    this.buttons = 0;
    this.leftStick = { x: 0, y: 0 };
    this.rightStick = { x: 0, y: 0 };
  }

  press(name) {
    const bit = Button[name];
    if (bit === undefined) throw new Error(`unknown button "${name}"`);
    const before = this.buttons;
    this.buttons |= bit;
    return this.buttons !== before;
  }

  release(name) {
    const bit = Button[name];
    if (bit === undefined) throw new Error(`unknown button "${name}"`);
    const before = this.buttons;
    this.buttons &= ~bit;
    return this.buttons !== before;
  }

  isPressed(name) {
    return (this.buttons & Button[name]) !== 0;
  }

  /** Returns true when the value changed, so the page knows to send. */
  setStick(which, value) {
    const stick = which === 'left' ? this.leftStick : this.rightStick;
    if (stick.x === value.x && stick.y === value.y) return false;
    stick.x = value.x;
    stick.y = value.y;
    return true;
  }

  /** Every pressed button's name, for readouts. */
  pressedNames() {
    return BUTTON_NAMES.filter((name) => this.isPressed(name));
  }
}

/**
 * The PAD wire message: the whole state every time, never a change, so one lost message is
 * corrected by the next (and #7's unreliable channel needs nothing extra).
 */
export function makePadMessage(state, seq) {
  return {
    type: PAD_MESSAGE_TYPE,
    seq,
    buttons: state.buttons,
    sticks: [state.leftStick.x, state.leftStick.y, state.rightStick.x, state.rightStick.y],
  };
}
