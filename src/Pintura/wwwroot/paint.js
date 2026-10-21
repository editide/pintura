'use strict';

// Fill and outline for the selected item, picked from the theme's colors and their shades, and outline weights as
// PowerPoint lists them. Hovering a choice previews it on the slide; picking one writes the element's own value, as
// an agent would, so a fill its style sets stays the style's until picked over.

const PAINT_SLOTS = ['dark1', 'light1', 'dark2', 'light2', 'accent1', 'accent2', 'accent3', 'accent4', 'accent5',
  'accent6'];
const OUTLINE_WEIGHTS = [0.5, 0.75, 1, 1.5, 2.25, 3, 4.5, 6];

// A color as the files write it, by its hex, drawn from the theme's slots, shades, and named colors.
function paintNames() {
  const names = new Map();
  const theme = state.themeView;
  for (const color of theme?.colors || []) {
    if (PAINT_SLOTS.includes(color.name)) {
      names.set(color.hex.toUpperCase(), names.get(color.hex.toUpperCase()) || color.name);
      for (const shade of color.shades) {
        names.set(shade.hex.toUpperCase(), names.get(shade.hex.toUpperCase()) || `${color.name} ${shade.label}`);
      }
    }
  }
  for (const color of theme?.named || []) {
    names.set(color.hex.toUpperCase(), names.get(color.hex.toUpperCase()) || color.name);
  }
  return names;
}

function paintLabel(source, hex) {
  if (source && source !== 'none') {
    return source.replace(/^"(.*)"$/, '$1');
  }
  return hex ? paintNames().get(hex.toUpperCase()) || hex : 'None';
}

// The row of fill and outline buttons the inspector shows for an item whose file the mouse may write.
function renderPaint(item, panel) {
  const edit = item?.edit;
  if (!edit || edit.locked || !(edit.fill || edit.stroke)) {
    return;
  }
  const row = element('div', 'paint-row', panel);
  if (edit.fill) {
    paintButton(row, 'Fill', item.fill, paintLabel(edit.fill.source, item.fill),
      (button) => openPaint(button, item, 'fill'));
  }
  const outline = item.stroke
    ? `${paintLabel(edit.strokeColor.source, item.stroke.color)} · ${item.stroke.width}pt`
    : 'None';
  paintButton(row, item.kind === 'line' ? 'Line' : 'Outline', item.stroke?.color, outline,
    (button) => openPaint(button, item, 'stroke'));
}

function paintButton(row, label, hex, text, open) {
  const button = element('button', 'paint-button', row);
  button.type = 'button';
  button.title = `${label}: ${text}`;
  const chip = element('span', `swatch${hex ? '' : ' empty'}`, button);
  if (hex) {
    chip.style.background = hex;
  }
  element('span', 'paint-label', button).textContent = label;
  element('span', 'paint-value', button).textContent = text;
  button.addEventListener('click', () => open(button));
}

function closePaint() {
  document.getElementById('paint-popover')?.remove();
  document.removeEventListener('pointerdown', paintOutside, true);
  document.removeEventListener('keydown', paintEscape, true);
  unpreviewPaint();
}

function paintOutside(event) {
  if (!event.target.closest('#paint-popover, .paint-button')) {
    closePaint();
  }
}

function paintEscape(event) {
  if (event.key === 'Escape') {
    event.stopPropagation();
    closePaint();
  }
}

function openPaint(button, item, key) {
  const open = document.getElementById('paint-popover');
  const same = open?.dataset.key === key;
  closePaint();
  if (same) {
    return;
  }
  const popover = element('div', 'paint-popover', document.body);
  popover.id = 'paint-popover';
  popover.dataset.key = key;
  const fill = key === 'fill';
  const current = fill ? item.fill : item.stroke?.color;
  const none = element('button', `paint-none${current ? '' : ' current'}`, popover);
  none.type = 'button';
  none.textContent = fill ? 'No fill' : 'No outline';
  paintChoice(none, item, () => (fill ? { fill: null } : { stroke: null }), () => noPaint(item, key));
  const grid = element('div', 'paint-grid', popover);
  const byName = new Map((state.themeView?.colors || []).map((color) => [color.name, color]));
  const columns = PAINT_SLOTS.map((name) => byName.get(name)).filter(Boolean);
  const rows = [null, ...(columns[0]?.shades || []).map((shade) => shade.label)];
  for (const label of rows) {
    for (const color of columns) {
      const hex = label ? color.shades.find((shade) => shade.label === label).hex : color.hex;
      paintSwatch(grid, item, key, label ? `${color.name} ${label}` : color.name, hex, current);
    }
  }
  const named = state.themeView?.named || [];
  if (named.length > 0) {
    const row = element('div', 'paint-named', popover);
    for (const color of named) {
      paintSwatch(row, item, key, color.name, color.hex, current);
    }
  }
  if (!fill) {
    element('div', 'paint-heading', popover).textContent = 'Weight';
    const weights = element('div', 'paint-weights', popover);
    for (const width of OUTLINE_WEIGHTS) {
      const choice = element('button', `paint-weight${item.stroke?.width === width ? ' current' : ''}`, weights);
      choice.type = 'button';
      choice.title = `${width}pt`;
      element('span', 'paint-weight-line', choice).style.borderTopWidth = `${Math.max(width, 0.5)}px`;
      element('span', null, choice).textContent = `${width}`;
      paintChoice(choice, item,
        () => ({
          stroke: { color: item.stroke?.color || paintHex('dark1'), width, dash: item.stroke?.dash || 'solid' }
        }),
        () => outlineEdits(item, { width }));
    }
  }
  const box = button.getBoundingClientRect();
  popover.style.left = `${Math.min(box.left, window.innerWidth - popover.offsetWidth - 8)}px`;
  const below = box.bottom + 6;
  const above = box.top - popover.offsetHeight - 6;
  popover.style.top = `${below + popover.offsetHeight > window.innerHeight ? above : below}px`;
  document.addEventListener('pointerdown', paintOutside, true);
  document.addEventListener('keydown', paintEscape, true);
}

function paintHex(name) {
  return state.themeView?.colors.find((color) => color.name === name)?.hex || '#000000';
}

function paintSwatch(parent, item, key, name, hex, current) {
  const choice = element('button', `paint-swatch${current?.toUpperCase() === hex.toUpperCase() ? ' current' : ''}`,
    parent);
  choice.type = 'button';
  choice.title = name;
  choice.setAttribute('aria-label', name);
  choice.style.background = hex;
  if (key === 'fill') {
    paintChoice(choice, item, () => ({ fill: hex }), () => [paintEdit(item.edit.fill, name)]);
  } else {
    paintChoice(choice, item,
      () => ({ stroke: { color: hex, width: item.stroke?.width || 1, dash: item.stroke?.dash || 'solid' } }),
      () => outlineEdits(item, { color: name }));
  }
}

// Hovering previews the change on the slide; clicking writes it.
function paintChoice(choice, item, change, edits) {
  choice.addEventListener('pointerenter', () => previewPaint(item, change()));
  choice.addEventListener('pointerleave', unpreviewPaint);
  choice.addEventListener('click', () => {
    const written = edits();
    closePaint();
    sendEdits(item, written);
  });
}

function paintEdit(source, value) {
  return { path: source.path, source: source.source, value };
}

function noPaint(item, key) {
  return [paintEdit(key === 'fill' ? item.edit.fill : item.edit.stroke, 'none')];
}

// A visible outline needs a color and a width, so picking one where there's no outline writes the other too.
function outlineEdits(item, { color, width }) {
  const { strokeColor, strokeWidth } = item.edit;
  const edits = [];
  if (color || !item.stroke) {
    edits.push(paintEdit(strokeColor, color || 'dark1'));
  }
  if (width !== undefined || !item.stroke) {
    edits.push(paintEdit(strokeWidth, String(width ?? 1)));
  }
  return edits;
}

// The item drawn again with the change, until the pointer leaves the choice.
let paintPreview = null;

function previewPaint(item, change) {
  unpreviewPaint();
  const node = selectedNode();
  if (!node) {
    return;
  }
  const fresh = renderItem({ ...item, ...change }, slideStage());
  fresh.dataset.index = node.dataset.index;
  fresh.classList.add('selected');
  node.replaceWith(fresh);
  paintPreview = { fresh, node };
  fitText(slideStage());
}

function unpreviewPaint() {
  if (paintPreview) {
    paintPreview.fresh.replaceWith(paintPreview.node);
    paintPreview = null;
  }
}