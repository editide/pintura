'use strict';

// Fill and outline for the selected item, picked from the theme's colors and their shades, and outline weights as
// PowerPoint lists them, and its text's alignments and style. Hovering a choice previews it on the slide; picking one
// writes the element's own value, as an agent would, so a fill its style sets stays the style's until picked over.

const PAINT_SLOTS = ['dark1', 'light1', 'dark2', 'light2', 'accent1', 'accent2', 'accent3', 'accent4', 'accent5',
  'accent6'];
const OUTLINE_WEIGHTS = [0.5, 0.75, 1, 1.5, 2.25, 3, 4.5, 6];
const ALIGNS = ['left', 'center', 'right'];
const VALIGNS = ['top', 'middle', 'bottom'];

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

// The rows of fill and outline, alignment and style buttons the inspector shows for an item whose file the mouse may
// write.
function renderPaint(item, panel) {
  const edit = item?.edit;
  if (!edit || edit.locked) {
    return;
  }
  if (edit.fill || edit.stroke) {
    renderFillAndOutline(item, panel);
  }
  if ((edit.align && item.text) || edit.style) {
    const row = element('div', 'paint-row', panel);
    if (edit.align && item.text) {
      const aligns = new Set(item.text.paragraphs.map((paragraph) => paragraph.align));
      const align = aligns.size === 1 ? sentence([...aligns][0]) : 'Mixed';
      pickButton(row, 'Align', `${align}, ${item.text.valign}`, (button) => openAlignment(button, item));
    }
    if (edit.style) {
      pickButton(row, 'Style', paintLabel(edit.style.source, null), (button) => openStyles(button, item));
    }
  }
}

function renderFillAndOutline(item, panel) {
  const edit = item.edit;
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

function pickButton(row, label, text, open) {
  const button = element('button', 'paint-button', row);
  button.type = 'button';
  button.title = `${label}: ${text}`;
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

// A popover under the button, or over it where the window has no room below; pressing the button again closes it.
function openPick(button, key, build) {
  const same = document.getElementById('paint-popover')?.dataset.key === key;
  closePaint();
  if (same) {
    return;
  }
  const popover = element('div', 'paint-popover', document.body);
  popover.id = 'paint-popover';
  popover.dataset.key = key;
  build(popover);
  const box = button.getBoundingClientRect();
  popover.style.left = `${Math.min(box.left, window.innerWidth - popover.offsetWidth - 8)}px`;
  const below = box.bottom + 6;
  const above = box.top - popover.offsetHeight - 6;
  popover.style.top = `${below + popover.offsetHeight > window.innerHeight ? above : below}px`;
  document.addEventListener('pointerdown', paintOutside, true);
  document.addEventListener('keydown', paintEscape, true);
}

function openPaint(button, item, key) {
  openPick(button, key, (popover) => paintChoices(popover, item, key));
}

function paintChoices(popover, item, key) {
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
}

// Nine places for the text, its alignment across and down, as one grid.
function openAlignment(button, item) {
  openPick(button, 'align', (popover) => {
    const grid = element('div', 'align-grid', popover);
    const { valign, paragraphs } = item.text;
    for (const down of VALIGNS) {
      for (const across of ALIGNS) {
        const current = down === valign && paragraphs.every((paragraph) => paragraph.align === across);
        const choice = element('button', `align-choice${current ? ' current' : ''}`, grid);
        choice.type = 'button';
        choice.title = `${sentence(across)}, ${down}`;
        choice.setAttribute('aria-label', choice.title);
        choice.dataset.align = across;
        choice.dataset.valign = down;
        for (const width of [70, 45]) {
          element('span', 'align-line', choice).style.width = `${width}%`;
        }
        paintChoice(choice, item,
          () => ({ text: { ...item.text, valign: down, paragraphs: alignmentPick(item, across, down).paragraphs } }),
          () => {
            const pick = alignmentPick(item, across, down);
            if (pick.notice) {
              notice(pick.notice);
            }
            return pick.edits;
          });
      }
    }
  });
}

// Only what the pick changes is written, so centering a box that already sits in the middle writes its align alone.
// A paragraph whose own style aligns it keeps that alignment over the box's, and the notice says where to change it.
function alignmentPick(item, align, valign) {
  const paragraphs = item.text.paragraphs.map((paragraph) =>
    (paragraph.alignStyle ? paragraph : { ...paragraph, align }));
  const edits = [];
  if (item.text.paragraphs.some((paragraph) => !paragraph.alignStyle && paragraph.align !== align)) {
    edits.push(paintEdit(item.edit.align, align));
  }
  if (item.text.valign !== valign) {
    edits.push(paintEdit(item.edit.valign, valign));
  }
  return { edits, paragraphs, notice: heldNotice(item.text.paragraphs, align) };
}

function heldNotice(paragraphs, align) {
  const held = new Map();
  paragraphs.forEach((paragraph, at) => {
    const style = paragraph.alignStyle;
    if (style && paragraph.align !== align) {
      const key = `${style.style}\n${style.file}:${style.line}\n${paragraph.align}`;
      held.set(key, [...(held.get(key) || []), at + 1]);
    }
  });
  if (held.size === 0) {
    return null;
  }
  const parts = [...held].map(([key, numbers]) => {
    const [style, where, aligned] = key.split('\n');
    const which = numbers.length === 1
      ? `paragraph ${numbers[0]}`
      : `paragraphs ${numbers.slice(0, -1).join(', ')} and ${numbers.at(-1)}`;
    return `${style} aligns ${which} ${aligned}, in ${where.split('/').pop()}`;
  });
  return `${parts.join('; ')}; change it there`;
}

// The theme's styles, each named in its own font and color, the box's own one marked.
function openStyles(button, item) {
  openPick(button, 'style', (popover) => {
    const list = element('div', 'style-choices', popover);
    for (const style of state.themeView?.styles || []) {
      const choice = element('button', `style-choice${style.name === item.edit.style.source ? ' current' : ''}`,
        list);
      choice.type = 'button';
      choice.textContent = style.name;
      Object.assign(choice.style, {
        fontFamily: `"${style.family}"`,
        fontSize: `${Math.min(Math.max(style.size, 11), 20)}px`,
        fontWeight: style.bold ? '700' : '',
        fontStyle: style.italic ? 'italic' : '',
        textDecoration: style.underline ? 'underline' : '',
        color: style.hex
      });
      // The list is white like a slide, so text meant for a dark band sits on the theme's dark color.
      if (lightness(style.hex) > 0.8) {
        choice.style.background = paintHex('dark1');
      }
      choice.addEventListener('click', () => {
        closePaint();
        sendEdits(item, [paintEdit(item.edit.style, style.name)]);
      });
    }
  });
}

function lightness(hex) {
  const [red, green, blue] = [1, 3, 5].map((at) => parseInt(hex.slice(at, at + 2), 16) / 255);
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
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

if (typeof module !== 'undefined') {
  module.exports = { alignmentPick };
}