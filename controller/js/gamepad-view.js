/**
 * Draws a layout from layouts.js and turns touches into GamepadState changes (issue #4).
 *
 * Each control owns the pointers that start on it (pointer capture), so a thumb on a stick
 * and a thumb on a button never steal from each other, and a thumb that slides off a button
 * keeps it held until it lifts, the way a physical button behaves under a thumb.
 */

import { stickFromOffset, dpadFromOffset } from './gamepad-state.js';
import { controlSize } from './layouts.js';

const HAPTIC_MS = 8;

function vibrate() {
  // Android only: iPhone Safari has no navigator.vibrate.
  if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function')
    navigator.vibrate(HAPTIC_MS);
}

function place(el, control) {
  const { width, height } = controlSize(control);
  el.style.left = `${control.x}%`;
  el.style.top = `${control.y}%`;
  // vmin: the short side, which is the height in landscape (as before) and the width upright.
  el.style.width = `${width}vmin`;
  el.style.height = `${height}vmin`;
}

/**
 * Render `layout` into `container`, wired to `state`. `onChange` is called after any change
 * the PC should hear about, right away, so a press is never held back for a timer.
 * Returns a function that releases everything (used when switching layouts).
 */
export function mountGamepad(container, layout, state, onChange) {
  container.replaceChildren();
  const releasers = [];

  for (const control of layout.controls) {
    const el = document.createElement('div');
    el.className = `control ${control.kind}${control.shape === 'pill' ? ' pill button' : ''}`;
    if (control.kind === 'button' && control.shape !== 'pill') el.classList.add('button');
    if (control.color) el.dataset.color = control.color;
    place(el, control);
    container.appendChild(el);

    if (control.kind === 'button') releasers.push(wireButton(el, control, state, onChange));
    else if (control.kind === 'stick') releasers.push(wireStick(el, control, state, onChange));
    else if (control.kind === 'dpad') releasers.push(wireDpad(el, state, onChange));
  }

  return () => {
    for (const release of releasers) release();
    onChange();
  };
}

function wireButton(el, control, state, onChange) {
  el.textContent = control.label;
  el.dataset.button = control.id;
  const pointers = new Set();

  const down = (event) => {
    event.preventDefault();
    el.setPointerCapture(event.pointerId);
    pointers.add(event.pointerId);
    el.classList.add('pressed');
    if (state.press(control.id)) {
      vibrate();
      onChange();
    }
  };
  const up = (event) => {
    if (!pointers.delete(event.pointerId) || pointers.size > 0) return;
    el.classList.remove('pressed');
    if (state.release(control.id)) onChange();
  };
  el.addEventListener('pointerdown', down);
  el.addEventListener('pointerup', up);
  el.addEventListener('pointercancel', up);

  return () => {
    pointers.clear();
    state.release(control.id);
  };
}

function wireStick(el, control, state, onChange) {
  const knob = document.createElement('div');
  knob.className = 'stick-knob';
  knob.textContent = control.label || '';
  el.appendChild(knob);
  el.dataset.stick = control.side;
  let activePointer = null;

  const update = (event) => {
    const rect = el.getBoundingClientRect();
    const radius = rect.width / 2;
    const dx = event.clientX - (rect.left + radius);
    const dy = event.clientY - (rect.top + radius);
    const value = stickFromOffset(dx, dy, radius * 0.75);
    // The knob shows where the stick reads, not where the finger is.
    knob.style.transform = `translate(${value.x * radius * 0.56}px, ${-value.y * radius * 0.56}px)`;
    if (state.setStick(control.side, value)) onChange();
  };
  const reset = () => {
    activePointer = null;
    el.classList.remove('active');
    knob.style.transform = '';
    if (state.setStick(control.side, { x: 0, y: 0 })) onChange();
  };

  el.addEventListener('pointerdown', (event) => {
    if (activePointer !== null) return;
    event.preventDefault();
    activePointer = event.pointerId;
    el.setPointerCapture(event.pointerId);
    el.classList.add('active');
    update(event);
  });
  el.addEventListener('pointermove', (event) => {
    if (event.pointerId === activePointer) update(event);
  });
  const end = (event) => {
    if (event.pointerId === activePointer) reset();
  };
  el.addEventListener('pointerup', end);
  el.addEventListener('pointercancel', end);

  return () => {
    activePointer = null;
    state.setStick(control.side, { x: 0, y: 0 });
  };
}

const DPAD_DIRECTIONS = ['up', 'down', 'left', 'right'];

function wireDpad(el, state, onChange) {
  const arms = {};
  for (const dir of DPAD_DIRECTIONS) {
    const arm = document.createElement('div');
    arm.className = 'dpad-arm';
    arm.dataset.dir = dir;
    el.appendChild(arm);
    arms[dir] = arm;
  }
  let activePointer = null;

  const apply = (pressed) => {
    let changed = false;
    for (const dir of DPAD_DIRECTIONS) {
      const on = pressed.includes(dir);
      arms[dir].classList.toggle('pressed', on);
      changed = (on ? state.press(dir) : state.release(dir)) || changed;
    }
    if (changed) {
      if (pressed.length) vibrate();
      onChange();
    }
  };
  const update = (event) => {
    const rect = el.getBoundingClientRect();
    const radius = rect.width / 2;
    apply(
      dpadFromOffset(
        event.clientX - (rect.left + radius),
        event.clientY - (rect.top + radius),
        radius,
      ),
    );
  };

  el.addEventListener('pointerdown', (event) => {
    if (activePointer !== null) return;
    event.preventDefault();
    activePointer = event.pointerId;
    el.setPointerCapture(event.pointerId);
    update(event);
  });
  el.addEventListener('pointermove', (event) => {
    if (event.pointerId === activePointer) update(event);
  });
  const end = (event) => {
    if (event.pointerId !== activePointer) return;
    activePointer = null;
    apply([]);
  };
  el.addEventListener('pointerup', end);
  el.addEventListener('pointercancel', end);

  return () => {
    activePointer = null;
    for (const dir of DPAD_DIRECTIONS) state.release(dir);
  };
}
