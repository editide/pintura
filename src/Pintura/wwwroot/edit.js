'use strict';

// Mouse edits on the slide view: handles on the selected item, dragging, resizing, and arrow-key nudges that snap to
// the slide, its areas and placeholders, and other items, rewording text in place on a double click or a click on
// the selected item, deleting an element, and reordering slides by dragging them in the sidebar. An edit goes to the
// server as the values it writes and the source each replaces; the server patches the file, and the page redraws
// from it, so nothing lives only in the page. The server answers each edit with the edit that writes it back, which
// undo and redo send in turn.

const FIELD_MARK = '￼';
const SNAP_PIXELS = 6;
const DRAG_PIXELS = 3;
const NUDGE_DELAY = 400;
const HANDLES = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];

// #region Geometry

function stops(start, length) {
  return [start, start + length / 2, start + length];
}

// The positions a moving item snaps to, in points: the slide's edges and middle, and the edges and middles of its
// areas, placeholders, and every item but the one moving.
function snapTargets(slide, size, skip) {
  const targets = { x: [0, size[0] / 2, size[0]], y: [0, size[1] / 2, size[1]] };
  const add = ([left, top, width, height]) => {
    targets.x.push(...stops(left, width));
    targets.y.push(...stops(top, height));
  };
  for (const guide of slide.guides || []) {
    add(guide.rect);
  }
  (slide.items || []).forEach((item, index) => {
    if (index === skip) {
      return;
    }
    if (item.frame) {
      add(item.frame);
    } else if (item.from) {
      targets.x.push(item.from[0], item.to[0]);
      targets.y.push(item.from[1], item.to[1]);
    }
  });
  return targets;
}

// The smallest shift within the threshold that lands one of the values on a target.
function nearest(values, targets, threshold) {
  let best = null;
  for (const value of values) {
    for (const target of targets) {
      const shift = target - value;
      if (Math.abs(shift) <= threshold && (!best || Math.abs(shift) < Math.abs(best.shift))) {
        best = { shift, at: target };
      }
    }
  }
  return best;
}

// The targets that the stops land on, once snapped, as lines.
function landed(values, targets, axis) {
  const at = values.map((value) => targets.find((target) => Math.abs(target - value) < 0.01)).filter((t) => t != null);
  return [...new Set(at)].map((target) => ({ axis, at: target }));
}

// A box moved by dx and dy, snapped on each axis by an edge or its middle, with a line at every edge and middle that
// then lines up with a target; without targets, it doesn't snap. An axis that doesn't snap lands on a whole point.
function moveBox([left, top, width, height], dx, dy, targets, threshold) {
  let x = left + dx;
  let y = top + dy;
  const lines = [];
  const snapX = targets && nearest(stops(x, width), targets.x, threshold);
  const snapY = targets && nearest(stops(y, height), targets.y, threshold);
  if (snapX) {
    x += snapX.shift;
    lines.push(...landed(stops(x, width), targets.x, 'x'));
  } else {
    x = Math.round(x);
  }
  if (snapY) {
    y += snapY.shift;
    lines.push(...landed(stops(y, height), targets.y, 'y'));
  } else {
    y = Math.round(y);
  }
  return { box: [x, y, width, height], lines };
}

// A box resized from a handle such as 'se', its moving edges snapped or on whole points, at least a point wide and
// high. With aspect, a corner keeps the box's proportions, which snapping would break, so it doesn't snap.
function resizeBox(box, handle, dx, dy, targets, threshold, aspect) {
  const [x, y, width, height] = box;
  let left = x;
  let top = y;
  let right = x + width;
  let bottom = y + height;
  const lines = [];
  const snap = (value, axis) => {
    const hit = targets && nearest([value], targets[axis], threshold);
    if (!hit) {
      return Math.round(value);
    }
    lines.push({ axis, at: hit.at });
    return hit.at;
  };
  if (handle.includes('w')) {
    left = snap(left + dx, 'x');
  }
  if (handle.includes('e')) {
    right = snap(right + dx, 'x');
  }
  if (handle.includes('n')) {
    top = snap(top + dy, 'y');
  }
  if (handle.includes('s')) {
    bottom = snap(bottom + dy, 'y');
  }
  if (aspect && handle.length === 2 && width > 0 && height > 0) {
    let newWidth = right - left;
    let newHeight = bottom - top;
    if (Math.abs(newWidth / width) > Math.abs(newHeight / height)) {
      newHeight = newWidth * height / width;
    } else {
      newWidth = newHeight * width / height;
    }
    left = handle.includes('w') ? Math.round(right - newWidth) : left;
    right = handle.includes('w') ? right : Math.round(left + newWidth);
    top = handle.includes('n') ? Math.round(bottom - newHeight) : top;
    bottom = handle.includes('n') ? bottom : Math.round(top + newHeight);
    lines.length = 0;
  }
  if (right - left < 1) {
    [left, right] = handle.includes('w') ? [right - 1, right] : [left, left + 1];
  }
  if (bottom - top < 1) {
    [top, bottom] = handle.includes('n') ? [bottom - 1, bottom] : [top, top + 1];
  }
  return { box: [left, top, right - left, bottom - top], lines };
}

// A line's end moved by dx and dy, snapped on each axis or on a whole point.
function movePoint([x, y], dx, dy, targets, threshold) {
  const lines = [];
  const snap = (value, axis) => {
    const hit = targets && nearest([value], targets[axis], threshold);
    if (!hit) {
      return Math.round(value);
    }
    lines.push({ axis, at: hit.at });
    return hit.at;
  };
  return { point: [snap(x + dx, 'x'), snap(y + dy, 'y')], lines };
}

// A whole line moved, snapped by the box its ends span.
function moveLine(from, to, dx, dy, targets, threshold) {
  const box = [Math.min(from[0], to[0]), Math.min(from[1], to[1]), Math.abs(to[0] - from[0]),
    Math.abs(to[1] - from[1])];
  const moved = moveBox(box, dx, dy, targets, threshold);
  const shiftX = moved.box[0] - box[0];
  const shiftY = moved.box[1] - box[1];
  return {
    from: [from[0] + shiftX, from[1] + shiftY],
    to: [to[0] + shiftX, to[1] + shiftY],
    lines: moved.lines
  };
}

// #endregion

// #region Editing on the page

const editing = {
  gesture: null,
  nudge: null,
  text: null,
  busy: false,
  reloadPending: false,
  suppressClick: false,
  reorder: null,
  reorderClick: false,
  noticeTimer: null,
  undo: [],
  redo: [],
  // Undos and redos pressed while an edit is in flight, replayed in order once it lands.
  queued: []
};

// While a gesture or a rewording is under way a reload would redraw the slide out from under it, so it waits.
function editingActive() {
  return Boolean((editing.gesture && editing.gesture.started) || (editing.reorder && editing.reorder.started) ||
    editing.nudge || editing.text || editing.busy);
}

function afterEditing() {
  if (!editingActive() && editing.reloadPending) {
    editing.reloadPending = false;
    load();
  }
}

function notice(message) {
  const node = document.getElementById('notice');
  node.textContent = message;
  node.hidden = false;
  clearTimeout(editing.noticeTimer);
  editing.noticeTimer = setTimeout(() => {
    node.hidden = true;
  }, 5000);
}

function slideStage() {
  return document.querySelector('#slide-frame .stage');
}

function stageScale(stage) {
  return stage.getBoundingClientRect().width / stage.offsetWidth;
}

function itemNode(index) {
  return document.querySelector(`#slide-frame .item[data-index="${index}"]`);
}

// Handles on the selected item: eight on a box, one on each end of a line, none on a rotated box or on an item the
// slide doesn't own.
function drawSelection() {
  const stage = slideStage();
  stage?.querySelector(':scope > .selection')?.remove();
  const node = selectedNode();
  if (!stage || !node || editing.text) {
    return;
  }
  const item = currentSlide().items[state.selected.index];
  const layer = element('div', `selection${item.edit.locked ? ' locked' : ''}`, stage);
  if (item.from) {
    if (item.edit.locked) {
      return;
    }
    for (const [end, [x, y]] of [['from', item.from], ['to', item.to]]) {
      const handle = element('div', 'handle end', layer);
      handle.dataset.end = end;
      handle.style.left = `${x}px`;
      handle.style.top = `${y}px`;
    }
    return;
  }
  const box = element('div', 'selection-box', layer);
  place(box, [node.offsetLeft, node.offsetTop, node.offsetWidth, node.offsetHeight]);
  box.style.transform = node.style.transform;
  if (item.edit.locked || item.rotate) {
    return;
  }
  for (const name of HANDLES) {
    element('div', `handle ${name}`, box).dataset.handle = name;
  }
}

function drawSnaps(stage, lines) {
  stage.querySelector(':scope > .snaps')?.remove();
  if (lines.length === 0) {
    return;
  }
  const layer = element('div', 'snaps', stage);
  for (const line of lines) {
    const node = element('div', `snap ${line.axis}`, layer);
    node.style[line.axis === 'x' ? 'left' : 'top'] = `${line.at}px`;
  }
}

// An item drawn again in place with changed geometry, so a resized shape redraws its path and its text rewraps.
function redraw(gesture, changed) {
  const stage = slideStage();
  const fresh = renderItem(changed, stage);
  fresh.dataset.index = gesture.index;
  fresh.classList.add('selected');
  gesture.node.replaceWith(fresh);
  gesture.node = fresh;
  fitText(stage);
}

// Geometry puts what an edit moves on a whole point or a snap target, and leaves the rest as the layout gave it, such
// as an area's 339.64-point width, so this only drops the float noise.
function hundredths(values) {
  return values.map((value) => Math.round(value * 100) / 100);
}

// The values an edit writes, each with the source it replaces.
function valueEdits(item, change) {
  const edits = [];
  for (const key of ['frame', 'from', 'to']) {
    // A drag that ends where it started writes nothing, rather than a frame the layout already gives.
    const was = item[key] && hundredths(item[key]);
    if (change[key] && hundredths(change[key]).some((value, index) => value !== was[index])) {
      edits.push({ path: item.edit[key].path, source: item.edit[key].source, numbers: hundredths(change[key]) });
    }
  }
  return edits;
}

function send(slide, edits, kind = 'edit') {
  return post('api/edit', { slide, edits }, (undo) => (undo?.length > 0 ? { slide, edits: undo } : null), kind);
}

function sendMove(move, kind = 'edit') {
  return post('api/move', move, (undo) => (undo ? { slide: move.slide, move: undo } : null), kind);
}

// An edit, an undo, or a redo. Each lands its answer on the other's stack, and a new edit forgets what was undone.
async function post(url, request, entry, kind) {
  editing.busy = true;
  try {
    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    });
    const body = await response.json().catch(() => ({}));
    const undo = response.ok ? entry(body.undo) : null;
    if (!response.ok) {
      const failed = body.error || `the edit failed (${response.status})`;
      notice(kind === 'edit' ? failed : `couldn't ${kind}: ${failed}`);
    } else if (undo) {
      (kind === 'undo' ? editing.redo : editing.undo).push(undo);
      if (kind === 'edit') {
        editing.redo.length = 0;
      }
    }
  } catch (error) {
    notice(String(error));
  } finally {
    editing.busy = false;
    editing.reloadPending = false;
    await load();
  }
  if (editing.queued.length > 0) {
    stepHistory(editing.queued.shift());
  }
}

function startGesture(event) {
  flushNudge();
  if (event.button !== 0 || editing.busy || editing.text || state.view !== 'slide' || reviewing()) {
    return;
  }
  const handle = event.target.closest('.handle');
  const node = handle ? selectedNode() : event.target.closest('.item');
  if (!node) {
    return;
  }
  const slide = currentSlide();
  const index = Number(node.dataset.index);
  const stage = slideStage();
  editing.gesture = {
    slide: slide.id,
    pointer: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    index,
    item: slide.items[index],
    node,
    handle: handle ? handle.dataset.handle || handle.dataset.end : null,
    started: false,
    scale: stageScale(stage),
    targets: snapTargets(slide, state.deck.size, index),
    change: null
  };
}

function moveGesture(event) {
  const gesture = editing.gesture;
  if (!gesture || event.pointerId !== gesture.pointer) {
    return;
  }
  const screenX = event.clientX - gesture.startX;
  const screenY = event.clientY - gesture.startY;
  if (!gesture.started) {
    if (Math.hypot(screenX, screenY) < DRAG_PIXELS) {
      return;
    }
    const { edit } = gesture.item;
    if (edit.locked || !(edit.frame || edit.from)) {
      notice(edit.locked || `${gesture.item.id} can't be moved with the mouse`);
      editing.gesture = null;
      return;
    }
    gesture.started = true;
    document.getElementById('slide-frame').setPointerCapture(event.pointerId);
    if (!gesture.handle) {
      document.body.classList.add('moving');
    }
    if (!state.selected || state.selected.index !== gesture.index) {
      const { item } = gesture;
      state.selected = { slide: currentSlide().id, index: gesture.index, id: item.id, owner: item.owner };
      state.detail = null;
      markSelection();
      renderInspector();
      loadDetail();
    }
  }
  let dx = screenX / gesture.scale;
  let dy = screenY / gesture.scale;
  if (event.shiftKey && !gesture.handle) {
    [dx, dy] = Math.abs(dx) > Math.abs(dy) ? [dx, 0] : [0, dy];
  }
  const targets = event.altKey ? null : gesture.targets;
  const threshold = SNAP_PIXELS / gesture.scale;
  const { item } = gesture;
  let lines;
  if (item.from) {
    const moved = gesture.handle
      ? movePoint(item[gesture.handle], dx, dy, targets, threshold)
      : moveLine(item.from, item.to, dx, dy, targets, threshold);
    gesture.change = gesture.handle ? { [gesture.handle]: moved.point } : { from: moved.from, to: moved.to };
    lines = moved.lines;
    redraw(gesture, { ...item, ...gesture.change });
  } else if (gesture.handle) {
    const resized = resizeBox(item.frame, gesture.handle, dx, dy, targets, threshold, event.shiftKey);
    gesture.change = { frame: resized.box };
    lines = resized.lines;
    redraw(gesture, { ...item, frame: resized.box });
  } else {
    const moved = moveBox(item.frame, dx, dy, targets, threshold);
    gesture.change = { frame: moved.box };
    lines = moved.lines;
    gesture.node.style.left = `${Math.round(moved.box[0])}px`;
    gesture.node.style.top = `${Math.round(moved.box[1])}px`;
  }
  drawSnaps(slideStage(), lines);
  drawSelection();
}

function endGesture(event) {
  const gesture = editing.gesture;
  if (!gesture || event.pointerId !== gesture.pointer) {
    return;
  }
  editing.gesture = null;
  if (!gesture.started) {
    return;
  }
  document.body.classList.remove('moving');
  editing.suppressClick = true;
  drawSnaps(slideStage(), []);
  const edits = gesture.change ? valueEdits(gesture.item, gesture.change) : [];
  if (edits.length > 0) {
    send(gesture.slide, edits);
  } else {
    afterEditing();
  }
}

// Arrow keys nudge the selected item a point, or ten with Shift, and write once the keys rest. Returns whether the
// key was a nudge.
function nudgeKey(event) {
  const steps = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
  if (!(event.key in steps) || !state.selected || state.view !== 'slide' || state.presenting || !selectedNode()) {
    return false;
  }
  if (editing.busy) {
    return true;
  }
  const item = currentSlide().items[state.selected.index];
  if (item.edit.locked || !(item.edit.frame || item.edit.from)) {
    notice(item.edit.locked || `${item.id} can't be moved with the mouse`);
    return true;
  }
  const size = event.shiftKey ? 10 : 1;
  if (editing.nudge && (editing.nudge.slide !== currentSlide().id || editing.nudge.index !== state.selected.index)) {
    flushNudge();
    return true;
  }
  const nudge = editing.nudge ||
    { slide: currentSlide().id, index: state.selected.index, item, node: selectedNode(), dx: 0, dy: 0 };
  editing.nudge = nudge;
  nudge.dx += steps[event.key][0] * size;
  nudge.dy += steps[event.key][1] * size;
  if (item.from) {
    const moved = moveLine(item.from, item.to, nudge.dx, nudge.dy, null, 0);
    nudge.change = { from: moved.from, to: moved.to };
    redraw(nudge, { ...item, ...nudge.change });
  } else {
    nudge.change = { frame: moveBox(item.frame, nudge.dx, nudge.dy, null, 0).box };
    nudge.node.style.left = `${nudge.change.frame[0]}px`;
    nudge.node.style.top = `${nudge.change.frame[1]}px`;
  }
  drawSelection();
  clearTimeout(nudge.timer);
  nudge.timer = setTimeout(flushNudge, NUDGE_DELAY);
  return true;
}

// Writes a nudge still waiting for the keys to rest. Whatever changes the selection calls this first, so the next
// arrow key starts a nudge of its own rather than adding to the last item's.
function flushNudge() {
  const nudge = editing.nudge;
  if (!nudge) {
    return;
  }
  clearTimeout(nudge.timer);
  editing.nudge = null;
  const edits = valueEdits(nudge.item, nudge.change);
  if (edits.length > 0) {
    send(nudge.slide, edits);
  } else {
    afterEditing();
  }
}

// Delete or Backspace removes the selected element from the slide's file. Returns whether the key was one of them.
function deleteKey(event) {
  if (!(event.key === 'Delete' || event.key === 'Backspace') || !state.selected || state.view !== 'slide' ||
    state.presenting || !selectedNode()) {
    return false;
  }
  if (editingActive()) {
    return true;
  }
  const item = currentSlide().items[state.selected.index];
  if (!item.edit.delete) {
    notice(item.edit.locked || `${item.id} is a placeholder the layout gives; empty it in the file`);
    return true;
  }
  const slide = currentSlide().id;
  selectItem(null);
  send(slide, [{ path: item.edit.delete.path, source: item.edit.delete.source, delete: true }]);
  return true;
}

// Delete or Backspace on a slide focused in the sidebar, or the current one in the grid, removes it and its file,
// showing the slide after it. Returns whether the key was one of them.
function deleteSlideKey(event) {
  if (!(event.key === 'Delete' || event.key === 'Backspace') || state.presenting || reviewing()) {
    return false;
  }
  const container = document.getElementById(state.view === 'grid' ? 'grid' : 'thumbnails');
  const node = state.view === 'grid'
    ? container.querySelector(`[data-slide="${CSS.escape(currentSlide().id)}"]`)
    : event.target.closest('#thumbnails [data-slide]');
  if (!node) {
    return false;
  }
  if (editingActive()) {
    return true;
  }
  const slides = state.deck.slides;
  const index = slides.findIndex((slide) => slide.id === node.dataset.slide);
  const next = slides[index + 1] || slides[index - 1];
  if (next) {
    select(next.id);
  }
  sendMove({ slide: node.dataset.slide, from: listEntry(nodeBefore(container, node)), to: null, delete: true });
  return true;
}

// Ctrl+Z undoes the last edit on any slide, showing that slide, and Ctrl+Shift+Z or Ctrl+Y redoes it. Returns
// whether the key was one of them.
function undoKey(event) {
  const key = event.key.toLowerCase();
  if (!(event.ctrlKey || event.metaKey) || event.altKey || !(key === 'z' || key === 'y') || state.presenting) {
    return false;
  }
  const kind = key === 'y' || event.shiftKey ? 'redo' : 'undo';
  if (editing.busy && !editing.text) {
    editing.queued.push(kind);
    return true;
  }
  if (editingActive()) {
    return true;
  }
  stepHistory(kind);
  return true;
}

// An item, or with no id the whole slide, written back as the revision compared against has it, undone as an edit.
function revert(slide, id, owner) {
  if (editingActive()) {
    return;
  }
  post('api/revert', { base: state.compare.commit, slide, id, owner },
    (undo) => (undo?.length > 0 ? { slide, edits: undo } : null), 'edit');
}

function stepHistory(kind) {
  const entry = editing[kind].pop();
  if (entry) {
    if (entry.slide !== state.current) {
      select(entry.slide);
    }
    if (entry.move) {
      sendMove(entry.move, kind);
    } else if (entry.comment) {
      sendComment(entry.comment, entry.slide, kind);
    } else {
      send(entry.slide, entry.edits, kind);
    }
  } else {
    editing.queued.length = 0;
  }
}

// #endregion

// #region Rewording text

// A paragraph's text as the server reads it back: fields as FIELD_MARK, without bullets or the placeholder that
// keeps an empty paragraph's height.
function paragraphText(node) {
  let text = '';
  for (const child of node.childNodes) {
    if (child.nodeType === Node.TEXT_NODE) {
      text += child.data;
    } else if (child.nodeType !== Node.ELEMENT_NODE || child.classList.contains('bullet')) {
      continue;
    } else if (child.dataset.field !== undefined) {
      text += FIELD_MARK;
    } else {
      text += paragraphText(child);
    }
  }
  return text.replace(/​/g, '');
}

function paragraphTexts(flow) {
  return [...flow.querySelectorAll('.paragraph')].map(paragraphText);
}

function startText(event) {
  const node = event.target.closest('#slide-frame .item');
  if (!node || editing.busy || editing.text || reviewing()) {
    return;
  }
  // A double click has already selected the word under it; keep that, or else put the caret where it landed.
  beginText(node, (flow) => {
    const selection = getSelection();
    if (selection.isCollapsed || !flow.contains(selection.anchorNode)) {
      const caret = document.caretPositionFromPoint?.(event.clientX, event.clientY);
      if (caret && flow.contains(caret.offsetNode)) {
        selection.collapse(caret.offsetNode, caret.offset);
      }
    }
  });
}

function beginText(node, place) {
  const item = currentSlide().items[Number(node.dataset.index)];
  const flow = node.querySelector(':scope > .text-box > .text-flow');
  if (!item.edit.text) {
    if (flow || item.edit.textLocked) {
      notice(item.edit.textLocked || item.edit.locked || `${item.id}'s text can't be edited on the slide`);
    }
    return;
  }
  if (!flow) {
    return;
  }
  for (const fixed of flow.querySelectorAll('.bullet, [data-field]')) {
    fixed.contentEditable = 'false';
  }
  // Firefox puts no caret beside a field without text there to hold it.
  for (const field of flow.querySelectorAll('[data-field]')) {
    field.before('\u200B');
    field.after('\u200B');
  }
  editing.text = { slide: currentSlide().id, node, item, flow, before: paragraphTexts(flow) };
  node.classList.add('editing');
  drawSelection();
  flow.contentEditable = 'true';
  flow.focus();
  place(flow);
  showFormatBar(node);
  flow.addEventListener('keydown', textKey);
  flow.addEventListener('beforeinput', textInput);
  flow.addEventListener('paste', textPaste);
  flow.addEventListener('blur', () => finishText(true), { once: true });
}

function textKey(event) {
  event.stopPropagation();
  const marks = { b: 'bold', i: 'italic', u: 'underline' };
  const mark = (event.ctrlKey || event.metaKey) && !event.altKey && marks[event.key.toLowerCase()];
  if (mark) {
    event.preventDefault();
    formatText(mark);
  } else if (event.key === 'Enter') {
    event.preventDefault();
    finishText(true);
  } else if (event.key === 'Escape') {
    event.preventDefault();
    finishText(false);
  }
}

// Editing on the slide rewords paragraphs; adding one is an edit to the file.
function textInput(event) {
  if (event.inputType === 'insertParagraph' || event.inputType === 'insertLineBreak') {
    event.preventDefault();
  }
}

function textPaste(event) {
  event.preventDefault();
  const text = event.clipboardData.getData('text/plain').replace(/\s*\n\s*/g, ' ');
  document.execCommand('insertText', false, text);
}

function finishText(commit) {
  const editor = editing.text;
  if (!editor) {
    return;
  }
  editing.text = null;
  hideFormatBar();
  const after = paragraphTexts(editor.flow);
  editor.flow.contentEditable = 'false';
  const changed = after.length !== editor.before.length || after.some((text, index) => text !== editor.before[index]);
  if (commit && changed) {
    send(editor.slide, [{ path: editor.item.edit.text.path, source: editor.item.edit.text.source, paragraphs: after }]);
    return;
  }
  if (editing.reloadPending) {
    afterEditing();
  } else {
    renderSlide();
  }
}

// #endregion

// #region Formatting text

const FORMAT_MARKS = [['bold', 'B', 'Bold (Ctrl+B)'], ['italic', 'I', 'Italic (Ctrl+I)'],
  ['underline', 'U', 'Underline (Ctrl+U)']];

// A bar above the text being reworded; pressing a button keeps the selection, since it takes no focus.
function showFormatBar(node) {
  hideFormatBar();
  const view = document.getElementById('slide-view');
  const bar = element('div', 'format-bar', view);
  bar.id = 'format-bar';
  for (const [mark, label, title] of FORMAT_MARKS) {
    const button = element('button', `format-${mark}`, bar);
    button.type = 'button';
    button.textContent = label;
    button.title = title;
    button.setAttribute('aria-label', title);
    button.addEventListener('pointerdown', (event) => event.preventDefault());
    button.addEventListener('click', () => formatText(mark));
  }
  const box = node.getBoundingClientRect();
  const frame = view.getBoundingClientRect();
  const top = box.top - frame.top - bar.offsetHeight - 8;
  bar.style.left = `${Math.max(4, box.left - frame.left) + view.scrollLeft}px`;
  bar.style.top = `${(top < 4 ? box.bottom - frame.top + 8 : top) + view.scrollTop}px`;
}

function hideFormatBar() {
  document.getElementById('format-bar')?.remove();
}

// Characters of a paragraph's text, as paragraphText counts them, before a point in it.
function textOffset(paragraph, container, offset) {
  const range = document.createRange();
  range.setStart(paragraph, 0);
  range.setEnd(container, offset);
  return paragraphText(range.cloneContents()).length;
}

// The point in a paragraph after that many of its characters, a field counting as one.
function textPoint(paragraph, count) {
  let left = count;
  const walk = (node) => {
    for (const child of node.childNodes) {
      if (child.nodeType === Node.TEXT_NODE) {
        let seen = 0;
        for (let at = 0; at <= child.data.length; at++) {
          if (seen === left) {
            return [child, at];
          }
          if (at < child.data.length && child.data[at] !== '\u200B') {
            seen++;
          }
        }
        left -= seen;
      } else if (child.nodeType === Node.ELEMENT_NODE && !child.classList.contains('bullet')) {
        if (child.dataset.field !== undefined) {
          left -= 1;
          if (left === 0) {
            return [child.parentNode, [...child.parentNode.childNodes].indexOf(child) + 1];
          }
        } else {
          const found = walk(child);
          if (found) {
            return found;
          }
        }
      }
    }
    return null;
  };
  return walk(paragraph) || [paragraph, paragraph.childNodes.length];
}

// The text nodes with more than spaces in them that a range takes in.
function selectedTexts(range) {
  const texts = [];
  const walker = document.createTreeWalker(range.commonAncestorContainer, NodeFilter.SHOW_TEXT);
  for (let node = walker.currentNode; node; node = walker.nextNode()) {
    if (node.nodeType === Node.TEXT_NODE && range.intersectsNode(node) && node.data.replace(/\u200B/g, '').trim()) {
      texts.push(node);
    }
  }
  return texts;
}

// Each button shows as pressed while all the selected text has its mark.
function markFormatBar() {
  const bar = document.getElementById('format-bar');
  const selection = getSelection();
  if (!bar || !editing.text || selection.rangeCount === 0) {
    return;
  }
  const texts = editing.text.flow.contains(selection.anchorNode) ? selectedTexts(selection.getRangeAt(0)) : [];
  for (const [mark] of FORMAT_MARKS) {
    const pressed = texts.length > 0 && texts.every((node) => marked(node, mark, editing.text.flow));
    bar.querySelector(`.format-${mark}`).setAttribute('aria-pressed', String(pressed));
  }
}

function marked(node, mark, flow) {
  const element = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
  const style = getComputedStyle(element);
  if (mark === 'bold') {
    return Number(style.fontWeight) >= 600;
  }
  if (mark === 'italic') {
    return style.fontStyle === 'italic';
  }
  for (let at = element; at && at !== flow.parentElement; at = at.parentElement) {
    if (getComputedStyle(at).textDecorationLine.includes('underline')) {
      return true;
    }
  }
  return false;
}

// Turns the mark off where all the selected text has it, and on otherwise, writing what was typed with it, then
// rewords on with the same text selected.
async function formatText(mark) {
  const editor = editing.text;
  const selection = getSelection();
  if (!editor || editing.busy) {
    return;
  }
  if (selection.isCollapsed || !editor.flow.contains(selection.anchorNode) ||
    !editor.flow.contains(selection.focusNode)) {
    notice('Select the text to format first');
    return;
  }
  const range = selection.getRangeAt(0);
  const paragraphs = [...editor.flow.querySelectorAll('.paragraph')];
  const ranges = [];
  paragraphs.forEach((paragraph, index) => {
    if (!range.intersectsNode(paragraph)) {
      return;
    }
    const start = paragraph.contains(range.startContainer)
      ? textOffset(paragraph, range.startContainer, range.startOffset) : 0;
    const end = paragraph.contains(range.endContainer)
      ? textOffset(paragraph, range.endContainer, range.endOffset) : paragraphText(paragraph).length;
    if (end > start) {
      ranges.push({ paragraph: index, start, end });
    }
  });
  const texts = selectedTexts(range);
  if (ranges.length === 0) {
    return;
  }
  const on = !texts.every((node) => marked(node, mark, editor.flow));
  const { item } = editor;
  editing.text = null;
  hideFormatBar();
  const edit = { path: item.edit.text.path, source: item.edit.text.source, paragraphs: paragraphTexts(editor.flow),
    format: { mark, on, ranges } };
  editor.flow.contentEditable = 'false';
  await send(editor.slide, [edit]);
  const fresh = currentSlide().id === editor.slide &&
    [...slideStage().querySelectorAll('.item')].find((candidate) => {
      const drawn = currentSlide().items[Number(candidate.dataset.index)];
      return drawn.id === item.id && drawn.owner === item.owner;
    });
  if (!fresh || !currentSlide().items[Number(fresh.dataset.index)].edit.text) {
    return;
  }
  const first = ranges[0];
  const last = ranges[ranges.length - 1];
  beginText(fresh, (flow) => {
    const drawn = [...flow.querySelectorAll('.paragraph')];
    if (!drawn[first.paragraph] || !drawn[last.paragraph]) {
      return;
    }
    const restored = document.createRange();
    restored.setStart(...textPoint(drawn[first.paragraph], first.start));
    restored.setEnd(...textPoint(drawn[last.paragraph], last.end));
    getSelection().removeAllRanges();
    getSelection().addRange(restored);
  });
}

// #endregion

// #region Reordering slides

// The entry in presentation.yaml's slides that a sidebar or grid node stands for, or null for the top of the list.
function listEntry(node) {
  if (!node) {
    return null;
  }
  return node.dataset.slide !== undefined ? { slide: node.dataset.slide } : { section: node.dataset.section };
}

function sameEntry(one, other) {
  return (one?.slide ?? null) === (other?.slide ?? null) && (one?.section ?? null) === (other?.section ?? null);
}

// The sidebar and the grid list slides and section names in the order presentation.yaml does.
function listNodes(container) {
  return [...container.querySelectorAll('[data-slide], [data-section]')];
}

function nodeBefore(container, node) {
  const nodes = listNodes(container);
  return nodes[nodes.indexOf(node) - 1] || null;
}

// Showing only the changed slides hides the rest, which still hold their places in the list. A gap between two shown
// nodes puts the slide just before the lower one, or just after the last one at the bottom; `beside` holds the two.
function shownSlot(container, nodes, index) {
  const below = nodes[index];
  const after = below ? nodeBefore(container, below) : nodes[index - 1] || null;
  return { after, beside: [nodes[index - 1], below] };
}

// The sidebar's gap nearest the pointer, and a line across the sidebar where it is.
function sidebarSlot(container, x, y) {
  const nodes = listNodes(container).filter((node) => !node.hidden);
  const bounds = nodes.map((node) => node.getBoundingClientRect());
  const index = bounds.filter((bound) => bound.top + bound.height / 2 < y).length;
  const above = bounds[index - 1];
  const below = bounds[index];
  const at = !above ? below.top - 4 : !below ? above.bottom + 4 : (above.bottom + below.top) / 2;
  const box = container.getBoundingClientRect();
  return { ...shownSlot(container, nodes, index), line: { left: box.left, top: at - 1, width: box.width, height: 2 } };
}

// The strip's gap nearest the pointer, and an upright line across the strip where it is.
function stripSlot(container, x) {
  const nodes = listNodes(container).filter((node) => !node.hidden);
  const bounds = nodes.map((node) => node.getBoundingClientRect());
  const index = bounds.filter((bound) => bound.left + bound.width / 2 < x).length;
  const left = bounds[index - 1];
  const right = bounds[index];
  const at = !left ? right.left - 5 : !right ? left.right + 5 : (left.right + right.left) / 2;
  const box = container.getBoundingClientRect();
  return { ...shownSlot(container, nodes, index), line: { left: at - 1, top: box.top, width: 2, height: box.height } };
}

// The grid's gap nearest the pointer: in the section under it, the row of cells nearest it, and the gap in that row
// nearest it, drawn as an upright line between cells.
function gridSlot(container, x, y) {
  const rows = [...container.querySelectorAll('.grid-slides')];
  const distance = (row) => {
    const header = row.previousElementSibling?.classList.contains('grid-section') ? row.previousElementSibling : null;
    const top = (header || row).getBoundingClientRect().top;
    const bottom = row.getBoundingClientRect().bottom;
    return y < top ? top - y : y > bottom ? y - bottom : 0;
  };
  const row = rows.reduce((best, candidate) => (distance(candidate) < distance(best) ? candidate : best));
  const header = row.previousElementSibling?.classList.contains('grid-section') ? row.previousElementSibling : null;
  const cells = [...row.querySelectorAll('.grid-cell')];
  if (cells.length === 0) {
    const box = (header || row).getBoundingClientRect();
    return { after: header, line: { left: box.left, top: box.bottom + 2, width: 60, height: 2 } };
  }
  // Cells wrap, so the gap is found in the visual line nearest the pointer.
  const lines = [];
  for (const cell of cells) {
    const box = cell.getBoundingClientRect();
    const last = lines[lines.length - 1];
    if (last && Math.abs(last.top - box.top) < 1) {
      last.cells.push({ cell, box });
    } else {
      lines.push({ top: box.top, bottom: box.bottom, cells: [{ cell, box }] });
    }
  }
  const line = lines.reduce((best, candidate) => {
    const gap = (entry) => (y < entry.top ? entry.top - y : y > entry.bottom ? y - entry.bottom : 0);
    return gap(candidate) < gap(best) ? candidate : best;
  });
  const index = line.cells.filter(({ box }) => box.left + box.width / 2 < x).length;
  const before = line.cells[index - 1];
  const next = line.cells[index];
  const at = !before ? next.box.left - 8 : !next ? before.box.right + 8 : (before.box.right + next.box.left) / 2;
  const first = cells.indexOf(line.cells[0].cell) + index;
  return {
    after: first === 0 ? header : cells[first - 1],
    line: { left: at - 1, top: line.top, width: 2, height: line.bottom - line.top }
  };
}

function startReorder(event, surface) {
  const node = event.target.closest(surface.draggable);
  editing.reorderClick = false;
  if (event.button !== 0 || !node || editing.busy || editing.text || state.deck.error) {
    return;
  }
  editing.reorder = {
    surface,
    pointer: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    node,
    started: false,
    to: undefined
  };
}

// A dimmed copy of the slide's picture, held where it was grabbed, follows the pointer.
function dragGhost(reorder) {
  const picture = reorder.node.querySelector(reorder.surface.picture);
  const box = picture.getBoundingClientRect();
  const scale = Math.min(1, 120 / box.width);
  const ghost = picture.cloneNode(true);
  ghost.classList.add('drag-ghost');
  ghost.style.width = `${box.width}px`;
  ghost.style.transform = `scale(${scale})`;
  document.body.append(ghost);
  reorder.grab = { x: (reorder.startX - box.left) * scale, y: (reorder.startY - box.top) * scale };
  return ghost;
}

function moveReorder(event) {
  const reorder = editing.reorder;
  if (!reorder || event.pointerId !== reorder.pointer) {
    return;
  }
  const { container, scroller, slot } = reorder.surface;
  if (!reorder.started) {
    if (Math.hypot(event.clientX - reorder.startX, event.clientY - reorder.startY) < DRAG_PIXELS * 2) {
      return;
    }
    reorder.started = true;
    container.setPointerCapture(event.pointerId);
    reorder.node.classList.add('dragging');
    document.body.classList.add('reordering');
    reorder.ghost = dragGhost(reorder);
    reorder.line = element('div', 'drop-line', document.body);
  }
  reorder.ghost.style.left = `${event.clientX - reorder.grab.x}px`;
  reorder.ghost.style.top = `${event.clientY - reorder.grab.y}px`;
  const edges = scroller.getBoundingClientRect();
  if (event.clientY < edges.top + 32) {
    scroller.scrollTop -= 12;
  } else if (event.clientY > edges.bottom - 32) {
    scroller.scrollTop += 12;
  }
  if (event.clientX < edges.left + 32) {
    scroller.scrollLeft -= 12;
  } else if (event.clientX > edges.right - 32) {
    scroller.scrollLeft += 12;
  }
  const { after, beside, line } = slot(container, event.clientX, event.clientY);
  const to = listEntry(after);
  // Dropped beside itself, the slide stays where it is.
  reorder.to = after === reorder.node || beside?.includes(reorder.node) ||
    sameEntry(to, listEntry(nodeBefore(container, reorder.node))) ? undefined : to;
  reorder.line.hidden = reorder.to === undefined;
  Object.assign(reorder.line.style, {
    left: `${line.left}px`, top: `${line.top}px`, width: `${line.width}px`, height: `${line.height}px`
  });
}

function endReorder(event) {
  const reorder = editing.reorder;
  if (!reorder || event.pointerId !== reorder.pointer) {
    return;
  }
  editing.reorder = null;
  if (!reorder.started) {
    return;
  }
  editing.reorderClick = true;
  reorder.node.classList.remove('dragging');
  document.body.classList.remove('reordering');
  reorder.ghost.remove();
  reorder.line.remove();
  if (event.type === 'pointerup' && reorder.to !== undefined) {
    const from = listEntry(nodeBefore(reorder.surface.container, reorder.node));
    sendMove({ slide: reorder.node.dataset.slide, from, to: reorder.to });
  } else {
    afterEditing();
  }
}

// Drag and drop on the sidebar or the grid: the nodes that drag, the picture of the slide the ghost copies, and how
// the gap under the pointer is found.
function installReordering(container, { draggable, picture, scroller, slot }) {
  const surface = { container, draggable, picture, scroller, slot };
  container.addEventListener('pointerdown', (event) => startReorder(event, surface));
  container.addEventListener('pointermove', moveReorder);
  container.addEventListener('pointerup', endReorder);
  container.addEventListener('pointercancel', endReorder);
  container.addEventListener('dragstart', (event) => event.preventDefault());
  // A drag ends in a click, which mustn't show the slide it was released over.
  container.addEventListener('click', (event) => {
    if (editing.reorderClick) {
      editing.reorderClick = false;
      event.stopPropagation();
    }
  }, true);
}

// #endregion

function installEditing(frame) {
  frame.addEventListener('pointerdown', startGesture);
  frame.addEventListener('pointermove', moveGesture);
  frame.addEventListener('pointerup', endGesture);
  frame.addEventListener('pointercancel', endGesture);
  frame.addEventListener('dblclick', startText);
  document.addEventListener('selectionchange', markFormatBar);
  // Firefox drags a picture as an image to drop elsewhere, which cancels the pointer's events.
  frame.addEventListener('dragstart', (event) => event.preventDefault());
}

if (typeof module !== 'undefined') {
  module.exports = { snapTargets, moveBox, resizeBox, movePoint, moveLine, valueEdits };
}