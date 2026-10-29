# Principles

Each principle is a test a design decision has to pass.
Most are inherited from Libreta; the rest come from the export.

## 1. Agents write, humans review

The files are the presentation.
The browser shows it and redraws on save, and nothing lives only in the page.
An agent never needs the preview to make a change, only to have it checked.

## 2. Name what a thing is; styles decide how it looks

A slide says `style: caption` or `{.positive}`, and `styles.yaml` says what those look like.
Restyling a presentation is an edit to one file.
One-off formatting is fine for a one-off; the same override on several slides is a style waiting to be named, and `--check` says so.
Colors work the same way: mostly theme colors, and hex when a color really is fixed.

## 3. All of the truth is in the files

PowerPoint resolves some properties from defaults that exist only inside PowerPoint.exe.
Pintura has no built-in defaults: the default theme is a file you can read, copied into the project when it is created.
Every effective property traces to the file and line that set it.

## 4. Three levels, all visible

Theme, layout, slide: the same ladder as PowerPoint's master, layout, slide, and nothing deeper.
Inheritance isn't the problem; inheritance you can't see is.
Only layouts and slides draw, so a layout's file shows everything on its slides but theirs.
A logo on three layouts is written three times: repetition an agent keeps in step costs less than inheritance a reader has to undo.

## 5. Our model, PowerPoint's objects

The format is designed for the people and agents writing it, and the exporter adapts it to OOXML.
Lines have endpoints, corner radii are in points, and only text is a placeholder, whatever OOXML does.
The exporter gates only capability: a chart type, fill, or text property ships when it can leave the export as a native, editable PowerPoint object.
A convenience PowerPoint lacks, such as a stack's total, is allowed when it compiles to objects PowerPoint has.
`html` is the one deliberate exception, and it exports as a picture, visibly so.

## 6. Numbers have a source

A figure is typed on the slide, read from a CSV, or referenced from a Libreta book.
A slide never computes: a figure it needs becomes a column in the CSV or a formula in the book.

## 7. Checks are the feedback loop

An agent can't see a slide the way a reviewer does, so `--check` reports it: text overflowing its box, an edge 2pt off alignment, a title sitting lower than on every other slide.
`--screenshot` shows the rest.
As in Libreta, checks report rather than block.

## 8. One way to write each thing

Synonyms cost an agent tokens and consistency.
One unit: points.
Boxes have a `frame`, lines have endpoints.
One place for each value.

## 9. Small diffs

Moving a box changes one line.
Reordering slides changes `presentation.yaml`, never file names.
Ids stay stable across edits.

## 10. The mouse nudges; it doesn't design

Dragging, resizing, rewording, and picking a theme color, an outline weight, a style, or an alignment write the same diff an agent would.
The mouse picks from closed sets, what the theme names or the format enumerates, and moves and resizes what has a frame; free values such as a size or a font belong to the agent: no ribbon, no format panes.

## 11. The engine knows no design

No style name, layout, or component is built into Pintura.
A KPI tile or a two-column comparison is a layout, an area, or a style in a theme file, not a branch in the renderer.

## 12. Export makes a deck people keep working in

Real layouts and title placeholders, theme colors and fonts as theme references, native charts carrying their data, native tables.
Not pictures of slides, and not text boxes with every property flattened onto them.

## 13. Nothing to build

Vanilla JavaScript served from source, as in Libreta, so the preview hot-reloads and an agent can change the interface without a toolchain.