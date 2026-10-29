# themes

A theme is a folder: `theme.yaml`, `styles.yaml` (see `styles`), `layouts/` (see `layouts`), and optionally `media/` and `fonts/`.
It is what a `.potx` is to PowerPoint: the colors, fonts, styles, and layouts every slide draws on.
The theme draws nothing itself: everything drawn on a slide but its own elements, such as a logo, a rule, or the slide number, is in a layout.
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
```

## Fonts

A `.ttf` or `.otf` in `fonts/` draws in the preview, screenshots, checks, PDF, and HTML export whether or not the machine has it installed.
Each file answers to its family at its weight, and to the family Windows and PowerPoint list it under, such as `IBM Plex Sans Medm` for IBM Plex Sans Medium.
A font the theme doesn't ship draws only where it is installed, so the HTML export lists it, leaving out those every Windows, Mac, and iPhone has, such as Arial and Georgia.
A `.pptx` doesn't embed them, so PowerPoint still needs the font installed.

## Usual mistakes

- Leaving out a theme color: all twelve are required.
- Unquoted hex values: `#` starts a YAML comment, so write `"#1F4E79"`.
- Writing a font family in a style instead of `heading` or `body`: it works, but the export can't retheme it.