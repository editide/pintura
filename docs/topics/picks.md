# picks

An agent offers two or three variants of a slide, the reviewer sees them side by side in the preview and keeps one, and `picks-resolved.yaml` records what was offered, what was kept, and why.
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

Offer three at most, since a fourth isn't worth the reading time, and two when you can't make a third that is genuinely different: a filler variant wastes the reviewer's look and dilutes the record.
`intent` is one neutral line on what the variant is trying to show; never a recommendation or pros and cons, which anchor the reviewer and spoil the record.
Which variant you expect the reviewer to keep stays outside the presentation, so the preview never shows it.
Commit the variants, `picks.yaml`, and `presentation.yaml` together at the end of the round, as with any change.

A pick takes one place and one slide number.
The slide list and the grid show it as a card saying how many variants wait, so none is seen before the others; presenting and every export draw its first variant.
Every variant is checked as any slide is, and `--slide`, `--element`, and `--screenshot` take a variant's stem.
Check each variant clean before offering it: the reviewer is choosing between takes, not spotting overflow.

## In the preview

Opening a pick shows its variants side by side, lettered A, B and C in an order shuffled once for the pick, so where a variant sits can't favour it, each under its intent with Keep beneath it, and None below them all.
Clicking a variant enlarges it, and the arrows or the mouse wheel flip through the others.
Keep and None ask why in a dialog: optional for Keep, required for None.
Pick first, then comment on or edit the slide kept, as any slide.

Keep writes the kept slide in place of the pick's entry in `presentation.yaml`, deletes the other variants' files, resolves any open comments on them as `removed`, and moves the pick to the end of `picks-resolved.yaml`, with the order the variants were shown in, so a reviewer's lean towards a position shows across picks.
None does the same keeping the first variant in place, so the deck still draws, records `chosen: none`, and marks the slide `rejected: <pick id>`.
Undo reopens the pick, its files, and its comments, as it does for a deleted slide.
Save commits both files with the deck, as it does the comments.

Only the reviewer resolves a pick, from the preview.

## Redoing a rejected slide

A slide marked `rejected` waits on a redo: the slide list and the grid show it as a card saying so, and `--check` warns of it with the reviewer's reason, saying instead that it has changed once it differs from the commit it was rejected at, beyond the mark.
In its place the slide view offers Choose again, which reopens the pick with the variants read back from the commit it was resolved at, so the reviewer can keep one after all.
Read `why` in `picks-resolved.yaml`, where a letter names the variant `shown` lists under it, then redo the slide, or offer a new pick in its place, and delete `rejected` from the slide.

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
| `resolved` | Only in `picks-resolved.yaml`: `by`, `date`, `rev`, `shown`, each letter the reviewer saw with its variant, `chosen`, the slide kept or `none`, and `why`, whose letters name variants by `shown`. |

```yaml
- id: p4kx
  by: Claude
  date: 2026-11-02 10:14
  rev: 3f9a1c2
  variants: [...]
  resolved:
    by: Daniel
    date: 2026-11-02 18:40
    rev: 8b21d07
    shown: { A: growth-waterfall, B: growth, C: growth-stacked }
    chosen: growth-stacked
    why: C, since one chart says it
```

## Checks

- `--check` warns of every open pick, every slide marked `rejected`, saying when it has changed since the commit it was rejected at, and `picks.yaml` or `picks-resolved.yaml` untracked in a repository.
- It errors on a pick with fewer than two variants or more than three, and on a variant whose slide doesn't exist.
- A slide both in `slides` and in a pick, a slide in two picks, and a `pick:` naming no open pick stop the presentation loading.
- The export draws each open pick's first variant and each rejected slide, and lists them among its warnings.

## Usual mistakes

- Deleting the variants you'd drop: leave the choice to the reviewer, so the pick records it.
- Resolving a pick yourself, or editing `picks-resolved.yaml`: it is the reviewer's record.
- Reading a letter in `why` as a variant's place in `variants`: the letters follow `shown`, which is shuffled.
- Leaving `rejected` on a slide you redid: the preview keeps hiding it behind the reviewer's reason.
- Writing a recommendation into `intent`: say what the variant shows, not why it's better.
- Listing a variant in `slides` as well as in the pick: name the pick alone.
- Offering one variant: list the slide in `slides` instead.
- Offering four, or padding two real options with a third: offer the two.
