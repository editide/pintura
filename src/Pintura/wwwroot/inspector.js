'use strict';

// The inspector answers what a reviewer asks of the selected element, or with none selected of the slide: what's
// wrong, what changed, where its numbers come from, and how it looks, in sections that fold to a summary. What the
// element's own file sets shows with its line; what it inherits folds beneath, naming where from. Paths stay in
// tooltips; `--element` prints the whole account for agents.

const KINDS = {
  text: 'Text box',
  shape: 'Shape',
  line: 'Line',
  picture: 'Picture',
  chart: 'Chart',
  table: 'Table',
  html: 'HTML'
};

const LABELS = {
  frame: 'Position',
  from: 'Start',
  to: 'End',
  rotate: 'Rotation',
  flip: 'Flip',
  opacity: 'Opacity',
  text: 'Text',
  style: 'Style',
  font: 'Font',
  size: 'Size',
  color: 'Color',
  bold: 'Bold',
  italic: 'Italic',
  underline: 'Underline',
  align: 'Alignment',
  valign: 'Vertical alignment',
  space_before: 'Space before',
  space_after: 'Space after',
  line_spacing: 'Line spacing',
  insets: 'Padding',
  fit: 'Fit',
  wrap: 'Wrap',
  columns: 'Columns',
  bullet: 'Bullet',
  bullet_color: 'Bullet color',
  bullet_size: 'Bullet size',
  indent: 'Indent',
  hanging: 'Hanging indent',
  shape: 'Shape',
  fill: 'Fill',
  stroke: 'Outline',
  'stroke.color': 'Outline color',
  'stroke.width': 'Outline width',
  'stroke.dash': 'Outline dash',
  shadow: 'Shadow',
  'shadow.color': 'Shadow color',
  'shadow.opacity': 'Shadow opacity',
  'shadow.blur': 'Shadow blur',
  'shadow.distance': 'Shadow distance',
  'shadow.angle': 'Shadow angle',
  'adjust.corner': 'Corner radius',
  start: 'Start arrow',
  end: 'End arrow',
  picture: 'File',
  alt: 'Alt text',
  crop: 'Crop',
  chart: 'Chart file',
  table: 'Table file',
  type: 'Chart type',
  data: 'Data',
  title: 'Title',
  legend: 'Legend',
  series_colors: 'Series colors',
  gap_width: 'Gap width',
  overlap: 'Overlap',
  header_rows: 'Header rows',
  row_style: 'Row style'
};

const POINTS = new Set(['size', 'space_before', 'space_after', 'indent', 'hanging', 'adjust.corner', 'text.size',
  'markers.size', 'shadow.blur', 'shadow.distance']);
const PERCENTAGES = new Set(['gap_width', 'overlap']);
// Names a file gives, which read as written rather than as words.
const LITERAL = new Set(['style', 'row_style', 'chart', 'table', 'data', 'picture', 'alt', 'title']);
const POSITION = new Set(['frame', 'from', 'to', 'rotate', 'flip', 'opacity']);
const TEXT = new Set(['style', 'font', 'size', 'color', 'bold', 'italic', 'underline', 'align', 'valign',
  'space_before', 'space_after', 'line_spacing', 'insets', 'fit', 'wrap', 'columns']);

function words(text) {
  const spaced = String(text).replace(/[_-]+/g, ' ').trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function propertyLabel(key) {
  if (LABELS[key]) {
    return LABELS[key];
  }
  const column = /^columns\[(\d+)\]\.(\w+)$/.exec(key);
  if (column) {
    return `Column ${column[1]} ${column[2].replace(/_/g, ' ')}`;
  }
  return words(key.split('.').map((part) => part.replace(/_/g, ' ')).join(' › '));
}

// A diff's keys put the paragraph or run first, as in "paragraph 2 run 1.color".
function changeLabel(key) {
  const scoped = /^([a-z]+ [^.]*)\.(.+)$/i.exec(key);
  return scoped ? `${words(scoped[1])} · ${propertyLabel(scoped[2])}` : propertyLabel(key);
}

function colorName(name) {
  const theme = /^([a-z_]+?)(\d+)?(?: (lighter|darker) (\d+))?$/.exec(name);
  if (!theme) {
    return name;
  }
  const [, base, number, shade, amount] = theme;
  return `${words(base)}${number ? ` ${number}` : ''}${shade ? `, ${shade} ${amount}%` : ''}`;
}

function unit(key) {
  if (POINTS.has(key) || /(^|\.|\])width$/.test(key) && !PERCENTAGES.has(key)) {
    return ' pt';
  }
  if (PERCENTAGES.has(key)) {
    return '%';
  }
  return key === 'rotate' ? '°' : '';
}

// Formats from the server's display string, which diffs carry too, rather than its value.
function propertyValue({ key, display }) {
  const shown = String(display);
  if (shown === 'none') {
    return { text: 'None' };
  }
  const numbers = /\[([^\]]*)\]/.exec(shown)?.[1].split(', ');
  if (key === 'frame' && numbers?.length === 4) {
    const area = shown.slice(0, shown.indexOf('[')).trim();
    const [x, y, width, height] = numbers;
    return { text: `${x}, ${y} · ${width} × ${height} pt`, ...(area ? { note: `${words(area)} area` } : {}) };
  }
  if (key === 'insets' && numbers?.length === 4) {
    const [left, top, right, bottom] = numbers;
    if (left === right && top === bottom) {
      return { text: left === top ? `${left} pt` : `${left} pt sides, ${top} pt top and bottom` };
    }
    return { text: `Left ${left}, top ${top}, right ${right}, bottom ${bottom} pt` };
  }
  if ((key === 'from' || key === 'to') && numbers?.length === 2) {
    return { text: `${numbers.join(', ')} pt` };
  }
  if (/#[0-9A-F]{6}/i.test(shown)) {
    const colors = [];
    const names = shown.split(', ').map((part) => {
      const [, name, hex] = /^(.*?)(?: \((#[0-9A-F]{6})\))?$/i.exec(part);
      colors.push(hex || name);
      return colorName(name);
    });
    return { text: names.join(', '), colors };
  }
  const font = key === 'font' && /^(.+?) \((.+)\)$/.exec(shown);
  if (font) {
    return { text: font[2], note: `${words(font[1])} font` };
  }
  if (shown === 'true' || shown === 'false') {
    return { text: shown === 'true' ? 'Yes' : 'No' };
  }
  if (/^-?\d+(\.\d+)?$/.test(shown)) {
    return { text: `${shown}${unit(key)}` };
  }
  if (/^[a-z]+(_[a-z]+)*$/.test(shown) && !LITERAL.has(key)) {
    return { text: words(shown) };
  }
  return { text: shown };
}

// The level that set a value, from the file its origin names: a slide, a layout, the theme, or a chart's or table's
// own file. A value with no origin is one the engine or PowerPoint defines.
function originOf(origin) {
  if (!origin) {
    return { level: 'default', label: 'Default' };
  }
  // A file and line, then the key; a theme value behind a restyle names no line.
  const [, file, line, key] = /^(.+?)(?::(\d+))?(?: ([\w.\[\]-]+))?$/.exec(origin) || [null, origin];
  const name = file.split('/').pop();
  const layout = /\/layouts\/([^/]+)\.yaml$/.exec(file);
  const [level, label] =
    /\/slides\//.test(file) ? ['slide', 'This slide']
      : layout ? ['layout', `${words(layout[1])} layout`]
        : /\/charts\//.test(file) ? ['chart', 'Chart file']
          : /\/tables\//.test(file) ? ['table', 'Table file']
            : name === 'presentation.yaml' ? ['presentation', 'Presentation']
              : name === 'styles.yaml' || name === 'theme.yaml' ? ['theme', 'Theme']
                : ['file', name];
  return { level, label, file, line: line ? Number(line) : null, key };
}

function hasText(element) {
  return Boolean(element.paragraphs?.length || element.text?.paragraphs?.length);
}

// A shape holding text reads as a text box, its geometry named beside it.
function kindName({ kind, role, ...element }) {
  if (role === 'title') {
    return 'Title';
  }
  if (role === 'placeholder') {
    return 'Placeholder';
  }
  if (kind === 'shape' && hasText(element)) {
    return 'Text box';
  }
  return KINDS[kind] || words(kind);
}

// What the element is, more closely than its kind: a shape's geometry, a chart's type.
function kindSummary(detail) {
  const value = (key) => detail.properties.find((property) => property.key === key)?.value;
  if (detail.kind === 'shape') {
    const shape = words(value('shape') || 'rectangle');
    return hasText(detail) ? `Text box · ${shape}` : shape;
  }
  if (detail.kind === 'chart' && value('type')) {
    return `${words(value('type'))} chart`;
  }
  return detail.role === 'title' ? 'Title placeholder' : kindName(detail);
}

const CHART_FILE = new Set(['chart', 'type', 'style', 'data', 'title']);
const TABLE_FILE = new Set(['table', 'style', 'data']);
const PICTURE = new Set(['picture', 'alt', 'crop', 'fit']);

// The section a property is read in, by what the element is.
function groupOf(kind, key, text) {
  if (POSITION.has(key)) {
    return 'placement';
  }
  if (kind === 'chart') {
    if (CHART_FILE.has(key)) {
      return 'chart';
    }
    if (key.startsWith('data_labels.')) {
      return 'labels';
    }
    if (/^\w+_axis\./.test(key)) {
      return 'axes';
    }
    if (key === 'legend' || key === 'series_colors') {
      return 'legend';
    }
    if (key === 'gap_width' || key === 'overlap') {
      return 'bars';
    }
    return key.startsWith('text.') ? 'text' : 'more';
  }
  if (kind === 'table') {
    if (TABLE_FILE.has(key)) {
      return 'table';
    }
    if (key.startsWith('columns[')) {
      return 'columns';
    }
    if (key === 'header_rows') {
      return 'rows';
    }
    return TEXT.has(key) ? 'text' : 'more';
  }
  if (kind === 'picture' && PICTURE.has(key)) {
    return 'picture';
  }
  if (TEXT.has(key) && (text || key !== 'style')) {
    return 'text';
  }
  return kind === 'line' ? 'line' : 'shape';
}

// What a value nothing sets falls back to, by kind.
const FALLBACKS = {
  chart: { label: 'PowerPoint', phrase: 'PowerPoint' },
  table: { label: 'Even share', phrase: 'an even share' }
};

// Where an inherited value comes from, as a tag and in words: the style naming it before the file holding it.
function sourceOf(origin, kind, property = '') {
  if (!origin && kind === 'chart' && /axis\.(min|max|step)$/.test(property)) {
    return { label: 'Automatic', phrase: 'the automatic scale' };
  }
  if (!origin) {
    return FALLBACKS[kind] || { label: 'Geometry', phrase: "the shape's geometry" };
  }
  const { level, label, key } = originOf(origin);
  const style = /^(?:styles|charts|tables)\.([\w-]+)\./.exec(key || '');
  if (style) {
    return { label: style[1], phrase: `the ${style[1]} style`, style: true };
  }
  if (level === 'slide') {
    return { label, phrase: 'this slide' };
  }
  return { label, phrase: level === 'file' ? label : `the ${label.toLowerCase()}` };
}

function sourcesPhrase(sources) {
  const phrases = [...new Set(sources.map((source) => source.phrase))];
  return phrases.length < 2 ? phrases.join('') : `${phrases.slice(0, -1).join(', ')} and ${phrases.at(-1)}`;
}

function placementSummary(frame) {
  const rect = Array.isArray(frame) ? frame : frame?.rect;
  if (!rect) {
    return '';
  }
  const [x, y, width, height] = rect;
  return `${frame.area ? `${words(frame.area)} area` : `${x}, ${y}`} · ${width} × ${height}`;
}

function elementName({ id, role }) {
  return role === 'title' ? 'Title' : words(id);
}

function sentence(text) {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function shorten(text, length) {
  return text.length <= length ? text : `${text.slice(0, length - 1).trimEnd()}…`;
}

function itemText(item) {
  return (item.text?.paragraphs || [])
    .map((paragraph) => paragraph.runs.map((run) => run.text).join(''))
    .filter((text) => text.length > 0)
    .join(' / ');
}

// #region Rendering

// What stays open as the panel redraws: the paragraphs unfolded, and how far it was scrolled, for one selection.
const inspector = { key: null, open: new Set() };

function renderInspector() {
  // The toolbar counts the slide's issues with the inspector closed too.
  loadFindings();
  if (!state.inspector) {
    return;
  }
  const panel = document.getElementById('inspector-panel');
  const selection = state.selected;
  const group = selectedIndexes().length > 1 ? state.group : null;
  const key = state.view === 'theme' ? `theme ${themeDetail?.kind}:${themeDetail?.name}`
    : group ? `group ${group.map((item) => `${item.owner}/${item.id}`).join(' ')}`
      : selection ? `${selection.slide}/${selection.owner}/${selection.id}` : `slide ${state.current}`;
  const scroll = key === inspector.key ? panel.scrollTop : 0;
  if (key !== inspector.key) {
    inspector.key = key;
    inspector.open.clear();
  }
  panel.replaceChildren();
  const item = selection && currentSlide()?.items?.[selection.index];
  if (state.view === 'theme') {
    renderThemePanel(panel);
  } else if (group) {
    renderGroupPanel(group, panel);
  } else if (!selection) {
    if (state.layout) {
      renderLayoutPanel(panel);
    } else {
      renderSlidePanel(currentSlide(), panel);
    }
  } else if (item?.kind === 'area') {
    renderAreaPanel(item, panel);
  } else if (!state.detail) {
    element('p', 'hint', panel).textContent = 'Loading…';
  } else {
    renderElementPanel(state.detail, selection, panel);
  }
  panel.scrollTop = scroll;
}

function renderGroupPanel(group, panel) {
  const slide = currentSlide();
  renderHeader(panel, { chips: [['Selection', 'slide']], title: `${group.length} elements` });
  const node = section(panel, null, 'elements');
  for (const { index } of group) {
    elementRow(slide, index, new Set(), node);
  }
  element('p', 'hint', panel).textContent =
    'Drag or nudge them together, or press Delete to remove them. Shift- or Ctrl-click one to leave it out.';
}

// A layout on the slide view: what moving its parts writes, and how many slides follow.
function renderLayoutPanel(panel) {
  const layout = state.themeView.layouts.find((candidate) => candidate.id === state.layout);
  renderHeader(panel, {
    chips: [['Layout', 'layout']],
    title: layout.name || words(layout.id),
    meta: [`Used by ${countLabel(layout.slides.length, 'slide')} · `,
      copyTag({ file: layout.file }, 'Copy path', 'link', null), ' · ', overlaysToggle()]
  });
  fact('Drag or nudge the title, placeholders, areas, and elements. Each edit writes the file that sets the value: ' +
    'this layout for its own, theme.yaml for what it inherits from the theme.', section(panel, null), 'hint');
  const items = layout.stage.items.map((item, index) => ({ item, index }));
  const list = (title, entries) => {
    if (entries.length === 0) {
      return;
    }
    const node = section(panel, title, 'elements');
    for (const { index } of entries) {
      elementRow(layout.stage, index, new Set(), node);
    }
  };
  list('Placeholders', items.filter(({ item }) => item.role));
  list('Elements', items.filter(({ item }) => !item.role && item.kind !== 'area'));
  list('Areas', items.filter(({ item }) => item.kind === 'area'));
}

function overlaysToggle() {
  const button = element('button', 'link');
  button.type = 'button';
  button.textContent = state.guides ? 'Hide overlays' : 'Show overlays';
  button.title = 'Show areas and placeholders (O)';
  button.setAttribute('aria-pressed', String(state.guides));
  button.addEventListener('click', () => toggle('guides'));
  return button;
}

function renderAreaPanel(item, panel) {
  const origin = originOf(item.edit.file);
  renderHeader(panel, {
    chips: [[origin.label, origin.level]],
    title: `${words(item.id)} area`,
    meta: [copyTag(origin, origin.file.split('/').pop(), 'file-link', null)]
  });
  fact(`[${item.frame.join(', ')}]`, section(panel, 'Frame'));
}

// The checks need the slide measured headless, so they arrive after the rest, once per slide and version.
async function loadFindings() {
  if (state.layout) {
    return;
  }
  const slide = currentSlide();
  const key = slide && `${state.deck.version}:${slide.id}`;
  if (!slide || state.findings?.key === key) {
    return;
  }
  state.findings = { key, list: null };
  renderToolbar();
  const response = await fetch(`api/findings?slide=${encodeURIComponent(slide.id)}`, { cache: 'no-store' });
  const list = response.ok ? await response.json() : null;
  if (state.findings.key === key) {
    state.findings.list = list;
    renderInspector();
    renderToolbar();
  }
}

function renderHeader(panel, { chips, title, quote, meta }) {
  const header = element('header', 'inspector-head', panel);
  const eyebrow = element('div', 'eyebrow', header);
  for (const [text, kind] of chips) {
    element('span', `chip ${kind}`, eyebrow).textContent = text;
  }
  element('h2', null, header).textContent = title;
  if (quote) {
    element('p', 'quote', header).textContent = shorten(quote, 160);
  }
  if (meta) {
    const line = element('div', 'meta', header);
    line.append(...meta);
  }
}

function section(panel, title, className = '') {
  const node = element('section', `inspector-section ${className}`.trim(), panel);
  if (title) {
    element('h3', null, node).textContent = title;
  }
  return node;
}

function originTag(origin, parent) {
  return copyTag(origin, origin.label, `origin-tag level-${origin.level}`, parent);
}

// A label that copies the file and line an origin names, or a plain one for an origin with no file.
function copyTag(origin, label, className, parent) {
  const tag = element(origin?.file ? 'button' : 'span', className, parent);
  tag.textContent = label;
  if (!origin?.file) {
    return tag;
  }
  tag.type = 'button';
  const where = origin.line ? `${origin.file}:${origin.line}` : origin.file;
  tag.title = `${where}${origin.key ? ` · ${origin.key}` : ''}\nClick to copy the path`;
  tag.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(where);
      tag.textContent = 'Copied';
      setTimeout(() => {
        tag.textContent = label;
      }, 1200);
    } catch {
      notice(where);
    }
  });
  return tag;
}


// #region Element

const ICONS = {
  text: '<path d="M4 5h12M10 5v11M7 16h6"/>',
  shape: '<rect x="3" y="4" width="14" height="12" rx="3"/>',
  line: '<path d="M4 16L16 4"/>',
  picture: '<rect x="3" y="4" width="14" height="12" rx="1.5"/><circle cx="7.5" cy="8.5" r="1.5"/>' +
    '<path d="M3 14l4-3 3 2 3-4 4 4"/>',
  chart: '<path d="M4 16V9M8 16V5M12 16v-6M16 16V7M3 16.5h14"/>',
  table: '<rect x="3" y="4" width="14" height="12" rx="1.5"/><path d="M3 8h14M3 12h14M8 4v12"/>',
  html: '<path d="M7 6l-4 4 4 4M13 6l4 4-4 4"/>',
  placement: '<path d="M10 3v14M3 10h14M8 5l2-2 2 2M8 15l2 2 2-2M5 8l-2 2 2 2M15 8l2 2-2 2"/>',
  typeface: '<path d="M3 15l4-10 4 10M4.5 11.5h5M13 9.5c.6-.8 1.4-1 2.2-1 1.2 0 1.8.7 1.8 1.8V15M17 12.2c-2.8 0-4.2.6-4.2 ' +
    '1.6 0 .8.6 1.3 1.5 1.3 1.3 0 2.7-1 2.7-2.9"/>',
  paragraphs: '<path d="M12 4v12M15 4v12M15 4H9.5a3.5 3.5 0 0 0 0 7H12"/>',
  data: '<ellipse cx="10" cy="5" rx="6" ry="2.5"/><path d="M4 5v10c0 1.4 2.7 2.5 6 2.5s6-1.1 6-2.5V5M4 10c0 1.4 2.7 2.5 ' +
    '6 2.5s6-1.1 6-2.5"/>',
  labels: '<path d="M4 4h8l4 4v8H4z"/><path d="M7 10h6M7 13h4"/>',
  axes: '<path d="M4 3v13h13M4 16l4-5 3 2 5-6"/>',
  legend: '<rect x="3" y="6" width="3" height="3" rx=".5"/><rect x="3" y="11" width="3" height="3" rx=".5"/>' +
    '<path d="M9 7.5h8M9 12.5h8"/>',
  bars: '<path d="M5 16V6M10 16V9M15 16V4"/>',
  columns: '<path d="M4 4v12M10 4v12M16 4v12"/>',
  rows: '<path d="M3 6h14M3 10h14M3 14h14"/>',
  cell: '<rect x="4" y="5" width="12" height="10" rx="1"/><path d="M4 10h12M10 5v10" opacity=".4"/>',
  numbers: '<path d="M8 12l4-4M7 9.5L5.5 11a2.5 2.5 0 0 0 3.5 3.5l1.5-1.5M13 10.5l1.5-1.5A2.5 2.5 0 0 0 11 5.5L9.5 7"/>',
  more: '<circle cx="5" cy="10" r="1"/><circle cx="10" cy="10" r="1"/><circle cx="15" cy="10" r="1"/>'
};

function icon(name, parent) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 20 20');
  svg.setAttribute('aria-hidden', 'true');
  svg.classList.add('icon');
  svg.innerHTML = ICONS[name] || ICONS.more;
  parent.append(svg);
  return svg;
}

// Sections in the order they read; charts and tables fold what matters less than their content.
const GROUPS = {
  placement: { title: 'Placement', icon: 'placement' },
  picture: { title: 'Picture', icon: 'picture' },
  chart: { title: 'Chart file', icon: 'data' },
  labels: { title: 'Data labels', icon: 'labels' },
  axes: { title: 'Axes', icon: 'axes', folded: true },
  legend: { title: 'Legend and series', icon: 'legend', folded: true },
  bars: { title: 'Bars', icon: 'bars', folded: true },
  table: { title: 'Table file', icon: 'data' },
  columns: { title: 'Columns', icon: 'columns' },
  rows: { title: 'Rows', icon: 'rows' },
  shape: { title: 'Shape', icon: 'shape' },
  line: { title: 'Line', icon: 'line' },
  text: { title: 'Text', icon: 'typeface' },
  more: { title: 'More', icon: 'more' }
};

function folded(kind, group) {
  return Boolean(GROUPS[group].folded) ||
    ((kind === 'chart' || kind === 'table') && (group === 'placement' || group === 'text'));
}

function propertyOf(detail, key) {
  return detail.properties.find((property) => property.key === key);
}

function renderElementPanel(detail, selection, panel) {
  renderElementHeader(detail, panel);
  const item = currentSlide()?.items?.[selection.index];
  if (!reviewing()) {
    renderPaint(item, panel);
  }
  const where = `${detail.slide}/${detail.id}`;
  renderIssues(state.findings?.list?.filter((finding) => finding.where === where), panel);
  renderItemChanges(selection, panel);
  const node = selectedNode();
  const box = node ? measureBox(node) : null;
  renderGlance(detail, { item, node, box, selection }, panel);
  renderNumbers(detail.paragraphs || [], panel);
  const home = originOf(detail.origin).file;
  if (detail.cells) {
    renderCell(detail, selection, home, panel);
  }
  if (item?.chart) {
    renderPoint(item.chart, selection, panel);
  }
  renderSections(detail, home, panel);
  if (hasText(detail)) {
    const count = detail.paragraphs.length;
    const body = fold(panel, {
      key: 'paragraphs',
      title: count === 1 ? 'Paragraph' : 'Paragraphs',
      icon: 'paragraphs',
      summary: { text: `${count}` }
    });
    if (body) {
      renderParagraphs(detail.paragraphs, selection.paragraph, box ? box.paragraphs : null, home, body);
    }
  }
}

function renderElementHeader(detail, panel) {
  const header = element('header', 'element-head', panel);
  const badge = element('span', `kind-icon kind-${detail.kind}`, header);
  icon(detail.kind === 'shape' && hasText(detail) ? 'text' : detail.kind, badge);
  element('h2', null, header).textContent = elementName(detail);
  const line = element('div', 'element-kind', header);
  element('span', null, line).textContent = kindSummary(detail);
  const origin = originOf(detail.origin);
  if (detail.owner === 'theme' || detail.owner === 'layout') {
    element('span', 'chip owner', line).textContent = origin.label;
  }
  copyTag(origin, `${origin.file.split('/').pop()}:${origin.line}`, 'file-link', line);
}

// A row as the sections show it: set by the element's own file, or inherited and naming where from.
function sectionRow(detail, group, property, home) {
  const origin = property.origin ? originOf(property.origin) : null;
  const own = Boolean(origin) && (origin.file === home || origin.level === 'chart' || origin.level === 'table');
  const key = detail.kind === 'chart' && (group === 'labels' || group === 'text')
    ? property.key.replace(/^(data_labels|text)\./, '')
    : property.key;
  return {
    key: property.key,
    label: propertyLabel(key),
    ...propertyValue(property),
    origin,
    own,
    source: own ? null : sourceOf(property.origin, detail.kind, property.key)
  };
}

// A section whose head names it, folding its body away to a summary; the fold is kept per kind and section.
function fold(panel, { key, title, icon: name, summary, folded: closed = false, className = '' }) {
  const open = state.folds[key] ?? !closed;
  const node = element('section', `inspector-section fold${open ? ' open' : ''} ${className}`.trim(), panel);
  const head = element('button', 'fold-head', node);
  head.type = 'button';
  head.setAttribute('aria-expanded', String(open));
  icon(name, head);
  element('span', 'fold-title', head).textContent = title;
  if (summary?.text && !open) {
    const text = element('span', 'fold-summary', head);
    swatches(summary.colors, text);
    text.append(summary.text);
  }
  head.addEventListener('click', () => {
    state.folds[key] = !open;
    save();
    renderInspector();
  });
  return open ? element('div', 'fold-body', node) : null;
}

function swatches(colors, parent) {
  for (const color of colors || []) {
    element('span', 'swatch', parent).style.background = color;
  }
}

function groupSummary(group, rows) {
  if (group === 'text') {
    const pick = (label) => rows.find((row) => row.label === label);
    const font = [pick('Font'), pick('Size')].filter(Boolean).map((row) => row.text).join(' ');
    const color = pick('Color');
    return { text: [font, color?.text].filter(Boolean).join(' · '), colors: color?.colors?.slice(0, 1) };
  }
  const value = (key) => rows.find((row) => row.key === key);
  if (group === 'shape' && value('fill')) {
    return { text: value('fill').text, colors: value('fill').colors };
  }
  if (group === 'axes') {
    const shown = (key) => value(key)?.text === 'Yes';
    return {
      text: sentence([
        value('category_axis.visible') && `categories ${shown('category_axis.visible') ? 'shown' : 'hidden'}`,
        shown('category_axis.visible') && value('category_axis.labels') && !shown('category_axis.labels') &&
          'no category labels',
        value('value_axis.visible') && `values ${shown('value_axis.visible') ? 'shown' : 'hidden'}`,
        value('value_axis.gridlines') && (shown('value_axis.gridlines') ? 'gridlines' : 'no gridlines'),
        shown('value_axis.reverse') && 'values reversed',
        shown('secondary_axis.reverse') && 'secondary reversed'
      ].filter(Boolean).join(', '))
    };
  }
  if (group === 'legend') {
    const colors = value('series_colors')?.colors || [];
    return {
      text: [value('legend') && `Legend ${value('legend').text.toLowerCase()}`, colors.length > 0 && `${colors.length} colors`]
        .filter(Boolean).join(' · '),
      colors
    };
  }
  if (group === 'bars') {
    return { text: rows.map((row) => `${row.label} ${row.text}`).join(' · ') };
  }
  return { text: rows.slice(0, 3).map((row) => row.text).join(' · ') };
}

function renderSections(detail, home, panel) {
  const groups = new Map();
  for (const property of detail.properties) {
    const group = groupOf(detail.kind, property.key, hasText(detail));
    groups.set(group, [...(groups.get(group) || []), property]);
  }
  for (const group of Object.keys(GROUPS)) {
    const properties = groups.get(group) || [];
    if (group === 'placement' && properties.length > 0) {
      renderPlacement(detail, properties, home, panel);
    } else if (group === 'columns' && detail.cells) {
      renderColumns(detail, properties, home, panel);
    } else if (group === 'rows' && detail.cells) {
      renderTableRows(detail, properties, home, panel);
    } else if (properties.length > 0) {
      const rows = properties.map((property) => sectionRow(detail, group, property, home));
      // Text values an element without text only inherits draw nothing.
      if (group === 'text' && !hasText(detail) && !['chart', 'table'].includes(detail.kind) &&
        !rows.some((row) => row.own)) {
        continue;
      }
      const key = `${detail.kind}.${group}`;
      const title = group === 'shape' && detail.kind !== 'shape' ? 'Fill and outline' : GROUPS[group].title;
      const body = fold(panel, {
        key, title, icon: GROUPS[group].icon, summary: groupSummary(group, rows), folded: folded(detail.kind, group)
      });
      if (body) {
        renderOwnAndInherited(rows, key, body);
      }
    }
  }
}

// What the element sets, then what it inherits folded under one line naming where from. A section with nothing of
// its own shows its inherited values unfolded, since the fold would be all there is.
function renderOwnAndInherited(rows, key, body) {
  const own = rows.filter((row) => row.own);
  const inherited = rows.filter((row) => !row.own);
  renderValues(own, body);
  if (inherited.length === 0) {
    return;
  }
  const open = state.folds[`${key}.inherited`] ?? own.length === 0;
  const toggle = element('button', 'inherited-toggle', body);
  toggle.type = 'button';
  toggle.setAttribute('aria-expanded', String(open));
  const count = own.length > 0 ? `${inherited.length} more` : `${inherited.length} ${inherited.length === 1
    ? 'value' : 'values'}`;
  toggle.textContent = `${count} from ${sourcesPhrase(inherited.map((row) => row.source))}`;
  toggle.addEventListener('click', () => {
    state.folds[`${key}.inherited`] = !open;
    save();
    renderInspector();
  });
  if (open) {
    renderValues(inherited, element('div', 'inherited', body));
  }
}

function renderValues(rows, parent) {
  if (rows.length === 0) {
    return;
  }
  const list = element('dl', 'values', parent);
  for (const row of rows) {
    const entry = element('div', `value-row${row.own ? ' own' : ''}${row.problem ? ' problem' : ''}`, list);
    element('dt', null, entry).textContent = row.label;
    const value = element('dd', null, entry);
    const text = element('span', 'value', value);
    swatches(row.colors, text);
    text.append(row.text);
    if (row.note) {
      element('span', 'note', value).textContent = row.note;
    }
    sourceTag(row, entry);
  }
}

// An own value shows its line, an inherited one the style or level it comes from; both copy the path.
function sourceTag(row, parent) {
  if (row.own) {
    return row.origin
      ? copyTag(row.origin, row.origin.line ? `:${row.origin.line}` : row.origin.file.split('/').pop(), 'line-tag', parent)
      : element('span', null, parent);
  }
  return copyTag(row.origin, row.source.label, `source-tag${row.source.style ? ' style' : ''}`, parent);
}

// #region Summary cards

function renderGlance(detail, drawn, panel) {
  if (hasText(detail)) {
    textGlance(detail, drawn, panel);
  } else if (detail.kind === 'chart' && drawn.item?.chart) {
    chartGlance(detail, drawn, panel);
  } else if (detail.kind === 'table' && detail.cells) {
    tableGlance(detail, drawn, panel);
  } else if (detail.kind === 'picture' && drawn.item?.picture) {
    pictureGlance(detail, drawn.item, panel);
  }
}

function fact(text, parent, className = 'fact') {
  const node = element('span', className, parent);
  node.textContent = text;
  return node;
}

function textGlance(detail, { node, box }, panel) {
  const card = element('div', 'glance', panel);
  const text = detail.paragraphs.map((paragraph) => paragraph.text).filter(Boolean).join(' / ');
  if (text) {
    element('p', 'glance-quote', card).textContent = shorten(text, 160);
  }
  const facts = element('div', 'facts', card);
  const style = propertyOf(detail, 'style');
  if (style) {
    fact(style.display, facts, 'chip style');
  }
  const font = [propertyOf(detail, 'font'), propertyOf(detail, 'size')].filter(Boolean)
    .map((property) => propertyValue(property).text).join(' ');
  if (font) {
    fact(font, facts, 'fact strong');
  }
  const color = propertyOf(detail, 'color');
  if (color) {
    const shown = propertyValue(color);
    const node = fact('', facts);
    swatches(shown.colors, node);
    node.append(shown.text);
  }
  if (box) {
    fact(`${box.lines} ${box.lines === 1 ? 'line' : 'lines'}`, facts);
  }
  if (box && node) {
    fillMeter(node, box, card);
  }
}

// How much of its box the text takes; fitted or overflowing text says what became of it instead.
function fillMeter(node, box, card) {
  const meter = element('div', 'meter', card);
  const bar = element('i', null, meter);
  const label = element('div', 'meter-label', card);
  if (box.fit === 'shrink' || box.fit === 'grow') {
    bar.style.width = box.fit === 'shrink' ? `${Math.round(box.scale * 100)}%` : '100%';
    fact(box.fit === 'shrink' ? `Shrunk to ${Math.round(box.scale * 100)}% to fit` : `Grown to ${box.height} pt tall`,
      label);
  }
  if (box.overflow > 0) {
    card.classList.add('over');
    bar.style.width = '100%';
    fact(`Runs ${box.overflow} pt past the box`, label);
  }
  if (box.fit === 'shrink' || box.fit === 'grow' || box.overflow > 0) {
    return;
  }
  const use = textUse(node);
  if (!use) {
    meter.remove();
    label.remove();
    return;
  }
  const share = Math.min(100, Math.round(use.text / use.room * 100));
  bar.style.width = `${share}%`;
  fact(`Text fills ${share}% of the box`, label);
  fact(`${Math.max(0, Math.round(use.room - use.text))} pt spare`, label);
  if (box.tight > 0.5) {
    fact(`Barely fits across: 1% wider wraps ${box.tight} pt past the box`, label).classList.add('problem');
  }
}

// The text's height and the room inside the box's insets, in points.
function textUse(node) {
  const box = node.querySelector(':scope > .text-box');
  const flow = box?.firstChild;
  const stage = node.closest('.stage');
  if (!flow || !stage) {
    return null;
  }
  const scale = stage.getBoundingClientRect().width / stage.offsetWidth;
  const style = getComputedStyle(box);
  const room = box.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
  return room > 0 ? { text: flow.getBoundingClientRect().height / scale, room } : null;
}

const GLANCE_SERIES = 8;
const GLANCE_CATEGORIES = 4;

function chartValue(value) {
  return value === null || value === undefined ? '—' : Number(value).toLocaleString('en-US');
}

// The chart's data as a small table, each series in its colour; long charts show their latest categories, and
// clicking a value picks it.
function chartGlance(detail, { item, selection }, panel) {
  const chart = item.chart;
  const card = element('div', 'glance', panel);
  const series = chart.series || [];
  const categories = chart.categories || [];
  const facts = element('div', 'facts', card);
  fact(`${series.length} series × ${categories.length} ${categories.length === 1 ? 'category' : 'categories'}`, facts,
    'fact strong');
  const data = propertyOf(detail, 'data');
  if (data) {
    copyTag(originOf(data.origin), data.display, 'file-link', facts);
  }
  const shown = categories.slice(-GLANCE_CATEGORIES);
  const skipped = categories.length - shown.length;
  const grid = element('div', 'series-table', card);
  grid.style.gridTemplateColumns = `minmax(0, 1fr) repeat(${shown.length}, auto)`;
  fact('Series', grid, 'head');
  for (const category of shown) {
    fact(category, grid, 'head number');
  }
  series.slice(0, GLANCE_SERIES).forEach((entry, index) => {
    const name = fact('', grid, 'name');
    swatches([chart.colors?.[index % chart.colors.length]].filter(Boolean), name);
    name.append(entry.name);
    entry.values.slice(skipped).forEach((value, column) => {
      const point = skipped + column;
      const picked = selection.point?.series === index && selection.point?.point === point;
      const node = element('button', `number${picked ? ' picked' : ''}`, grid);
      node.type = 'button';
      node.textContent = chartValue(value);
      node.title = `${entry.name}, ${categories[point]}`;
      node.addEventListener('click', () => {
        if (!reviewing()) {
          selectItem({ ...state.selected, point: { series: index, point } });
        }
      });
    });
  });
  const notes = [
    skipped > 0 && `the last ${shown.length} of ${categories.length} categories`,
    series.length > GLANCE_SERIES && `${series.length - GLANCE_SERIES} more series`
  ].filter(Boolean);
  if (notes.length > 0) {
    element('div', 'meter-label', card).textContent = sentence(notes.join(', and '));
  }
  if (!selection.point) {
    fact('Click a value, here or on the slide', element('div', 'meter-label', card));
  }
}

function headerRows(detail) {
  return Number(propertyOf(detail, 'header_rows')?.value ?? 0);
}

function spanEnd(cells, start, span) {
  return Math.max(0, ...cells.map((cell) => cell[start] + cell[span] - 1));
}

// A map of the table's cells, header, styled rows, and merges drawn to scale; clicking one picks it.
function tableGlance(detail, { item, box, selection }, panel) {
  const card = element('div', 'glance', panel);
  const headers = headerRows(detail);
  const rows = spanEnd(detail.cells, 'row', 'rows');
  const columns = spanEnd(detail.cells, 'column', 'columns');
  const facts = element('div', 'facts', card);
  const style = propertyOf(detail, 'style');
  if (style) {
    fact(style.display, facts, 'chip style');
  }
  const body = rows - headers;
  fact(`${body} ${body === 1 ? 'row' : 'rows'}${headers > 0 ? ` + ${headers} header` : ''} × ${columns} ${columns === 1
    ? 'column' : 'columns'}`, facts, 'fact strong');
  const data = propertyOf(detail, 'data');
  if (data) {
    copyTag(originOf(data.origin), data.display, 'file-link', facts);
  }
  const map = element('div', 'table-map', card);
  const widths = item?.table?.widths;
  map.style.gridTemplateColumns = widths?.length === columns
    ? widths.map((width) => `${width}fr`).join(' ')
    : `repeat(${columns}, 1fr)`;
  for (const cell of detail.cells) {
    const styled = cell.row > headers && cell.properties.some((property) => property.key === 'row_style');
    const picked = selection.cell && selection.cell.row >= cell.row && selection.cell.row < cell.row + cell.rows &&
      selection.cell.column >= cell.column && selection.cell.column < cell.column + cell.columns;
    const classes = ['map-cell', cell.row <= headers && 'header', styled && 'styled',
      (cell.rows > 1 || cell.columns > 1) && 'merged', picked && 'picked'].filter(Boolean).join(' ');
    const node = element('button', classes, map);
    node.type = 'button';
    node.style.gridRow = `${cell.row} / span ${cell.rows}`;
    node.style.gridColumn = `${cell.column} / span ${cell.columns}`;
    node.title = `Row ${cell.row}, column ${cell.column}${cell.text ? `: ${cell.text}` : ''}`;
    node.addEventListener('click', () => {
      if (!reviewing()) {
        selectItem({ ...state.selected, cell: { row: cell.row, column: cell.column } });
      }
    });
  }
  const label = element('div', 'meter-label', card);
  if (box) {
    const height = item?.frame?.[3];
    const drawn = fact(box.overflow > 0 ? `Runs ${box.overflow} pt past the frame`
      : `Drawn ${box.height}${height ? ` of ${height}` : ''} pt`, label);
    drawn.classList.toggle('problem', box.overflow > 0);
  }
  if (!selection.cell) {
    fact('Click a cell, here or on the slide', label);
  }
}

const FITS = { contain: 'Contained', cover: 'Covering', stretch: 'Stretched' };

function pictureGlance(detail, item, panel) {
  const card = element('div', 'glance', panel);
  const thumb = element('div', 'thumb', card);
  const image = element('img', null, thumb);
  image.src = item.picture;
  image.alt = '';
  const facts = element('div', 'facts', card);
  const picture = propertyOf(detail, 'picture');
  if (picture) {
    copyTag(originOf(picture.origin), picture.display.split('/').pop(), 'file-link', facts);
  }
  const fit = propertyOf(detail, 'fit');
  const [, , width, height] = item.frame || [];
  if (fit && width) {
    fact(`${FITS[fit.display] || words(fit.display)} in ${width} × ${height}`, facts);
  }
  const alt = propertyOf(detail, 'alt');
  fact(alt ? `Alt text: “${shorten(alt.display, 60)}”` : 'No alt text', facts);
}

// #endregion

// #region Sections

// Where the element sits as four numbers, with what else places it beneath.
function renderPlacement(detail, properties, home, panel) {
  const frame = properties.find((property) => property.key === 'frame');
  const body = fold(panel, {
    key: `${detail.kind}.placement`,
    title: 'Placement',
    icon: 'placement',
    summary: { text: placementSummary(frame?.value) },
    folded: folded(detail.kind, 'placement')
  });
  if (!body) {
    return;
  }
  const rect = Array.isArray(frame?.value) ? frame.value : frame?.value?.rect;
  if (rect) {
    const geometry = element('div', 'geometry', body);
    ['X', 'Y', 'W', 'H'].forEach((name, index) => {
      const cell = element('div', 'geometry-value', geometry);
      element('span', null, cell).textContent = name;
      cell.append(`${rect[index]}`);
    });
    const foot = element('div', 'geometry-foot', body);
    const area = frame.value.area;
    fact(area ? `Points, in the ${words(area).toLowerCase()} area` : 'Points', foot);
    sourceTag(sectionRow(detail, 'placement', frame, home), foot);
  }
  const rest = properties.filter((property) => property !== frame);
  renderOwnAndInherited(rest.map((property) => sectionRow(detail, 'placement', property, home)),
    `${detail.kind}.placement`, body);
}

function columnCells(detail, column) {
  return detail.cells.filter((cell) => cell.column === column && cell.columns === 1 && cell.row > headerRows(detail));
}

// What a column sets for its cells, read off the first body cell that carries it.
function columnValue(detail, column, key) {
  const scope = `columns[${column}].${key}`;
  for (const cell of columnCells(detail, column)) {
    const property = cell.properties.find((entry) => entry.key === key && originOf(entry.origin).key === scope);
    if (property) {
      return property;
    }
  }
  return null;
}

function renderColumns(detail, properties, home, panel) {
  const widths = new Map(properties.filter((property) => /^columns\[\d+\]\.width$/.test(property.key))
    .map((property) => [Number(/\d+/.exec(property.key)[0]), property]));
  const columns = spanEnd(detail.cells, 'column', 'columns');
  const total = [...widths.values()].reduce((sum, property) => sum + Number(property.value), 0);
  const body = fold(panel, {
    key: 'table.columns',
    title: 'Columns',
    icon: 'columns',
    summary: { text: widths.size === columns ? `${total} pt across` : `${columns}` }
  });
  if (!body) {
    return;
  }
  const headers = headerRows(detail);
  const table = element('table', 'column-table', body);
  const head = element('tr', null, element('thead', null, table));
  for (const title of ['#', headers > 0 ? 'Header' : '', 'Width', 'Align', 'Format']) {
    element('th', null, head).textContent = title;
  }
  const rows = element('tbody', null, table);
  for (let column = 1; column <= columns; column++) {
    const row = element('tr', null, rows);
    element('td', 'number', row).textContent = `${column}`;
    const header = headers > 0 && detail.cells.find((cell) => cell.row === 1 && cell.column === column);
    element('td', null, row).textContent = header ? header.text : '';
    const width = widths.get(column);
    const shown = element('td', 'number', row);
    if (width) {
      const tag = copyTag(width.origin ? originOf(width.origin) : null, `${Math.round(Number(width.value) * 10) / 10}`,
        'plain-tag', shown);
      tag.title ||= 'An even share of the width';
    } else {
      shown.textContent = 'Even';
    }
    for (const key of ['align', 'format']) {
      const property = columnValue(detail, column, key);
      const cell = element('td', null, row);
      if (property) {
        copyTag(originOf(property.origin), propertyValue(property).text, 'plain-tag', cell);
      } else {
        cell.textContent = '—';
      }
    }
  }
  const rest = properties.filter((property) => !/^columns\[\d+\]\.width$/.test(property.key));
  renderOwnAndInherited(rest.map((property) => sectionRow(detail, 'columns', property, home)), 'table.columns', body);
}

function rowRange(start, span) {
  return span > 1 ? `${start}–${start + span - 1}` : `${start}`;
}

function listed(items) {
  return items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;
}

// The header count, then the row styles and merges the cells carry.
function renderTableRows(detail, properties, home, panel) {
  const rows = properties.map((property) => sectionRow(detail, 'rows', property, home));
  const headers = headerRows(detail);
  const styles = new Map();
  for (const cell of detail.cells.filter((entry) => entry.row > headers)) {
    const style = cell.properties.find((property) => property.key === 'row_style');
    if (style) {
      const entry = styles.get(style.display) || { rows: new Set(), property: style };
      entry.rows.add(cell.row);
      styles.set(style.display, entry);
    }
  }
  const summary = [];
  for (const [name, { rows: numbers, property }] of styles) {
    const sorted = [...numbers].sort((a, b) => a - b);
    rows.push({
      ...sectionRow(detail, 'rows', property, home),
      label: `${words(name)} style`,
      text: `${sorted.length === 1 ? 'Row' : 'Rows'} ${listed(sorted.map(String))}`,
      note: undefined
    });
    summary.push(`${sorted.length} ${name}`);
  }
  const merges = detail.cells.filter((cell) => cell.rows > 1 || cell.columns > 1);
  if (merges.length > 0) {
    rows.push({
      label: 'Merged',
      text: listed(merges.map((cell) => `rows ${rowRange(cell.row, cell.rows)}, column ${rowRange(cell.column,
        cell.columns)}`)).replace(/^r/, 'R'),
      own: true
    });
    summary.push(`${merges.length} merged`);
  }
  if (rows.length === 0) {
    return;
  }
  const body = fold(panel, { key: 'table.rows', title: 'Rows', icon: 'rows', summary: { text: summary.join(' · ') } });
  if (body) {
    renderOwnAndInherited(rows, 'table.rows', body);
  }
}

// #endregion

function renderIssues(findings, panel, slide = null) {
  if (!findings || findings.length === 0) {
    return;
  }
  const node = section(panel, null, 'issues');
  const head = element('button', 'issues-toggle', element('h3', null, node));
  head.type = 'button';
  head.setAttribute('aria-expanded', String(!state.issuesHidden));
  head.title = state.issuesHidden ? 'Show the issues' : 'Hide the issues';
  head.textContent = 'Issues';
  for (const level of ['error', 'warning']) {
    const count = findings.filter((finding) => finding.level === level).length;
    if (count > 0) {
      element('span', `issues-count ${level}`, head).textContent = `${count} ${level}${count === 1 ? '' : 's'}`;
    }
  }
  head.addEventListener('click', () => {
    state.issuesHidden = !state.issuesHidden;
    save();
    renderInspector();
  });
  if (state.issuesHidden) {
    return;
  }
  for (const finding of findings) {
    const about = slide && finding.where !== finding.slide ? finding.where.slice(finding.slide.length + 1) : null;
    const index = about ? (slide.items || []).findIndex((item) => item.id === about) : -1;
    const entry = element(index >= 0 ? 'button' : 'div', `issue ${finding.level}`, node);
    if (index >= 0) {
      entry.type = 'button';
      entry.title = 'Select it';
      entry.addEventListener('click', () => pickItem(slide, index));
    }
    if (about) {
      element('span', 'issue-where', entry).textContent = words(about);
    }
    element('span', 'issue-message', entry).textContent = sentence(finding.message);
  }
}

function renderNumbers(paragraphs, panel) {
  const runs = paragraphs.flatMap((paragraph) => paragraph.runs).filter((run) => run.trace);
  if (runs.length === 0) {
    return;
  }
  const body = fold(panel, {
    key: 'numbers', title: 'Numbers from Libreta', icon: 'numbers', summary: { text: `${runs.length}` }
  });
  if (!body) {
    return;
  }
  const list = element('div', 'numbers', body);
  for (const run of runs) {
    numberRow(run.text, run.trace, list);
  }
}

function numberRow(text, target, parent) {
  const row = element('div', 'number-row', parent);
  element('span', 'number-value', row).textContent = text;
  const cell = element('span', 'number-cell', row);
  cell.textContent = `${words(target.line.split('.').pop())} · ${target.column}`;
  cell.title = `${target.line} in ${target.column}`;
  const open = element('button', 'link', row);
  open.type = 'button';
  open.textContent = 'Open in Libreta';
  open.addEventListener('click', () => trace(target));
}

// The clicked cell, with what it sets apart from the table; the map above picks one.
function renderCell(detail, selection, home, panel) {
  const cell = selection.cell && detail.cells.find((entry) =>
    selection.cell.row >= entry.row && selection.cell.row < entry.row + entry.rows &&
    selection.cell.column >= entry.column && selection.cell.column < entry.column + entry.columns);
  if (!cell) {
    return;
  }
  const across = [[cell.rows, 'rows'], [cell.columns, 'columns']]
    .filter(([count]) => count > 1)
    .map(([count, what]) => `${count} ${what}`);
  const body = fold(panel, {
    key: 'table.cell',
    title: `Row ${cell.row}, column ${cell.column}`,
    icon: 'cell',
    summary: { text: across.length > 0 ? `Merged across ${across.join(' and ')}` : cell.text },
    className: 'cell'
  });
  if (!body) {
    return;
  }
  element('p', 'glance-quote', body).textContent = cell.text || '(empty)';
  if (cell.trace) {
    numberRow(cell.text, cell.trace, element('div', 'numbers', body));
  }
  renderOwnAndInherited(cell.properties.map((property) => sectionRow(detail, 'cell', property, home)), 'table.cell',
    body);
  if (cell.paragraphs.length > 1) {
    renderParagraphs(cell.paragraphs, null, null, home, body);
  }
}

// The clicked value, with the book cell it comes from; the table above picks one.
function renderPoint(chart, selection, panel) {
  const series = selection.point && chart.series?.[selection.point.series];
  const point = selection.point?.point;
  if (!series || point >= series.values.length) {
    return;
  }
  const category = chart.categories?.[point];
  const value = chartValue(series.values[point]);
  const body = fold(panel, {
    key: 'chart.point',
    title: category === undefined ? series.name : `${series.name}, ${category}`,
    icon: 'cell',
    summary: { text: value },
    className: 'cell'
  });
  if (!body) {
    return;
  }
  const target = series.traces?.[point];
  if (target) {
    numberRow(value, target, element('div', 'numbers', body));
  } else {
    element('p', 'glance-quote', body).textContent = value;
  }
}

// Each paragraph on one line, unfolding to what it sets beyond the box: the clicked one opens by itself.
function renderParagraphs(paragraphs, selected, lineCounts, home, parent) {
  for (const paragraph of paragraphs) {
    const current = selected === paragraph.number - 1;
    const more = paragraph.properties.length > 0 || paragraph.runs.some(runSets);
    const open = more && (current || inspector.open.has(paragraph.number));
    const entry = element('div', `paragraph-entry${current ? ' current' : ''}${open ? ' open' : ''}`, parent);
    const row = element(more ? 'button' : 'div', 'paragraph-row', entry);
    if (more) {
      row.type = 'button';
      row.setAttribute('aria-expanded', String(open));
      row.addEventListener('click', () => {
        if (open) {
          inspector.open.delete(paragraph.number);
        } else {
          inspector.open.add(paragraph.number);
        }
        renderInspector();
      });
    }
    const bullet = paragraph.properties.find((property) => property.key === 'bullet');
    element('span', 'marker', row).textContent = bullet ? bullet.display : '¶';
    element('span', 'paragraph-text', row).textContent = paragraph.text || '(empty)';
    const tags = element('span', 'paragraph-tags', row);
    if (paragraph.style) {
      element('span', 'chip style', tags).textContent = paragraph.style;
    }
    if (paragraph.level > 1) {
      element('span', 'chip', tags).textContent = `Level ${paragraph.level}`;
    }
    const lines = lineCounts ? lineCounts[paragraph.number - 1] : undefined;
    if (lines !== undefined) {
      element('span', 'lines', tags).textContent = `${lines} ${lines === 1 ? 'line' : 'lines'}`;
    }
    if (open) {
      renderParagraphDetail(paragraph, home, entry);
    }
    if (current) {
      entry.scrollIntoView({ block: 'nearest' });
    }
  }
}

function runSets(run) {
  return run.properties.length > 0 || Boolean(run.styles) || Boolean(run.field);
}

function renderParagraphDetail(paragraph, home, entry) {
  const body = element('div', 'paragraph-body', entry);
  const rows = (properties) => properties.map((property) => sectionRow({ kind: 'text' }, 'text', property, home));
  renderValues(rows(paragraph.properties), body);
  for (const run of paragraph.runs.filter(runSets)) {
    const entryRun = element('div', 'run-entry', body);
    const label = element('div', 'run-label', entryRun);
    element('span', 'run-text', label).textContent = `“${run.text}”`;
    for (const style of run.styles || []) {
      element('span', 'chip style', label).textContent = style;
    }
    if (run.field) {
      element('span', 'chip', label).textContent = `\${${run.field}}`;
    }
    renderValues(rows(run.properties), entryRun);
  }
}

// #endregion

// #region Slide

function renderSlidePanel(slide, panel) {
  if (!slide) {
    return;
  }
  const meta = [];
  if (slide.layout) {
    const layout = element('button', 'link');
    layout.type = 'button';
    layout.textContent = `${words(slide.layout)} layout`;
    layout.title = 'Show the layout in the theme view';
    layout.addEventListener('click', () => showLayout(slide.layout));
    meta.push(layout, ' · ');
  }
  // Overlays draw on the one slide, not the grid's.
  if (state.view === 'slide') {
    meta.push(overlaysToggle());
  }
  if (meta.at(-1) === ' · ') {
    meta.pop();
  }
  renderHeader(panel, {
    chips: [['Slide', 'slide'], [`${slide.number} of ${state.deck.slides.length}`, 'muted']],
    title: slide.title || words(slide.id),
    meta: meta.length > 0 ? meta : null
  });
  if (state.findings?.list === null) {
    element('p', 'hint checking', panel).textContent = 'Checking the slide…';
  } else if (state.findings?.list?.length === 0) {
    element('p', 'all-clear', panel).textContent = 'No issues on this slide.';
  }
  renderIssues(state.findings?.list, panel, slide);
  renderSlideChanges(slide, panel);
  renderElementList(slide, panel);
}

// The slide's elements, its own first, as a list to pick from; hovering one outlines it on the slide.
function renderElementList(slide, panel) {
  const items = (slide.items || []).map((item, index) => ({ item, index }));
  if (items.length === 0) {
    return;
  }
  const own = items.filter(({ item }) => item.owner !== 'theme' && item.owner !== 'layout');
  const inherited = items.filter(({ item }) => item.owner === 'theme' || item.owner === 'layout');
  const issues = new Set((state.findings?.list || []).map((finding) => finding.where));
  const list = (title, entries) => {
    if (entries.length === 0) {
      return;
    }
    const node = section(panel, title, 'elements');
    for (const { index } of entries) {
      elementRow(slide, index, issues, node);
    }
  };
  list('On this slide', own);
  list('From the layout and theme', inherited);
  element('p', 'hint', panel).textContent = 'Click an element, here or on the slide, to inspect it.';
}

function elementRow(slide, index, issues, parent) {
  const item = slide.items[index];
  const row = element('button', 'element-row', parent);
  row.type = 'button';
  element('span', `chip kind-${item.kind}`, row).textContent = kindName(item);
  element('span', 'element-name', row).textContent = elementName(item);
  element('span', 'element-text', row).textContent = itemText(item);
  if (issues.has(`${slide.id}/${item.id}`)) {
    element('span', 'issue-dot', row).title = 'Has issues';
  }
  row.addEventListener('click', () => pickItem(slide, index));
  row.addEventListener('pointerenter', () => hoverItem(index, true));
  row.addEventListener('pointerleave', () => hoverItem(index, false));
}

function hoverItem(index, on) {
  document.querySelector(`#slide-frame .item[data-index="${index}"]`)?.classList.toggle('hovered', on);
}

function pickItem(slide, index) {
  if (reviewing()) {
    return;
  }
  const item = slide.items[index];
  if (state.view !== 'slide') {
    setView('slide');
  }
  hoverItem(index, false);
  selectItem({ slide: slide.id, index, id: item.id, owner: item.owner, paragraph: null, cell: null });
}

// #endregion

// #endregion

if (typeof module !== 'undefined') {
  module.exports = {
    changeLabel, colorName, elementName, groupOf, kindName, kindSummary, originOf, placementSummary, propertyLabel,
    propertyValue, sourceOf, sourcesPhrase
  };
}