using System.Globalization;
using System.Text.RegularExpressions;

namespace Pintura;

public partial class PresentationLoader
{
  private void LoadChart(string stem)
  {
    if (charts.ContainsKey(stem))
    {
      return;
    }
    string path = Path.Combine(root, "charts", $"{stem}.yaml");
    Node document = File(path).Load("charts");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    Node typeNode = map.Require("type");
    string type = typeNode.AsString();
    if (!Chart.Types.Contains(type))
    {
      throw typeNode.Error($"unknown chart type {type}; the types are {string.Join(", ", Chart.Types)}");
    }
    origins["type"] = typeNode.Origin;

    string? style = null;
    if (map.Take("style") is Node styleNode)
    {
      style = styleNode.AsString();
      if (!styles.Charts.ContainsKey(style))
      {
        throw styleNode.Error(styles.Charts.Count == 0
          ? $"unknown chart style {style}; the theme has none"
          : $"unknown chart style {style}; the theme's are {string.Join(", ", styles.Charts.Keys)}");
      }
      origins["style"] = styleNode.Origin;
    }

    Node dataNode = map.Require("data");
    string data = dataNode.IsMap ? "" : dataNode.AsString();
    origins["data"] = dataNode.Origin;
    string seriesIn = "rows";
    if (map.Take("series_in") is Node seriesInNode)
    {
      seriesIn = seriesInNode.AsChoice("rows", "columns");
      origins["series_in"] = seriesInNode.Origin;
    }
    var totals = new List<string>();
    if (map.Take("totals") is Node totalsNode)
    {
      totals = totalsNode.AsList().Select(entry => entry.AsString()).ToList();
      origins["totals"] = totalsNode.Origin;
    }

    var seriesOptions = new Dictionary<string, ChartSeriesOptions>(StringComparer.Ordinal);
    if (map.Take("series") is Node seriesNode)
    {
      foreach ((string name, Node value) in seriesNode.AsMap().Entries())
      {
        seriesOptions[name] = ReadSeriesOptions(value);
        origins[$"series.{name}"] = value.Origin;
      }
      origins["series"] = seriesNode.Origin;
    }
    var properties = new ChartProperties();
    ReadChartProperties(map, properties, styles.Formats);
    map.Done();

    string? book = null;
    List<string>? lines = null;
    List<string>? bookColumns = null;
    List<string> categories;
    List<ChartSeries> series;
    if (dataNode.IsMap)
    {
      (book, lines, bookColumns, categories, series) = ReadChartLines(dataNode, seriesIn);
    }
    else
    {
      string csvPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, data));
      if (!files.Exists(csvPath))
      {
        throw dataNode.Error($"no CSV at {data}; paths are relative to the file naming them");
      }
      RequireCsv(dataNode, data, csvPath);
      (categories, series) = ReadChartData(File(csvPath), seriesIn);
    }

    var chart = new Chart
    {
      Id = stem,
      Type = type,
      Style = style,
      Data = data,
      Book = book,
      Lines = lines,
      Columns = bookColumns,
      SeriesIn = seriesIn,
      Properties = properties,
      Categories = categories,
      Series = series,
      SeriesOptions = seriesOptions,
      Totals = totals,
      Origin = document.Origin,
      Origins = origins
    };
    ValidateChart(chart, dataNode);
    charts[stem] = chart;
  }

  /// <summary>Checks what depends on the type once the levels are merged, naming the level that set the value.
  /// </summary>
  private void ValidateChart(Chart chart, Node dataNode)
  {
    ChartProperties[] levels = ChartProperties.Levels(styles, chart);
    (object Value, Origin Origin)? Effective(string key) => PropertyLayers.Cascade(levels, key);

    if (Effective("data_labels.show") is ("percent", Origin showOrigin) && chart.Family != "pie")
    {
      throw new LoadException($"show: percent is for pie and doughnut charts; a {chart.Type} shows value or none",
        showOrigin, "charts");
    }
    if (Effective("data_labels.position") is (string position, Origin positionOrigin))
    {
      string group = Chart.LabelPositionGroup(chart.Type);
      string[] allowed = Chart.LabelPositions[group];
      if (!allowed.Contains(position))
      {
        string what = chart.Family == "line" ? "a line chart" : $"a {chart.Type}";
        throw new LoadException(allowed.Length == 0
            ? $"{what} places its labels on the ring, so it takes no position"
            : $"{what} places labels {Join(allowed)}, not {position}", positionOrigin, "charts");
      }
    }
    if (Effective("value_axis.min") is (double min, _) && Effective("value_axis.max") is (double max, Origin maxOrigin)
      && min >= max)
    {
      throw new LoadException($"value_axis.max, {max}, must be more than its min, {min}", maxOrigin, "charts");
    }
    if (Effective("category_axis.format") is (string dateFormat, Origin formatOrigin))
    {
      string? notDate = chart.Categories.FirstOrDefault(category => !IsoDate(category, out _));
      if (notDate is not null)
      {
        throw new LoadException($"{dateFormat} spells dates, and category {notDate} in {chart.Source} isn't one " +
          "written as YYYY-MM-DD", formatOrigin, "charts");
      }
    }
    else if (Effective("category_axis.step") is (DateStep, Origin stepOrigin))
    {
      throw new LoadException("a step is for a date axis; give category_axis a date format", stepOrigin, "charts");
    }
    if (Effective("secondary_axis.min") is (double secondaryMin, _)
      && Effective("secondary_axis.max") is (double secondaryMax, Origin secondaryOrigin)
      && secondaryMin >= secondaryMax)
    {
      throw new LoadException($"secondary_axis.max, {secondaryMax}, must be more than its min, {secondaryMin}",
        secondaryOrigin, "charts");
    }
    string? chartMarker = chart.Properties.Origins.Keys.FirstOrDefault(key =>
      key.StartsWith("markers.", StringComparison.Ordinal));
    if (chartMarker is not null && chart.Family != "line"
      && chart.SeriesOptions.Values.All(options => options.Type != "line"))
    {
      throw new LoadException($"markers sit on a line's points, and a {chart.Type} draws no line; give a series " +
        "type: line", chart.Properties.Origins[chartMarker], "charts");
    }
    ValidateWaterfall(chart, dataNode, Effective("category_axis.format")?.Origin);
    ValidateSeriesOptions(chart);
    if (chart.Type == "pie" && chart.Series.Count > 1)
    {
      throw dataNode.Error($"a pie draws one series, and {chart.Source} has {chart.Series.Count}; use series_in to " +
        "turn it, or a doughnut for a ring per series");
    }
  }

  private static void ValidateWaterfall(Chart chart, Node dataNode, Origin? dateFormat)
  {
    if (chart.Type != "waterfall")
    {
      if (chart.Origins.TryGetValue("totals", out Origin? totals))
      {
        throw new LoadException("totals are a waterfall's; a stack's total is a series with type: total", totals,
          "charts");
      }
      return;
    }
    if (chart.Series.Count != 1)
    {
      throw dataNode.Error($"a waterfall draws one series, and {chart.Source} has {chart.Series.Count}");
    }
    if (chart.SeriesOptions.Values.FirstOrDefault(options => options.Type is not null) is ChartSeriesOptions typed)
    {
      throw new LoadException("a waterfall draws its one series as steps and totals, so it takes no type",
        typed.Origins["type"], "charts");
    }
    if (dateFormat is not null)
    {
      throw new LoadException("a waterfall's categories are its steps, not a time line, so its axis takes no date " +
        "format", dateFormat, "charts");
    }
    if (chart.Totals.FirstOrDefault(total => !chart.Categories.Contains(total)) is string unknown)
    {
      throw new LoadException($"no category {unknown} in {chart.Source}; its categories are " +
        string.Join(", ", chart.Categories), chart.Origins["totals"], "charts");
    }
  }

  private ChartSeriesOptions ReadSeriesOptions(Node node)
  {
    MapNode map = node.AsMap();
    var options = new ChartSeriesOptions();
    if (map.Take("type") is Node type)
    {
      options.Type = type.AsChoice("line", "total");
      options.Origins["type"] = type.Origin;
    }
    if (map.Take("axis") is Node axis)
    {
      options.Axis = axis.AsChoice("secondary");
      options.Origins["axis"] = axis.Origin;
    }
    if (map.Take("dash") is Node dash)
    {
      options.Dash = dash.AsChoice("solid", "dash", "dot");
      options.Origins["dash"] = dash.Origin;
    }
    if (map.Take("outline") is Node outline)
    {
      options.Outline = ReadStroke(outline);
      options.Origins["outline"] = outline.Origin;
    }
    if (map.Take("data_labels") is Node labels)
    {
      ReadLabels(labels, options.DataLabels, styles.Formats,
        (key, value) => options.Origins[$"data_labels.{key}"] = value.Origin);
    }
    if (map.Take("markers") is Node markers)
    {
      ReadMarkers(markers, options.Markers, (key, value) => options.Origins[$"markers.{key}"] = value.Origin);
    }
    if (map.Take("points") is Node points)
    {
      foreach ((string category, Node value) in points.AsMap().Entries())
      {
        MapNode point = value.AsMap();
        var target = new ChartPoint();
        string prefix = $"points.{category}.";
        options.Origins[prefix] = value.Origin;
        if (point.Take("color") is Node color)
        {
          target.Color = ReadColor(color);
          options.Origins[$"{prefix}color"] = color.Origin;
        }
        if (point.Take("outline") is Node pointOutline)
        {
          target.Outline = ReadStroke(pointOutline);
          options.Origins[$"{prefix}outline"] = pointOutline.Origin;
        }
        if (point.Take("markers") is Node pointMarkers)
        {
          ReadMarkers(pointMarkers, target.Markers,
            (key, node) => options.Origins[$"{prefix}markers.{key}"] = node.Origin);
        }
        if (point.Take("data_labels") is Node pointLabels)
        {
          ReadLabels(pointLabels, target.DataLabels, styles.Formats,
            (key, node) => options.Origins[$"{prefix}data_labels.{key}"] = node.Origin);
        }
        point.Done();
        if (!options.Origins.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal) && key != prefix))
        {
          throw value.Error("a point takes a color, an outline, markers, data_labels, or several");
        }
        options.Points[category] = target;
      }
    }
    map.Done();
    return options;
  }

  private static void ValidateSeriesOptions(Chart chart)
  {
    if (chart.SeriesOptions.Count > 0 && chart.Family == "pie")
    {
      throw new LoadException("a pie's series take nothing of their own; series_colors colors its slices",
        chart.Origins["series"], "charts");
    }
    foreach ((string name, ChartSeriesOptions options) in chart.SeriesOptions)
    {
      LoadException Error(string key, string problem)
      {
        return new LoadException(problem, options.Origins.GetValueOrDefault(key) ?? chart.Origins[$"series.{name}"],
          "charts");
      }
      if (chart.Series.All(series => series.Name != name))
      {
        throw Error("", $"no series {name} in {chart.Source}; its series are " +
          string.Join(", ", chart.Series.Select(series => series.Name)));
      }
      if (options.Type == "line")
      {
        string? problem = chart.Family == "line" ? "a line chart's series are lines already"
          : chart.Family == "bar"
            ? "a line runs along the categories, which run down a bar chart; make it a column chart"
          : chart.Grouping == "percent_stacked" && options.Axis is null
            ? "a percent stacked chart's value axis shows shares; put the line on axis: secondary"
          : null;
        if (problem is not null)
        {
          throw Error("type", problem);
        }
      }
      if (options.Type == "total")
      {
        if (chart.Grouping != "stacked" || chart.Family == "line")
        {
          throw Error("type", "a total sits on a stack of columns or bars; make the chart a stacked_column or " +
            "stacked_bar");
        }
        if (options.DataLabels.Position is not null)
        {
          throw Error("data_labels.position",
            "a total's labels sit past the end of its stack, so it takes no position");
        }
      }
      string drawn = options.Type ?? chart.Family;
      foreach (string category in options.Points.Keys)
      {
        string key = $"points.{category}.";
        string? LabelKey(string label) => options.Origins.ContainsKey($"{key}data_labels.{label}")
          ? $"{key}data_labels.{label}"
          : null;
        if (drawn == "total" && options.Origins.Keys.FirstOrDefault(origin =>
          origin.StartsWith(key, StringComparison.Ordinal) && origin != key &&
          !origin.StartsWith($"{key}data_labels.", StringComparison.Ordinal)) is string unlabelled)
        {
          throw Error(unlabelled, $"a total's points take only data_labels, and {name} is drawn as a total");
        }
        if (drawn == "total" && LabelKey("position") is string totalPosition)
        {
          throw Error(totalPosition, "a total's labels sit past the end of its stack, so it takes no position");
        }
        if (options.Points[category].DataLabels.Show == "percent")
        {
          throw Error(LabelKey("show")!, "show: percent is for pie and doughnut charts; a point shows value or none");
        }
        if (drawn != "total" && options.Points[category].DataLabels.Position is string pointPosition)
        {
          string group = drawn == "line" ? "line" : Chart.LabelPositionGroup(chart.Type);
          if (!Chart.LabelPositions[group].Contains(pointPosition))
          {
            throw Error(LabelKey("position")!, drawn == "line"
              ? $"a line places labels {Join(Chart.LabelPositions["line"])}, not {pointPosition}"
              : $"a {chart.Type} places labels {Join(Chart.LabelPositions[group])}, not {pointPosition}");
          }
        }
        if (drawn == "line" && options.Points[category].Color is { IsNone: true })
        {
          throw Error($"{key}color", $"color: none is for a column or bar, and {name} is drawn as a {drawn}");
        }
        if (drawn == "line" && options.Points[category].Outline is not null)
        {
          throw Error($"{key}outline", $"an outline is for a column or bar, and {name} is drawn as a line; a " +
            "line takes a dash");
        }
        if (drawn != "line" && options.Origins.Keys.FirstOrDefault(origin =>
          origin.StartsWith($"{key}markers.", StringComparison.Ordinal)) is string pointMarker)
        {
          throw Error(pointMarker, $"a point's markers sit on a line, and {name} is drawn as a {drawn}");
        }
        if (!chart.Categories.Contains(category))
        {
          throw Error(key, $"no category {category} in {chart.Source}; its categories are " +
            string.Join(", ", chart.Categories));
        }
      }
      if (options.Axis is not null && drawn != "line")
      {
        throw Error("axis", options.Type == "total"
          ? "a total sits on its stack, so it takes no axis"
          : $"only a line takes the secondary axis; give {name} type: line");
      }
      if (options.Origins.Keys.FirstOrDefault(key => key.StartsWith("markers.", StringComparison.Ordinal))
        is string markerKey && drawn != "line")
      {
        throw Error(markerKey, drawn == "total"
          ? $"markers sit on a line's points, and {name} is a total; a total draws only its labels"
          : $"markers sit on a line's points, and {name} is drawn as {drawn}s; give it type: line");
      }
      if (options.Outline is not null && drawn is "line" or "total")
      {
        throw Error("outline", drawn == "total"
          ? $"an outline is for a column or bar, and {name} is a total; a total draws only its labels"
          : $"an outline is for a column or bar, and {name} is drawn as a line; a line takes a dash");
      }
      if (options.Dash is not null && drawn != "line")
      {
        throw Error("dash", drawn == "total"
          ? $"a dash is for a line, and {name} is a total; a total draws only its labels"
          : $"a dash is for a line, and {name} is drawn as {drawn}s; give it type: line");
      }
      if (options.DataLabels.Show == "percent")
      {
        throw Error("data_labels.show", $"show: percent is for pie and doughnut charts; a series shows value or none");
      }
      if (options.DataLabels.Position is string position && drawn == "line"
        && !Chart.LabelPositions["line"].Contains(position))
      {
        throw Error("data_labels.position", $"a line places labels {Join(Chart.LabelPositions["line"])}, not {position}");
      }
      if (options.DataLabels.Position is string barPosition && drawn != "line"
        && !Chart.LabelPositions[Chart.LabelPositionGroup(chart.Type)].Contains(barPosition))
      {
        throw Error("data_labels.position",
          $"a {chart.Type} places labels {Join(Chart.LabelPositions[Chart.LabelPositionGroup(chart.Type)])}, not {barPosition}");
      }
    }
    if (chart.Series.All(series => chart.SeriesOptions.GetValueOrDefault(series.Name)?.Axis is not null))
    {
      throw new LoadException("every series is on the secondary axis; leave one on the value axis",
        chart.SeriesOptions.Values.Last().Origins["axis"], "charts");
    }
  }

  public static bool IsoDate(string text, out DateOnly date)
  {
    return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
  }

  private static string Join(string[] choices)
  {
    return choices.Length == 1 ? choices[0] : $"{string.Join(", ", choices[..^1])}, or {choices[^1]}";
  }

  private (List<string> Categories, List<ChartSeries> Series) ReadChartData(SourceFile file, string seriesIn)
  {
    LoadException Error(int line, string problem)
    {
      return new LoadException(problem, new Origin(file.Display, line, 1), "charts");
    }
    List<CsvRow> rows = Csv.Parse(file.ReadText("charts"), Error);
    if (rows.Count < 2 || rows[0].Fields.Count < 2)
    {
      throw Error(1, "a chart's CSV needs a header and a row, and a name column and a value column");
    }
    List<string> header = rows[0].Fields;
    foreach (CsvRow row in rows.Skip(1).Where(row => row.Fields.Count != header.Count))
    {
      throw Error(row.Line, $"the row has {row.Fields.Count} cells and the header {header.Count}");
    }

    List<string> names = rows.Skip(1).Select(row => row.Fields[0]).ToList();
    List<string> columns = header.Skip(1).ToList();
    double?[,] values = new double?[names.Count, columns.Count];
    for (int row = 0; row < names.Count; row++)
    {
      for (int column = 0; column < columns.Count; column++)
      {
        string cell = rows[row + 1].Fields[column + 1];
        if (cell.Length == 0)
        {
          continue;
        }
        if (!PlainNumber.TryParse(cell, out double value))
        {
          throw Error(rows[row + 1].Line, $"{cell}, for {names[row]} in {columns[column]}, isn't a plain number: " +
            "write it without separators or symbols, and let a format spell it");
        }
        values[row, column] = value;
      }
    }

    if (seriesIn == "rows")
    {
      return (columns, names.Select((name, row) => new ChartSeries
      {
        Name = name,
        Values = Enumerable.Range(0, columns.Count).Select(column => values[row, column]).ToList()
      }).ToList());
    }
    return (names, columns.Select((name, column) => new ChartSeries
    {
      Name = name,
      Values = Enumerable.Range(0, names.Count).Select(row => values[row, column]).ToList()
    }).ToList());
  }

  /// <summary>Series from a book's lines, over the columns the chart names or the first line's, or with
  /// <c>series_in: columns</c> a series from each column over the lines; a column's label, if it has one, names its
  /// category or series. A revision draws a line or column its book no longer has as gaps rather than failing the
  /// chart, as it draws such a reference in text as <c>#REF!</c>.</summary>
  private (string Book, List<string> Lines, List<string> Columns, List<string> Categories, List<ChartSeries> Series)
    ReadChartLines(Node node, string seriesIn)
  {
    MapNode map = node.WithTopic("books").AsMap();
    Node bookNode = map.Require("book");
    Book book = FindBook(bookNode.AsString()) ?? throw bookNode.Error(UnknownBook(bookNode.AsString()));
    Node? columnsNode = map.Take("columns");
    List<Node> entries = map.Require("series").AsList();
    map.Done();
    (IReadOnlyDictionary<string, BookLine>? lines, string? problem) = BookLines(book);
    if (problem is not null)
    {
      throw bookNode.Error(problem);
    }

    var picked = new List<(BookLine Line, string Name)>();
    foreach (Node entry in entries)
    {
      MapNode seriesMap = entry.AsMap();
      Node lineNode = seriesMap.Require("line");
      string? label = seriesMap.Take("label")?.AsString();
      seriesMap.Done();
      if (!lines!.TryGetValue(lineNode.AsString(), out BookLine? line))
      {
        if (files is not GitTree)
        {
          throw lineNode.Error($"no line {lineNode.AsString()} in book {book.Id}; name it as libreta --lines lists " +
            "it, from the book's folder");
        }
        line = new BookLine(lineNode.AsString(), lineNode.AsString(), null, [], [], []);
      }
      string name = label ?? line.Label;
      if (picked.Any(other => other.Name == name))
      {
        throw entry.Error($"two series are called {name}; give one a label");
      }
      picked.Add((line, name));
    }
    if (picked.Count == 0)
    {
      throw node.Error("a chart from a book needs a line in series");
    }

    var columns = new List<(string Name, string Label, Node Node)>();
    foreach (Node entry in columnsNode?.AsList() ?? [])
    {
      Node columnNode = entry;
      string? label = null;
      if (entry.IsMap)
      {
        MapNode columnMap = entry.AsMap();
        columnNode = columnMap.Require("column");
        label = columnMap.Require("label").AsString();
        columnMap.Done();
      }
      string name = columnNode.AsString();
      if (columns.Any(other => other.Label == (label ?? name)))
      {
        throw entry.Error($"two columns are called {label ?? name}; give one a label");
      }
      columns.Add((name, label ?? name, entry));
    }
    if (columnsNode is null)
    {
      BookLine first = picked.FirstOrDefault(entry => entry.Line.Columns.Count > 0).Line ?? picked[0].Line;
      columns.AddRange(first.Columns.Select(name => (name, name, node)));
    }
    var values = new List<List<double?>>();
    foreach ((BookLine line, string _) in picked)
    {
      var row = new List<double?>();
      foreach ((string name, string _, Node columnNode) in columns)
      {
        int column = line.Columns.IndexOf(name);
        if (column < 0 && files is not GitTree)
        {
          throw columnNode.Error(NoColumn(line, name));
        }
        row.Add(column >= 0 && PlainNumber.TryParse(line.Exact[column], out double value) ? value : null);
      }
      values.Add(row);
    }
    List<string> names = picked.Select(entry => entry.Name).ToList();
    List<string> labels = columns.Select(column => column.Label).ToList();
    List<ChartSeries> series = seriesIn == "columns"
      ? labels.Select((label, column) => new ChartSeries
      {
        Name = label,
        Values = values.Select(row => row[column]).ToList()
      }).ToList()
      : names.Select((name, line) => new ChartSeries { Name = name, Values = values[line] }).ToList();
    return (book.Id, picked.Select(entry => entry.Line.Name).ToList(), columns.Select(column => column.Name).ToList(),
      seriesIn == "columns" ? names : labels, series);
  }

  private void ReadLabels(Node node, ChartLabels target, Dictionary<string, NumberFormat> formats,
    Action<string, Node> set)
  {
    MapNode labels = node.AsMap();
    if (labels.Take("show") is Node show)
    {
      target.Show = show.AsChoice("value", "percent", "none");
      set("show", show);
    }
    if (labels.Take("format") is Node format)
    {
      target.Format = NumberFormatName(format, formats, false);
      set("format", format);
    }
    if (labels.Take("position") is Node position)
    {
      target.Position = position.AsChoice(Chart.LabelPositions.Values.SelectMany(choices => choices).Distinct().ToArray());
      set("position", position);
    }
    if (labels.Take("color") is Node color)
    {
      target.Color = ReadColor(color, false);
      set("color", color);
    }
    if (labels.Take("bold") is Node bold)
    {
      target.Bold = bold.AsBoolean();
      set("bold", bold);
    }
    labels.Done();
  }

  private void ReadMarkers(Node node, ChartMarkers target, Action<string, Node> set)
  {
    MapNode markers = node.AsMap();
    if (markers.Take("shape") is Node shape)
    {
      target.Shape = shape.AsChoice(Element.MarkerShapes);
      set("shape", shape);
    }
    if (markers.Take("size") is Node size)
    {
      target.Size = size.AsNumber() is double value and >= 2 and <= 72 && value == Math.Floor(value)
        ? value
        : throw size.Error("a marker's size is a whole number of points, from 2 to 72, as in PowerPoint");
      set("size", size);
    }
    if (markers.Take("fill") is Node fill)
    {
      target.Fill = ReadColor(fill, false);
      set("fill", fill);
    }
    markers.Done();
  }

  private static void ReadValueAxis(Node node, string name, ChartValueAxis target, Func<Node, bool, string> format,
    Action<string, Node> set)
  {
    MapNode axis = node.AsMap();
    if (axis.Take("visible") is Node visible)
    {
      target.Visible = visible.AsBoolean();
      set($"{name}.visible", visible);
    }
    if (axis.Take("min") is Node min)
    {
      target.Min = min.AsNumber();
      set($"{name}.min", min);
    }
    if (axis.Take("max") is Node max)
    {
      target.Max = max.AsNumber();
      set($"{name}.max", max);
    }
    if (axis.Take("step") is Node step)
    {
      target.Step = step.AsNumber() is double value and > 0
        ? value
        : throw step.Error("a value axis's step is the distance between its ticks, more than 0");
      set($"{name}.step", step);
    }
    if (axis.Take("format") is Node formatNode)
    {
      target.Format = format(formatNode, false);
      set($"{name}.format", formatNode);
    }
    if (axis.Take("gridlines") is Node gridlines)
    {
      if (name != "value_axis")
      {
        throw gridlines.Error("gridlines follow the value axis's ticks; the secondary axis takes none");
      }
      target.Gridlines = gridlines.AsBoolean();
      set($"{name}.gridlines", gridlines);
    }
    if (axis.Take("reverse") is Node reverse)
    {
      target.Reverse = reverse.AsBoolean();
      set($"{name}.reverse", reverse);
    }
    axis.Done();
  }

  private static string NumberFormatName(Node node, Dictionary<string, NumberFormat> formats, bool date)
  {
    string name = node.AsString();
    if (!formats.TryGetValue(name, out NumberFormat? format))
    {
      throw node.Error(formats.Count == 0
        ? $"unknown format {name}; the theme has none"
        : $"unknown format {name}; the theme's formats are {string.Join(", ", formats.Keys)}");
    }
    if ((format.Date is not null) != date)
    {
      throw node.Error(date
        ? $"{name} spells numbers; a category axis takes a date format"
        : $"{name} spells dates; only a category axis takes a date format");
    }
    return name;
  }

  /// <summary>The keys a chart file, named chart style, and the theme's <c>chart</c> base share.</summary>
  private void ReadChartProperties(MapNode map, ChartProperties target, Dictionary<string, NumberFormat> formats)
  {
    void Set(string key, Node node)
    {
      target.Origins[key] = node.Origin;
    }
    string Format(Node node, bool date = false)
    {
      return NumberFormatName(node, formats, date);
    }

    if (map.Take("title") is Node title)
    {
      target.Title = title.AsString();
      Set("title", title);
    }
    if (map.Take("legend") is Node legend)
    {
      target.Legend = legend.AsChoice("bottom", "top", "left", "right", "none");
      Set("legend", legend);
    }
    if (map.Take("data_labels") is Node labelsNode)
    {
      ReadLabels(labelsNode, target.DataLabels, formats, (key, node) => Set($"data_labels.{key}", node));
    }
    if (map.Take("value_axis") is Node valueNode)
    {
      ReadValueAxis(valueNode, "value_axis", target.ValueAxis, Format, Set);
    }
    if (map.Take("secondary_axis") is Node secondaryNode)
    {
      ReadValueAxis(secondaryNode, "secondary_axis", target.SecondaryAxis, Format, Set);
    }
    if (map.Take("category_axis") is Node categoryNode)
    {
      MapNode axis = categoryNode.AsMap();
      if (axis.Take("visible") is Node visible)
      {
        target.CategoryAxis.Visible = visible.AsBoolean();
        Set("category_axis.visible", visible);
      }
      if (axis.Take("labels") is Node labels)
      {
        target.CategoryAxis.Labels = labels.AsBoolean();
        Set("category_axis.labels", labels);
      }
      if (axis.Take("format") is Node format)
      {
        target.CategoryAxis.Format = Format(format, true);
        Set("category_axis.format", format);
      }
      if (axis.Take("step") is Node step)
      {
        Match match = Regex.Match(step.AsString(), @"^([1-9][0-9]{0,8}) (day|month|year)s?$");
        target.CategoryAxis.Step = match.Success
          ? new DateStep(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value + "s")
          : throw step.Error("a category axis's step is a time, such as 7 days, 1 month, or 2 years");
        Set("category_axis.step", step);
      }
      if (axis.Take("stroke") is Node strokeNode)
      {
        MapNode stroke = strokeNode.AsMap();
        if (stroke.Take("color") is Node color)
        {
          target.CategoryAxis.Stroke.Color = ReadColor(color, false);
          Set("category_axis.stroke.color", color);
        }
        if (stroke.Take("width") is Node width)
        {
          target.CategoryAxis.Stroke.Width = width.AsNumber() is > 0 and double points
            ? points
            : throw width.Error("an axis's width is more than 0 points; hide the axis with visible: false");
          Set("category_axis.stroke.width", width);
        }
        stroke.Done();
      }
      axis.Done();
    }
    if (map.Take("series_colors") is Node colors)
    {
      List<Node> entries = colors.AsList();
      if (entries.Count == 0)
      {
        throw colors.Error("series_colors needs at least one color", "colors");
      }
      target.SeriesColors = entries.Select(entry => ReadColor(entry)).ToList();
      Set("series_colors", colors);
    }
    if (map.Take("gap_width") is Node gap)
    {
      target.GapWidth = gap.AsNumber() is double value and >= 0 and <= 500
        ? value
        : throw gap.Error("gap_width is a percent of a bar's width, from 0 to 500");
      Set("gap_width", gap);
    }
    if (map.Take("overlap") is Node overlap)
    {
      target.Overlap = overlap.AsNumber() is double value and >= -100 and <= 100
        ? value
        : throw overlap.Error("overlap is a percent of a bar's width, from -100 to 100");
      Set("overlap", overlap);
    }
    if (map.Take("first_slice_angle") is Node angle)
    {
      target.FirstSliceAngle = angle.AsNumber() is double value and >= 0 and <= 360 && value == Math.Floor(value)
        ? value
        : throw angle.Error("first_slice_angle is whole degrees clockwise from twelve o'clock, from 0 to 360");
      Set("first_slice_angle", angle);
    }
    if (map.Take("connector_lines") is Node connectors)
    {
      target.ConnectorLines = connectors.AsBoolean();
      Set("connector_lines", connectors);
    }
    if (map.Take("plot") is Node plotNode)
    {
      MapNode plot = plotNode.AsMap();
      if (plot.Take("insets") is Node insets)
      {
        target.PlotInsets = insets.AsNumbers(4, "[left, top, right, bottom] in points");
        Set("plot.insets", insets);
      }
      plot.Done();
    }
    if (map.Take("markers") is Node markersNode)
    {
      ReadMarkers(markersNode, target.Markers, (key, node) => Set($"markers.{key}", node));
    }
    if (map.Take("text") is Node textNode)
    {
      MapNode text = textNode.AsMap();
      if (text.Take("font") is Node font)
      {
        target.Text.Font = font.AsString();
        Set("text.font", font);
      }
      if (text.Take("size") is Node size)
      {
        target.Text.Size = Positive(size);
        Set("text.size", size);
      }
      if (text.Take("color") is Node color)
      {
        target.Text.Color = ReadColor(color, false);
        Set("text.color", color);
      }
      text.Done();
    }
  }


}
