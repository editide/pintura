'use strict';

// How the preview and the exported page move: turning slides with the wheel or a touch, and zooming by steps or by the
// wheel about the pointer. Pure, given the page's state, so the pages wire it to events and node --test checks it.

// Zooms as multiples of the slide at fit.
const ZOOM_STEPS = [0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8];
const GRID_ZOOM_STEPS = [0.5, 0.75, 1, 1.25, 1.5, 2, 3];

// The slide a wheel turns to, 1 or -1, or 0 to leave the wheel to scroll, zoom, or edit text. The container scrolls
// a slide zoomed taller than its view.
function wheelStep(event, state, editing, container) {
  if (!state.deck || (state.view !== 'slide' && !state.presenting) || editing.text || editing.gesture ||
      event.ctrlKey || event.metaKey || event.altKey || container.scrollHeight > container.clientHeight ||
      event.target.closest('input, textarea, [contenteditable="true"]') ||
      !event.deltaY || Math.abs(event.deltaX) > Math.abs(event.deltaY)) {
    return 0;
  }
  return Math.sign(event.deltaY);
}

// The next step in or out, from a zoom a pinch may have left between steps.
function zoomStep(zoom, direction, steps = ZOOM_STEPS) {
  const next = direction > 0 ? steps.find((step) => step > zoom + 1e-6)
    : steps.findLast((step) => step < zoom - 1e-6);
  return next ?? zoom;
}

// A mouse notch is 100 pixels, or 3 lines in Firefox, and zooms by about a fifth; a pinch sends smaller deltas.
function wheelZoom(zoom, event, steps = ZOOM_STEPS) {
  const pixels = event.deltaMode === 1 ? event.deltaY * 100 / 3 : event.deltaY;
  return Math.min(Math.max(zoom * Math.exp(-pixels * 0.002), steps[0]), steps[steps.length - 1]);
}

// The scroll that keeps the point of the slide under the pointer there as its frame moves from before to after, read
// at the same scroll.
function anchoredScroll(scroll, pointer, before, after) {
  return {
    left: scroll.left + after.left + (pointer.x - before.left) / before.width * after.width - pointer.x,
    top: scroll.top + after.top + (pointer.y - before.top) / before.height * after.height - pointer.y
  };
}

// The slide a swipe from start to end turns to: left advances, right goes back, and 0 for a tap or a vertical drag.
function swipeStep(start, end) {
  const dx = end.x - start.x;
  return Math.abs(dx) >= 40 && Math.abs(dx) > 2 * Math.abs(end.y - start.y) ? -Math.sign(dx) : 0;
}

// The slide a tap at x turns to, going back on the left third of the frame as most viewers do.
function tapStep(x, frame) {
  return x - frame.left < frame.width / 3 ? -1 : 1;
}

// What a click on the exported slide does: 'trace' a number to its book, 'step' to the slide tapStep picks, or 'none'
// for a drag that selected text or a click that clears a selection. A finger turning slides on a phone lands on
// numbers by accident, so a touch traces only by a long press.
function clickAction({ pointer, traced, selecting, dragged }) {
  if (selecting || dragged) {
    return 'none';
  }
  return traced && pointer !== 'touch' ? 'trace' : 'step';
}

if (typeof module !== 'undefined') {
  module.exports = { wheelStep, zoomStep, wheelZoom, anchoredScroll, swipeStep, tapStep, clickAction };
}