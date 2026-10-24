# checks

`pintura --check` reports what an agent can't see; add `--export` for what the PowerPoint export can't reproduce, and `--json` for findings as JSON.
It exits 1 when there are errors, and lists warnings without failing.
`--slides 3,revenue` reports the findings on the slides it names, and those on no slide, such as a missing font, and `--diff` only those your changes since the last commit introduced.

| Kind | Finding | Level |
| --- | --- | --- |
| Structure | A file that fails to load: unknown layouts, placeholders, areas, styles, colors, fields, or keys; duplicate ids; missing media. | error |
| Structure | A `fill` painting an SVG of several colors in one. | warning |
| Text | Text running past its frame, or under `fit: shrink` past its insets even shrunk; table rows growing past the bottom of the table's frame. | error |
| Text | Text shrunk below the theme's `checks: { shrink }` percent; fonts that aren't installed. | warning |
| Geometry | Table columns wider than the table's frame. | error |
| Geometry | Elements running off the slide; boxes partly overlapping, where a box wholly inside another is layering; text crossing a chart's axis, data, or legend labels, even inside the chart's frame; an edge within 3pt of the same side of another element or area without meeting it, unless the two share a centre line. None of these count an element marked `decorative: true`. | warning |
| Consistency | A title or placeholder moved from its layout's frame; text at another size on one slide than the rest of the presentation has for its style and level; the same one-off text properties on three or more slides. | warning |
| Comments | A comment on a slide or element that no longer exists, or a comments file git doesn't track in a repository. | warning |
| Export | With `--export`: picture crops, callout tails, text columns, and the other features the export can't reproduce. | warning |

Each finding names the slide and element, `revenue/takeaway`, and a structure error names the file, the line, and the topic to read.
A theme or layout element is one finding, named by the first slide showing it.

## Keys

`theme.yaml` holds the thresholds, and a check without one is off:

```yaml
checks: { shrink: 70 }      # warn when fit: shrink scales text below 70%
```

## Looking at a presentation

- `--list`: slides with their layouts and titles.
- `--slide <stem>`: its elements with their effective frames.
- `--element <slide> <id>`: effective properties, each with the file and line that set it; for a table, each cell's too.
- `--screenshot <slide>`: a PNG of the slide, named by id or number; `--screenshot all` for a contact sheet.

## Usual mistakes

- Fixing an overflow by shrinking one box's font: shorten the text, enlarge the box, or set `fit: shrink`.
- Silencing a near-miss by moving the element a point further: align it exactly, or to an area.
- Marking content `decorative` to silence a finding: it is for decoration that bleeds or sits under content on purpose.
- Repeating a one-off on each slide it applies to: name a style in `styles.yaml`, and write `style:` on the slides.