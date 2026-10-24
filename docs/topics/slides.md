# slides

A slide is a file in `slides/` that picks a layout, writes its title and placeholders, and places elements.

## Keys

| Key | Value |
| --- | --- |
| `layout` | A layout's file stem. |
| `title` | The title's text, or a map overriding the layout: `{ text: …, frame: [x, y, w, h] }`. |
| `placeholders` | Each placeholder's text, or a map, by the names its layout defines in the theme's `layouts/` files (see `layouts`). |
| `elements` | Everything else on the slide, back to front, in front of the title and placeholders unless it names them. |
| `hide` | Ids of the layout's elements this slide leaves out, such as `[page]` for the slide number. |

The title and placeholders take one paragraph of plain text, in the layout's style: fields such as `${date}` work, markup doesn't, and a `$` not followed by `{` is literal, as everywhere.
A newline in it breaks the line, as `title: "Borrowing to\nBuild the AI Cloud"` or a block (`|-`) of two lines.

## Elements

The key naming the kind carries its content or its reference.

| Key | Kind | Value |
| --- | --- | --- |
| `text` | text box | Content in the markup (see `text`). |
| `shape` | shape | A shape's name; may also carry `text` (see `shapes`). |
| `line` | line | `{ from: [x, y], to: [x, y] }` (see `shapes`). |
| `picture` | picture | A media path (see `shapes`). |
| `chart` | chart | A file stem in `charts/` (see `charts`). |
| `table` | table | A file stem in `tables/` (see `tables`). |

Every element has an `id`, unique within its slide, which `--check` and `--element` use to name it.
Every element but a line has a `frame`: `[x, y, w, h]` in points from the top-left corner, or an area's name.

The title and placeholders draw behind the slide's elements.
To draw one in front of some of them, name it in `elements` where it belongs, as `- title` or `- number`: elements before the entry sit behind it, and those after it in front.

```yaml
title: The compute hall
elements:
  - id: hall
    picture: ../media/hall.png
    frame: [0, 0, 960, 540]
    fit: cover
    decorative: true
  - title
```

Shared keys: `id`, `frame`, `rotate` (degrees clockwise), `flip` (`horizontal`, `vertical`), `style`, `fill`, `stroke`, `shadow`, `opacity`, `decorative`.
Lines, charts, and tables take no `shadow` (see `shapes`).
`decorative: true` marks decoration rather than content, such as a watermark or a band bleeding off the slide, and the geometry checks leave it out; charts and tables can't be decorative.
Shape keys: `direction`, `adjust`, and `tail` (see `shapes`).
Line keys: `start` and `end` (see `shapes`).
Text keys: `insets` (`[left, top, right, bottom]`), `fit` (`none`, `grow`, `shrink`), `wrap`, `columns`, and any text property as a one-off, such as `align` or `color` (see `styles`).

Alignment, insets, and fit default to the theme's `text` style, and `shape_text` for text in a shape.
The title and placeholders take text keys too, `fit` included, from the layout or as a slide's override.
`fit: none` is the default: text may fill the insets, as in PowerPoint, and text running past the frame is an error in `--check`.
`grow` makes the box as tall as its text, a shape's outline included, and `shrink` scales the text down until it fits.
`wrap: false` breaks lines only where the text does, running past the frame if a line is longer; text wraps unless something says otherwise.

## Example

```yaml
layout: content
title: Connectivity revenue nearly tripled in two years

elements:
  - id: chart
    chart: revenue-by-segment
    frame: left
  - id: takeaway
    shape: rounded_rectangle
    adjust: { corner: 8 }
    frame: [490, 100, 430, 170]
    fill: accent1 lighter 80
    stroke: none
    align: left
    valign: top
    text: |
      {.lead}
      2025 revenue by segment
      - Connectivity: [$11.4B]{.emphasis}
      - Space: $4.1B
      - AI: $3.2B
  - id: source
    text: "Source: Acme S-1/A"
    style: source
    frame: [40, 484, 430, 14]
```

## Usual mistakes

- Pixels, inches, or percentages: every length is in points.
- `x`, `y`, `width`, `height`, or `position`: write `frame: [x, y, w, h]`.
- A frame for a line: lines have `from` and `to` instead.
- A layout of its own to put a slide's picture behind its title: name the title in the slide's `elements`, after the picture.
- Text YAML reads as a number or a boolean, such as `2025`, `1.5`, `yes`, or `no`: quote it, `text: "2025"`.
- Markup in a title: bold, spans, and links aren't allowed there; the layout's style decides how a title looks.
- Text containing `: ` or starting with `[`, `{`, `*`, or `#`: quote it or use a block scalar (`|`), since YAML reads those characters itself.