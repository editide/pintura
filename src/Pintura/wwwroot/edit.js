'use strict';

// Mouse edits on the slide view: handles on the selected item, dragging, resizing, and arrow-key nudges that snap to
// the slide, its areas and placeholders, and other items, a box dragged across the slide that selects several items
// to move or delete together, rewording text in place on a click on it or on the selected item, deleting
// an element, and reordering slides by dragging them in the sidebar. An edit goes to the
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

// The positions moving items snap to, in points: the slide's edges and middle, and the edges and middles of its
// areas, placeholders, and every item but those moving, by index.
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
    if (skip.includes(index)) {
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
  const box = itemBox({ from, to });
  const moved = moveBox(box, dx, dy, targets, threshold);
  const shiftX = moved.box[0] - box[0];
  const shiftY = moved.box[1] - box[1];
  return {
    from: [from[0] + shiftX, from[1] + shiftY],
    to: [to[0] + shiftX, to[1] + shiftY],
    lines: moved.lines
  };
}

// The box an item spans: its frame, or the box a line's ends span.
function itemBox(item) {
  if (item.frame) {
    return item.frame;
  }
  const [x1, y1] = item.from;
  const [x2, y2] = item.to;
  return [Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1)];
}

function unionBox(boxes) {
  const left = Math.min(...boxes.map((box) => box[0]));
  const top = Math.min(...boxes.map((box) => box[1]));
  const right = Math.max(...boxes.map((box) => box[0] + box[2]));
  const bottom = Math.max(...boxes.map((box) => box[1] + box[3]));
  return [left, top, right - left, bottom - top];
}

function movable(item) {
  return !item.edit.locked && Boolean(item.edit.frame || item.edit.from);
}

// Whether a line crosses a box, by clipping it to the box's edges.
function lineCrosses([x1, y1], [x2, y2], [left, top, width, height]) {
  let start = 0;
  let end = 1;
  const dx = x2 - x1;
  const dy = y2 - y1;
  for (const [p, q] of [[-dx, x1 - left], [dx, left + width - x1], [-dy, y1 - top], [dy, top + height - y1]]) {
    if (p === 0) {
      if (q < 0) {
        return false;
      }
    } else if (p < 0) {
      start = Math.max(start, q / p);
    } else {
      end = Math.min(end, q / p);
    }
  }
  return start <= end;
}

// The movable items a box dragged across the slide touches, by index. One that holds the whole box is passed over,
// since the box was drawn inside it, as over a full-bleed picture.
function boxPicks(items, box) {
  const [left, top, width, height] = box;
  const picked = [];
  items.forEach((item, index) => {
    if (!movable(item)) {
      return;
    }
    if (item.from) {
      if (lineCrosses(item.from, item.to, box)) {
        picked.push(index);
      }
      return;
    }
    const [x, y, w, h] = item.frame;
    const touches = x <= left + width && x + w >= left && y <= top + height && y + h >= top;
    const holds = x <= left && y <= top && x + w >= left + width && y + h >= top + height;
    if (touches && !holds) {
      picked.push(index);
    }
  });
  return picked;
}

// Items moved together, snapped by the box around them all, each shifted as that box is.
function moveGroup(items, dx, dy, targets, threshold) {
  const box = unionBox(items.map(itemBox));
  const moved = moveBox(box, dx, dy, targets, threshold);
  const shiftX = moved.box[0] - box[0];
  const shiftY = moved.box[1] - box[1];
  const shift = ([x, y]) => [x + shiftX, y + shiftY];
  const changes = items.map((item) => (item.from
    ? { from: shift(item.from), to: shift(item.to) }
    : { frame: [...shift(item.frame), item.frame[2], item.frame[3]] }));
  return { changes, lines: moved.lines };
}

// A text box drawn from where the press began to the pointer, each corner snapping as a line's end does.
function drawBox(start, dx, dy, targets, threshold) {
  const from = movePoint(start, 0, 0, targets, threshold);
  const to = movePoint(start, dx, dy, targets, threshold);
  const [[x1, y1], [x2, y2]] = [from.point, to.point];
  return {
    box: [Math.min(x1, x2), Math.min(y1, y2), Math.abs(x2 - x1), Math.abs(y2 - y1)],
    lines: [...from.lines, ...to.lines]
  };
}

// The first of text, text2, text3, and so on that no item on the slide is called.
function freshId(items) {
  const taken = new Set(items.map((item) => item.id));
  const name = (number) => (number === 1 ? 'text' : `text${number}`);
  let number = 1;
  while (taken.has(name(number))) {
    number++;
  }
  return name(number);
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
  // The text box tool is on, so a drag on the slide draws a box.
  drawing: false,
  // The box last drawn, gone again if its words are left empty, with the undo that adding it pushed.
  added: null,
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
// slide doesn't own. A group has no handles, only a box around it all, and a box drawn across the slide shows while
// it picks one.
function drawSelection() {
  const stage = slideStage();
  stage?.querySelector(':scope > .selection')?.remove();
  if (stage && editing.gesture?.marquee?.box) {
    place(element('div', 'marquee', element('div', 'selection', stage)), editing.gesture.marquee.box);
    return;
  }
  if (stage && editing.gesture?.draw?.box) {
    place(element('div', 'drawn', element('div', 'selection', stage)), editing.gesture.draw.box);
    return;
  }
  const indexes = selectedIndexes();
  if (stage && indexes.length > 1) {
    const boxes = indexes.map(itemNode).filter(Boolean)
      .map((node) => [node.offsetLeft, node.offsetTop, node.offsetWidth, node.offsetHeight]);
    place(element('div', 'group-box', element('div', 'selection', stage)), unionBox(boxes));
    return;
  }
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
function redrawItem(index, node, changed) {
  const stage = slideStage();
  const fresh = renderItem(changed, stage);
  fresh.dataset.index = index;
  fresh.classList.add('selected');
  node.replaceWith(fresh);
  fitText(stage);
  return fresh;
}

function redraw(gesture, changed) {
  gesture.node = redrawItem(gesture.index, gesture.node, changed);
}

// Items moving together shown where they would land: a box by its place alone, a line drawn again.
function showMoves(move) {
  move.items.forEach((item, at) => {
    const change = move.changes[at];
    if (item.from) {
      move.nodes[at] = redrawItem(move.indexes[at], move.nodes[at], { ...item, ...change });
    } else {
      move.nodes[at].style.left = `${Math.round(change.frame[0])}px`;
      move.nodes[at].style.top = `${Math.round(change.frame[1])}px`;
    }
  });
}

// Why an item stays put under the mouse.
function unmoved(item) {
  return item.edit.locked || `${item.id} can't be moved with the mouse`;
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

// Edits to the slide's file, and those naming one to the theme's or a layout's, undone from whichever slide made
// them.
function send(slide, edits, kind = 'edit') {
  const layout = state.layout;
  return post('api/edit', { slide, edits },
    (undo) => (undo?.length > 0 ? { slide, layout, edits: undo } : null), kind);
}

// An item's edits, each naming the theme's or layout's file where the item is written there.
function ownEdits(item, edits) {
  return item.edit.file ? edits.map((edit) => ({ ...edit, file: item.edit.file })) : edits;
}

function sendEdits(item, edits) {
  if (edits.length > 0) {
    send(currentSlide().id, ownEdits(item, edits));
  }
}

// What items moved together write, or nothing where none moved.
function sendMoves(slide, move) {
  const edits = move.items.flatMap((item, at) => ownEdits(item, valueEdits(item, move.changes[at])));
  if (edits.length > 0) {
    send(slide, edits);
  } else {
    afterEditing();
  }
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
    } else if (undo && kind !== 'discard') {
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

// A press on an item starts dragging it, or the whole group where it is one of a group's; on the slide's empty space
// or the pasteboard around it, a box that picks what it touches, adding to the selection with Shift or Ctrl.
function startGesture(event) {
  flushNudge();
  if (event.button !== 0 || editing.busy || editing.text || state.view !== 'slide' || reviewing()) {
    return;
  }
  const handle = event.target.closest('.handle');
  const node = handle ? selectedNode() : event.target.closest('#slide-frame .item');
  const slide = currentSlide();
  const stage = slideStage();
  if (editing.drawing) {
    if (stage && !event.target.closest('button, a, input, textarea, .format-bar')) {
      editing.gesture = {
        slide: slide.id,
        pointer: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        started: false,
        draw: { box: null, targets: snapTargets(slide, state.deck.size, []) }
      };
    }
    return;
  }
  if (!node) {
    const view = event.currentTarget;
    const scrollbar = event.target === view &&
      (event.offsetX >= view.clientWidth || event.offsetY >= view.clientHeight);
    if (stage && !scrollbar && !event.target.closest('button, a, input, textarea, .format-bar')) {
      const added = event.shiftKey || event.ctrlKey || event.metaKey;
      editing.gesture = {
        slide: slide.id,
        pointer: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        started: false,
        marquee: { kept: added ? selectedIndexes() : [], picked: null, box: null }
      };
    }
    return;
  }
  const index = Number(node.dataset.index);
  const indexes = selectedIndexes();
  const group = !handle && indexes.length > 1 && indexes.includes(index) ? indexes : null;
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
    targets: snapTargets(slide, state.deck.size, group || [index]),
    change: null,
    group: group && { indexes: group, items: group.map((at) => slide.items[at]), nodes: group.map(itemNode) }
  };
}

function moveGesture(event) {
  const gesture = editing.gesture;
  if (!gesture || event.pointerId !== gesture.pointer) {
    return;
  }
  if (gesture.draw) {
    moveDraw(gesture, event);
    return;
  }
  const screenX = event.clientX - gesture.startX;
  const screenY = event.clientY - gesture.startY;
  if (!gesture.started) {
    if (Math.hypot(screenX, screenY) < DRAG_PIXELS) {
      return;
    }
    const stuck = gesture.marquee ? null : (gesture.group?.items || [gesture.item]).find((item) => !movable(item));
    if (stuck) {
      notice(unmoved(stuck));
      editing.gesture = null;
      return;
    }
    gesture.started = true;
    document.getElementById('slide-frame').setPointerCapture(event.pointerId);
    if (gesture.marquee) {
      moveMarquee(gesture, event);
      return;
    }
    if (!gesture.handle) {
      document.body.classList.add('moving');
    }
    if (!gesture.group && (!state.selected || state.selected.index !== gesture.index)) {
      const { item } = gesture;
      state.selected = { slide: currentSlide().id, index: gesture.index, id: item.id, owner: item.owner };
      state.group = null;
      state.detail = null;
      markSelection();
      renderInspector();
      loadDetail();
    }
  }
  if (gesture.marquee) {
    moveMarquee(gesture, event);
    return;
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
  if (gesture.group) {
    const moved = moveGroup(gesture.group.items, dx, dy, targets, threshold);
    gesture.group.changes = moved.changes;
    lines = moved.lines;
    showMoves(gesture.group);
  } else if (item.from) {
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

// The box from where the press began to the pointer, in the slide's points, outlining what it picks as it goes.
function moveMarquee(gesture, event) {
  const stage = slideStage();
  const scale = stageScale(stage);
  const origin = stage.getBoundingClientRect();
  const x = [gesture.startX, event.clientX].map((value) => (value - origin.left) / scale);
  const y = [gesture.startY, event.clientY].map((value) => (value - origin.top) / scale);
  const marquee = gesture.marquee;
  marquee.box = [Math.min(...x), Math.min(...y), Math.abs(x[1] - x[0]), Math.abs(y[1] - y[0])];
  marquee.picked = [...new Set([...marquee.kept, ...boxPicks(currentSlide().items || [], marquee.box)])];
  markSelection(marquee.picked);
}

function endGesture(event) {
  const gesture = editing.gesture;
  if (!gesture || event.pointerId !== gesture.pointer) {
    return;
  }
  editing.gesture = null;
  if (gesture.draw) {
    endDraw(gesture);
    return;
  }
  if (!gesture.started) {
    return;
  }
  editing.suppressClick = true;
  if (gesture.marquee) {
    selectGroup(gesture.marquee.picked);
    afterEditing();
    return;
  }
  document.body.classList.remove('moving');
  drawSnaps(slideStage(), []);
  if (gesture.group) {
    sendMoves(gesture.slide, gesture.group);
    return;
  }
  const edits = gesture.change ? valueEdits(gesture.item, gesture.change) : [];
  if (edits.length > 0) {
    send(gesture.slide, ownEdits(gesture.item, edits));
  } else {
    afterEditing();
  }
}

// Arrow keys nudge the selected item or group a point, or ten with Shift, and write once the keys rest. Returns
// whether the key was a nudge.
function nudgeKey(event) {
  const steps = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
  const indexes = selectedIndexes();
  if (!(event.key in steps) || indexes.length === 0 || state.presenting) {
    return false;
  }
  if (editing.busy) {
    return true;
  }
  const slide = currentSlide();
  const items = indexes.map((index) => slide.items[index]);
  const stuck = items.find((item) => !movable(item));
  if (stuck) {
    notice(unmoved(stuck));
    return true;
  }
  const key = `${slide.id} ${indexes.join(' ')}`;
  if (editing.nudge && editing.nudge.key !== key) {
    flushNudge();
    return true;
  }
  const size = event.shiftKey ? 10 : 1;
  const nudge = editing.nudge ||
    { key, slide: slide.id, indexes, items, nodes: indexes.map(itemNode), dx: 0, dy: 0 };
  editing.nudge = nudge;
  nudge.dx += steps[event.key][0] * size;
  nudge.dy += steps[event.key][1] * size;
  nudge.changes = moveGroup(items, nudge.dx, nudge.dy, null, 0).changes;
  showMoves(nudge);
  drawSelection();
  clearTimeout(nudge.timer);
  nudge.timer = setTimeout(flushNudge, NUDGE_DELAY);
  return true;
}

// Writes a nudge still waiting for the keys to rest. Whatever changes the selection calls this first, so the next
// arrow key starts a nudge of its own rather than adding to the last one's.
function flushNudge() {
  const nudge = editing.nudge;
  if (!nudge) {
    return;
  }
  clearTimeout(nudge.timer);
  editing.nudge = null;
  sendMoves(nudge.slide, nudge);
}

// Delete or Backspace removes the selected elements from the slide's file, leaving those the layout or theme gives,
// or on a layout's stage from the layout's file. Returns whether the key was one of them.
function deleteKey(event) {
  const indexes = selectedIndexes();
  if (!(event.key === 'Delete' || event.key === 'Backspace') || indexes.length === 0 || state.presenting) {
    return false;
  }
  if (editingActive()) {
    return true;
  }
  const slide = currentSlide();
  const items = indexes.map((index) => slide.items[index]);
  const kept = items.filter((item) => !item.edit.delete);
  if (kept.length > 0 && state.layout) {
    notice(`${kept.map(elementName).join(', ')} ${kept.length > 1 ? 'stay' : 'stays'}: only elements are deleted ` +
      'here; remove the rest in the file');
  } else if (kept.length > 0) {
    notice(kept.length > 1
      ? `${kept.map(elementName).join(', ')} stay: the layout or theme gives them`
      : kept[0].edit.locked || `${kept[0].id} is a placeholder the layout gives; empty it in the file`);
  }
  const deleted = items.filter((item) => item.edit.delete);
  if (deleted.length === 0) {
    return true;
  }
  selectItem(null);
  send(slide.id, deleted.flatMap((item) =>
    ownEdits(item, [{ path: item.edit.delete.path, source: item.edit.delete.source, delete: true }])));
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
    if (entry.layout && entry.layout !== state.layout) {
      editLayout(entry.layout);
    } else if (!entry.layout && (state.layout || entry.slide !== state.current)) {
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

// #region Drawing text boxes

function toggleDrawing(on = !editing.drawing) {
  editing.drawing = on && state.view === 'slide' && !state.layout && !reviewing();
  document.body.classList.toggle('drawing', editing.drawing);
  renderToolbar();
}

function moveDraw(gesture, event) {
  const stage = slideStage();
  if (!gesture.started) {
    if (Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY) < DRAG_PIXELS) {
      return;
    }
    gesture.started = true;
    document.getElementById('slide-frame').setPointerCapture(event.pointerId);
  }
  const scale = stageScale(stage);
  const origin = stage.getBoundingClientRect();
  const start = [(gesture.startX - origin.left) / scale, (gesture.startY - origin.top) / scale];
  const drawn = drawBox(start, (event.clientX - gesture.startX) / scale, (event.clientY - gesture.startY) / scale,
    event.altKey ? null : gesture.draw.targets, SNAP_PIXELS / scale);
  gesture.draw.box = drawn.box;
  drawSnaps(stage, drawn.lines);
  drawSelection();
}

function endDraw(gesture) {
  const box = gesture.draw.box;
  drawSnaps(slideStage(), []);
  drawSelection();
  if (!gesture.started || box[2] < 1 || box[3] < 1) {
    notice('Drag across the slide to draw the text box');
    afterEditing();
    return;
  }
  editing.suppressClick = true;
  toggleDrawing(false);
  addTextBox(gesture.slide, box);
}

// The box is written empty, in the theme's text style, and its words are then typed on the slide.
async function addTextBox(slide, frame) {
  const id = freshId(currentSlide().items || []);
  await send(slide, [{ path: `elements.${id}`, numbers: hundredths(frame), add: true }]);
  const index = currentSlide().id === slide
    ? currentSlide().items.findIndex((item) => item.id === id && item.owner === 'slide')
    : -1;
  if (index < 0) {
    return;
  }
  selectItem({ slide, index, id, owner: 'slide' });
  editing.added = { slide, id, entry: editing.undo[editing.undo.length - 1] };
  beginText(itemNode(index), (flow) => getSelection().collapse(flow.querySelector('.paragraph') || flow, 0));
}

// A box left without words goes again, taking the edits made to it off the undo list, as if never drawn.
function discardAdded(added, item) {
  const at = editing.undo.indexOf(added.entry);
  if (at >= 0) {
    editing.undo.splice(at);
  }
  selectItem(null);
  send(added.slide, [{ path: item.edit.delete.path, source: item.edit.delete.source, delete: true }], 'discard');
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
    } else if (child.nodeName === 'BR') {
      text += '\n';
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
  const fields = [];
  for (const fixed of flow.querySelectorAll('.bullet, [data-field]')) {
    if (fixed.dataset.fieldEdit && fixed.children.length === 0) {
      fields.push({ span: fixed, edit: JSON.parse(fixed.dataset.fieldEdit), before: fieldText(fixed) });
      if (!fixed.firstChild) {
        fixed.append('\u200B');
      }
    } else {
      fixed.contentEditable = 'false';
    }
  }
  // Firefox puts no caret beside a field without text there to hold it.
  for (const field of flow.querySelectorAll('[data-field]')) {
    field.before('\u200B');
    field.after('\u200B');
  }
  editing.text = { slide: currentSlide().id, node, item, flow, fields, before: paragraphTexts(flow) };
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
  const locked = caretField('false');
  if (mark) {
    event.preventDefault();
    formatText(mark);
  } else if (locked && event.key.length === 1 && !event.ctrlKey && !event.metaKey) {
    event.preventDefault();
    notice(lockedField(locked));
  } else if (event.key === 'Enter' && !event.isComposing) {
    event.preventDefault();
    // A field's value is one line in presentation.yaml.
    if (caretField('inherit')) {
      return;
    }
    // The title and placeholders are a paragraph each, so Enter breaks the line there.
    if (editing.text.item.role) {
      breakLine();
    } else {
      splitParagraph();
    }
  } else if ((event.key === 'Backspace' || event.key === 'Delete') && joinParagraphs(event.key === 'Delete')) {
    event.preventDefault();
  } else if (event.key === 'Escape') {
    event.preventDefault();
    finishText(true);
  }
}

function breakLine() {
  const selection = getSelection();
  const range = selection.getRangeAt(0);
  range.deleteContents();
  dropSpaces(range);
  // The caret has no place after a break that ends the text without something there to hold it.
  const holder = document.createTextNode('\u200B');
  range.insertNode(holder);
  range.insertNode(document.createElement('br'));
  selection.collapse(holder, 1);
}

// The spaces where a line breaks would only trail one line or lead the next.
function dropSpaces(range) {
  const node = range.startContainer;
  if (node.nodeType !== Node.TEXT_NODE) {
    return;
  }
  let start = range.startOffset;
  let end = start;
  while (start > 0 && node.data[start - 1] === ' ') {
    start--;
  }
  while (end < node.data.length && node.data[end] === ' ') {
    end++;
  }
  node.deleteData(start, end - start);
  range.setStart(node, start);
  range.collapse(true);
}

function caretParagraph(range) {
  const node = range.startContainer;
  const paragraph = (node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement).closest('.paragraph');
  return editing.text.flow.contains(paragraph) ? paragraph : null;
}

// The paragraph splits at the caret, its second half taking its bullet until the file redraws it.
function splitParagraph() {
  const selection = getSelection();
  const range = selection.getRangeAt(0);
  const paragraph = caretParagraph(range);
  if (!paragraph) {
    return;
  }
  range.deleteContents();
  dropSpaces(range);
  const bullet = paragraph.querySelector(':scope > .bullet');
  // A caret before the bullet, as Home leaves it, splits after it, so the bullet stays whole.
  if (bullet && range.comparePoint(paragraph, [...paragraph.childNodes].indexOf(bullet) + 1) > 0) {
    range.setStartAfter(bullet);
    range.collapse(true);
  }
  const tail = document.createRange();
  tail.setStart(range.startContainer, range.startOffset);
  tail.setEnd(paragraph, paragraph.childNodes.length);
  const moved = tail.extractContents();
  const next = paragraph.cloneNode(false);
  if (bullet) {
    next.append(bullet.cloneNode(true));
  }
  next.append(moved);
  // A half left empty keeps none of the runs split from the other, such as a bold one, since the file writes what is
  // typed there in the paragraph's own style, and it would show it in theirs until then.
  for (const half of [paragraph, next]) {
    if (paragraphText(half).length === 0) {
      half.replaceChildren(...[...half.children].filter((child) => child.classList.contains('bullet')), '\u200B');
    }
  }
  paragraph.after(next);
  selection.collapse(next, bullet ? 1 : 0);
}

// Backspace at a paragraph's start, or Delete at its end, joins it to its neighbour; true when it did.
function joinParagraphs(forward) {
  const selection = getSelection();
  const range = selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
  const paragraph = range?.collapsed && caretParagraph(range);
  if (!paragraph || editing.text.item.role) {
    return false;
  }
  const at = textOffset(paragraph, range.startContainer, range.startOffset);
  const [first, second] = forward ? [paragraph, paragraph.nextElementSibling]
    : [paragraph.previousElementSibling, paragraph];
  if (at !== (forward ? paragraphText(paragraph).length : 0) || !second?.classList.contains('paragraph') ||
    !first?.classList.contains('paragraph')) {
    return false;
  }
  const join = document.createRange();
  join.selectNodeContents(first);
  join.collapse(false);
  for (const child of [...second.childNodes]) {
    if (!child.classList?.contains('bullet')) {
      first.append(child);
    }
  }
  second.remove();
  selection.removeAllRanges();
  selection.addRange(join);
  return true;
}

// Editing on the slide rewords paragraphs; adding one is an edit to the file.
function textInput(event) {
  if (event.inputType === 'insertParagraph' || event.inputType === 'insertLineBreak') {
    event.preventDefault();
    return;
  }
  const typing = event.inputType === 'insertText' || event.inputType === 'insertReplacementText';
  const target = event.getTargetRanges()[0];
  if (event.isComposing || !target || !(typing || event.inputType.startsWith('delete'))) {
    return;
  }
  const range = document.createRange();
  range.setStart(target.startContainer, target.startOffset);
  range.setEnd(target.endContainer, target.endOffset);
  const data = typing ? event.data ?? event.dataTransfer?.getData('text/plain') ?? '' : '';
  if (fieldInput(range, data)) {
    event.preventDefault();
  }
}

function textPaste(event) {
  event.preventDefault();
  const text = event.clipboardData.getData('text/plain').replace(/\s*\n\s*/g, ' ');
  const selection = getSelection();
  if (selection.rangeCount === 0 || !fieldInput(selection.getRangeAt(0), text)) {
    document.execCommand('insertText', false, text);
  }
}

function fieldText(span) {
  return span.textContent.replace(/\u200B/g, '');
}

// Typing over a field's text rewrites the field's value, so text typed in a field, or over the whole of one, is
// written into it here: the browser would drop a field emptied of its text, and the field with it. True when it was.
function fieldInput(range, data) {
  for (const { span } of editing.text.fields) {
    if (!span.isConnected || !range.intersectsNode(span)) {
      continue;
    }
    const start = fieldOffset(span, range.startContainer, range.startOffset, true);
    const end = fieldOffset(span, range.endContainer, range.endOffset, false);
    const text = fieldText(span);
    if (start === null || end === null) {
      return false;
    }
    // Deleting nothing is moving past the characters that hold the caret, except in an emptied field, which stays.
    if (start === end && data === '') {
      return text === '';
    }
    const typed = text.slice(0, start) + data + text.slice(end);
    // An emptied field keeps a character to hold the caret, so what is typed next goes into it.
    span.textContent = typed || '\u200B';
    getSelection().collapse(span.firstChild, typed ? start + data.length : 1);
    return true;
  }
  return false;
}

// Characters of a field's text before a range's edge, or null where the range takes in text outside the field; an
// edge outside it, past only the characters that hold the caret, is the field's start or end.
function fieldOffset(span, container, offset, start) {
  const length = fieldText(span).length;
  if (span.contains(container)) {
    return container.nodeType === Node.TEXT_NODE ? container.data.slice(0, offset).replace(/\u200B/g, '').length
      : offset === 0 ? 0 : length;
  }
  const between = document.createRange();
  if (start) {
    between.setStart(container, offset);
    between.setEndBefore(span);
  } else {
    between.setStartAfter(span);
    between.setEnd(container, offset);
  }
  return between.toString().replace(/\u200B/g, '') === '' ? (start ? 0 : length) : null;
}

// The field the caret is in, among those taking no typing ('false') or those that do ('inherit').
function caretField(editable) {
  const node = getSelection().focusNode;
  const field = (node?.nodeType === Node.ELEMENT_NODE ? node : node?.parentElement)?.closest('[data-field]');
  return field && editing.text.flow.contains(field) && field.contentEditable === editable ? field : null;
}

function lockedField(span) {
  const name = `\${${span.dataset.field}}`;
  return span.dataset.field === 'slide' ? `${name} is the slide's number`
    : span.dataset.trace ? `${name} reads from a book; change it there`
    : `${name} can't be typed over on the slide`;
}

// The values of the fields typed over, each written where presentation.yaml defines it, once however often it shows.
function fieldEdits(editor) {
  const edits = new Map();
  for (const { span, edit, before } of editor.fields) {
    const value = span.isConnected ? fieldText(span).trim() : before;
    if (value === before.trim()) {
      continue;
    }
    if (value === '') {
      notice(`\${${span.dataset.field}} can't be empty, so it keeps its value`);
      continue;
    }
    edits.set(edit.path, { file: edit.file, path: edit.path, source: edit.source, value });
  }
  return [...edits.values()];
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
  const { added } = editing;
  editing.added = null;
  if (added?.slide === editor.slide && added.id === editor.item.id && editor.item.owner === 'slide' &&
    after.every((text) => text.trim() === '')) {
    discardAdded(added, editor.item);
    return;
  }
  const changed = after.length !== editor.before.length || after.some((text, index) => text !== editor.before[index]);
  const edits = commit ? fieldEdits(editor) : [];
  if (commit && changed) {
    edits.unshift({ path: editor.item.edit.text.path, source: editor.item.edit.text.source, paragraphs: after });
  }
  if (edits.length > 0) {
    send(editor.slide, edits);
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

// The point in a paragraph after that many of its characters, a field or a line break counting as one.
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
        if (child.dataset.field !== undefined || child.nodeName === 'BR') {
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

function installEditing(frame, view) {
  view.addEventListener('pointerdown', startGesture);
  view.addEventListener('pointermove', moveGesture);
  view.addEventListener('pointerup', endGesture);
  view.addEventListener('pointercancel', endGesture);
  frame.addEventListener('dblclick', startText);
  document.addEventListener('selectionchange', markFormatBar);
  // Firefox drags a picture as an image to drop elsewhere, which cancels the pointer's events.
  frame.addEventListener('dragstart', (event) => event.preventDefault());
}

if (typeof module !== 'undefined') {
  module.exports = { snapTargets, moveBox, resizeBox, movePoint, moveLine, moveGroup, boxPicks, valueEdits, drawBox,
    freshId };
}