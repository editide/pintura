# styles

`styles.yaml` in the theme holds every style; Pintura has no built-in style names.
Slides name what a thing is (`style: caption`, `[+12%]{.positive}`), and this file decides how it looks.

## Keys

| Key | Value |
| --- | --- |
| `text` | The base every text box starts from: text properties, `insets`, and bullet `levels`. |
| `shape_text` | Text properties layered over `text` for text inside a shape, usually `align` and `valign`. |
| `shape` | The base every shape starts from, usually `fill` and `stroke`, and any `shadow`. |
| `picture` | The base every picture starts from, usually `fit`. |
| `line` | The base every line starts from, usually `stroke`. |
| `chart` | The base every chart starts from (see `charts`). |
| `styles` | Named text styles. |
| `tables` | Named table styles (see `tables`). |
| `charts` | Named chart styles, layered over `chart` (see `charts`). |
| `formats` | Named number formats. |

Text properties: `font` (`heading`, `body`, or a family), `size`, `color`, `bold`, `italic`, `underline`, `align` (`left`, `center`, `right`, `justify`), `valign` (`top`, `middle`, `bottom`), `space_before`, `space_after`, `line_spacing` (a multiple, such as `1.2`).

A level takes its bullet keys and any text property.
Level 1 is `- item`; each two spaces of indentation in the markup is one level deeper.

| Bullet key | Value |
| --- | --- |
| `bullet` | A character, or `none`. |
| `bullet_color` | The bullet's color; without it, the bullet takes the text's. |
| `bullet_size` | A multiple of the text size, such as `1.5`. |
| `indent` | Where the text starts, from the box's left inset. |
| `hanging` | How far left of the text the bullet sits; without it, the gap to the level above's `indent`. |

A named style can change bullets too, with its own `levels` taking only the bullet keys, for levels the theme's `text` has.
A box or paragraph in that style takes them over the theme's level, key by key, and a paragraph's style wins over its box's: a pros-and-cons grid is two styles, not two themes.

A named style can also set `insets` and the box itself: `fill`, `stroke`, `shadow`, and `opacity`, as a slide writes them (see `shapes`).
A text box or shape in that style takes them over the theme's `shape` base, and its own keys win over the style's, key by key for `stroke` and `shadow`.
A picture in that style takes its `stroke`, `shadow`, and `opacity`, and an SVG its `fill` too, so every icon or screenshot is one style.
The same band on several slides, such as a dark strip behind a photo credit, is one style rather than the same keys on each slide.

A style used on a box applies all its properties; on a paragraph, all but `valign`, `insets`, `fill`, `stroke`, `shadow`, and `opacity`; on a run (`[text]{.name}`), only `font`, `size`, `color`, `bold`, `italic`, and `underline`.

## Formats

Formats spell numbers in tables and charts, and later in Libreta references; a number typed into text is spelled as typed.
Name a format for what a number is (`millions_of_dollars`), not how it looks (`one_decimal`).

| Key | Value |
| --- | --- |
| `scale` | Multiplies the value before display: `0.001` shows millions as billions, `100` shows a ratio as a percent. |
| `decimals` | Digits after the decimal point, from 0 to 15. |
| `separator` | `true` for thousands separators. |
| `prefix`, `suffix` | Text around the number, such as `$` or `%`. |
| `negative` | `parentheses` for `(125)` or `minus` for `-125`, around the prefix and suffix: `($19.6B)`, `-$19.6B`. |
| `negative_style` | A style every negative number in this format takes. |

What a format leaves out is Libreta's default: `scale: 1`, `decimals: 0`, `separator: true`, no prefix or suffix, and `negative: parentheses`.

A format with `date` spells dates instead, and takes no other key.
Its pattern writes the year as `YYYY` or `YY`, the month as `MMMM` (`January`), `MMM` (`Jan`), `MM` (`01`), or `M` (`1`), and the day as `DD` or `D`; every other character is itself, so `MMM-D-'YY` spells `Jan-2-'26`.
Dates are written `YYYY-MM-DD` in a CSV, and a date format spells them on a chart's category axis or in a table cell; a cell that isn't a date is left as written.

## Example

```yaml
text:
  font: body
  size: 16
  color: dark1
  align: left
  valign: top
  insets: [7.2, 3.6, 7.2, 3.6]      # left, top, right, bottom
  levels:
    1: { bullet: "•", indent: 18, space_before: 6 }
    2: { bullet: "–", size: 14, indent: 36, space_before: 3 }
    3: { bullet: "•", size: 12, indent: 54 }

shape_text: { align: center, valign: middle }

picture: { fit: contain }

shape: { fill: light2, stroke: { color: dark1 lighter 50, width: 1 } }

line: { stroke: { color: dark1, width: 1 } }

styles:
  title: { font: heading, size: 28 }
  subtitle: { size: 18, color: muted }
  lead: { size: 18, bold: true, space_after: 6 }
  caption: { size: 11, color: muted }
  source: { size: 9, italic: true, color: muted }
  emphasis: { bold: true, color: accent1 }
  pros: { levels: { 1: { bullet: "+", bullet_color: positive } } }
  cons: { levels: { 1: { bullet: "–", bullet_color: negative } } }
  positive: { color: positive }
  negative: { color: negative }

formats:
  millions_of_dollars: { decimals: 0, separator: true, negative: parentheses, negative_style: negative }
  billions_of_dollars: { scale: 0.001, decimals: 1, prefix: "$", suffix: B, negative: parentheses }
  percent: { scale: 100, decimals: 0, suffix: "%", negative: minus, negative_style: negative }
  trading_day: { date: "MMM-D-'YY" }
```

## Usual mistakes

- Repeating the same one-off formatting on several slides: name a style instead; `--check` warns about it.
- Expecting a paragraph property (`align`, `space_before`) from a style used on a run: runs take character properties only.
- Unquoted bullets such as `-` or `*`: quote them, `bullet: "–"`.
- A text property such as `size` in a named style's `levels`: those take only the bullet keys; set text properties on the style itself.