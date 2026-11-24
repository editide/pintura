using System.Globalization;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>Reads a presentation folder and its theme into the model, rejecting unknown keys and unknown names with
/// the file, line, and docs topic. A slide that fails keeps its error and the rest of the presentation loads.
/// </summary>
public partial class PresentationLoader
{
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
  private readonly IFiles files;
  private string? themeRoot;
  private Dictionary<string, Field> fields = new Dictionary<string, Field>();
  private readonly Dictionary<string, Book> books = new Dictionary<string, Book>(StringComparer.Ordinal);
  private readonly Dictionary<string, BookValue> references = new Dictionary<string, BookValue>(StringComparer.Ordinal);
  private Dictionary<string, Color> namedColors = new Dictionary<string, Color>();
  private StyleSheet styles = new StyleSheet();
  private readonly Dictionary<string, Chart> charts = new Dictionary<string, Chart>(StringComparer.Ordinal);
  private readonly Dictionary<string, Table> tables = new Dictionary<string, Table>(StringComparer.Ordinal);
  private List<Pick>? resolved;

  private PresentationLoader(string root, string displayBase, IBookReader reader, IFiles files)
  {
    this.root = root;
    this.displayBase = displayBase;
    this.reader = reader;
    this.files = files;
  }

  /// <summary>Loads the presentation at <paramref name="root"/> from <paramref name="files"/>, the disk unless a
  /// revision's tree.</summary>
  public static Presentation Load(string root, string? displayBase = null, IBookReader? books = null,
    IFiles? files = null)
  {
    return new PresentationLoader(Path.GetFullPath(root), displayBase ?? Directory.GetCurrentDirectory(),
      (books ?? LibretaReader.Shared).Session(), files ?? DiskFiles.Instance).LoadPresentation();
  }

  /// <summary>Whether a revision is read and <paramref name="path"/> lies outside its repository, where git has none
  /// of it.</summary>
  private bool OutsideTree(string path)
  {
    return files is GitTree tree &&
      Path.GetRelativePath(tree.Top, path).Split(Path.DirectorySeparatorChar)[0] == "..";
  }

  private SourceFile File(string path)
  {
    return SourceFile.For(Path.GetFullPath(path), displayBase, files);
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
    if (OutsideTree(themeRoot))
    {
      throw themeNode.Error($"the theme at {themeNode.AsString()} is outside the repository, so it can't be read at " +
        "a revision", "themes");
    }
    if (!files.Exists(Path.Combine(themeRoot, "theme.yaml")))
    {
      throw themeNode.Error($"no theme.yaml in {themeNode.AsString()}", "themes");
    }
    Theme theme = LoadTheme(themeRoot);

    List<Pick> picks = PickFile.Load(root, displayBase, files);
    // Which pick offers each slide, so one listed in slides as well is caught whichever comes first.
    var offered = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (Pick pick in picks)
    {
      foreach (Variant variant in pick.Variants)
      {
        if (!offered.TryAdd(variant.Slide, pick.Id))
        {
          throw new LoadException($"slide {variant.Slide} is offered in picks {offered[variant.Slide]} and {pick.Id}",
            pick.Origin, "picks");
        }
      }
    }
    var sections = new List<Section>();
    var slides = new List<Slide>();
    var variants = new List<Slide>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var placed = new HashSet<string>(StringComparer.Ordinal);
    Section current = new Section();
    sections.Add(current);
    foreach (Node entry in slidesNode.AsList())
    {
      if (entry.IsMap && entry.AsMap().Has("pick"))
      {
        MapNode pickMap = entry.AsMap();
        Node pickNode = pickMap.Require("pick");
        pickMap.Done();
        string id = pickNode.AsString();
        Pick pick = picks.FirstOrDefault(candidate => candidate.Id == id)
          ?? throw pickNode.Error($"no open pick {id} in {PickFile.Name}", "picks");
        if (!placed.Add(id))
        {
          throw pickNode.Error($"pick {id} is listed twice", "picks");
        }
        int number = slides.Count + 1;
        foreach (Variant variant in pick.Variants)
        {
          if (!seen.Add(variant.Slide))
          {
            throw pickNode.Error($"slide {variant.Slide} is both in slides and offered in pick {id}", "picks");
          }
          Slide slide = LoadSlide(theme, variant.Slide, number, current.Name, entry, id);
          (variant == pick.Variants[0] ? slides : variants).Add(slide);
        }
        current.Slides.Add(pick.Variants[0].Slide);
        continue;
      }
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
      if (offered.TryGetValue(stem, out string? offering))
      {
        throw entry.Error($"slide {stem} is both in slides and offered in pick {offering}", "picks");
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
      Comments = CommentFile.Load(root, displayBase, files),
      Picks = picks,
      ResolvedPicks = resolved!,
      Variants = variants,
      Root = root,
      Files = files,
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
      string bookRoot = Path.GetFullPath(Path.Combine(root, path));
      if (OutsideTree(bookRoot))
      {
        throw pathNode.Error($"the book at {path} is outside the repository, so it can't be read at a revision");
      }
      if (!files.Exists(Path.Combine(bookRoot, "book.yaml")))
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
    IEnumerable<(Book Book, string? View)> views = files.Files(tables, "*.yaml")
      .SelectMany(file => ViewNamed().Matches(TextOrNothing(file)))
      .Select(match => (Book: books.GetValueOrDefault(match.Groups["book"].Value),
        View: (string?)match.Groups["view"].Value))
      .Where(entry => entry.Book is not null)
      .Select(entry => (entry.Book!, entry.View));
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

  /// <summary>What a revision draws for a number its book no longer has.</summary>
  private const string Unresolved = "#REF!";

  /// <summary>Resolves the reference into <see cref="references"/>, or says why it doesn't resolve. A revision draws
  /// one that doesn't as <c>#REF!</c>, since its book may have moved on in a commit its slides caught up with only
  /// later, and comparing against it shouldn't lose the slide.</summary>
  private string? ReferenceProblem(Reference reference)
  {
    string? problem = Resolve(reference);
    if (problem is null || files is not GitTree)
    {
      return problem;
    }
    references[reference.ToString()] = new BookValue
    {
      Text = Unresolved,
      Book = reference.Book,
      Line = reference.Line,
      Column = reference.Column,
      Origin = FindBook(reference.Book)?.Origin.At(reference.ToString()) ?? new Origin("", 0, 0)
    };
    return null;
  }

  private string? Resolve(Reference reference)
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
      return NoColumn(line, reference.Column);
    }
    if (line.Exact[column] is null)
    {
      return $"{reference.Line} has no value in {reference.Column}";
    }
    string exact = line.Exact[column]!;
    if (format?.Date is not null && !IsoDate(exact, out _))
    {
      return $"{reference.Format} spells dates, and {reference.Line} in {reference.Column} is {exact}, not a date " +
        "written as YYYY-MM-DD";
    }
    if (format is { Date: null } && !PlainNumber.TryParse(exact, out _))
    {
      return $"{reference.Format} spells numbers, and {reference.Line} in {reference.Column} is {exact}";
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

  private Slide LoadSlide(Theme theme, string stem, int number, string? section, Node entry, string? pick = null)
  {
    string path = Path.Combine(root, "slides", $"{stem}.yaml");
    Origin origin = new Origin(File(path).Display, 1, 1);
    try
    {
      if (!files.Exists(path))
      {
        throw entry.Error($"no slide file slides/{stem}.yaml");
      }
      return ReadSlide(theme, File(path).Load("slides"), stem, number, section, pick);
    }
    catch (LoadException exception)
    {
      return new Slide { Id = stem, Number = number, Section = section, Pick = pick, Error = exception,
        Origin = origin };
    }
    // What the loader didn't foresee still stays on its slide, rather than taking every other slide with it.
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      var error = new LoadException($"couldn't load the slide ({exception.Message.TrimEnd('.')})", origin, "slides");
      return new Slide { Id = stem, Number = number, Section = section, Pick = pick, Error = error,
        Origin = origin };
    }
  }

  private Slide ReadSlide(Theme theme, Node document, string stem, int number, string? section, string? pick)
  {
    MapNode map = document.AsMap();
    Node layoutNode = map.Require("layout");
    string layoutId = layoutNode.AsString();
    if (!theme.Layouts.TryGetValue(layoutId, out Layout? layout))
    {
      throw layoutNode.Error($"unknown layout {layoutId}; the theme's layouts are " +
        string.Join(", ", theme.Layouts.Keys), "layouts");
    }
    Dictionary<string, Area> areas = theme.AreasOf(layout)
      .ToDictionary(entry => entry.Name, entry => entry.Area, StringComparer.Ordinal);

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
      if (!theme.HasTitle(layout))
      {
        throw titleNode.Error($"layout {layoutId} has no title", "layouts");
      }
      title = ReadPlaceholder(titleNode, areas, true);
    }

    var available = new HashSet<string>(theme.PlaceholderNames(layout), StringComparer.Ordinal);
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
    Rejection? rejected = map.Take("rejected") is Node rejectedNode ? ReadRejection(rejectedNode) : null;
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
    if (theme.HasTitle(layout))
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
      Pick = pick,
      Rejected = rejected,
      Origin = document.Origin
    };
  }

  /// <summary>The pick a slide's <c>rejected</c> names, which must have been resolved with none, as last resolved.
  /// </summary>
  private Rejection ReadRejection(Node node)
  {
    string id = node.AsString();
    resolved ??= PickFile.LoadResolved(root, displayBase, files);
    Pick pick = resolved.LastOrDefault(each => each.Id == id)
      ?? throw node.Error($"rejected names no pick resolved with {PickFile.None} in {PickFile.ResolvedName}", "picks");
    return pick.Resolved!.Chosen == PickFile.None
      ? new Rejection(id, pick.Resolved.Why)
      : throw node.Error($"rejected names pick {id}, which kept {pick.Resolved.Chosen} rather than {PickFile.None}",
        "picks");
  }

  #endregion

  #region Elements

  private static readonly string[] Kinds = ["shape", "line", "arc", "picture", "chart", "table", "text"];

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
  /// A <c>.git</c> folder counts only with a HEAD, as git requires, since a stray empty one would widen what a picture
  /// may reach to everything beneath it; a <c>.git</c> file marks a worktree.</summary>
  public static string? Repository(string folder)
  {
    for (DirectoryInfo? current = new DirectoryInfo(folder); current is not null; current = current.Parent)
    {
      string marker = Path.Combine(current.FullName, ".git");
      if (System.IO.File.Exists(Path.Combine(marker, "HEAD")) || System.IO.File.Exists(marker))
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
      ?? throw entry.Error($"element {id} needs one of text, shape, line, arc, picture, chart, or table", "slides");
    string topic = kind is "shape" or "line" or "arc" or "picture" ? "shapes" : kind is "chart" ? "charts"
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
      if (!Element.Shapes.Contains(shape))
      {
        throw shapeNode.Error($"unknown shape {shape}; draw with the sixteen, or use a picture");
      }
      if (Take("direction") is Node directionNode)
      {
        if (!Element.DirectionalShapes.Contains(shape))
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
      start = Take("start")?.AsChoice(Element.Markers);
      end = Take("end")?.AsChoice(Element.Markers);
    }

    double[]? arc = null;
    if (kind == "arc")
    {
      MapNode arcMap = Take("arc")!.AsMap();
      double Angle(string key)
      {
        Node node = arcMap.Require(key);
        origins[$"arc.{key}"] = node.Origin;
        return node.AsNumber() is double angle and >= 0 and < 360
          ? angle
          : throw node.Error("an arc's angles are degrees clockwise from the right, from 0 up to 360");
      }
      arc = [Angle("from"), Angle("to")];
      if (arc[0] == arc[1])
      {
        throw arcMap.Node.Error("an arc's from and to are different angles");
      }
      arcMap.Done();
      start = Take("start")?.AsChoice(Element.Markers);
      end = Take("end")?.AsChoice(Element.Markers);
    }

    string? picture = null;
    string? picturePath = null;
    string? fit = null;
    double[]? crop = null;
    string? alt = null;
    string? link = null;
    if (kind == "picture")
    {
      Node pictureNode = Take("picture")!;
      picture = pictureNode.AsString();
      picturePath = Path.GetFullPath(Path.Combine(folder, picture));
      if (!files.Exists(picturePath))
      {
        throw pictureNode.Error($"no picture at {picture}; paths are relative to the file naming them");
      }
      // Judged by the file a link leads to, so a link named like a picture can't carry another kind of file.
      if (Pictures.ContentType(Real(picturePath)) is null)
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
      if (Take("crop") is Node cropNode)
      {
        crop = Crop(cropNode, picture, picturePath);
      }
      alt = Take("alt")?.AsString();
      if (Take("link") is Node linkNode)
      {
        link = linkNode.AsString().Trim();
        if (!Pintura.Markup.WebLink(link))
        {
          throw linkNode.Error($"a link is http, https, or mailto, not {link}");
        }
      }
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
      if (!files.Exists(Path.Combine(root, folderName, $"{stem}.yaml")))
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
      Arc = arc,
      Start = start,
      End = end,
      Picture = picture,
      PicturePath = picturePath,
      Fit = fit,
      Crop = crop,
      Alt = alt,
      Link = link,
      Chart = chart,
      Table = table,
      Origin = entry.Origin.At($"elements.{id}"),
      Origins = origins
    };
  }

  /// <summary>Reads a crop, refusing one that trims the whole image away, as reading the crop as the corners of the part
  /// kept does.</summary>
  private double[] Crop(Node node, string picture, string path)
  {
    const string shape = "[left, top, right, bottom] pixels from the image's edges, each 0 or more";
    double[] crop = node.AsNumbers(4, $"the {shape}");
    if (crop.Any(side => side < 0))
    {
      throw node.Error($"crop trims {shape}");
    }
    if (Pictures.Intrinsic(path, files) is double[] size)
    {
      string name = Path.GetFileName(picture);
      foreach ((int axis, string direction) in new[] { (0, "across"), (1, "down") })
      {
        double trimmed = crop[axis] + crop[axis + 2];
        if (trimmed >= size[axis])
        {
          throw node.Error(string.Create(CultureInfo.InvariantCulture,
            $"crop trims {trimmed} of {name}'s {size[axis]} pixels {direction}, leaving nothing; it trims {shape}"));
        }
      }
    }
    return crop;
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


}