# presentations

A presentation is a folder holding `presentation.yaml`.

```text
q3-board-update/
  presentation.yaml
  comments.yaml  reviewers' comments, if any; see pintura --docs comments
  slides/        one file per slide
  charts/        chart files and their CSV
  tables/        table files and their CSV
  media/         pictures
```

`pintura --new <folder>` creates one: the default theme copied into `theme/`, to read and change, and a cover slide.

Slides, charts, and tables are named by file stem: `revenue` means `slides/revenue.yaml`.
Paths are relative to the file that names them.

## Keys

| Key | Value |
| --- | --- |
| `name` | The presentation's name. |
| `theme` | Path to a theme folder. |
| `size` | `[width, height]` in points; `[960, 540]` is 16:9. |
| `fields` | Text any slide or layout shows as `${name}`. |
| `books` | Libreta books the slides quote; see `pintura --docs books`. |
| `slides` | The slides in order, by file stem, with `section:` entries between them. |

## Example

```yaml
name: Q3 Board Update
theme: ../../themes/acme
size: [960, 540]

fields:
  footer: Confidential. Prepared for board discussion.
  date: September 30, 2026

slides:
  - cover
  - section: Financial performance
  - revenue
  - margins
  - section: Outlook
  - guidance
```

A `section` entry starts a group: slides after it show its name as `${section}`, up to the next `section`.
Slides before the first section show `${section}` as empty.
A `section` entry draws nothing: a divider is an ordinary slide, usually on a section layout, and can write `title: ${section}`.

A theme and its layouts can show fields too, such as a footer showing `${footer}`, so `fields` defines every field they use.
Two fields need no entry: `${slide}` is the slide number, and `${section}` is the current section's name.

## Usual mistakes

- Numbering file names (`01-cover.yaml`): the order is `slides`, so reordering is a one-line move.
- Listing a slide file with its extension or folder: write `revenue`, not `slides/revenue.yaml`.
- Writing a field that `fields` doesn't define: it is an error, not empty text.
- Leaving out a field the theme shows: search the theme's and layouts' files for `${`.