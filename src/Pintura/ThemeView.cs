using System.Globalization;

namespace Pintura;

/// <summary>The theme as the page's theme view shows it: the palette, fonts, styles, layouts, table and chart styles,
/// and formats, each with the file and line that sets it and how many elements on which slides use it.</summary>
public record ThemeViewPayload
{
  public string Name { get; init; } = "";
  /// <summary>The theme's <c>theme.yaml</c>, as origins name files.</summary>
  public string File { get; init; } = "";
  public string Background { get; init; } = "";
  public List<ThemeColorPayload> Colors { get; init; } = new List<ThemeColorPayload>();
  public List<NamedColorPayload> Named { get; init; } = new List<NamedColorPayload>();
  /// <summary>Hex values written outside the palette, which a theme changed in PowerPoint won't recolor.</summary>
  public List<StrayColorPayload> Stray { get; init; } = new List<StrayColorPayload>();
  public List<FontPayload> Fonts { get; init; } = new List<FontPayload>();
  /// <summary>The base text every style is drawn over.</summary>
  public StylePayload Text { get; init; } = new StylePayload();
  public List<StylePayload> Styles { get; init; } = new List<StylePayload>();
  public List<LayoutPayload> Layouts { get; init; } = new List<LayoutPayload>();
  public List<TableStylePayload> Tables { get; init; } = new List<TableStylePayload>();
  public ChartStylesPayload Charts { get; init; } = new ChartStylesPayload();
  public List<FormatSamplePayload> Formats { get; init; } = new List<FormatSamplePayload>();
}

public record OriginPayload(string File, int Line);

/// <summary>How many elements use a value, and on which slides, in deck order.</summary>
public record UsesPayload(int Elements, List<string> Slides);

public record ShadePayload(string Label, string Hex);

public record ThemeColorPayload(string Name, string Hex, OriginPayload? Origin, List<ShadePayload> Shades,
  UsesPayload Uses);

public record NamedColorPayload(string Name, string Value, string Hex, OriginPayload? Origin, UsesPayload Uses);

public record StrayColorPayload(string Hex, UsesPayload Uses);

/// <summary>A theme font and the styles drawn in it.</summary>
public record FontPayload(string Role, string Family, OriginPayload? Origin, List<string> Styles);

/// <summary>A style's effective text over the theme's base text; <see cref="Own"/> lists the keys it sets itself.
/// </summary>
public record StylePayload
{
  public string Name { get; init; } = "";
  public string Font { get; init; } = "";
  public string Family { get; init; } = "";
  public double Size { get; init; }
  public string Color { get; init; } = "";
  public string Hex { get; init; } = "";
  public bool Bold { get; init; }
  public bool Italic { get; init; }
  public bool Underline { get; init; }
  public List<string> Own { get; init; } = new List<string>();
  /// <summary>The values of the keys in <see cref="Own"/>, as written.</summary>
  public Dictionary<string, string> Sets { get; init; } = new Dictionary<string, string>();
  public OriginPayload? Origin { get; init; }
  public UsesPayload Uses { get; init; } = new UsesPayload(0, new List<string>());
  /// <summary>Text the deck draws in the style, for a specimen that shows what the style is for.</summary>
  public StyleSamplePayload? Sample { get; init; }
}

public record StyleSamplePayload(string Text, string Slide);

/// <summary>A layout drawn on its own, its title and placeholders holding their names.</summary>
/// <summary>A layout drawn with specimen words: <paramref name="Slide"/> as a slide on it looks, and
/// <paramref name="Stage"/> with the theme's and layout's files for the mouse to write.</summary>
public record LayoutPayload(string Id, string Name, string File, SlidePayload Slide, SlidePayload Stage,
  List<string> Slides);

public record TableStylePayload(string Name, OriginPayload? Origin, List<TableRowSamplePayload> Rows, string? Banded,
  UsesPayload Uses);

/// <summary>A row style, or <c>body</c> for rows without one.</summary>
public record TableRowSamplePayload(string Name, string? Fill, RunPayload Text, StrokePayload? BorderTop,
  StrokePayload? BorderBottom);

public record ChartStylesPayload
{
  public List<NamedColorPayload> SeriesColors { get; init; } = new List<NamedColorPayload>();
  public RunPayload Text { get; init; } = new RunPayload();
  public OriginPayload? Origin { get; init; }
  public List<ChartStylePayload> Styles { get; init; } = new List<ChartStylePayload>();
}

public record ChartStylePayload(string Name, OriginPayload? Origin, UsesPayload Uses);

/// <summary>A format spelling a sample number, or a sample date for a date format.</summary>
public record FormatSamplePayload(string Name, string Positive, string? Negative, string? NegativeHex,
  OriginPayload? Origin, UsesPayload Uses);

public static class ThemeView
{
  private static readonly (string Label, int? Lighter, int? Darker)[] Shades =
    [("lighter 80", 80, null), ("lighter 60", 60, null), ("lighter 40", 40, null), ("darker 25", null, 25),
      ("darker 50", null, 50)];

  public static ThemeViewPayload Build(Presentation presentation, PictureUrl pictureUrl)
  {
    Theme theme = presentation.Theme;
    StyleSheet styles = theme.Styles;
    Usage usage = Usage.Count(presentation);
    List<StylePayload> stylePayloads = styles.Styles
      .Select(pair => Style(pair.Key, pair.Value, theme, usage.Of($"style:{pair.Key}")) with
      {
        Sample = usage.Sample(pair.Key)
      })
      .ToList();
    return new ThemeViewPayload
    {
      Name = theme.Name,
      File = $"{theme.Display}/theme.yaml",
      Background = ColorMath.Resolve(theme.Background, theme),
      Colors = Color.ThemeColors.Select(name => new ThemeColorPayload(name, theme.Colors[name],
          Origin(theme.Origins.GetValueOrDefault($"colors.{name}")),
          Shades.Select(shade => new ShadePayload(shade.Label,
            ColorMath.Resolve(new Color { Theme = name, Lighter = shade.Lighter, Darker = shade.Darker }, theme)))
            .ToList(),
          usage.Of($"color:{name}")))
        .ToList(),
      Named = theme.NamedColors.Select(pair => new NamedColorPayload(pair.Key, pair.Value.ToString(),
          ColorMath.Resolve(pair.Value, theme), Origin(theme.Origins.GetValueOrDefault($"named_colors.{pair.Key}")),
          usage.Of($"color:{pair.Key}")))
        .ToList(),
      Stray = usage.Keys("hex:").Select(hex => new StrayColorPayload(hex, usage.Of($"hex:{hex}"))).ToList(),
      Fonts = theme.Fonts.Select(pair => new FontPayload(pair.Key, pair.Value,
          Origin(theme.Origins.GetValueOrDefault($"fonts.{pair.Key}")),
          stylePayloads.Where(style => style.Font == pair.Key).Select(style => style.Name).ToList()))
        .ToList(),
      Text = Style("text", styles.Text, theme, new UsesPayload(0, new List<string>())) with
      {
        Origin = Origin(styles.Text.Origins.Values.MinBy(origin => origin.Line))
      },
      Styles = stylePayloads,
      Layouts = theme.Layouts.Values.Select(layout => Layout(presentation, layout, pictureUrl)).ToList(),
      Tables = styles.Tables.Select(pair => Table(pair.Key, pair.Value, theme, usage.Of($"table:{pair.Key}")))
        .ToList(),
      Charts = Charts(theme, usage),
      Formats = styles.Formats.Select(pair => Format(pair.Key, pair.Value, theme, usage.Of($"format:{pair.Key}")))
        .ToList()
    };
  }

  private static OriginPayload? Origin(Origin? origin)
  {
    return origin is null ? null : new OriginPayload(origin.File, origin.Line);
  }

  private static StylePayload Style(string name, TextProperties style, Theme theme, UsesPayload uses)
  {
    TextProperties text = theme.Styles.Text;
    object? Get(string key)
    {
      return style.Get(key) ?? text.Get(key);
    }
    var color = (Color)Get("color")!;
    string font = (string)Get("font")!;
    return new StylePayload
    {
      Name = name,
      Font = font,
      Family = PayloadBuilder.Family(font, theme),
      Size = Convert.ToDouble(Get("size"), CultureInfo.InvariantCulture),
      Color = color.ToString(),
      Hex = ColorMath.Resolve(color, theme),
      Bold = Get("bold") as bool? ?? false,
      Italic = Get("italic") as bool? ?? false,
      Underline = Get("underline") as bool? ?? false,
      Own = style.Origins.Keys.ToList(),
      Sets = style.Origins.Keys.ToDictionary(key => key, key => Written(style.Get(key))),
      Origin = Origin(theme.Styles.Origins.GetValueOrDefault($"styles.{name}")),
      Uses = uses
    };
  }

  private static string Written(object? value)
  {
    return value switch
    {
      null => "",
      bool flag => flag ? "true" : "false",
      IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
      System.Collections.IEnumerable list and not string => string.Join(", ",
        list.Cast<object?>().Select(Written)),
      _ => value.ToString() ?? ""
    };
  }

  private static LayoutPayload Layout(Presentation presentation, Layout layout, PictureUrl pictureUrl)
  {
    Slide specimen = Specimen(presentation.Theme, layout);
    return new LayoutPayload(layout.Id, layout.Name, $"{presentation.Theme.Display}/layouts/{layout.Id}.yaml",
      PayloadBuilder.Slide(presentation, specimen, pictureUrl),
      PayloadBuilder.Slide(presentation, specimen, pictureUrl, layout),
      presentation.Slides.Where(slide => slide.Layout == layout.Id).Select(slide => slide.Id).ToList());
  }

  /// <summary>A slide on the layout naming each of its placeholders in its words.</summary>
  public static Slide Specimen(Theme theme, Layout layout)
  {
    IEnumerable<string> placeholders = theme.Placeholders.Keys.Concat(layout.Placeholders.Keys).Distinct();
    return new Slide
    {
      Id = layout.Id,
      Layout = layout.Id,
      Section = "Section",
      Title = layout.TitleRemoved ? null : Words("Title"),
      Placeholders = placeholders.ToDictionary(name => name, Words)
    };
  }

  private static Placeholder Words(string name)
  {
    string words = name.Replace('_', ' ');
    return new Placeholder { Text = [new Run { Text = char.ToUpperInvariant(words[0]) + words[1..] }] };
  }

  private static TableStylePayload Table(string name, TableStyle style, Theme theme, UsesPayload uses)
  {
    TableRowSamplePayload Row(string row, TableFormatting? formatting)
    {
      var layers = new List<TextProperties> { theme.Styles.Text };
      if (formatting?.Style is string textStyle)
      {
        layers.Add(theme.Styles.Styles[textStyle]);
      }
      if (formatting is not null)
      {
        layers.Add(formatting.Properties);
      }
      object? Get(string key)
      {
        return layers.Select(layer => layer.Get(key)).LastOrDefault(value => value is not null);
      }
      string font = (string)Get("font")!;
      var text = new RunPayload
      {
        Font = PayloadBuilder.Family(font, theme),
        Size = Convert.ToDouble(Get("size"), CultureInfo.InvariantCulture),
        Color = PayloadBuilder.Hex(Get("color")!, theme),
        Bold = Get("bold") as bool? == true ? true : null,
        Italic = Get("italic") as bool? == true ? true : null
      };
      return new TableRowSamplePayload(row, formatting?.Fill is Color fill ? PayloadBuilder.Hex(fill, theme) : null,
        text, Stroke(formatting?.BorderTop, theme), Stroke(formatting?.BorderBottom, theme));
    }

    var rows = new List<TableRowSamplePayload>();
    if (style.Rows.TryGetValue("header", out TableFormatting? header))
    {
      rows.Add(Row("header", header));
    }
    rows.Add(Row("body", null));
    rows.AddRange(style.Rows.Where(pair => pair.Key != "header").Select(pair => Row(pair.Key, pair.Value)));
    return new TableStylePayload(name, Origin(theme.Styles.Origins.GetValueOrDefault($"tables.{name}")), rows,
      style.Banded is Color banded ? PayloadBuilder.Hex(banded, theme) : null, uses);
  }

  private static StrokePayload? Stroke(Stroke? stroke, Theme theme)
  {
    if (stroke is null || stroke.None == true || stroke.Color is null)
    {
      return null;
    }
    return new StrokePayload(ColorMath.Resolve(stroke.Color, theme), stroke.Width ?? 1, stroke.Dash ?? "solid");
  }

  private static ChartStylesPayload Charts(Theme theme, Usage usage)
  {
    ChartProperties chart = theme.Styles.Chart;
    return new ChartStylesPayload
    {
      SeriesColors = (chart.SeriesColors ?? new List<Color>()).Select((color, index) => new NamedColorPayload(
          $"series {index + 1}", color.ToString(), ColorMath.Resolve(color, theme), null, usage.Of($"series:{index}")))
        .ToList(),
      Text = new RunPayload
      {
        Font = chart.Text.Font is string font ? PayloadBuilder.Family(font, theme) : null,
        Size = chart.Text.Size,
        Color = chart.Text.Color is Color color ? ColorMath.Resolve(color, theme) : null
      },
      Origin = Origin(chart.Origins.GetValueOrDefault("series_colors")),
      Styles = theme.Styles.Charts.Keys.Select(name => new ChartStylePayload(name,
          Origin(theme.Styles.Origins.GetValueOrDefault($"charts.{name}")), usage.Of($"chart:{name}")))
        .ToList()
    };
  }

  private static FormatSamplePayload Format(string name, NumberFormat format, Theme theme, UsesPayload uses)
  {
    OriginPayload? origin = Origin(theme.Styles.Origins.GetValueOrDefault($"formats.{name}"));
    if (format.Date is not null)
    {
      return new FormatSamplePayload(name, format.SpellDate(new DateOnly(2026, 3, 31)), null, null, origin, uses);
    }
    // The same figure through every format, so formats compare side by side; a percentage reads as a share.
    double scale = format.Scale ?? 1;
    double sample = (format.Suffix == "%" ? 12.3456 : 1234.5678) / scale;
    Color? negative = format.NegativeStyle is null ? null : theme.Styles.Styles[format.NegativeStyle].Color;
    return new FormatSamplePayload(name, format.Spell(sample), format.Spell(-sample),
      negative is null ? null : ColorMath.Resolve(negative, theme), origin, uses);
  }

  /// <summary>Counts what every slide's scene uses: colors, styles, table and chart styles, and formats, each by
  /// the elements that use it and their slides.</summary>
  private class Usage
  {
    private readonly Dictionary<string, (HashSet<string> Elements, List<string> Slides)> uses =
      new Dictionary<string, (HashSet<string>, List<string>)>(StringComparer.Ordinal);
    private const int SampleLength = 60;
    private readonly Dictionary<string, StyleSamplePayload> samples =
      new Dictionary<string, StyleSamplePayload>(StringComparer.Ordinal);
    private readonly Presentation presentation;
    private readonly Theme theme;
    private readonly string stylesFile;

    private Usage(Presentation presentation)
    {
      this.presentation = presentation;
      theme = presentation.Theme;
      stylesFile = $"{theme.Display}/styles.yaml";
    }

    public static Usage Count(Presentation presentation)
    {
      var usage = new Usage(presentation);
      foreach (Slide slide in presentation.Slides.Where(slide => slide.Error is null))
      {
        SlideScene scene;
        try
        {
          scene = SceneBuilder.Build(presentation, slide);
        }
        catch (LoadException)
        {
          continue;
        }
        usage.Value(scene.Background.Value, slide.Id, $"{slide.Id}/background");
        foreach (SceneItem item in scene.Items)
        {
          usage.Item(slide.Id, item);
        }
      }
      return usage;
    }

    public UsesPayload Of(string key)
    {
      return uses.TryGetValue(key, out (HashSet<string> Elements, List<string> Slides) found)
        ? new UsesPayload(found.Elements.Count, found.Slides)
        : new UsesPayload(0, new List<string>());
    }

    public StyleSamplePayload? Sample(string style)
    {
      return samples.GetValueOrDefault(style);
    }

    public IEnumerable<string> Keys(string prefix)
    {
      return uses.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
        .Select(key => key[prefix.Length..])
        .Order(StringComparer.Ordinal);
    }

    private void Add(string key, string slide, string element)
    {
      if (!uses.TryGetValue(key, out (HashSet<string> Elements, List<string> Slides) found))
      {
        found = (new HashSet<string>(StringComparer.Ordinal), new List<string>());
        uses[key] = found;
      }
      found.Elements.Add(element);
      if (!found.Slides.Contains(slide))
      {
        found.Slides.Add(slide);
      }
    }

    // A specimen reads best as a short line, so the longest text that fits one wins, or else the shortest.
    private void Offer(string style, string text, string slide)
    {
      text = text.Trim();
      if (text.Length == 0)
      {
        return;
      }
      if (samples.TryGetValue(style, out StyleSamplePayload? best))
      {
        bool fits = text.Length <= SampleLength;
        bool better = fits != best.Text.Length <= SampleLength ? fits
          : fits ? text.Length > best.Text.Length : text.Length < best.Text.Length;
        if (!better)
        {
          return;
        }
      }
      samples[style] = new StyleSamplePayload(text, slide);
    }

    private static string Text(SceneParagraph paragraph)
    {
      return string.Concat(paragraph.Runs.Select(run => run.Text));
    }

    private void Item(string slide, SceneItem item)
    {
      string element = $"{slide}/{item.Owner}/{item.Id}";
      if (item.Properties.TryGetValue("style", out Resolved? style) && style.Value is string name)
      {
        string kind = item.Kind switch { "table" => "table", "chart" => "chart", _ => "style" };
        Add($"{kind}:{name}", slide, element);
        if (kind == "style")
        {
          foreach (SceneParagraph paragraph in (item.Paragraphs ?? []).Where(paragraph => paragraph.Style is null))
          {
            Offer(name, Text(paragraph), slide);
          }
        }
      }
      if (item.Element?.Table is string id && presentation.Tables[id].Style is string table)
      {
        Add($"table:{table}", slide, element);
      }
      if (item.Chart?.Style is string chart)
      {
        Add($"chart:{chart}", slide, element);
      }
      // Only the colors a chart's series reach count, not the whole cycle.
      int series = item.Chart is null ? 0 : item.Chart.Family == "pie" ? item.Chart.Categories.Count
        : item.Chart.Series.Count;
      foreach ((string key, Resolved resolved) in item.Properties)
      {
        if (key == "series_colors" && resolved.Value is List<Color> colors)
        {
          for (int index = 0; index < Math.Min(series, colors.Count); index++)
          {
            Add($"series:{index}", slide, element);
            Value(colors[index], slide, element);
          }
          Origin(resolved.Origin, slide, element);
          continue;
        }
        Property(key, resolved, slide, element);
      }
      foreach (SceneParagraph paragraph in item.Paragraphs ?? [])
      {
        Paragraph(paragraph, slide, element);
      }
      foreach (SceneCell cell in item.Table?.Cells ?? [])
      {
        foreach ((string key, Resolved resolved) in cell.Properties)
        {
          Property(key, resolved, slide, element);
        }
        foreach (SceneParagraph paragraph in cell.Paragraphs)
        {
          Paragraph(paragraph, slide, element);
        }
      }
    }

    private void Paragraph(SceneParagraph paragraph, string slide, string element)
    {
      if (paragraph.Style is string style)
      {
        Add($"style:{style}", slide, element);
        Offer(style, Text(paragraph), slide);
      }
      if (paragraph.Bullet is not null && paragraph.BulletColor is Resolved bullet)
      {
        Property("bullet_color", bullet, slide, element);
      }
      foreach ((string key, Resolved resolved) in paragraph.Properties)
      {
        Property(key, resolved, slide, element);
      }
      foreach (SceneRun run in paragraph.Runs)
      {
        foreach (string runStyle in run.Styles ?? [])
        {
          Add($"style:{runStyle}", slide, element);
          Offer(runStyle, run.Text, slide);
        }
        foreach ((string key, Resolved resolved) in run.Properties)
        {
          Property(key, resolved, slide, element);
        }
      }
    }

    private void Property(string key, Resolved resolved, string slide, string element)
    {
      if ((key == "format" || key.EndsWith(".format", StringComparison.Ordinal)) && resolved.Value is string format &&
        theme.Styles.Formats.ContainsKey(format))
      {
        Add($"format:{format}", slide, element);
      }
      Value(resolved.Value, slide, element);
      Origin(resolved.Origin, slide, element);
    }

    // A value the styles file sets names the style, table style, or chart style it was set under.
    private void Origin(Origin? origin, string slide, string element)
    {
      if (origin is null || origin.File != stylesFile)
      {
        return;
      }
      string[] path = origin.Path.Split('.');
      if (path.Length < 2)
      {
        return;
      }
      string? kind = path[0] switch { "styles" => "style", "tables" => "table", "charts" => "chart", _ => null };
      if (kind is not null)
      {
        Add($"{kind}:{path[1]}", slide, element);
      }
    }

    private void Value(object value, string slide, string element)
    {
      if (value is not Color { IsNone: false } color)
      {
        return;
      }
      if (color.Hex is not null)
      {
        Add($"hex:{color.Hex}", slide, element);
        return;
      }
      for (Color? current = color; current is not null;)
      {
        string name = current.Theme ?? current.Named!;
        Add($"color:{name}", slide, element);
        current = current.Named is null ? null : theme.NamedColors[name];
      }
    }


  }


}