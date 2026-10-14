# colors

A color is written the same way everywhere: `fill`, `stroke`, `color`, `background`, and styles.

| Write | Means |
| --- | --- |
| `accent1` | A theme color. |
| `accent1 lighter 40` | PowerPoint's "Lighter 40%". |
| `accent1 darker 25` | PowerPoint's "Darker 25%". |
| `positive` | A named color from the theme's `named_colors`. |
| `"#C0392B"` | A fixed color. |
| `none` | No fill or stroke. |

The twelve theme colors: `dark1`, `light1`, `dark2`, `light2`, `accent1` to `accent6`, `hyperlink`, `followed_hyperlink`.
Text defaults to `dark1` on `light1`, as the theme's `text` style says; a dark layout states its own text colors.

Any step from 1 to 99 works.
PowerPoint's palette shows lighter `80`, `60`, `40` and darker `25`, `50` for most colors, and finer steps for near-black and near-white, so those are the ones a person editing the export will recognize.

`opacity: 60` sits beside a color, as a key of its own, never inside it.

## Example

```yaml
fill: accent1 lighter 80
stroke: { color: accent1, width: 1 }
opacity: 60
```

## Usual mistakes

- Hex where a theme color would do: a theme color follows the theme into PowerPoint, and a hex value never changes.
- `opacity` inside a color (`accent1 60%`): write it as its own key.
- Unquoted hex: `#` starts a YAML comment.