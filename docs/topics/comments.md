# comments

Reviewers comment on slides in the preview, and the comments live in `comments.yaml` beside `presentation.yaml`, never in the slides.
Right-click a slide to write one, pinned to the element under the pointer or to the slide; click a pin to reword, reply to, revert, or resolve it.

## Working through them

```bash
pintura presentations/q3 --comments
pintura presentations/q3 --comments --json
```

`--comments` lists them in slide order, each beside the file and line that writes what it points at, which is the line to change.
Act on each open comment, then mark it in `comments.yaml`: `done:` with what you changed, or `skipped:` with why you didn't.
Leave the entry: the reviewer checks each marked comment against its pin, then resolves it or answers it with a reply.
A comment with replies waits on its last one: act on that reply, and mark it `done:` or `skipped:` rather than the comment.
A reply with `revert: true` asks to undo what the agent did for the comment, for the reason in its `text` if any; put back what that change replaced, then mark the reply `done:`.
Resolve entries only when the reviewer asks, with `--resolve`: whichever they name, or, asked to clear the comments, those marked `done:`, saying which open or skipped ones are left.
Never delete an entry by hand: its thread is the record of how the request went.
An empty file means no comments.

## Resolved comments

```bash
pintura presentations/q3 --resolve k3fq,m7wd
pintura presentations/q3 --comments --resolved
```

Resolving a comment moves it, with its whole thread, to the end of `comments-resolved.yaml` beside `comments.yaml`, under a `resolved` key saying who resolved it, when, the commit the deck was at, and how the thread ended:

| Outcome | The thread |
| --- | --- |
| `accepted` | Resolved after its first `done:`, with no reply. |
| `partly` | Took a reply before it was resolved. |
| `reverted` | Had a reply asking to revert what was done. |
| `skipped` | Resolved after a `skipped:`, with no reply. |
| `withdrawn` | Resolved while still waiting on an agent. |
| `removed` | Went with its slide, deleted in the preview. |

`--comments --resolved` lists them in the order they were resolved, each with its outcome.
Commit both files with the slides they're about, as Save does: in a repository, `--check` warns of either when git doesn't track it.
New comments take ids no resolved comment had, so an id names one thread for good.

## Keys

| Key | Value |
| --- | --- |
| `id` | Four letters and digits, unique in the file. |
| `slide` | The slide's file stem. |
| `element` | The id of the element, the title (`title`), or the placeholder commented on; absent for the slide itself. |
| `at` | `[x, y]` in points from the element's top-left corner, or the slide's without an element. |
| `by` | Who wrote it. |
| `date` | When, as `2026-10-07 14:03`. |
| `rev` | The commit the deck was at when the reviewer wrote it, absent outside a repository; commit at the end of every round, before the reviewer looks, so it names what they saw. |
| `uncommitted` | `true` when the slides or theme had changed since `rev`, so the reviewer saw more than that commit. |
| `text` | What the reviewer asks for. |
| `done` | What an agent changed for it, for the reviewer to check. |
| `skipped` | Why an agent didn't act on it; a comment is done or skipped, never both. |
| `replies` | The reviewer's answers once an agent marked the comment, in order, each with `by`, `date`, `rev`, `uncommitted`, `text`, and its own `done` or `skipped`; `revert: true` asks to undo what was done, and its `text` is optional. |
| `resolved` | Only in `comments-resolved.yaml`: `{ by, date, rev, uncommitted, outcome }`, the outcome one of those above. |

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

- id: m7wd
  slide: revenue
  element: total
  at: [20, 10]
  by: Ana
  date: 2026-10-07 14:08
  text: Say the growth too
  done: Added "up 33% on 2024"
  replies:
    - by: Ana
      date: 2026-10-07 16:20
      text: Put it on its own line
      done: Moved the growth to a second line
    - by: Ana
      date: 2026-10-07 16:45
      revert: true
      text: One line read better
```

A reply goes only after a `done` or `skipped`, and a revert only after a `done`.
Rewording what waits on an agent changes its words in place; once it is marked, the reviewer replies instead.
The preview writes new comments in slide order, and deleting a slide in the preview resolves its comments as `removed`, which undo reopens.
Comments, resolved or not, are left out of the export, `--diff`, and the versions the preview compares against.

## Usual mistakes

- Deleting a comment because it's done: mark it `done:`, and leave resolving it to the reviewer.
- Deleting a comment the reviewer asked to clear: `--resolve` it, so its thread and outcome stay in `comments-resolved.yaml`.
- Editing `comments-resolved.yaml`: it is the history; leave it to `--resolve` and the preview.
- Rewording a comment to say it's done: its `text` stays the reviewer's; write `done:` beside it.
- Marking a comment `done:` again when its last reply waits: mark the reply.
- Renaming a slide's file and leaving its comments on the old stem: `--check` warns of a comment on a slide or element that no longer exists; update `slide` or `element`.
- Writing a comment into a slide's file: slides hold only the presentation.
