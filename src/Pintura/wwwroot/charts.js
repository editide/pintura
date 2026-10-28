'use strict';

// Lays a chart out as primitives (rectangles, lines, paths, and text in points) that render.js draws as SVG. Layout
// is pure, given a function measuring text, so axis scaling and label placement run under node --test.

const PERCENT = { scale: 100, decimals: 0, separator: false, prefix: '', suffix: '%', negative: 'minus' };
const LINE_WIDTH = 2.25;
const RULE_WIDTH = 0.75;
const DOUGHNUT_HOLE = 0.5;
const MAX_TICKS = 100;

// Excel's General format: up to ten significant digits, no separators.
function formatGeneral(value) {
  return String(Number(value.toPrecision(10)));
}

function formatNumber(value, format) {
  if (!format) {
    return formatGeneral(value);
  }
  const scaled = value * format.scale;
  let [whole, fraction] = Math.abs(scaled).toFixed(format.decimals).split('.');
  if (format.separator) {
    whole = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  }
  const text = `${format.prefix}${whole}${fraction === undefined ? '' : `.${fraction}`}${format.suffix}`;
  if (scaled >= 0) {
    return text;
  }
  return format.negative === 'parentheses' ? `(${text})` : `-${text}`;
}

function tidy(value) {
  return Number(value.toPrecision(12));
}

// The ticks from an axis's min to its max, a step apart; the payload resolves all three. A step too small to draw,
// such as one in billions on values in millions, ticks only the ends rather than hanging the page.
function axisTicks({ min, max, step }) {
  if (!((max - min) / step <= MAX_TICKS)) {
    return { min, max, unit: step, ticks: [min, max] };
  }
  const ticks = [];
  for (let index = 0; ; index++) {
    const tick = tidy(min + index * step);
    if (tick > max + step * 1e-9) {
      break;
    }
    ticks.push(tick);
  }
  return { min, max, unit: step, ticks };
}

function seriesType(chart, series) {
  return series.type ?? chart.family;
}

// Each series' extent per category as [from, to]. A series of the chart's type stacks when the chart does, up from
// zero for positives and down for negatives, and apart on each axis; percent stacking divides by the category's total
// size. A line over columns stands alone, and a total sits at the end of its stack, where its label goes. A gap is
// null.
function stackSeries(chart) {
  const percent = chart.grouping === 'percent_stacked';
  const stacks = chart.series.map((series) =>
    chart.grouping !== 'clustered' && seriesType(chart, series) === chart.family);
  const axis = (series) => (series.secondary ? 1 : 0);
  const sides = () => [0, 1].map(() => chart.categories.map(() => 0));
  const sizes = sides();
  chart.series.forEach((series, index) => series.values.forEach((raw, point) => {
    if (stacks[index]) {
      sizes[axis(series)][point] += Math.abs(raw ?? 0);
    }
  }));
  const above = sides();
  const below = sides();
  const extents = chart.series.map((series, index) => series.values.map((raw, point) => {
    if (raw === null || raw === undefined || seriesType(chart, series) === 'total') {
      return null;
    }
    if (!stacks[index]) {
      return [0, raw];
    }
    const size = sizes[axis(series)][point];
    const value = percent ? (size === 0 ? 0 : raw / size) : raw;
    // Lines stack every value on the running total; bars stack positives and negatives apart.
    const side = chart.family === 'line' || value >= 0 ? above[axis(series)] : below[axis(series)];
    const from = side[point];
    side[point] += value;
    return [from, side[point]];
  }));
  return extents.map((series, index) => seriesType(chart, chart.series[index]) !== 'total' ? series
    : chart.series[index].values.map((raw, point) => {
      if (raw === null || raw === undefined) {
        return null;
      }
      const end = raw >= 0 ? above[0][point] : below[0][point];
      return [end, end];
    }));
}

function colorAt(chart, index) {
  return chart.colors.length === 0 ? null : chart.colors[index % chart.colors.length];
}

// Words greedily onto lines no wider than `width`; a word wider than that keeps a line to itself, and a line feed
// always starts a new line, as in a PowerPoint category label.
function wrap(text, width, measure) {
  const lines = [];
  for (const paragraph of text.split('\n')) {
    const start = lines.length;
    for (const word of paragraph.split(/\s+/).filter(Boolean)) {
      const last = lines.length - 1;
      if (last >= start && measure(`${lines[last]} ${word}`) <= width) {
        lines[last] = `${lines[last]} ${word}`;
      } else {
        lines.push(word);
      }
    }
  }
  return lines.length === 0 ? [''] : lines;
}

// A series or slice drawn in no color has no entry, and nor has a total, which draws only its labels.
function legendEntries(chart) {
  const pie = chart.family === 'pie';
  const names = pie ? chart.categories : chart.series.map((series) => series.name);
  const entries = names.map((name, index) => ({ name, index, color: colorAt(chart, index),
    line: !pie && seriesType(chart, chart.series[index]) === 'line',
    markers: pie ? null : chart.series[index].markers,
    total: !pie && seriesType(chart, chart.series[index]) === 'total' }))
    .filter((entry) => entry.color !== null && !entry.total);
  // PowerPoint lists a side legend of an upward stack from its top, so the entries line up with what they name.
  const upward = chart.grouping !== 'clustered' && (chart.family === 'column' || chart.family === 'line');
  return upward && (chart.legend === 'left' || chart.legend === 'right') ? entries.reverse() : entries;
}

// Places the legend inside `box` and returns what is left for the plot.
function layoutLegend(chart, box, context) {
  const { size, measure, out } = context;
  const entries = legendEntries(chart);
  const swatch = (entry) => (entry.line ? size * 1.6 : size * 0.7);
  const gap = size * 0.4;
  const spacing = size * 1.2;
  const lineHeight = size * 1.2;
  const widths = entries.map((entry) => swatch(entry) + gap + measure(entry.name));
  const draw = (entry, x, y) => {
    const width = swatch(entry);
    const of = { part: 'legend', [chart.family === 'pie' ? 'point' : 'series']: entry.index };
    if (entry.line) {
      out.push({ kind: 'line', x1: x, y1: y, x2: x + width, y2: y, stroke: entry.color, width: LINE_WIDTH, ...of });
      if (entry.markers) {
        out.push({ ...pointMarker(entry.markers, entry.color, x + width / 2, y), ...of });
      }
    } else {
      out.push({ kind: 'rect', x, y: y - width / 2, width, height: width, fill: entry.color, ...of });
    }
    out.push({ kind: 'text', x: x + width + gap, y, text: entry.name, anchor: 'start', ...of });
  };

  if (chart.legend === 'top' || chart.legend === 'bottom') {
    const rows = [[]];
    let used = 0;
    entries.forEach((entry, index) => {
      const width = widths[index];
      if (rows[rows.length - 1].length > 0 && used + spacing + width > box.width) {
        rows.push([]);
        used = 0;
      }
      used += (rows[rows.length - 1].length > 0 ? spacing : 0) + width;
      rows[rows.length - 1].push(index);
    });
    const height = rows.length * lineHeight;
    const top = chart.legend === 'top' ? box.y : box.y + box.height - height;
    rows.forEach((row, rowIndex) => {
      const rowWidth = row.reduce((sum, index) => sum + widths[index], 0) + spacing * (row.length - 1);
      let x = box.x + (box.width - rowWidth) / 2;
      for (const index of row) {
        draw(entries[index], x, top + lineHeight * (rowIndex + 0.5));
        x += widths[index] + spacing;
      }
    });
    const taken = height + size * 0.6;
    return chart.legend === 'top'
      ? { ...box, y: box.y + taken, height: box.height - taken }
      : { ...box, height: box.height - taken };
  }

  const width = Math.max(...widths);
  const height = entries.length * lineHeight;
  const left = chart.legend === 'left' ? box.x : box.x + box.width - width;
  const top = box.y + (box.height - height) / 2;
  entries.forEach((entry, index) => draw(entry, left, top + lineHeight * (index + 0.5)));
  const taken = width + size;
  return chart.legend === 'left'
    ? { ...box, x: box.x + taken, width: box.width - taken }
    : { ...box, width: box.width - taken };
}

// The series and point a mark draws, for a click to select and the overlay of changes to mark, and the book cell behind
// it, for the page to open on a Ctrl-click; only a chart read from a book has cells.
function traced(primitive, series, index, point) {
  const datum = { ...primitive, series: index, point, datum: { series: index, point } };
  const trace = series.traces && series.traces[point];
  return trace ? { ...datum, trace } : datum;
}

// A point's own labels, or else its series'; none where neither shows one.
function labelsAt(series, point) {
  const labels = series.pointLabels?.[point] || series.labels;
  return labels && labels.show !== 'none' ? labels : null;
}

function labelColor(labels, value) {
  const format = labels.format;
  return value < 0 && format && format.negativeColor ? format.negativeColor : labels.color || undefined;
}

// A bar's label, where `from` and `to` are the bar's base and end along the value direction, in points, and `across`
// is its middle the other way. Outside runs past the end, so a negative bar's label sits below or to its left, past
// the plot and over any axis labels there, as PowerPoint draws it.
function barLabel(labels, context, raw, across, from, to, horizontal, trace = (primitive) => primitive) {
  const { size, measure, out } = context;
  const text = formatNumber(raw, labels.format);
  const lineHeight = size * 1.2;
  const gap = size * 0.3;
  const forward = Math.sign(to - from) || (horizontal ? 1 : -1);
  const extent = horizontal ? measure(text) : lineHeight;
  let along;
  switch (labels.position) {
    case 'outside':
      along = to + forward * (gap + extent / 2);
      break;
    case 'inside':
      along = to - forward * (gap + extent / 2);
      break;
    case 'inside_base':
      along = from + forward * (gap + extent / 2);
      break;
    default:
      along = (from + to) / 2;
  }
  const label = { kind: 'text', part: 'label', text, anchor: 'middle', color: labelColor(labels, raw),
    bold: labels.bold };
  out.push(trace(horizontal ? { ...label, x: along, y: across } : { ...label, x: across, y: along }));
}

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October',
  'November', 'December'];
const DAY = 86400000;
const UNITS = ['days', 'months', 'years'];
// The steps a date axis tries, finest first, when the chart names none.
const DATE_STEPS = [[1, 'days'], [2, 'days'], [7, 'days'], [14, 'days'], [1, 'months'], [2, 'months'], [3, 'months'],
  [6, 'months'], [1, 'years'], [2, 'years'], [5, 'years'], [10, 'years']];

function isoDate(text) {
  const [y, m, d] = text.split('-').map(Number);
  return { y, m, d };
}

// A date as a pattern spells it, as NumberFormat.SpellDate in Model.cs does.
function spellDate(date, pattern) {
  return pattern.replace(/Y+|M+|D+/g, (run) => ({
    YYYY: String(date.y).padStart(4, '0'),
    YY: String(date.y % 100).padStart(2, '0'),
    MMMM: MONTHS[date.m - 1],
    MMM: MONTHS[date.m - 1].slice(0, 3),
    MM: String(date.m).padStart(2, '0'),
    M: String(date.m),
    DD: String(date.d).padStart(2, '0'),
    D: String(date.d)
  })[run] ?? run);
}

function unitIndex(date, unit) {
  if (unit === 'years') {
    return date.y;
  }
  return unit === 'months' ? date.y * 12 + date.m - 1 : Date.UTC(date.y, date.m - 1, date.d) / DAY;
}

// A month past the end of a shorter month lands on its last day.
function addDates(date, count, unit) {
  if (unit === 'days') {
    const moved = new Date(Date.UTC(date.y, date.m - 1, date.d + count));
    return { y: moved.getUTCFullYear(), m: moved.getUTCMonth() + 1, d: moved.getUTCDate() };
  }
  const months = date.y * 12 + date.m - 1 + (unit === 'years' ? count * 12 : count);
  const y = Math.floor(months / 12);
  const m = months % 12 + 1;
  return { y, m, d: Math.min(date.d, new Date(Date.UTC(y, m, 0)).getUTCDate()) };
}

// Excel's date axis: a slot per day, or per month or year when every date falls on the first of one, with each
// category in the slot of its date and empty slots between.
function dateAxis(chart) {
  const dates = chart.categories.map(isoDate);
  const unit = dates.every((date) => date.d === 1) ? (dates.every((date) => date.m === 1) ? 'years' : 'months')
    : 'days';
  const indexes = dates.map((date) => unitIndex(date, unit));
  const start = Math.min(...indexes);
  return { dates, unit, start, offsets: indexes.map((index) => index - start),
    count: Math.max(...indexes) - start + 1 };
}

// Labels from the first date on, every step, as `{ offset, text }`; with no step, the finest that keeps each label
// `apart(text, previous)` points from the one before, `slot` points being one unit of the axis.
function dateLabels(chart, axis, slot, apart) {
  const at = (count, unit) => {
    const labels = [];
    for (let index = 0; ; index++) {
      const date = addDates(axis.dates[0], index * count, unit);
      const offset = unitIndex(date, axis.unit) - axis.start;
      if (offset > axis.count - 1) {
        return labels;
      }
      labels.push({ offset, text: spellDate(date, chart.dates.pattern) });
    }
  };
  if (chart.dates.stepCount) {
    return at(chart.dates.stepCount, chart.dates.stepUnit);
  }
  const steps = DATE_STEPS.filter(([, unit]) => UNITS.indexOf(unit) >= UNITS.indexOf(axis.unit));
  let labels = [];
  for (const [count, unit] of steps) {
    labels = at(count, unit);
    if (labels.every((label, index) => index === 0 ||
      (label.offset - labels[index - 1].offset) * slot >= apart(label.text, labels[index - 1].text))) {
      break;
    }
  }
  return labels;
}

// A box less `[left, top, right, bottom]` points.
function insetBox(box, insets) {
  const [left, top, right, bottom] = insets ?? [0, 0, 0, 0];
  return { x: box.x + left, y: box.y + top, width: Math.max(1, box.width - left - right),
    height: Math.max(1, box.height - top - bottom) };
}

// The plot and its axis labels sit inside the plot's insets, the room for data labels, though the plot narrows only
// as far as its end axis labels need to fit the chart. A data label still past the chart's edge moves back in, as
// PowerPoint moves it. PowerPoint can't narrow the plot, so an axis label the plot narrowed for is marked clipped.
function layoutCartesian(chart, box, context) {
  const { size, out } = context;
  const span = (text) => {
    const width = context.measure(text.text, text.bold);
    const left = { start: text.x, end: text.x - width }[text.anchor] ?? text.x - width / 2;
    return [left, left + width];
  };
  const outside = ([start, end]) => start < box.x - 0.01 || end > box.x + box.width + 0.01;
  let inner = insetBox(box, chart.plotInsets);
  let drawn;
  let result;
  let clipped = null;
  for (let pass = 0; pass < 4; pass++) {
    drawn = [];
    result = layoutPlot(chart, inner, { ...context, out: drawn });
    const texts = drawn.filter((primitive) => primitive.kind === 'text' && primitive.part !== 'label');
    clipped ??= new Set(texts.filter((text) => outside(span(text))).map((text) => `${text.part}:${text.text}`));
    const axes = texts.map(span);
    const left = Math.max(0, box.x - Math.min(...axes.map(([start]) => start)));
    const right = Math.max(0, Math.max(...axes.map(([, end]) => end)) - (box.x + box.width));
    if (left + right < 0.01) {
      break;
    }
    inner = { ...inner, x: inner.x + left, width: Math.max(1, inner.width - left - right) };
  }
  const half = size * 0.6;
  for (const primitive of drawn) {
    if (primitive.kind === 'text' && clipped.has(`${primitive.part}:${primitive.text}`)) {
      primitive.clipped = true;
    }
    if (primitive.kind === 'text' && primitive.part === 'label') {
      const [start, end] = span(primitive);
      const dx = Math.max(0, box.x - start) - Math.max(0, end - (box.x + box.width));
      const dy = Math.min(box.y + box.height - half, Math.max(box.y + half, primitive.y)) - primitive.y;
      primitive.x += dx;
      primitive.y += dy;
      // Pushed along the value axis past the gap it keeps, a label covers its own bar or point; across, it only
      // shifts aside.
      const pushed = chart.family === 'bar' ? dx : -dy;
      if (Math.abs(pushed) > size * 0.3 + 0.5) {
        primitive.moved = chart.family === 'bar' ? (pushed > 0 ? 'left' : 'right') : (pushed > 0 ? 'bottom' : 'top');
      }
    }
  }
  out.push(...drawn);
  return result;
}

// Columns and bars first, lines over them, then every label; a secondary axis's ticks sit right of the plot.
function layoutPlot(chart, box, context) {
  const { size, measure, out } = context;
  const horizontal = chart.family === 'bar';
  const lineHeight = size * 1.2;
  const gap = size * 0.4;
  const extents = stackSeries(chart);
  const valueAxis = chart.valueAxis;
  const secondaryAxis = chart.secondaryAxis;
  const showValues = valueAxis.visible;
  const showSecondary = Boolean(secondaryAxis && secondaryAxis.visible);
  const showCategories = chart.categoryAxis;
  const showCategoryLabels = showCategories && chart.categoryLabels !== false;
  const valueFormat = valueAxis.format ?? (chart.grouping === 'percent_stacked' ? PERCENT : null);
  const dates = chart.dates ? dateAxis(chart) : null;
  const count = dates ? dates.count : Math.max(1, chart.categories.length);
  const offsets = dates ? dates.offsets : chart.categories.map((_, point) => point);
  const spelled = dates ? dates.dates.map((date) => spellDate(date, chart.dates.pattern)) : chart.categories;
  const scale = axisTicks(valueAxis);
  const secondaryScale = secondaryAxis ? axisTicks(secondaryAxis) : null;
  let plot;
  let categoryLines = chart.categories.map((category) => [category]);
  if (horizontal) {
    const categoryWidth = showCategoryLabels
      ? Math.min(box.width * 0.4, Math.max(0, ...spelled.map((category) => measure(category)))) + gap
      : 0;
    const valueHeight = showValues ? lineHeight + gap : 0;
    plot = { x: box.x + categoryWidth, y: box.y, width: box.width - categoryWidth, height: box.height - valueHeight };
    categoryLines = dates ? [] : chart.categories.map((category) => wrap(category, categoryWidth - gap, measure));
  } else {
    const tickWidth = (axis, format) =>
      Math.max(...axis.ticks.map((tick) => measure(formatNumber(tick, format)))) + gap;
    let categoryHeight = showCategoryLabels ? lineHeight + gap : 0;
    for (let pass = 0; pass < 2; pass++) {
      const height = box.height - categoryHeight;
      const valueWidth = showValues ? tickWidth(scale, valueFormat) : 0;
      const secondaryWidth = showSecondary ? tickWidth(secondaryScale, secondaryAxis.format) : 0;
      plot = { x: box.x + valueWidth, y: box.y, width: box.width - valueWidth - secondaryWidth, height };
      categoryLines = dates ? []
        : chart.categories.map((category) => wrap(category, plot.width / count - gap, measure));
      const lines = Math.max(1, ...categoryLines.map((lines) => lines.length));
      const needed = showCategoryLabels ? lines * lineHeight + gap : 0;
      if (needed === categoryHeight) {
        break;
      }
      categoryHeight = needed;
    }
  }

  const position = (value, secondary = false) => {
    const axis = secondary ? secondaryScale : scale;
    const clamped = Math.min(axis.max, Math.max(axis.min, value));
    const reverse = (secondary ? secondaryAxis : valueAxis).reverse;
    const share = (reverse ? axis.max - clamped : clamped - axis.min) / (axis.max - axis.min);
    return horizontal ? plot.x + plot.width * share : plot.y + plot.height * (1 - share);
  };
  const slot = (horizontal ? plot.height : plot.width) / count;
  const slotStart = (point) => (horizontal ? plot.y : plot.x) + slot * offsets[point];
  const baseline = position(Math.min(scale.max, Math.max(scale.min, 0)));

  if (valueAxis.gridlines) {
    for (const tick of scale.ticks) {
      const at = position(tick);
      out.push(horizontal
        ? { kind: 'line', part: 'gridline', x1: at, y1: plot.y, x2: at, y2: plot.y + plot.height,
          stroke: chart.gridColor, width: RULE_WIDTH }
        : { kind: 'line', part: 'gridline', x1: plot.x, y1: at, x2: plot.x + plot.width, y2: at,
          stroke: chart.gridColor, width: RULE_WIDTH });
    }
  }

  const labels = [];
  const labelContext = { ...context, out: labels };
  const types = chart.series.map((series) => seriesType(chart, series));
  const bars = types.map((type, index) => (type === 'column' || type === 'bar' ? index : -1))
    .filter((index) => index >= 0);
  const clustered = chart.grouping === 'clustered';
  const places = clustered ? bars.length : 1;
  const overlap = (chart.overlap ?? 0) / 100;
  const gapWidth = (chart.gapWidth ?? 150) / 100;
  const thickness = slot / (places - (places - 1) * overlap + gapWidth);
  bars.forEach((index, place) => {
    const series = chart.series[index];
    const color = colorAt(chart, index);
    extents[index].forEach((extent, point) => {
      if (!extent) {
        return;
      }
      const start = slotStart(point) + gapWidth * thickness / 2 + (clustered ? place * thickness * (1 - overlap) : 0);
      const from = position(extent[0]);
      const to = position(extent[1]);
      const low = Math.min(from, to);
      const length = Math.abs(to - from);
      const fill = series.colors?.[point] || color;
      out.push(traced(horizontal
        ? { kind: 'rect', part: 'bar', x: low, y: start, width: length, height: thickness, fill }
        : { kind: 'rect', part: 'bar', x: start, y: low, width: thickness, height: length, fill }, series, index,
        point));
      const labels = labelsAt(series, point);
      if (labels) {
        barLabel(labels, labelContext, series.values[point], start + thickness / 2, from, to, horizontal,
          (primitive) => traced(primitive, series, index, point));
      }
    });
  });
  types.forEach((type, index) => {
    const series = chart.series[index];
    if (type === 'total') {
      extents[index].forEach((extent, point) => {
        const labels = labelsAt(series, point);
        if (extent && labels) {
          const across = slotStart(point) + slot / 2;
          barLabel(labels, labelContext, series.values[point], across, baseline, position(extent[1]),
            horizontal, (primitive) => traced(primitive, series, index, point));
        }
      });
    }
    if (type !== 'line') {
      return;
    }
    let d = '';
    let drawing = false;
    const points = [];
    extents[index].forEach((extent, point) => {
      if (!extent) {
        drawing = false;
        return;
      }
      const x = slotStart(point) + slot / 2;
      const y = position(extent[1], series.secondary);
      d += `${drawing ? 'L' : 'M'}${roundChart(x)} ${roundChart(y)} `;
      drawing = true;
      points.push([x, y, point]);
      const labels = labelsAt(series, point);
      if (labels) {
        lineLabel(labels, labelContext, series.values[point], x, y,
          (primitive) => traced(primitive, series, index, point));
      }
    });
    const color = colorAt(chart, index);
    out.push({ kind: 'path', part: 'line', series: index, d: d.trim(), fill: 'none', stroke: color,
      width: LINE_WIDTH });
    points.forEach(([x, y, point]) => {
      const markers = series.pointMarkers?.[point] || series.markers;
      out.push(traced({ part: 'marker', ...(markers && markers.shape !== 'none'
        ? pointMarker(markers, series.colors?.[point] || color, x, y)
        : { kind: 'hit', x, y, r: size * 0.5 }) }, series, index, point));
    });
  });

  if (showCategories) {
    out.push(horizontal
      ? { kind: 'line', part: 'category_axis', x1: baseline, y1: plot.y, x2: baseline, y2: plot.y + plot.height,
        stroke: chart.axisColor, width: RULE_WIDTH }
      : { kind: 'line', part: 'category_axis', x1: plot.x, y1: baseline, x2: plot.x + plot.width, y2: baseline,
        stroke: chart.axisColor, width: RULE_WIDTH });
  }
  if (showCategoryLabels) {
    if (dates) {
      const apart = horizontal ? () => lineHeight : (text, previous) => (measure(text) + measure(previous)) / 2 + gap;
      categoryLines = [];
      for (const label of dateLabels(chart, dates, slot, apart)) {
        categoryLines[label.offset] = [label.text];
      }
    }
    categoryLines.forEach((lines, offset) => {
      const middle = (horizontal ? plot.y : plot.x) + slot * (offset + 0.5);
      lines.forEach((line, index) => {
        out.push(horizontal
          ? { kind: 'text', part: 'category_axis', x: plot.x - gap,
            y: middle + (index - (lines.length - 1) / 2) * lineHeight, text: line, anchor: 'end' }
          : { kind: 'text', part: 'category_axis', x: middle,
            y: plot.y + plot.height + gap + lineHeight * (index + 0.5), text: line, anchor: 'middle' });
      });
    });
  }
  if (showValues) {
    for (const tick of scale.ticks) {
      const text = formatNumber(tick, valueFormat);
      out.push(horizontal
        ? { kind: 'text', part: 'value_axis', x: position(tick), y: plot.y + plot.height + gap + lineHeight / 2, text,
          anchor: 'middle' }
        : { kind: 'text', part: 'value_axis', x: plot.x - gap, y: position(tick), text, anchor: 'end' });
    }
  }
  if (showSecondary) {
    for (const tick of secondaryScale.ticks) {
      out.push({ kind: 'text', part: 'secondary_axis', x: plot.x + plot.width + gap, y: position(tick, true),
        text: formatNumber(tick, secondaryAxis.format), anchor: 'start' });
    }
  }
  out.push(...labels);
  return { plot, scale, secondaryScale };
}

// A marker is outlined in its line's color and width, centered on the point, and `size` across, as PowerPoint's is.
function pointMarker(markers, color, x, y) {
  const r = markers.size / 2;
  const d = {
    circle: `M${roundChart(x - r)} ${roundChart(y)}a${r} ${r} 0 1 0 ${2 * r} 0a${r} ${r} 0 1 0 ${-2 * r} 0Z`,
    square: `M${roundChart(x - r)} ${roundChart(y - r)}h${2 * r}v${2 * r}h${-2 * r}Z`,
    diamond: `M${roundChart(x)} ${roundChart(y - r)}l${r} ${r}l${-r} ${r}l${-r} ${-r}Z`,
    triangle: `M${roundChart(x)} ${roundChart(y - r)}l${r} ${2 * r}h${-2 * r}Z`
  }[markers.shape];
  return { kind: 'path', d, fill: markers.fill || color, stroke: color, width: LINE_WIDTH, join: 'miter' };
}

function lineLabel(labels, context, raw, x, y, trace) {
  const { size, out } = context;
  const gap = size * 0.4;
  const offset = gap + size * 0.6;
  const along = { above: y - offset, below: y + offset }[labels.position] ?? y;
  const across = { left: x - gap, right: x + gap }[labels.position] ?? x;
  const anchor = { left: 'end', right: 'start' }[labels.position] ?? 'middle';
  out.push(trace({ kind: 'text', part: 'label', x: across, y: along, text: formatNumber(raw, labels.format), anchor,
    color: labelColor(labels, raw), bold: labels.bold }));
}

function roundChart(value) {
  return Math.round(value * 100) / 100;
}

function arcPoint(cx, cy, radius, angle) {
  return [cx + radius * Math.cos(angle), cy + radius * Math.sin(angle)];
}

// A slice from `start` to `end` radians, clockwise from three o'clock, between two radii.
function slicePath(cx, cy, inner, outer, start, end) {
  if (end - start >= Math.PI * 2 - 1e-9) {
    const ring = (radius) => `M${roundChart(cx + radius)} ${roundChart(cy)} A${roundChart(radius)} ${roundChart(radius)} 0 1 1 ` +
      `${roundChart(cx - radius)} ${roundChart(cy)} A${roundChart(radius)} ${roundChart(radius)} 0 1 1 ${roundChart(cx + radius)} ${roundChart(cy)}Z`;
    return inner > 0 ? `${ring(outer)} ${ring(inner)}` : ring(outer);
  }
  const large = end - start > Math.PI ? 1 : 0;
  const [x1, y1] = arcPoint(cx, cy, outer, start);
  const [x2, y2] = arcPoint(cx, cy, outer, end);
  let d = `M${roundChart(x1)} ${roundChart(y1)} A${roundChart(outer)} ${roundChart(outer)} 0 ${large} 1 ${roundChart(x2)} ${roundChart(y2)} `;
  if (inner > 0) {
    const [x3, y3] = arcPoint(cx, cy, inner, end);
    const [x4, y4] = arcPoint(cx, cy, inner, start);
    d += `L${roundChart(x3)} ${roundChart(y3)} A${roundChart(inner)} ${roundChart(inner)} 0 ${large} 0 ${roundChart(x4)} ${roundChart(y4)}Z`;
  } else {
    d += `L${roundChart(cx)} ${roundChart(cy)}Z`;
  }
  return d;
}

// Pies start at twelve o'clock and run clockwise, as PowerPoint's do; a doughnut draws a ring per series, the
// first innermost.
function layoutPie(chart, box, context) {
  const { size, measure, out } = context;
  const lineHeight = size * 1.2;
  const doughnut = chart.type === 'doughnut';
  const labels = chart.series[0] && chart.series[0].labels;
  const texts = chart.series.map((series) => {
    const labels = series.labels;
    const total = series.values.reduce((sum, value) => sum + Math.abs(value ?? 0), 0);
    return series.values.map((value) => {
      if (value === null || value === undefined || !labels) {
        return null;
      }
      return labels.show === 'percent'
        ? formatNumber(total === 0 ? 0 : Math.abs(value) / total, labels.format ?? PERCENT)
        : formatNumber(value, labels.format);
    });
  });
  const outside = labels && !doughnut && labels.position === 'outside';
  const room = outside ? Math.max(0, ...texts.flat().filter(Boolean).map((text) => measure(text))) + size * 0.6 : 0;
  const radius = Math.max(1, Math.min(box.width / 2 - room, box.height / 2 - (outside ? lineHeight + size * 0.6 : 0)));
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  const hole = doughnut ? radius * DOUGHNUT_HOLE : 0;
  const rings = doughnut ? chart.series.length : 1;
  const thickness = (radius - hole) / rings;
  const pending = [];

  chart.series.slice(0, rings).forEach((series, ring) => {
    const total = series.values.reduce((sum, value) => sum + Math.abs(value ?? 0), 0);
    const inner = hole + thickness * ring;
    const outer = inner + thickness;
    let angle = -Math.PI / 2;
    series.values.forEach((value, point) => {
      if (value === null || value === undefined || total === 0) {
        return;
      }
      const sweep = Math.PI * 2 * Math.abs(value) / total;
      if (sweep > 0) {
        out.push(traced({ kind: 'path', part: 'slice', d: slicePath(cx, cy, inner, outer, angle, angle + sweep),
          fill: colorAt(chart, point), stroke: chart.edgeColor, width: RULE_WIDTH }, series, ring, point));
      }
      const text = texts[ring][point];
      if (text !== null) {
        const middle = angle + sweep / 2;
        let at;
        let anchor = 'middle';
        if (doughnut || labels.position === 'center') {
          at = arcPoint(cx, cy, doughnut ? (inner + outer) / 2 : outer / 2, middle);
        } else if (labels.position === 'inside') {
          at = arcPoint(cx, cy, outer * 0.72, middle);
        } else {
          at = arcPoint(cx, cy, outer + size * 0.5, middle);
          const cos = Math.cos(middle);
          anchor = Math.abs(cos) < 0.2 ? 'middle' : cos > 0 ? 'start' : 'end';
          at[1] += Math.sin(middle) * lineHeight / 2;
        }
        pending.push(traced({ kind: 'text', part: 'label', x: at[0], y: at[1], text, anchor,
          color: labelColor(series.labels, value), bold: series.labels.bold }, series, ring, point));
      }
      angle += sweep;
    });
  });
  out.push(...pending);
}

// Every primitive of a chart drawn in a frame `width` × `height` points, text measured by `measure(text, size, bold)`.
function layoutChart(chart, width, height, measure) {
  const size = chart.text.size;
  const out = [];
  const context = { size, out, measure: (text, bold = false) => measure(text, size, bold) };
  const inset = size * 0.5;
  let box = { x: inset, y: inset, width: Math.max(1, width - inset * 2), height: Math.max(1, height - inset * 2) };
  if (chart.title) {
    const titleSize = size * 1.2;
    out.push({ kind: 'text', part: 'title', x: width / 2, y: box.y + titleSize * 0.6, text: chart.title,
      anchor: 'middle', size: titleSize, bold: true });
    const taken = titleSize * 1.2 + size * 0.6;
    box = { ...box, y: box.y + taken, height: box.height - taken };
  }
  if (chart.legend) {
    box = layoutLegend(chart, box, context);
  }
  box = { ...box, width: Math.max(1, box.width), height: Math.max(1, box.height) };
  if (chart.family === 'pie') {
    layoutPie(chart, insetBox(box, chart.plotInsets), context);
  } else {
    layoutCartesian(chart, box, context);
  }
  return out;
}

let measureContext = null;

function textMeasurer(font) {
  return (text, size, bold) => {
    measureContext = measureContext || document.createElement('canvas').getContext('2d');
    measureContext.font = `${bold ? '700 ' : ''}${size}px ${fontFamily(font)}`;
    return measureContext.measureText(text).width;
  };
}

// What hovering a mark shows of a value no label on the chart gives.
function hoverValue(chart, { series: index, point }) {
  const series = chart.series[index];
  const value = series.values[point];
  if (value === null || value === undefined) {
    return null;
  }
  // A percent-stacked axis formats shares, not the values.
  const format = series.labels?.format ?? (chart.grouping === 'percent_stacked' ? null : chart.valueAxis?.format);
  const category = chart.categories?.[point];
  return {
    value: format ? formatNumber(value, format) : Number(value).toLocaleString('en-US'),
    of: [series.name, category].filter((part) => part !== undefined && part !== null && part !== '').join(' · ')
  };
}

function drawChart(chart, width, height) {
  const svg = svgElement('svg', { width, height, viewBox: `0 0 ${width} ${height}`, role: 'img' });
  const text = chart.text;
  const primitives = layoutChart(chart, width, height, textMeasurer(text.font));
  const labelled = new Set(primitives.filter((primitive) => primitive.kind === 'text' && primitive.datum)
    .map(({ datum }) => `${datum.series}:${datum.point}`));
  for (const primitive of primitives) {
    let node;
    switch (primitive.kind) {
      case 'rect':
        node = svgElement('rect', {
          x: roundChart(primitive.x),
          y: roundChart(primitive.y),
          width: roundChart(primitive.width),
          height: roundChart(primitive.height),
          fill: primitive.fill || 'none'
        }, svg);
        break;
      case 'line':
        node = svgElement('line', {
          x1: roundChart(primitive.x1),
          y1: roundChart(primitive.y1),
          x2: roundChart(primitive.x2),
          y2: roundChart(primitive.y2),
          stroke: primitive.stroke || 'none',
          'stroke-width': primitive.width
        }, svg);
        break;
      case 'path':
        node = svgElement('path', {
          d: primitive.d,
          fill: primitive.fill || 'none',
          'fill-rule': 'evenodd',
          stroke: primitive.stroke || 'none',
          'stroke-width': primitive.width,
          'stroke-linejoin': primitive.join || 'round',
          'stroke-linecap': 'round'
        }, svg);
        break;
      case 'text':
        node = svgElement('text', {
          x: roundChart(primitive.x),
          y: roundChart(primitive.y),
          'text-anchor': primitive.anchor,
          'dominant-baseline': 'central',
          'font-family': fontFamily(text.font),
          'font-size': primitive.size || text.size,
          'font-weight': primitive.bold ? 700 : null,
          fill: primitive.color || text.color
        }, svg);
        node.textContent = primitive.text;
        break;
      case 'hit':
        node = svgElement('circle', { cx: roundChart(primitive.x), cy: roundChart(primitive.y), r: primitive.r,
          fill: 'transparent' }, svg);
        break;
    }
    node.dataset.part = primitive.part;
    if (primitive.clipped) {
      node.dataset.clipped = '';
    }
    if (primitive.moved) {
      node.dataset.moved = primitive.moved;
    }
    if (primitive.series !== undefined) {
      node.dataset.partSeries = primitive.series;
    }
    if (primitive.point !== undefined) {
      node.dataset.partPoint = primitive.point;
    }
    if (primitive.datum) {
      node.dataset.series = primitive.datum.series;
      node.dataset.point = primitive.datum.point;
      const hover = primitive.kind !== 'text' && !labelled.has(`${primitive.datum.series}:${primitive.datum.point}`) &&
        hoverValue(chart, primitive.datum);
      if (hover) {
        node.dataset.hover = JSON.stringify(hover);
      }
    }
    if (primitive.trace) {
      node.dataset.trace = JSON.stringify(primitive.trace);
    }
  }
  return svg;
}

function renderChart(item, parent) {
  const node = element('div', 'item chart', parent);
  place(node, item.frame);
  const [, , width, height] = item.frame;
  node.draw = () => {
    node.replaceChildren(drawChart(item.chart, width, height));
  };
  node.draw();
  // Text is measured to place axes and labels, so the chart is laid out again once its font has loaded.
  const { font, size } = item.chart.text;
  node.loaded = document.fonts.load(`${size}px ${fontFamily(font)}`).then(() => node.draw()).catch(() => {});
  return node;
}

if (typeof module !== 'undefined') {
  module.exports = { formatNumber, axisTicks, stackSeries, layoutChart, wrap, spellDate, dateAxis, dateLabels };
}