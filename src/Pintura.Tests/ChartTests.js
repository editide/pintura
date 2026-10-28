const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');

const { formatNumber, axisTicks, stackSeries, layoutChart, wrap, spellDate, dateAxis, dateLabels } =
  require(path.join(__dirname, '..', 'Pintura', 'wwwroot', 'charts.js'));

const billions = { scale: 0.001, decimals: 1, separator: true, prefix: '$', suffix: 'B', negative: 'parentheses' };

// `labels` in the overrides go on every series, as the payload gives a series its chart's labels.
function chart({ labels = null, ...overrides } = {}) {
  const drawn = {
    type: 'clustered_column',
    family: 'column',
    grouping: 'clustered',
    categories: ['2024', '2025'],
    series: [{ name: 'Space', values: [3796, 4086] }, { name: 'AI', values: [2620, -1237] }],
    colors: ['#111111', '#222222'],
    valueAxis: { visible: true, gridlines: false, min: -2000, max: 5000, step: 1000 },
    categoryAxis: true,
    gapWidth: 150,
    overlap: 0,
    text: { font: 'Arial', size: 10, color: '#000000' },
    gridColor: '#D9D9D9',
    axisColor: '#BFBFBF',
    edgeColor: '#FFFFFF',
    ...overrides
  };
  return { ...drawn, series: drawn.series.map((series) => ({ labels, ...series })) };
}

// Six points a character keeps layout arithmetic predictable.
function measure(text) {
  return text.length * 6;
}

function texts(primitives) {
  return primitives.filter((primitive) => primitive.kind === 'text');
}

test('a label breaks at each line feed, then wraps each line to the width', () => {
  // Arrange
  const measure = (text) => text.length;

  // Act
  const lines = wrap("Jul '26–\nJun '28", 12, measure);

  // Assert
  assert.deepEqual(lines, ["Jul '26–", "Jun '28"]);
});

test('a format scales, rounds, and wraps a negative in parentheses around its prefix and suffix', () => {
  // Act
  const negative = formatNumber(-19575, billions);
  const positive = formatNumber(11387, { ...billions, scale: 1, decimals: 0, prefix: '', suffix: '' });

  // Assert
  assert.equal(negative, '($19.6B)');
  assert.equal(positive, '11,387');
});

test('without a format a number shows as written, as Excel General does', () => {
  // Act
  const shown = [formatNumber(18000, null), formatNumber(-0.25, null), formatNumber(0.1 + 0.2, null)];

  // Assert
  assert.deepEqual(shown, ['18000', '-0.25', '0.3']);
});

test('an axis ticks from its min to its max a step apart', () => {
  // Act
  const scale = axisTicks({ min: -0.2, max: 0.4, step: 0.2 });

  // Assert
  assert.deepEqual(scale.ticks, [-0.2, 0, 0.2, 0.4]);
});

test('an axis whose step would tick it thousands of times ticks only its ends', () => {
  // Act
  const scale = axisTicks({ min: 0, max: 20000, step: 0.1 });

  // Assert
  assert.deepEqual(scale.ticks, [0, 20000]);
});

test('stacked bars stack positives up and negatives down, each from zero', () => {
  // Arrange
  const stacked = chart({ type: 'stacked_column', grouping: 'stacked', series: [
    { name: 'A', values: [5, -2] }, { name: 'B', values: [3, -4] }, { name: 'C', values: [-1, 6] }
  ] });

  // Act
  const extents = stackSeries(stacked);

  // Assert
  assert.deepEqual(extents, [[[0, 5], [0, -2]], [[5, 8], [-2, -6]], [[0, -1], [0, 6]]]);
});

test('percent stacking divides by each category total', () => {
  // Arrange
  const percent = chart({ grouping: 'percent_stacked', series: [
    { name: 'A', values: [1, 3] }, { name: 'B', values: [3, null] }
  ] });

  // Act
  const extents = stackSeries(percent);

  // Assert
  assert.deepEqual(extents, [[[0, 0.25], [0, 1]], [[0.25, 1], null]]);
});

test('bar charts list categories top to bottom in the order of the data', () => {
  // Arrange
  const bars = chart({ type: 'clustered_bar', family: 'bar', categories: ['Operating', 'Investing', 'Financing'],
    series: [{ name: '2025', values: [6785, -19575, 26350] }] });

  // Act
  const labels = texts(layoutChart(bars, 400, 300, measure)).filter((text) => text.anchor === 'end');

  // Assert
  assert.deepEqual(labels.map((label) => label.text), ['Operating', 'Investing', 'Financing']);
  assert.ok(labels[0].y < labels[1].y && labels[1].y < labels[2].y);
});

test('a point with a color of its own fills its bar with it, and the rest take the series color', () => {
  // Arrange
  const colored = chart({ series: [{ name: 'Space', values: [3796, 4086], colors: [null, '#C00000'] }] });

  // Act
  const bars = layoutChart(colored, 400, 300, measure).filter((primitive) => primitive.kind === 'rect');

  // Assert
  assert.deepEqual(bars.map((bar) => bar.fill), ['#111111', '#C00000']);
});

test('an outside label sits past the end of its bar, below a negative one', () => {
  // Arrange
  const labelled = chart({ labels: { show: 'value', position: 'outside', format: billions } });

  // Act
  const primitives = layoutChart(labelled, 400, 300, measure);
  const bars = primitives.filter((primitive) => primitive.kind === 'rect');
  const negative = bars[3];
  const label = texts(primitives).find((text) => text.text === '($1.2B)');

  // Assert
  assert.ok(label.y > negative.y + negative.height);
  assert.ok(Math.abs(label.x - (negative.x + negative.width / 2)) < 0.01);
});

test('an outside label past the plot stays inside it, clear of the category labels, as PowerPoint keeps it', () => {
  // Arrange
  const labelled = chart({ series: [{ name: 'Margin', values: [0.3, -0.19] }],
    valueAxis: { visible: true, gridlines: false, min: -0.2, max: 0.4, step: 0.1 },
    labels: { show: 'value', position: 'outside', format: null } });
  const bars = chart({ type: 'clustered_bar', family: 'bar', series: [{ name: 'Margin', values: [0.3, -0.19] }],
    valueAxis: { visible: true, gridlines: false, min: -0.2, max: 0.4, step: 0.1 },
    labels: { show: 'value', position: 'outside', format: null } });

  // Act
  const columnTexts = texts(layoutChart(labelled, 400, 300, measure));
  const barTexts = texts(layoutChart(bars, 400, 300, measure));
  const columnLabel = columnTexts.find((text) => text.text === '-0.19');
  const category = columnTexts.find((text) => text.text === '2025');
  const barLabel = barTexts.find((text) => text.text === '-0.19');
  const barCategory = barTexts.find((text) => text.text === '2025');

  // Assert
  assert.ok(columnLabel.y + 6 <= category.y - 6, `${columnLabel.y} overlaps ${category.y}`);
  assert.ok(barLabel.x - measure('-0.19') / 2 >= barCategory.x, `${barLabel.x} overlaps ${barCategory.x}`);
});

test('a negative label takes the color its format names', () => {
  // Arrange
  const labelled = chart({ labels: { show: 'value', position: 'center',
    format: { ...billions, negativeColor: '#C00000' } } });

  // Act
  const labels = texts(layoutChart(labelled, 400, 300, measure)).filter((text) => text.text.startsWith('('));

  // Assert
  assert.deepEqual(labels.map((label) => label.color), ['#C00000']);
});

test('labels take their own color, and a negative the color its format names over it', () => {
  // Arrange
  const labelled = chart({ labels: { show: 'value', position: 'center', color: '#FFFFFF',
    format: { ...billions, negativeColor: '#C00000' } } });

  // Act
  const labels = texts(layoutChart(labelled, 400, 300, measure)).filter((text) => text.text.startsWith('$') ||
    text.text.startsWith('('));
  const colors = new Set(labels.filter((label) => label.color).map((label) => `${label.text} ${label.color}`));

  // Assert
  assert.ok(colors.has('($1.2B) #C00000'));
  assert.ok(colors.has('$3.8B #FFFFFF'));
});

test('a pie shows percent labels of its one series and colors each slice', () => {
  // Arrange
  const pie = chart({ type: 'pie', family: 'pie', categories: ['Space', 'Connectivity', 'AI'],
    series: [{ name: '2025', values: [4086, 11387, 3201] }], colors: ['#111111', '#222222', '#333333'],
    valueAxis: null, categoryAxis: null, labels: { show: 'percent', position: 'center', format: null } });

  // Act
  const primitives = layoutChart(pie, 400, 300, measure);

  // Assert
  assert.deepEqual(primitives.filter((primitive) => primitive.kind === 'path').map((slice) => slice.fill),
    ['#111111', '#222222', '#333333']);
  assert.deepEqual(texts(primitives).map((text) => text.text), ['22%', '61%', '17%']);
});

test('a side legend lists stacked columns and lines from the top of the stack, as PowerPoint does', () => {
  // Arrange
  const columns = chart({ type: 'stacked_column', grouping: 'stacked', legend: 'right' });

  // Act
  const primitives = layoutChart(columns, 400, 300, measure);

  // Assert
  const legend = texts(primitives).filter((text) => text.anchor === 'start');
  assert.deepEqual(legend.map((text) => text.text), ['AI', 'Space']);
  assert.ok(legend[0].y < legend[1].y);
  const swatches = primitives.filter((primitive) => primitive.kind === 'rect' && primitive.x > 300);
  assert.deepEqual(swatches.map((swatch) => swatch.fill), ['#222222', '#111111']);
});

test('a legend keeps the order of the data below a stack, beside stacked bars, and beside clustered columns', () => {
  // Arrange
  const charts = [
    chart({ type: 'stacked_column', grouping: 'stacked', legend: 'bottom' }),
    chart({ type: 'stacked_bar', family: 'bar', grouping: 'stacked', legend: 'right' }),
    chart({ legend: 'left' })
  ];

  // Act
  const legends = charts.map((each) => texts(layoutChart(each, 400, 300, measure))
    .filter((text) => text.anchor === 'start').map((text) => text.text));

  // Assert
  assert.deepEqual(legends, [['Space', 'AI'], ['Space', 'AI'], ['Space', 'AI']]);
});

test('a date pattern spells the year, month, and day, and leaves every other character', () => {
  // Act
  const short = spellDate({ y: 2026, m: 1, d: 2 }, "MMM-D-'YY");
  const long = spellDate({ y: 2025, m: 9, d: 30 }, 'MMMM DD, YYYY');

  // Assert
  assert.equal(short, "Jan-2-'26");
  assert.equal(long, 'September 30, 2025');
});

test('a date axis gives each day a slot, weekends empty, and months their own when every date is a first', () => {
  // Act
  const days = dateAxis({ categories: ['2026-01-02', '2026-01-05', '2026-01-06'] });
  const months = dateAxis({ categories: ['2025-11-01', '2026-01-01'] });

  // Assert
  assert.deepEqual([days.unit, days.offsets, days.count], ['days', [0, 3, 4], 5]);
  assert.deepEqual([months.unit, months.offsets, months.count], ['months', [0, 2], 3]);
});

test('date labels step as the chart says, or at the finest step that keeps them apart', () => {
  // Arrange
  const categories = Array.from({ length: 60 }, (_, day) =>
    new Date(Date.UTC(2026, 0, 1 + day)).toISOString().slice(0, 10));
  const fixed = { categories, dates: { pattern: 'MMM-D', stepCount: 1, stepUnit: 'months' } };
  const automatic = { categories, dates: { pattern: 'MMM-D' } };

  // Act
  const monthly = dateLabels(fixed, dateAxis(fixed), 5, () => 0).map((label) => label.text);
  const fitted = dateLabels(automatic, dateAxis(automatic), 5, () => 30).map((label) => label.text);

  // Assert
  assert.deepEqual(monthly, ['Jan-1', 'Feb-1', 'Mar-1']);
  assert.deepEqual(fitted.slice(0, 3), ['Jan-1', 'Jan-8', 'Jan-15']);
});

test('a series drawn in no color leaves the legend, and bold labels draw bold', () => {
  // Arrange
  const stacked = chart({ type: 'stacked_column', grouping: 'stacked', colors: ['#111111', null], legend: 'bottom',
    labels: { show: 'value', position: 'center', bold: true } });

  // Act
  const drawn = texts(layoutChart(stacked, 400, 300, measure));

  // Assert
  assert.ok(drawn.some((text) => text.text === 'Space'));
  assert.ok(!drawn.some((text) => text.text === 'AI'));
  assert.ok(drawn.filter((text) => text.text === '3796').every((text) => text.bold));
});

test('a total labels the end of its stack, and a line over the columns runs on the secondary axis', () => {
  // Arrange
  const white = { show: 'value', position: 'center', format: null, color: '#FFFFFF', bold: false };
  const combo = chart({
    type: 'stacked_column',
    grouping: 'stacked',
    legend: 'bottom',
    series: [
      { name: 'Space', values: [3796, 4086], type: 'column', secondary: false, labels: white },
      { name: 'AI', values: [2620, 3201], type: 'column', secondary: false, labels: white },
      { name: 'Total', values: [6416, 7287], type: 'total', secondary: false,
        labels: { ...white, position: 'outside', color: null } },
      { name: 'Margin', values: [0.2, 0.25], type: 'line', secondary: true, labels: null }
    ],
    colors: ['#111111', '#222222', '#333333', '#444444'],
    valueAxis: { visible: true, gridlines: false, min: 0, max: 8000, step: 1000 },
    secondaryAxis: { visible: true, min: 0, max: 0.3, step: 0.05, format: null }
  });

  // Act
  const primitives = layoutChart(combo, 400, 300, measure);

  // Assert
  const columns = primitives.filter((primitive) => primitive.kind === 'rect' && primitive.fill !== '#444444' &&
    primitive.height > 20);
  const tops = [0, 1].map((point) => Math.min(...columns.filter((_, index) => index % 2 === point)
    .map((column) => column.y)));
  const totals = texts(primitives).filter((text) => text.text === '6416' || text.text === '7287');
  assert.equal(totals.length, 2);
  assert.ok(totals.every((total, point) => total.y < tops[point] && tops[point] - total.y < 20));
  assert.ok(totals.every((total) => total.color === undefined));
  const values = texts(primitives).filter((text) => text.anchor === 'end').map((text) => Number(text.text));
  assert.equal(Math.max(...values), 8000);
  const secondary = texts(primitives).filter((text) => text.anchor === 'start' && /^0\.\d+$|^0$/.test(text.text));
  assert.ok(secondary.length >= 2 && secondary.every((tick) => tick.x > 300));
  const line = primitives.findIndex((primitive) => primitive.kind === 'path' && primitive.stroke === '#444444');
  assert.ok(line > primitives.findLastIndex((primitive) => primitive.kind === 'rect' && primitive.height > 20));
  const legend = texts(primitives).filter((text) => text.anchor === 'start' && /^[A-Z]/.test(text.text));
  assert.deepEqual(legend.map((text) => text.text), ['Space', 'AI', 'Margin']);
  assert.ok(primitives.some((primitive) => primitive.kind === 'line' && primitive.stroke === '#444444'));
});

test('a line over clustered columns leaves the columns their width', () => {
  // Arrange
  const series = [{ name: 'Space', values: [3796, 4086] }, { name: 'AI', values: [2620, 1237], type: 'line' }];

  // Act
  const alone = layoutChart(chart({ series: series.slice(0, 1), colors: ['#111111'] }), 400, 300, measure);
  const combo = layoutChart(chart({ series }), 400, 300, measure);

  // Assert
  const width = (primitives) => primitives.find((primitive) => primitive.kind === 'rect').width;
  assert.equal(width(combo), width(alone));
  assert.equal(combo.filter((primitive) => primitive.kind === 'path').length, 1);
});

test('a marker sits on each point of its line, outlined in the line\'s color, and on its legend swatch', () => {
  // Arrange
  const markers = { shape: 'circle', size: 7, fill: '#FFFFFF' };
  const series = [{ name: 'Space', values: [3796, 4086] }, { name: 'AI', values: [2620, null], markers }];

  // Act
  const primitives = layoutChart(chart({ type: 'line', family: 'line', series, legend: 'bottom' }), 400, 300,
    measure);

  // Assert
  const circles = primitives.filter((primitive) => primitive.kind === 'path' && primitive.fill === '#FFFFFF');
  assert.equal(circles.length, 2);
  assert.ok(circles.every((circle) => circle.stroke === '#222222' && circle.d.includes('a3.5 3.5')));
});

test('a point read from a book traces to its cell, in its bar, its label, and its slice', () => {
  // Arrange
  const trace = (line) => ['2024', '2025'].map((column) => ({ book: 'acme', line, column }));
  const series = [
    { name: 'Space', values: [3796, 4086], traces: trace('space_revenue') },
    { name: 'AI', values: [2620, 1237], traces: trace('ai_revenue') }
  ];
  const labels = { show: 'value', position: 'outside', format: null, color: null, bold: false };

  // Act
  const columns = layoutChart(chart({ series, labels }), 400, 300, measure);
  const pie = layoutChart(chart({ type: 'pie', family: 'pie', series: series.slice(0, 1), labels }), 400, 300,
    measure);

  // Assert
  const traced = (primitives, kind) => primitives.filter((primitive) => primitive.kind === kind && primitive.trace)
    .map((primitive) => `${primitive.trace.line} ${primitive.trace.column}`);
  const cells = ['space_revenue 2024', 'space_revenue 2025', 'ai_revenue 2024', 'ai_revenue 2025'];
  assert.deepEqual(traced(columns, 'rect'), cells);
  assert.deepEqual(traced(columns, 'text'), cells);
  assert.deepEqual(traced(pie, 'path'), cells.slice(0, 2));
});

test('a line read from a book traces each point, with or without markers', () => {
  // Arrange
  const traces = ['2024', '2025'].map((column) => ({ book: 'acme', line: 'space_revenue', column }));
  const series = [{ name: 'Space', values: [3796, null], traces }];

  // Act
  const primitives = layoutChart(chart({ type: 'line', family: 'line', series, colors: ['#111111'] }), 400, 300,
    measure);

  // Assert
  const hits = primitives.filter((primitive) => primitive.kind === 'hit');
  assert.deepEqual(hits.map((hit) => hit.trace.column), ['2024']);
});


test('every bar, label, slice, and point names its series and point, read from a book or not', () => {
  // Arrange
  const labels = { show: 'value', position: 'outside', format: null, color: null, bold: false };
  const line = [{ name: 'Space', values: [3796, null] }];

  // Act
  const columns = layoutChart(chart({ labels }), 400, 300, measure);
  const pie = layoutChart(chart({ type: 'pie', family: 'pie', labels }), 400, 300, measure);
  const lines = layoutChart(chart({ type: 'line', family: 'line', series: line, colors: ['#111111'] }), 400, 300,
    measure);

  // Assert
  const points = (primitives, kind) => primitives.filter((primitive) => primitive.kind === kind && primitive.datum)
    .map(({ datum }) => `${datum.series} ${datum.point}`);
  assert.deepEqual(points(columns, 'rect'), ['0 0', '0 1', '1 0', '1 1']);
  assert.deepEqual(points(columns, 'text'), ['0 0', '0 1', '1 0', '1 1']);
  assert.deepEqual(points(pie, 'path'), ['0 0', '0 1']);
  assert.deepEqual(points(lines, 'hit'), ['0 0']);
});


test('a category axis without labels keeps its line and gives the plot the labels\' room', () => {
  // Act
  const labelled = layoutChart(chart(), 400, 300, measure);
  const bare = layoutChart(chart({ categoryLabels: false }), 400, 300, measure);

  // Assert
  const categories = (primitives) => texts(primitives).filter((text) => ['2024', '2025'].includes(text.text));
  const axisLines = (primitives) => primitives.filter((primitive) => primitive.kind === 'line' &&
    primitive.stroke === '#BFBFBF');
  assert.equal(categories(labelled).length, 2);
  assert.equal(categories(bare).length, 0);
  assert.equal(axisLines(bare).length, 1);
  assert.ok(axisLines(bare)[0].y1 > axisLines(labelled)[0].y1);
});


test('a reversed value axis puts its minimum at the top, and a line on it runs down as its values rise', () => {
  // Arrange
  const series = [{ name: 'Points', values: [80, 90] }, { name: 'Position', values: [1, 4], type: 'line',
    secondary: true }];
  const axis = { visible: true, gridlines: false, min: 1, max: 4, step: 1, reverse: true };

  // Act
  const primitives = layoutChart(chart({ series, valueAxis: { ...axis, min: 0, max: 100, step: 50 },
    secondaryAxis: axis }), 400, 300, measure);

  // Assert
  const tick = (text) => texts(primitives).find((primitive) => primitive.text === text).y;
  assert.ok(tick('1') < tick('4'));
  assert.ok(tick('0') < tick('100'));
  const line = primitives.find((primitive) => primitive.kind === 'path' && primitive.fill === 'none');
  const [, first, second] = line.d.match(/M\S+ (\S+) L\S+ (\S+)/);
  assert.ok(Number(first) < Number(second));
});

test('a line\'s point draws its own marker, outlined in its own color', () => {
  // Arrange
  const series = [{ name: 'Position', values: [1, 4], type: 'line', markers: { shape: 'circle', size: 5, fill: null },
    colors: ['#C00000', null], pointMarkers: [{ shape: 'square', size: 9, fill: '#FFFFFF' }, null] }];

  // Act
  const primitives = layoutChart(chart({ series }), 400, 300, measure);

  // Assert
  const markers = primitives.filter((primitive) => primitive.kind === 'path' && primitive.join === 'miter');
  assert.equal(markers.length, 2);
  assert.deepEqual([markers[0].fill, markers[0].stroke], ['#FFFFFF', '#C00000']);
  assert.match(markers[0].d, /h9v9/);
  assert.deepEqual([markers[1].fill, markers[1].stroke], ['#111111', '#111111']);
});

test('a point\'s own labels draw over its series\', and one shown none draws no label', () => {
  // Arrange
  const hidden = { show: 'none', position: '', format: null, color: null, bold: false };
  const series = [
    { name: 'Space', values: [3796, 4086], labels: { show: 'value', position: 'center', format: billions },
      pointLabels: [hidden, null] },
    { name: 'AI', values: [2620, 3201], type: 'line', labels: null,
      pointLabels: [null, { show: 'value', position: 'above', format: billions, color: '#C00000', bold: true }] }
  ];

  // Act
  const primitives = layoutChart(chart({ series }), 400, 300, measure);

  // Assert
  const labels = texts(primitives).filter((primitive) => primitive.datum);
  assert.deepEqual(labels.map((label) => [label.text, label.datum.series, label.datum.point]),
    [['$4.1B', 0, 1], ['$3.2B', 1, 1]]);
  assert.deepEqual([labels[1].color, labels[1].bold], ['#C00000', true]);
});


test('every primitive names the part of the chart it draws, and the series or point it draws for', () => {
  // Arrange
  const labels = { show: 'value', position: 'outside', format: null, color: null, bold: false };
  const series = [{ name: 'Space', values: [3796, 4086] }, { name: 'AI', values: [2620, 3201], type: 'line',
    markers: { shape: 'circle', size: 5 } }];
  const axis = { visible: true, gridlines: true, min: 0, max: 5000, step: 1000 };

  // Act
  const primitives = layoutChart(chart({ labels, series, title: 'Revenue', legend: 'bottom', valueAxis: axis }), 400,
    300, measure);

  // Assert
  const parts = (part) => primitives.filter((primitive) => primitive.part === part);
  const of = (part) => parts(part).map((primitive) => `${primitive.series ?? '-'} ${primitive.point ?? '-'}`);
  assert.deepEqual(parts('title').map((primitive) => primitive.text), ['Revenue']);
  assert.deepEqual([...new Set(of('legend'))], ['0 -', '1 -']);
  assert.deepEqual(of('bar'), ['0 0', '0 1']);
  assert.deepEqual(of('line'), ['1 -']);
  assert.deepEqual(of('marker'), ['1 0', '1 1']);
  assert.deepEqual(of('label'), ['0 0', '0 1', '1 0', '1 1']);
  assert.deepEqual(parts('gridline').length, 6);
  assert.deepEqual(parts('value_axis').map((primitive) => primitive.text), ['0', '1000', '2000', '3000', '4000',
    '5000']);
  assert.deepEqual(parts('category_axis').map((primitive) => primitive.text ?? 'line'), ['line', '2024', '2025']);
  assert.equal(primitives.filter((primitive) => !primitive.part).length, 0);
});


test('a line\'s first and last labels and its axis\'s end dates draw inside the chart, as PowerPoint keeps them', () => {
  // Arrange
  const categories = Array.from({ length: 80 }, (_, week) =>
    new Date(Date.UTC(2025, 2, 28 + week * 7)).toISOString().slice(0, 10));
  const values = categories.map((_, week) => (week === 12 ? 200 : 40 + week));
  const below = { show: 'value', position: 'below', format: { ...billions, scale: 1, decimals: 2, suffix: '' } };
  const pointLabels = values.map((_, week) =>
    (week === 0 || week === 79 ? below : week === 12 ? { ...below, position: 'above' } : null));
  const series = [{ name: 'Close', values, type: 'line', pointLabels }];
  const dates = { pattern: "MMM 'YY", stepCount: 3, stepUnit: 'months' };

  // Act
  const primitives = layoutChart(chart({ type: 'line', family: 'line', grouping: 'standard', categories, dates,
    series, valueAxis: { visible: false, gridlines: false, min: 0, max: 200, step: 50 } }), 430, 132, measure);

  // Assert
  const drawn = texts(primitives);
  const labels = drawn.filter((text) => text.part === 'label');
  assert.deepEqual(labels.map((label) => label.text), ['$40.00', '$200.00', '$119.00']);
  for (const text of drawn) {
    const width = measure(text.text);
    const left = { start: text.x, end: text.x - width }[text.anchor] ?? text.x - width / 2;
    assert.ok(left >= 4.99 && left + width <= 425.01, `${text.text} runs from ${left} to ${left + width}`);
    assert.ok(text.y - 6 >= 5 && text.y + 6 <= 127, `${text.text} sits at ${text.y}`);
  }
  const line = primitives.find((primitive) => primitive.kind === 'path' && primitive.fill === 'none');
  const [, firstX] = line.d.match(/^M(\S+) /);
  assert.ok(Math.abs(labels[0].x - Number(firstX)) < 0.5, 'the first label stays under its point');
  assert.deepEqual(drawn.filter((text) => text.clipped).map((text) => text.text), ["Mar '25", "Sep '26"]);
});

test('a line\'s label left or right of its point sits level with it, its near end a gap from the point', () => {
  // Arrange
  const drawn = (position) => {
    const series = [{ name: 'Close', values: [100, 150, 200], type: 'line',
      labels: { show: 'value', position, format: null } }];
    return layoutChart(chart({ type: 'line', family: 'line', grouping: 'standard', series,
      valueAxis: { visible: false, gridlines: false, min: 0, max: 200, step: 50 } }), 400, 200, measure);
  };
  const middle = (primitives) => primitives.filter((primitive) => primitive.part === 'label')[1];

  // Act
  const above = middle(drawn('above'));
  const right = middle(drawn('right'));
  const left = middle(drawn('left'));

  // Assert
  const gap = 10 * 0.4;
  assert.deepEqual([right.anchor, right.x, right.y], ['start', above.x + gap, above.y + 10]);
  assert.deepEqual([left.anchor, left.x, left.y], ['end', above.x - gap, above.y + 10]);
});

test('a plot\'s insets make room around it and its axis labels, which data labels may take', () => {
  // Arrange
  const series = [{ name: 'Close', values: [100, 200], type: 'line',
    labels: { show: 'value', position: 'above', format: null } }];
  const drawn = (plotInsets) => layoutChart(chart({ type: 'line', family: 'line', grouping: 'standard', series,
    valueAxis: { visible: false, gridlines: false, min: 0, max: 200, step: 50 }, plotInsets }), 400, 200, measure);

  // Act
  const plain = drawn(undefined);
  const inset = drawn([10, 30, 10, 20]);

  // Assert
  const axis = (primitives) => primitives.find((primitive) => primitive.part === 'category_axis' &&
    primitive.kind === 'line');
  const peak = (primitives) => texts(primitives).find((text) => text.text === '200');
  const category = (primitives, name) => texts(primitives).find((text) => text.text === name);
  assert.equal(axis(inset).x1, axis(plain).x1 + 10);
  assert.equal(axis(inset).x2, axis(plain).x2 - 10);
  assert.equal(category(inset, '2025').y, category(plain, '2025').y - 20);
  assert.equal(peak(plain).y, 11);
  assert.equal(peak(inset).y, 25);
});

test('an outside label runs past the plot into the plot\'s insets, as PowerPoint draws it above the plot area', () => {
  // Arrange
  const labels = { show: 'value', position: 'outside', format: null };
  const drawn = (plotInsets) => texts(layoutChart(chart({ labels, series: [{ name: 'Rent', values: [800, 200] }],
    valueAxis: { visible: false, gridlines: false, min: 0, max: 800, step: 200 }, plotInsets }), 400, 200, measure));

  // Act
  const inset = drawn([0, 30, 0, 0]);

  // Assert
  const peak = inset.find((text) => text.text === '800');
  const axis = inset.find((text) => text.text === '2024');
  assert.ok(peak.y + 6 <= 35, `${peak.y} sits over its bar, whose end is at 35`);
  assert.equal(peak.moved, undefined);
  assert.ok(axis);
});

test('an outside label with no room past its bar\'s end is marked with the edge that pushed it back', () => {
  // Arrange
  const labels = { show: 'value', position: 'outside', format: null };
  const drawn = (max, overrides = {}) => texts(layoutChart(chart({ labels,
    series: [{ name: 'Rent', values: [800, -200] }],
    valueAxis: { visible: false, gridlines: false, min: -200, max, step: 200 }, ...overrides }), 400, 200, measure))
    .filter((text) => text.part === 'label');

  // Act
  const columns = drawn(800);
  const roomy = drawn(1000);
  const bars = drawn(800, { type: 'clustered_bar', family: 'bar' });

  // Assert
  assert.deepEqual(columns.map((label) => [label.text, label.moved]), [['800', 'top'], ['-200', 'bottom']]);
  assert.deepEqual(roomy.map((label) => label.moved), [undefined, 'bottom']);
  assert.deepEqual(bars.map((label) => [label.text, label.moved]), [['800', 'right'], ['-200', 'left']]);
});