# Picks

A design, not built yet; it moves to `docs/topics/picks.md` once it is.

An agent offers two to four variants of a slide, the reviewer sees them side by side and keeps one, and the archive records what was offered, what was kept, and why.
The record is the point: a pick is a preference pair, which deleting the losing slides by hand doesn't leave behind.

## Files

Open picks live in `picks.yaml` beside `presentation.yaml`, written by the agent offering them:

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

`presentation.yaml` names the pick where its slide goes:

```yaml
slides:
  - business
  - pick: p4kx
  - cash
```

Each variant is an ordinary slide file.
`intent` is one neutral line on what the variant is trying to show; never a recommendation or pros and cons, which anchor the reviewer and spoil the record.

## In the preview

A pick takes one place in the slide list and one slide number, drawn as its first variant with a mark saying how many there are.
Opening it shows the variants side by side, each under its intent, with Keep beneath each and None below them all, beside a box for why.
Why is optional for Keep and required for None.
Comments pin to a variant as to any slide, so "keep B, with A's legend" is a Keep on B and a comment on it.

Keep rewrites the pick's entry in `presentation.yaml` to the kept slide, deletes the other variants' files, resolves their open comments as `removed`, and moves the pick to the end of `picks-resolved.yaml`:

```yaml
- id: p4kx
  by: Claude
  date: 2026-11-02 10:14
  rev: 3f9a1c2
  variants: [...]
  resolved: { by: Daniel, date: 2026-11-02 18:40, rev: 8b21d07, chosen: growth-stacked, why: One chart says it }
```

None does the same keeping the first variant in place, so the deck still draws, and records `chosen: none`.
Undo reopens the pick, its files, and its comments, as it does for a deleted slide.
Save commits both files with the deck, as it does the comments.

## Checks and the CLI

- `--check` warns of every open pick, and of `picks.yaml` or `picks-resolved.yaml` untracked in a repository.
- It errors on a pick with fewer than two variants, a variant whose slide doesn't exist, a slide both in `slides` and in a pick, and a `pick:` naming no open pick.
- The export draws each open pick's first variant and lists the pick among its warnings.
- `--picks` lists the open picks with their variants and intents, and `--picks --resolved` the archive in the order they were resolved.
- Only the reviewer resolves a pick, from the preview.

## Not Pintura's job

Which variant the agent expects the reviewer to keep: the agent records that outside the presentation, so the preview never shows it.