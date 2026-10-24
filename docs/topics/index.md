# Pintura

A presentation is a folder of YAML rendered as slides.
Run `pintura --docs <topic>` for one topic: its keys, a complete example, and the usual mistakes.

- `presentations`: `presentation.yaml`, the folder layout, slide order, sections, and fields.
- `themes`: `theme.yaml`: theme colors, fonts, background, the title, and areas.
- `styles`: `styles.yaml`: the base text style, bullet levels, named text styles, and number formats.
- `colors`: theme colors, lighter and darker steps, named colors, hex, and opacity.
- `layouts`: variations on the theme: the title, placeholders, areas, elements, and background.
- `slides`: picking a layout, writing the title and placeholders, and placing elements.
- `text`: the markup for paragraphs, bullets, numbering, bold, italic, links, spans, and fields.
- `shapes`: the sixteen shapes, lines and arrowheads, pictures, fill, stroke, and shadow.
- `charts`: chart files, CSV data, chart types, and chart styles.
- `tables`: table files, CSV data, table styles, columns, rows, cells, merges, and formats.
- `books`: including Libreta books, and references to them in text, charts, and tables.
- `checks`: what `pintura --check` reports, and the theme's thresholds for it.
- `comments`: reviewers' comments in `comments.yaml`, and working through them with `pintura --comments`.

Units are points everywhere; the slide is 960 × 540 unless `presentation.yaml` says otherwise.
Unknown keys are errors.
Examples of slides, text, shapes, charts, and tables use the default theme's style names.

## Working beside others

Other agents may be working in the same presentation, each on its own slides, while the reviewer edits in the preview.
Given slides to work on, pass them to `--comments`, `--check`, and `--diff` with `--slides`, so each lists only what's yours, and act only on those.
Commit your own changes at the end of every round, naming each file: `git add slides/revenue.yaml comments.yaml`, never `git add -A`, `git add .` or `git commit -a`, which take the reviewer's and other agents' unfinished work.
Never stage, revert, or tidy a change you didn't make: a file `git status` shows changed that you didn't touch is another agent's or the reviewer's.
Change the theme or `presentation.yaml` whenever your slides need it, and commit that change as soon as it's done, since git commits whole files and another agent committing one takes everything in it.
The reviewer's Save commits only what they edited in the preview, never your unfinished work.