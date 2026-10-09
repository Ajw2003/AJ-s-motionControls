/**
 * Unit tests for the on-screen gamepad's state, wire message and layouts (issue #4).
 * No browser, no device.
 *
 * Run with: npm test    (from controller/)
 */

import assert from 'node:assert/strict';
import {
  Button,
  BUTTON_NAMES,
  GamepadState,
  makePadMessage,
  stickFromOffset,
  dpadFromOffset,
  STICK_DEADZONE,
  PAD_MESSAGE_TYPE,
} from './js/gamepad-state.js';
import { LAYOUTS, DEFAULT_LAYOUT, controlBox } from './js/layouts.js';

let failures = 0;
function check(label, fn) {
  try {
    fn();
    console.log(`  PASS  ${label}`);
  } catch (err) {
    failures++;
    console.log(`  FAIL  ${label} — ${err.message}`);
  }
}

console.log('gamepad-state');

check('button bits are distinct single bits', () => {
  const bits = Object.values(Button);
  assert.equal(new Set(bits).size, bits.length);
  for (const bit of bits) assert.equal(bit & (bit - 1), 0);
});

check('the wire order of the first buttons never moves (append only)', () => {
  assert.deepEqual(BUTTON_NAMES.slice(0, 4), ['south', 'east', 'west', 'north']);
  assert.equal(Button.rt, 1 << 16);
});

check('press and release report whether anything changed', () => {
  const state = new GamepadState();
  assert.equal(state.press('south'), true);
  assert.equal(state.press('south'), false);
  assert.equal(state.isPressed('south'), true);
  assert.equal(state.release('south'), true);
  assert.equal(state.release('south'), false);
});

check('several buttons at once', () => {
  const state = new GamepadState();
  state.press('south');
  state.press('rt');
  state.press('left');
  assert.deepEqual(state.pressedNames(), ['south', 'left', 'rt']);
});

check('an unknown button is a loud error, not a silent no-op', () => {
  assert.throws(() => new GamepadState().press('turbo'), /unknown button/);
});

check('stick: inside the deadzone reads centred', () => {
  assert.deepEqual(stickFromOffset(5, 5, 100), { x: 0, y: 0 });
});

check('stick: just past the deadzone starts near 0, not at 0.12', () => {
  const v = stickFromOffset(STICK_DEADZONE * 100 + 1, 0, 100);
  assert.ok(v.x > 0 && v.x < 0.02, `got ${v.x}`);
});

check('stick: full right is +1, full up is +1 (screen y is flipped)', () => {
  assert.deepEqual(stickFromOffset(100, 0, 100), { x: 1, y: 0 });
  assert.deepEqual(stickFromOffset(0, -100, 100), { x: 0, y: 1 });
});

check('stick: past the edge clamps to the unit circle', () => {
  const v = stickFromOffset(300, 300, 100);
  assert.ok(Math.abs(Math.hypot(v.x, v.y) - 1) < 0.002, `length ${Math.hypot(v.x, v.y)}`);
});

check('d-pad: the four directions and four diagonals', () => {
  const r = 100;
  assert.deepEqual(dpadFromOffset(80, 0, r), ['right']);
  assert.deepEqual(dpadFromOffset(0, -80, r), ['up']);
  assert.deepEqual(dpadFromOffset(-80, 0, r), ['left']);
  assert.deepEqual(dpadFromOffset(0, 80, r), ['down']);
  assert.deepEqual(dpadFromOffset(60, -60, r).sort(), ['right', 'up']);
  assert.deepEqual(dpadFromOffset(-60, -60, r).sort(), ['left', 'up']);
  assert.deepEqual(dpadFromOffset(-60, 60, r).sort(), ['down', 'left']);
  assert.deepEqual(dpadFromOffset(60, 60, r).sort(), ['down', 'right']);
});

check('d-pad: the centre presses nothing', () => {
  assert.deepEqual(dpadFromOffset(5, 5, 100), []);
});

check('PAD message carries the whole state', () => {
  const state = new GamepadState();
  state.press('east');
  state.setStick('left', { x: 0.5, y: -0.25 });
  assert.deepEqual(makePadMessage(state, 9), {
    type: PAD_MESSAGE_TYPE,
    seq: 9,
    buttons: Button.east,
    sticks: [0.5, -0.25, 0, 0],
  });
});

console.log('layouts');

check('the default layout exists', () => assert.ok(LAYOUTS[DEFAULT_LAYOUT]));

for (const [id, layout] of Object.entries(LAYOUTS)) {
  check(`${id}: every button drives a real gamepad position`, () => {
    for (const c of layout.controls.filter((c) => c.kind === 'button')) {
      assert.ok(Button[c.id] !== undefined, `${c.label} -> ${c.id}`);
    }
  });
  check(`${id}: no position is driven by two buttons`, () => {
    const ids = layout.controls.filter((c) => c.kind === 'button').map((c) => c.id);
    assert.equal(new Set(ids).size, ids.length, ids.join(','));
  });
  check(`${id}: every control sits on screen and is thumb-sized`, () => {
    for (const c of layout.controls) {
      assert.ok(
        c.x >= 0 && c.x <= 100 && c.y >= 0 && c.y <= 100,
        `${c.label ?? c.kind} at ${c.x},${c.y}`,
      );
      assert.ok(c.size >= 13, `${c.label ?? c.kind} size ${c.size}`);
    }
  });
}

// From a 16:9 phone to a tall modern one (iPhone 13 is 844x390, about 2.16).
const ASPECTS = [1.78, 2.0, 2.17];
const GAP = 1; // 1% of screen height between controls, so a thumb on one is not on another

for (const [id, layout] of Object.entries(LAYOUTS)) {
  for (const aspect of ASPECTS) {
    check(`${id} at ${aspect}:1: no two controls overlap and none leaves the screen`, () => {
      const boxes = layout.controls.map((c) => ({ c, b: controlBox(c, aspect) }));
      // The status chip (also the layout menu) sits top centre on every layout.
      const width = 100 * aspect;
      boxes.push({
        c: { label: 'status chip' },
        b: { left: width / 2 - 28, right: width / 2 + 28, top: 0, bottom: 10 },
      });
      const problems = [];
      for (const { c, b } of boxes) {
        if (b.left < 0 || b.top < 0 || b.right > 100 * aspect || b.bottom > 100) {
          problems.push(`${c.label ?? c.kind} off screen`);
        }
      }
      for (let i = 0; i < boxes.length; i++) {
        for (let j = i + 1; j < boxes.length; j++) {
          const a = boxes[i].b;
          const b = boxes[j].b;
          const apart =
            a.right + GAP <= b.left ||
            b.right + GAP <= a.left ||
            a.bottom + GAP <= b.top ||
            b.bottom + GAP <= a.top;
          if (!apart)
            problems.push(
              `${boxes[i].c.label ?? boxes[i].c.kind} / ${boxes[j].c.label ?? boxes[j].c.kind}`,
            );
        }
      }
      assert.deepEqual(problems, []);
    });
  }
}

check('switch: Nintendo A sits on the east position, B on south', () => {
  const byLabel = Object.fromEntries(
    LAYOUTS.switch.controls.filter((c) => c.label).map((c) => [c.label, c.id]),
  );
  assert.equal(byLabel.A, 'east');
  assert.equal(byLabel.B, 'south');
});

if (failures) {
  console.log(`\nFAIL — ${failures} gamepad check(s) failed`);
  process.exit(1);
}
console.log('\nPASS — all gamepad checks green');
