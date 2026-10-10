'use strict';

// Paces motion samples to DSU clients at the phone's own sample spacing (#29): each goes out at
// phone timestamp + fastest recent transit + buffer; a late one goes out at once, in order.
// doc-ref 938d docs/4-systems/DsuServer.md

// How fast the transit estimate may rise, per sample: 20 us at 60 Hz follows a phone clock
// up to ~1200 ppm slow. Real phone clocks are within ~100 ppm.
const TRANSIT_CREEP_US = 20;

function defaultNowUs() {
  return Number(process.hrtime.bigint() / 1000n);
}

/**
 * @param {object} options
 * @param {number} options.bufferUs extra delay on top of the fastest transit; 0 sends at once
 * @param {(slot:number, sample:object) => void} options.send
 * @param {() => number} [options.nowUs] monotonic clock in microseconds
 * @param {(fn:() => void) => void} [options.schedule] how the next pump is queued
 */
function createPacer({ bufferUs, send, nowUs = defaultNowUs, schedule = setImmediate }) {
  const slots = new Map(); // slot -> { transitUs, queue: [{ due, sample }] }
  let pumping = false;

  function slotState(slot) {
    let state = slots.get(slot);
    if (!state) {
      state = { transitUs: null, queue: [] };
      slots.set(slot, state);
    }
    return state;
  }

  function push(slot, sample) {
    if (bufferUs <= 0) {
      send(slot, sample);
      return;
    }
    const now = nowUs();
    const state = slotState(slot);
    const transit = now - sample.ts;
    state.transitUs = state.transitUs === null ? transit : Math.min(state.transitUs + TRANSIT_CREEP_US, transit);
    state.queue.push({ due: sample.ts + state.transitUs + bufferUs, sample });
    pump();
  }

  function pump() {
    const now = nowUs();
    let waiting = false;
    for (const [slot, state] of slots) {
      // In order: a late sample never overtakes an earlier one still waiting.
      while (state.queue.length > 0 && state.queue[0].due <= now) send(slot, state.queue.shift().sample);
      if (state.queue.length > 0) waiting = true;
    }
    if (waiting && !pumping) {
      pumping = true;
      schedule(() => {
        pumping = false;
        pump();
      });
    }
  }

  function clear(slot) {
    slots.delete(slot);
  }

  function queued() {
    let total = 0;
    for (const state of slots.values()) total += state.queue.length;
    return total;
  }

  return { push, pump, clear, queued };
}

module.exports = { createPacer };
