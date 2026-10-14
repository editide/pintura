# text

Text elements, shapes, and table cells take a Markdown subset.
Write it as a block scalar (`text: |`) whenever it has more than one line.

| Write | Get |
| --- | --- |
| A line | A paragraph. |
| `- item` | A bullet at level 1; two more spaces of indentation per level. |
| `1. item` | A numbered paragraph. |
| `**bold**`, `*italic*` | Bold, italic. |
| `[text](https://…)` | A link: `http`, `https`, or `mailto`. |
| `[text]{.emphasis}` | A run in a named style. |
| `[text]{size=40 font=heading color=accent2}` | A run with one-off character properties. |
| `{.lead}` alone on a line | The next paragraph in a named style. |
| `${footer}`, `${slide}`, `${section}` | A field. |
| `${acme::historical.segments.ai_revenue["2025"]}` | A cell in a Libreta book (see `pintura --docs books`). |

Spans follow Pandoc's bracketed spans.
A span can mix styles and properties, `[text]{.negative size=24}`, and properties win over styles.
Several styles on one span layer left to right, so in `[text]{.emphasis .positive}` the later one wins.
Bold and italic are only `**` and `*`, never span attributes: write `**[$28.5B]{size=40}**`.
A span attribute whose value has spaces is quoted: `[text]{color="accent1 darker 25"}`.

A `$` not followed by `{` is literal, so `$4.7B` needs no escaping; write `\${` for a literal `${`.
A line that starts like a list item but isn't one escapes its marker, as `1\. Click` or `\- or press Enter`, and a paragraph reading `{.note}` is written `\{.note}`.
A blank line is an empty paragraph, which adds space; prefer `space_before` or `space_after` in a style.

## Example

```yaml
text: |
  {.lead}
  [$4.4B]{.positive} operating income in 2025, from $0.5B in 2023
  - Revenue up from $3.9B to $11.4B
  - Adjusted EBITDA margin of 63%
    - 62% in the first half of 2026
  1. Hold Space spending flat
  2. Fund AI from Connectivity
  See the [filing](https://www.sec.gov/) for details.
```

## Usual mistakes

- Text starting with `[` or `{` on a single line: YAML reads it as a list or a map, so quote it (`text: "[$4.4B]{.positive} of income"`) or use `|`.
- `<b>`, `<br>`, or other HTML: not markup here.
- Headings (`#`): use a style, such as `{.lead}`.
- Indenting bullets with tabs or by four spaces: each level is two spaces.