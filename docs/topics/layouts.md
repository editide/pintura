# layouts

A layout is a file in the theme's `layouts/`, a variation on the theme such as a cover or a section divider.
It inherits the theme's title, placeholders, areas, elements, and background, and overrides any of them by name.
Slides refer to it by file stem: `layout: cover` means `layouts/cover.yaml`.

## Keys

| Key | Value |
| --- | --- |
| `name` | What PowerPoint shows in its layout gallery. |
| `background` | A color, replacing the theme's. |
| `theme_elements` | `false` drops the theme's elements. |
| `title` | Overrides the theme's title: `frame`, `style`, and any text key; `none` removes it. |
| `placeholders` | Named text placeholders: `name: { frame, style, … }`. |
| `areas` | Areas added or replaced by name. |
| `elements` | Elements drawn on every slide using this layout, after the theme's. |

A placeholder is text whose frame and style the layout owns and whose words each slide writes, such as a subtitle or a kicker above the title.
Only text is a placeholder: a chart, table, or picture takes an area as its `frame` instead.

## Example

```yaml
# layouts/cover.yaml
name: Cover
background: dark2
theme_elements: false

title: { frame: [60, 190, 840, 90], style: display, color: light1 }

placeholders:
  subtitle: { frame: [60, 290, 840, 40], style: subtitle, color: light2 }

elements:
  - id: logo
    picture: ../media/logo-white.svg
    frame: [60, 60, 120, 40]
```

```yaml
# layouts/comparison.yaml
name: Comparison

placeholders:
  left_heading: { frame: [40, 100, 430, 32], style: column_heading }
  right_heading: { frame: [490, 100, 430, 32], style: column_heading }

areas:
  left: [40, 140, 430, 340]
  right: [490, 140, 430, 340]
```

The simplest layout is `name: Content`, which is the theme unchanged.

## Usual mistakes

- A chart or picture placeholder: add an area, and let the slide's chart take it as its `frame`.
- A dark background with the theme's dark text: state `color` on the title, the placeholders, and the elements.
- Paths in a layout are relative to `layouts/`: `../media/logo.svg`.