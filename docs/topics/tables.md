# tables

A table is a file in `tables/`, placed on a slide with `table: <stem>`, and its data is a CSV beside it.

## Keys

| Key | Value |
| --- | --- |
| `data` | A CSV path, every row of which, header included, becomes a table row, or a Libreta view (see `pintura --docs books`). |
| `style` | A table style from the theme's `styles.yaml`. |
| `header_rows` | How many leading rows are headers. |
| `columns` | One entry per column, from the first: `width`, `format`, `style`, `border_left`, `border_right`, and any text property. |
| `rows` | By row number: `style`, `height`, `format`, `fill`, `border_top`, `border_bottom`, and any text property. |
| `cells` | A list of `{ row, column, … }` with `style`, `format`, `fill`, `border_top`, `border_bottom`, `border_left`, `border_right`, and any text property. |
| `merge` | A list of `{ row, column, rows, columns }`, merging from that cell; `rows` and `columns` default to 1. |

Rows and columns are numbered from 1, header included.
A row's `style` is a row style from the table style; a column's or cell's is a text style from `styles`.
Text properties are those of a style, with `insets`; `fit` and `columns` belong to boxes, not cells.
Formatting layers from columns through the row's style in the table style, rows, and cells, the last one winning.
A column formats its values, so the table style's `header` outranks it in header rows: a column styled red keeps a white header white.
Rows grow to fit their text; `height` sets a minimum.
The frame sets the table's position, width, and the height available: columns without a `width` share what the others leave, widths that add up to more than the frame are an error in `--check`, and so are rows that grow past its bottom.
The frame can be an area's name.

Cells take the text markup and book references (see `pintura --docs books`).
A cell that parses as a number is spelled by its `format`; without one it prints as the CSV writes it.
Header rows are never formatted, so a `2025` header stays `2025`.
An empty cell stays empty.
In a merge, the first cell's text and formatting show, and the rest are left empty in the CSV; text left in them is an error.
A merge's top border is its first row's and its bottom border its last row's, so a label spanning a section closes with the section's total; likewise its left border is its first column's and its right border its last column's.
Merges work in header rows too, and a column's `align` reaches every cell in it, headers included; a merged cell takes its first cell's `align`, so a cell entry can center a header spanning right-aligned columns.
Every CSV row has the same number of cells, and fields with commas are quoted, as in any CSV.

Rules across the table are a row's `border_top` and `border_bottom` (see Table styles), and rules down it a column's `border_left` and `border_right`, each `{ color, width, dash }`.
A cell's own borders merge key by key over its row's and its column's, and `none` removes one, so a rule can run beneath some cells of a row and not others, such as the years but not their label.
Neighbouring cells' rules meet, so to break an underline between two columns, put a narrow empty column between them.
Two cells beside each other share an edge, so draw a rule between them as one cell's border or the other's, not both.

```yaml
# tables/h1-results.yaml
columns:
  - { width: 180, border_right: { color: dark1 lighter 50, width: 0.75 } }
cells:
  - { row: 1, column: 1, border_right: none }
  - { row: 1, column: 2, border_bottom: { color: dark1, width: 0.75 } }
  - { row: 1, column: 3, border_bottom: { color: dark1, width: 0.75 } }
```

## Table styles

A table style names row styles, each a set of text properties plus `fill`, `border_top`, and `border_bottom` (`{ color, width, dash }`).
`header` applies to header rows that name no other row style, `banded` is a color for the second, fourth, and every other even body row without a row style, and any other row style is applied with `rows: { 6: { style: total } }`.
`indent` is how far in, in points, a row from a Libreta view sets its label per step the view indents it.
A row's own `border_top` or `border_bottom` merges key by key over its row style's, and `none` removes it.

```yaml
# styles.yaml
tables:
  plain:
    header: { bold: true, fill: light2 }
    subtotal: { bold: true, border_top: { color: dark1 lighter 50, width: 0.75 } }
    total: { bold: true, border_top: { color: dark1, width: 1 }, border_bottom: { color: dark1, width: 2 } }
    banded: light2 lighter 50
    indent: 12
```

## Example

```yaml
# slides/h1-results.yaml
elements:
  - id: results
    table: h1-results
    frame: content
```

```yaml
# tables/h1-results.yaml
data: h1-results.csv
style: plain
header_rows: 1
columns:
  - { width: 180 }
  - { width: 220 }
  - { width: 160, align: right, format: millions_of_dollars }
  - { width: 160, align: right, format: millions_of_dollars }
  - { width: 160, align: right, format: percent }
rows:
  5: { style: total }
  9: { style: total }
merge:
  - { row: 2, column: 1, rows: 4 }
  - { row: 6, column: 1, rows: 4 }
```

```csv
measure,segment,H1 2025,H1 2026,Change
Revenue,Space,1611,1581,-0.019
,Connectivity,5062,7548,0.491
,AI,1465,3379,1.307
,Total,8138,12508,0.537
Operating income,Space,-439,-1204,
,Connectivity,1956,2844,0.454
,AI,-2460,-3726,
,Total,-943,-2086,
```

## Usual mistakes

- Styling each negative cell: give the column a format with `negative_style`.
- Numbering rows from the first body row: row 1 is the header.
- Typing `(439)` or `12,508` in the CSV: write `-439` and `12508`, and let a format spell them.
- Repeating a merged label on each row it spans: write it once, in the merge's first cell.
- Giving every column a `width` that sums past the frame: leave the widest column without one to take what remains.