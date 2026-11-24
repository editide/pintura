namespace Pintura;

public partial class PresentationLoader
{
  private Theme LoadTheme(string themeRoot)
  {
    Node document = File(Path.Combine(themeRoot, "theme.yaml")).Load("themes");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    string name = map.Require("name").AsString();
    var colors = new Dictionary<string, string>();
    MapNode colorMap = map.Require("colors").AsMap();
    foreach (string key in Color.ThemeColors)
    {
      Node value = colorMap.Take(key) ?? throw colorMap.Node.Error(
        $"colors needs all twelve theme colors, and {key} is missing", "colors");
      Color color = Color.Parse(value.AsString(), _ => false, problem => value.Error(problem, "colors"));
      if (color.Hex is null)
      {
        throw value.Error($"theme color {key} must be a hex value such as \"#1F4E79\"", "colors");
      }
      colors[key] = color.Hex;
      origins[$"colors.{key}"] = value.Origin;
    }
    colorMap.Done();

    if (map.Take("named_colors") is Node namedNode)
    {
      foreach ((string key, Node value) in namedNode.AsMap().Entries())
      {
        if (Color.ThemeColors.Contains(key) || key == "none")
        {
          throw value.Error($"{key} is already a color name", "colors");
        }
        Color color = Color.Parse(value.AsString(), _ => false, problem => value.Error(problem, "colors"));
        if (color.IsNone)
        {
          throw value.Error($"named color {key} can't be none", "colors");
        }
        namedColors[key] = color;
        origins[$"named_colors.{key}"] = value.Origin;
      }
    }

    var fonts = new Dictionary<string, string>();
    MapNode fontMap = map.Require("fonts").AsMap();
    foreach (string key in new[] { "heading", "body" })
    {
      Node value = fontMap.Require(key);
      fonts[key] = value.AsString();
      origins[$"fonts.{key}"] = value.Origin;
    }
    fontMap.Done();

    styles = LoadStyles(Path.Combine(themeRoot, "styles.yaml"));

    Color background = new Color { Theme = "light1" };
    if (map.Take("background") is Node backgroundNode)
    {
      background = ReadColor(backgroundNode, false);
      origins["background"] = backgroundNode.Origin;
    }

    Dictionary<string, Area> areas = ReadAreas(map.Take("areas"), new Dictionary<string, Area>());
    Placeholder? title = null;
    if (map.Take("title") is Node titleNode)
    {
      if (titleNode.IsNone)
      {
        throw titleNode.Error("the theme's title can't be none; remove it on a layout with title: none");
      }
      title = ReadPlaceholder(titleNode, areas);
      RequireFrame(title, titleNode);
    }
    Dictionary<string, Placeholder> placeholders = ReadPlaceholders(map.Take("placeholders"), areas, null);
    ThemeChecks checks = ReadChecks(map.Take("checks"));
    map.Done();

    var layouts = new Dictionary<string, Layout>(StringComparer.Ordinal);
    string layoutFolder = Path.Combine(themeRoot, "layouts");
    foreach (string path in files.Files(layoutFolder, "*.yaml").Order(StringComparer.Ordinal))
    {
      Layout layout = LoadLayout(path, areas, title, placeholders);
      layouts[layout.Id] = layout;
    }

    return new Theme
    {
      Name = name,
      Colors = colors,
      NamedColors = namedColors,
      Fonts = fonts,
      Faces = ThemeFonts.Read(Path.Combine(themeRoot, "fonts"), files),
      Background = background,
      Title = title,
      Placeholders = placeholders,
      Areas = areas,
      Styles = styles,
      Layouts = layouts,
      Checks = checks,
      Root = themeRoot,
      Display = SourceFile.For(themeRoot, displayBase).Display,
      Origins = origins
    };
  }

  private static ThemeChecks ReadChecks(Node? node)
  {
    if (node is null)
    {
      return new ThemeChecks();
    }
    MapNode map = node.WithTopic("checks").AsMap();
    double? shrink = null;
    if (map.Take("shrink") is Node shrinkNode)
    {
      shrink = shrinkNode.AsNumber() is > 0 and <= 100 and double percent
        ? percent
        : throw shrinkNode.Error("shrink is a percent, more than 0 and at most 100");
    }
    map.Done();
    return new ThemeChecks { Shrink = shrink };
  }

  private StyleSheet LoadStyles(string path)
  {
    Node document = File(path).Load("styles");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    var text = new TextProperties();
    var levels = new Dictionary<int, Level>();
    MapNode textMap = map.Require("text").AsMap();
    ReadTextProperties(textMap, text, TextProperties.All);
    if (textMap.Take("levels") is Node levelsNode)
    {
      foreach ((string key, Node value) in levelsNode.AsMap().Entries())
      {
        if (!int.TryParse(key, out int number) || number != levels.Count + 1)
        {
          throw value.Error("levels are numbered 1, 2, 3, and so on, in order");
        }
        levels[number] = ReadLevel(value, true);
      }
    }
    textMap.Done();
    foreach (string required in new[] { "font", "size", "color" })
    {
      if (!text.Origins.ContainsKey(required))
      {
        throw textMap.Node.Error($"text needs {required}: every default is in the theme");
      }
    }

    var shapeText = new TextProperties();
    if (map.Take("shape_text") is Node shapeTextNode)
    {
      MapNode shapeTextMap = shapeTextNode.AsMap();
      ReadTextProperties(shapeTextMap, shapeText, TextProperties.All);
      shapeTextMap.Done();
    }

    Color? shapeFill = null;
    Stroke? shapeStroke = null;
    Shadow? shapeShadow = null;
    if (map.Take("shape") is Node shapeNode)
    {
      MapNode shapeMap = shapeNode.AsMap();
      if (shapeMap.Take("fill") is Node fill)
      {
        shapeFill = ReadColor(fill);
        origins["shape.fill"] = fill.Origin;
      }
      if (shapeMap.Take("stroke") is Node stroke)
      {
        shapeStroke = ReadStroke(stroke);
      }
      if (shapeMap.Take("shadow") is Node shadow)
      {
        shapeShadow = ReadShadow(shadow);
      }
      shapeMap.Done();
    }

    string? pictureFit = null;
    if (map.Take("picture") is Node pictureNode)
    {
      MapNode pictureMap = pictureNode.AsMap();
      if (pictureMap.Take("fit") is Node fit)
      {
        pictureFit = fit.AsChoice("contain", "cover", "stretch");
        origins["picture.fit"] = fit.Origin;
      }
      pictureMap.Done();
    }

    Stroke? lineStroke = null;
    if (map.Take("line") is Node lineNode)
    {
      MapNode lineMap = lineNode.AsMap();
      if (lineMap.Take("stroke") is Node stroke)
      {
        lineStroke = ReadStroke(stroke);
      }
      lineMap.Done();
    }

    var named = new Dictionary<string, TextProperties>(StringComparer.Ordinal);
    var styleLevels = new Dictionary<string, Dictionary<int, Level>>(StringComparer.Ordinal);
    var styleShapes = new Dictionary<string, StyleShape>(StringComparer.Ordinal);
    if (map.Take("styles") is Node stylesNode)
    {
      foreach ((string key, Node value) in stylesNode.AsMap().Entries())
      {
        var properties = new TextProperties();
        MapNode styleMap = value.AsMap();
        ReadTextProperties(styleMap, properties, TextProperties.Named);
        var shapeOrigins = new Dictionary<string, Origin>();
        Node? TakeShape(string shapeKey)
        {
          Node? found = styleMap.Take(shapeKey);
          if (found is not null)
          {
            shapeOrigins[shapeKey] = found.Origin;
          }
          return found;
        }
        var look = new StyleShape
        {
          Fill = TakeShape("fill") is Node fill ? ReadColor(fill) : null,
          Stroke = TakeShape("stroke") is Node stroke ? ReadStroke(stroke) : null,
          Shadow = TakeShape("shadow") is Node shadow ? ReadShadow(shadow) : null,
          Opacity = TakeShape("opacity") is Node opacity ? ReadOpacity(opacity) : null,
          Origins = shapeOrigins
        };
        if (shapeOrigins.Count > 0)
        {
          styleShapes[key] = look;
        }
        if (styleMap.Take("levels") is Node styleLevelsNode)
        {
          var own = new Dictionary<int, Level>();
          foreach ((string number, Node levelNode) in styleLevelsNode.AsMap().Entries())
          {
            if (!int.TryParse(number, out int level) || !levels.ContainsKey(level))
            {
              throw levelNode.Error(levels.Count == 0
                ? "the theme's text has no levels for a style to change"
                : $"the theme's text has levels 1 to {levels.Count}");
            }
            own[level] = ReadLevel(levelNode, false);
          }
          styleLevels[key] = own;
        }
        styleMap.Done();
        named[key] = properties;
        origins[$"styles.{key}"] = value.Origin;
      }
    }

    var formats = new Dictionary<string, NumberFormat>(StringComparer.Ordinal);
    if (map.Take("formats") is Node formatsNode)
    {
      foreach ((string key, Node value) in formatsNode.AsMap().Entries())
      {
        formats[key] = ReadFormat(value, named);
        origins[$"formats.{key}"] = value.Origin;
      }
    }

    var chart = new ChartProperties();
    if (map.Take("chart") is Node chartNode)
    {
      MapNode chartMap = chartNode.WithTopic("charts").AsMap();
      ReadChartProperties(chartMap, chart, formats);
      chartMap.Done();
    }
    var chartStyles = new Dictionary<string, ChartProperties>(StringComparer.Ordinal);
    if (map.Take("charts") is Node chartsNode)
    {
      foreach ((string key, Node value) in chartsNode.WithTopic("charts").AsMap().Entries())
      {
        MapNode styleMap = value.AsMap();
        chartStyles[key] = new ChartProperties();
        ReadChartProperties(styleMap, chartStyles[key], formats);
        styleMap.Done();
        origins[$"charts.{key}"] = value.Origin;
      }
    }

    var tableStyles = new Dictionary<string, TableStyle>(StringComparer.Ordinal);
    if (map.Take("tables") is Node tablesNode)
    {
      foreach ((string key, Node value) in tablesNode.WithTopic("tables").AsMap().Entries())
      {
        tableStyles[key] = ReadTableStyle(value, named);
        origins[$"tables.{key}"] = value.Origin;
      }
    }
    map.Done();

    return new StyleSheet
    {
      Text = text,
      Levels = levels,
      ShapeText = shapeText,
      ShapeFill = shapeFill,
      ShapeStroke = shapeStroke,
      ShapeShadow = shapeShadow,
      PictureFit = pictureFit,
      LineStroke = lineStroke,
      Styles = named,
      StyleLevels = styleLevels,
      StyleShapes = styleShapes,
      Formats = formats,
      Chart = chart,
      Charts = chartStyles,
      Tables = tableStyles,
      Origins = origins
    };
  }

  private TableStyle ReadTableStyle(Node node, Dictionary<string, TextProperties> named)
  {
    var rows = new Dictionary<string, TableFormatting>(StringComparer.Ordinal);
    Color? banded = null;
    double? indent = null;
    var origins = new Dictionary<string, Origin>();
    foreach ((string key, Node value) in node.AsMap().Entries())
    {
      if (key == "banded")
      {
        banded = ReadColor(value, false);
        origins["banded"] = value.Origin;
        continue;
      }
      if (key == "indent")
      {
        indent = value.AsNumber() is >= 0 and double step
          ? step
          : throw value.Error("indent is how far in a view row's label sits per step, 0 or more");
        origins["indent"] = value.Origin;
        continue;
      }
      MapNode map = value.AsMap();
      rows[key] = ReadTableFormatting(map, "row style", named, null);
      map.Done();
    }
    return new TableStyle { Rows = rows, Banded = banded, Indent = indent, Origins = origins };
  }

  /// <summary>Reads what a row style, column, row, or cell sets; <paramref name="what"/> decides which keys it takes.
  /// A row names its row style, which the caller checks, and a column or cell a text style.</summary>
  private TableFormatting ReadTableFormatting(MapNode map, string what, Dictionary<string, TextProperties> named,
    Dictionary<string, NumberFormat>? formats)
  {
    var origins = new Dictionary<string, Origin>();
    Node? Take(string key)
    {
      Node? value = map.Take(key);
      if (value is not null)
      {
        origins[key] = value.Origin;
      }
      return value;
    }

    string? style = null;
    if (what != "row style" && Take("style") is Node styleNode)
    {
      style = styleNode.AsString();
      if (what != "row" && !named.ContainsKey(style))
      {
        throw styleNode.Error($"unknown style {style}", "styles");
      }
    }
    string? format = null;
    if (formats is not null && Take("format") is Node formatNode)
    {
      format = formatNode.AsString();
      if (!formats.ContainsKey(format))
      {
        throw formatNode.Error(formats.Count == 0
          ? $"unknown format {format}; the theme has none"
          : $"unknown format {format}; the theme's formats are {string.Join(", ", formats.Keys)}", "styles");
      }
    }
    Color? fill = what != "column" && Take("fill") is Node fillNode ? ReadColor(fillNode) : null;
    bool across = what != "column";
    bool down = what is "column" or "cell";
    Stroke? top = across && Take("border_top") is Node topNode ? ReadStroke(topNode) : null;
    Stroke? bottom = across && Take("border_bottom") is Node bottomNode ? ReadStroke(bottomNode) : null;
    Stroke? left = down && Take("border_left") is Node leftNode ? ReadStroke(leftNode) : null;
    Stroke? right = down && Take("border_right") is Node rightNode ? ReadStroke(rightNode) : null;
    var properties = new TextProperties();
    ReadTextProperties(map, properties, TableFormatting.Text);
    return what switch
    {
      "column" => new TableColumn
      {
        Width = Take("width") is Node width ? Positive(width) : null,
        Style = style, Format = format, BorderLeft = left, BorderRight = right, Properties = properties,
        Origin = map.Origin, Origins = origins
      },
      "row" => new TableRow
      {
        Height = Take("height") is Node height ? Positive(height) : null,
        Style = style, Format = format, Fill = fill, BorderTop = top, BorderBottom = bottom, Properties = properties,
        Origin = map.Origin, Origins = origins
      },
      "cell" => new TableCell
      {
        Row = map.Require("row").AsInteger(),
        Column = map.Require("column").AsInteger(),
        Style = style, Format = format, Fill = fill, BorderTop = top, BorderBottom = bottom, BorderLeft = left,
        BorderRight = right, Properties = properties, Origin = map.Origin, Origins = origins
      },
      _ => new TableFormatting
      {
        Fill = fill, BorderTop = top, BorderBottom = bottom, Properties = properties, Origin = map.Origin,
        Origins = origins
      }
    };
  }

  /// <summary>A theme level takes text properties too; a named style's level only its bullets and indents.</summary>
  private Level ReadLevel(Node node, bool text)
  {
    MapNode map = node.AsMap();
    var origins = new Dictionary<string, Origin>();
    Node? Take(string key)
    {
      Node? value = map.Take(key);
      if (value is not null)
      {
        origins[key] = value.Origin;
      }
      return value;
    }

    string? bullet = Take("bullet")?.AsString();
    Color? bulletColor = Take("bullet_color") is Node colorNode ? ReadColor(colorNode, false) : null;
    double? bulletSize = Take("bullet_size") is Node sizeNode ? Positive(sizeNode) : null;
    double? indent = Take("indent")?.AsNumber();
    double? hanging = null;
    if (Take("hanging") is Node hangingNode)
    {
      hanging = hangingNode.AsNumber() is >= 0 and double value
        ? value
        : throw hangingNode.Error("hanging is how far the bullet sits left of the text, 0 or more");
    }
    var properties = new TextProperties();
    if (text)
    {
      ReadTextProperties(map, properties, [.. TextProperties.Character, .. TextProperties.Paragraph]);
    }
    map.Done();
    return new Level
    {
      Bullet = bullet, BulletColor = bulletColor, BulletSize = bulletSize, Indent = indent, Hanging = hanging,
      Properties = properties, Origins = origins
    };
  }

  private static NumberFormat ReadFormat(Node node, Dictionary<string, TextProperties> named)
  {
    MapNode map = node.AsMap();
    if (map.Take("date") is Node date)
    {
      string pattern = date.AsString();
      if (!pattern.Any(letter => letter is 'Y' or 'M' or 'D'))
      {
        throw date.Error("a date pattern spells a date with YYYY, YY, MMMM, MMM, MM, M, DD, or D");
      }
      if (map.Entries().FirstOrDefault(entry => entry.Key != "date") is (string other, Node otherNode))
      {
        throw otherNode.Error($"a date format takes only date, not {other}");
      }
      return new NumberFormat { Date = pattern };
    }
    string? negativeStyle = map.Take("negative_style") is Node styleNode ? styleNode.AsString() : null;
    if (negativeStyle is not null && !named.ContainsKey(negativeStyle))
    {
      throw map.Node.Error($"unknown style {negativeStyle}");
    }
    var format = new NumberFormat
    {
      Scale = map.Take("scale")?.AsNumber(),
      Decimals = map.Take("decimals")?.AsInteger(0, 15),
      Separator = map.Take("separator")?.AsBoolean(),
      Prefix = map.Take("prefix")?.AsString(),
      Suffix = map.Take("suffix")?.AsString(),
      Negative = map.Take("negative")?.AsChoice("parentheses", "minus"),
      NegativeStyle = negativeStyle
    };
    map.Done();
    return format;
  }

  private static double ReadOpacity(Node node)
  {
    return node.AsNumber() is double value and >= 0 and <= 100
      ? value
      : throw node.Error("opacity runs from 0 to 100", "colors");
  }

  private Stroke ReadStroke(Node node)
  {
    var stroke = new Stroke();
    if (node.IsNone)
    {
      stroke.None = true;
      stroke.Origins["none"] = node.Origin;
      return stroke;
    }
    MapNode map = node.AsMap();
    if (map.Take("color") is Node color)
    {
      stroke.Color = ReadColor(color, false);
      stroke.Origins["color"] = color.Origin;
    }
    if (map.Take("width") is Node width)
    {
      stroke.Width = width.AsNumber() is >= 0 and double points
        ? points
        : throw width.Error($"{width.Name} must be 0 or more");
      stroke.Origins["width"] = width.Origin;
    }
    if (map.Take("dash") is Node dash)
    {
      stroke.Dash = dash.AsChoice("solid", "dash", "dot");
      stroke.Origins["dash"] = dash.Origin;
    }
    map.Done();
    return stroke;
  }

  private Shadow ReadShadow(Node node)
  {
    var shadow = new Shadow();
    if (node.IsNone)
    {
      shadow.None = true;
      shadow.Origins["none"] = node.Origin;
      return shadow;
    }
    MapNode map = node.AsMap();
    if (map.Take("color") is Node color)
    {
      shadow.Color = ReadColor(color, false);
      shadow.Origins["color"] = color.Origin;
    }
    if (map.Take("opacity") is Node opacity)
    {
      shadow.Opacity = ReadOpacity(opacity);
      shadow.Origins["opacity"] = opacity.Origin;
    }
    double? Points(string key)
    {
      if (map.Take(key) is not Node points)
      {
        return null;
      }
      shadow.Origins[key] = points.Origin;
      return points.AsNumber() is >= 0 and double value ? value : throw points.Error($"{points.Name} must be 0 or more");
    }

    shadow.Blur = Points("blur");
    shadow.Distance = Points("distance");
    if (map.Take("angle") is Node angle)
    {
      shadow.Angle = angle.AsNumber() is >= 0 and < 360 and double degrees
        ? degrees
        : throw angle.Error($"{angle.Name} must be 0 or more and under 360");
      shadow.Origins["angle"] = angle.Origin;
    }
    map.Done();
    return shadow;
  }

  private void ReadTextProperties(MapNode map, TextProperties target, string[] allowed)
  {
    foreach (string key in allowed)
    {
      if (map.Take(key) is not Node node)
      {
        continue;
      }
      switch (key)
      {
        case "font":
          target.Font = node.AsString();
          break;
        case "size":
          target.Size = Positive(node);
          break;
        case "color":
          target.Color = ReadColor(node, false);
          break;
        case "bold":
          target.Bold = node.AsBoolean();
          break;
        case "italic":
          target.Italic = node.AsBoolean();
          break;
        case "underline":
          target.Underline = node.AsBoolean();
          break;
        case "align":
          target.Align = node.AsChoice("left", "center", "right", "justify");
          break;
        case "valign":
          target.Valign = node.AsChoice("top", "middle", "bottom");
          break;
        case "space_before":
          target.SpaceBefore = node.AsNumber();
          break;
        case "space_after":
          target.SpaceAfter = node.AsNumber();
          break;
        case "line_spacing":
          target.LineSpacing = Positive(node);
          break;
        case "insets":
          target.Insets = node.AsNumbers(4, "[left, top, right, bottom] in points");
          break;
        case "fit":
          target.Fit = node.AsChoice("none", "grow", "shrink");
          break;
        case "wrap":
          target.Wrap = node.AsBoolean();
          break;
        case "columns":
          target.Columns = node.AsInteger() is int columns and >= 1
            ? columns
            : throw node.Error("columns must be 1 or more");
          break;
      }
      target.Origins[key] = node.Origin;
    }
  }

  private static double Positive(Node node)
  {
    double value = node.AsNumber();
    return value > 0 ? value : throw node.Error($"{node.Name} must be more than 0");
  }


}
