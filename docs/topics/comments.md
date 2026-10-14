# comments

Reviewers comment on slides in the preview, and the comments live in `comments.yaml` beside `presentation.yaml`, never in the slides.
Right-click a slide to write one, pinned to the element under the pointer or to the slide; click a pin to reword, reopen, or resolve it.

## Working through them

```bash
pintura presentations/q3 --comments
pintura presentations/q3 --comments --json
```

`--comments` lists them in slide order, each beside the file and line that writes what it points at, which is the line to change.
Act on each open comment, then mark it in `comments.yaml`: `done:` with what you changed, or `skipped:` with why you didn't.
Leave the entry: the reviewer checks each marked comment against its pin, then resolves it, which deletes it.
Delete entries only when the reviewer asks: whichever they name, or, asked to clear the comments, those marked `done:`, saying which open or skipped ones are left.
An empty file means no comments.

## Keys

| Key | Value |
| --- | --- |
| `id` | Four letters and digits, unique in the file. |
| `slide` | The slide's file stem. |
| `element` | The id of the element, the title (`title`), or the placeholder commented on; absent for the slide itself. |
| `at` | `[x, y]` in points from the element's top-left corner, or the slide's without an element. |
| `by` | Who wrote it. |
| `date` | When, as `2026-10-07 14:03`. |
| `text` | What the reviewer asks for. |
| `done` | What an agent changed for it, for the reviewer to check. |
| `skipped` | Why an agent didn't act on it; a comment is done or skipped, never both. |

## Example

```yaml
- id: k3fq
  slide: revenue
  element: chart
  at: [152, 125.4]
  by: Ana
  date: 2026-10-07 14:03
  text: The 2023 label is wrong
  done: Relabelled the 2023 bar from the book's 2023 column

- id: p2xa
  slide: revenue
  at: [480, 300]
  by: Ana
  date: 2026-10-07 14:05
  text: |-
    Too empty.
    Add the margin chart from the book.
```

Rewording a marked comment in the preview reopens it, dropping its `done` or `skipped`.
The preview writes new comments in slide order, and deleting a slide in the preview deletes its comments, which undo puts back.
Comments are left out of the export, `--diff`, and the versions the preview compares against.

## Usual mistakes

- Deleting a comment because it's done: mark it `done:`, and leave resolving it to the reviewer.
- Rewording a comment to say it's done: its `text` stays the reviewer's; write `done:` beside it.
- Renaming a slide's file and leaving its comments on the old stem: `--check` warns of a comment on a slide or element that no longer exists; update `slide` or `element`.
- Writing a comment into a slide's file: slides hold only the presentation.
