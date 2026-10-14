# charts

A chart is a file in `charts/`, placed on a slide with `chart: <stem>`, and its data is a CSV beside it.

## Keys

| Key | Value |
| --- | --- |
| `type` | A chart type from the list below. |
| `style` | A named chart style from the theme's `styles.yaml`; without one, the chart takes only the `chart` base. |
| `data` | A CSV path, or a book's lines (see `pintura --docs books`). |
| `series_in` | `rows` (the default) or `columns`. |
| `title` | Chart title text, bold at 1.2 times the chart's text size; none if left out. |
| `legend` | `bottom`, `top`, `left`, `right`, or `none`. |
| `data_labels` | `{ show, format, position, color, bold }`; `show` is `value`, `percent` (pie and doughnut), or `none`, and labels are off until something sets it; `color` defaults to the text's, and a format's `negative_style` wins over it. |
| `value_axis` | `{ visible, min, max, step, format, gridlines, reverse }`; `format` names a format from `styles.yaml`; `min`, `max`, and `step`, the distance between ticks, default to the automatic scale below; `reverse: true` runs the values top down, or right to left on a bar chart, as for a league position with 1st at the top. |
| `secondary_axis` | `{ visible, min, max, step, format, reverse }`, the axis on the right for lines with `axis: secondary`; it takes no gridlines. |
| `series` | Keys for single series, by their names in the CSV (see Series); only a chart file takes it. |
| `category_axis` | `{ visible, labels, format, step }`; `labels: false` keeps the axis's line but drops its labels, as for logos placed beneath the categories; `format` names a date format, which makes the axis a time line, and `step` is the time between its labels, such as `7 days`, `1 month`, or `2 years`. |
| `series_colors` | Colors in series order. |
| `gap_width`, `overlap` | Bar spacing, in percent as in PowerPoint. |
| `markers` | `{ shape, size, fill }` on each point of the chart's lines; `shape` is `circle`, `square`, `diamond`, `triangle`, or `none`, the default; `size` is whole points across, from 2 to 72, 5 by default; `fill` defaults to the line's color. |
| `text` | `{ size, color, font }` for every label on the chart; what it leaves out comes from the base `text`. |

`position` is `center`, `inside`, or `inside_base` on any column or bar chart, `outside` on clustered ones and pies (past the bar's end, which for a negative value is below or to the left), and `above`, `below`, or `center` on lines.
A doughnut's labels sit on its ring and take no position.

What nothing sets is PowerPoint's default, which `--element` shows as such: `gap_width: 150`, `overlap: 0`, both axes visible, series colors `accent1` to `accent6`, and labels `outside` on clustered charts, `center` on stacked ones, `above` on lines, and `inside` on pies.
Stacked charts always overlap fully.
Gridlines, the legend, the title, labels, and markers stay off until something sets them.
A marker is outlined in its line's color and width, so `fill: light1` draws it hollow.
A series drawn in `none`, or a pie's slice, has no legend entry, as a series carrying only labels, such as a total, needs none.
A number with no format shows as written, and percents (a percent stacked axis, `show: percent`) as whole percents.
An axis's `min`, `max`, or `step` nobody fixes comes from its data: the axis reaches zero and every stack's end, less totals, 5% past each end, its step is 1, 2, or 5 times a power of ten giving five to ten steps, and its ends round out to whole steps, so a stack of 830, 24.5, and 66 runs from 0 to 1000 by 100.
A percent stack runs to 100% exactly.
`pintura --element` shows the scale an axis gets, and the export fixes it in PowerPoint, so the deck draws the same axis.
Category labels sit at the plot's edge, clear of negative bars.

Keys layer from the theme's `chart` base through the named style to the chart, the last one winning; the base and styles take every key but `type`, `data`, and `series_in`.
Maps merge key by key, so a chart writing `data_labels: { format: billions_of_dollars }` keeps its style's `show: value`.

## Series

A series takes five keys of its own under `series`, by its name in the CSV:

| Key | Value |
| --- | --- |
| `type` | `line`, a line over a column chart's columns, or `total`, a stack's total on a stacked column or bar chart. |
| `axis` | `secondary`, for a line drawn against `secondary_axis`; one series at least stays on the value axis. |
| `data_labels` | The chart's `data_labels` keys, layered over the chart's key by key. |
| `markers` | The chart's `markers` keys, for a line, layered over the chart's key by key. |
| `points` | By category, `{ color }` for a single column or bar in a color of its own, such as a year to pick out; on a line, `{ color, markers }` styles that point's marker, `color` outlining it and filling it unless `markers` names a `fill`, and `markers` taking the `markers` keys over the series' key by key; on any series, `data_labels` takes the `data_labels` keys over the series' key by key, for one label to stand out, show alone, or show none. A total's points take only `data_labels`, without a position. |

A line over columns takes the chart's labels but not their position, which is `above` unless it sets one.
A total draws no bar and has no legend entry: it shows its value past the end of its stack, which the axis scales to, in the chart's label format and boldness but the text's color, since the chart's label color is usually for inside the bars.
The total is a row in the CSV like any other, and `--check` warns when it isn't what its stack adds up to, to within the last digit its labels show.
A series keeps its slot in `series_colors`, a total's going unused.
A point's color leaves its legend entry in the series color.
A line's point with `markers: { shape: none }` drops its marker, and one with a shape draws it on a line that has none.
A point's labels layer over its series' as written, so a point with `show: value` on a series with `show: none` takes the position, format, and color the series would.

```yaml
# charts/revenue-by-segment.yaml, over a CSV whose last row is Total
type: stacked_column
data: segment-revenue.csv
data_labels: { show: value, format: billions_of_dollars, color: light1 }
series:
  Total: { type: total, data_labels: { bold: true } }
```

```yaml
# charts/points.yaml, with the title seasons picked out
type: clustered_column
data: points.csv
series:
  Points:
    points:
      "2011-12": { color: accent3 }
      "2013-14": { color: accent3 }
```

```yaml
# charts/revenue.yaml, labelling only the latest year, in bold
type: line
data: revenue.csv
data_labels: { show: none, format: billions_of_dollars }
series:
  Revenue:
    points:
      "2025": { data_labels: { show: value, bold: true } }
```

```yaml
# charts/league.yaml, with 1st at the top and the title seasons' markers picked out
type: clustered_column
data: league.csv
secondary_axis: { min: 1, max: 4, step: 1, reverse: true }
series:
  Position:
    type: line
    axis: secondary
    markers: { shape: circle, size: 6 }
    points:
      "2011-12": { color: accent3, markers: { shape: diamond, size: 9, fill: light1 } }
```

```yaml
# charts/revenue-and-margin.yaml, over rows Revenue and Adjusted EBITDA margin
type: clustered_column
data: revenue-margin.csv
value_axis: { format: billions_of_dollars }
secondary_axis: { min: 0, max: 0.5, format: percent }
series:
  Adjusted EBITDA margin:
    type: line
    axis: secondary
    data_labels: { show: value, format: percent }
    markers: { shape: circle, size: 7, fill: light1 }
```

Types: `clustered_column`, `stacked_column`, `percent_stacked_column`, `clustered_bar`, `stacked_bar`, `percent_stacked_bar`, `line`, `stacked_line`, `percent_stacked_line`, `pie`, `doughnut`.

## Data

One series per row and one category per column; the first column names the series, and the header's first cell is ignored.
Fields with commas are quoted, as in any CSV.
`series_in: columns` reads it the other way: the header's cells after the first name the series, and the first column names the categories.
Bar charts list categories top to bottom in the CSV's order.
An empty cell is a gap: no bar, and a break in a line.
A pie draws one series, so its CSV has one row, or one value column with `series_in: columns`; a doughnut draws a ring per series, the first innermost.

```csv
segment,2023,2024,2025
Space,3557,3796,4086
Connectivity,3869,7599,11387
AI,2961,2620,3201
```

Values are plain numbers: no thousands separators, currency symbols, or percent signs.
A format spells them on the chart.

A date axis reads its categories as dates written `YYYY-MM-DD`, and places each in its own slot on a time line, as Excel's date axis does: a slot per day, or per month or year when every date is the first of one, so a weekend in a daily series is a gap.
Without a `step`, labels come every day, two days, week, two weeks, month, quarter, half year, or year, the finest that keeps them apart, from the first date on.

```yaml
# charts/index-performance.yaml, over a daily CSV whose header runs 2026-01-02,2026-01-05,…
type: line
data: index-performance.csv
category_axis: { format: trading_day, step: 7 days }
```

## Example

```yaml
# charts/revenue-by-segment.yaml
type: stacked_column
data: segment-revenue.csv
data_labels: { show: value, format: billions_of_dollars, position: center }
```

A named style carries what several charts share, and a chart then writes only what differs:

```yaml
# styles.yaml
chart:
  series_colors: [accent1, accent2, accent3, accent4, accent5, accent6]
  text: { size: 11, color: dark1 }
  gap_width: 80
  legend: bottom

charts:
  minimal:
    series_colors: [accent4 lighter 40, accent1, accent3]
    text: { size: 10, color: muted }
    value_axis: { visible: false }
    legend: bottom
    data_labels: { show: value, color: light1 }
```

```yaml
# charts/revenue-by-segment.yaml, on a theme with the minimal style
type: stacked_column
style: minimal
data: segment-revenue.csv
data_labels: { format: billions_of_dollars }
```

## Usual mistakes

- Computing a total or a growth rate in the chart: add it to the CSV.
- A total as a series in `none` with its labels moved to `inside_base` and the axis's `max` fixed to hide it, as PowerPoint needs: give it `type: total`.
- A line on a bar chart, or a column series on `axis: secondary`: a line runs along the categories, so only lines over columns, and only lines take the secondary axis.
- `"$11.4B"` in the CSV: write `11387` and let a format spell it.
- `Jan-2-26` or `1/2/2026` as a category: write `2026-01-02`, and let a date format on `category_axis` spell it.
- A type outside the list (`waterfall`, `scatter`, `area`): not supported.