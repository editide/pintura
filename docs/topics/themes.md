# themes

A theme is a folder: `theme.yaml`, `styles.yaml` (see `styles`), `layouts/` (see `layouts`), and optionally `media/` and `fonts/`.
It is what a `.potx` is to PowerPoint: the colors, fonts, and everything every slide shares.
Every default is in these files; Pintura has none of its own.

## Keys

| Key | Value |
| --- | --- |
| `name` | The theme's name. |
| `colors` | The twelve theme colors, each a hex value (see `colors`). |
| `named_colors` | Colors the theme names, such as `positive`, as a theme color with steps or a hex value. |
| `fonts` | `heading` and `body`, each a font family. |
| `background` | A color. |
| `title` | The title's `frame` and `style`, and any text key. |
| `placeholders` | Text placeholders beyond the title (see `layouts`). |
| `areas` | Named rectangles, `name: [x, y, w, h]`, that elements take as their `frame`. |
| `elements` | Elements drawn on every slide, behind the slide's own (see `slides` and `shapes`). |
| `checks` | Thresholds for `pintura --check`, such as `{ shrink: 70 }` (see `checks`). |

## Example

```yaml
name: Acme

colors:
  dark1: "#1B1B1B"
  light1: "#FFFFFF"
  dark2: "#1F3A5F"
  light2: "#E9EDF2"
  accent1: "#1F4E79"
  accent2: "#2E86AB"
  accent3: "#F2A541"
  accent4: "#6C757D"
  accent5: "#A23B72"
  accent6: "#3B8F5A"
  hyperlink: "#0563C1"
  followed_hyperlink: "#954F72"

named_colors:
  positive: accent6
  negative: "#C0392B"
  muted: dark1 lighter 50

fonts:
  heading: Georgia
  body: Arial

background: light1

title: { frame: [40, 36, 800, 44], style: title }

areas:
  content: [40, 100, 880, 380]
  left: [40, 100, 430, 380]
  right: [490, 100, 430, 380]

elements:
  - id: logo
    picture: media/logo.svg
    frame: [856, 44, 64, 24]
  - id: rule
    line: { from: [40, 88], to: [920, 88] }
    stroke: { color: accent1, width: 1.5 }
  - id: footer
    text: ${footer}
    style: caption
    frame: [40, 508, 600, 22]
  - id: page
    text: ${slide}
    style: caption
    align: right
    frame: [860, 508, 60, 22]
```

The slide number is an ordinary text box showing `${slide}`.
Theme elements can't be moved or edited from a slide; change them here, or drop them on a layout with `theme_elements: false`.

## Usual mistakes

- Leaving out a theme color: all twelve are required.
- Unquoted hex values: `#` starts a YAML comment, so write `"#1F4E79"`.
- Writing a font family in a style instead of `heading` or `body`: it works, but the export can't retheme it.