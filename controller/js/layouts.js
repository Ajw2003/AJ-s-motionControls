/**
 * On-screen gamepad layouts (issue #4). Pure data: each control names the gamepad position
 * it drives (gamepad-state.js) and where it sits on a landscape screen.
 *
 * x and y are the control's centre as a percentage of the screen's width and height; size is
 * its diameter (or width, for pills) as a percentage of the screen's height, so controls
 * keep their shape on any aspect ratio. Thumbs need big targets: nothing here is under 13.
 */

const face = (id, label, x, y, size = 17, color) => ({
  kind: 'button',
  id,
  label,
  x,
  y,
  size,
  color,
});
const pill = (id, label, x, y, size = 20) => ({
  kind: 'button',
  id,
  label,
  x,
  y,
  size,
  shape: 'pill',
});
const small = (id, label, x, y) => ({ kind: 'button', id, label, x, y, size: 13, shape: 'pill' });

export const LAYOUTS = Object.freeze({
  xbox: {
    name: 'Xbox',
    hint: 'Two sticks, ABXY, bumpers and triggers',
    controls: [
      pill('lt', 'LT', 9, 8, 14),
      pill('lb', 'LB', 25, 8, 14),
      pill('rb', 'RB', 75, 8, 14),
      pill('rt', 'RT', 91, 8, 14),
      { kind: 'stick', side: 'left', x: 17, y: 45, size: 36 },
      { kind: 'dpad', x: 33, y: 80, size: 32 },
      { kind: 'stick', side: 'right', x: 67, y: 80, size: 32 },
      face('north', 'Y', 85, 32, 17, 'yellow'),
      face('west', 'X', 75, 50, 17, 'blue'),
      face('east', 'B', 95, 50, 17, 'red'),
      face('south', 'A', 85, 68, 17, 'green'),
      small('back', '⧉', 35.5, 30),
      small('home', '⌂', 50, 30),
      small('start', '☰', 64.5, 30),
    ],
  },
  switch: {
    name: 'Switch',
    hint: 'Two sticks, ABXY in Nintendo positions, L/R/ZL/ZR',
    controls: [
      pill('lt', 'ZL', 9, 8, 14),
      pill('lb', 'L', 25, 8, 14),
      pill('rb', 'R', 75, 8, 14),
      pill('rt', 'ZR', 91, 8, 14),
      { kind: 'stick', side: 'left', x: 17, y: 45, size: 36 },
      { kind: 'dpad', x: 33, y: 80, size: 32 },
      { kind: 'stick', side: 'right', x: 67, y: 80, size: 32 },
      // Nintendo labels on Xbox positions: A is east, B south, X north, Y west.
      face('north', 'X', 85, 32),
      face('west', 'Y', 75, 50),
      face('east', 'A', 95, 50),
      face('south', 'B', 85, 68),
      small('back', '−', 35.5, 30),
      small('home', '⌂', 50, 30),
      small('start', '+', 64.5, 30),
    ],
  },
  gamecube: {
    name: 'GameCube',
    hint: 'Big A, kidney B/X/Y, C-stick, L/R, Z',
    controls: [
      pill('lt', 'L', 11, 9, 18),
      pill('rt', 'R', 89, 9, 18),
      pill('rb', 'Z', 72, 24, 15),
      { kind: 'stick', side: 'left', x: 17, y: 48, size: 38 },
      { kind: 'dpad', x: 33, y: 84, size: 26 },
      { kind: 'stick', side: 'right', x: 59, y: 82, size: 26, label: 'C' },
      face('south', 'A', 83.5, 58, 24, 'green'),
      face('west', 'B', 72, 74, 14, 'red'),
      face('east', 'X', 95.5, 48, 15),
      face('north', 'Y', 82, 38, 14),
      small('start', 'START', 50, 48),
    ],
  },
  wii: {
    name: 'Wii Remote (sideways)',
    hint: 'Hold the phone sideways like a Wii Remote: D-pad left, 1 and 2 right',
    controls: [
      { kind: 'dpad', x: 18, y: 50, size: 44 },
      small('back', '−', 38.5, 26),
      small('home', '⌂', 53, 26),
      small('start', '+', 67.5, 26),
      // Recenter for Dolphin's pointer (#32); Home stays Home.
      small('centre', '⊙', 53, 88),
      face('west', '1', 70, 64, 22),
      face('south', '2', 88, 64, 22),
      face('east', 'A', 50, 66, 20, 'blue'),
      pill('rt', 'B', 88, 16, 20),
    ],
  },
});

export const DEFAULT_LAYOUT = 'xbox';

// A pill is wider than tall: shoulder buttons and the small centre buttons.
const PILL_WIDTH = 1.9;
const PILL_HEIGHT = 0.8;

/** A control's width and height in units of 1% of the screen height. */
export function controlSize(control) {
  const isPill = control.shape === 'pill';
  return {
    width: isPill ? control.size * PILL_WIDTH : control.size,
    height: isPill ? control.size * PILL_HEIGHT : control.size,
  };
}

/**
 * A control's box on a screen `aspect` times wider than tall, in units of 1% of the screen
 * height. The overlap test uses it; the page sizes controls with the same controlSize.
 */
export function controlBox(control, aspect) {
  const { width, height } = controlSize(control);
  const cx = (control.x / 100) * 100 * aspect;
  return {
    left: cx - width / 2,
    right: cx + width / 2,
    top: control.y - height / 2,
    bottom: control.y + height / 2,
  };
}
