'use strict';

// The theme view: a sheet of the theme's palette, fonts, and layouts, then its text, table, and chart styles and its
// formats. Each value shows how it looks and how much the slides use it; clicking one shows it in the inspector, with
// the file and line that sets it and the slides using it. It only reads; the theme's files are where it changes.

const SHEET_LAYOUT_WIDTH = 220;
const SPECIMEN = 'Revenue grew 12% to $1,234.5m in 2026';
const SLOT_GROUPS = [
  ['Text and background', ['dark1', 'light1', 'dark2', 'light2']],
  ['Accents', ['accent1', 'accent2', 'accent3', 'accent4', 'accent5', 'accent6']],
  ['Links', ['hyperlink', 'followed_hyperlink']]
];

// The value the inspector shows, kept across redraws so a save updates it in place.
let themeDetail = null;
// The sections showing their unused values, which otherwise fold behind a count.
const unusedShown = new Set();
// Whether text styles show as rows of the deck's text rather than a compact scale of their names.
let stylesExpanded = false;

function renderTheme(theme) {
  const view = document.getElementById('theme-view');
  const scroll = view.scrollTop;
  view.replaceChildren();
  if (!theme) {
    element('p', 'theme-empty', view).textContent = 'The theme appears once the presentation loads.';
    return;
  }
  const page = element('div', 'theme-page', view);
  const head = element('header', 'theme-head', page);
  element('h1', null, head).textContent = theme.name;
  element('p', 'theme-note', head).textContent = 'Click anything to inspect where it is set and which slides use it.';

  renderSheet(page, theme);
  renderNotes(page, theme);
  renderStyles(themeSection(page, 'Text styles'), theme);
  if (theme.tables.length > 0) {
    renderTables(themeSection(page, 'Table styles'), theme);
  }
  renderCharts(themeSection(page, 'Charts'), theme);
  if (theme.formats.length > 0) {
    renderFormats(themeSection(page, 'Number formats'), theme);
  }
  markDetail();
  view.scrollTop = scroll;
}

function themeSection(page, title) {
  const node = element('section', 'theme-section', page);
  element('h2', null, node).textContent = title;
  return node;
}

// #region Inspector

/** Makes a node show a value in the inspector. */
function detailTarget(node, kind, name) {
  node.classList.add('detail-target');
  node.dataset.detail = `${kind}:${name}`;
  node.tabIndex = 0;
  node.setAttribute('role', 'button');
  const open = () => openDetail(kind, name);
  node.addEventListener('click', (event) => {
    if (!event.target.closest('button, a')) {
      open();
    }
  });
  node.addEventListener('keydown', (event) => {
    if ((event.key === 'Enter' || event.key === ' ') && event.target === node) {
      event.preventDefault();
      open();
    }
  });
  return node;
}

function openDetail(kind, name) {
  themeDetail = { kind, name };
  markDetail();
  if (state.inspector) {
    renderInspector();
  } else {
    toggle('inspector');
  }
}

function closeDetail() {
  themeDetail = null;
  markDetail();
  renderInspector();
}

function markDetail() {
  const key = themeDetail && `${themeDetail.kind}:${themeDetail.name}`;
  for (const node of document.querySelectorAll('#theme-view .detail-target')) {
    node.classList.toggle('selected', node.dataset.detail === key);
  }
}

/** The inspector's panel in the theme view: the value clicked, or how to pick one. */
function renderThemePanel(panel) {
  const theme = state.themeView;
  const value = theme && themeDetail && findValue(theme, themeDetail);
  if (!value) {
    themeDetail = null;
    renderHeader(panel, { chips: [['Theme', 'style']], title: theme?.name || 'Theme' });
    element('p', 'hint', panel).textContent =
      'Click a color, font, layout, style, or format to see where it is set and which slides use it.';
    return;
  }
  const origin = DETAIL_ORIGINS[themeDetail.kind]?.(value, theme) ?? value.origin;
  renderHeader(panel, {
    chips: [[KIND_NAMES[themeDetail.kind], 'style']],
    title: DETAIL_TITLES[themeDetail.kind]?.(value) ?? value.name,
    meta: origin?.file ? [themeSourceTag(origin, null)] : null
  });
  DETAILS[themeDetail.kind](panel, value, theme);
}

const KIND_NAMES = {
  color: 'Theme color',
  named: 'Named color',
  stray: 'Hex color',
  font: 'Font',
  text: 'Base text',
  style: 'Text style',
  layout: 'Layout',
  table: 'Table style',
  series: 'Chart series color',
  format: 'Number format'
};

const DETAIL_TITLES = {
  font: (font) => font.family,
  text: () => 'Base text',
  layout: (layout) => layout.name || words(layout.id),
  series: (color) => words(color.name)
};

const DETAIL_ORIGINS = {
  layout: (layout) => ({ file: layout.file }),
  series: (color, theme) => theme.charts.origin
};

function findValue(theme, { kind, name }) {
  const lists = {
    color: theme.colors,
    named: theme.named,
    stray: theme.stray.map((color) => ({ ...color, name: color.hex })),
    font: theme.fonts.map((font) => ({ ...font, name: font.role })),
    text: [theme.text],
    style: theme.styles,
    layout: theme.layouts.map((layout) => ({ ...layout, key: layout.id })),
    table: theme.tables,
    series: theme.charts.seriesColors,
    format: theme.formats
  };
  return lists[kind]?.find((value) => (value.key ?? value.name) === name);
}

function detailSwatch(panel, hex) {
  const big = element('div', 'detail-swatch', panel);
  big.style.background = hex;
  big.style.color = inkOn(hex);
  big.textContent = hex;
  return big;
}

function detailFact(panel, text) {
  const fact = element('p', 'detail-fact', panel);
  fact.textContent = text;
  return fact;
}

function detailWarning(panel, text) {
  element('p', 'detail-warning', panel).textContent = text;
}

function detailUses(panel, uses) {
  usesLine(section(panel, 'Used by'), uses);
}

const DETAILS = {
  color(panel, color, theme) {
    detailSwatch(panel, color.hex);
    const twins = theme.colors.filter((other) => other !== color && other.hex === color.hex);
    if (twins.length > 0) {
      // PowerPoint keeps twelve slots whatever they hold, so two holding one color leave a choice the theme can't show.
      detailWarning(panel, `Same color as ${twins.map((twin) => twin.name).join(' and ')}, so the theme offers ` +
        'one color fewer than its slots suggest.');
    }
    const shades = element('div', 'detail-shades', section(panel, 'Shades'));
    for (const shade of color.shades) {
      const cell = element('div', null, shades);
      swatch(cell, shade.hex);
      element('span', null, cell).textContent = shade.label;
    }
    detailUses(panel, color.uses);
  },

  named(panel, color) {
    detailSwatch(panel, color.hex);
    detailFact(panel, `Written as ${color.value}`);
    detailUses(panel, color.uses);
  },

  stray(panel, color) {
    detailSwatch(panel, color.hex);
    detailWarning(panel, 'Written as hex on the slides rather than named in the palette, so it stays as written ' +
      'when the theme changes, in Pintura or in PowerPoint.');
    detailUses(panel, color.uses);
  },

  font(panel, font, theme) {
    detailFact(panel, `The ${font.role} font`);
    if (!fontAvailable(font.family)) {
      detailWarning(panel, 'Not installed here, so the slides fall back to another font; pintura --check names the ' +
        'fix.');
    }
    const sample = element('div', 'font-sample', panel);
    sample.style.fontFamily = fontFamily(font.family);
    element('div', 'font-large', sample).textContent = 'Aa Bb Gg Qq 0123456789 £€$%';
    element('div', null, sample).textContent = 'The quick brown fox jumps over the lazy dog.';
    const italic = element('div', null, sample);
    italic.style.fontStyle = 'italic';
    italic.textContent = 'Italic: the quick brown fox jumps over the lazy dog.';
    const bold = element('div', null, sample);
    bold.style.fontWeight = '700';
    bold.textContent = 'Bold: the quick brown fox jumps over the lazy dog.';
    const styles = section(panel, font.styles.length === 0 ? 'No styles use it' : 'Styles drawn in it');
    const chips = element('div', 'detail-chips', styles);
    if (theme.text.font === font.role) {
      detailChip(chips, 'text', 'text', 'base text');
    }
    for (const name of font.styles) {
      detailChip(chips, 'style', name, name);
    }
  },

  text(panel, text, theme) {
    styleSpecimen(element('div', 'detail-specimen', panel), text, theme, null);
    detailFact(panel, 'What every text is drawn in before its style; a style sets only what differs from it.');
    styleFacts(section(panel, 'Sets'), text, theme, true);
  },

  style(panel, style, theme) {
    styleSpecimen(element('div', 'detail-specimen', panel), style, theme, style.sample);
    if (style.sample) {
      const from = detailFact(panel, 'Text from ');
      slideLink(from, style.sample.slide);
    }
    styleFacts(section(panel, 'Sets'), style, theme, true);
    if (['font', 'size', 'color'].some((key) => !style.own.includes(key))) {
      styleFacts(section(panel, 'From the base text'), style, theme, false);
    }
    detailUses(panel, style.uses);
  },

  layout(panel, layout) {
    const frame = element('div', 'grid-frame', panel);
    const width = Math.max(240, panel.clientWidth - 44);
    const stage = mountStage(frame, layout.slide, state.deck.size, width);
    if (!layout.slide.error) {
      renderGuides(layout.slide, stage);
    }
    detailFact(panel, `Written as layout: ${layout.id}`);
    const areas = (layout.slide.guides || []).filter((guide) => guide.kind === 'area');
    if (areas.length > 0) {
      detailFact(section(panel, 'Areas'), areas.map((area) =>
        `${area.name}${area.owner === 'theme' ? ' (theme)' : ''}`).join(' · '));
    }
    detailUses(panel, { elements: null, slides: layout.slides });
  },

  table(panel, style, theme) {
    tableSample(panel, style, theme);
    detailUses(panel, style.uses);
  },

  series(panel, color) {
    detailSwatch(panel, color.hex);
    detailFact(panel, `Written as ${color.value}`);
    detailUses(panel, color.uses);
  },

  format(panel, format) {
    const table = element('table', 'format-table', section(panel, 'Spells'));
    formatRow(table, 'Positive', format.positive);
    if (format.negative) {
      formatRow(table, 'Negative', format.negative, format.negativeHex);
    }
    detailUses(panel, format.uses);
  }
};

function detailChip(parent, kind, name, label) {
  const chip = element('button', 'chip style', parent);
  chip.type = 'button';
  chip.textContent = label;
  chip.addEventListener('click', () => openDetail(kind, name));
  return chip;
}

function slideLink(parent, id) {
  const slide = state.deck.slides.find((candidate) => candidate.id === id);
  const link = element('button', 'chip slide', parent);
  link.type = 'button';
  link.textContent = slide ? `${slide.number} · ${slide.title || words(id)}` : id;
  link.addEventListener('click', () => {
    select(id);
    setView('slide');
  });
  return link;
}

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && themeDetail && state.view === 'theme' && !event.target.closest('input, textarea')) {
    closeDetail();
    event.preventDefault();
    event.stopImmediatePropagation();
  }
});

// #endregion

// #region Sheet

function renderSheet(page, theme) {
  const sheet = element('section', 'theme-sheet', page);
  const top = element('div', 'sheet-top', sheet);
  renderPalette(element('div', 'sheet-palette', top), theme);
  renderFonts(element('div', 'sheet-fonts', top), theme);
  renderLayouts(element('div', 'sheet-layouts', sheet), theme);
}

/** WCAG's contrast ratio between two hex colors, from 1 to 21. */
function contrast(first, second) {
  const luminance = (hex) => {
    const channels = [1, 3, 5].map((start) => parseInt(hex.slice(start, start + 2), 16) / 255)
      .map((value) => (value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4));
    return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
  };
  const [light, dark] = [luminance(first), luminance(second)].sort((a, b) => b - a);
  return (light + 0.05) / (dark + 0.05);
}

function inkOn(hex) {
  return contrast(hex, '#FFFFFF') >= contrast(hex, '#000000') ? '#FFFFFF' : '#000000';
}

function renderPalette(parent, theme) {
  element('h2', 'sheet-label', parent).textContent = 'Colors';
  const byName = new Map(theme.colors.map((color) => [color.name, color]));
  for (const [label, names] of SLOT_GROUPS) {
    const group = element('div', 'palette-group', parent);
    element('span', 'theme-label', group).textContent = label;
    const row = element('div', 'palette-row', group);
    for (const name of names) {
      const color = byName.get(name);
      const tile = detailTarget(element('div', 'palette-tile', row), 'color', name);
      tile.style.background = color.hex;
      tile.style.color = inkOn(color.hex);
      tile.title = `${name} · ${color.hex}`;
      if (color.uses.elements === 0) {
        element('span', 'palette-unused', tile).textContent = 'Unused';
      }
      element('span', 'palette-name', tile).textContent = name.replace('followed_hyperlink', 'followed');
      element('span', 'palette-hex', tile).textContent = color.hex;
    }
  }
  if (theme.named.length > 0) {
    const group = element('div', 'palette-group', parent);
    element('span', 'theme-label', group).textContent = 'Named';
    const row = element('div', 'palette-chips', group);
    for (const color of theme.named) {
      colorChip(row, color.hex, color.name, 'named', color.name, color.uses);
    }
  }
  if (theme.stray.length > 0) {
    const group = element('div', 'palette-group stray', parent);
    element('span', 'theme-label', group).textContent = 'Hex outside the palette';
    const row = element('div', 'palette-chips', group);
    for (const color of theme.stray) {
      colorChip(row, color.hex, color.hex, 'stray', color.hex, color.uses);
    }
  }
}

function colorChip(parent, hex, label, kind, name, uses) {
  const chip = detailTarget(element('div', 'color-chip', parent), kind, name);
  swatch(chip, hex);
  element('span', null, chip).textContent = label;
  if (uses.elements === 0) {
    element('span', 'theme-label', chip).textContent = '· unused';
  }
  return chip;
}

function swatch(parent, hex) {
  const node = element('span', 'swatch', parent);
  node.style.background = hex;
  node.title = hex;
  return node;
}

function renderFonts(parent, theme) {
  element('h2', 'sheet-label', parent).textContent = 'Fonts';
  for (const font of theme.fonts) {
    const card = detailTarget(element('div', 'sheet-font'), 'font', font.role);
    parent.append(card);
    const sample = element('div', 'sheet-font-sample', card);
    sample.style.fontFamily = fontFamily(font.family);
    sample.textContent = 'Aa';
    const name = element('div', 'sheet-font-name', card);
    element('strong', null, name).textContent = font.family;
    element('span', 'theme-label', name).textContent = words(font.role);
    if (!fontAvailable(font.family)) {
      element('span', 'chip missing', name).textContent = 'Missing';
    }
  }
}

// Each layout drawn alone, its title and placeholders holding their names, outlined with the areas it adds; the
// theme's areas would be the same on every one, so the inspector shows them.
function renderLayouts(parent, theme) {
  element('h2', 'sheet-label', parent).textContent = 'Layouts';
  const grid = element('div', 'layout-cards', parent);
  for (const layout of theme.layouts) {
    const card = detailTarget(element('div', `layout-card${layout.slides.length === 0 ? ' unused' : ''}`, grid),
      'layout', layout.id);
    card.dataset.layout = layout.id;
    const frame = element('div', 'grid-frame', card);
    const stage = mountStage(frame, layout.slide, state.deck.size, SHEET_LAYOUT_WIDTH);
    if (!layout.slide.error) {
      const own = (layout.slide.guides || []).filter((guide) => guide.kind !== 'area' || guide.owner !== 'theme');
      renderGuides({ ...layout.slide, guides: own }, stage);
    }
    const head = element('div', 'layout-head', card);
    element('strong', null, head).textContent = layout.name || words(layout.id);
    element('span', 'theme-label', head).textContent = countLabel(layout.slides.length, 'slide');
  }
}

// #endregion

// #region Notes

// What a reviewer would want to fix or question, gathered from across the theme so the values themselves stay quiet.
function renderNotes(page, theme) {
  const notes = [];
  const seen = new Set();
  for (const color of theme.colors) {
    const twins = theme.colors.filter((other) => other.hex === color.hex);
    if (twins.length > 1 && !seen.has(color.hex)) {
      seen.add(color.hex);
      notes.push([`${twins.map((twin) => twin.name).join(' and ')} are the same color`, 'color', color.name]);
    }
  }
  for (const font of theme.fonts.filter((candidate) => !fontAvailable(candidate.family))) {
    notes.push([`${font.family} isn't installed, so the slides draw in another font`, 'font', font.role]);
  }
  for (const color of theme.stray) {
    notes.push([`${color.hex} is written on ${countLabel(color.uses.slides.length, 'slide')} outside the palette`,
      'stray', color.hex]);
  }
  const unused = [
    [theme.colors, 'color'], [theme.styles, 'style'], [theme.formats, 'format'], [theme.tables, 'table style']
  ].map(([values, noun]) => [values.filter((value) => value.uses.elements === 0).length, noun])
    .filter(([count]) => count > 0);
  const layouts = theme.layouts.filter((layout) => layout.slides.length === 0).length;
  if (layouts > 0) {
    unused.push([layouts, 'layout']);
  }
  if (notes.length === 0 && unused.length === 0) {
    return;
  }
  const box = element('section', 'theme-notes', page);
  element('h2', 'sheet-label', box).textContent = 'Worth a look';
  const list = element('ul', null, box);
  for (const [text, kind, name] of notes) {
    const item = element('li', null, list);
    detailTarget(element('span', 'note-link', item), kind, name).textContent = text;
  }
  if (unused.length > 0) {
    element('li', 'theme-label', list).textContent =
      `Unused: ${unused.map(([count, noun]) => countLabel(count, noun)).join(', ')}`;
  }
}

function countLabel(count, noun) {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}

// #endregion

// #region Styles

// A style setting its own font or size is a step on the type scale; one setting only color, weight, or alignment
// modifies whatever text it's on. The scale shows each style's name in its look, at a size compressed so the largest
// stays a line, and expands to rows of the deck's own text in each.
function renderStyles(section, theme) {
  const scale = theme.styles.filter((style) => style.own.includes('font') || style.own.includes('size'))
    .sort((a, b) => b.size - a.size);
  const modifiers = theme.styles.filter((style) => !scale.includes(style));
  if (stylesExpanded) {
    const list = element('div', 'style-list', section);
    styleRow(list, theme.text, theme, 'text');
    foldUnused(section, list, 'styles', scale, (style) => styleRow(list, style, theme, 'style'));
  } else {
    const chips = element('div', 'type-scale', section);
    for (const style of [theme.text, ...scale]) {
      typeChip(chips, style, theme, style === theme.text ? 'text' : 'style');
    }
  }
  const toggle = element('button', 'unused-toggle', section);
  toggle.type = 'button';
  toggle.textContent = stylesExpanded ? 'Show as a scale' : "Show each in the deck's text";
  toggle.addEventListener('click', () => {
    stylesExpanded = !stylesExpanded;
    renderTheme(state.themeView);
  });
  if (modifiers.length > 0) {
    element('h3', null, section).textContent = 'Modifiers, which keep the font and size of the text they are on';
    const chips = element('div', 'modifier-chips', section);
    for (const style of modifiers) {
      const chip = detailTarget(element('div', `modifier${style.uses.elements === 0 ? ' unused' : ''}`, chips),
        'style', style.name);
      const look = element('span', 'modifier-look', chip);
      look.textContent = style.name;
      look.style.color = style.hex;
      look.style.fontWeight = style.bold ? '700' : '400';
      look.style.fontStyle = style.italic ? 'italic' : 'normal';
      look.style.textDecoration = style.underline ? 'underline' : 'none';
      const facts = element('span', 'theme-label', chip);
      facts.textContent = ownFacts(style).join(' · ');
      if (contrast(style.hex, theme.background) < 2) {
        chip.style.background = darkest(theme);
        facts.style.color = 'rgb(255 255 255 / 0.7)';
      }
    }
  }
}

function typeChip(parent, style, theme, kind) {
  const chip = detailTarget(element('div', `type-chip${kind === 'style' && style.uses.elements === 0 ? ' unused' : ''}`,
    parent), kind, style.name);
  chip.style.background = contrast(style.hex, theme.background) < 2 ? darkest(theme) : theme.background;
  const name = element('span', 'type-name', chip);
  name.textContent = kind === 'text' ? 'text' : style.name;
  name.style.fontFamily = fontFamily(style.family);
  name.style.fontSize = `${11 + (style.size - 8) * 0.6}px`;
  name.style.color = style.hex;
  name.style.fontWeight = style.bold ? '700' : '400';
  name.style.fontStyle = style.italic ? 'italic' : 'normal';
  const size = element('span', 'type-size', chip);
  size.textContent = style.size;
  size.title = `${style.family} ${style.size}pt`;
  size.style.color = style.hex;
  return chip;
}

function darkest(theme) {
  return theme.colors.find((color) => color.name === 'dark2').hex;
}

function styleRow(list, style, theme, kind) {
  const row = detailTarget(element('div', `style-row${kind === 'style' && style.uses.elements === 0 ? ' unused' : ''}`,
    list), kind, style.name);
  styleSpecimen(element('div', 'style-sample', row), style, theme, kind === 'text' ? null : style.sample);
  const meta = element('div', 'style-meta', row);
  element('strong', 'style-name', meta).textContent = kind === 'text' ? 'Base text' : style.name;
  const facts = element('span', 'style-facts', meta);
  facts.textContent = `${style.family} ${style.size}pt`;
  if (kind === 'style') {
    element('span', 'theme-label', meta).textContent = style.uses.elements === 0
      ? 'Unused' : countLabel(style.uses.slides.length, 'slide');
  }
  return row;
}

// The deck's own text in the style, or else a stock line; a style too pale for the background is shown on dark2.
function styleSpecimen(node, style, theme, sample) {
  node.style.background = contrast(style.hex, theme.background) < 2 ? darkest(theme) : theme.background;
  const text = element('span', sample ? null : 'stock', node);
  text.textContent = sample?.text || SPECIMEN;
  text.style.fontFamily = fontFamily(style.family);
  text.style.fontSize = `${style.size}pt`;
  text.style.color = style.hex;
  text.style.fontWeight = style.bold ? '700' : '400';
  text.style.fontStyle = style.italic ? 'italic' : 'normal';
  text.style.textDecoration = style.underline ? 'underline' : 'none';
  return node;
}

function ownFacts(style) {
  return style.own.map((key) => {
    const value = style.sets[key];
    return value === 'true' ? key : `${key.replace(/_/g, ' ')} ${value}`;
  });
}

// What a style sets itself, or what it takes from the base text; the base text sets everything it lists.
function styleFacts(parent, style, theme, own) {
  const rows = [
    ['font', `${style.family}`, `${style.font} font`],
    ['size', `${style.size}pt`],
    ['color', style.color, null, style.hex],
    ['bold', style.bold ? 'bold' : 'regular'],
    ['italic', style.italic ? 'italic' : 'upright']
  ];
  const keys = new Set(style.own);
  const list = element('dl', 'style-fact-list', parent);
  let shown = 0;
  for (const [key, value, note, hex] of rows) {
    if (keys.has(key) !== own && !(own && style === theme.text)) {
      continue;
    }
    if (!own && (key === 'bold' || key === 'italic') && !style[key]) {
      continue;
    }
    element('dt', null, list).textContent = key;
    const cell = element('dd', null, list);
    if (hex) {
      swatch(cell, hex);
    }
    cell.append(value);
    if (note) {
      element('span', 'theme-label', cell).textContent = ` · ${note}`;
    }
    shown++;
  }
  const others = own ? style.own.filter((key) => !rows.some(([name]) => name === key)) : [];
  for (const key of others) {
    element('dt', null, list).textContent = key.replace(/_/g, ' ');
    element('dd', null, list).textContent = style.sets[key];
    shown++;
  }
  if (shown === 0) {
    list.remove();
    element('p', 'theme-label', parent).textContent = 'Nothing of its own';
  }
}

// Unused values fold behind a count, since they are candidates to remove rather than part of the theme's look.
function foldUnused(section, parent, key, values, draw) {
  const used = values.filter((value) => value.uses.elements !== 0);
  const unused = values.filter((value) => value.uses.elements === 0);
  const shown = unusedShown.has(key) ? values : used;
  for (const value of shown) {
    draw(value);
  }
  if (unused.length === 0) {
    return;
  }
  const toggle = element('button', 'unused-toggle', section);
  toggle.type = 'button';
  toggle.textContent = unusedShown.has(key) ? 'Hide unused' : `Show ${unused.length} unused`;
  toggle.addEventListener('click', () => {
    if (!unusedShown.delete(key)) {
      unusedShown.add(key);
    }
    renderTheme(state.themeView);
  });
}

// #endregion

// #region Tables, charts, and formats

function renderTables(section, theme) {
  const grid = element('div', 'table-cards', section);
  foldUnused(section, grid, 'tables', theme.tables, (style) => {
    const card = detailTarget(element('div', `table-card${style.uses.elements === 0 ? ' unused' : ''}`, grid),
      'table', style.name);
    const head = element('div', 'layout-head', card);
    element('strong', null, head).textContent = style.name;
    element('span', 'theme-label', head).textContent = countLabel(style.uses.slides.length, 'slide');
    tableSample(card, style, theme);
  });
}

function tableSample(parent, style, theme) {
  const table = element('table', 'table-sample', parent);
  table.style.background = theme.background;
  const body = style.rows.find((row) => row.name === 'body');
  // Three body rows show the banding between the header and the row styles below them.
  const rows = style.rows.flatMap((row) => (row === body ? [row, { ...row, banded: true }, row] : [row]));
  for (const row of rows) {
    const tr = element('tr', null, table);
    const fill = row.fill || (row.banded ? style.banded : null);
    if (fill) {
      tr.style.background = fill;
    }
    ['label', 'value', 'value'].forEach((kind, index) => {
      const cell = element('td', kind, tr);
      cell.textContent = index === 0 ? words(row.name) : ['1,234', '5,678'][index - 1];
      applyCharacter(cell, row.text);
      cell.style.borderTop = themeBorder(row.borderTop);
      cell.style.borderBottom = themeBorder(row.borderBottom);
    });
  }
  return table;
}

function themeBorder(stroke) {
  return stroke ? `${stroke.width}pt ${stroke.dash === 'solid' ? 'solid' : 'dashed'} ${stroke.color}` : 'none';
}

function renderCharts(section, theme) {
  const charts = theme.charts;
  element('span', 'theme-label', section).textContent = 'Series colors, in the order charts take them';
  const strip = element('div', 'series-strip', section);
  charts.seriesColors.forEach((color, index) => {
    const tile = detailTarget(element('div', 'series-tile', strip), 'series', color.name);
    const bar = element('div', 'series-bar', tile);
    bar.style.background = color.hex;
    element('strong', null, tile).textContent = index + 1;
    element('span', 'theme-label', tile).textContent = color.uses.elements === 0
      ? `${color.value} · unused` : color.value;
  });
  const text = element('p', 'chart-text', section);
  applyCharacter(text, charts.text);
  text.textContent = `Chart text: ${charts.text.font || 'body'} ${charts.text.size ?? ''}pt`;
  if (charts.styles.length > 0) {
    element('h3', null, section).textContent = 'Chart styles';
    const chips = element('div', 'modifier-chips', section);
    for (const style of charts.styles) {
      const chip = element('div', `modifier${style.uses.elements === 0 ? ' unused' : ''}`, chips);
      element('strong', 'modifier-look', chip).textContent = style.name;
      element('span', 'theme-label', chip).textContent = style.uses.elements === 0
        ? 'Unused' : countLabel(style.uses.slides.length, 'slide');
      themeSourceTag(style.origin, chip);
    }
  }
}

function renderFormats(section, theme) {
  const table = element('table', 'format-table', section);
  const head = element('tr', null, element('thead', null, table));
  for (const label of ['Format', 'Positive', 'Negative', 'Used on']) {
    element('th', null, head).textContent = label;
  }
  const body = element('tbody', null, table);
  foldUnused(section, body, 'formats', theme.formats, (format) => {
    const row = detailTarget(element('tr', format.uses.elements === 0 ? 'unused' : null, body), 'format',
      format.name);
    element('td', 'format-name', row).textContent = format.name;
    element('td', 'format-sample', row).textContent = format.positive;
    const negative = element('td', 'format-sample', row);
    negative.textContent = format.negative ?? '';
    if (format.negativeHex) {
      negative.style.color = format.negativeHex;
    }
    element('td', 'theme-label', row).textContent = format.uses.elements === 0
      ? 'Unused' : countLabel(format.uses.slides.length, 'slide');
  });
}

function formatRow(table, label, value, hex) {
  const row = element('tr', null, table);
  element('th', null, row).textContent = label;
  const cell = element('td', 'format-sample', row);
  cell.textContent = value;
  if (hex) {
    cell.style.color = hex;
  }
}

// #endregion

// The slides using a value, as chips that open each one; unused values say so, since they are candidates to remove.
function usesLine(parent, uses) {
  const line = element('div', 'theme-uses', parent);
  if (uses.slides.length === 0) {
    element('span', 'chip muted', line).textContent = 'Unused';
    return line;
  }
  const every = uses.slides.length === state.deck.slides.length;
  const count = uses.elements === null ? '' : `${countLabel(uses.elements, 'element')} on `;
  element('span', 'theme-label', line).textContent = every
    ? `${count}every slide`
    : `${count}${countLabel(uses.slides.length, 'slide')}`;
  const numbers = new Map(state.deck.slides.map((slide) => [slide.id, slide]));
  for (const id of uses.slides) {
    const slide = numbers.get(id);
    const chip = element('button', 'chip slide', line);
    chip.type = 'button';
    chip.textContent = slide ? slide.number : id;
    chip.title = slide ? `${slide.number} · ${slide.title || words(id)}` : id;
    chip.addEventListener('click', () => {
      select(id);
      setView('slide');
    });
  }
  return line;
}

function themeSourceTag(origin, parent) {
  if (!origin?.file) {
    return null;
  }
  const label = `${origin.file.split('/').pop()}${origin.line ? `:${origin.line}` : ''}`;
  return copyTag(origin, label, 'origin-tag', parent);
}