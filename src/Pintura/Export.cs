using System.Globalization;

namespace Pintura;

/// <summary>The presentation as the exporter reads it: the theme and each layout defined once, and each slide with
/// only what it owns. Properties are resolved through the style ladder, but colors and fonts stay theme references.
/// Pictures are paths relative to the presentation folder.</summary>
public record ExportDeck(string Name, double[] Size, ExportTheme Theme, List<ExportLayout> Layouts,
  List<ExportSlide> Slides);

public record ExportTheme(
  string Name,
  Dictionary<string, string> Colors,
  Dictionary<string, string> Fonts,
  object Background,
  Dictionary<string, object> Text,
  Dictionary<int, ExportLevel> Levels,
  ExportItem? Title,
  List<ExportItem> Placeholders,
  Dictionary<string, ExportFormat> Formats,
  Dictionary<string, double[]> Areas);

/// <summary>A named number format, with its <c>negative_style</c> resolved to the color that style sets.</summary>
public record ExportFormat(double? Scale, int? Decimals, bool? Separator, string? Prefix, string? Suffix,
  string? Negative, ExportColor? NegativeColor, string? Date);

public record ExportLevel(string? Bullet, ExportColor? BulletColor, double? BulletSize, double? Indent, double? Hanging,
  Dictionary<string, object> Properties);

/// <summary>A null background inherits the theme's, and a null title with <see cref="TitleRemoved"/> false the
/// theme's title. <see cref="Areas"/> holds the theme's with the layout's over them.</summary>
public record ExportLayout(
  string Id,
  string Name,
  object? Background,
  bool TitleRemoved,
  ExportItem? Title,
  List<ExportItem> Placeholders,
  List<ExportItem> Elements,
  Dictionary<string, double[]> Areas);

/// <summary><see cref="Hide"/> lists the layout's elements the slide leaves out, and a null background is the
/// layout's.</summary>
public record ExportSlide(string Id, int Number, string? Section, string Layout, string? Error,
  List<ExportItem>? Items, List<string> Hide, object? Background = null);

public record ExportItem(
  string Id,
  string Kind,
  string? Role,
  Dictionary<string, object> Properties,
  List<ExportParagraph>? Paragraphs);

/// <summary><see cref="Style"/> names the paragraph's own style, and a run's <see cref="ExportRun.Styles"/> its
/// span's, so an import can name the styles it finds again.</summary>
public record ExportParagraph(
  int Level,
  string? Style,
  string? Bullet,
  ExportColor? BulletColor,
  double? BulletSize,
  double? Indent,
  double Hanging,
  Dictionary<string, object> Properties,
  List<ExportRun> Runs);

/// <summary>A table cell, or a merge from its first cell, numbered from 1, with its fill, borders, and text.</summary>
public record ExportCell(
  int Row,
  int Column,
  int Rows,
  int Columns,
  Dictionary<string, object> Properties,
  List<ExportParagraph> Paragraphs);

/// <summary>A chart series with its <see cref="Type"/>, the chart's family, <c>line</c> over columns, or a stack's
/// <c>total</c>. A series with keys of its own carries its labels and markers resolved, as the chart's
/// <c>data_labels.</c> and <c>markers.</c> keys; otherwise it takes the chart's. Points are its single points' own
/// colors and outlines, as <c>points.{category}.color</c> and <c>points.{category}.outline.</c>. Dash is a dashed
/// line's <c>dash</c> or <c>dot</c>, and Outline a column's or bar's resolved <c>outline.</c> keys.</summary>
public record ExportSeries(string Name, List<double?> Values, string Type, bool Secondary,
  Dictionary<string, object>? Labels, Dictionary<string, object>? Markers, Dictionary<string, object>? Points = null,
  string? Dash = null, Dictionary<string, object>? Outline = null);

/// <summary>A field run keeps its name; its text is empty in the theme and layouts for <c>slide</c> and
/// <c>section</c>, which only a slide can fill.</summary>
public record ExportRun(string Text, string? Field, string? Link, List<string>? Styles,
  Dictionary<string, object> Properties);

/// <summary>A theme color, with its step, or a fixed hex. Named colors resolve to what they name.</summary>
public record ExportColor(string? Theme, string? Hex, int? Lighter, int? Darker, bool? None);

/// <summary><c>heading</c> or <c>body</c> for the theme's fonts, otherwise a literal family.</summary>
public record ExportFont(string? Theme, string? Family);

public class ExportBuilder
{
  private readonly Presentation presentation;
  private readonly Theme theme;

  private ExportBuilder(Presentation presentation)
  {
    this.presentation = presentation;
    theme = presentation.Theme;
  }

  public static ExportDeck Build(Presentation presentation)
  {
    return new ExportBuilder(presentation).Build();
  }

  private ExportDeck Build()
  {
    string anyLayout = theme.Layouts.Keys.First();
    StyleSheet styles = theme.Styles;
    var exportTheme = new ExportTheme(
      theme.Name,
      theme.Colors,
      theme.Fonts,
      Value(theme.Background),
      Properties(styles.Text),
      styles.Levels.ToDictionary(pair => pair.Key, pair => Level(pair.Value)),
      theme.Title is null ? null : Placeholder(anyLayout, "title", "title", [theme.Title]),
      theme.Placeholders.Select(pair => Placeholder(anyLayout, pair.Key, "placeholder", [pair.Value])).ToList(),
      styles.Formats.ToDictionary(pair => pair.Key, pair => Format(pair.Value)),
      theme.Areas.ToDictionary(pair => pair.Key, pair => pair.Value.Rect));

    List<ExportLayout> layouts = theme.Layouts.Values.Select(Layout).ToList();
    List<ExportSlide> slides = presentation.Slides.Select(Slide).ToList();
    return new ExportDeck(presentation.Name, presentation.Size, exportTheme, layouts, slides);
  }

  private ExportLayout Layout(Layout layout)
  {
    ExportItem? title = layout.TitleRemoved || layout.Title is null
      ? null
      : Placeholder(layout.Id, "title", "title", [theme.Title, layout.Title]);
    List<ExportItem> placeholders = theme.PlaceholderNames(layout)
      .Select(name =>
      {
        (Placeholder? themeLevel, Placeholder? layoutLevel) = theme.PlaceholderLevels(layout, name);
        return Placeholder(layout.Id, name, "placeholder", [themeLevel, layoutLevel]);
      })
      .ToList();
    return new ExportLayout(
      layout.Id,
      layout.Name,
      layout.Background is null ? null : Value(layout.Background),
      layout.TitleRemoved,
      title,
      placeholders,
      layout.Elements.Select(element => Element(layout.Id, element, "layout")).ToList(),
      theme.AreasOf(layout).ToDictionary(entry => entry.Name, entry => entry.Area.Rect));
  }

  private ExportSlide Slide(Slide slide)
  {
    if (slide.Error is not null)
    {
      return new ExportSlide(slide.Id, slide.Number, slide.Section, slide.Layout, slide.Error.Message, null, []);
    }
    SlideScene scene = SceneBuilder.Build(presentation, slide);
    List<ExportItem> items = scene.Items
      .Where(item => item.Owner == "slide")
      .Select(item => Item(item, "slide"))
      .ToList();
    return new ExportSlide(slide.Id, slide.Number, slide.Section, slide.Layout, null, items, slide.Hide,
      slide.Background is null ? null : Value(slide.Background));
  }

  private ExportItem Placeholder(string layout, string name, string role, Placeholder?[] chain)
  {
    return Item(SceneBuilder.Placeholder(presentation, layout, name, role, chain), "layout");
  }

  private ExportItem Element(string layout, Element element, string owner)
  {
    return Item(SceneBuilder.Element(presentation, layout, element, owner), owner);
  }

  private ExportItem Item(SceneItem item, string owner)
  {
    List<ExportParagraph>? paragraphs = item.Paragraphs is null ? null : Paragraphs(item.Paragraphs, owner);
    Dictionary<string, object> properties = Properties(item.Properties);
    if (item.Element?.PicturePath is string picture)
    {
      string relative = Path.GetRelativePath(presentation.Root, picture).Replace('\\', '/');
      properties["picture"] = relative;
    }
    if (item.Chart is Chart chart)
    {
      properties["categories"] = chart.Categories;
      properties["series"] = chart.Series.Select(series => Series(chart, series, properties)).ToList();
      foreach (string key in properties.Keys.Where(key => key.StartsWith("series.", StringComparison.Ordinal)).ToList())
      {
        properties.Remove(key);
      }
    }
    if (item.Table is SceneTable table)
    {
      properties["widths"] = table.Widths.Select(width => Convert.ToDouble(width.Value)).ToList();
      properties["heights"] = table.Heights
        .Select(height => height is null ? (double?)null : Convert.ToDouble(height.Value))
        .ToList();
      properties["cells"] = table.Cells
        .Select(cell => new ExportCell(cell.Row, cell.Column, cell.Rows, cell.Columns, Properties(cell.Properties),
          Paragraphs(cell.Paragraphs, owner)))
        .ToList();
    }
    return new ExportItem(item.Id, item.Kind, item.Role, properties, paragraphs);
  }

  private static ExportSeries Series(Chart chart, ChartSeries series, Dictionary<string, object> properties)
  {
    if (!chart.SeriesOptions.ContainsKey(series.Name))
    {
      return new ExportSeries(series.Name, series.Values, chart.Family, false, null, null);
    }
    string prefix = $"series.{series.Name}.";
    Dictionary<string, object> Own(string group)
    {
      return properties
        .Where(pair => pair.Key.StartsWith($"{prefix}{group}.", StringComparison.Ordinal))
        .ToDictionary(pair => pair.Key[prefix.Length..], pair => pair.Value);
    }
    return new ExportSeries(series.Name, series.Values, properties.GetValueOrDefault($"{prefix}type") as string
      ?? chart.Family, properties.GetValueOrDefault($"{prefix}axis") is "secondary", Own("data_labels"),
      Own("markers"), Own("points") is { Count: > 0 } points ? points : null,
      properties.GetValueOrDefault($"{prefix}dash") is string dash and not "solid" ? dash : null,
      Own("outline") is { Count: > 0 } outline ? outline : null);
  }

  private ExportLevel Level(Level level)
  {
    return new ExportLevel(level.Bullet, level.BulletColor is null ? null : Color(level.BulletColor), level.BulletSize,
      level.Indent, level.Hanging, Properties(level.Properties));
  }

  private List<ExportParagraph> Paragraphs(List<SceneParagraph> paragraphs, string owner)
  {
    return paragraphs
      .Select(paragraph => new ExportParagraph(
        paragraph.Level,
        paragraph.Style,
        paragraph.Bullet?.Value as string,
        paragraph.Bullet is not null && paragraph.BulletColor?.Value is Color color ? Color(color) : null,
        paragraph.Bullet is null ? null : paragraph.BulletSize?.Value as double?,
        paragraph.Indent is null ? null : Convert.ToDouble(paragraph.Indent.Value),
        paragraph.Hanging is null ? 0 : Convert.ToDouble(paragraph.Hanging.Value),
        Properties(paragraph.Properties),
        paragraph.Runs.Select(run => Run(run, owner)).ToList()))
      .ToList();
  }

  private ExportRun Run(SceneRun run, string owner)
  {
    string text = owner != "slide" && run.Field is "slide" or "section" ? "" : run.Text;
    return new ExportRun(text, run.Field, run.Link, run.Styles, Properties(run.Properties));
  }

  private Dictionary<string, object> Properties(Dictionary<string, Resolved> properties)
  {
    return properties.ToDictionary(pair => pair.Key, pair => Value(pair.Value.Value, pair.Key));
  }

  private Dictionary<string, object> Properties(TextProperties properties)
  {
    var result = new Dictionary<string, object>();
    foreach (string key in TextProperties.All)
    {
      if (properties.Get(key) is object value)
      {
        result[key] = Value(value, key);
      }
    }
    return result;
  }

  private object Value(object value, string key = "")
  {
    return value switch
    {
      Color color => Color(color),
      Frame frame => frame.Rect,
      List<Color> colors => colors.Select(Color).ToList(),
      DateStep step => step.ToString(),
      string font when key == "font" || key.EndsWith(".font", StringComparison.Ordinal) => theme.Fonts.ContainsKey(font)
        ? new ExportFont(font, null)
        : new ExportFont(null, font),
      _ => value
    };
  }

  private ExportFormat Format(NumberFormat format)
  {
    Color? negative = format.NegativeStyle is null ? null : theme.Styles.Styles[format.NegativeStyle].Color;
    return new ExportFormat(format.Scale, format.Decimals, format.Separator, format.Prefix, format.Suffix,
      format.Negative, negative is null ? null : Color(negative), format.Date);
  }

  private ExportColor Color(Color color)
  {
    return color.Named is not null
      ? Color(theme.NamedColors[color.Named])
      : new ExportColor(color.Theme, color.Hex, color.Lighter, color.Darker, color.None);
  }


}

/// <summary>What PowerPoint or editide's tools can't reproduce, shared between <c>--check --export</c> and the
/// exporter so both report the same gaps.</summary>
public static class ExportGaps
{
  private static readonly HashSet<string> SupportedShapes =
  [
    "rectangle", "ellipse", "diamond", "pentagon", "hexagon", "rounded_rectangle", "parallelogram", "trapezoid",
    "triangle", "right_triangle", "chevron", "home_plate", "arrow", "brace", "bracket", "callout"
  ];

  public const string WaterfallPointLabel =
    "a waterfall point's label in a color or boldness of its own exports in its series', as editide can't yet style " +
    "one waterfall label";

  public const string SlideNumberOnSlide =
    "${slide} in a slide's own text exports as plain text, or not at all when it is the only text";

  public static IEnumerable<string> Picture(bool cover, bool crop, bool alt, bool link, bool opacity)
  {
    if (cover)
    {
      yield return "fit: cover needs a crop, which isn't exported";
    }
    if (crop)
    {
      yield return "picture crop isn't exported";
    }
    if (alt)
    {
      yield return "picture alt text isn't exported";
    }
    if (link)
    {
      yield return "picture links aren't exported";
    }
    if (opacity)
    {
      yield return "picture opacity isn't exported";
    }
  }

  public static IEnumerable<string> Shape(string? shape, string? direction)
  {
    if (shape == "right_triangle" && direction != "up")
    {
      yield return $"a right triangle facing {direction} exports facing right, as PowerPoint has no such shape and a " +
        "turn moves its right angle";
    }
    else if (shape is "brace" or "bracket")
    {
      yield return $"a {shape} exports with PowerPoint's curl rather than a quarter of its thickness";
    }
    else if (shape == "callout")
    {
      yield return "a callout's tail isn't exported, so it points elsewhere";
    }
    else if (shape is null || !SupportedShapes.Contains(shape))
    {
      yield return $"a {shape} shape isn't exported";
    }
  }

  public static IEnumerable<string> OpacityWithoutFill(bool hasFill, bool hasOpacity)
  {
    if (!hasFill && hasOpacity)
    {
      yield return "opacity on an element with no fill isn't exported";
    }
  }

  public static IEnumerable<string> TextColumns(double columns)
  {
    if (columns > 1)
    {
      yield return "text columns aren't exported";
    }
  }

  public static IEnumerable<string> PlotInsets(bool hasInsets)
  {
    if (hasInsets)
    {
      yield return "a chart's plot insets aren't exported, so PowerPoint places its plot itself";
    }
  }

  public static IEnumerable<string> ReversedAxis(string axisName, bool reversed)
  {
    if (reversed)
    {
      yield return $"a reversed {(axisName == "value_axis" ? "value" : "secondary")} axis isn't exported, " +
        "so it runs bottom up in PowerPoint";
    }
  }

  public static IEnumerable<string> Format(double? scale, string? suffix, bool hasNegativeColor, string? date)
  {
    if (!string.IsNullOrEmpty(date))
    {
      yield break;
    }
    double remaining = scale ?? 1;
    if (suffix == "%" && Math.Abs(remaining - 100) < 1e-9)
    {
      remaining = 1;
    }
    int commas = 0;
    while (remaining < 1 - 1e-9 && commas < 3)
    {
      remaining *= 1000;
      commas++;
    }
    if (Math.Abs(remaining - 1) > 1e-9)
    {
      yield return $"a chart's format scale of {scale?.ToString(CultureInfo.InvariantCulture)} " +
        "isn't exported, as PowerPoint scales only by thousands, or by a hundred as a percent";
    }
    if (hasNegativeColor)
    {
      yield return "a chart's negative_style isn't exported, as PowerPoint colors negatives only from a fixed palette";
    }
  }

  public static string UnknownFormat(string name)
  {
    return $"a chart's format {name} isn't in the theme, so its numbers export unformatted";
  }

  public static string HiddenLayoutElement(string slideId, string elementId)
  {
    return $"slide {slideId} hides {elementId}, which PowerPoint shows, as a slide can't leave out one of its " +
      "layout's shapes";
  }

  /// <summary>Every static export gap on a resolved scene item.</summary>
  public static IEnumerable<string> ForItem(SceneItem item, Theme theme)
  {
    Element? element = item.Element;
    bool hasOpacity = item.Properties.ContainsKey("opacity");
    bool hasFill = item.HasFill;
    switch (item.Kind)
    {
      case "picture":
        bool cover = item.Properties.TryGetValue("fit", out Resolved? fit) && (string)fit.Value == "cover";
        foreach (string gap in Picture(cover, element?.Crop is not null, element?.Alt is not null,
          element?.Link is not null, element?.Opacity is not null || hasOpacity))
        {
          yield return gap;
        }
        break;
      case "shape":
        string? shape = item.Properties.TryGetValue("shape", out Resolved? shapeProp)
          ? shapeProp.Value as string : element?.Shape;
        string? direction = item.Properties.TryGetValue("direction", out Resolved? dirProp)
          ? dirProp.Value as string : element?.Direction;
        foreach (string gap in Shape(shape, direction))
        {
          yield return gap;
        }
        foreach (string gap in OpacityWithoutFill(hasFill, hasOpacity))
        {
          yield return gap;
        }
        break;
      case "text":
        foreach (string gap in OpacityWithoutFill(hasFill, hasOpacity))
        {
          yield return gap;
        }
        break;
      case "chart":
        foreach (string gap in PlotInsets(item.Properties.ContainsKey("plot.insets")))
        {
          yield return gap;
        }
        foreach (string axis in new[] { "value_axis", "secondary_axis" })
        {
          bool reversed = item.Properties.TryGetValue($"{axis}.reverse", out Resolved? rev) && rev.Value is true;
          foreach (string gap in ReversedAxis(axis, reversed))
          {
            yield return gap;
          }
        }
        foreach (string gap in ChartFormats(item, theme))
        {
          yield return gap;
        }
        if (item.Chart?.Type == "waterfall" && WaterfallPointLabelStyled(item))
        {
          yield return WaterfallPointLabel;
        }
        break;
    }
    if (item.Properties.TryGetValue("columns", out Resolved? columns) && Convert.ToDouble(columns.Value) is double count)
    {
      foreach (string gap in TextColumns(count))
      {
        yield return gap;
      }
    }
    // A layout's page number becomes PowerPoint's slide-number placeholder; a slide's own field has no such home.
    bool slideNumber = (item.Paragraphs ?? []).Any(paragraph => paragraph.Runs.Any(run => run.Field == "slide"));
    if (slideNumber && item.Owner == "slide" && item.Role is null)
    {
      yield return SlideNumberOnSlide;
    }
  }

  /// <summary>Slide-level static export gaps: hiding a layout's non-page-number shape.</summary>
  public static IEnumerable<string> ForSlide(Slide slide, Layout layout)
  {
    foreach (string id in slide.Hide)
    {
      Element? target = layout.Elements.FirstOrDefault(element => element.Id == id);
      if (target is not null && !IsPageNumber(target))
      {
        yield return HiddenLayoutElement(slide.Id, id);
      }
    }
  }

  private static bool IsPageNumber(Element element)
  {
    return element.Kind == "text" && element.Text is [Paragraph paragraph] &&
      paragraph.Runs is [Run { Field: "slide" }];
  }

  private static IEnumerable<string> ChartFormats(SceneItem item, Theme theme)
  {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string key, Resolved resolved) in item.Properties)
    {
      if ((key == "value_axis.format" || key == "secondary_axis.format" || key == "data_labels.format" ||
        key.EndsWith(".data_labels.format", StringComparison.Ordinal)) &&
        resolved.Value is string { Length: > 0 } name && seen.Add(name))
      {
        if (!theme.Styles.Formats.TryGetValue(name, out NumberFormat? format))
        {
          yield return UnknownFormat(name);
          continue;
        }
        bool hasNegativeColor = format.NegativeStyle is not null &&
          theme.Styles.Styles.TryGetValue(format.NegativeStyle, out TextProperties? style) && style.Color is not null;
        foreach (string gap in Format(format.Scale, format.Suffix, hasNegativeColor, format.Date))
        {
          yield return gap;
        }
      }
    }
  }

  private static bool WaterfallPointLabelStyled(SceneItem item)
  {
    foreach ((string key, Resolved resolved) in item.Properties)
    {
      if (key.Contains(".points.", StringComparison.Ordinal) &&
        (key.EndsWith(".data_labels.color", StringComparison.Ordinal) ||
        key.EndsWith(".data_labels.bold", StringComparison.Ordinal) && resolved.Value is true))
      {
        return true;
      }
    }
    return false;
  }


}