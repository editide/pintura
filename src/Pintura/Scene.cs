using System.Globalization;

namespace Pintura;

/// <summary>An effective value and the line that set it. A null origin is geometry the engine defines, such as a
/// shape's default direction, or a chart property PowerPoint defaults when the export leaves it out.</summary>
public record Resolved(object Value, Origin? Origin);

/// <summary>A slide as drawn: the background, then the theme's and layout's elements, the title and placeholders,
/// and the slide's own elements, each with its effective properties.</summary>
public class SlideScene
{
  public Slide Slide { get; init; } = new Slide();
  public Resolved Background { get; init; } = new Resolved(new Color(), null);
  public List<SceneItem> Items { get; init; } = new List<SceneItem>();
}

public class SceneItem
{
  public string Id { get; init; } = "";
  /// <summary><c>theme</c>, <c>layout</c>, or <c>slide</c>: the level whose file owns the item.</summary>
  public string Owner { get; init; } = "";
  public string Kind { get; init; } = "";
  /// <summary><c>title</c> or <c>placeholder</c> for text the layout places and the slide writes.</summary>
  public string? Role { get; init; }
  public Origin Origin { get; init; } = new Origin("", 0, 0);
  public Dictionary<string, Resolved> Properties { get; init; } = new Dictionary<string, Resolved>();
  public List<SceneParagraph>? Paragraphs { get; init; }
  /// <summary>The model element behind the item; null for the title and placeholders.</summary>
  public Element? Element { get; init; }
  public Chart? Chart { get; init; }
  public SceneTable? Table { get; init; }
}

/// <summary>A table as drawn: each column's width, each row's minimum height, and the cells a merge leaves showing,
/// row by row.</summary>
public class SceneTable
{
  /// <summary>A null origin is a column without a width, sharing what the others leave of the frame.</summary>
  public List<Resolved> Widths { get; init; } = new List<Resolved>();
  public List<Resolved?> Heights { get; init; } = new List<Resolved?>();
  public List<SceneCell> Cells { get; init; } = new List<SceneCell>();
}

/// <summary>A cell, or a merge from its first cell, with its box properties, fill, and borders.</summary>
public class SceneCell
{
  public int Row { get; init; }
  public int Column { get; init; }
  public int Rows { get; init; } = 1;
  public int Columns { get; init; } = 1;
  /// <summary>The book cell a view's number reads.</summary>
  public TracePayload? Trace { get; init; }
  public Dictionary<string, Resolved> Properties { get; init; } = new Dictionary<string, Resolved>();
  public List<SceneParagraph> Paragraphs { get; init; } = new List<SceneParagraph>();
}

public class SceneParagraph
{
  public int Level { get; init; }
  public Resolved? Bullet { get; init; }
  public Resolved? BulletColor { get; init; }
  public Resolved? BulletSize { get; init; }
  public Resolved? Indent { get; init; }
  /// <summary>How far the bullet hangs left of the text: the level's <c>hanging</c>, or else from the previous level's
  /// indent to this one's.</summary>
  public Resolved? Hanging { get; init; }
  public string? Style { get; init; }
  /// <summary>Whether the paragraph's style sets its alignment, which then beats the box's own.</summary>
  public bool StyleAligns { get; init; }
  public Origin Origin { get; init; } = new Origin("", 0, 0);
  public Dictionary<string, Resolved> Properties { get; init; } = new Dictionary<string, Resolved>();
  public List<SceneRun> Runs { get; init; } = new List<SceneRun>();
}

public class SceneRun
{
  public string Text { get; init; } = "";
  public string? Field { get; init; }
  public string? Link { get; init; }
  public List<string>? Styles { get; init; }
  public Origin Origin { get; init; } = new Origin("", 0, 0);
  public Dictionary<string, Resolved> Properties { get; init; } = new Dictionary<string, Resolved>();
}

/// <summary>Computes effective properties by walking the ladder: the theme's bases, the element's style, its
/// one-offs, then paragraph and run styles. Nothing is stored; every call recomputes from the model.</summary>
public class SceneBuilder
{
  private static readonly string[] ParagraphKeys = [.. TextProperties.Character, .. TextProperties.Paragraph];

  private readonly Presentation presentation;
  private readonly Slide slide;
  private readonly Layout layout;
  private readonly StyleSheet styles;

  private SceneBuilder(Presentation presentation, Slide slide)
  {
    this.presentation = presentation;
    this.slide = slide;
    layout = presentation.Theme.Layouts[slide.Layout];
    styles = presentation.Theme.Styles;
  }

  public static SlideScene Build(Presentation presentation, Slide slide)
  {
    if (slide.Error is not null)
    {
      throw slide.Error;
    }
    return new SceneBuilder(presentation, slide).Build();
  }

  /// <summary>A theme or layout element as the layout places it, apart from any slide: <c>${slide}</c> reads 0 and
  /// <c>${section}</c> reads empty.</summary>
  public static SceneItem Element(Presentation presentation, string layout, Element element, string owner)
  {
    return new SceneBuilder(presentation, new Slide { Layout = layout }).Item(element, owner);
  }

  /// <summary>The title or a placeholder as the theme and layout define it, with no slide's words.</summary>
  public static SceneItem Placeholder(Presentation presentation, string layout, string name, string role,
    Placeholder?[] chain)
  {
    return new SceneBuilder(presentation, new Slide { Layout = layout }).Placeholder(name, role, chain);
  }

  /// <summary>Runs as one string with their fields filled in for <paramref name="slide"/>.</summary>
  public static string Text(Presentation presentation, Slide slide, IEnumerable<Run> runs)
  {
    var builder = new SceneBuilder(presentation, slide);
    return string.Concat(runs.Select(run => run.Field is null ? run.Text : builder.FieldValue(run.Field).Text));
  }

  private SlideScene Build()
  {
    Theme theme = presentation.Theme;
    var items = new List<SceneItem>(layout.Elements.Where(element => !slide.Hide.Contains(element.Id))
      .Select(element => Item(element, "layout")));

    if (slide.Title is not null)
    {
      items.Add(Placeholder("title", "title", [theme.Title, layout.Title, slide.Title]));
    }
    IEnumerable<string> names = theme.Placeholders.Keys.Concat(layout.Placeholders.Keys).Distinct();
    foreach (string name in names.Where(slide.Placeholders.ContainsKey))
    {
      items.Add(Placeholder(name, "placeholder", [
        theme.Placeholders.GetValueOrDefault(name),
        layout.Placeholders.GetValueOrDefault(name),
        slide.Placeholders[name]
      ]));
    }
    items.AddRange(slide.Elements.Select(element => Item(element, "slide")));

    Resolved background = layout.Background is not null
      ? new Resolved(layout.Background, layout.Origins["background"])
      : new Resolved(theme.Background, theme.Origins.GetValueOrDefault("background"));
    return new SlideScene { Slide = slide, Background = background, Items = items };
  }

  private SceneItem Placeholder(string name, string role, Placeholder?[] chain)
  {
    Placeholder[] levels = chain.OfType<Placeholder>().ToArray();
    var properties = new Dictionary<string, Resolved>();
    Placeholder? framed = levels.LastOrDefault(level => level.Frame is not null);
    if (framed is not null)
    {
      properties["frame"] = new Resolved(framed.Frame!, framed.Origins["frame"]);
    }
    Placeholder? styled = levels.LastOrDefault(level => level.Style is not null);
    var layers = new List<TextProperties> { styles.Text };
    if (styled is not null)
    {
      properties["style"] = new Resolved(styled.Style!, styled.Origins["style"]);
      layers.Add(styles.Styles[styled.Style!]);
    }
    layers.AddRange(levels.Select(level => level.Properties));
    Apply(properties, layers, TextProperties.All);

    Placeholder words = levels[^1];
    var paragraphProperties = new Dictionary<string, Resolved>();
    Apply(paragraphProperties, layers, ParagraphKeys);
    var paragraph = new SceneParagraph
    {
      Origin = words.Origin,
      Properties = paragraphProperties,
      Runs = (words.Text ?? []).Select(run => Run(run, paragraphProperties)).ToList()
    };
    return new SceneItem
    {
      Id = name,
      Owner = "slide",
      Kind = "text",
      Role = role,
      Origin = words.Origin.At(role == "title" ? "title" : $"placeholders.{name}"),
      Properties = properties,
      Paragraphs = [paragraph]
    };
  }

  private SceneItem Item(Element element, string owner)
  {
    var properties = new Dictionary<string, Resolved>();
    void Set(string key, object? value)
    {
      if (value is not null)
      {
        properties[key] = new Resolved(value, element.Origins.GetValueOrDefault(key));
      }
    }

    Set("frame", element.Frame);
    Set("rotate", element.Rotate);
    Set("flip", element.Flip);
    StyleShape? look = element.Style is null ? null : styles.StyleShapes.GetValueOrDefault(element.Style);
    if (element.Opacity is null && look?.Opacity is double opacity)
    {
      properties["opacity"] = new Resolved(opacity, look.Origins["opacity"]);
    }
    Set("opacity", element.Opacity);
    Set("decorative", element.Decorative);
    switch (element.Kind)
    {
      case "shape":
        Set("shape", element.Shape);
        if (PresentationLoader.DirectionalShapes.Contains(element.Shape))
        {
          properties["direction"] = element.Direction is not null
            ? new Resolved(element.Direction, element.Origins["direction"])
            : DefaultDirection(element.Shape!);
        }
        foreach ((string key, double value) in element.Adjust ?? [])
        {
          properties[$"adjust.{key}"] = new Resolved(value, element.Origins[$"adjust.{key}"]);
        }
        Set("tail", element.Tail);
        Fill(properties, element, look?.Fill is null ? styles.ShapeFill : look.Fill,
          look?.Fill is null ? styles.Origins.GetValueOrDefault("shape.fill") : look.Origins["fill"]);
        Strokes(properties, "stroke", styles.ShapeStroke, look?.Stroke, element.Stroke);
        Shadows(properties, styles.ShapeShadow, look?.Shadow, element.Shadow);
        break;
      case "text":
        Fill(properties, element, look?.Fill, look?.Origins.GetValueOrDefault("fill"));
        Strokes(properties, "stroke", look?.Stroke, element.Stroke);
        Shadows(properties, look?.Shadow, element.Shadow);
        break;
      case "line":
        Set("from", element.From);
        Set("to", element.To);
        Set("start", element.Start);
        Set("end", element.End);
        Strokes(properties, styles.LineStroke, element.Stroke);
        break;
      case "picture":
        Set("picture", element.Picture);
        properties["fit"] = element.Fit is not null
          ? new Resolved(element.Fit, element.Origins["fit"])
          : new Resolved(styles.PictureFit ?? "contain", styles.Origins.GetValueOrDefault("picture.fit"));
        Set("crop", element.Crop);
        Set("alt", element.Alt);
        Set("style", element.Style);
        if (PresentationLoader.Svg(element.PicturePath!))
        {
          Fill(properties, element, look?.Fill, look?.Origins.GetValueOrDefault("fill"));
        }
        Strokes(properties, "stroke", look?.Stroke, element.Stroke);
        Shadows(properties, look?.Shadow, element.Shadow);
        break;
      case "chart":
        Set("chart", element.Chart);
        ChartProperties(properties, presentation.Charts[element.Chart!]);
        break;
      case "table":
        Set("table", element.Table);
        TableProperties(properties, presentation.Tables[element.Table!]);
        break;
    }

    List<SceneParagraph>? paragraphs = null;
    if (element.Kind is "text" or "shape")
    {
      List<TextProperties> box = BoxLayers(element, null);
      if (element.Style is not null)
      {
        Set("style", element.Style);
      }
      Apply(properties, box, TextProperties.All);
      paragraphs = (element.Text ?? []).Select(paragraph => Paragraph(element, paragraph)).ToList();
    }

    return new SceneItem
    {
      Id = element.Id,
      Owner = owner,
      Kind = element.Kind,
      Origin = element.Origin,
      Properties = properties,
      Paragraphs = paragraphs,
      Element = element,
      Chart = element.Chart is null ? null : presentation.Charts[element.Chart],
      Table = element.Table is null ? null : Table(presentation.Tables[element.Table], (Frame)properties["frame"].Value)
    };
  }

  /// <summary>The table's own keys, and the base text every cell starts from.</summary>
  private void TableProperties(Dictionary<string, Resolved> properties, Table table)
  {
    foreach (string key in new[] { "style", "data", "header_rows" }.Where(table.Origins.ContainsKey))
    {
      object value = key switch { "style" => table.Style!, "data" => table.Source, _ => table.HeaderRows };
      properties[key] = new Resolved(value, table.Origins[key]);
    }
    Apply(properties, [styles.Text], TableFormatting.Text);
  }

  private SceneTable Table(Table table, Frame frame)
  {
    double fixedWidth = table.Columns.Sum(column => column.Width ?? 0);
    int shared = table.ColumnCount - table.Columns.Count(column => column.Width is not null);
    double share = shared == 0 ? 0 : Math.Max(0, (frame.Rect[2] - fixedWidth) / shared);
    var widths = new List<Resolved>();
    for (int index = 0; index < table.ColumnCount; index++)
    {
      TableColumn? column = index < table.Columns.Count ? table.Columns[index] : null;
      widths.Add(column?.Width is double width
        ? new Resolved(width, column.Origins["width"])
        : new Resolved(share, null));
    }

    var heights = new List<Resolved?>();
    for (int row = 1; row <= table.Values.Count; row++)
    {
      TableRow? own = table.Rows.GetValueOrDefault(row);
      heights.Add(own?.Height is double height ? new Resolved(height, own.Origins["height"]) : null);
    }

    var cells = new List<SceneCell>();
    for (int row = 1; row <= table.Values.Count; row++)
    {
      for (int column = 1; column <= table.ColumnCount; column++)
      {
        TableMerge? merge = table.Merges.FirstOrDefault(candidate => candidate.Covers(row, column));
        if (merge is not null && (merge.Row, merge.Column) != (row, column))
        {
          continue;
        }
        cells.Add(Cell(table, row, column, merge));
      }
    }
    return new SceneTable { Widths = widths, Heights = heights, Cells = cells };
  }

  /// <summary>Layers the column, the row's style from the table style, the row, and the cell, then the negative
  /// style when a format spells a negative number. A merge's bottom border is its last row's, and its right border its
  /// last column's.</summary>
  private SceneCell Cell(Table table, int row, int column, TableMerge? merge)
  {
    TableStyle? tableStyle = table.Style is null ? null : styles.Tables[table.Style];
    TableColumn? own = column <= table.Columns.Count ? table.Columns[column - 1] : null;
    TableRow? rowLevel = table.Rows.GetValueOrDefault(row);
    TableCell? cellLevel = table.Cells.LastOrDefault(cell => cell.Row == row && cell.Column == column);
    bool header = row <= table.HeaderRows;
    var properties = new Dictionary<string, Resolved>();

    Resolved? rowStyle = RowStyle(table, tableStyle, row);
    TableFormatting? styled = null;
    if (rowStyle is not null)
    {
      properties["row_style"] = rowStyle;
      styled = tableStyle!.Rows[(string)rowStyle.Value];
    }
    // A column formats its values, so a row's style, a header's above all, outranks it.
    var layers = new List<TextProperties> { styles.Text };
    if (own?.Style is string columnStyle)
    {
      properties["style"] = new Resolved(columnStyle, own.Origins["style"]);
      layers.Add(styles.Styles[columnStyle]);
    }
    layers.AddRange(new[] { own?.Properties, styled?.Properties, rowLevel?.Properties }.OfType<TextProperties>());
    if (cellLevel?.Style is string cellStyle)
    {
      properties["style"] = new Resolved(cellStyle, cellLevel.Origins["style"]);
      layers.Add(styles.Styles[cellStyle]);
    }
    if (cellLevel is not null)
    {
      layers.Add(cellLevel.Properties);
    }

    // A view's cell spells its exact value in a format, and shows Libreta's display without one.
    string value = table.Exact?[row - 1][column - 1] ?? table.Values[row - 1][column - 1];
    TableFormatting? formatted = header ? null : new TableFormatting?[] { cellLevel, rowLevel, own }
      .FirstOrDefault(level => level?.Format is not null);
    string? spelled = null;
    NumberFormat? cellFormat = formatted is null ? null : styles.Formats[formatted.Format!];
    if (cellFormat?.Date is not null && PresentationLoader.IsoDate(value, out DateOnly date))
    {
      properties["format"] = new Resolved(formatted!.Format!, formatted.Origins["format"]);
      spelled = cellFormat.SpellDate(date);
    }
    else if (cellFormat is not null && cellFormat.Date is null &&
      PlainNumber.TryParse(value, out double number))
    {
      properties["format"] = new Resolved(formatted!.Format!, formatted.Origins["format"]);
      NumberFormat format = cellFormat;
      spelled = format.Spell(number);
      if (format.NegativeStyle is string negative && number * (format.Scale ?? 1) < 0)
      {
        layers.Add(styles.Styles[negative]);
      }
    }
    Apply(properties, layers, TableFormatting.Text);

    int body = row - table.HeaderRows;
    bool banded = rowStyle is null && body > 0 && body % 2 == 0 && tableStyle?.Banded is not null;
    if (styled?.Fill is Color styleFill)
    {
      properties["fill"] = new Resolved(styleFill, styled.Origins["fill"]);
    }
    else if (banded)
    {
      properties["fill"] = new Resolved(tableStyle!.Banded!, tableStyle.Origins["banded"]);
    }
    foreach (TableFormatting? level in new TableFormatting?[] { rowLevel, cellLevel })
    {
      if (level?.Fill is Color fill)
      {
        properties["fill"] = new Resolved(fill, level.Origins["fill"]);
      }
    }

    int last = merge is null ? row : merge.Row + merge.Rows - 1;
    TableFormatting? lastStyled = RowStyle(table, tableStyle, last) is Resolved lastStyle
      ? tableStyle!.Rows[(string)lastStyle.Value]
      : null;
    TableRow? lastRow = table.Rows.GetValueOrDefault(last);
    Strokes(properties, "border_top", styled?.BorderTop, rowLevel?.BorderTop, cellLevel?.BorderTop);
    Strokes(properties, "border_bottom", lastStyled?.BorderBottom, lastRow?.BorderBottom, cellLevel?.BorderBottom);
    int right = merge is null ? column : merge.Column + merge.Columns - 1;
    TableColumn? lastColumn = right <= table.Columns.Count ? table.Columns[right - 1] : null;
    Strokes(properties, "border_left", own?.BorderLeft, cellLevel?.BorderLeft);
    Strokes(properties, "border_right", lastColumn?.BorderRight, cellLevel?.BorderRight);

    List<Paragraph> paragraphs = spelled is null
      ? table.Paragraphs[row - 1][column - 1]
      : [Spelled(spelled, table.Paragraphs[row - 1][column - 1][0].Origin)];
    Resolved? indent = column == 1 && table.Indents?[row - 1] is int steps and > 0 &&
      tableStyle?.Indent is double step
      ? new Resolved(step * steps, tableStyle!.Origins["indent"])
      : null;
    TracePayload? trace = table.View is string view &&
      table.Sources?[row - 1][column - 1] is (string line, string bookColumn)
      ? new TracePayload(view[..view.IndexOf("::", StringComparison.Ordinal)], line, bookColumn)
      : null;
    return new SceneCell
    {
      Row = row,
      Column = column,
      Rows = merge?.Rows ?? 1,
      Columns = merge?.Columns ?? 1,
      Trace = trace,
      Properties = properties,
      Paragraphs = paragraphs.Select(paragraph => Paragraph(paragraph, level => Leveled(layers, level),
        cellLevel?.Style ?? own?.Style, indent)).ToList()
    };
  }

  private static Paragraph Spelled(string text, Origin origin)
  {
    return new Paragraph { Runs = [new Run { Text = text, Origin = origin }], Origin = origin };
  }

  /// <summary>The row's own row style, or <c>header</c> in a header row when the table style has one.</summary>
  private static Resolved? RowStyle(Table table, TableStyle? tableStyle, int row)
  {
    TableRow? own = table.Rows.GetValueOrDefault(row);
    if (own?.Style is string name)
    {
      return new Resolved(name, own.Origins["style"]);
    }
    return row <= table.HeaderRows && tableStyle?.Rows.ContainsKey("header") == true
      ? new Resolved("header", tableStyle.Rows["header"].Origin)
      : null;
  }

  /// <summary>A cell's layers with a bullet level's properties over the base text.</summary>
  private static List<TextProperties> Leveled(List<TextProperties> layers, Level? level)
  {
    return level is null ? layers : [layers[0], level.Properties, .. layers.Skip(1)];
  }

  /// <summary>Layers the theme's <c>chart</c> base, the chart's style, and the chart. Text nothing sets comes from
  /// the base text; what a chart of this type doesn't draw is left out.</summary>
  private void ChartProperties(Dictionary<string, Resolved> properties, Chart chart)
  {
    properties["type"] = new Resolved(chart.Type, chart.Origins["type"]);
    foreach (string key in new[] { "style", "data", "series_in" }.Where(chart.Origins.ContainsKey))
    {
      object value = key switch { "style" => chart.Style!, "data" => chart.Source, _ => chart.SeriesIn };
      properties[key] = new Resolved(value, chart.Origins[key]);
    }

    properties["text.font"] = new Resolved(styles.Text.Font!, styles.Text.Origins["font"]);
    properties["text.size"] = new Resolved(styles.Text.Size!, styles.Text.Origins["size"]);
    properties["text.color"] = new Resolved(styles.Text.Color!, styles.Text.Origins["color"]);
    ChartProperties?[] levels = [styles.Chart, chart.Style is null ? null : styles.Charts[chart.Style],
      chart.Properties];
    foreach (ChartProperties level in levels.OfType<ChartProperties>())
    {
      foreach (string key in Pintura.ChartProperties.Keys)
      {
        if (level.Get(key) is object value)
        {
          properties[key] = new Resolved(value, level.Origins[key]);
        }
      }
    }

    bool pie = chart.Family == "pie";
    bool bars = chart.Family is "column" or "bar";
    if (!pie)
    {
      properties.TryAdd("value_axis.visible", new Resolved(true, null));
      properties.TryAdd("category_axis.visible", new Resolved(true, null));
      properties.TryAdd("category_axis.labels", new Resolved(true, null));
    }
    else
    {
      foreach (string key in properties.Keys.Where(key => key.Contains("axis.", StringComparison.Ordinal)).ToList())
      {
        properties.Remove(key);
      }
    }
    if (bars)
    {
      properties.TryAdd("gap_width", new Resolved(150.0, null));
      if (chart.Grouping == "clustered")
      {
        properties.TryAdd("overlap", new Resolved(0.0, null));
      }
      else
      {
        properties["overlap"] = new Resolved(100.0, null);
      }
    }
    else
    {
      properties.Remove("gap_width");
      properties.Remove("overlap");
    }
    properties.TryAdd("series_colors", new Resolved(
      new List<Color>(Color.ThemeColors[4..10].Select(name => new Color { Theme = name })), null));
    foreach ((string name, ChartSeriesOptions options) in chart.SeriesOptions)
    {
      SeriesProperties(properties, chart, name, options);
    }
    if (chart.SeriesOptions.Values.Any(options => options.Axis is not null))
    {
      properties.TryAdd("secondary_axis.visible", new Resolved(true, null));
    }
    else
    {
      foreach (string key in properties.Keys.Where(key => key.StartsWith("secondary_axis.", StringComparison.Ordinal))
        .ToList())
      {
        properties.Remove(key);
      }
    }
    if (chart.Family == "line" || chart.SeriesOptions.Values.Any(options => options.Type == "line"))
    {
      MarkerDefaults(properties, "");
    }
    else
    {
      foreach (string key in properties.Keys.Where(key => key.StartsWith("markers.", StringComparison.Ordinal)).ToList())
      {
        properties.Remove(key);
      }
    }
    if (properties.TryGetValue("data_labels.show", out Resolved? show) && (string)show.Value != "none")
    {
      properties.TryAdd("data_labels.position", new Resolved(DefaultLabelPosition(chart), null));
    }
    else
    {
      properties.Remove("data_labels.position");
      properties.Remove("data_labels.format");
      properties.Remove("data_labels.color");
    }
    foreach ((string axis, bool secondary) in new[] { ("value_axis", false), ("secondary_axis", true) })
    {
      if (properties.ContainsKey($"{axis}.visible"))
      {
        ScaleAxis(properties, chart, axis, secondary);
      }
    }
  }

  /// <summary>Fills the ends and step of an axis that its files leave out with <see cref="AxisScale"/>'s.</summary>
  private static void ScaleAxis(Dictionary<string, Resolved> properties, Chart chart, string axis, bool secondary)
  {
    double? Fixed(string key) => properties.TryGetValue($"{axis}.{key}", out Resolved? value)
      ? Convert.ToDouble(value.Value)
      : null;
    Scale scale = AxisScale.Of(chart, secondary, Fixed("min"), Fixed("max"), Fixed("step"));
    properties.TryAdd($"{axis}.min", new Resolved(scale.Min, null));
    properties.TryAdd($"{axis}.max", new Resolved(scale.Max, null));
    properties.TryAdd($"{axis}.step", new Resolved(scale.Step, null));
  }

  /// <summary>A series' own keys under <c>series.{name}.</c>, its labels layered over the chart's. A series drawn
  /// otherwise than the chart's type doesn't take the chart's label position, and a total shows its value without
  /// being asked, past its stack's end and in the text's color, since the chart's label color is for inside the bars.
  /// </summary>
  private static void SeriesProperties(Dictionary<string, Resolved> properties, Chart chart, string name,
    ChartSeriesOptions options)
  {
    string prefix = $"series.{name}.";
    string drawn = options.Type ?? chart.Family;
    if (options.Type is string type)
    {
      properties[$"{prefix}type"] = new Resolved(type, options.Origins["type"]);
    }
    if (options.Axis is string axis)
    {
      properties[$"{prefix}axis"] = new Resolved(axis, options.Origins["axis"]);
    }
    ChartLabels own = options.DataLabels;
    var values = new Dictionary<string, object?>
    {
      ["show"] = own.Show, ["format"] = own.Format, ["position"] = own.Position, ["color"] = own.Color,
      ["bold"] = own.Bold
    };
    foreach ((string key, object? value) in values)
    {
      string labelKey = $"data_labels.{key}";
      bool inherits = key switch
      {
        "show" or "color" => drawn != "total",
        "position" => drawn == chart.Family,
        _ => true
      };
      if (value is not null)
      {
        properties[prefix + labelKey] = new Resolved(value, options.Origins[labelKey]);
      }
      else if (inherits && properties.TryGetValue(labelKey, out Resolved? inherited))
      {
        properties[prefix + labelKey] = inherited;
      }
    }
    if (drawn == "total")
    {
      properties.TryAdd($"{prefix}data_labels.show", new Resolved("value", null));
    }
    // A point's labels layer over its series' as written, so one point shown on a series shown none takes the
    // position and color the series would.
    Dictionary<string, Resolved> written = values.Keys
      .Where(key => properties.ContainsKey($"{prefix}data_labels.{key}"))
      .ToDictionary(key => key, key => properties[$"{prefix}data_labels.{key}"]);
    string defaultPosition = drawn switch { "line" => "above", "total" => "outside", _ => DefaultLabelPosition(chart) };
    LabelDefaults(properties, prefix, values.Keys, defaultPosition);
    foreach ((string category, ChartPoint point) in options.Points)
    {
      string pointKey = $"points.{category}.";
      foreach ((string key, object? value) in new Dictionary<string, object?>
      {
        ["color"] = point.Color, ["markers.shape"] = point.Markers.Shape, ["markers.size"] = point.Markers.Size,
        ["markers.fill"] = point.Markers.Fill
      })
      {
        if (value is not null)
        {
          properties[prefix + pointKey + key] = new Resolved(value, options.Origins[pointKey + key]);
        }
      }
      ChartLabels labels = point.DataLabels;
      var pointLabels = new Dictionary<string, object?>
      {
        ["show"] = labels.Show, ["format"] = labels.Format, ["position"] = labels.Position, ["color"] = labels.Color,
        ["bold"] = labels.Bold
      };
      if (pointLabels.Values.All(value => value is null))
      {
        continue;
      }
      string labelPrefix = $"{prefix}{pointKey}";
      foreach ((string key, object? value) in pointLabels)
      {
        string labelKey = $"data_labels.{key}";
        if (value is not null)
        {
          properties[labelPrefix + labelKey] = new Resolved(value, options.Origins[pointKey + labelKey]);
        }
        else if (written.TryGetValue(key, out Resolved? inherited))
        {
          properties[labelPrefix + labelKey] = inherited;
        }
      }
      properties.TryAdd($"{labelPrefix}data_labels.show", new Resolved("none", null));
      LabelDefaults(properties, labelPrefix, pointLabels.Keys, defaultPosition);
    }
    if (drawn != "line")
    {
      return;
    }
    ChartMarkers markers = options.Markers;
    foreach ((string key, object? value) in new Dictionary<string, object?>
      { ["shape"] = markers.Shape, ["size"] = markers.Size, ["fill"] = markers.Fill })
    {
      string markerKey = $"markers.{key}";
      if (value is not null)
      {
        properties[prefix + markerKey] = new Resolved(value, options.Origins[markerKey]);
      }
      else if (properties.TryGetValue(markerKey, out Resolved? inherited))
      {
        properties[prefix + markerKey] = inherited;
      }
    }
    MarkerDefaults(properties, prefix);
  }

  /// <summary>Shown labels take the position their series draws them at unless something places them; hidden ones
  /// keep only <c>show</c>.</summary>
  private static void LabelDefaults(Dictionary<string, Resolved> properties, string prefix, IEnumerable<string> keys,
    string position)
  {
    if (properties.TryGetValue($"{prefix}data_labels.show", out Resolved? show) && (string)show.Value != "none")
    {
      properties.TryAdd($"{prefix}data_labels.position", new Resolved(position, null));
      return;
    }
    foreach (string key in keys.Where(key => key != "show"))
    {
      properties.Remove($"{prefix}data_labels.{key}");
    }
  }

  /// <summary>A line has no markers until something gives them a shape, and then they're PowerPoint's 5 points.
  /// </summary>
  private static void MarkerDefaults(Dictionary<string, Resolved> properties, string prefix)
  {
    properties.TryAdd($"{prefix}markers.shape", new Resolved("none", null));
    if ((string)properties[$"{prefix}markers.shape"].Value == "none")
    {
      properties.Remove($"{prefix}markers.size");
      properties.Remove($"{prefix}markers.fill");
    }
    else
    {
      properties.TryAdd($"{prefix}markers.size", new Resolved(5.0, null));
    }
  }

  private static string DefaultLabelPosition(Chart chart)
  {
    return PresentationLoader.DefaultLabelPosition(PresentationLoader.LabelPositionGroup(chart)) ?? "center";
  }

  /// <summary>The text layers of a box: the base, <c>shape_text</c> in a shape, the bullet level when there is one,
  /// the box's style, and its one-offs.</summary>
  private List<TextProperties> BoxLayers(Element element, Level? level)
  {
    var layers = new List<TextProperties> { styles.Text };
    if (element.Kind == "shape")
    {
      layers.Add(styles.ShapeText);
    }
    if (level is not null)
    {
      layers.Add(level.Properties);
    }
    if (element.Style is not null)
    {
      layers.Add(styles.Styles[element.Style]);
    }
    layers.Add(element.Properties);
    return layers;
  }

  private SceneParagraph Paragraph(Element element, Paragraph paragraph)
  {
    return Paragraph(paragraph, level => BoxLayers(element, level), element.Style);
  }

  /// <summary><paramref name="indent"/> sets an unbulleted paragraph's indent, as a view row's does its label.
  /// </summary>
  private SceneParagraph Paragraph(Paragraph paragraph, Func<Level?, List<TextProperties>> boxLayers,
    string? boxStyle, Resolved? indent = null)
  {
    Level? level = paragraph.Level == 0 ? null : styles.Levels[paragraph.Level];
    List<TextProperties> layers = [.. boxLayers(level)];
    if (paragraph.Style is not null)
    {
      layers.Add(styles.Styles[paragraph.Style]);
    }
    var properties = new Dictionary<string, Resolved>();
    Apply(properties, layers, ParagraphKeys);

    Resolved? bullet = null;
    Resolved? bulletColor = null;
    Resolved? bulletSize = null;
    Resolved? hanging = indent is null ? null : new Resolved(0.0, indent.Origin);
    if (level is not null)
    {
      string?[] named = [boxStyle, paragraph.Style];
      Resolved? Key(int number, string key, Func<Level, object?> value)
      {
        Resolved? found = null;
        foreach (Level layer in LevelLayers(number, named))
        {
          if (value(layer) is object set)
          {
            found = new Resolved(set, layer.Origins[key]);
          }
        }
        return found;
      }

      Resolved? character = Key(paragraph.Level, "bullet", layer => layer.Bullet);
      bullet = paragraph.Number is int number
        ? new Resolved($"{number}.", paragraph.Origin)
        : character is not null && (string)character.Value != "none" ? character : null;
      bulletColor = Key(paragraph.Level, "bullet_color", layer => layer.BulletColor);
      bulletSize = Key(paragraph.Level, "bullet_size", layer => layer.BulletSize);
      indent = Key(paragraph.Level, "indent", layer => layer.Indent) ?? new Resolved(0.0, null);
      hanging = Key(paragraph.Level, "hanging", layer => layer.Hanging);
      if (hanging is null)
      {
        double previous = paragraph.Level == 1
          ? 0
          : Convert.ToDouble(Key(paragraph.Level - 1, "indent", layer => layer.Indent)?.Value ?? 0.0);
        hanging = new Resolved(Math.Max(0, Convert.ToDouble(indent.Value) - previous), indent.Origin);
      }
    }

    return new SceneParagraph
    {
      Level = paragraph.Level,
      Bullet = bullet,
      BulletColor = bulletColor,
      BulletSize = bulletSize,
      Indent = indent,
      Hanging = hanging,
      Style = paragraph.Style,
      StyleAligns = paragraph.Style is not null && styles.Styles[paragraph.Style].Align is not null,
      Origin = paragraph.Origin,
      Properties = properties,
      Runs = paragraph.Runs.Select(run => Run(run, properties)).ToList()
    };
  }

  /// <summary>The theme's level, then the box's style's, then the paragraph's style's, for the styles that set one.
  /// </summary>
  private IEnumerable<Level> LevelLayers(int number, string?[] named)
  {
    yield return styles.Levels[number];
    foreach (string? style in named)
    {
      if (style is not null && styles.StyleLevels.GetValueOrDefault(style)?.GetValueOrDefault(number) is Level own)
      {
        yield return own;
      }
    }
  }

  private SceneRun Run(Run run, Dictionary<string, Resolved> paragraph)
  {
    var properties = new Dictionary<string, Resolved>();
    foreach (string key in TextProperties.Character)
    {
      if (paragraph.TryGetValue(key, out Resolved? value))
      {
        properties[key] = value;
      }
    }
    if (run.Link is not null)
    {
      Origin? hyperlink = presentation.Theme.Origins.GetValueOrDefault("colors.hyperlink");
      properties["color"] = new Resolved(new Color { Theme = "hyperlink" }, hyperlink);
      properties["underline"] = new Resolved(true, hyperlink);
    }
    foreach (string style in run.Styles ?? [])
    {
      Apply(properties, [styles.Styles[style]], TextProperties.Character);
    }
    if (run.Properties is not null)
    {
      Apply(properties, [run.Properties], TextProperties.Character);
    }
    if (run.Bold == true)
    {
      properties["bold"] = new Resolved(true, run.Origin.At("**"));
    }
    if (run.Italic == true)
    {
      properties["italic"] = new Resolved(true, run.Origin.At("*"));
    }

    string text = run.Text;
    Origin origin = run.Origin;
    if (run.Field is not null)
    {
      (text, Origin? fieldOrigin) = FieldValue(run.Field);
      origin = fieldOrigin ?? origin;
    }
    return new SceneRun
    {
      Text = text,
      Field = run.Field,
      Link = run.Link,
      Styles = run.Styles,
      Origin = origin,
      Properties = properties
    };
  }

  private (string Text, Origin? Origin) FieldValue(string name)
  {
    return name switch
    {
      "slide" => (slide.Number.ToString(CultureInfo.InvariantCulture), null),
      "section" => (slide.Section ?? "", presentation.Origin),
      _ when presentation.References.TryGetValue(name, out BookValue? value) => (value.Text, value.Origin),
      _ => (presentation.Fields[name].Value, presentation.Fields[name].Origin)
    };
  }

  private static void Apply(Dictionary<string, Resolved> target, IEnumerable<TextProperties> layers, string[] keys)
  {
    foreach (TextProperties layer in layers)
    {
      foreach (string key in keys)
      {
        if (layer.Get(key) is object value)
        {
          target[key] = new Resolved(value, layer.Origins[key]);
        }
      }
    }
  }

  private static void Fill(Dictionary<string, Resolved> properties, Element element, Color? fallback, Origin? origin)
  {
    if (element.Fill is not null)
    {
      properties["fill"] = new Resolved(element.Fill, element.Origins["fill"]);
    }
    else if (fallback is not null)
    {
      properties["fill"] = new Resolved(fallback, origin);
    }
  }

  /// <summary>Merges a stroke key by key over the base, so <c>stroke: { color: accent1 }</c> keeps the base width.
  /// </summary>
  private static void Strokes(Dictionary<string, Resolved> properties, Stroke? fallback, Stroke? own)
  {
    Strokes(properties, "stroke", fallback, own);
  }

  private static void Strokes(Dictionary<string, Resolved> properties, string key, params Stroke?[] layers)
  {
    foreach (Stroke? stroke in layers)
    {
      if (stroke is null)
      {
        continue;
      }
      if (stroke.None == true)
      {
        properties.Remove($"{key}.color");
        properties.Remove($"{key}.width");
        properties.Remove($"{key}.dash");
        properties[key] = new Resolved("none", stroke.Origins["none"]);
        continue;
      }
      properties.Remove(key);
      if (stroke.Color is not null)
      {
        properties[$"{key}.color"] = new Resolved(stroke.Color, stroke.Origins["color"]);
      }
      if (stroke.Width is not null)
      {
        properties[$"{key}.width"] = new Resolved(stroke.Width, stroke.Origins["width"]);
      }
      if (stroke.Dash is not null)
      {
        properties[$"{key}.dash"] = new Resolved(stroke.Dash, stroke.Origins["dash"]);
      }
    }
  }

  /// <summary>Merges a shadow key by key over the base, as <see cref="Strokes"/> does a stroke.</summary>
  private static void Shadows(Dictionary<string, Resolved> properties, params Shadow?[] layers)
  {
    foreach (Shadow? shadow in layers)
    {
      if (shadow is null)
      {
        continue;
      }
      if (shadow.None == true)
      {
        foreach (string key in properties.Keys.Where(key => key.StartsWith("shadow.", StringComparison.Ordinal)).ToList())
        {
          properties.Remove(key);
        }
        properties["shadow"] = new Resolved("none", shadow.Origins["none"]);
        continue;
      }
      properties.Remove("shadow");
      foreach ((string key, object? value) in new (string, object?)[]
        {
          ("color", shadow.Color), ("opacity", shadow.Opacity), ("blur", shadow.Blur), ("distance", shadow.Distance),
          ("angle", shadow.Angle)
        })
      {
        if (value is not null)
        {
          properties[$"shadow.{key}"] = new Resolved(value, shadow.Origins[key]);
        }
      }
    }
  }

  private static Resolved DefaultDirection(string shape)
  {
    return new Resolved(shape is "triangle" or "right_triangle" ? "up" : "right", null);
  }


}