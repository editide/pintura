# books

A presentation can quote a Libreta book, so its figures come from the book rather than being typed: text reads one cell, a chart reads lines, and a table presents a view.
Reading a book needs the `libreta` CLI on the path, or `PINTURA_LIBRETA` pointing at it.

## Including a book

```yaml
# presentation.yaml
books:
  - id: acme
    path: ../../../libreta/books/acme
```

| Key | Value |
| --- | --- |
| `id` | The name references use, letters, digits, `_`, and `-`, starting with a letter. |
| `path` | The book's folder, the one holding `book.yaml`, relative to `presentation.yaml`. |

## Text

`${acme::historical.segments.connectivity_revenue["2025"]}` shows one cell: the book's id, `::`, the line's qualified name, and the column in brackets and quotes.
It shows as Libreta displays it, and `| format` after the column spells the exact value in a format from `styles.yaml` instead:

```yaml
title: Connectivity revenue reached ${acme::historical.segments.connectivity_revenue["2025"] | billions_of_dollars}
```

Qualified names and columns are as `libreta <book> --lines` lists them; `--lines` with names prints just those.
A reference never takes arithmetic: a figure the book doesn't have becomes a formula in the book.
Like a field, a reference is read-only when rewording text in the page.
References work wherever text does, a table's CSV cells included, where the field is quoted and its quotes doubled, as in any CSV: `"Revenue ${acme::historical.revenue[""2025""]}"`.

## Charts

`data` names a book and a series per line instead of a CSV:

```yaml
# charts/revenue-by-segment.yaml
type: stacked_column
data:
  book: acme
  columns: ["2023", "2024", "2025"]
  series:
    - { line: historical.segments.space_revenue, label: Space }
    - { line: historical.segments.connectivity_revenue, label: Connectivity }
data_labels: { format: billions_of_dollars }
```

| Key | Value |
| --- | --- |
| `book` | The book's id. |
| `columns` | The categories, as the book's columns; every column the first line has if left out. |
| `series` | `{ line, label }` per series; `label` defaults to the line's label in the book, and names the series under the chart's `series` key, or the category with `series_in: columns`. |

Values are the book's exact values in natural direction, and a cell with no value is a gap.
`series_in: columns` turns it, as for a CSV: each column is a series and each line a category, so a pie of segments in one year lists the segments' lines under `series` and the year under `columns`.

## Tables

`data` names a view instead of a CSV:

```yaml
# tables/revenue.yaml
data:
  view: acme::historical/segments/revenue
  lines:
    - space_revenue
    - connectivity_revenue
    - { line: total_revenue, label: Total }
  columns: ["2025", H1 2026]
style: financial
columns:
  - { width: 400 }
  - { align: right, format: millions_of_dollars }
  - { align: right, format: millions_of_dollars }
```

| Key | Value |
| --- | --- |
| `view` | The book's id, `::`, and a view id as `libreta <book> --list` lists it. |
| `label` | The header above the rows' labels; empty if left out. |
| `lines` | The view's lines to show, in this order, by qualified name or the last part of it, each with an optional `label`; every row but spacers if left out. |
| `columns` | The view's columns to show, by name or label, in this order, each with an optional `label` as `{ column, label }`; all of them if left out. |

The first row is the columns' labels and the first column the rows' labels, and `header_rows` defaults to 1.
A line the view shows twice, such as one a bridge adds back, has the same values both times, so naming it picks either.
A cell the view shows negated, a `sign: contra` line, keeps its sign under a `format`.
A row's role in the view, such as `header`, `subtotal`, or `total`, names the table style's row style of that name when it has one, unless `rows` gives the row a style of its own.
A row the view indents has its label indented by the table style's `indent` per step, and flush when the table style has none.
Cells show Libreta's displayed values, and a column, row, or cell `format` spells the exact value instead.
Everything else in the table file, `columns`, `rows`, `cells`, and `merge`, counts rows and columns as the table shows them.

## In the page

Every slide showing a number from a book redraws when a file in the book changes, with no edit to the presentation.
Comparing against a revision reads the book as that commit left it, which needs the book in the presentation's repository.
Holding Ctrl (Cmd on a Mac) underlines the linked numbers, and Ctrl-clicking one, in text, a table, or a chart's bar, slice, point, or label, opens the book in Libreta at that cell; the inspector has a button for each number in text or a table.
In a page `--export-html` writes, a plain click opens the book, which the page carries, over the slides.

## Usual mistakes

- Writing a line's short name in text: references take the qualified name, `historical.segments.ai_revenue`, not `ai_revenue`.
- Writing the column without quotes: `["2025"]`, not `[2025]`.
- Doing arithmetic in a reference: add a formula to the book and reference it.
- Typing a figure the book has: it drifts from the book the first time a fact is restated.