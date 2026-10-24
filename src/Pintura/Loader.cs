using System.Globalization;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>Reads a presentation folder and its theme into the model, rejecting unknown keys and unknown names with
/// the file, line, and docs topic. A slide that fails keeps its error and the rest of the presentation loads.
/// </summary>
public partial class PresentationLoader
{
  public static readonly string[] Shapes =
  [
    "rectangle", "rounded_rectangle", "ellipse", "triangle", "right_triangle", "diamond", "parallelogram",
    "trapezoid", "pentagon", "hexagon", "chevron", "home_plate", "arrow", "brace", "bracket", "callout"
  ];

  public static readonly string[] DirectionalShapes =
    ["triangle", "right_triangle", "chevron", "home_plate", "arrow", "brace", "bracket"];

  public static readonly string[] Markers = ["arrow", "triangle", "stealth", "diamond", "oval"];
  public static readonly string[] MarkerShapes = ["circle", "square", "diamond", "triangle", "none"];

  private static readonly Dictionary<string, string[]> Adjustments = new Dictionary<string, string[]>
  {
    ["rounded_rectangle"] = ["corner"],
    ["chevron"] = ["point"],
    ["home_plate"] = ["point"],
    ["arrow"] = ["point"],
    ["parallelogram"] = ["slant"],
    ["trapezoid"] = ["slant"]
  };

  public static readonly string[] BuiltInFields = ["slide", "section"];

  private readonly string root;
  private readonly string displayBase;
  private readonly IBookReader reader;
  private readonly string bookBase;
  private string? themeRoot;
  private Dictionary<string, Field> fields = new Dictionary<string, Field>();
  private readonly Dictionary<string, Book> books = new Dictionary<string, Book>(StringComparer.Ordinal);
  private readonly Dictionary<string, BookValue> references = new Dictionary<string, BookValue>(StringComparer.Ordinal);
  private Dictionary<string, Color> namedColors = new Dictionary<string, Color>();
  private StyleSheet styles = new StyleSheet();
  private readonly Dictionary<string, Chart> charts = new Dictionary<string, Chart>(StringComparer.Ordinal);
  private readonly Dictionary<string, Table> tables = new Dictionary<string, Table>(StringComparer.Ordinal);

  private PresentationLoader(string root, string displayBase, IBookReader reader, string bookBase)
  {
    this.root = root;
    this.displayBase = displayBase;
    this.reader = reader;
    this.bookBase = bookBase;
  }

  /// <summary>Loads the presentation at <paramref name="root"/>, finding the books it quotes from
  /// <paramref name="bookBase"/>, the presentation's own folder unless a revision's copy of it is elsewhere.</summary>
  public static Presentation Load(string root, string? displayBase = null, IBookReader? books = null,
    string? bookBase = null)
  {
    string full = Path.GetFullPath(root);
    return new PresentationLoader(full, displayBase ?? Directory.GetCurrentDirectory(),
      (books ?? LibretaReader.Shared).Session(), Path.GetFullPath(bookBase ?? full)).LoadPresentation();
  }

  private SourceFile File(string path)
  {
    return SourceFile.For(Path.GetFullPath(path), displayBase);
  }

  private Presentation LoadPresentation()
  {
    Node document = File(Path.Combine(root, "presentation.yaml")).Load("presentations");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    string name = map.Take("name")?.AsString() ?? "";
    Node themeNode = map.Require("theme");
    origins["theme"] = themeNode.Origin;
    double[] size = [960, 540];
    if (map.Take("size") is Node sizeNode)
    {
      size = sizeNode.AsNumbers(2, "[width, height] in points");
      if (size.Any(length => length <= 0))
      {
        throw sizeNode.Error("size must be [width, height] in points, both more than 0");
      }
      origins["size"] = sizeNode.Origin;
    }
    if (map.Take("fields") is Node fieldsNode)
    {
      foreach ((string key, Node value) in fieldsNode.AsMap().Entries())
      {
        if (BuiltInFields.Contains(key))
        {
          throw value.Error($"{key} is a built-in field and can't be defined");
        }
        fields[key] = new Field { Value = value.AsString(), Origin = value.Origin };
      }
    }
    if (map.Take("books") is Node booksNode)
    {
      ReadBooks(booksNode);
      AskBooks();
    }
    Node slidesNode = map.Require("slides");
    map.Done();

    themeRoot = Path.GetFullPath(Path.Combine(root, themeNode.AsString()));
    if (!System.IO.File.Exists(Path.Combine(themeRoot, "theme.yaml")))
    {
      throw themeNode.Error($"no theme.yaml in {themeNode.AsString()}", "themes");
    }
    Theme theme = LoadTheme(themeRoot);

    var sections = new List<Section>();
    var slides = new List<Slide>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    Section current = new Section();
    sections.Add(current);
    foreach (Node entry in slidesNode.AsList())
    {
      if (entry.IsMap)
      {
        MapNode sectionMap = entry.AsMap();
        Node sectionNode = sectionMap.Require("section");
        sectionMap.Done();
        current = new Section { Name = sectionNode.AsString(), Origin = sectionNode.Origin };
        sections.Add(current);
        continue;
      }
      string stem = entry.AsString();
      if (stem.EndsWith(".yaml", StringComparison.Ordinal) || !IsSlideStem(stem))
      {
        string written = Path.GetFileNameWithoutExtension(stem.Replace('\\', '/').Split('/')[^1]);
        throw entry.Error($"list slides by file stem: write {written}");
      }
      if (!seen.Add(stem))
      {
        throw entry.Error($"slide {stem} is listed twice");
      }
      current.Slides.Add(stem);
      slides.Add(LoadSlide(theme, stem, slides.Count + 1, current.Name, entry));
    }
    if (sections[0].Slides.Count == 0)
    {
      sections.RemoveAt(0);
    }

    return new Presentation
    {
      Name = name,
      Size = size,
      Fields = fields,
      Books = books,
      References = references,
      Theme = theme,
      Sections = sections,
      Slides = slides,
      Charts = charts,
      Tables = tables,
      Comments = CommentFile.Load(root, displayBase),
      Root = root,
      Origin = document.Origin,
      Origins = origins
    };
  }

  private string? FieldProblem(string name)
  {
    if (Reference.Looks(name))
    {
      return ReferenceProblem(Reference.Parse(name)!);
    }
    return BuiltInFields.Contains(name) || fields.ContainsKey(name)
      ? null
      : $"unknown field {name}; define it in presentation.yaml fields";
  }

  private bool IsNamedColor(string name)
  {
    return namedColors.ContainsKey(name);
  }

  private MarkupContext Markup()
  {
    return new MarkupContext(styles.Styles.ContainsKey, FieldProblem, IsNamedColor, styles.Levels.Count);
  }

  private Color ReadColor(Node node, bool allowNone = true)
  {
    Color color = Color.Parse(node.AsString(), IsNamedColor, problem => node.Error($"{node.Name}: {problem}",
      "colors"));
    if (color.IsNone && !allowNone)
    {
      throw node.Error($"{node.Name} can't be none", "colors");
    }
    return color;
  }

  #region Books

  private void ReadBooks(Node node)
  {
    foreach (Node entry in node.WithTopic("books").AsList())
    {
      MapNode map = entry.AsMap();
      Node idNode = map.Require("id");
      string id = idNode.AsString();
      if (!BookId().IsMatch(id))
      {
        throw idNode.Error($"{id} isn't a book id; use letters, digits, _, and -, starting with a letter");
      }
      if (books.ContainsKey(id))
      {
        throw idNode.Error($"book {id} is listed twice");
      }
      Node pathNode = map.Require("path");
      map.Done();
      string path = pathNode.AsString();
      string bookRoot = Path.GetFullPath(Path.Combine(bookBase, path));
      if (!System.IO.File.Exists(Path.Combine(bookRoot, "book.yaml")))
      {
        throw pathNode.Error($"no book.yaml in {path}; paths are relative to presentation.yaml");
      }
      books[id] = new Book { Id = id, Path = path, Root = bookRoot, Origin = entry.Origin };
    }
  }

  private Book? FindBook(string id)
  {
    return books.GetValueOrDefault(id);
  }

  /// <summary>Asks Libreta for each book's lines and every view a table names at once, since each answer costs a
  /// process and a load of the book, and a reader keeps them for the load that follows. Failures wait for that load,
  /// which reports them where they're read.</summary>
  private void AskBooks()
  {
    string tables = Path.Combine(root, "tables");
    IEnumerable<(Book Book, string? View)> views = Directory.Exists(tables)
      ? Directory.EnumerateFiles(tables, "*.yaml")
        .SelectMany(file => ViewNamed().Matches(TextOrNothing(file)))
        .Select(match => (Book: books.GetValueOrDefault(match.Groups["book"].Value),
          View: (string?)match.Groups["view"].Value))
        .Where(entry => entry.Book is not null)
        .Select(entry => (entry.Book!, entry.View))
      : [];
    List<(Book Book, string? View)> questions =
      [.. books.Values.Select(book => (book, (string?)null)), .. views.Distinct()];
    Parallel.ForEach(questions, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, question =>
    {
      try
      {
        if (question.View is string view)
        {
          reader.View(question.Book.Root, view);
        }
        else
        {
          reader.Lines(question.Book.Root);
        }
      }
      catch (BookException)
      {
      }
    });
  }

  private string TextOrNothing(string file)
  {
    try
    {
      return File(file).ReadText("tables");
    }
    catch (LoadException)
    {
      return "";
    }
  }

  [GeneratedRegex(@"^\s*view:\s*[""']?(?<book>[A-Za-z][A-Za-z0-9_-]*)::(?<view>[^""'\s#]+)", RegexOptions.Multiline)]
  private static partial Regex ViewNamed();

  private string UnknownBook(string id)
  {
    return books.Count == 0
      ? $"unknown book {id}; include it under books in presentation.yaml"
      : $"unknown book {id}; the presentation's books are {And([.. books.Keys])}";
  }

  /// <summary>The book's lines, or why they can't be read, which fails only what reads them.</summary>
  private (IReadOnlyDictionary<string, BookLine>? Lines, string? Problem) BookLines(Book book)
  {
    try
    {
      return (reader.Lines(book.Root), null);
    }
    catch (BookException exception)
    {
      return (null, $"book {book.Id} can't be read: {exception.Message}");
    }
  }

  /// <summary>Resolves the reference into <see cref="references"/>, or says why it doesn't resolve.</summary>
  private string? ReferenceProblem(Reference reference)
  {
    string name = reference.ToString();
    if (references.ContainsKey(name))
    {
      return null;
    }
    if (FindBook(reference.Book) is not Book book)
    {
      return UnknownBook(reference.Book);
    }
    NumberFormat? format = null;
    if (reference.Format is string formatName)
    {
      format = styles.Formats.GetValueOrDefault(formatName);
      if (format is null)
      {
        return $"unknown format {formatName}; the theme's formats are {And([.. styles.Formats.Keys])}";
      }
      if (format.Date is not null)
      {
        return $"{formatName} spells dates, and a reference reads a number";
      }
    }
    (IReadOnlyDictionary<string, BookLine>? lines, string? problem) = BookLines(book);
    if (problem is not null)
    {
      return problem;
    }
    if (!lines!.TryGetValue(reference.Line, out BookLine? line))
    {
      return $"no line {reference.Line} in book {book.Id}; name it as libreta --lines lists it, from the book's " +
        "folder";
    }
    int column = line.Columns.IndexOf(reference.Column);
    if (column < 0)
    {
      return $"{reference.Line} has no column {reference.Column}; its columns are {And([.. line.Columns])}";
    }
    if (line.Exact[column] is null)
    {
      return $"{reference.Line} has no value in {reference.Column}";
    }
    references[name] = new BookValue
    {
      Text = Reference.Spell(line, column, format)!,
      Book = book.Id,
      Line = reference.Line,
      Column = reference.Column,
      Origin = book.Origin.At(name)
    };
    return null;
  }

  [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]*$")]
  private static partial Regex BookId();

  #endregion

  #region Themes and styles

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
    if (Directory.Exists(layoutFolder))
    {
      foreach (string path in Directory.GetFiles(layoutFolder, "*.yaml").Order(StringComparer.Ordinal))
      {
        Layout layout = LoadLayout(path, areas, title, placeholders);
        layouts[layout.Id] = layout;
      }
    }

    return new Theme
    {
      Name = name,
      Colors = colors,
      NamedColors = namedColors,
      Fonts = fonts,
      Faces = ThemeFonts.Read(Path.Combine(themeRoot, "fonts")),
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

  #endregion

  #region Layouts, areas, and placeholders

  private Layout LoadLayout(
    string path,
    Dictionary<string, Area> themeAreas,
    Placeholder? themeTitle,
    Dictionary<string, Placeholder> themePlaceholders)
  {
    Node document = File(path).Load("layouts");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();
    string id = Path.GetFileNameWithoutExtension(path);
    string name = map.Require("name").AsString();

    Color? background = null;
    if (map.Take("background") is Node backgroundNode)
    {
      background = ReadColor(backgroundNode, false);
      origins["background"] = backgroundNode.Origin;
    }

    Dictionary<string, Area> areas = ReadAreas(map.Take("areas"), themeAreas);
    Placeholder? title = null;
    bool titleRemoved = false;
    if (map.Take("title") is Node titleNode)
    {
      origins["title"] = titleNode.Origin;
      if (titleNode.IsNone)
      {
        titleRemoved = true;
      }
      else
      {
        title = ReadPlaceholder(titleNode, areas);
        if (themeTitle is null)
        {
          RequireFrame(title, titleNode);
        }
      }
    }
    Dictionary<string, Placeholder> placeholders = ReadPlaceholders(map.Take("placeholders"), areas,
      themePlaceholders);
    List<Element> elements = ReadElements(map.Take("elements"), areas, path, "layout");
    map.Done();

    return new Layout
    {
      Id = id,
      Name = name,
      Background = background,
      Title = title,
      TitleRemoved = titleRemoved,
      Placeholders = placeholders,
      Areas = areas.Where(pair => !themeAreas.TryGetValue(pair.Key, out Area? inherited) || inherited != pair.Value)
        .ToDictionary(),
      Elements = elements,
      Origins = origins
    };
  }

  /// <summary>Areas added or replaced by name over <paramref name="inherited"/>, returning the combined set.
  /// </summary>
  private static Dictionary<string, Area> ReadAreas(Node? node, Dictionary<string, Area> inherited)
  {
    var areas = new Dictionary<string, Area>(inherited, StringComparer.Ordinal);
    if (node is null)
    {
      return areas;
    }
    foreach ((string key, Node value) in node.AsMap().Entries())
    {
      areas[key] = new Area { Rect = Rect(value), Origin = value.Origin };
    }
    return areas;
  }

  private static double[] Rect(Node node)
  {
    double[] rect = node.AsNumbers(4, "[x, y, w, h] in points");
    if (rect[2] < 0 || rect[3] < 0)
    {
      throw node.Error($"{node.Name} has a negative width or height");
    }
    return rect;
  }

  private static Frame ReadFrame(Node node, Dictionary<string, Area> areas)
  {
    if (node.IsScalar)
    {
      string area = node.AsString();
      if (!areas.TryGetValue(area, out Area? found))
      {
        throw node.Error(areas.Count == 0
          ? $"unknown area {area}; this theme has none"
          : $"unknown area {area}; areas here are {string.Join(", ", areas.Keys)}");
      }
      return new Frame { Rect = found.Rect, Area = area };
    }
    return new Frame { Rect = Rect(node) };
  }

  private Dictionary<string, Placeholder> ReadPlaceholders(
    Node? node,
    Dictionary<string, Area> areas,
    Dictionary<string, Placeholder>? inherited)
  {
    var placeholders = new Dictionary<string, Placeholder>(StringComparer.Ordinal);
    if (node is null)
    {
      return placeholders;
    }
    foreach ((string key, Node value) in node.AsMap().Entries())
    {
      if (key == "title")
      {
        throw value.Error("the title has its own key: write title: rather than a title placeholder");
      }
      placeholders[key] = ReadPlaceholder(value, areas);
      if (inherited?.ContainsKey(key) != true)
      {
        RequireFrame(placeholders[key], value);
      }
    }
    return placeholders;
  }

  private static void RequireFrame(Placeholder placeholder, Node node)
  {
    if (placeholder.Frame is null)
    {
      throw node.Error($"{node.Name} needs a frame");
    }
  }

  /// <summary>A title or placeholder as a theme or layout defines it, or as a slide overrides it when
  /// <paramref name="text"/> is allowed.</summary>
  private Placeholder ReadPlaceholder(Node node, Dictionary<string, Area> areas, bool text = false)
  {
    if (text && node.IsScalar)
    {
      return new Placeholder { Text = Pintura.Markup.ParsePlain(node, FieldProblem), Origin = node.Origin };
    }
    MapNode map = node.AsMap();
    var origins = new Dictionary<string, Origin>();
    List<Run>? runs = null;
    if (text && map.Take("text") is Node textNode)
    {
      runs = Pintura.Markup.ParsePlain(textNode, FieldProblem);
      origins["text"] = textNode.Origin;
    }
    Frame? frame = null;
    if (map.Take("frame") is Node frameNode)
    {
      frame = ReadFrame(frameNode, areas);
      origins["frame"] = frameNode.Origin;
    }
    string? style = null;
    if (map.Take("style") is Node styleNode)
    {
      style = ReadStyleName(styleNode);
      origins["style"] = styleNode.Origin;
    }
    var properties = new TextProperties();
    ReadTextProperties(map, properties, TextProperties.All);
    if (!text && map.Has("text"))
    {
      throw map.Take("text")!.Error("a layout's placeholder has no text; each slide writes its own");
    }
    map.Done();
    return new Placeholder
    {
      Frame = frame,
      Style = style,
      Properties = properties,
      Text = runs,
      Origin = node.Origin,
      Origins = origins
    };
  }

  private string ReadStyleName(Node node)
  {
    string style = node.AsString();
    return styles.Styles.ContainsKey(style) ? style : throw node.Error($"unknown style {style}", "styles");
  }

  #endregion

  #region Slides

  private Slide LoadSlide(Theme theme, string stem, int number, string? section, Node entry)
  {
    string path = Path.Combine(root, "slides", $"{stem}.yaml");
    Origin origin = new Origin(File(path).Display, 1, 1);
    try
    {
      if (!System.IO.File.Exists(path))
      {
        throw entry.Error($"no slide file slides/{stem}.yaml");
      }
      return ReadSlide(theme, File(path).Load("slides"), stem, number, section);
    }
    catch (LoadException exception)
    {
      return new Slide { Id = stem, Number = number, Section = section, Error = exception, Origin = origin };
    }
    // What the loader didn't foresee still stays on its slide, rather than taking every other slide with it.
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      var error = new LoadException($"couldn't load the slide ({exception.Message.TrimEnd('.')})", origin, "slides");
      return new Slide { Id = stem, Number = number, Section = section, Error = error, Origin = origin };
    }
  }

  private Slide ReadSlide(Theme theme, Node document, string stem, int number, string? section)
  {
    MapNode map = document.AsMap();
    Node layoutNode = map.Require("layout");
    string layoutId = layoutNode.AsString();
    if (!theme.Layouts.TryGetValue(layoutId, out Layout? layout))
    {
      throw layoutNode.Error($"unknown layout {layoutId}; the theme's layouts are " +
        string.Join(", ", theme.Layouts.Keys), "layouts");
    }
    Dictionary<string, Area> areas = new Dictionary<string, Area>(theme.Areas, StringComparer.Ordinal);
    foreach ((string key, Area area) in layout.Areas)
    {
      areas[key] = area;
    }

    Color? background = null;
    Origin? backgroundOrigin = null;
    if (map.Take("background") is Node backgroundNode)
    {
      background = ReadColor(backgroundNode, false);
      backgroundOrigin = backgroundNode.Origin;
    }

    Placeholder? title = null;
    if (map.Take("title") is Node titleNode)
    {
      if (layout.TitleRemoved || (layout.Title is null && theme.Title is null))
      {
        throw titleNode.Error($"layout {layoutId} has no title", "layouts");
      }
      title = ReadPlaceholder(titleNode, areas, true);
    }

    var available = new HashSet<string>(theme.Placeholders.Keys.Concat(layout.Placeholders.Keys),
      StringComparer.Ordinal);
    var placeholders = new Dictionary<string, Placeholder>(StringComparer.Ordinal);
    if (map.Take("placeholders") is Node placeholdersNode)
    {
      foreach ((string key, Node value) in placeholdersNode.AsMap().Entries())
      {
        if (!available.Contains(key))
        {
          throw value.Error(available.Count == 0
            ? $"unknown placeholder {key}; layout {layoutId} has none"
            : $"unknown placeholder {key}; layout {layoutId} has {string.Join(", ", available)}", "layouts");
        }
        placeholders[key] = ReadPlaceholder(value, areas, true);
      }
    }

    var named = new List<(Node Entry, int Over)>();
    List<Element> elements = ReadElements(map.Take("elements"), areas, document.File.FullPath, "slide", named);
    var written = new List<string>(placeholders.Keys);
    if (title is not null)
    {
      written.Insert(0, "title");
    }
    var stacked = new List<Stacked>();
    foreach ((Node entry, int over) in named)
    {
      string id = entry.AsString();
      if (!written.Contains(id))
      {
        throw entry.Error(written.Count == 0
          ? $"{id} isn't the title or a placeholder, and this slide writes neither"
          : $"{id} isn't the title or a placeholder this slide writes: {string.Join(", ", written)}", "slides");
      }
      if (stacked.Any(other => other.Id == id))
      {
        throw entry.Error($"{id} is placed twice", "slides");
      }
      stacked.Add(new Stacked(id, over));
    }
    Node? hideNode = map.Take("hide");
    map.Done();

    var hide = new List<string>();
    if (hideNode is not null)
    {
      List<string> drawn = layout.Elements.Select(element => element.Id).ToList();
      foreach (Node idNode in hideNode.AsList())
      {
        string id = idNode.AsString();
        if (!drawn.Contains(id))
        {
          throw idNode.Error(drawn.Count == 0
            ? $"unknown element {id}; layout {layoutId} draws no elements"
            : $"unknown element {id}; layout {layoutId} draws {string.Join(", ", drawn)}");
        }
        hide.Add(id);
      }
    }

    var owned = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (Element element in layout.Elements)
    {
      owned[element.Id] = $"layout {layoutId}";
    }
    if (!layout.TitleRemoved && (layout.Title ?? theme.Title) is not null)
    {
      owned["title"] = "the title";
    }
    foreach (string placeholder in available)
    {
      owned[placeholder] = $"placeholder {placeholder}";
    }
    foreach (Element element in elements)
    {
      if (owned.TryGetValue(element.Id, out string? owner))
      {
        throw new LoadException($"duplicate id {element.Id}, already used by {owner}", element.Origin, "slides");
      }
    }

    return new Slide
    {
      Id = stem,
      Number = number,
      Section = section,
      Layout = layoutId,
      Background = background,
      BackgroundOrigin = backgroundOrigin,
      Title = title,
      Placeholders = placeholders,
      Elements = elements,
      Stacked = stacked,
      Hide = hide,
      Origin = document.Origin
    };
  }

  #endregion

  #region Elements

  private static readonly string[] Kinds = ["shape", "line", "picture", "chart", "table", "text"];

  /// <summary>Whether a slide id, or a chart's or table's, names a file in its folder and nowhere else, on any
  /// system, since the page's requests name slides too.</summary>
  public static bool IsSlideStem(string stem)
  {
    return stem.Length > 0 && stem is not ("." or "..") && !stem.Any(char.IsControl) &&
      stem.IndexOfAny(['/', '\\', ':', '\u2028', '\u2029']) < 0 && !Path.IsPathRooted(stem) &&
      stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
  }

  /// <summary>Whether <paramref name="path"/> lies inside <paramref name="folder"/> once links on either are followed.
  /// </summary>
  private static bool Within(string path, string folder)
  {
    string real = Real(path);
    string inside = Real(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal
      : StringComparison.OrdinalIgnoreCase;
    return real.StartsWith(inside, comparison);
  }

  /// <summary>The folder of the git repository holding <paramref name="folder"/>, where shared pictures can live.
  /// </summary>
  private static string? Repository(string folder)
  {
    for (DirectoryInfo? current = new DirectoryInfo(folder); current is not null; current = current.Parent)
    {
      string marker = Path.Combine(current.FullName, ".git");
      if (Directory.Exists(marker) || System.IO.File.Exists(marker))
      {
        return current.FullName;
      }
    }
    return null;
  }

  private static string Real(string path, int depth = 0)
  {
    string full = Path.GetFullPath(path);
    string current = Path.GetPathRoot(full)!;
    foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar,
      StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, part);
      if (depth < 40 && new FileInfo(current).LinkTarget is string target)
      {
        current = Real(Path.Combine(Path.GetDirectoryName(current)!, target), depth + 1);
      }
    }
    return current;
  }

  public static bool Svg(string path)
  {
    return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The elements of a list, keeping in <paramref name="named"/> each entry naming the title or a
  /// placeholder, with how many elements it follows, where the list may hold them.</summary>
  private List<Element> ReadElements(Node? node, Dictionary<string, Area> areas, string file, string owner,
    List<(Node Entry, int Over)>? named = null)
  {
    var elements = new List<Element>();
    if (node is null)
    {
      return elements;
    }
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (Node entry in node.AsList())
    {
      if (named is not null && entry.IsScalar)
      {
        named.Add((entry, elements.Count));
        continue;
      }
      Element element = ReadElement(entry, areas, Path.GetDirectoryName(file)!);
      if (!ids.Add(element.Id))
      {
        throw new LoadException($"duplicate id {element.Id} on the {owner}", element.Origin, "slides");
      }
      elements.Add(element);
    }
    return elements;
  }

  private Element ReadElement(Node entry, Dictionary<string, Area> areas, string folder)
  {
    MapNode probe = entry.WithTopic("slides").AsMap();
    Node idNode = probe.Require("id");
    string id = idNode.AsString();
    if (id.Length == 0)
    {
      throw idNode.Error("id can't be empty");
    }
    string kind = Kinds.FirstOrDefault(probe.Has)
      ?? throw entry.Error($"element {id} needs one of text, shape, line, picture, chart, or table", "slides");
    string topic = kind is "shape" or "line" or "picture" ? "shapes" : kind is "chart" ? "charts"
      : kind is "table" ? "tables" : "slides";
    MapNode map = entry.WithPath($"elements.{id}").WithTopic(topic).AsMap();
    map.Take("id");
    var origins = new Dictionary<string, Origin> { ["id"] = idNode.Origin };

    Node? Take(string key)
    {
      Node? value = map.Take(key);
      if (value is not null)
      {
        origins[key] = value.Origin;
      }
      return value;
    }

    Frame? frame = null;
    if (kind == "line")
    {
      if (map.Has("frame"))
      {
        throw map.Take("frame")!.Error("a line has no frame: give it line: { from: [x, y], to: [x, y] }");
      }
    }
    else
    {
      frame = ReadFrame(Take("frame") ?? throw map.Node.Error($"element {id} needs a frame"), areas);
    }

    bool boxed = kind is "text" or "shape" or "picture";
    bool texted = kind is "text" or "shape";
    double? rotate = boxed ? Take("rotate")?.AsNumber() : null;
    string? flip = boxed ? Take("flip")?.AsChoice("horizontal", "vertical") : null;
    double? opacity = null;
    if (kind is not ("chart" or "table") && Take("opacity") is Node opacityNode)
    {
      opacity = ReadOpacity(opacityNode);
    }
    bool? decorative = kind is not ("chart" or "table") ? Take("decorative")?.AsBoolean() : null;
    string? style = (texted || kind == "picture") && Take("style") is Node styleNode ? ReadStyleName(styleNode) : null;
    Color? fill = texted && Take("fill") is Node fillNode ? ReadColor(fillNode) : null;
    Stroke? stroke = kind is not ("chart" or "table") && Take("stroke") is Node strokeNode
      ? ReadStroke(strokeNode)
      : null;
    Shadow? shadow = boxed && Take("shadow") is Node shadowNode ? ReadShadow(shadowNode) : null;
    var properties = new TextProperties();
    List<Paragraph>? text = null;
    if (texted)
    {
      ReadTextProperties(map, properties, TextProperties.All);
      if (Take("text") is Node textNode)
      {
        text = Pintura.Markup.Parse(textNode.WithTopic("text"), Markup());
      }
    }

    string? shape = null;
    string? direction = null;
    Dictionary<string, double>? adjust = null;
    double[]? tail = null;
    if (kind == "shape")
    {
      Node shapeNode = Take("shape")!;
      shape = shapeNode.AsString();
      if (!Shapes.Contains(shape))
      {
        throw shapeNode.Error($"unknown shape {shape}; draw with the sixteen, or use a picture");
      }
      if (Take("direction") is Node directionNode)
      {
        if (!DirectionalShapes.Contains(shape))
        {
          throw directionNode.Error($"a {shape} has no direction");
        }
        direction = directionNode.AsChoice("right", "left", "up", "down");
      }
      if (Take("adjust") is Node adjustNode)
      {
        string[] allowed = Adjustments.GetValueOrDefault(shape, []);
        adjust = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string key, Node value) in adjustNode.AsMap().Entries())
        {
          if (!allowed.Contains(key))
          {
            throw value.Error(allowed.Length == 0
              ? $"a {shape} takes no adjustments"
              : $"a {shape} adjusts {string.Join(", ", allowed)}, not {key}");
          }
          adjust[key] = value.AsNumber() is double length and >= 0
            ? length
            : throw value.Error("adjustments are lengths in points, 0 or more");
          origins[$"adjust.{key}"] = value.Origin;
        }
      }
      if (Take("tail") is Node tailNode)
      {
        if (shape != "callout")
        {
          throw tailNode.Error("only a callout has a tail");
        }
        tail = tailNode.AsNumbers(2, "[x, y], the point on the slide the tail reaches");
      }
      else if (shape == "callout")
      {
        throw map.Node.Error("a callout needs tail: [x, y], the point on the slide its tail reaches");
      }
    }

    double[]? from = null;
    double[]? to = null;
    string? start = null;
    string? end = null;
    if (kind == "line")
    {
      MapNode lineMap = Take("line")!.AsMap();
      Node fromNode = lineMap.Require("from");
      Node toNode = lineMap.Require("to");
      from = fromNode.AsNumbers(2, "[x, y] in points");
      to = toNode.AsNumbers(2, "[x, y] in points");
      origins["from"] = fromNode.Origin;
      origins["to"] = toNode.Origin;
      lineMap.Done();
      start = Take("start")?.AsChoice(Markers);
      end = Take("end")?.AsChoice(Markers);
    }

    string? picture = null;
    string? picturePath = null;
    string? fit = null;
    double[]? crop = null;
    string? alt = null;
    if (kind == "picture")
    {
      Node pictureNode = Take("picture")!;
      picture = pictureNode.AsString();
      picturePath = Path.GetFullPath(Path.Combine(folder, picture));
      if (!System.IO.File.Exists(picturePath))
      {
        throw pictureNode.Error($"no picture at {picture}; paths are relative to the file naming them");
      }
      // Judged by the file a link leads to, so a link named like a picture can't carry another kind of file.
      if (PresentationStore.ContentType(Real(picturePath)) is null)
      {
        throw pictureNode.Error($"{picture} isn't a picture; use a PNG, JPEG, GIF, SVG, or WebP file");
      }
      // A picture travels with the presentation, into the shared HTML file and the export, so it can't be any file
      // the machine holds.
      // Shared pictures live beside the presentations, or anywhere in their repository.
      string shared = Repository(root) ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? root;
      if (!Within(picturePath, root) && (themeRoot is null || !Within(picturePath, themeRoot)) &&
        !Within(picturePath, shared))
      {
        throw pictureNode.Error($"{picture} is outside the presentation's folder, its theme, and its repository; " +
          "copy it into one of them");
      }
      if (Take("fill") is Node pictureFill)
      {
        fill = Svg(picturePath)
          ? ReadColor(pictureFill, false)
          : throw pictureFill.Error($"fill recolors an SVG picture, and {picture} isn't one");
        if (SvgPaint.Malformed(File(picturePath).ReadText("slides")) is string problem)
        {
          throw pictureFill.Error($"{picture} can't be recolored, since it isn't well-formed SVG: {problem}");
        }
      }
      fit = Take("fit")?.AsChoice("contain", "cover", "stretch");
      crop = Take("crop")?.AsNumbers(4, "[left, top, right, bottom] in the image's pixels");
      alt = Take("alt")?.AsString();
    }

    string? chart = null;
    string? table = null;
    if (kind is "chart" or "table")
    {
      Node reference = Take(kind)!;
      string stem = reference.AsString();
      string folderName = kind == "chart" ? "charts" : "tables";
      if (!IsSlideStem(stem))
      {
        throw reference.Error($"name a {kind} by its file's stem in {folderName}/, such as revenue");
      }
      if (!System.IO.File.Exists(Path.Combine(root, folderName, $"{stem}.yaml")))
      {
        throw reference.Error($"no {kind} file {folderName}/{stem}.yaml");
      }
      chart = kind == "chart" ? stem : null;
      table = kind == "table" ? stem : null;
      if (chart is not null)
      {
        LoadChart(stem);
      }
      else
      {
        LoadTable(stem);
      }
    }

    map.Done();

    return new Element
    {
      Id = id,
      Kind = kind,
      Frame = frame,
      Rotate = rotate,
      Flip = flip,
      Style = style,
      Fill = fill,
      Stroke = stroke,
      Shadow = shadow,
      Opacity = opacity,
      Decorative = decorative,
      Properties = properties,
      Text = text,
      Shape = shape,
      Direction = direction,
      Adjust = adjust,
      Tail = tail,
      From = from,
      To = to,
      Start = start,
      End = end,
      Picture = picture,
      PicturePath = picturePath,
      Fit = fit,
      Crop = crop,
      Alt = alt,
      Chart = chart,
      Table = table,
      Origin = entry.Origin.At($"elements.{id}"),
      Origins = origins
    };
  }

  /// <summary>Refuses data that isn't a CSV file, the only kind a chart or a table reads, judged by the file a link
  /// leads to, as a picture's type is, so a link named like a CSV can't carry another kind of file into an export.
  /// </summary>
  private static void RequireCsv(Node node, string data, string path)
  {
    if (!Real(path).EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
    {
      throw node.Error($"{data} isn't a CSV file; data is a .csv file, or lines or a view from a book");
    }
  }

  #endregion

  #region Charts

  private static readonly Dictionary<string, string[]> LabelPositions = new Dictionary<string, string[]>
  {
    ["clustered"] = ["center", "inside", "inside_base", "outside"],
    ["stacked"] = ["center", "inside", "inside_base"],
    ["line"] = ["above", "below", "center"],
    ["pie"] = ["center", "inside", "outside"],
    ["doughnut"] = []
  };

  /// <summary>Where labels sit in a <paramref name="group"/> that names no position, or null for a doughnut, which
  /// places them nowhere else.</summary>
  public static string? DefaultLabelPosition(string group)
  {
    return group switch
    {
      "clustered" => "outside",
      "line" => "above",
      "pie" => "inside",
      "doughnut" => null,
      _ => "center"
    };
  }

  /// <summary>Where a chart of <paramref name="type"/>, or a line series in one, can place its labels.</summary>
  public static string[] LabelPositionsFor(string type)
  {
    return LabelPositions[LabelPositionGroup(type)];
  }

  public static string LabelPositionGroup(Chart chart)
  {
    return LabelPositionGroup(chart.Type);
  }

  public static string LabelPositionGroup(string type)
  {
    string family = Chart.FamilyOf(type);
    return type == "doughnut" ? "doughnut"
      : family is "pie" or "line" ? family
      : Chart.GroupingOf(type) == "clustered" ? "clustered"
      : "stacked";
  }

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
      if (!System.IO.File.Exists(csvPath))
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
    ChartProperties[] levels = chart.Style is null
      ? [styles.Chart, chart.Properties]
      : [styles.Chart, styles.Charts[chart.Style], chart.Properties];
    (object Value, Origin Origin)? Effective(string key)
    {
      ChartProperties? level = levels.LastOrDefault(candidate => candidate.Get(key) is not null);
      return level is null ? null : (level.Get(key)!, level.Origins[key]);
    }

    if (Effective("data_labels.show") is ("percent", Origin showOrigin) && chart.Family != "pie")
    {
      throw new LoadException($"show: percent is for pie and doughnut charts; a {chart.Type} shows value or none",
        showOrigin, "charts");
    }
    if (Effective("data_labels.position") is (string position, Origin positionOrigin))
    {
      string group = LabelPositionGroup(chart);
      string[] allowed = LabelPositions[group];
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
    ValidateSeriesOptions(chart);
    if (chart.Type == "pie" && chart.Series.Count > 1)
    {
      throw dataNode.Error($"a pie draws one series, and {chart.Source} has {chart.Series.Count}; use series_in to " +
        "turn it, or a doughnut for a ring per series");
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
          target.Color = ReadColor(color, false);
          options.Origins[$"{prefix}color"] = color.Origin;
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
          throw value.Error("a point takes a color, markers, data_labels, or several");
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
          string group = drawn == "line" ? "line" : LabelPositionGroup(chart);
          if (!LabelPositions[group].Contains(pointPosition))
          {
            throw Error(LabelKey("position")!, drawn == "line"
              ? $"a line places labels {Join(LabelPositions["line"])}, not {pointPosition}"
              : $"a {chart.Type} places labels {Join(LabelPositions[group])}, not {pointPosition}");
          }
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
      if (options.DataLabels.Show == "percent")
      {
        throw Error("data_labels.show", $"show: percent is for pie and doughnut charts; a series shows value or none");
      }
      if (options.DataLabels.Position is string position && drawn == "line"
        && !LabelPositions["line"].Contains(position))
      {
        throw Error("data_labels.position", $"a line places labels {Join(LabelPositions["line"])}, not {position}");
      }
      if (options.DataLabels.Position is string barPosition && drawn != "line"
        && !LabelPositions[LabelPositionGroup(chart)].Contains(barPosition))
      {
        throw Error("data_labels.position",
          $"a {chart.Type} places labels {Join(LabelPositions[LabelPositionGroup(chart)])}, not {barPosition}");
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
  /// category or series.</summary>
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
        throw lineNode.Error($"no line {lineNode.AsString()} in book {book.Id}; name it as libreta --lines lists it, " +
          "from the book's folder");
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
      columns.AddRange(picked[0].Line.Columns.Select(name => (name, name, node)));
    }
    var values = new List<List<double?>>();
    foreach ((BookLine line, string _) in picked)
    {
      var row = new List<double?>();
      foreach ((string name, string _, Node columnNode) in columns)
      {
        int column = line.Columns.IndexOf(name);
        if (column < 0)
        {
          throw columnNode.Error($"{line.Name} has no column {name}; its columns are " + And([.. line.Columns]));
        }
        row.Add(PlainNumber.TryParse(line.Exact[column], out double value) ? value : null);
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
      target.Position = position.AsChoice(LabelPositions.Values.SelectMany(choices => choices).Distinct().ToArray());
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
      target.Shape = shape.AsChoice(MarkerShapes);
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

  #endregion

  #region Tables

  private void LoadTable(string stem)
  {
    if (tables.ContainsKey(stem))
    {
      return;
    }
    string path = Path.Combine(root, "tables", $"{stem}.yaml");
    Node document = File(path).Load("tables");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    Node dataNode = map.Require("data");
    string data = dataNode.IsMap ? "" : dataNode.AsString();
    origins["data"] = dataNode.Origin;
    ViewData? view = null;
    List<CsvRow> rows = [];
    SourceFile? csv = null;
    if (dataNode.IsMap)
    {
      view = ReadViewData(dataNode);
    }
    else
    {
      string csvPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, data));
      if (!System.IO.File.Exists(csvPath))
      {
        throw dataNode.Error($"no CSV at {data}; paths are relative to the file naming them");
      }
      RequireCsv(dataNode, data, csvPath);
      csv = File(csvPath);
      rows = ReadTableData(csv);
    }
    List<List<string>> values = view?.Values ?? rows.Select(row => row.Fields).ToList();
    int rowCount = values.Count;
    int columnCount = values[0].Count;

    TableStyle? style = null;
    string? styleName = null;
    if (map.Take("style") is Node styleNode)
    {
      styleName = styleNode.AsString();
      style = styles.Tables.GetValueOrDefault(styleName) ?? throw styleNode.Error(styles.Tables.Count == 0
        ? $"unknown table style {styleName}; the theme has none"
        : $"unknown table style {styleName}; the theme's are {string.Join(", ", styles.Tables.Keys)}");
      origins["style"] = styleNode.Origin;
    }

    int headerRows = view is null ? 0 : 1;
    if (map.Take("header_rows") is Node headerNode)
    {
      headerRows = headerNode.AsInteger() is int count and >= 0 && count <= rowCount
        ? count
        : throw headerNode.Error($"header_rows runs from 0 to the table's {rowCount} rows");
      origins["header_rows"] = headerNode.Origin;
    }

    var columns = new List<TableColumn>();
    if (map.Take("columns") is Node columnsNode)
    {
      List<Node> entries = columnsNode.AsList();
      if (entries.Count > columnCount)
      {
        throw columnsNode.Error($"columns lists {entries.Count} columns, and {data} has {columnCount}");
      }
      foreach (Node entry in entries)
      {
        MapNode column = entry.AsMap();
        columns.Add((TableColumn)ReadTableFormatting(column, "column", styles.Styles, styles.Formats));
        column.Done();
      }
    }

    var tableRows = new Dictionary<int, TableRow>();
    if (map.Take("rows") is Node rowsNode)
    {
      foreach ((string key, Node value) in rowsNode.AsMap().Entries())
      {
        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        {
          throw value.Error($"rows are keyed by row number, from 1 with the header; {key} isn't one");
        }
        RowInRange(value, number, rowCount);
        MapNode row = value.AsMap();
        var formatting = (TableRow)ReadTableFormatting(row, "row", styles.Styles, styles.Formats);
        row.Done();
        if (formatting.Style is string rowStyle && style?.Rows.ContainsKey(rowStyle) != true)
        {
          Origin at = formatting.Origins["style"];
          throw new LoadException(style is null
            ? $"row style {rowStyle} needs a table style; name one with style"
            : $"unknown row style {rowStyle}; table style {styleName} has {And([.. style.Rows.Keys])}", at,
            "tables");
        }
        tableRows[number] = formatting;
      }
    }

    var cells = new List<TableCell>();
    if (map.Take("cells") is Node cellsNode)
    {
      foreach (Node entry in cellsNode.AsList())
      {
        MapNode cell = entry.AsMap();
        var formatting = (TableCell)ReadTableFormatting(cell, "cell", styles.Styles, styles.Formats);
        cell.Done();
        RowInRange(entry, formatting.Row, rowCount);
        ColumnInRange(entry, formatting.Column, columnCount);
        cells.Add(formatting);
      }
    }

    List<TableMerge> merges = map.Take("merge") is Node mergeNode
      ? ReadMerges(mergeNode, values)
      : new List<TableMerge>();
    map.Done();

    List<List<List<Paragraph>>> paragraphs;
    if (view is null)
    {
      MarkupContext context = Markup();
      paragraphs = rows
        .Select(row => row.Fields
          .Select(field => Pintura.Markup.Parse(field, _ => new Origin(csv!.Display, row.Line, 1), context))
          .ToList())
        .ToList();
    }
    else
    {
      // A view's labels and numbers are text as Libreta wrote them, never markup.
      paragraphs = values
        .Select(row => row
          .Select(value => new List<Paragraph>
          {
            new Paragraph
            {
              Runs = value.Length == 0 ? [] : [new Run { Text = value, Origin = dataNode.Origin }],
              Origin = dataNode.Origin
            }
          })
          .ToList())
        .ToList();
      for (int row = 0; row < view.Styles.Count; row++)
      {
        if (view.Styles[row] is not string role || style?.Rows.ContainsKey(role) != true)
        {
          continue;
        }
        // A row entry without a style of its own, such as one setting a height, keeps the role's row style.
        if (tableRows.TryGetValue(row + 1, out TableRow? written))
        {
          if (written.Style is null)
          {
            tableRows[row + 1] = new TableRow
            {
              Style = role,
              Format = written.Format,
              Fill = written.Fill,
              BorderTop = written.BorderTop,
              BorderBottom = written.BorderBottom,
              BorderLeft = written.BorderLeft,
              BorderRight = written.BorderRight,
              Properties = written.Properties,
              Height = written.Height,
              Origin = written.Origin,
              Origins = new Dictionary<string, Origin>(written.Origins) { ["style"] = dataNode.Origin }
            };
          }
        }
        else
        {
          tableRows[row + 1] = new TableRow { Style = role, Origins = { ["style"] = dataNode.Origin } };
        }
      }
    }

    tables[stem] = new Table
    {
      Id = stem,
      Style = styleName,
      Data = data,
      View = view?.View,
      Exact = view?.Exact,
      Sources = view?.Sources,
      Indents = view?.Indents,
      HeaderRows = headerRows,
      Columns = columns,
      Rows = tableRows,
      Cells = cells,
      Merges = merges,
      Values = values,
      Paragraphs = paragraphs,
      Origin = document.Origin,
      Origins = origins
    };
  }

  /// <summary>A view as a table's rows: a header of column labels, then each row's label and displayed values, with
  /// the row's role in <c>Styles</c> for a table style to match by name.</summary>
  private record ViewData(string View, List<List<string>> Values, List<List<string?>> Exact,
    List<List<(string Line, string Column)?>> Sources, List<string?> Styles, List<int> Indents);

  private ViewData ReadViewData(Node node)
  {
    MapNode map = node.WithTopic("books").AsMap();
    Node viewNode = map.Require("view");
    string text = viewNode.AsString();
    int split = text.IndexOf("::", StringComparison.Ordinal);
    if (split < 0)
    {
      throw viewNode.Error($"write the view after its book's id, such as acme::{text}");
    }
    string id = text[..split];
    string viewId = text[(split + 2)..];
    Book book = FindBook(id) ?? throw viewNode.Error(UnknownBook(id));
    string corner = map.Take("label")?.AsString() ?? "";
    Node? linesNode = map.Take("lines");
    Node? columnsNode = map.Take("columns");
    map.Done();
    BookView view;
    try
    {
      view = reader.View(book.Root, viewId);
    }
    catch (BookException exception)
    {
      throw viewNode.Error($"view {viewId} can't be read from book {id}: {exception.Message}");
    }

    var columns = new List<int>();
    var headers = new List<string>();
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
      int index = view.Columns.FindIndex(column => column.Name == name || column.Label == name);
      columns.Add(index >= 0 ? index : throw columnNode.Error($"view {viewId} has no column {name}; its columns " +
        $"are {And([.. view.Columns.Select(column => column.Label)])}"));
      headers.Add(label ?? view.Columns[index].Label);
    }
    if (columnsNode is null)
    {
      columns.AddRange(Enumerable.Range(0, view.Columns.Count));
      headers.AddRange(view.Columns.Select(column => column.Label));
    }

    var rows = new List<BookRow>();
    foreach (Node entry in linesNode?.AsList() ?? [])
    {
      Node lineNode = entry;
      string? label = null;
      if (entry.IsMap)
      {
        MapNode lineMap = entry.AsMap();
        lineNode = lineMap.Require("line");
        label = lineMap.Require("label").AsString();
        lineMap.Done();
      }
      string name = lineNode.AsString();
      List<BookRow> matches = view.Rows
        .Where(row => row.Name is not null &&
          (row.Name == name || row.Name.EndsWith($".{name}", StringComparison.Ordinal)))
        // A view showing a line twice, as in a bridge that adds it back, shows the same values both times.
        .DistinctBy(row => row.Name)
        .ToList();
      rows.Add(matches.Count switch
      {
        1 => label is null ? matches[0] : matches[0] with { Label = label },
        0 => throw lineNode.Error($"view {viewId} shows no line {name}"),
        _ => throw lineNode.Error($"{name} is {And([.. matches.Select(row => row.Name!)])} in view {viewId}; " +
          "name the one you mean in full")
      });
    }
    if (linesNode is null)
    {
      rows.AddRange(view.Rows.Where(row => row.Kind != "spacer"));
    }

    List<List<string>> values = [[corner, .. headers]];
    List<List<string?>> exact = [[null, .. columns.Select(_ => (string?)null)]];
    List<List<(string, string)?>> sources = [[null, .. columns.Select(_ => ((string, string)?)null)]];
    List<string?> roles = [null];
    List<int> indents = [0];
    foreach (BookRow row in rows)
    {
      values.Add([row.Label ?? "", .. columns.Select(column => row.Display?[column] ?? "")]);
      exact.Add([null, .. columns.Select(column => row.Exact?[column])]);
      sources.Add([null, .. columns.Select(column => row.Cells is not null
        ? (row.Cells[column].Line, row.Cells[column].Column)
        : row.Name is null
          ? ((string, string)?)null
          : (row.Name, view.Columns[column].Name))]);
      roles.Add(row.Style);
      indents.Add(row.Indent);
    }
    return new ViewData(text, values, exact, sources, roles, indents);
  }

  private List<CsvRow> ReadTableData(SourceFile file)
  {
    LoadException Error(int line, string problem)
    {
      return new LoadException(problem, new Origin(file.Display, line, 1), "tables");
    }
    List<CsvRow> rows = Csv.Parse(file.ReadText("tables"), Error);
    if (rows.Count == 0)
    {
      throw Error(1, "a table's CSV needs at least one row");
    }
    int width = rows[0].Fields.Count;
    foreach (CsvRow row in rows.Skip(1).Where(row => row.Fields.Count != width))
    {
      throw Error(row.Line, $"the row has {row.Fields.Count} cells and the first row {width}; leave a cell empty " +
        "rather than out");
    }
    return rows;
  }

  private static List<TableMerge> ReadMerges(Node node, List<List<string>> values)
  {
    var merges = new List<TableMerge>();
    foreach (Node entry in node.AsList())
    {
      MapNode map = entry.AsMap();
      int row = map.Require("row").AsInteger();
      int column = map.Require("column").AsInteger();
      int rows = map.Take("rows") is Node rowsNode ? AtLeastOne(rowsNode) : 1;
      int columns = map.Take("columns") is Node columnsNode ? AtLeastOne(columnsNode) : 1;
      map.Done();
      RowInRange(entry, row + rows - 1, values.Count);
      ColumnInRange(entry, column + columns - 1, values[0].Count);
      var merge = new TableMerge { Row = row, Column = column, Rows = rows, Columns = columns, Origin = entry.Origin };
      TableMerge? overlapped = merges.FirstOrDefault(other =>
        other.Row < row + rows && row < other.Row + other.Rows &&
        other.Column < column + columns && column < other.Column + other.Columns);
      if (overlapped is not null)
      {
        throw entry.Error($"this merge overlaps the one from row {overlapped.Row}, column {overlapped.Column}");
      }
      for (int covered = row; covered < row + rows; covered++)
      {
        for (int across = column; across < column + columns; across++)
        {
          string value = values[covered - 1][across - 1];
          if ((covered, across) != (row, column) && value.Length > 0)
          {
            throw entry.Error($"the merge covers row {covered}, column {across}, which holds {value}; a merge shows " +
              "its first cell, so leave the rest empty in the CSV");
          }
        }
      }
      merges.Add(merge);
    }
    return merges;
  }

  private static string And(string[] names)
  {
    return names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
  }

  private static int AtLeastOne(Node node)
  {
    return node.AsInteger() is int count and >= 1 ? count : throw node.Error($"{node.Name} must be 1 or more");
  }

  private static void RowInRange(Node node, int row, int rows)
  {
    if (row < 1 || row > rows)
    {
      throw node.Error($"row {row} is past the table's {rows} rows; rows are numbered from 1, header included");
    }
  }

  private static void ColumnInRange(Node node, int column, int columns)
  {
    if (column < 1 || column > columns)
    {
      throw node.Error($"column {column} is past the table's {columns} columns; columns are numbered from 1");
    }
  }

  #endregion

}