const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { colorName, elementName, groupOf, kindName, kindSummary, originOf, placementSummary, propertyLabel, propertyValue,
  sourceOf, sourcesPhrase } =
  require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'inspector.js'));

const SLIDE = 'presentations/example/slides/segments.yaml';

function property(key, value, display, origin = null) {
  return { key, value, display, origin };
}

test('labels name a property as a designer would', () => {
  assert.equal(propertyLabel('frame'), 'Position');
  assert.equal(propertyLabel('insets'), 'Padding');
  assert.equal(propertyLabel('valign'), 'Vertical alignment');
  assert.equal(propertyLabel('stroke.width'), 'Outline width');
  assert.equal(propertyLabel('data_labels.show'), 'Data labels › show');
  assert.equal(propertyLabel('border_bottom.color'), 'Border bottom › color');
});

test('theme colors read as PowerPoint names them', () => {
  assert.equal(colorName('dark2'), 'Dark 2');
  assert.equal(colorName('accent1 lighter 80'), 'Accent 1, lighter 80%');
  assert.equal(colorName('followed_hyperlink'), 'Followed hyperlink');
  assert.equal(colorName('#3B8F5A'), '#3B8F5A');
});

test('a frame reads as position and size in points, naming its area', () => {
  const placed = propertyValue(property('frame', { area: 'left', rect: [40, 140, 430, 340] },
    'left [40, 140, 430, 340]'));
  assert.equal(placed.text, '40, 140 · 430 × 340 pt');
  assert.equal(placed.note, 'Left area');
  const free = propertyValue(property('frame', [856, 44, 64, 24], '[856, 44, 64, 24]'));
  assert.equal(free.text, '856, 44 · 64 × 24 pt');
  assert.equal(free.note, undefined);
});

test('values carry their units and plain words', () => {
  assert.equal(propertyValue(property('size', 16, '16')).text, '16 pt');
  assert.equal(propertyValue(property('bold', true, 'true')).text, 'Yes');
  assert.equal(propertyValue(property('align', 'left', 'left')).text, 'Left');
  assert.equal(propertyValue(property('type', 'stacked_column', 'stacked_column')).text, 'Stacked column');
  assert.equal(propertyValue(property('insets', [7.2, 3.6, 7.2, 3.6], '[7.2, 3.6, 7.2, 3.6]')).text,
    '7.2 pt sides, 3.6 pt top and bottom');
  assert.equal(propertyValue(property('insets', [0, 0, 0, 0], '[0, 0, 0, 0]')).text, '0 pt');
  assert.equal(propertyValue(property('from', [276, 264], '[276, 264]')).text, '276, 264 pt');
  assert.equal(propertyValue(property('gap_width', 80, '80')).text, '80%');
  assert.equal(propertyValue(property('stroke', 'none', 'none')).text, 'None');
});

test('a style keeps the name the theme gives it', () => {
  assert.equal(propertyValue(property('style', 'callout', 'callout')).text, 'callout');
});

test('a font names its family, and the theme font it stands for', () => {
  const font = propertyValue(property('font', 'body', 'body (Arial)'));
  assert.equal(font.text, 'Arial');
  assert.equal(font.note, 'Body font');
});

test('colors come with swatches', () => {
  const one = propertyValue(property('color', 'dark1', 'dark1 (#1B1B1B)'));
  assert.equal(one.text, 'Dark 1');
  assert.deepEqual(one.colors, ['#1B1B1B']);
  const series = propertyValue(property('series_colors', [],
    'accent4 lighter 40 (#A6ACB2), accent1 (#1F4E79), accent3 (#F2A541)'));
  assert.equal(series.text, 'Accent 4, lighter 40%, Accent 1, Accent 3');
  assert.deepEqual(series.colors, ['#A6ACB2', '#1F4E79', '#F2A541']);
  const hex = propertyValue(property('color', '#3B8F5A', '#3B8F5A'));
  assert.equal(hex.text, '#3B8F5A');
  assert.deepEqual(hex.colors, ['#3B8F5A']);
});

test('an origin names the level that set it', () => {
  assert.deepEqual(originOf(`${SLIDE}:9 elements.connectivity.frame`),
    { level: 'slide', label: 'This slide', file: SLIDE, line: 9, key: 'elements.connectivity.frame' });
  assert.equal(originOf('themes/acme/styles.yaml:27 styles.lead.size').level, 'theme');
  assert.equal(originOf('themes/acme/theme.yaml:28 title.frame').label, 'Theme');
  const layout = originOf('themes/acme/layouts/cover.yaml:5 title.frame');
  assert.equal(layout.level, 'layout');
  assert.equal(layout.label, 'Cover layout');
  assert.equal(originOf('presentations/example/charts/revenue.yaml:1 type').label, 'Chart file');
  assert.equal(originOf('presentations/example/tables/h1.yaml:1 data').label, 'Table file');
  assert.equal(originOf('presentations/example/presentation.yaml:3 fields.date').label, 'Presentation');
  assert.equal(originOf(null).level, 'default');
  assert.deepEqual(originOf('themes/acme/styles.yaml charts.minimal.data_labels.color'),
    { level: 'theme', label: 'Theme', file: 'themes/acme/styles.yaml', line: null,
      key: 'charts.minimal.data_labels.color' });
});

test('kinds and roles read as nouns', () => {
  assert.equal(kindName({ kind: 'text', role: 'title' }), 'Title');
  assert.equal(kindName({ kind: 'text', role: 'placeholder' }), 'Placeholder');
  assert.equal(kindName({ kind: 'text' }), 'Text box');
  assert.equal(kindName({ kind: 'chart' }), 'Chart');
  assert.equal(kindName({ kind: 'shape', paragraphs: [{ text: '2025 revenue' }] }), 'Text box');
  assert.equal(kindName({ kind: 'shape', text: { paragraphs: [{ runs: [] }] } }), 'Text box');
  assert.equal(kindName({ kind: 'shape' }), 'Shape');
});

test("the line under an element's name says what kind of thing it is", () => {
  const shape = (value) => ({ key: 'shape', value, display: value });
  assert.equal(kindSummary({ kind: 'shape', properties: [shape('rounded_rectangle')], paragraphs: [{ text: 'x' }] }),
    'Text box · Rounded rectangle');
  assert.equal(kindSummary({ kind: 'shape', properties: [shape('right_arrow')] }), 'Right arrow');
  assert.equal(kindSummary({ kind: 'chart', properties: [{ key: 'type', value: 'stacked_column',
    display: 'stacked_column' }] }), 'Stacked column chart');
  assert.equal(kindSummary({ kind: 'table', properties: [] }), 'Table');
  assert.equal(kindSummary({ kind: 'text', role: 'title', properties: [] }), 'Title placeholder');
});

test('an element is named by its id, in words', () => {
  assert.equal(elementName({ id: 'left_heading' }), 'Left heading');
  assert.equal(elementName({ id: 'to-total' }), 'To total');
  assert.equal(elementName({ id: 'title', role: 'title' }), 'Title');
});

test('properties fall into the sections a reviewer reads them by', () => {
  assert.equal(groupOf('shape', 'frame', true), 'placement');
  assert.equal(groupOf('shape', 'fill', true), 'shape');
  assert.equal(groupOf('shape', 'size', true), 'text');
  assert.equal(groupOf('text', 'fill', true), 'shape');
  assert.equal(groupOf('line', 'stroke.width', false), 'line');
  assert.equal(groupOf('picture', 'fit', false), 'picture');
  assert.equal(groupOf('chart', 'type', false), 'chart');
  assert.equal(groupOf('chart', 'data_labels.show', false), 'labels');
  assert.equal(groupOf('chart', 'value_axis.gridlines', false), 'axes');
  assert.equal(groupOf('chart', 'series_colors', false), 'legend');
  assert.equal(groupOf('chart', 'gap_width', false), 'bars');
  assert.equal(groupOf('chart', 'text.size', false), 'text');
  assert.equal(groupOf('table', 'data', false), 'table');
  assert.equal(groupOf('table', 'columns[2].width', false), 'columns');
  assert.equal(groupOf('table', 'header_rows', false), 'rows');
  assert.equal(groupOf('table', 'font', false), 'text');
});

test('an inherited value names the style, file or level it comes from', () => {
  assert.deepEqual(sourceOf('themes/acme/styles.yaml:29 styles.callout.size'),
    { label: 'callout', phrase: 'the callout style', style: true });
  assert.deepEqual(sourceOf('themes/acme/styles.yaml:60 charts.minimal.data_labels.show'),
    { label: 'minimal', phrase: 'the minimal style', style: true });
  assert.deepEqual(sourceOf('themes/acme/styles.yaml:2 text.font'), { label: 'Theme', phrase: 'the theme' });
  assert.deepEqual(sourceOf('themes/acme/layouts/cover.yaml:5 title.frame'),
    { label: 'Cover layout', phrase: 'the cover layout' });
  assert.deepEqual(sourceOf(null, 'chart'), { label: 'PowerPoint', phrase: 'PowerPoint' });
  assert.deepEqual(sourceOf(null, 'chart', 'value_axis.max'), { label: 'Automatic', phrase: 'the automatic scale' });
  assert.deepEqual(sourceOf(null, 'shape'), { label: 'Geometry', phrase: "the shape's geometry" });
});

test('a folded run of inherited values says where they come from', () => {
  const callout = { phrase: 'the callout style' };
  const theme = { phrase: 'the theme' };
  assert.equal(sourcesPhrase([callout, theme, callout, theme]), 'the callout style and the theme');
  assert.equal(sourcesPhrase([theme]), 'the theme');
  assert.equal(sourcesPhrase([callout, theme, { phrase: 'PowerPoint' }]), 'the callout style, the theme and PowerPoint');
});

test('a folded placement reads as its area or position, and its size', () => {
  assert.equal(placementSummary({ area: 'left', rect: [40, 100, 430, 380] }), 'Left area · 430 × 380');
  assert.equal(placementSummary([490, 100, 430, 170]), '490, 100 · 430 × 170');
  assert.equal(placementSummary(undefined), '');
});