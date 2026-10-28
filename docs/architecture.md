# Architecture

## Shape of the system

```text
                                 pintura
  ┌──────────────────────────────────────────────────────────────────┐
  │                                                                  │
  │  source files ──► load ──► presentation model ──┬──► preview     │
  │  YAML, CSV, media   ▲                           ├──► checks      │
  │                     │                           ├──► screenshots │
  │          Libreta books                          ├──► inspection  │
  │                                                 └──► HTML / PDF  │
  │  mouse edits ──► patch ──► source files                          │
  │                                                                  │
  └───────────────────────────────┬──────────────────────────────────┘
                                  │ model as JSON, plus media
                        editide API account
  ┌───────────────────────────────▼──────────────────────────────────┐
  │  editide's tools ──► .pptx                                       │
  └──────────────────────────────────────────────────────────────────┘
```

## The presentation model

The loader turns the files into one typed model: the theme, its layouts, and the slides, each level kept apart, as OOXML keeps master, layout, and slide apart.
Loading expands the authoring shorthand and keeps the meaning:

- markup becomes paragraphs and runs;
- `accent1 lighter 40` becomes a theme color with a luminance modifier;
- `frame: left` becomes the area's rectangle;
- fields and Libreta references become their values, remembering where they came from;
- theme colors, theme fonts, and style names stay references.

Everything downstream reads the model, and nothing else parses YAML.
It is the counterpart of Libreta's evaluated book, which the page renders without evaluating a formula itself.

An element's effective properties are what it ends up with after the ladder: the slide, its layout, the theme, the style it names, the base text style.
They are computed on demand, never stored.
The preview draws them, checks inspect them, and `pintura --element revenue takeaway` prints them with provenance, showing each theme color beside its hex value, so an agent sees both what `accent1 lighter 80` looks like and which line set it.

The exporter reads the model rather than effective properties, because a deck people keep working in needs the levels.
The theme's colors become the theme part, the title's style becomes the master's, and a slide keeps only what it overrides; flattening everything would hand them a deck of direct formatting.

Serialized as JSON, the model is the export contract.
It is the size of the source, since theme values appear once, in the theme, and everything else refers to them.
The exporter turns it into calls to editide's tools, and the import turns their reads of a deck back into it.

## Object model

### Levels

```text
theme ──► layout ──► slide
```

A **theme** is a folder: `theme.yaml` with the colors, fonts, background, title, and areas, `styles.yaml` with the text, table, and chart styles and number formats, and `layouts/`.
It folds together what PowerPoint splits across a theme part and a slide master.
The two are one-to-one in practice, and PowerPoint's own interface calls the pair a theme, so masters are an export artifact: the exporter writes one per theme, and authors never see one.

A **layout** inherits everything the theme defines and overrides it by name: a different title position, an extra placeholder, its own background.
It inherits from its theme only, as a PowerPoint layout inherits only from its master.
Only layouts and slides draw elements: a logo on three layouts is written three times, so each layout's file shows all it draws, and nothing a layout inherits needs switching off.

A **slide** picks a layout, writes its title and placeholders, and adds elements.

A presentation uses one theme.
More, or several color palettes on one theme, can come later without changing the model.

### The title and placeholders

A placeholder is text whose position and style the layout owns and whose words the slide writes.
There is nothing else to it: no types, no numeric indices, and no chart, picture, or table placeholders.

The title gets its own key at every level, since nearly every slide has one and PowerPoint's outline, navigation, and screen readers read it.
Other placeholders (a subtitle, a kicker above the title, a section name) are named text in the layout's `placeholders`.

What recurs because the presentation supplies it, rather than the slide's author, is a field in ordinary text: `${slide}` for the slide number, `${section}`, and any value in the presentation's `fields`, such as a footer or a date.
A slide number is then a text box on the theme showing `${slide}`, styled like anything else.

The exporter decides what becomes a PowerPoint placeholder.
The title always does.
A layout's other placeholders become body placeholders or plain text boxes, `${slide}` becomes a slide-number field, and the choice can change without touching a presentation.

### Areas

An area is a named rectangle on the theme or a layout, such as `content`, `left`, and `right`.
An element takes one as its frame: `frame: left`.
Moving an area moves every chart, table, and paragraph placed in it across the presentation, the consistency a content placeholder buys in PowerPoint, without making anything a placeholder.

### Elements

| Kind | What it is |
| --- | --- |
| `text` | A box of paragraphs. |
| `shape` | A shape with fill, stroke, and optional text. |
| `line` | A straight line between two points, with optional arrowheads. |
| `arc` | A curved line around the ellipse its frame holds, between two angles, with optional arrowheads. |
| `picture` | A media file with fit and crop. |
| `chart` | A reference to `charts/<id>.yaml`. |
| `table` | A reference to `tables/<id>.yaml`. |

Groups can come later; agents move several elements well enough without them.
`html` comes later too.

Every element has an id, unique within its slide.
Ids are how the rest of the tool talks about elements: `--check` reports that `revenue/takeaway` overflows, and `--element` and the mouse patcher address them.
The export doesn't write them as shape names, since editide's tools take no name for a shape they add.
They are slide-local, so `chart` and `source` can recur on every slide; OOXML's numeric shape ids are allocated by the exporter.

List order is z-order, back to front.
A slide's title and placeholders draw behind its elements unless the list names them, as `- title`, where they draw instead, as PowerPoint keeps a slide's placeholders among its shapes.

### Geometry and units

Boxes have `frame: [x, y, w, h]`, or an area's name in its place.
One key keeps a move or a resize a one-line diff and lets an area stand in for the whole rectangle.
Lines have endpoints, `from` and `to`, where OOXML has a bounding box plus flips that decide which corner the line starts in.

Units are points throughout.
Font sizes and stroke widths are points in every tool, so points for geometry keep one unit in the files; pixels would put a 12pt font in a box measured at 16 to the point.
Both convert to EMU exactly (12,700 per point, 9,525 per pixel), so neither is easier to export.
The default slide is 960 × 540, PowerPoint's 16:9 (13.333 × 7.5 inches).

### Shapes

Sixteen shapes cover what professional slides draw: rectangles, rounded rectangles, ellipses, triangles, diamonds, parallelograms, trapezoids, pentagons, hexagons, chevrons, home plates, block arrows, braces, brackets, and callouts.
The rest of OOXML's 186 presets (the smiley face, the lightning bolt, the moon) stay out.

The shapes are designed for the page, and the exporter adapts them.
A directional shape takes `direction`, and the exporter picks a preset or a rotation.
Adjustments that are lengths are in points, so a corner radius stays constant across boxes of different sizes, where OOXML's proportions make it drift.
A callout's tail is a point on the slide, where OOXML stores it as a fraction of the box.

### Text

Content is a Markdown subset: one paragraph per line, `- ` bullets indented two spaces per level, `1. ` numbering, `**bold**`, `*italic*`, links, and Pandoc's bracketed spans for runs, `[text]{.emphasis}` for a named style and `[text]{size=40 font=heading}` for one-off formatting.
`{.lead}` alone on a line styles the next paragraph.
It resolves to paragraphs of runs, each run with its own text style.

A box carries a style, alignment, insets, and fit: `none`, `grow`, or `shrink`.
`grow` sets the height to the text's, and the width too when the text doesn't wrap, anchored on the side the first paragraph aligns to; `--check` places the box where it was drawn.
PowerPoint lets text spill past its box, which a person notices and an agent can't, so overflow under `none` fails `--check`.
The exporter computes the grown height or the shrink scale rather than leaving it to PowerPoint, which recalculates both only when someone edits the text.

### Styles

Every style is a YAML definition in the theme's `styles.yaml`; Pintura has no built-in style names.
One namespace serves boxes, paragraphs, and runs, and a style used on a run applies only its character properties.
Table styles name row styles, which Libreta's row roles find by name, and tables layer columns, rows, and cells on top.
Chart styles set series colors, axes, legends, and labels, and travel with the theme to every presentation using it.
One-off formatting on an element or a run is always allowed.

### Color

Twelve theme colors with readable names: `dark1`, `light1`, `dark2`, `light2`, `accent1` to `accent6`, `hyperlink`, `followed_hyperlink` (OOXML's `dk1` to `folHlink`).
Modifiers follow PowerPoint's palette: `accent1 lighter 40` is its "Lighter 40%" (luminance mod 60, offset 40), `accent1 darker 25` its "Darker 25%" (luminance mod 75), and `opacity` sets alpha.
A theme can name colors (`positive`, `negative`, `muted`) as a theme color with modifiers or as a hex value, and hex works anywhere.
A theme color exports as a scheme reference, so changing the theme in PowerPoint still recolors it.

OOXML's color map (the `tx1` and `bg1` indirection) is dropped.
The exporter writes the identity map, and a dark layout states its own text colors.

### Fonts

Two theme fonts, `heading` and `body`, OOXML's major and minor, exported as theme-font links (`+mj-lt`, `+mn-lt`).
A theme can ship font files in `fonts/`, which the page registers under the families the files name, so the preview, its prints, and the HTML export draw in them on any machine; the `.pptx` doesn't embed them.

## What we keep from OOXML, and what we drop

| OOXML | Pintura |
| --- | --- |
| A package of parts, one per slide, shared media by relationship | Kept: a folder of files, one per slide, media by path |
| Master, layout, slide | Kept as theme, layout, slide |
| Shapes on the master, hidden per layout all at once | Dropped: each layout lists its own elements |
| Theme part separate from the master | Folded into the theme |
| Placeholders for every kind of content, bound by type and numeric index | Replaced: a title and text placeholders bound by name, and areas for content |
| Date, footer, and slide-number placeholders | Replaced by fields in ordinary text |
| Twelve scheme colors with luminance transforms | Kept, with readable names and PowerPoint's lighter and darker steps |
| Color map (`clrMap`, `tx1`, `bg1`) | Dropped |
| Major and minor theme fonts | Kept as heading and body |
| Title, body, and other text style ladders | Replaced by one base style with bullet levels, and named styles |
| Format scheme and style-matrix references (`fillRef idx="2"`) | Dropped: fills are explicit or theme colors |
| Background as a theme style reference (`bgRef`) | Dropped: backgrounds are explicit |
| Table styles addressed by GUID | Dropped: named styles, exported as explicit cell formatting |
| 187 presets with proportional adjust values | Replaced by 16 shapes with directions and adjustments in points |
| Lines as a bounding box with flips | Replaced by endpoints |
| Custom geometry | Out of scope |
| Groups and their child coordinate spaces | Flattened: an import keeps a group's elements, in slide coordinates |
| Z-order by document order | Kept, and visible as list order |
| Numeric shape ids | Replaced by slide-local names |
| EMU | Replaced by points |
| Chart data in an embedded workbook plus a cache | Replaced by CSV or a Libreta book; the exporter writes both |
| Autofit scale stored as a stale computed value | Computed at export |
| Speaker notes and comments | Dropped |
| Application defaults inside PowerPoint.exe | Dropped: every default is in a theme file |

## Loading and provenance

The loader parses YAML with YamlDotNet, keeping each node's file, line, and column, and rejects unknown keys as Libreta does.
Every effective property can name its origin: `size 16 ← themes/acme/styles.yaml:4 text.size`.
That answers "why is this 16pt" in one step, the question the blog post shows taking a scavenger hunt through a `.pptx`.

## The CLI for agents

The format is much larger than Libreta's, so its documentation is disclosed a topic at a time rather than printed whole.

- `pintura --docs` prints an index, one line per topic: presentations, themes, layouts, slides, text, shapes, charts, tables, checks.
- `pintura --docs charts` prints one topic: its keys, one complete example, and the usual mistakes.
- Errors name their topic: `unknown key legend_position in charts/revenue-by-segment.yaml:6; see pintura --docs charts`.
- The default theme and the example presentation are the long-form documentation, as Libreta's sample book is.

Looking at a presentation:

- `--list`: slides with their layouts and titles.
- `--slide revenue`: its elements with their effective frames, one line each.
- `--element revenue takeaway`: effective properties with provenance.
- `--screenshot revenue`: a PNG of the rendered slide, the agent's way to look at it.
- `--check`, and `--json` on any of these.

Screenshots and text measurement need a browser whether or not anyone has the page open, so Pintura drives headless Chromium through Playwright.
It launches an installed Edge or Chrome through Playwright's channels, so the measurement matches the browser most people preview in, and fetches Playwright's own headless Chromium only where neither is installed, as on WSL, where Windows' Edge can't be driven from Linux.
It sees the fonts fontconfig sees; on WSL, a `<dir>/mnt/c/Windows/Fonts</dir>` entry in `/etc/fonts/local.conf` gives it the Windows fonts, Office's included, and `--check` names that fix when a theme font is missing.
A running server launches the browser on the first request and keeps it; without one, the command starts a headless server in the background, which keeps the browser until it has been idle five minutes.

## Preview

The server is Libreta's: ASP.NET minimal API, file watchers over the presentation and its theme, server-sent events announcing changes, and load errors shown in the page rather than stopping the server.
It keeps Libreta's lifecycle too: launching a presentation already served reuses that server, `--url` prints the address without opening a tab, and the server stops shortly after its last tab closes.

Each slide is a 960 × 540 stage in which one CSS pixel is one point, scaled to fit with a transform.
Text boxes are absolutely positioned elements, shapes and lines are SVG, charts are SVG from Pintura's own renderer, and tables are HTML tables with fixed column widths.

The page offers a sidebar of thumbnails grouped by section, one slide at fit, a grid of all slides, and a full-screen present mode driven by the arrow keys.
Overlays show areas, placeholders, and markers where checks fail.
The inspector shows the selected element's findings, changes, and what its file sets, with every effective property and its provenance folded beneath.

Charts reproduce PowerPoint's defaults (gap width, overlap, label placement), and every value axis is scaled in C# by `AxisScale.cs`, so the page draws it and the export fixes the same min, max, and step in PowerPoint.
Shapes are hand-written SVG paths; sixteen don't need a geometry evaluator.

## Fidelity: the preview is not PowerPoint

The browser is the reference.
When the export disagrees with the preview, the export gets fixed; only when PowerPoint can't do what the preview does does the preview change.

The preview runs on localhost, so it renders with the fonts installed on the machine, the same files PowerPoint uses there.
The default theme uses Arial, which every machine and every PowerPoint has, and theme fonts in `fonts/` load through the FontFace API ahead of installed ones.
On a machine without Office, metric-compatible substitutes keep line breaks the same: Carlito for Calibri, Liberation Sans for Arial.
That leaves layout, where the two programs differ:

- **Line spacing.**
  PowerPoint's single spacing is 1.2 times the size in every font, whatever its metrics, as measured in PowerPoint, so the preview's lines are too.
- **Autofit.**
  Computed at export, never left to PowerPoint.
- **Charts.**
  PowerPoint's default styling, reproduced in the renderer, and Pintura's own axis scale, fixed in the export.
- **Table row heights.**
  PowerPoint grows a row to its text and its cells' insets, an empty cell holding a line at its own size, with borders centered on the edge adding nothing, and the preview draws rows the same way.
- **Bullets.**
  A level's `indent` is OOXML's `marL` and its `hanging` the negative of OOXML's `indent`, exactly.
- **Missing fonts.**
  The page reports fonts it couldn't render, and an exported font the recipient lacks reflows unless the export embeds it.

The gap is measured per text box as line counts, the plainest metric there is: does this text wrap to the same number of lines in both?

## Mouse edits

Select, drag, resize, nudge with arrow keys, and snap to areas, guides, and other elements' edges and centers.
A selection of several elements moves or deletes as one request to `api/edit`, whose edits each name their file where it is the theme's or a layout's; the server checks them all before writing any, puts back the files it wrote if a later one fails, and answers with one undo.

Write-back is a patch, never a re-serialization.
The loader keeps the source of each value, and the page sends it back with the edit: the value's key path, such as `elements.chart.frame`, where a list entry is named by its id, the source it drew, and the new value.
`Patcher.cs` parses the file again, finds the value by its path, and replaces its span alone, preserving comments, formatting, and whatever else changed in the file since.
It reads the result back and tries the next way of writing the value, such as quoting it, until it reads as the edit meant.
A value a gesture moves lands on a whole point or the edge it snapped to, and the rest keep the layout's values, to a hundredth of a point.

Moving the title writes a frame into the slide's title, turning `title: Revenue` into `title: { text: Revenue, frame: [x, y, w, h] }` on its one line, and moving an element placed in an area replaces the area's name with numbers.
Dragging a layout's element on a slide is refused with the path of the file that owns it, as PowerPoint locks master shapes on slides.

A patch whose value no longer reads as the source the page drew is rejected and the page redraws from the file, so a person and an agent can work on one presentation at once.

The server answers each patch with its undo: for every value, the smallest node the patch changed, its source now, and its source before.
That node is the value itself, or the map or shorthand title a new key went into, so undoing a title's move turns it back into `title: Revenue`.
An undo is sent as a patch that restores that source, checked like any other, and its answer is the redo; the page keeps both stacks.

Deleting an element removes its lines from `elements`, with the blank lines that set it apart, and deleting the last one leaves `elements: []`.
Its undo is not the list as it read before, which an agent's edit to any other element would refuse, but the removed lines and the id of the element they followed, so putting it back only needs that element to still be there.

Reordering slides moves one slide's line in `presentation.yaml`, with any comment on it, to after the entry it was dropped after, a slide or a section, or to the top.
The page sends the entry the slide followed as it drew the sidebar, and the move is refused when the slide follows another now or the entry it is dropped after is gone; its undo is the same move back.

Literal text is edited in place and written back as markup.
The markup parser records the source of every character it reads, so a reworded stretch is replaced in its line, escaped where it would read as markup, while formatting and fields outside it stay as written.
A paragraph split or joined is the same stretch replaced across lines, a paragraph break counting as a character that spans the line's end, the next one's style line, and its list marker.
Typing over a field replaces its source with the typed text, and a stretch that crosses formatting is refused rather than guessed at.
Typing inside a field instead rewrites its value where `presentation.yaml` writes it, a section's name or an entry in `fields`, which the payload gives each such run as its key path and source, a list entry named by its place as in `slides[6].section`.

## Checks

`--check` exits nonzero on errors, as Libreta's does, and lists warnings without failing.
Export findings need `--export`, since they describe the exporter rather than the slides.

- **Structure** (errors): unknown layouts, placeholders, areas, styles, colors, formats, charts, tables, or media; duplicate ids; unknown keys; unresolved fields.
- **Text**: overflow (error); shrink below a threshold, fonts that didn't render (warnings).
- **Geometry** (warnings): elements off the slide; partly overlapping elements, since a box wholly inside another is layering; near-miss alignment, where an edge sits within a few points of the same side of another element or area without meeting it, since facing edges a few points apart are spacing.
- **Consistency** (warnings): things that should match across slides and don't: a title moved on one slide, body text at 14pt on one slide and 16pt on the rest, the same one-off formatting on several slides, which wants to be a style.
- **Export** (warnings): features the exporter can't reproduce.

## Libreta link

A presentation includes books with the `books:` entries Libreta uses to compose books, and `pintura --docs books` is the format.

- **Text** interpolates `${acme::historical.segments.connectivity_revenue["2025"]}`, Libreta's own reference syntax, displayed as Libreta formats it, or `| billions_of_dollars` for a format named in the theme.
- **Charts** take series from lines and categories from columns.
- **Tables** render a Libreta view: its row roles find the table style's row styles by name, and its indents the table style's `indent`.
- **Watching** covers the book's files, and folders linked into it, so editing a fact redraws every slide showing it.
- **Revisions** extract the books with the presentation and its theme, so a restated fact is a change.
- **Tracing**: Ctrl-clicking a linked number, in text, a table cell, or a chart's point, opens Libreta's inspector at that cell, so a board slide traces back to the filing.

Pintura and Libreta each work without the other.
The link crosses a process boundary, the Libreta CLI's JSON output, in `Books.cs`.
The loader reads every line of a book with one `libreta --lines`, the first time a slide references it, and each view a table presents with `libreta --view`, so a reference resolves where it is parsed and fails with the file and line like an unknown field.
Answers are kept until a file in the book changes, so a reload for a slide's edit starts no Libreta.
A reference stays a field in the model, named as written, and the scene fills it in with the book's value, remembering the book's entry in `presentation.yaml` as its origin; a chart or table from a book holds the values it read, as one from a CSV does.
Tracing runs `libreta <book> --cell <line> <column>`, which opens the first view presenting the cell in the server running the book, or a new one.

## Export

Export runs on an editide API account, metered by slides delivered.

### Mapping

| Pintura | OOXML |
| --- | --- |
| Slide size | `p:sldSz` |
| Theme colors | `a:clrScheme`, with luminance transforms |
| Theme fonts | `a:fontScheme`, `+mj-lt`, `+mn-lt` |
| Base text style, title style | `p:txStyles` |
| Theme background | `p:bg` on the master |
| Layout and slide backgrounds | `p:bg` on the layout or slide |
| Title | `p:ph type="title"` on the master and layouts |
| Layout | `p:sldLayout` |
| Other placeholders | `p:ph type="body"`, or a text box (`p:sp`) |
| Layout elements | non-placeholder shapes on the layout, but one showing `${section}`, which goes on each slide |
| `${slide}` | `a:fld type="slidenum"` |
| Area | nothing: elements get their frames (`a:xfrm`) |
| Text | `p:sp` with `p:txBody` |
| Shape | `p:sp` with `a:prstGeom` and its adjustments |
| Line | `p:cxnSp` with flips, `a:headEnd`, `a:tailEnd` |
| Picture | `p:pic` |
| Chart | `p:graphicFrame`, chart part, embedded workbook |
| Table | `p:graphicFrame` with `a:tbl` |
| Section | `p14:sectionLst`, the slides before the first in PowerPoint's "Default Section" |
| Element id | shape name, `p:cNvPr/@name`, not written |

### Pipeline

The exporter replays the model as calls to editide's tools, `POST /api/tools/{tool}`, with no language model involved.
It starts from a copy of the blank 16:9 deck Pintura carries, resized to the presentation, then makes `edit_theme`, `edit_slide` on the master and on each layout (created with `create_layout`), and `add_slide` and `edit_slide` for each slide, and ends with one `edit_sections`.
That is about five calls a slide and twenty for the theme and layouts, and it isn't atomic.
A re-export starts from the last one and rewrites only the slides whose model changed, through a manifest beside the deck.

### Gaps

What the export can't write, which it reports rather than approximates:

- Picture crop.
- Sections, on editide before 1.0.7, which has no `edit_sections`.
- Callout tails, which come out normalized.
- Font embedding, needed before a brand font survives on a machine without it.

The chart types editide's tools write natively (column, bar, and line in all three groupings, pie, doughnut) set Pintura's first chart vocabulary, and the waterfall followed when the tools could write it.

## HTML snippets (later)

An `html` element places a self-contained HTML file in a frame.
The preview renders it in a sandboxed iframe with the theme passed in as CSS variables (`--accent1`, `--font-body`), so snippets stay on brand, and with no network access, so they stay deterministic.
The export keeps an SVG snippet as an SVG picture, which stays vector in PowerPoint, and rasterizes anything else at two or three times its size into a PNG.
Whether rasterizing happens in the CLI or the export service is for when it gets built.

## Stack and layout

Everything mirrors Libreta: .NET 10, ASP.NET minimal API, YamlDotNet, and vanilla JavaScript with no build.

```text
pintura/
  src/Pintura/              CLI, server, loader, model, checks, patcher
    wwwroot/                index.html, app.js, app.css, fonts/
  src/Pintura.Tests/
  themes/default/
  presentations/example/    a board update for Acme, a made-up company
  docs/                     format.md, running.md
```

## Open questions

1. Text starting with `[` or `{` must be quoted in YAML: acceptable, or pick markup that never collides?
2. When do presentations need more than one theme, or more than one palette on a theme?