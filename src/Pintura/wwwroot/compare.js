'use strict';

// Comparing the working tree against a git revision: the last save, or the version before it when nothing is unsaved,
// unless the page's address pins another with ?base=. The server sends the revision as drawn and what changed since;
// B flips the slide to the revision in place, and C
// overlays the changes: added elements outlined in green, removed ones in red where the revision shows, changed ones
// in amber, reworded ones marked in the words that changed, charts in the parts that draw what changed, and tables in
// the cells whose look changed. Slides the revision had and the working tree doesn't are listed under the sidebar. Nothing is editable while the revision shows,
// but the inspector reverts a change: an element, the title, a placeholder, or a whole slide written back as the
// revision has it.

// #region Marks

// Words and the whitespace between them, from two texts, as runs kept, removed, and added. A space between two changes
// joins them, so a phrase reworded word by word reads as one change rather than alternating fragments.
function textDiff(before, after) {
  const first = before.match(/\s+|\S+/g) || [];
  const second = after.match(/\s+|\S+/g) || [];
  const lengths = Array.from({ length: first.length + 1 }, () => new Array(second.length + 1).fill(0));
  for (let i = first.length - 1; i >= 0; i--) {
    for (let j = second.length - 1; j >= 0; j--) {
      lengths[i][j] = first[i] === second[j]
        ? lengths[i + 1][j + 1] + 1
        : Math.max(lengths[i + 1][j], lengths[i][j + 1]);
    }
  }
  const tokens = [];
  let i = 0;
  let j = 0;
  while (i < first.length || j < second.length) {
    if (i < first.length && j < second.length && first[i] === second[j]) {
      tokens.push({ op: 'same', text: first[i++] });
      j++;
    } else if (j === second.length || (i < first.length && lengths[i + 1][j] >= lengths[i][j + 1])) {
      tokens.push({ op: 'removed', text: first[i++] });
    } else {
      tokens.push({ op: 'added', text: second[j++] });
    }
  }
  const blocks = [];
  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index];
    const last = blocks[blocks.length - 1];
    const joins = token.op === 'same' && /^\s+$/.test(token.text) && last && last.op !== 'same' &&
      tokens[index + 1] && tokens[index + 1].op !== 'same';
    if (token.op === 'same' && !joins) {
      if (last && last.op === 'same') {
        last.text += token.text;
      } else {
        blocks.push({ op: 'same', text: token.text });
      }
      continue;
    }
    let block = last;
    if (!block || block.op !== 'change') {
      block = { op: 'change', removed: '', added: '' };
      blocks.push(block);
    }
    if (token.op !== 'added') {
      block.removed += token.text;
    }
    if (token.op !== 'removed') {
      block.added += token.text;
    }
  }
  return blocks.flatMap((block) => block.op === 'same' ? [block] : [
    ...(block.removed ? [{ op: 'removed', text: block.removed }] : []),
    ...(block.added ? [{ op: 'added', text: block.added }] : [])
  ]);
}

// The words only the side showing has, as offsets into its text, and a caret where the other side has words it lacks.
function textMarks(before, after, showingBefore) {
  const shown = showingBefore ? before : after;
  const own = showingBefore ? 'removed' : 'added';
  const marks = [];
  let offset = 0;
  const parts = textDiff(before, after);
  for (let index = 0; index < parts.length; index++) {
    const part = parts[index];
    if (part.op === 'same') {
      offset += part.text.length;
      continue;
    }
    const next = parts[index + 1];
    const paired = next && next.op !== 'same' && next.op !== part.op ? next : null;
    const mine = part.op === own ? part : paired;
    const theirs = part.op === own ? paired : part;
    if (paired) {
      index++;
    }
    if (mine && /\S/.test(mine.text)) {
      const start = offset + mine.text.length - mine.text.trimStart().length;
      marks.push({ start, end: start + mine.text.trim().length });
    } else if (!mine && /\S/.test(theirs.text)) {
      let start = offset;
      while (start > 0 && /[ \t]/.test(shown[start - 1])) {
        start--;
      }
      marks.push({ start, end: start });
    }
    offset += mine ? mine.text.length : 0;
  }
  return marks;
}

function comparedText(text) {
  return text.paragraphs.map((paragraph) => paragraph.runs.map((run) => run.text).join('')).join('\n');
}

// Each character's look, in comparedText's order, a paragraph's newline looking like nothing.
function characterLooks(text) {
  return text.paragraphs.flatMap((paragraph, index) => [...(index > 0 ? [''] : []),
    ...paragraph.runs.flatMap((run) => {
      const look = JSON.stringify([run.bold, run.italic, run.underline, run.font, run.size, run.color]);
      return [...run.text].map(() => look);
    })]);
}

// The stretches of the side showing whose words both sides have but draw differently, such as a word made bold.
function formatMarks(earlier, later, showingBefore) {
  const before = comparedText(earlier);
  const after = comparedText(later);
  const looksBefore = characterLooks(earlier);
  const looksAfter = characterLooks(later);
  const shown = showingBefore ? before : after;
  const changed = [];
  let b = 0;
  let a = 0;
  for (const part of before === after ? [{ op: 'same', text: before }] : textDiff(before, after)) {
    if (part.op === 'same') {
      for (let k = 0; k < part.text.length; k++) {
        if (looksBefore[b + k] !== looksAfter[a + k]) {
          changed.push(showingBefore ? b + k : a + k);
        }
      }
    }
    b += part.op === 'added' ? 0 : part.text.length;
    a += part.op === 'removed' ? 0 : part.text.length;
  }
  const marks = [];
  for (const at of changed) {
    const last = marks[marks.length - 1];
    if (last && last.end === at) {
      last.end++;
    } else {
      marks.push({ start: at, end: at + 1, restyled: true });
    }
  }
  return marks.map((mark) => {
    let { start, end } = mark;
    while (start < end && /\s/.test(shown[start])) {
      start++;
    }
    while (end > start && /\s/.test(shown[end - 1])) {
      end--;
    }
    return { start, end, restyled: true };
  }).filter((mark) => mark.end > mark.start);
}

// The marks for an item's reworded or restyled text on the side showing: one entry for a text or shape, one per
// changed cell for a table.
function textChanges(earlier, later, showingBefore) {
  const regions = [];
  const add = (cell, older, newer) => {
    const before = comparedText(older);
    const after = comparedText(newer);
    const marks = [...(before === after ? [] : textMarks(before, after, showingBefore)),
      ...formatMarks(older, newer, showingBefore)].sort((one, other) => one.start - other.start);
    if (marks.length > 0) {
      regions.push({ cell, marks });
    }
  };
  if (earlier.text && later.text) {
    add(null, earlier.text, later.text);
  }
  if (earlier.table && later.table) {
    const cells = new Map(earlier.table.cells.map((cell) => [`${cell.row},${cell.column}`, cell]));
    for (const cell of later.table.cells) {
      const previous = cells.get(`${cell.row},${cell.column}`);
      if (previous) {
        add({ row: cell.row, column: cell.column }, previous.text, cell.text);
      }
    }
  }
  return regions;
}

function textChange(entry) {
  return entry.key === 'text' || /^cell \d+,\d+$/.test(entry.key);
}

// Reworded text, or a run's look, which the overlay marks in the words themselves.
function markedInWords(entry) {
  return textChange(entry) ||
    /^(cell \d+,\d+ )?paragraph \d+ run \d+\.(bold|italic|underline|font|size|color)$/.test(entry.key);
}

const MARKS = ['bar', 'marker', 'slice'];
// Parts the overlay traces in their own shape rather than boxing.
const SHAPED = ['slice', 'line'];

// A list's entries, as the diff shows them, that differ from the other's.
function changedEntries({ before, after }) {
  const was = (before || '').split(', ');
  const now = (after || '').split(', ');
  return Array.from({ length: Math.max(was.length, now.length) }, (_, index) => index)
    .filter((index) => was[index] !== now[index]);
}

// The parts of a chart that draw its changes, each as the parts it names and the series and point they draw for, or
// null where a change is drawn across the whole chart, such as its type, its frame, or its text.
function chartParts(changes, chart) {
  const pie = chart.family === 'pie';
  const seriesAt = (name) => chart.series.findIndex((series) => series.name === name);
  const pointAt = (category) => chart.categories.indexOf(category);
  const parts = [];
  for (const change of changes) {
    const key = change.key;
    const point = /^series\.(.+?)\.points\.(.+?)\.(data_labels|markers|color)(\.|$)/.exec(key);
    const series = /^series\.(.+?)\.(data_labels|markers|type|axis)(\.|$)/.exec(key);
    const values = /^series (.+)$/.exec(key);
    if (key === 'title' || key === 'legend') {
      parts.push({ parts: [key] });
    } else if (/^data_labels\./.test(key)) {
      parts.push({ parts: ['label'] });
    } else if (/^markers\./.test(key)) {
      parts.push({ parts: ['marker', 'legend'] });
    } else if (key === 'value_axis.format' || key === 'value_axis.visible') {
      parts.push({ parts: ['value_axis'] });
    } else if (key === 'value_axis.gridlines') {
      parts.push({ parts: ['gridline'] });
    } else if (/^value_axis\./.test(key)) {
      parts.push({ parts: ['value_axis', 'gridline'] });
    } else if (/^(secondary_axis|category_axis)\./.test(key)) {
      parts.push({ parts: [key.split('.')[0]] });
    } else if (key === 'categories') {
      parts.push({ parts: ['category_axis'] });
    } else if (point) {
      const named = { data_labels: ['label'], markers: ['marker'], color: MARKS }[point[3]];
      parts.push({ parts: named, series: seriesAt(point[1]), point: pointAt(point[2]) });
    } else if (series) {
      const named = { data_labels: ['label'], markers: ['marker', 'legend'] }[series[2]] ||
        [...MARKS, 'line', 'label', 'legend'];
      parts.push({ parts: named, series: seriesAt(series[1]) });
    } else if (values) {
      const index = seriesAt(values[1]);
      for (const point of changedEntries(change).filter((point) => point < chart.categories.length)) {
        parts.push({ parts: [...MARKS, 'label'], series: index, point });
      }
      if (!pie) {
        parts.push({ parts: ['line'], series: index });
      }
    } else if (key === 'series_colors') {
      for (const index of changedEntries(change)) {
        if (pie && index < chart.categories.length) {
          parts.push({ parts: ['slice', 'legend'], point: index });
        } else if (!pie && index < chart.series.length) {
          parts.push({ parts: ['bar', 'marker', 'line', 'legend'], series: index });
        }
      }
    } else {
      return null;
    }
  }
  return parts;
}

// The cells of a table whose look changed, which the overlay boxes, or null where a change spans the table, such as its
// frame or style. Reworded cells are marked in their words instead.
function changedCells(changes) {
  const cells = new Map();
  for (const change of changes) {
    const cell = /^cell (\d+),(\d+)([. ]|$)/.exec(change.key);
    if (!cell) {
      return null;
    }
    if (!markedInWords(change)) {
      cells.set(`${cell[1]},${cell[2]}`, { row: Number(cell[1]), column: Number(cell[2]) });
    }
  }
  return [...cells.values()];
}

// What the overlay marks on the side showing, by drawing index: each changed item it draws, outlined unless only its
// words changed, which are marked instead, or it is a chart or table whose changes its parts or cells draw.
function changeMarks(diff, earlier, later, showingBefore) {
  const slide = showingBefore ? earlier : later;
  const items = slide ? slide.items || [] : [];
  const marks = [];
  for (const change of diff.items) {
    const index = items.findIndex((item) => item.id === change.id && item.owner === change.owner);
    if (index < 0) {
      continue;
    }
    const changed = change.status === 'changed';
    const chart = items[index].chart;
    const table = items[index].table;
    const parts = changed && chart ? chartParts(change.changes, chart) : null;
    const cells = changed && table ? changedCells(change.changes) : null;
    marks.push({
      id: change.id,
      owner: change.owner,
      status: change.status,
      index,
      outline: !changed || (!parts && !cells && !change.changes.every(markedInWords)),
      restyled: changed && change.changes.every((entry) => entry.cause),
      ...(chart ? { parts } : {}),
      ...(table ? { cells } : {})
    });
  }
  return marks;
}

// #endregion

// #region State

// The revision the address pins, or null to follow the latest changes: the unsaved ones, or the last save's.
function compareBase() {
  return new URLSearchParams(location.search).get('base');
}

// The later version the address pins the comparison to, drawn in the working tree's place; null for the working tree.
function compareHead() {
  return new URLSearchParams(location.search).get('head');
}

async function fetchCompare() {
  try {
    const query = new URLSearchParams();
    for (const [name, value] of [['base', compareBase()], ['head', compareHead()]]) {
      if (value) {
        query.set(name, value);
      }
    }
    const response = await fetch(`api/compare${query.size ? `?${query}` : ''}`, { cache: 'no-store' });
    const body = await response.json();
    return response.ok ? body : { error: body.error || `comparing failed (${response.status})` };
  } catch (error) {
    return { error: String(error) };
  }
}

function comparing() {
  return Boolean(state.compare && state.compare.diff);
}

const VERSION_MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// The day a version was saved, spoken of as a reader would: today, yesterday, a weekday within the week, or the date,
// with the year only when it isn't this one.
function versionDay(date, now = new Date()) {
  const day = (moment) => new Date(moment.getFullYear(), moment.getMonth(), moment.getDate());
  const days = Math.round((day(now) - day(date)) / 86400000);
  return days === 0 ? 'Today'
    : days === 1 ? 'Yesterday'
    : days > 1 && days < 7 ? date.toLocaleDateString('en-GB', { weekday: 'long' })
    : `${VERSION_MONTHS[date.getMonth()]} ${date.getDate()}` +
      (date.getFullYear() === now.getFullYear() ? '' : `, ${date.getFullYear()}`);
}

function versionTime(date) {
  return `${date.getHours() % 12 || 12}:${String(date.getMinutes()).padStart(2, '0')} ` +
    (date.getHours() < 12 ? 'AM' : 'PM');
}

function baseName() {
  return comparing() ? `${namedBase()} (${state.compare.commit.slice(0, 7)})` : compareBase();
}

function namedBase() {
  return comparing() ? `v${state.compare.version.number}` : compareBase()?.slice(0, 7) ?? null;
}

// What the changes run to: a later version, the working tree as the last version saved it, or its unsaved changes.
function namedHead() {
  if (state.compare?.head) {
    return `v${state.compare.head.number}`;
  }
  if (state.compare?.lastSave) {
    return `v${state.compare.lastSave.number}`;
  }
  return !state.saving || state.saving.unsaved ? 'unsaved' : `v${state.saving.next - 1}`;
}

// Both ends, since a range named by its start alone leaves the reader guessing what it's compared with.
function rangeName() {
  return `${namedBase()} → ${namedHead()}`;
}

function sinceBase() {
  if (!namedBase()) {
    return '';
  }
  return state.compare?.head ? ` from ${namedBase()} to ${namedHead()}` : ` since ${namedBase()}`;
}

// Pins the comparison to a version, and to a later one in the working tree's place, or without one follows the
// latest changes, keeping the choice in the address.
function chooseBase(commit, head = null) {
  const url = new URL(location.href);
  for (const [name, value] of [['base', commit], ['head', head]]) {
    if (value) {
      url.searchParams.set(name, value);
    } else {
      url.searchParams.delete(name);
    }
  }
  history.replaceState(history.state, '', url);
}

function slideDiff(id) {
  return comparing() ? state.compare.diff.slides.find((slide) => slide.id === id) || null : null;
}

// Whether Before and Changes have something to show: the slide on screen changed, or in the grid, any slide did.
function changesToShow() {
  if (!comparing()) {
    return false;
  }
  return state.view === 'slide' ? Boolean(slideDiff(currentSlide()?.id)) : state.compare.diff.slides.length > 0;
}

// Whether the sidebar and grid show only the slides changed since the revision, which needs one to have changed.
function filteringChanges() {
  return state.changedOnly && comparing() && state.compare.diff.slides.length > 0;
}

// The next changed slide from the current one in the offset's direction, or the current one past either end; a removed
// slide, which the deck lacks, steps in from outside it.
function changedStep(ids, changed, current, offset) {
  const direction = Math.sign(offset);
  const index = ids.indexOf(current);
  const start = index !== -1 ? index : direction > 0 ? -1 : ids.length;
  for (let i = start + direction; i >= 0 && i < ids.length; i += direction) {
    if (changed.has(ids[i])) {
      return ids[i];
    }
  }
  return current;
}

function beforeSlide(id) {
  return comparing() ? state.compare.before.slides.find((slide) => slide.id === id) || null : null;
}

function removedSlide(id) {
  return slideDiff(id)?.status === 'removed' ? beforeSlide(id) : null;
}

// A revision shows, flipped to, as a slide only it has, or as the later of two versions, so the slide on screen isn't
// the one edits would write.
function reviewing(slide = currentSlide()) {
  return comparing() && (state.before || Boolean(state.compare.head) || Boolean(slide && removedSlide(slide.id)));
}

// The deck the page draws: a version stepped to in the working tree's place, under the working tree's comments, since
// those are what's being reviewed against it.
function shownDeck(deck, after) {
  return after ? { ...after, comments: deck.comments } : deck;
}

// The slide as the side showing draws it; null for a slide added since the revision.
function shownSlide(slide) {
  if (!comparing() || removedSlide(slide.id)) {
    return slide;
  }
  return state.before ? beforeSlide(slide.id) : slide;
}

// #endregion

// #region Drawing

// A badge on a sidebar thumbnail or grid cell for a slide added, changed, or moved since the revision.
function markThumbnail(node, id) {
  const status = slideDiff(id)?.status;
  if (status) {
    node.dataset.change = status;
    node.title = `${node.title || id} · ${status}${sinceBase()}`;
  }
}

function renderRemoved() {
  const container = document.getElementById('removed');
  container.replaceChildren();
  const removed = comparing() ? state.compare.diff.slides.filter((slide) => slide.status === 'removed') : [];
  if (removed.length === 0) {
    return;
  }
  element('span', null, element('div', 'section-name', container)).textContent = `Removed slides${sinceBase()}`;
  for (const entry of removed) {
    const slide = beforeSlide(entry.id);
    const button = element('button', 'thumbnail removed', container);
    button.type = 'button';
    button.dataset.slide = entry.id;
    button.dataset.change = 'removed';
    button.title = `${slide.title || entry.id} · removed${sinceBase()}`;
    element('span', 'number', button).textContent = entry.before;
    mountStage(element('div', 'preview', button), slide, state.compare.before.size,
      thumbnailWidth(state.compare.before.size));
    button.addEventListener('click', () => pickSlide(entry.id));
  }
}

// A stage for a slide the revision lacks, saying so.
function mountAbsent(frame, size, width) {
  const stage = mountStage(frame, { items: [], background: '#fff' }, size, width);
  element('div', 'absent', stage).textContent = namedBase() ? `New since ${namedBase()}` : 'New slide';
  return stage;
}

function renderChanges(slide, stage) {
  const diff = slideDiff(slide.id);
  if (!diff || diff.status === 'moved') {
    return;
  }
  const layer = element('div', 'changes', stage);
  if (diff.status !== 'changed') {
    layer.classList.add(diff.status);
    element('span', 'change-label', layer).textContent = `${diff.status}${sinceBase()}`;
    return;
  }
  const showingBefore = state.before;
  const earlier = beforeSlide(slide.id);
  const scale = stage.getBoundingClientRect().width / stage.offsetWidth;
  for (const mark of changeMarks(diff, earlier, slide, showingBefore)) {
    const classes = `change ${mark.status}${mark.outline ? ' outlined' : ''}${mark.restyled ? ' restyled' : ''}`;
    const node = element('div', classes, layer);
    const drawn = stage.querySelector(`:scope > .item[data-index="${mark.index}"]`);
    place(node, [drawn.offsetLeft, drawn.offsetTop, drawn.offsetWidth, drawn.offsetHeight]);
    node.style.transform = drawn.style.transform;
    if (mark.parts && !markParts(drawn, node, mark.parts)) {
      node.classList.add('outlined');
    }
    if (mark.cells && !markCells(drawn, node, mark.cells, scale)) {
      node.classList.add('outlined');
    }
    if (mark.status === 'changed') {
      const find = (side) => side.items.find((item) => item.id === mark.id && item.owner === mark.owner);
      markText(drawn, node, textChanges(find(earlier), find(slide), showingBefore), showingBefore, scale);
    }
  }
  if (diff.changes.length > 0) {
    element('span', 'change-label', layer).textContent =
      diff.changes.map((change) => `${change.key}: ${change.before || '(none)'} → ${change.after || '(none)'}`)
        .join(' · ');
  }
}

// Boxes each changed cell of a table, measured with the table unrotated so the boxes turn with it, and says whether it
// found them all.
function markCells(drawn, node, cells, scale) {
  const turned = drawn.style.transform;
  drawn.style.transform = '';
  const origin = drawn.getBoundingClientRect();
  let found = 0;
  for (const { row, column } of cells) {
    const td = drawn.querySelector(`td[data-row="${row}"][data-column="${column}"]`);
    if (td) {
      const rect = td.getBoundingClientRect();
      place(element('div', 'part-change cell', node), [(rect.left - origin.left) / scale,
        (rect.top - origin.top) / scale, rect.width / scale, rect.height / scale]);
      found++;
    }
  }
  drawn.style.transform = turned;
  return found === cells.length;
}

// Outlines each drawn part of a chart its changes name, a slice in its own shape and a line along itself, a legend
// entry's swatch and name as one, and a point's label where it draws none outlining its mark, and says whether it drew
// any.
function markParts(drawn, node, parts) {
  const nodes = [...drawn.querySelectorAll('[data-part]')];
  const of = ({ parts, series, point }) => nodes.filter(({ dataset }) => parts.includes(dataset.part) &&
    (series === undefined || Number(dataset.partSeries) === series) &&
    (point === undefined || Number(dataset.partPoint) === point));
  const marked = new Set();
  for (const part of parts) {
    const found = of(part);
    const shown = found.length > 0 || part.point === undefined ? found : of({ ...part, parts: MARKS });
    shown.forEach((shape) => marked.add(shape));
  }
  const traced = [...marked].filter((shape) => SHAPED.includes(shape.dataset.part));
  if (traced.length > 0) {
    const svg = svgElement('svg', { class: 'part-change-shapes', width: drawn.offsetWidth,
      height: drawn.offsetHeight }, node);
    for (const shape of traced) {
      const d = shape.getAttribute('d');
      svgElement('path', { class: shape.dataset.part, d, 'fill-rule': 'evenodd' }, svg);
      // A line's highlight runs under it, so the line keeps its own color.
      if (shape.dataset.part === 'line') {
        svgElement('path', { class: 'repainted', d, style: `stroke: ${shape.getAttribute('stroke')}; stroke-width: ` +
          `${shape.getAttribute('stroke-width')}px` }, svg);
      }
    }
  }
  const boxes = new Map();
  for (const shape of marked) {
    const { dataset } = shape;
    if (SHAPED.includes(dataset.part)) {
      continue;
    }
    const key = dataset.part === 'legend' ? `legend ${dataset.partSeries} ${dataset.partPoint}` : shape;
    const { x, y, width, height } = shape.getBBox();
    const [left, top, right, bottom] = boxes.get(key) || [x, y, x + width, y + height];
    boxes.set(key, [Math.min(left, x), Math.min(top, y), Math.max(right, x + width), Math.max(bottom, y + height)]);
  }
  const pad = 2;
  for (const [left, top, right, bottom] of boxes.values()) {
    place(element('div', 'part-change', node), [left - pad, top - pad, right - left + pad * 2, bottom - top + pad * 2]);
  }
  return marked.size > 0;
}

// The text nodes a drawn text or cell reads in order, each with its offsets in the text textChanges compares, whose
// paragraphs are joined by one newline. Bullets and an empty paragraph's placeholder aren't part of it.
function textSegments(root) {
  const segments = [];
  let offset = 0;
  root.querySelectorAll('.paragraph').forEach((paragraph, index) => {
    offset += index > 0 ? 1 : 0;
    const walker = document.createTreeWalker(paragraph, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      if (node.parentElement.closest('.bullet') || node.data === '\u200b') {
        continue;
      }
      segments.push({ node, start: offset, end: offset + node.data.length });
      offset += node.data.length;
    }
  });
  return segments;
}

// The line boxes of a mark's characters, or for a caret the middle of the space it sits in, or else the edge of the
// character beside it.
function markRects(segments, { start, end }) {
  const range = document.createRange();
  if (start === end) {
    const after = segments.find((segment) => segment.start <= start && start < segment.end);
    const before = segments.find((segment) => segment.start < start && start <= segment.end);
    const space = after && /\s/.test(after.node.data[start - after.start]);
    const segment = space || !before ? after : before;
    if (!segment) {
      return [];
    }
    const at = start - segment.start;
    range.setStart(segment.node, segment === after ? at : at - 1);
    range.setEnd(segment.node, segment === after ? at + 1 : at);
    const rect = range.getBoundingClientRect();
    const left = space ? (rect.left + rect.right) / 2 : segment === after ? rect.left : rect.right;
    return [{ left, top: rect.top, width: 0, height: rect.height }];
  }
  const rects = [];
  for (const segment of segments) {
    const from = Math.max(start, segment.start);
    const to = Math.min(end, segment.end);
    if (from < to) {
      range.setStart(segment.node, from - segment.start);
      range.setEnd(segment.node, to - segment.start);
      rects.push(...range.getClientRects());
    }
  }
  return rects;
}

// Marks the changed words inside the item's mark, measured with the item unrotated so they turn with it.
function markText(drawn, node, regions, showingBefore, scale) {
  if (regions.length === 0) {
    return;
  }
  const turned = drawn.style.transform;
  drawn.style.transform = '';
  const origin = drawn.getBoundingClientRect();
  for (const region of regions) {
    const root = region.cell
      ? drawn.querySelector(`td[data-row="${region.cell.row}"][data-column="${region.cell.column}"]`)
      : drawn;
    const segments = textSegments(root);
    for (const mark of region.marks) {
      const caret = mark.start === mark.end;
      const side = mark.restyled ? 'changed' : caret === showingBefore ? 'added' : 'removed';
      for (const rect of markRects(segments, mark)) {
        place(element('div', `text-change ${side}${caret ? ' caret' : ''}`, node), [(rect.left - origin.left) / scale,
          (rect.top - origin.top) / scale, rect.width / scale, rect.height / scale]);
      }
    }
  }
  drawn.style.transform = turned;
}

// #endregion

// #region Inspector

// Each changed value before and after, reworded text as one diff, with the level that set it now.
function renderChangeTable(changes, parent) {
  if (changes.length === 0) {
    return;
  }
  const list = element('dl', 'props changes-table', parent);
  for (const change of changes) {
    const entry = element('div', `prop${change.cause ? ' restyled' : ''}`, list);
    element('dt', null, entry).textContent = changeLabel(change.key);
    const value = element('dd', null, entry);
    if (textChange(change) && change.before != null && change.after != null) {
      const diff = element('div', 'text-diff', value);
      for (const part of textDiff(change.before, change.after)) {
        element({ same: 'span', removed: 'del', added: 'ins' }[part.op], null, diff).textContent = part.text;
      }
    } else {
      const key = change.key.split('.').pop();
      const shown = (display) => (display == null ? 'Unset' : propertyValue({ key, display }).text);
      element('div', 'was', value).textContent = shown(change.before);
      element('div', 'now', value).textContent = shown(change.after);
    }
    const origin = change.cause || change.afterOrigin || change.beforeOrigin;
    if (origin) {
      originTag(originOf(origin), entry);
    } else {
      element('span', null, entry);
    }
  }
}

// Whether the slide's file holds the change, so writing the item back as the revision has it undoes it. Theme and
// layout items, and changes only the theme made, are reverted in those files.
function revertible(item) {
  return item.owner === 'slide' && (item.status !== 'changed' || item.changes.some((change) => !change.cause));
}

// Writes the item, or without one the whole slide, back as the revision has it.
function revertButton(parent, label, slide, item = null) {
  const button = element('button', 'revert', parent);
  button.type = 'button';
  button.textContent = label;
  const was = namedBase() ? `of ${namedBase()}` : 'it was';
  button.title = `Write ${item ? item.id : slide} back as ${was}; Ctrl+Z undoes it`;
  button.addEventListener('click', () => revert(slide, item?.id, item?.owner));
}

const STATUS = { added: 'Added', removed: 'Removed', changed: 'Changed', moved: 'Moved' };

// The selected item's changes since the revision, above how it looks.
function renderItemChanges(selection, panel) {
  const change = slideDiff(selection.slide)?.items.find((item) =>
    item.id === selection.id && item.owner === selection.owner);
  if (!change) {
    return;
  }
  const node = section(panel, `${STATUS[change.status] || change.status}${sinceBase()}`,
    `change-section ${change.status}`);
  renderChangeTable(change.changes, node);
  if (revertible(change)) {
    revertButton(node, namedBase() ? `Revert to ${namedBase()}` : 'Revert', selection.slide, change);
  }
}

// With nothing selected, what changed on the slide, and the theme values behind any restyling.
function renderSlideChanges(slide, panel) {
  const diff = slideDiff(slide.id);
  if (!diff) {
    return;
  }
  const status = diff.moved
    ? `${diff.status === 'changed' ? 'Changed and moved' : 'Moved'} from slide ${diff.before}`
    : STATUS[diff.status] || diff.status;
  const node = section(panel, `${status}${sinceBase()}`, `change-section ${diff.status}`);
  renderChangeTable(diff.changes, node);
  const list = element('ul', 'change-list', node);
  for (const item of diff.items) {
    const restyled = item.status === 'changed' && item.changes.every((change) => change.cause);
    const entry = element('li', `${item.status}${restyled ? ' restyled' : ''}`, list);
    const index = (slide.items || []).findIndex((candidate) => candidate.id === item.id && candidate.owner === item.owner);
    const name = element(index >= 0 ? 'button' : 'span', 'change-name', entry);
    name.textContent = words(item.id);
    if (index >= 0) {
      name.type = 'button';
      name.addEventListener('click', () => pickItem(slide, index));
    }
    const keys = item.changes.map((change) => changeLabel(change.key).toLowerCase());
    element('span', 'change-what', entry).textContent =
      `${(STATUS[item.status] || item.status).toLowerCase()}${keys.length > 0 ? `: ${keys.join(', ')}` : ''}`;
    if (revertible(item)) {
      revertButton(entry, 'Revert', slide.id, item);
    }
  }
  if (diff.status === 'changed' && (diff.changes.some((change) => !change.cause) || diff.items.some(revertible))) {
    revertButton(node, namedBase() ? `Revert the slide to ${namedBase()}` : 'Revert the slide', slide.id);
  }
  const causes = state.compare.diff.causes.filter((cause) => cause.slides.includes(slide.id));
  for (const cause of causes) {
    const note = element('p', 'note', node);
    note.textContent = `Restyled by the theme: ${cause.before ?? 'unset'} → ${cause.after ?? 'unset'} `;
    originTag(originOf(cause.origin), note);
  }
}

// #endregion

if (typeof module !== 'undefined') {
  module.exports = { changeMarks, changedStep, chartParts, shownDeck, textChanges, textDiff, textMarks, versionDay,
    versionTime };
}