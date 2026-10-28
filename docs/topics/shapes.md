# shapes

Shapes, lines, arcs, and pictures are elements (see `slides`).

## Shapes

`rectangle`, `rounded_rectangle`, `ellipse`, `triangle`, `right_triangle`, `diamond`, `parallelogram`, `trapezoid`, `pentagon`, `hexagon`, `chevron`, `home_plate`, `arrow`, `brace`, `bracket`, `callout`.

| Key | Value |
| --- | --- |
| `shape` | A name from the list. |
| `frame` | `[x, y, w, h]` or an area's name. |
| `fill` | A color or `none` (see `colors`); the shape's style, then the theme's `shape` base, sets the default. |
| `stroke` | `{ color, width, dash }`, or `none`; `dash` is `solid`, `dash`, or `dot`; the shape's style, then the theme's `shape` base, sets the default. |
| `shadow` | A drop shadow, or `none` (see Shadows); the shape's style, then the theme's `shape` base, sets the default. |
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

## Arcs

An arc is a curved line around an ellipse, the one its `frame` holds, for a cycle of boxes or an arrow bending around something.

| Key | Value |
| --- | --- |
| `arc` | `{ from, to }`: where it starts and ends on the ellipse, in degrees clockwise from the right, from 0 up to 360; it runs clockwise from `from` to `to`. |
| `frame` | The whole ellipse's `[x, y, w, h]`, however little of it the arc draws. |
| `stroke` | As for a line; the theme's `line` base sets the default. |
| `start`, `end` | Arrowheads, as for a line; `end` points clockwise. |

Arcs around one cycle share a frame and differ only in their angles: 0 is the right, 90 the bottom, 180 the left, 270 the top.
To run counterclockwise, swap `from` and `to` and put the arrowhead on `start`.

```yaml
# Four boxes at the top, right, bottom and left of a circle, and an arc between each pair, clockwise.
elements:
  - id: plan_to_build
    arc: { from: 290, to: 340 }
    frame: [330, 120, 300, 300]
    end: triangle
  - id: build_to_ship
    arc: { from: 20, to: 70 }
    frame: [330, 120, 300, 300]
    end: triangle
```

The export draws PowerPoint's own arc, whose yellow handles move its ends.
`--check` places an arc by the curve it draws, so the boxes it circles don't overlap its frame.

## Pictures

| Key | Value |
| --- | --- |
| `picture` | A path to a PNG, JPEG, GIF, WebP, or SVG, relative to the file naming it, inside the presentation, its theme, the folder holding the presentation, or the git repository holding it. |
| `fit` | `contain`, `cover`, or `stretch`; the theme's `picture` base sets the default. |
| `crop` | `[left, top, right, bottom]`, the pixels trimmed from each of the image's edges before fitting, not the corners of the part kept. |
| `alt` | Alternative text. |
| `fill` | An SVG's one color: every fill and stroke in it but `none` takes it, as for an icon. |
| `stroke` | An outline around the image, `{ color, width, dash }`; the picture's style sets the default. |
| `shadow` | A drop shadow under the image, or `none` (see Shadows); the picture's style sets the default. |
| `style` | A named style, whose `fill`, `stroke`, `shadow`, and `opacity` the picture takes. |

An icon is an SVG from any set, such as Lucide, Tabler, or Material, saved in `media/` and given a `fill` or a style with one, so it follows the theme.
A `fill` on an SVG of several colors, such as a logo, paints them all in one, and `--check` warns.
The export draws an SVG as a PNG in its fill, so changing the theme in PowerPoint leaves it as it was.

## Shadows

Shapes, text boxes, and pictures take a drop shadow: `shadow: { color, opacity, blur, distance, angle }`, or `shadow: none` to clear one a style or base sets.

| Key | Value |
| --- | --- |
| `color` | The shadow's color (see `colors`); a shadow without one isn't drawn. |
| `opacity` | 0 to 100; without it, 100. |
| `blur` | The blur radius in points; 0 is a hard edge. |
| `distance` | How far the shadow falls from the element, in points. |
| `angle` | Which way it falls, in degrees clockwise from the right: 90 is straight down, 45 down and to the right. |

A number left out is 0.
A shadow follows what the element draws: a shape's outline, a text box's fill or, with none, its text, and an image's opaque pixels.
It keeps its angle on the slide when the element is rotated, as PowerPoint's does.
A style or base's shadow merges key by key, so `shadow: { distance: 8 }` lifts one card of a style higher.

```yaml
# styles.yaml
styles:
  card: { fill: light1, stroke: none, shadow: { color: dark1, opacity: 35, blur: 6, distance: 3, angle: 90 } }
```

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
- A shadow at full opacity, which reads as a second shape: shadows are usually 25 to 50.