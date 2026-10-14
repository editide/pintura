# shapes

Shapes, lines, and pictures are elements (see `slides`).

## Shapes

`rectangle`, `rounded_rectangle`, `ellipse`, `triangle`, `right_triangle`, `diamond`, `parallelogram`, `trapezoid`, `pentagon`, `hexagon`, `chevron`, `home_plate`, `arrow`, `brace`, `bracket`, `callout`.

| Key | Value |
| --- | --- |
| `shape` | A name from the list. |
| `frame` | `[x, y, w, h]` or an area's name. |
| `fill` | A color or `none` (see `colors`); the shape's style, then the theme's `shape` base, sets the default. |
| `stroke` | `{ color, width, dash }`, or `none`; `dash` is `solid`, `dash`, or `dot`; the shape's style, then the theme's `shape` base, sets the default. |
| `direction` | `right`, `left`, `up`, or `down`, for `triangle`, `right_triangle`, `chevron`, `home_plate`, `arrow`, `brace`, and `bracket`. |
| `adjust` | Adjustments, in points: `corner` for `rounded_rectangle`, `point` for `chevron`, `home_plate`, and `arrow`, `slant` for `parallelogram` and `trapezoid`. |
| `tail` | For `callout`, the point `[x, y]` on the slide its tail reaches; the `frame` is the balloon, and the tail leaves from its nearest edge. |
| `text` | Text inside the shape, in the markup, aligned as the theme's `shape_text` says. |

Shapes also take every text key (see `slides`), such as `color` for the text inside.

A `brace` or `bracket` points in its `direction`: `direction: right` opens to the left and points right.

## Lines

| Key | Value |
| --- | --- |
| `line` | `{ from: [x, y], to: [x, y] }` |
| `stroke` | `{ color, width, dash }`; the theme's `line` base sets the default. |
| `start`, `end` | Arrowheads: `arrow`, `triangle`, `stealth`, `diamond`, `oval`. |

## Pictures

| Key | Value |
| --- | --- |
| `picture` | A path to a PNG, JPEG, GIF, WebP, or SVG, relative to the file naming it, inside the presentation, its theme, the folder holding the presentation, or the git repository holding it. |
| `fit` | `contain`, `cover`, or `stretch`; the theme's `picture` base sets the default. |
| `crop` | `[left, top, right, bottom]` in the image's pixels, trimmed before fitting. |
| `alt` | Alternative text. |
| `fill` | An SVG's one color: every fill and stroke in it but `none` takes it, as for an icon. |
| `style` | A named style, whose `fill` and `opacity` the picture takes. |

An icon is an SVG from any set, such as Lucide, Tabler, or Material, saved in `media/` and given a `fill` or a style with one, so it follows the theme.
A `fill` on an SVG of several colors, such as a logo, paints them all in one, and `--check` warns.
The export draws an SVG as a PNG in its fill, so changing the theme in PowerPoint leaves it as it was.

## Example

```yaml
elements:
  - id: step
    shape: chevron
    direction: right
    adjust: { point: 16 }
    frame: [40, 200, 180, 60]
    fill: accent1
    stroke: none
    color: light1
    text: Launch
  - id: link
    line: { from: [230, 230], to: [300, 230] }
    stroke: { color: muted, width: 1.5 }
    end: triangle
  - id: note
    shape: callout
    frame: [320, 120, 200, 56]
    tail: [260, 210]
    fill: light1
    stroke: { color: accent3, width: 1.5 }
    text: Twelve launches in the quarter
  - id: photo
    picture: ../media/launch.jpg
    frame: [560, 100, 360, 380]
    fit: cover
    alt: A rocket on the pad
  - id: launches-icon
    picture: ../media/rocket.svg
    frame: [40, 300, 32, 32]
    style: icon               # styles.yaml: icon: { fill: accent1 }
```

## Usual mistakes

- A `frame` on a line: lines have `from` and `to`.
- `rotate: 90` on an arrow to point it down: use `direction: down`.
- Adjustments as fractions (`corner: 0.1`): they are points.
- Shapes outside the list (`star`, `cloud`, `heart`): draw with the sixteen, or use a picture.
- `fill` on a PNG or JPEG: only an SVG can be recolored.