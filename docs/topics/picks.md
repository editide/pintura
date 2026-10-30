# picks

An agent offers two to four variants of a slide, the reviewer sees them side by side in the preview and keeps one, and `picks-resolved.yaml` records what was offered, what was kept, and why.
The record is the point: a pick is a preference pair, which deleting the losing slides by hand doesn't leave behind.

## Offering one

Write each variant as an ordinary slide file, add the pick to `picks.yaml` beside `presentation.yaml`, and name the pick in `slides` where its slide goes:

```yaml
- id: p4kx
  by: Claude
  date: 2026-11-02 10:14
  rev: 3f9a1c2
  variants:
    - slide: growth
      intent: Revenue and signed backlog as two charts, side by side
    - slide: growth-stacked
      intent: One stacked chart, so the gap between earned and signed reads at a glance
    - slide: growth-waterfall
      intent: A waterfall from last year's revenue to this year's backlog
```

```yaml
slides:
  - business
  - pick: p4kx
  - cash
```

`intent` is one neutral line on what the variant is trying to show; never a recommendation or pros and cons, which anchor the reviewer and spoil the record.
Which variant you expect the reviewer to keep stays outside the presentation, so the preview never shows it.
Commit the variants, `picks.yaml`, and `presentation.yaml` together at the end of the round, as with any change.

A pick takes one place and one slide number, drawn as its first variant wherever the deck is drawn whole: the slide list, the grid, presenting, and every export.
Every variant is checked as any slide is, and `--slide`, `--element`, and `--screenshot` take a variant's stem.

## In the preview

Opening a pick shows its variants side by side, each under its intent, with Keep beneath each and None below them all, beside a box for why.
Why is optional for Keep and required for None.
Comments pin to a variant as to any slide, so "keep B, with A's legend" is a Keep on B and a comment on it.

Keep writes the kept slide in place of the pick's entry in `presentation.yaml`, deletes the other variants' files, resolves their open comments as `removed`, and moves the pick to the end of `picks-resolved.yaml`.
None does the same keeping the first variant in place, so the deck still draws, and records `chosen: none`.
Undo reopens the pick, its files, and its comments, as it does for a deleted slide.
Save commits both files with the deck, as it does the comments.

Only the reviewer resolves a pick, from the preview.

## Listing them

```bash
pintura presentations/q3 --picks
pintura presentations/q3 --picks --resolved
```

`--picks` lists the open picks with their variants and intents, and `--picks --resolved` the resolved ones in the order they were resolved, each with what was kept and why; both take `--json`.

## Keys

| Key | Value |
| --- | --- |
| `id` | Four letters and digits, unique in the file. |
| `by` | Who offered it. |
| `date` | When, as `2026-11-02 10:14`. |
| `rev` | The commit the deck was at when it was offered. |
| `variants` | The slides offered, in order, each with `slide`, its file stem, and `intent`; the first draws in the pick's place. |
| `resolved` | Only in `picks-resolved.yaml`: `{ by, date, rev, chosen, why }`, `chosen` the slide kept or `none`. |

```yaml
- id: p4kx
  by: Claude
  date: 2026-11-02 10:14
  rev: 3f9a1c2
  variants: [...]
  resolved: { by: Daniel, date: 2026-11-02 18:40, rev: 8b21d07, chosen: growth-stacked, why: One chart says it }
```

## Checks

- `--check` warns of every open pick, and of `picks.yaml` or `picks-resolved.yaml` untracked in a repository.
- It errors on a pick with fewer than two variants, and on a variant whose slide doesn't exist.
- A slide both in `slides` and in a pick, a slide in two picks, and a `pick:` naming no open pick stop the presentation loading.
- The export draws each open pick's first variant and lists the pick among its warnings.

## Usual mistakes

- Deleting the variants you'd drop: leave the choice to the reviewer, so the pick records it.
- Resolving a pick yourself, or editing `picks-resolved.yaml`: it is the reviewer's record.
- Writing a recommendation into `intent`: say what the variant shows, not why it's better.
- Listing a variant in `slides` as well as in the pick: name the pick alone.
- Offering one variant: list the slide in `slides` instead.
