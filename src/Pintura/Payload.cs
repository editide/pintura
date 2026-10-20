using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pintura;

public static class PayloadJson
{
  public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions(Options) { WriteIndented = true };
}

public record DeckPayload
{
  public string Name { get; init; } = "";
  public double[] Size { get; init; } = [960, 540];
  public long Version { get; init; }
  public string? Error { get; init; }
  public List<SectionPayload> Sections { get; init; } = new List<SectionPayload>();
  public List<SlidePayload> Slides { get; init; } = new List<SlidePayload>();
  public List<Comment> Comments { get; init; } = new List<Comment>();
  public List<FontFacePayload> Fonts { get; init; } = new List<FontFacePayload>();
}

/// <summary>A font file the theme ships, as the page registers it: a CSS family, weight, and style at a URL.
/// </summary>
public record FontFacePayload(string Family, int Weight, string Style, string Url);

public record SectionPayload(string? Name, List<string> Slides);

public record SlidePayload
{
  public string Id { get; init; } = "";
  public int Number { get; init; }
  public string? Title { get; init; }
  public string? Layout { get; init; }
  public string? Section { get; init; }
  public string? Background { get; init; }
  public string? Error { get; init; }
  public List<ItemPayload>? Items { get; init; }
  public List<GuidePayload>? Guides { get; init; }
}

/// <summary>An area, or where the layout puts the title or a placeholder, drawn as an overlay. Used is whether the
/// slide fills it: an element framed by the area, or words for the placeholder.</summary>
public record GuidePayload(string Kind, string Name, string Owner, double[] Rect, bool Used);

public record ItemPayload
{
  public string Id { get; init; } = "";
  public string Owner { get; init; } = "";
  public string Kind { get; init; } = "";
  public string? Role { get; init; }
  public double[]? Frame { get; init; }
  public double? Rotate { get; init; }
  public string? Flip { get; init; }
  public double? Opacity { get; init; }
  public string? Fill { get; init; }
  public StrokePayload? Stroke { get; init; }
  public string? Shape { get; init; }
  public string? Direction { get; init; }
  public Dictionary<string, double>? Adjust { get; init; }
  public double[]? Tail { get; init; }
  public double[]? From { get; init; }
  public double[]? To { get; init; }
  public string? Start { get; init; }
  public string? End { get; init; }
  public string? Picture { get; init; }
  public string? Fit { get; init; }
  public double[]? Crop { get; init; }
  public string? Alt { get; init; }
  public TextPayload? Text { get; init; }
  public ChartPayload? Chart { get; init; }
  public TablePayload? Table { get; init; }
  public EditPayload Edit { get; init; } = new EditPayload();
}

/// <summary>What the mouse may write for an item: each value's key path in the slide's file and its source as drawn,
/// null where the slide leaves the key out, or why it can't, naming the file to edit instead.</summary>
public record EditPayload
{
  public string? Locked { get; init; }
  public SourcePayload? Frame { get; init; }
  public SourcePayload? From { get; init; }
  public SourcePayload? To { get; init; }
  public SourcePayload? Text { get; init; }
  public string? TextLocked { get; init; }
  public SourcePayload? Delete { get; init; }
}

public record SourcePayload(string Path, string? Source);

/// <summary>Column widths and minimum row heights in points, and the cells a merge leaves showing, row by row.
/// </summary>
public record TablePayload(List<double> Widths, List<double?> Heights, List<CellPayload> Cells);

public record CellPayload
{
  public int Row { get; init; }
  public int Column { get; init; }
  public int Rows { get; init; } = 1;
  public int Columns { get; init; } = 1;
  public string? Fill { get; init; }
  public StrokePayload? BorderTop { get; init; }
  public StrokePayload? BorderBottom { get; init; }
  public StrokePayload? BorderLeft { get; init; }
  public StrokePayload? BorderRight { get; init; }
  public TextPayload Text { get; init; } = new TextPayload();
  public TracePayload? Trace { get; init; }
}

/// <summary>A chart with its data and every property resolved: colors as hex, the font as a family, and formats as
/// their properties. Value axes carry the bounds <see cref="AxisScale"/> resolves.</summary>
public record ChartPayload
{
  public string Type { get; init; } = "";
  /// <summary><c>column</c>, <c>bar</c>, <c>line</c>, or <c>pie</c>.</summary>
  public string Family { get; init; } = "";
  public string Grouping { get; init; } = "";
  public string? Title { get; init; }
  public string? Legend { get; init; }
  public List<string> Categories { get; init; } = new List<string>();
  public List<SeriesPayload> Series { get; init; } = new List<SeriesPayload>();
  /// <summary>Series colors, cycled by series, or by category on a pie; null is no fill.</summary>
  public List<string?> Colors { get; init; } = new List<string?>();
  public ValueAxisPayload? ValueAxis { get; init; }
  /// <summary>Set when a line is on the secondary axis.</summary>
  public ValueAxisPayload? SecondaryAxis { get; init; }
  public bool? CategoryAxis { get; init; }
  public bool? CategoryLabels { get; init; }
  /// <summary>Set when the categories are dates on a time line.</summary>
  public DateAxisPayload? Dates { get; init; }
  public double? GapWidth { get; init; }
  public double? Overlap { get; init; }
  public ChartTextPayload Text { get; init; } = new ChartTextPayload("", 0, "");
  public string GridColor { get; init; } = "";
  public string AxisColor { get; init; } = "";
  /// <summary>The line between slices, in the theme's light1 as PowerPoint's default chart style draws it.</summary>
  public string EdgeColor { get; init; } = "";
}

/// <summary><see cref="Type"/> is the chart's family, <c>line</c> for a line over columns, or <c>total</c> for a
/// stack's total, drawn only as its labels; null labels are none. <see cref="Traces"/> is the book cell behind each
/// value, for a chart read from a book.</summary>
/// <summary>Colors are a point's own color, by category, where the series colors any; on a line they outline its
/// markers. <see cref="PointMarkers"/> are a line's points' own markers, over the series', by category, and
/// <see cref="PointLabels"/> the points' own labels, null where a point takes the series'.</summary>
public record SeriesPayload(string Name, List<double?> Values, string Type, bool Secondary, LabelsPayload? Labels,
  MarkersPayload? Markers, List<TracePayload>? Traces = null, List<string?>? Colors = null,
  List<MarkersPayload?>? PointMarkers = null, List<LabelsPayload?>? PointLabels = null);

/// <summary>A null fill is the line's color.</summary>
public record MarkersPayload(string Shape, double Size, string? Fill);

/// <summary>A null color is the chart's text color. <see cref="Hidden"/> is a point's label shown none on a series
/// that shows its others.</summary>
public record LabelsPayload(string Show, string Position, FormatPayload? Format, string? Color, bool Bold)
{
  public static readonly LabelsPayload Hidden = new LabelsPayload("none", "", null, null, false);
}

public record ValueAxisPayload(bool Visible, double Min, double Max, double Step, FormatPayload? Format,
  bool Gridlines, bool Reverse = false);

/// <summary>A date axis's pattern, and its step between labels; a null step is for the page to choose.</summary>
public record DateAxisPayload(string Pattern, int? StepCount, string? StepUnit);

public record ChartTextPayload(string Font, double Size, string Color);

/// <summary>A named format with Libreta's defaults for what it leaves out; null where a number shows as written.
/// </summary>
public record FormatPayload(double Scale, int Decimals, bool Separator, string Prefix, string Suffix, string Negative,
  string? NegativeColor);

public record StrokePayload(string Color, double Width, string Dash);

public record TextPayload
{
  public double[] Insets { get; init; } = [0, 0, 0, 0];
  public string Valign { get; init; } = "top";
  public string Fit { get; init; } = "none";
  public bool Wrap { get; init; } = true;
  public int Columns { get; init; } = 1;
  public List<ParagraphPayload> Paragraphs { get; init; } = new List<ParagraphPayload>();
}

public record ParagraphPayload
{
  public string Align { get; init; } = "left";
  public double SpaceBefore { get; init; }
  public double SpaceAfter { get; init; }
  public double? LineSpacing { get; init; }
  public double Indent { get; init; }
  public double Hanging { get; init; }
  public string? Bullet { get; init; }
  /// <summary>The bullet's own color and size, a multiple of the paragraph's, when its level sets them.</summary>
  public string? BulletColor { get; init; }
  public double? BulletSize { get; init; }
  /// <summary>The paragraph's own character formatting, which the bullet and an empty paragraph's height take.
  /// </summary>
  public RunPayload Mark { get; init; } = new RunPayload();
  public List<RunPayload> Runs { get; init; } = new List<RunPayload>();
}

public record RunPayload
{
  public string Text { get; init; } = "";
  public string? Font { get; init; }
  public double? Size { get; init; }
  public string? Color { get; init; }
  public bool? Bold { get; init; }
  public bool? Italic { get; init; }
  public bool? Underline { get; init; }
  public string? Link { get; init; }
  public string? Field { get; init; }
  public TracePayload? Trace { get; init; }
}

/// <summary>Where the page finds a picture, recolored by <paramref name="fill"/> when it is an SVG with a fill.
/// </summary>
public delegate string PictureUrl(string path, string? fill);

/// <summary>Turns scenes into what the page draws: colors as hex, fonts as families, and pictures as URLs the server
/// agrees to serve.</summary>
public static class PayloadBuilder
{
  public static DeckPayload Build(Presentation presentation, long version, PictureUrl pictureUrl)
  {
    return new DeckPayload
    {
      Name = presentation.Name,
      Size = presentation.Size,
      Version = version,
      Sections = presentation.Sections.Select(section => new SectionPayload(section.Name, section.Slides)).ToList(),
      Slides = presentation.Slides.Select(slide => Slide(presentation, slide, pictureUrl)).ToList(),
      Comments = presentation.Comments,
      Fonts = presentation.Theme.Faces
        .Select(face => new FontFacePayload(face.Family, face.Weight, face.Italic ? "italic" : "normal",
          pictureUrl(face.Path, null)))
        .ToList()
    };
  }

  public static string Title(Presentation presentation, Slide slide)
  {
    return slide.Title?.Text is List<Run> runs ? SceneBuilder.Text(presentation, slide, runs) : "";
  }

  public static SlidePayload Slide(Presentation presentation, Slide slide, PictureUrl pictureUrl)
  {
    SlideScene scene;
    try
    {
      scene = SceneBuilder.Build(presentation, slide);
    }
    catch (LoadException exception)
    {
      return new SlidePayload
      {
        Id = slide.Id,
        Number = slide.Number,
        Section = slide.Section,
        Error = exception.Message
      };
    }
    Theme theme = presentation.Theme;
    SceneItem? title = scene.Items.FirstOrDefault(item => item.Role == "title");
    return new SlidePayload
    {
      Id = slide.Id,
      Number = slide.Number,
      Title = title is null ? null : string.Concat(title.Paragraphs![0].Runs.Select(run => run.Text)),
      Layout = slide.Layout,
      Section = slide.Section,
      Background = Hex(scene.Background.Value, theme),
      Items = scene.Items.Select(item => Item(item, theme, pictureUrl) with { Edit = Edit(slide, item) }).ToList(),
      Guides = Guides(theme, slide)
    };
  }

  private static List<GuidePayload> Guides(Theme theme, Slide slide)
  {
    Layout layout = theme.Layouts[slide.Layout];
    var guides = new List<GuidePayload>();
    HashSet<string> framed = slide.Elements.Select(element => element.Frame?.Area).OfType<string>().ToHashSet();
    foreach (string name in theme.Areas.Keys.Concat(layout.Areas.Keys).Distinct())
    {
      (Area area, string owner) = layout.Areas.TryGetValue(name, out Area? own)
        ? (own, "layout")
        : (theme.Areas[name], "theme");
      guides.Add(new GuidePayload("area", name, owner, area.Rect, framed.Contains(name)));
    }

    void Placeholder(string name, Placeholder? themeLevel, Placeholder? layoutLevel, bool used)
    {
      (Frame? frame, string owner) = layoutLevel?.Frame is not null
        ? (layoutLevel.Frame, "layout")
        : (themeLevel?.Frame, "theme");
      if (frame is not null)
      {
        guides.Add(new GuidePayload("placeholder", name, owner, frame.Rect, used));
      }
    }

    if (!layout.TitleRemoved)
    {
      Placeholder("title", theme.Title, layout.Title, slide.Title is not null);
    }
    foreach (string name in theme.Placeholders.Keys.Concat(layout.Placeholders.Keys).Distinct())
    {
      Placeholder(name, theme.Placeholders.GetValueOrDefault(name), layout.Placeholders.GetValueOrDefault(name),
        slide.Placeholders.ContainsKey(name));
    }
    return guides;
  }

  private static ItemPayload Item(SceneItem item, Theme theme, PictureUrl pictureUrl)
  {
    Dictionary<string, Resolved> properties = item.Properties;
    T? Get<T>(string key) where T : class
    {
      return properties.TryGetValue(key, out Resolved? value) ? value.Value as T : null;
    }
    double? Number(string key)
    {
      return properties.TryGetValue(key, out Resolved? value) ? Convert.ToDouble(value.Value) : null;
    }

    Dictionary<string, double> adjust = properties
      .Where(pair => pair.Key.StartsWith("adjust.", StringComparison.Ordinal))
      .ToDictionary(pair => pair.Key["adjust.".Length..], pair => Convert.ToDouble(pair.Value.Value));
    string? picture = item.Element?.PicturePath;
    string? fill = properties.TryGetValue("fill", out Resolved? painted) ? Hex(painted.Value, theme) : null;
    return new ItemPayload
    {
      Id = item.Id,
      Owner = item.Owner,
      Kind = item.Kind,
      Role = item.Role,
      Frame = Get<Frame>("frame")?.Rect,
      Rotate = Number("rotate"),
      Flip = Get<string>("flip"),
      Opacity = Number("opacity"),
      Fill = fill,
      Stroke = Stroke(properties, theme),
      Shape = Get<string>("shape"),
      Direction = item.Kind == "shape" ? Get<string>("direction") : null,
      Adjust = adjust.Count == 0 ? null : adjust,
      Tail = Get<double[]>("tail"),
      From = Get<double[]>("from"),
      To = Get<double[]>("to"),
      Start = Get<string>("start"),
      End = Get<string>("end"),
      Picture = picture is null ? null : pictureUrl(picture, fill),
      Fit = item.Kind == "picture" ? Get<string>("fit") : null,
      Crop = Get<double[]>("crop"),
      Alt = Get<string>("alt"),
      Text = item.Paragraphs is null ? null : Text(item, theme),
      Chart = item.Chart is null ? null : Chart(item.Chart, properties, theme),
      Table = item.Table is null ? null : Table(item.Table, theme)
    };
  }

  private static EditPayload Edit(Slide slide, SceneItem item)
  {
    if (item.Owner != "slide")
    {
      string locked = $"{item.Id} is on the {item.Owner}; change it in {item.Origin.File}";
      return new EditPayload { Locked = locked, TextLocked = item.Paragraphs is null ? null : locked };
    }
    if (item.Element is not Element element)
    {
      Placeholder words = item.Role == "title" ? slide.Title! : slide.Placeholders[item.Id];
      Origin? text = words.Origins.GetValueOrDefault("text") ?? (words.Text is null ? null : words.Origin);
      return new EditPayload
      {
        Frame = words.Origins.TryGetValue("frame", out Origin? frame)
          ? Source(frame)
          : new SourcePayload($"{words.Origin.Path}.frame", null),
        Text = text is null ? null : Source(text)
      };
    }
    return new EditPayload
    {
      Frame = element.Origins.TryGetValue("frame", out Origin? frameOrigin) ? Source(frameOrigin) : null,
      From = element.Origins.TryGetValue("from", out Origin? from) ? Source(from) : null,
      To = element.Origins.TryGetValue("to", out Origin? to) ? Source(to) : null,
      Text = element.Text is not null && element.Origins.TryGetValue("text", out Origin? paragraphs)
        ? Source(paragraphs)
        : null,
      Delete = Source(element.Origin)
    };
  }

  private static SourcePayload Source(Origin origin)
  {
    return new SourcePayload(origin.Path, origin.Source);
  }

  private static TablePayload Table(SceneTable table, Theme theme)
  {
    return new TablePayload(
      table.Widths.Select(width => Convert.ToDouble(width.Value)).ToList(),
      table.Heights.Select(height => height is null ? (double?)null : Convert.ToDouble(height.Value)).ToList(),
      table.Cells.Select(cell => new CellPayload
      {
        Row = cell.Row,
        Column = cell.Column,
        Rows = cell.Rows,
        Columns = cell.Columns,
        Fill = cell.Properties.TryGetValue("fill", out Resolved? fill) ? Hex(fill.Value, theme) : null,
        BorderTop = Stroke(cell.Properties, theme, "border_top"),
        BorderBottom = Stroke(cell.Properties, theme, "border_bottom"),
        BorderLeft = Stroke(cell.Properties, theme, "border_left"),
        BorderRight = Stroke(cell.Properties, theme, "border_right"),
        Text = Text(cell.Properties, cell.Paragraphs, theme),
        Trace = cell.Trace
      }).ToList());
  }

  private static ChartPayload Chart(Chart chart, Dictionary<string, Resolved> properties, Theme theme)
  {
    object? Value(string key)
    {
      return properties.TryGetValue(key, out Resolved? value) ? value.Value : null;
    }
    string text = Hex(Value("text.color")!, theme)!;
    LabelsPayload? Labels(string prefix)
    {
      string show = Value($"{prefix}data_labels.show") as string ?? "none";
      return show == "none"
        ? null
        : new LabelsPayload(show, (string)Value($"{prefix}data_labels.position")!,
          Format(Value($"{prefix}data_labels.format") as string, theme),
          Value($"{prefix}data_labels.color") is Color color ? Hex(color, theme) : null,
          Value($"{prefix}data_labels.bold") as bool? ?? false);
    }
    MarkersPayload? Markers(string prefix)
    {
      return Value($"{prefix}markers.shape") is not string shape || shape == "none"
        ? null
        : new MarkersPayload(shape, (double)Value($"{prefix}markers.size")!,
          Value($"{prefix}markers.fill") is Color fill ? Hex(fill, theme) : null);
    }
    // A point's own marker keys over its series', or null where it has none and draws the series' marker; shape none
    // drops the series' marker from that point.
    MarkersPayload? PointMarkers(string prefix, string category)
    {
      string point = $"{prefix}points.{category}.markers.";
      if (!new[] { "shape", "size", "fill" }.Any(key => properties.ContainsKey(point + key)))
      {
        return null;
      }
      object? Own(string key) => Value(point + key) ?? Value($"{prefix}markers.{key}");
      return Own("shape") is not string shape || shape == "none"
        ? new MarkersPayload("none", 0, null)
        : new MarkersPayload(shape, Own("size") as double? ?? 5, Own("fill") is Color fill ? Hex(fill, theme) : null);
    }
    ValueAxisPayload? Axis(string name)
    {
      return Value($"{name}.visible") is not bool visible
        ? null
        : new ValueAxisPayload(visible, Convert.ToDouble(Value($"{name}.min")), Convert.ToDouble(Value($"{name}.max")),
          Convert.ToDouble(Value($"{name}.step")), Format(Value($"{name}.format") as string, theme),
          Value($"{name}.gridlines") as bool? ?? false, Value($"{name}.reverse") as bool? ?? false);
    }
    return new ChartPayload
    {
      Type = chart.Type,
      Family = chart.Family,
      Grouping = chart.Grouping,
      Title = Value("title") as string,
      Legend = Value("legend") is string legend and not "none" ? legend : null,
      Categories = chart.Categories,
      Series = chart.Series.Select((series, index) =>
      {
        List<TracePayload>? traces = chart.Book is not string book ? null
          : chart.SeriesIn == "columns"
            ? chart.Lines!.Select(line => new TracePayload(book, line, chart.Columns![index])).ToList()
            : chart.Columns!.Select(column => new TracePayload(book, chart.Lines![index], column)).ToList();
        if (!chart.SeriesOptions.ContainsKey(series.Name))
        {
          return new SeriesPayload(series.Name, series.Values, chart.Family, false, Labels(""),
            chart.Family == "line" ? Markers("") : null, traces);
        }
        string prefix = $"series.{series.Name}.";
        List<string?> colors = chart.Categories
          .Select(category => Value($"{prefix}points.{category}.color") is Color color ? Hex(color, theme) : null)
          .ToList();
        List<MarkersPayload?> pointMarkers = chart.Categories.Select(category => PointMarkers(prefix, category))
          .ToList();
        List<LabelsPayload?> pointLabels = chart.Categories
          .Select(category => $"{prefix}points.{category}.")
          .Select(point => properties.ContainsKey($"{point}data_labels.show")
            ? Labels(point) ?? LabelsPayload.Hidden
            : null)
          .ToList();
        return new SeriesPayload(series.Name, series.Values, Value($"{prefix}type") as string ?? chart.Family,
          Value($"{prefix}axis") is "secondary", Labels(prefix), Markers(prefix), traces,
          colors.Any(color => color is not null) ? colors : null,
          pointMarkers.Any(markers => markers is not null) ? pointMarkers : null,
          pointLabels.Any(labels => labels is not null) ? pointLabels : null);
      }).ToList(),
      Colors = ((List<Color>)Value("series_colors")!).Select(color => Hex(color, theme)).ToList(),
      ValueAxis = Axis("value_axis"),
      SecondaryAxis = Axis("secondary_axis"),
      CategoryAxis = Value("category_axis.visible") as bool?,
      CategoryLabels = Value("category_axis.labels") as bool?,
      Dates = Value("category_axis.format") is string dates
        ? new DateAxisPayload(theme.Styles.Formats[dates].Date!, (Value("category_axis.step") as DateStep)?.Count,
          (Value("category_axis.step") as DateStep)?.Unit)
        : null,
      GapWidth = Value("gap_width") as double?,
      Overlap = Value("overlap") as double?,
      Text = new ChartTextPayload(Family((string)Value("text.font")!, theme), Convert.ToDouble(Value("text.size")),
        text),
      GridColor = ColorMath.Luminance(text, 0.15, 0.85),
      AxisColor = ColorMath.Luminance(text, 0.25, 0.75),
      EdgeColor = theme.Colors["light1"]
    };
  }

  private static FormatPayload? Format(string? name, Theme theme)
  {
    if (name is null)
    {
      return null;
    }
    NumberFormat format = theme.Styles.Formats[name];
    Color? negative = format.NegativeStyle is null ? null : theme.Styles.Styles[format.NegativeStyle].Color;
    return new FormatPayload(format.Scale ?? 1, format.Decimals ?? 0, format.Separator ?? true, format.Prefix ?? "",
      format.Suffix ?? "", format.Negative ?? "parentheses", negative is null ? null : Hex(negative, theme));
  }

  private static StrokePayload? Stroke(Dictionary<string, Resolved> properties, Theme theme, string key = "stroke")
  {
    if (!properties.TryGetValue($"{key}.color", out Resolved? color) ||
      !properties.TryGetValue($"{key}.width", out Resolved? width))
    {
      return null;
    }
    string dash = properties.TryGetValue($"{key}.dash", out Resolved? value) ? (string)value.Value : "solid";
    return new StrokePayload(Hex(color.Value, theme)!, Convert.ToDouble(width.Value), dash);
  }

  private static TextPayload Text(SceneItem item, Theme theme)
  {
    return Text(item.Properties, item.Paragraphs!, theme);
  }

  private static TextPayload Text(Dictionary<string, Resolved> properties, List<SceneParagraph> paragraphs,
    Theme theme)
  {
    return new TextPayload
    {
      Insets = properties.TryGetValue("insets", out Resolved? insets) ? (double[])insets.Value : [0, 0, 0, 0],
      Valign = properties.TryGetValue("valign", out Resolved? valign) ? (string)valign.Value : "top",
      Fit = properties.TryGetValue("fit", out Resolved? fit) ? (string)fit.Value : "none",
      Wrap = !properties.TryGetValue("wrap", out Resolved? wrap) || (bool)wrap.Value,
      Columns = properties.TryGetValue("columns", out Resolved? columns) ? (int)columns.Value : 1,
      Paragraphs = paragraphs.Select(paragraph => Paragraph(paragraph, theme)).ToList()
    };
  }

  private static ParagraphPayload Paragraph(SceneParagraph paragraph, Theme theme)
  {
    Dictionary<string, Resolved> properties = paragraph.Properties;
    double Number(string key)
    {
      return properties.TryGetValue(key, out Resolved? value) ? Convert.ToDouble(value.Value) : 0;
    }
    return new ParagraphPayload
    {
      Align = properties.TryGetValue("align", out Resolved? align) ? (string)align.Value : "left",
      SpaceBefore = Number("space_before"),
      SpaceAfter = Number("space_after"),
      LineSpacing = properties.ContainsKey("line_spacing") ? Number("line_spacing") : null,
      Indent = paragraph.Indent is null ? 0 : Convert.ToDouble(paragraph.Indent.Value),
      Hanging = paragraph.Hanging is null ? 0 : Convert.ToDouble(paragraph.Hanging.Value),
      Bullet = paragraph.Bullet?.Value as string,
      BulletColor = paragraph.Bullet is null || paragraph.BulletColor is null
        ? null
        : Hex(paragraph.BulletColor.Value, theme),
      BulletSize = paragraph.Bullet is null || paragraph.BulletSize is null
        ? null
        : Convert.ToDouble(paragraph.BulletSize.Value),
      Mark = Run("", null, properties, theme),
      Runs = paragraph.Runs.Select(run => Run(run.Text, run.Link, run.Properties, theme) with
      {
        Field = run.Field,
        Trace = Reference.Trace(run.Field)
      })
        .ToList()
    };
  }

  private static RunPayload Run(string text, string? link, Dictionary<string, Resolved> properties, Theme theme)
  {
    bool? Flag(string key)
    {
      return properties.TryGetValue(key, out Resolved? value) && (bool)value.Value ? true : null;
    }
    return new RunPayload
    {
      Text = text,
      Font = properties.TryGetValue("font", out Resolved? font) ? Family((string)font.Value, theme) : null,
      Size = properties.TryGetValue("size", out Resolved? size) ? Convert.ToDouble(size.Value) : null,
      Color = properties.TryGetValue("color", out Resolved? color) ? Hex(color.Value, theme) : null,
      Bold = Flag("bold"),
      Italic = Flag("italic"),
      Underline = Flag("underline"),
      Link = link
    };
  }

  public static string Family(string font, Theme theme)
  {
    return theme.Fonts.TryGetValue(font, out string? family) ? family : font;
  }

  public static string? Hex(object value, Theme theme)
  {
    return value is Color { IsNone: false } color ? ColorMath.Resolve(color, theme) : null;
  }


}