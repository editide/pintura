'use strict';

// Draws what the server resolved: every color is hex, every font a family, every frame in points. Nothing here knows
// a style name or a theme; that is the model's work. The preview and the headless page share it.

const SVG = 'http://www.w3.org/2000/svg';

let markerCount = 0;

function element(tag, className, parent) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (parent) {
    parent.appendChild(node);
  }
  return node;
}

function svgElement(tag, attributes, parent) {
  const node = document.createElementNS(SVG, tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value !== null && value !== undefined) {
      node.setAttribute(name, value);
    }
  }
  if (parent) {
    parent.appendChild(node);
  }
  return node;
}

function place(node, [x, y, width, height]) {
  node.style.left = `${x}px`;
  node.style.top = `${y}px`;
  node.style.width = `${width}px`;
  node.style.height = `${height}px`;
}

function fontFamily(family) {
  return `"${family.replace(/"/g, '')}"`;
}

const themeFaces = new Map();

// The theme's own font files, registered under the families slides name so they draw whatever this machine has
// installed. Resolves once they have loaded, since text is measured and fitted as it is drawn.
async function useFonts(faces = []) {
  const wanted = new Set(faces.map((face) => JSON.stringify(face)));
  for (const [key, face] of themeFaces) {
    if (!wanted.has(key)) {
      document.fonts.delete(face);
      themeFaces.delete(key);
    }
  }
  for (const face of faces) {
    const key = JSON.stringify(face);
    if (!themeFaces.has(key)) {
      const loaded = new FontFace(face.family, `url("${face.url}")`, { weight: `${face.weight}`, style: face.style });
      themeFaces.set(key, loaded);
      document.fonts.add(loaded);
    }
  }
  await Promise.all([...themeFaces.values()].map((face) => face.load().catch(() => {})));
}

// A missing family falls back to a generic one; a family that is there changes the width of the sample against at
// least one of the three. document.fonts.check can't tell, since it answers true for any family it has no face for.
function fontAvailable(family) {
  const probe = element('span', null, document.body);
  probe.style.font = '40px monospace';
  probe.style.position = 'absolute';
  probe.textContent = 'mmmmmmmmmmlli WQ@#';
  const width = (fontFamilies) => {
    probe.style.fontFamily = fontFamilies;
    return probe.getBoundingClientRect().width;
  };
  const available = ['monospace', 'serif', 'sans-serif']
    .some((generic) => width(`${fontFamily(family)}, ${generic}`) !== width(generic));
  probe.remove();
  return available;
}

function applyCharacter(node, run) {
  if (run.font) {
    node.style.fontFamily = fontFamily(run.font);
  }
  if (run.size) {
    node.style.fontSize = `calc(${run.size}px * var(--fit, 1))`;
  }
  if (run.color) {
    node.style.color = run.color;
  }
  node.style.fontWeight = run.bold ? '700' : '400';
  node.style.fontStyle = run.italic ? 'italic' : 'normal';
  node.style.textDecoration = run.underline ? 'underline' : 'none';
}

// PowerPoint adds a paragraph's space after to the next one's space before, where CSS margins would collapse, so
// spacing is padding; and it ignores space before on a box's first paragraph.
function renderParagraph(paragraph, index, parent) {
  const first = index === 0;
  const node = element('p', 'paragraph', parent);
  node.dataset.paragraph = index;
  applyCharacter(node, paragraph.mark);
  node.style.textAlign = paragraph.align;
  node.style.paddingTop = first ? '0' : `calc(${paragraph.spaceBefore}px * var(--fit, 1))`;
  node.style.paddingBottom = `calc(${paragraph.spaceAfter}px * var(--fit, 1))`;
  if (paragraph.lineSpacing) {
    node.style.lineHeight = `${1.2 * paragraph.lineSpacing}`;
  }
  if (paragraph.indent) {
    node.style.paddingLeft = `${paragraph.indent}px`;
    node.style.textIndent = `-${paragraph.hanging}px`;
  }
  if (paragraph.bullet) {
    const bullet = element('span', 'bullet', node);
    bullet.style.width = `${paragraph.hanging}px`;
    bullet.textContent = paragraph.bullet;
    if (paragraph.bulletColor) {
      bullet.style.color = paragraph.bulletColor;
    }
    if (paragraph.bulletSize) {
      bullet.style.fontSize = `${paragraph.bulletSize}em`;
    }
  }
  const text = paragraph.runs.map((run) => run.text).join('');
  if (text.length === 0) {
    node.appendChild(document.createTextNode('​'));
  }
  for (const run of paragraph.runs) {
    // A field stays in the paragraph when it reads empty, so rewording the text in place keeps it.
    if (run.text.length === 0 && !run.field) {
      continue;
    }
    const span = element(run.link ? 'a' : 'span', null, node);
    if (run.field) {
      span.dataset.field = run.field;
    }
    if (run.trace) {
      markTrace(span, run.trace);
    }
    if (run.link) {
      span.href = run.link;
      span.target = '_blank';
      span.rel = 'noopener';
    }
    applyCharacter(span, run);
    span.textContent = run.text;
  }
  return node;
}

// A number read from a Libreta book, which Ctrl- or Cmd-clicking opens there.
function markTrace(node, trace) {
  node.dataset.trace = JSON.stringify(trace);
}

function renderText(text, parent) {
  const box = element('div', `text-box${text.fit === 'grow' ? ' grow' : ''}${text.wrap ? '' : ' unwrapped'}`, parent);
  const [left, top, right, bottom] = text.insets;
  box.style.padding = `${top}px ${right}px ${bottom}px ${left}px`;
  box.style.justifyContent = { top: 'flex-start', middle: 'center', bottom: 'flex-end' }[text.valign];
  box.dataset.fit = text.fit;
  const flow = element('div', 'text-flow', box);
  if (text.columns > 1) {
    flow.style.columnCount = text.columns;
  }
  text.paragraphs.forEach((paragraph, index) => renderParagraph(paragraph, index, flow));
  return box;
}

function strokeAttributes(stroke, opacity) {
  if (!stroke) {
    return { stroke: 'none' };
  }
  return {
    stroke: stroke.color,
    'stroke-width': stroke.width,
    'stroke-dasharray': dashArray(stroke.dash, stroke.width),
    'stroke-opacity': opacity
  };
}

function transform(item, node) {
  if (item.rotate) {
    node.style.transform = `rotate(${item.rotate}deg)`;
  }
}

function flipped(item, svg, width, height) {
  if (item.flip === 'horizontal') {
    svg.setAttribute('transform', `matrix(-1 0 0 1 ${width} 0)`);
  } else if (item.flip === 'vertical') {
    svg.setAttribute('transform', `matrix(1 0 0 -1 0 ${height})`);
  }
}

function renderShape(item, parent) {
  const node = element('div', 'item shape', parent);
  place(node, item.frame);
  transform(item, node);
  const [x, y, width] = item.frame;
  const opacity = item.opacity === undefined ? null : item.opacity / 100;
  const tail = item.tail ? [item.tail[0] - x, item.tail[1] - y] : null;
  // A shape that grows to fit its text is drawn again at the height its text needs, once that can be measured.
  node.draw = (height) => {
    node.querySelector(':scope > svg')?.remove();
    const svg = svgElement('svg', { width, height, viewBox: `0 0 ${width} ${height}` });
    node.prepend(svg);
    const group = svgElement('g', {}, svg);
    flipped(item, group, width, height);
    const d = shapePath(item.shape, width, height, { direction: item.direction, adjust: item.adjust, tail });
    svgElement('path', {
      d,
      fill: isOpen(item.shape) || !item.fill ? 'none' : item.fill,
      'fill-opacity': opacity,
      'stroke-linejoin': 'round',
      ...strokeAttributes(item.stroke, opacity)
    }, group);
  };
  node.draw(item.frame[3]);
  if (item.text) {
    renderText(item.text, node);
  }
  return node;
}

function renderTextItem(item, parent) {
  const node = element('div', 'item text', parent);
  place(node, item.frame);
  transform(item, node);
  if (item.fill || item.stroke) {
    const svg = svgElement('svg', { width: '100%', height: '100%' }, node);
    const opacity = item.opacity === undefined ? null : item.opacity / 100;
    svgElement('rect', {
      width: '100%',
      height: '100%',
      fill: item.fill || 'none',
      'fill-opacity': opacity,
      ...strokeAttributes(item.stroke, opacity)
    }, svg);
  }
  const box = renderText(item.text, node);
  if (item.text.fit === 'grow') {
    node.style.height = 'auto';
    box.style.position = 'relative';
  }
  return node;
}

function marker(kind, color, defs) {
  const shape = MARKERS[kind];
  const id = `marker-${++markerCount}`;
  const node = svgElement('marker', {
    id,
    viewBox: '-1 -1 12 12',
    refX: shape.anchor,
    refY: 5,
    markerWidth: 3.6,
    markerHeight: 3.6,
    markerUnits: 'strokeWidth',
    orient: 'auto-start-reverse'
  }, defs);
  svgElement('path', {
    d: shape.path,
    fill: shape.fill ? color : 'none',
    stroke: color,
    'stroke-width': shape.fill ? 0 : 10 / 3,
    'stroke-linejoin': 'miter'
  }, node);
  return `url(#${id})`;
}

function renderLine(item, parent) {
  const [x1, y1] = item.from;
  const [x2, y2] = item.to;
  const left = Math.min(x1, x2);
  const top = Math.min(y1, y2);
  const node = element('div', 'item line', parent);
  place(node, [left, top, Math.abs(x2 - x1), Math.abs(y2 - y1)]);
  const svg = svgElement('svg', { width: Math.max(1, Math.abs(x2 - x1)), height: Math.max(1, Math.abs(y2 - y1)) },
    node);
  if (!item.stroke) {
    return node;
  }
  const defs = svgElement('defs', {}, svg);
  const opacity = item.opacity === undefined ? null : item.opacity / 100;
  // A line's box can be zero high or wide, so the preview clicks on a wider, invisible stroke instead.
  svgElement('line', {
    class: 'hit',
    x1: x1 - left,
    y1: y1 - top,
    x2: x2 - left,
    y2: y2 - top,
    stroke: 'transparent',
    'stroke-width': Math.max(8, item.stroke.width + 6)
  }, svg);
  svgElement('line', {
    x1: x1 - left,
    y1: y1 - top,
    x2: x2 - left,
    y2: y2 - top,
    'marker-start': item.start ? marker(item.start, item.stroke.color, defs) : null,
    'marker-end': item.end ? marker(item.end, item.stroke.color, defs) : null,
    ...strokeAttributes(item.stroke, opacity)
  }, svg);
  return node;
}

function renderPicture(item, parent) {
  const node = element('div', 'item picture-item', parent);
  place(node, item.frame);
  transform(item, node);
  if (item.opacity !== undefined) {
    node.style.opacity = item.opacity / 100;
  }
  const aspect = { contain: 'xMidYMid meet', cover: 'xMidYMid slice', stretch: 'none' }[item.fit];
  if (!item.crop) {
    const image = element('img', 'picture', node);
    image.src = item.picture;
    image.alt = item.alt || '';
    image.style.objectFit = { contain: 'contain', cover: 'cover', stretch: 'fill' }[item.fit];
    node.loaded = image.decode().catch(() => {});
    return node;
  }
  // A crop needs the image's own size before the visible part can be fitted into the frame.
  node.loaded = imageSize(item.picture).then(([width, height]) => {
    const [left, top, right, bottom] = item.crop;
    const svg = svgElement('svg', {
      class: 'picture',
      viewBox: `${left} ${top} ${width - left - right} ${height - top - bottom}`,
      preserveAspectRatio: aspect,
      role: 'img',
      'aria-label': item.alt || null
    }, node);
    const image = svgElement('image', { href: item.picture, width, height, preserveAspectRatio: 'none' }, svg);
    return new Promise((resolve) => {
      image.addEventListener('load', resolve);
      image.addEventListener('error', resolve);
    });
  }).catch(() => {});
  return node;
}

// An SVG's size is its width and height, or else its viewBox; browsers invent 300 × 150 for one with only a viewBox.
async function imageSize(url) {
  const response = await fetch(url);
  if ((response.headers.get('content-type') || '').startsWith('image/svg')) {
    const root = new DOMParser().parseFromString(await response.text(), 'image/svg+xml').documentElement;
    const width = parseFloat(root.getAttribute('width'));
    const height = parseFloat(root.getAttribute('height'));
    if (width && height) {
      return [width, height];
    }
    const box = (root.getAttribute('viewBox') || '').split(/[\s,]+/).map(Number);
    return [box[2] || 300, box[3] || 150];
  }
  const bitmap = await createImageBitmap(await response.blob());
  return [bitmap.width, bitmap.height];
}

function border(stroke) {
  return stroke ? `${stroke.width}px ${{ solid: 'solid', dash: 'dashed', dot: 'dotted' }[stroke.dash]} ${stroke.color}` : '';
}

// An HTML table with fixed column widths, as tall as its rows grow; the frame's height only bounds it, for --check.
function renderTable(item, parent) {
  const node = element('div', 'item table', parent);
  place(node, item.frame);
  node.style.height = 'auto';
  node.dataset.frameHeight = item.frame[3];
  const { widths, heights, cells } = item.table;
  const table = element('table', 'cells', node);
  table.style.width = `${widths.reduce((sum, width) => sum + width, 0)}px`;
  const columns = element('colgroup', null, table);
  for (const width of widths) {
    element('col', null, columns).style.width = `${width}px`;
  }
  const body = element('tbody', null, table);
  const rows = heights.map((height) => {
    const row = element('tr', null, body);
    if (height) {
      row.style.height = `${height}px`;
    }
    return row;
  });
  for (const cell of cells) {
    const td = element('td', null, rows[cell.row - 1]);
    td.rowSpan = cell.rows;
    td.colSpan = cell.columns;
    td.dataset.row = cell.row;
    td.dataset.column = cell.column;
    if (cell.trace) {
      markTrace(td, cell.trace);
    }
    const [left, top, right, bottom] = cell.text.insets;
    td.style.padding = `${top}px ${right}px ${bottom}px ${left}px`;
    td.style.verticalAlign = cell.text.valign;
    td.style.background = cell.fill || '';
    // Borders sit centered on the cell's edge without adding to its row, as PowerPoint draws them.
    const sides = { top: cell.borderTop, bottom: cell.borderBottom, left: cell.borderLeft, right: cell.borderRight };
    for (const [side, stroke] of Object.entries(sides)) {
      if (stroke) {
        td.style.setProperty(`--border-${side}`, border(stroke));
        td.style.setProperty(`--border-${side}-width`, `${stroke.width}px`);
      }
    }
    cell.text.paragraphs.forEach((paragraph, index) => renderParagraph(paragraph, index, td));
  }
  return node;
}

// An area on a layout's stage, which only exists to be moved.
function renderArea(item, parent) {
  const node = element('div', `item area-item ${item.owner}`, parent);
  place(node, item.frame);
  element('span', 'guide-label', node).textContent = item.id;
  return node;
}

function renderItem(item, parent) {
  const node = {
    text: renderTextItem,
    shape: renderShape,
    line: renderLine,
    picture: renderPicture,
    chart: renderChart,
    table: renderTable,
    area: renderArea
  }[item.kind](item, parent);
  node.dataset.id = item.id;
  node.dataset.owner = item.owner;
  return node;
}

function renderStage(slide, [width, height]) {
  const stage = element('div', 'stage');
  stage.style.width = `${width}px`;
  stage.style.height = `${height}px`;
  if (slide.error) {
    stage.style.background = '#fff';
    element('div', 'slide-error', stage).textContent = slide.error;
    return stage;
  }
  stage.style.background = slide.background || '#fff';
  slide.items.forEach((item, index) => {
    renderItem(item, stage).dataset.index = index;
  });
  return stage;
}

// Needs the stage in the document, since both read the laid-out text.
function fitText(stage) {
  growShapes(stage);
  shrinkToFit(stage);
}

// PowerPoint's "resize shape to fit text" sets the height to the text's, smaller as well as larger.
function growShapes(stage) {
  for (const node of stage.querySelectorAll('.item.shape')) {
    const box = node.querySelector(':scope > .text-box[data-fit="grow"]');
    if (box) {
      const height = box.offsetHeight;
      node.style.height = `${height}px`;
      node.draw(height);
    }
  }
}

// Shrink scales every size in the box down until the text fits, as PowerPoint's font scale does.
function shrinkToFit(stage) {
  for (const box of stage.querySelectorAll('.text-box[data-fit="shrink"]')) {
    box.style.setProperty('--fit', '1');
    if (box.scrollHeight <= box.clientHeight) {
      continue;
    }
    let low = 0.25;
    let high = 1;
    for (let step = 0; step < 8; step++) {
      const middle = (low + high) / 2;
      box.style.setProperty('--fit', `${middle}`);
      if (box.scrollHeight <= box.clientHeight) {
        low = middle;
      } else {
        high = middle;
      }
    }
    box.style.setProperty('--fit', `${low}`);
  }
}

function mountStage(frame, slide, size, width) {
  const scale = width / size[0];
  frame.replaceChildren();
  frame.style.width = `${width}px`;
  frame.style.height = `${size[1] * scale}px`;
  const stage = renderStage(slide, size);
  stage.style.transform = `scale(${scale})`;
  stage.style.setProperty('--scale', scale);
  frame.appendChild(stage);
  fitText(stage);
  return stage;
}

// The number of lines a paragraph wraps to, from its rectangles in document order. A rectangle overlapping the current
// line by at least half its height sits on it, so a small run beside a large one shares the line while tight line
// spacing, whose glyph boxes overlap slightly, still separates two.
function countLines(rects) {
  let lines = 0;
  let top = 0;
  let bottom = 0;
  for (const rect of rects) {
    const height = rect.bottom - rect.top;
    if (height <= 0) {
      continue;
    }
    const overlap = Math.min(bottom, rect.bottom) - Math.max(top, rect.top);
    if (lines > 0 && overlap >= Math.min(height, bottom - top) / 2) {
      top = Math.min(top, rect.top);
      bottom = Math.max(bottom, rect.bottom);
    } else {
      lines++;
      top = rect.top;
      bottom = rect.bottom;
    }
  }
  return lines;
}

// Text nodes only: a range over a span yields the span's line boxes before its text's, visiting each line twice.
function textRects(paragraph) {
  const rects = [];
  const walker = document.createTreeWalker(paragraph, NodeFilter.SHOW_TEXT);
  const range = document.createRange();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    range.selectNodeContents(node);
    rects.push(...range.getClientRects());
  }
  return rects;
}

// A table's height and its rows', how far its rows grow past the frame, and the lines in each cell, row by row, in
// points.
function measureTable(node) {
  const stage = node.closest('.stage');
  const scale = stage.getBoundingClientRect().width / stage.offsetWidth;
  const height = node.getBoundingClientRect().height / scale;
  const cells = [...node.querySelectorAll('td')].map((td) =>
    [...td.querySelectorAll('.paragraph')].reduce((sum, paragraph) => sum + countLines(textRects(paragraph)), 0));
  return {
    id: node.dataset.id,
    owner: node.dataset.owner,
    kind: 'table',
    fit: 'none',
    lines: cells.reduce((sum, lines) => sum + lines, 0),
    paragraphs: cells,
    overflow: Math.max(0, Math.round((height - parseFloat(node.dataset.frameHeight)) * 10) / 10),
    height: Math.round(height * 10) / 10,
    rows: [...node.querySelectorAll('tr')].map((tr) => Math.round(tr.getBoundingClientRect().height / scale * 10) / 10)
  };
}

// A drawn item's text: its lines per paragraph, how far it runs past the frame, and what fitting made of it, in
// points. Null for an item without text.
function measureBox(node) {
  if (node.classList.contains('table')) {
    return measureTable(node);
  }
  const box = node.querySelector(':scope > .text-box');
  if (!box) {
    return null;
  }
  const stage = node.closest('.stage');
  const scale = stage.getBoundingClientRect().width / stage.offsetWidth;
  const style = getComputedStyle(box);
  const frame = { top: 0, bottom: box.clientHeight };
  const content = { top: parseFloat(style.paddingTop), bottom: box.clientHeight - parseFloat(style.paddingBottom) };
  const flow = box.firstChild.getBoundingClientRect();
  const top = (flow.top - box.getBoundingClientRect().top) / scale;
  const text = { top, bottom: top + flow.height / scale };
  const paragraphs = [...box.querySelectorAll('.paragraph')].map((paragraph) => countLines(textRects(paragraph)));
  const ink = [...box.querySelectorAll('.paragraph')].flatMap(textRects).filter((rect) => rect.width > 0)
    .map((rect) => onSlide(rect, stage));
  const fit = box.dataset.fit;
  return {
    id: node.dataset.id,
    owner: node.dataset.owner,
    fit,
    lines: paragraphs.reduce((sum, lines) => sum + lines, 0),
    paragraphs,
    overflow: overflow(fit, text, frame, content),
    scale: fit === 'shrink' ? parseFloat(box.style.getPropertyValue('--fit')) : undefined,
    height: fit === 'grow' ? Math.round(node.getBoundingClientRect().height / scale * 10) / 10 : undefined,
    ink
  };
}

// A drawn rectangle as [x, y, width, height] in points on the slide.
function onSlide(rect, stage) {
  const origin = stage.getBoundingClientRect();
  const scale = origin.width / stage.offsetWidth;
  return [rect.left - origin.left, rect.top - origin.top, rect.width, rect.height]
    .map((value) => Math.round(value / scale * 10) / 10);
}

// How far text runs past what holds it, in points, each extent measured from the top of its box. Unfitted text may
// fill the insets, as it does in PowerPoint, so only text past the frame shows; shrinking fits text inside the insets.
function overflow(fit, text, frame, content) {
  if (fit === 'grow') {
    return 0;
  }
  const past = fit === 'shrink'
    ? (text.bottom - text.top) - (content.bottom - content.top)
    : Math.max(frame.top - text.top, text.bottom - frame.bottom);
  return Math.max(0, Math.round(past * 10) / 10);
}

// Every text box on a drawn stage.
function measureText(stage) {
  return [...stage.querySelectorAll('.item')].map(measureBox).filter(Boolean);
}

// Every chart's labels on a drawn stage, for text laid over a chart to be checked against them.
function measureCharts(stage) {
  return [...stage.querySelectorAll('.item.chart')].map((node) => ({
    id: node.dataset.id,
    labels: [...node.querySelectorAll('text')].filter((text) => text.textContent)
      .map((text) => ({ text: text.textContent, rect: onSlide(text.getBoundingClientRect(), stage) }))
  }));
}

if (typeof module !== 'undefined') {
  module.exports = { countLines, overflow };
}