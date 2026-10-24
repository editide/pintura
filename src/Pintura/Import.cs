using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>What an import wrote, and everything in the model's JSON it couldn't write back.</summary>
public record ImportResult(List<string> Findings);

/// <summary>Writes a presentation folder, with its theme inside, from the model's JSON as <c>--export-json</c> writes
/// it: effective values, with style names where the deck kept them.
///
/// Rather than working out the style ladder itself, the importer lets the loader do it. It writes the folder, loads
/// it, exports the load, and states a key wherever the two disagree, the theme first, then the layouts, then the
/// slides, so a value the theme can state is never repeated below it. The first load, with every style empty, tells
/// each style what it sets: what most of the things naming it differ from the bases by.</summary>
public partial class Importer
{
  private const int Passes = 8;

  private static readonly string[] Character = TextProperties.Character;
  private static readonly string[] ParagraphKeys = [.. TextProperties.Character, .. TextProperties.Paragraph];
  private static readonly HashSet<string> ChartKeys = [.. ChartProperties.Keys];
  /// <summary>File names, which the import chooses, and values its files carry rather than its keys.</summary>
  private static readonly HashSet<string> Unstated = ["chart", "data", "table", "categories"];
  private static readonly HashSet<string> Structure =
    ["shape", "picture", "type", "header_rows", "from", "to"];
  private static readonly string[] Sides = ["border_top", "border_bottom", "border_left", "border_right"];
  private static readonly string[] Borders =
    [.. Sides.SelectMany(side => new[] { side, $"{side}.color", $"{side}.width", $"{side}.dash" })];
  private static readonly string[] CellKeys = [.. TableFormatting.Text, "fill", .. Borders];
  /// <summary>What a key the JSON leaves out means, where both PowerPoint and Pintura default it; any other key the
  /// JSON leaves out is the import's to choose.</summary>
  private static readonly Dictionary<string, JsonNode> Defaults = new Dictionary<string, JsonNode>
  {
    ["bold"] = false, ["italic"] = false, ["underline"] = false, ["space_before"] = 0.0, ["space_after"] = 0.0,
    ["line_spacing"] = 1.0, ["fit"] = "none", ["wrap"] = true, ["legend"] = "none", ["data_labels.show"] = "none",
    ["data_labels.bold"] = false, ["markers.shape"] = "none"
  };

  private readonly JsonObject deck;
  private readonly string source;
  private readonly string folder;
  private readonly List<string> findings = new List<string>();

  private readonly OrderedDictionary<string, string> fields = new OrderedDictionary<string, string>();
  private readonly List<ItemDraft> items = new List<ItemDraft>();
  private readonly OrderedDictionary<string, OrderedDictionary<string, JsonNode?>> styles =
    new OrderedDictionary<string, OrderedDictionary<string, JsonNode?>>();
  private readonly OrderedDictionary<string, OrderedDictionary<string, JsonNode?>> chartStyles =
    new OrderedDictionary<string, OrderedDictionary<string, JsonNode?>>();
  private readonly OrderedDictionary<string, JsonNode?> chartBase = new OrderedDictionary<string, JsonNode?>();
  private readonly OrderedDictionary<string, TableStyleDraft> tableStyles =
    new OrderedDictionary<string, TableStyleDraft>();
  private readonly Dictionary<string, string> negativeStyles = new Dictionary<string, string>();
  private readonly OrderedDictionary<string, JsonNode?> shapeText = new OrderedDictionary<string, JsonNode?>();
  private readonly OrderedDictionary<string, JsonNode?> shapeBase = new OrderedDictionary<string, JsonNode?>();
  private readonly OrderedDictionary<string, JsonNode?> lineBase = new OrderedDictionary<string, JsonNode?>();
  private string? pictureFit;
  private readonly OrderedDictionary<string, ChartDraft> charts = new OrderedDictionary<string, ChartDraft>();
  private readonly OrderedDictionary<string, TableDraft> tables = new OrderedDictionary<string, TableDraft>();
  /// <summary>Each picture, by its path in the JSON, and the file it was copied to, from the presentation folder.
  /// </summary>
  private readonly Dictionary<string, string> media = new Dictionary<string, string>();

  private Importer(JsonObject deck, string source, string folder)
  {
    this.deck = deck;
    this.source = source;
    this.folder = folder;
  }

  /// <summary>Imports <paramref name="json"/> into <paramref name="folder"/>, which must be empty or missing, with
  /// pictures read from <paramref name="source"/>, the folder the JSON's picture paths are relative to.</summary>
  public static ImportResult Import(string json, string source, string folder)
  {
    string target = Path.GetFullPath(folder);
    RefuseUnlessEmpty(target);
    JsonObject deck = JsonNode.Parse(json)!.AsObject();
    // The import rewrites its folder on every pass, so it works beside the destination and moves in once it loads.
    string scratch = Scratch(target);
    try
    {
      ImportResult result = new Importer(deck, Path.GetFullPath(source), scratch).Run();
      Place(scratch, target);
      return result;
    }
    finally
    {
      if (Directory.Exists(scratch))
      {
        Directory.Delete(scratch, true);
      }
    }
  }

  internal static void RefuseUnlessEmpty(string target)
  {
    if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
    {
      throw new ArgumentException($"{target} isn't empty; pick an empty folder, or one that doesn't exist yet");
    }
  }

  /// <summary>A folder beside <paramref name="target"/>, on the same volume, so <see cref="Place"/> can move it.
  /// </summary>
  internal static string Scratch(string target)
  {
    return Path.Combine(Path.GetDirectoryName(target)!,
      $".{Path.GetFileName(target)}.importing-{Guid.NewGuid().ToString("N")[..8]}");
  }

  internal static void Place(string from, string target)
  {
    RefuseUnlessEmpty(target);
    if (Directory.Exists(target))
    {
      Directory.Delete(target);
    }
    Directory.Move(from, target);
  }

  /// <summary>The model's JSON for a loaded presentation, as the import compares it.</summary>
  public static JsonObject Snapshot(Presentation presentation)
  {
    return JsonSerializer.SerializeToNode(ExportBuilder.Build(presentation), PayloadJson.Options)!.AsObject();
  }

  private ImportResult Run()
  {
    Draft();
    JsonObject actual = WriteAndLoad();
    InferStyles(actual);
    for (int pass = 0; pass < Passes; pass++)
    {
      actual = WriteAndLoad();
      Dictionary<string, JsonObject> loaded = Items(actual).ToDictionary(entry => entry.Key, entry => entry.Item);
      var residues = items
        .Select(draft => (Draft: draft, Residue: Residue(draft, loaded.GetValueOrDefault(draft.Key))))
        .Where(entry => entry.Residue.Count > 0)
        .ToList();
      if (residues.Count == 0)
      {
        break;
      }
      int level = residues.Min(entry => entry.Draft.Level);
      foreach ((ItemDraft draft, Dictionary<string, JsonNode?> residue) in residues.Where(entry =>
        entry.Draft.Level == level))
      {
        foreach ((string key, JsonNode? value) in residue)
        {
          // A label's position resolves once labels show, and a pie's ring takes none of its own.
          if (key.EndsWith("data_labels.position", StringComparison.Ordinal) &&
            residue.ContainsKey(key.Replace("position", "show")))
          {
            continue;
          }
          State(draft, key, value);
        }
      }
    }

    Dictionary<string, JsonObject> settled = Items(actual).ToDictionary(entry => entry.Key, entry => entry.Item);
    StyleText(settled);
    MergeNegativeStyles();
    actual = WriteAndLoad();
    Verify(actual);
    return new ImportResult(findings.Distinct().ToList());
  }

  #region Drafting from the JSON

  private sealed class ItemDraft
  {
    public required string Key { get; init; }
    /// <summary>0 for the theme, 1 for a layout, 2 for a slide.</summary>
    public required int Level { get; init; }
    public required JsonObject Wanted { get; init; }
    public string Id => (string)Wanted["id"]!;
    public string Kind => (string)Wanted["kind"]!;
    public string? Role => (string?)Wanted["role"];
    public JsonObject Properties => Wanted["properties"]!.AsObject();
    public JsonArray? Paragraphs => Wanted["paragraphs"]?.AsArray();
    public string? Style { get; set; }
    /// <summary>Keys the item states, dotted as the JSON writes them.</summary>
    public OrderedDictionary<string, JsonNode?> Own { get; } = new OrderedDictionary<string, JsonNode?>();
    /// <summary>A placeholder this level defines rather than overrides: the theme's, or a layout's own.</summary>
    public bool Defines { get; set; }
    public string? Picture { get; set; }
    public string? Chart { get; set; }
    public string? Table { get; set; }
    /// <summary>Each paragraph's style, once the text is styled; null until then, when the markup is plain.</summary>
    public List<string?>? ParagraphStyles { get; set; }
    public List<List<RunSpan>>? Spans { get; set; }
  }

  /// <summary>What a run states in its markup: bold and italic, its span's styles, and its span's attributes.
  /// </summary>
  private sealed record RunSpan(bool Bold, bool Italic, List<string> Styles, OrderedDictionary<string, JsonNode?> Own);

  private sealed class ChartDraft
  {
    public required string Stem { get; init; }
    public required string Data { get; init; }
    public required string Type { get; init; }
    public string? Style { get; init; }
    public bool SeriesInColumns { get; init; }
    public OrderedDictionary<string, JsonNode?> Own { get; } = new OrderedDictionary<string, JsonNode?>();
    /// <summary>Each series' own keys, by name, dotted as the JSON writes them.</summary>
    public OrderedDictionary<string, OrderedDictionary<string, JsonNode?>> Series { get; } =
      new OrderedDictionary<string, OrderedDictionary<string, JsonNode?>>();
    public string Csv { get; init; } = "";
  }

  /// <summary>A table's file: each cell's text as drawn, and what its columns, rows, and cells state, dotted as the
  /// JSON writes them. Rows and columns are numbered from 1.</summary>
  private sealed class TableDraft
  {
    public required string Stem { get; init; }
    public required string Data { get; init; }
    public string? Style { get; init; }
    public int HeaderRows { get; init; }
    /// <summary>Each cell's markup, empty where a merge covers it.</summary>
    public required string[,] Text { get; init; }
    public List<object?> Merges { get; } = new List<object?>();
    public Dictionary<int, OrderedDictionary<string, JsonNode?>> Columns { get; } =
      new Dictionary<int, OrderedDictionary<string, JsonNode?>>();
    public Dictionary<int, OrderedDictionary<string, JsonNode?>> Rows { get; } =
      new Dictionary<int, OrderedDictionary<string, JsonNode?>>();
    public OrderedDictionary<(int Row, int Column), OrderedDictionary<string, JsonNode?>> Cells { get; } =
      new OrderedDictionary<(int Row, int Column), OrderedDictionary<string, JsonNode?>>();
    public int RowCount => Text.GetLength(0);
    public int ColumnCount => Text.GetLength(1);

    public OrderedDictionary<string, JsonNode?> Column(int column)
    {
      return Columns.TryGetValue(column, out var own) ? own : Columns[column] = new OrderedDictionary<string, JsonNode?>();
    }

    public OrderedDictionary<string, JsonNode?> Row(int row)
    {
      return Rows.TryGetValue(row, out var own) ? own : Rows[row] = new OrderedDictionary<string, JsonNode?>();
    }

    public OrderedDictionary<string, JsonNode?> Cell(int row, int column)
    {
      return Cells.TryGetValue((row, column), out var own)
        ? own
        : Cells[(row, column)] = new OrderedDictionary<string, JsonNode?>();
    }


  }

  /// <summary>A table style's row styles, dotted as the JSON writes them, and the color of its banded rows.</summary>
  private sealed class TableStyleDraft
  {
    public OrderedDictionary<string, OrderedDictionary<string, JsonNode?>> Rows { get; } =
      new OrderedDictionary<string, OrderedDictionary<string, JsonNode?>>();
    public JsonNode? Banded { get; set; }
  }

  private JsonObject Theme => deck["theme"]!.AsObject();

  private IEnumerable<JsonObject> Layouts => deck["layouts"]!.AsArray().Select(layout => layout!.AsObject());

  private IEnumerable<JsonObject> Slides => deck["slides"]!.AsArray().Select(slide => slide!.AsObject());

  /// <summary>Every item in a deck's JSON, keyed by where it sits, with the level that owns it.</summary>
  private static IEnumerable<(string Key, int Level, JsonObject Item)> Items(JsonObject deck)
  {
    JsonObject theme = deck["theme"]!.AsObject();
    foreach ((string key, JsonObject item) in Scope("theme", theme["title"], theme["placeholders"],
      theme["elements"]))
    {
      yield return (key, 0, item);
    }
    foreach (JsonObject layout in deck["layouts"]!.AsArray().Select(node => node!.AsObject()))
    {
      foreach ((string key, JsonObject item) in Scope($"layout:{layout["id"]}", layout["title"],
        layout["placeholders"], layout["elements"]))
      {
        yield return (key, 1, item);
      }
    }
    foreach (JsonObject slide in deck["slides"]!.AsArray().Select(node => node!.AsObject()))
    {
      foreach (JsonObject item in slide["items"]?.AsArray().Select(node => node!.AsObject()) ?? [])
      {
        string role = (string?)item["role"] ?? "element";
        yield return ($"slide:{slide["id"]}/{role}/{item["id"]}", 2, item);
      }
    }
  }

  private static IEnumerable<(string Key, JsonObject Item)> Scope(string scope, JsonNode? title, JsonNode? placeholders,
    JsonNode? elements)
  {
    if (title is JsonObject titled)
    {
      yield return ($"{scope}/title/title", titled);
    }
    foreach (JsonObject item in placeholders?.AsArray().Select(node => node!.AsObject()) ?? [])
    {
      yield return ($"{scope}/placeholder/{item["id"]}", item);
    }
    foreach (JsonObject item in elements?.AsArray().Select(node => node!.AsObject()) ?? [])
    {
      yield return ($"{scope}/element/{item["id"]}", item);
    }
  }

  private void Draft()
  {
    foreach (JsonObject slide in Slides.Where(slide => slide["error"] is not null))
    {
      findings.Add($"slide {slide["id"]} didn't load where it came from: {slide["error"]}");
    }
    foreach ((string key, int level, JsonObject item) in Items(deck))
    {
      var draft = new ItemDraft { Key = key, Level = level, Wanted = item };
      items.Add(draft);
      Fields(item);
    }
    Levels();
    Bases();
    Formats();
    Dictionary<string, JsonObject> theme = Items(deck).Where(entry => entry.Level == 0)
      .ToDictionary(entry => entry.Key["theme".Length..], entry => entry.Item);
    Dictionary<string, JsonObject> layouts = Items(deck).Where(entry => entry.Level == 1)
      .ToDictionary(entry => entry.Key, entry => entry.Item);
    foreach (ItemDraft draft in items)
    {
      Skeleton(draft, theme, layouts);
    }
  }

  private void Fields(JsonObject item)
  {
    foreach (JsonObject run in Runs(item))
    {
      if (Field(run) is string field && !PresentationLoader.BuiltInFields.Contains(field))
      {
        fields.TryAdd(field, (string?)run["text"] ?? "");
      }
    }
  }

  private static IEnumerable<JsonObject> Runs(JsonObject item)
  {
    return Paragraphs(item).SelectMany(paragraph => paragraph["runs"]!.AsArray()).Select(run => run!.AsObject());
  }

  /// <summary>An item's paragraphs, with its table's cells'.</summary>
  private static IEnumerable<JsonObject> Paragraphs(JsonObject item)
  {
    IEnumerable<JsonNode?> paragraphs = item["paragraphs"]?.AsArray() ?? [];
    IEnumerable<JsonNode?> cells = item["properties"]?["cells"]?.AsArray()
      .SelectMany(cell => cell!["paragraphs"]!.AsArray()) ?? [];
    return paragraphs.Concat(cells).Select(paragraph => paragraph!.AsObject());
  }

  /// <summary>States a level for every depth the deck's text reaches, each a step past the one above, since the
  /// theme's come from slides' text boxes alone and a cell or a layout's text can go deeper; the styles made for its
  /// paragraphs then set what differs.</summary>
  private void Levels()
  {
    const double Step = 18;
    JsonObject stated = Theme["levels"]!.AsObject();
    int deepest = Items(deck).SelectMany(entry => Paragraphs(entry.Item))
      .Select(paragraph => (int)paragraph["level"]!)
      .Concat(stated.Select(level => int.Parse(level.Key, CultureInfo.InvariantCulture))).DefaultIfEmpty(0).Max();
    // The loader reads levels numbered from 1 in order, so they are written that way, a missing one filled in.
    var levels = new JsonObject();
    JsonObject? previous = null;
    for (int level = 1; level <= deepest; level++)
    {
      string key = level.ToString(CultureInfo.InvariantCulture);
      JsonObject next;
      if (stated[key] is JsonObject own)
      {
        next = own.DeepClone().AsObject();
      }
      else
      {
        next = previous?.DeepClone().AsObject() ??
          new JsonObject { ["bullet"] = "•", ["indent"] = 0.0, ["properties"] = new JsonObject() };
        next["indent"] = ((double?)next["indent"] ?? 0) + Step;
        next.Remove("hanging");
      }
      levels[key] = next;
      previous = next;
    }
    Theme["levels"] = levels;
  }

  /// <summary>The bases every shape, line, and picture starts from, and the text in shapes, each what most of them
  /// share.</summary>
  private void Bases()
  {
    List<ItemDraft> shapes = items.Where(item => item.Kind == "shape").ToList();
    string align = (string)Theme["text"]!["align"]!;
    string valign = (string)Theme["text"]!["valign"]!;
    List<ItemDraft> worded = shapes.Where(shape => shape.Paragraphs is { Count: > 0 }).ToList();
    if (Mode(worded.Select(shape => shape.Properties["align"])) is JsonNode shapeAlign &&
      (string)shapeAlign! != align)
    {
      shapeText["align"] = shapeAlign;
    }
    if (Mode(worded.Select(shape => shape.Properties["valign"])) is JsonNode shapeValign &&
      (string)shapeValign! != valign)
    {
      shapeText["valign"] = shapeValign;
    }
    if (Mode(shapes.Select(shape => shape.Properties["fill"])) is JsonNode fill)
    {
      shapeBase["fill"] = fill;
    }
    if (Mode(shapes.Select(shape => Group(shape.Properties, "stroke"))) is JsonObject shapeStroke)
    {
      foreach ((string key, JsonNode? value) in shapeStroke)
      {
        shapeBase[key] = value?.DeepClone();
      }
    }
    // A shape without a shadow states none of its keys, so it couldn't clear one from the base: only a shadow every
    // shape has goes there.
    List<JsonObject?> shadows = shapes.Select(shape => Group(shape.Properties, "shadow")).ToList();
    if (shadows.Count > 0 && shadows.All(shadow => shadow is not null && JsonNode.DeepEquals(shadow, shadows[0])))
    {
      foreach ((string key, JsonNode? value) in shadows[0]!)
      {
        shapeBase[key] = value?.DeepClone();
      }
    }
    if (Mode(items.Where(item => item.Kind == "line").Select(line => Group(line.Properties, "stroke"))) is
      JsonObject stroke)
    {
      foreach ((string key, JsonNode? value) in stroke)
      {
        lineBase[key] = value?.DeepClone();
      }
    }
    pictureFit = (string?)Mode(items.Where(item => item.Kind == "picture").Select(picture =>
      picture.Properties["fit"]));
  }

  /// <summary>A key such as <c>stroke</c> and its dotted keys, or null where the item sets none of them.</summary>
  private static JsonObject? Group(JsonObject properties, string group)
  {
    var found = new JsonObject();
    foreach ((string key, JsonNode? value) in properties)
    {
      if (value is not null && (key == group || key.StartsWith($"{group}.", StringComparison.Ordinal)))
      {
        found[key] = value.DeepClone();
      }
    }
    return found.Count == 0 ? null : found;
  }

  /// <summary>The value most of <paramref name="values"/> share, ties going to the first seen, or null when most
  /// have none.</summary>
  private static JsonNode? Mode(IEnumerable<JsonNode?> values)
  {
    var counts = new Dictionary<string, (int Count, int First, JsonNode? Value)>();
    int index = 0;
    foreach (JsonNode? value in values)
    {
      string key = value?.ToJsonString() ?? "";
      counts[key] = counts.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.First, seen.Value) : (1, index, value);
      index++;
    }
    if (counts.Count == 0)
    {
      return null;
    }
    return counts.Values.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.First).First().Value;
  }

  /// <summary>Whether a loaded value is the one wanted, a key the JSON leaves out being its default or free.</summary>
  private static bool Same(string key, JsonNode? wanted, JsonNode? loaded)
  {
    if (wanted is null && !Defaults.ContainsKey(key))
    {
      return true;
    }
    return JsonNode.DeepEquals(wanted ?? Defaults[key], loaded ?? Defaults.GetValueOrDefault(key));
  }

  /// <summary>The value wanted for a key, its default when the JSON leaves it out.</summary>
  private static JsonNode? Wanted(string key, JsonNode? wanted)
  {
    return (wanted ?? Defaults.GetValueOrDefault(key))?.DeepClone();
  }

  /// <summary>Formats as the theme names them, with each negative color a style of its own, since a format names the
  /// style its negatives take.</summary>
  private void Formats()
  {
    foreach ((string _, JsonNode? format) in Theme["formats"]!.AsObject())
    {
      if (format!["negativeColor"] is JsonNode color && !negativeStyles.ContainsKey(color.ToJsonString()))
      {
        string name = Unique(negativeStyles.Count == 0 ? "negative" : "negative_" + (negativeStyles.Count + 1),
          HintedStyles().ToHashSet());
        negativeStyles[color.ToJsonString()] = name;
        styles[name] = new OrderedDictionary<string, JsonNode?> { ["color"] = color.DeepClone() };
      }
    }
  }

  private IEnumerable<string> HintedStyles()
  {
    foreach (ItemDraft item in items)
    {
      if (item.Kind is "text" or "shape" && (string?)item.Properties["style"] is string style)
      {
        yield return style;
      }
      foreach (JsonObject paragraph in item.Paragraphs?.Select(node => node!.AsObject()) ?? [])
      {
        if ((string?)paragraph["style"] is string own)
        {
          yield return own;
        }
      }
      foreach (JsonObject run in Runs(item.Wanted))
      {
        foreach (string name in run["styles"]?.AsArray().Select(node => (string)node!) ?? [])
        {
          yield return name;
        }
      }
    }
  }

  private static string Unique(string name, ISet<string> taken)
  {
    string candidate = name;
    for (int suffix = 2; taken.Contains(candidate); suffix++)
    {
      candidate = $"{name}_{suffix}";
    }
    return candidate;
  }

  /// <summary>What each item needs to load at all: its kind and content, its frame, and the style it names; the keys
  /// it states come from comparing loads.</summary>
  private void Skeleton(ItemDraft draft, Dictionary<string, JsonObject> theme, Dictionary<string, JsonObject> layouts)
  {
    JsonObject properties = draft.Properties;
    foreach (string name in HintedStyles())
    {
      styles.TryAdd(name, new OrderedDictionary<string, JsonNode?>());
    }
    if (draft.Role is not null)
    {
      string name = draft.Role == "title" ? "title/title" : $"placeholder/{draft.Id}";
      JsonObject? above = draft.Level switch
      {
        0 => null,
        1 => theme.GetValueOrDefault($"/{name}"),
        _ => layouts.GetValueOrDefault($"layout:{Slide(draft)["layout"]}/{name}") ??
          theme.GetValueOrDefault($"/{name}")
      };
      draft.Defines = above is null && draft.Level < 2;
      string? style = (string?)properties["style"];
      if (style is not null && (above is null || (string?)above["properties"]!["style"] != style))
      {
        draft.Style = style;
      }
      JsonNode? frame = properties["frame"];
      if (draft.Defines || (draft.Level == 1 && draft.Role == "title" && draft.Style is null) ||
        (above is not null && !JsonNode.DeepEquals(above["properties"]!["frame"], frame)))
      {
        draft.Own["frame"] = frame?.DeepClone();
      }
      return;
    }

    draft.Style = draft.Kind is "text" or "shape" ? (string?)properties["style"] : null;
    if (properties["frame"] is JsonNode rect)
    {
      draft.Own["frame"] = rect.DeepClone();
    }
    if (properties["tail"] is JsonNode tail)
    {
      draft.Own["tail"] = tail.DeepClone();
    }
    switch (draft.Kind)
    {
      case "picture":
        draft.Picture = Picture(draft, (string)properties["picture"]!);
        break;
      case "chart":
        draft.Chart = Chart(draft);
        break;
      case "table":
        draft.Table = Table(draft);
        break;
    }
  }

  private JsonObject Slide(ItemDraft draft)
  {
    string id = draft.Key["slide:".Length..draft.Key.IndexOf('/')];
    return Slides.Single(slide => (string)slide["id"]! == id);
  }

  /// <summary>Copies a picture beside the theme when the theme or a layout shows it, and beside the slides
  /// otherwise, returning its path from the presentation folder.</summary>
  private string Picture(ItemDraft draft, string path)
  {
    string target = draft.Level < 2 ? "theme/media" : "media";
    string key = $"{target}|{path}";
    if (media.TryGetValue(key, out string? copied))
    {
      draft.Properties["picture"] = copied;
      return copied;
    }
    string from = Path.GetFullPath(Path.Combine(source, path));
    string name = Path.GetFileName(path);
    var taken = media.Values.ToHashSet();
    string stem = Path.GetFileNameWithoutExtension(name);
    string extension = Path.GetExtension(name);
    string written = $"{target}/{name}";
    for (int suffix = 2; taken.Contains(written); suffix++)
    {
      written = $"{target}/{stem}-{suffix}{extension}";
    }
    if (!File.Exists(from))
    {
      findings.Add($"{draft.Key}: no picture at {path}");
    }
    media[key] = written;
    pictureSources[written] = from;
    draft.Properties["picture"] = written;
    return written;
  }

  private readonly Dictionary<string, string> pictureSources = new Dictionary<string, string>();

  private string Chart(ItemDraft draft)
  {
    JsonObject properties = draft.Properties;
    string stem = Unique((string?)properties["chart"] ?? draft.Id, charts.Keys.ToHashSet());
    string type = (string)properties["type"]!;
    string family = type switch
    {
      "pie" or "doughnut" => "pie",
      _ when type.EndsWith("line", StringComparison.Ordinal) => "line",
      _ when type.EndsWith("bar", StringComparison.Ordinal) => "bar",
      _ => "column"
    };
    List<string> categories = properties["categories"]!.AsArray().Select(node => (string)node!).ToList();
    var grid = new List<List<string>> { categories.Prepend("series").ToList() };
    var series = new OrderedDictionary<string, OrderedDictionary<string, JsonNode?>>();
    foreach (JsonObject entry in properties["series"]!.AsArray().Select(node => node!.AsObject()))
    {
      string name = (string)entry["name"]!;
      grid.Add(entry["values"]!.AsArray()
        .Select(value => value is null ? "" : YamlWriter.Number((double)value))
        .Prepend(name)
        .ToList());
      var options = new OrderedDictionary<string, JsonNode?>();
      if ((string)entry["type"]! != family)
      {
        options["type"] = (string)entry["type"]!;
      }
      if ((bool)entry["secondary"]!)
      {
        options["axis"] = "secondary";
      }
      foreach ((string key, JsonNode? color) in entry["points"]?.AsObject() ?? new JsonObject())
      {
        options[key] = color?.DeepClone();
      }
      series[name] = options;
    }
    bool columns = (string?)properties["series_in"] == "columns";
    var csv = new StringBuilder();
    int lines = columns ? grid[0].Count : grid.Count;
    for (int line = 0; line < lines; line++)
    {
      IEnumerable<string> cells = columns ? grid.Select(row => row[line]) : grid[line];
      csv.AppendLine(string.Join(",", cells.Select(CsvCell)));
    }
    string data = CsvPath(properties, stem);
    string text = csv.ToString();
    ChartDraft? sharing = charts.Values.FirstOrDefault(chart => chart.Data == data);
    if (sharing is not null && sharing.Csv != text)
    {
      var taken = charts.Values.Select(chart => chart.Data).ToHashSet();
      data = $"{stem}.csv";
      for (int suffix = 2; taken.Contains(data); suffix++)
      {
        data = $"{stem}-{suffix}.csv";
      }
    }
    var chart = new ChartDraft
    {
      Stem = stem,
      Data = data,
      Type = type,
      Style = (string?)properties["style"],
      SeriesInColumns = columns,
      Csv = text
    };
    foreach ((string key, OrderedDictionary<string, JsonNode?> value) in series)
    {
      chart.Series[key] = value;
    }
    if (chart.Style is not null)
    {
      chartStyles.TryAdd(chart.Style, new OrderedDictionary<string, JsonNode?>());
    }
    charts[stem] = chart;
    return stem;
  }

  /// <summary>A table's text, merges, and the row styles its rows name; what its columns, rows, and cells state comes
  /// from comparing loads.</summary>
  private string Table(ItemDraft draft)
  {
    JsonObject properties = draft.Properties;
    string stem = Unique((string?)properties["table"] ?? draft.Id, tables.Keys.ToHashSet());
    int rows = properties["heights"]!.AsArray().Count;
    int columns = properties["widths"]!.AsArray().Count;
    var table = new TableDraft
    {
      Stem = stem,
      Data = CsvPath(properties, stem),
      Style = (string?)properties["style"],
      HeaderRows = (int?)properties["header_rows"] ?? 0,
      Text = new string[rows, columns]
    };
    TableStyleDraft? style = null;
    if (table.Style is not null)
    {
      style = tableStyles.TryGetValue(table.Style, out TableStyleDraft? existing)
        ? existing
        : tableStyles[table.Style] = new TableStyleDraft();
    }
    foreach (JsonObject cell in Cells(properties))
    {
      int row = (int)cell["row"]!;
      int column = (int)cell["column"]!;
      table.Text[row - 1, column - 1] = Markup(cell["paragraphs"]!.AsArray(), null, null, false);
      if ((int)cell["rows"]! > 1 || (int)cell["columns"]! > 1)
      {
        var merge = new OrderedDictionary<string, object?> { ["row"] = row, ["column"] = column };
        if ((int)cell["rows"]! > 1)
        {
          merge["rows"] = (int)cell["rows"]!;
        }
        if ((int)cell["columns"]! > 1)
        {
          merge["columns"] = (int)cell["columns"]!;
        }
        table.Merges.Add(merge);
      }
      if ((string?)cell["properties"]!["row_style"] is string rowStyle)
      {
        style?.Rows.TryAdd(rowStyle, new OrderedDictionary<string, JsonNode?>());
        if (row > table.HeaderRows || rowStyle != "header")
        {
          table.Row(row)["style"] = rowStyle;
        }
      }
    }
    for (int column = 1; column <= columns; column++)
    {
      table.Column(column)["width"] = properties["widths"]![column - 1]?.DeepClone();
    }
    for (int row = 1; row <= rows; row++)
    {
      if (properties["heights"]![row - 1] is JsonNode height)
      {
        table.Row(row)["height"] = height.DeepClone();
      }
    }
    tables[stem] = table;
    return stem;
  }

  private static IEnumerable<JsonObject> Cells(JsonObject properties)
  {
    return properties["cells"]!.AsArray().Select(node => node!.AsObject());
  }

  /// <summary>A CSV cell, quoted where it holds a separator, a quote, a line break, or spaces at either end, which an
  /// unquoted cell loses.</summary>
  private static string CsvCell(string text)
  {
    return text.IndexOfAny([',', '"', '\n']) >= 0 || text.Trim().Length != text.Length
      ? $"\"{text.Replace("\"", "\"\"")}\""
      : text;
  }

  #endregion

  #region Comparing loads

  /// <summary>Loads what the drafts write, as the model's JSON.</summary>
  private JsonObject WriteAndLoad()
  {
    // One element the format can't hold costs that element, not the whole import.
    while (true)
    {
      Write();
      (Presentation? presentation, LoadException? error, string? scope) = Attempt();
      if (scope is null)
      {
        return Snapshot(presentation!);
      }
      ItemDraft culprit = Culprit(scope) ??
        throw new InvalidDataException($"the import wrote a {scope.Split(':')[0]} that doesn't load: {error!.Message}");
      findings.Add($"{culprit.Key}: left out, since it doesn't load as written: {error!.Problem}");
      items.Remove(culprit);
      (culprit.Wanted.Parent as JsonArray)?.Remove(culprit.Wanted);
    }
  }

  /// <summary>Loads what was written, with the scope of what fails to, <c>slide:id/</c> or <c>layout:id/</c>, or
  /// null when everything loads.</summary>
  private (Presentation? Presentation, LoadException? Error, string? Scope) Attempt()
  {
    try
    {
      Presentation presentation = PresentationLoader.Load(folder);
      return presentation.Slides.FirstOrDefault(slide => slide.Error is not null) is Slide broken
        ? (presentation, broken.Error, $"slide:{broken.Id}/")
        : (presentation, null, null);
    }
    catch (LoadException error) when (LayoutFile().Match(error.Origin.File) is { Success: true } match)
    {
      return (null, error, $"layout:{match.Groups[1].Value}/");
    }
  }

  [GeneratedRegex(@"theme/layouts/([^/]+)\.yaml$")]
  private static partial Regex LayoutFile();

  /// <summary>An item of <paramref name="scope"/> it can't load with, or null when none is: taking its items away in
  /// turn until it loads, the last taken, since several may each fail alone and <see cref="WriteAndLoad"/> asks again.
  /// </summary>
  private ItemDraft? Culprit(string scope)
  {
    var taken = new List<(int Index, ItemDraft Item)>();
    try
    {
      foreach (ItemDraft candidate in items.Where(item => item.Key.StartsWith(scope, StringComparison.Ordinal))
        .ToList())
      {
        int index = items.IndexOf(candidate);
        items.RemoveAt(index);
        taken.Add((index, candidate));
        Write();
        if (Attempt().Scope != scope)
        {
          return candidate;
        }
      }
      return null;
    }
    finally
    {
      foreach ((int index, ItemDraft item) in Enumerable.Reverse(taken))
      {
        items.Insert(index, item);
      }
    }
  }

  /// <summary>The keys whose values differ, with the value wanted, or null for a key only the load has. A chart's
  /// series and a table's cells are keyed <c>series/name/key</c> and <c>cells/row/column/key</c>.</summary>
  private static Dictionary<string, JsonNode?> Residue(ItemDraft draft, JsonObject? actual)
  {
    var residue = new Dictionary<string, JsonNode?>();
    JsonObject properties = draft.Properties;
    JsonObject loaded = actual?["properties"]?.AsObject() ?? new JsonObject();
    foreach (string key in properties.Select(pair => pair.Key).Union(loaded.Select(pair => pair.Key)))
    {
      if (!Unstated.Contains(key) && key is not ("series" or "cells") && !Same(key, properties[key], loaded[key]))
      {
        residue[key] = Wanted(key, properties[key]);
      }
    }
    // A series takes the chart's labels and markers, so its own wait until the chart's settle.
    if (properties["series"] is JsonArray series && !residue.Keys.Any(ChartKeys.Contains))
    {
      Dictionary<string, JsonObject> loadedSeries = (loaded["series"]?.AsArray() ?? [])
        .ToDictionary(entry => (string)entry!["name"]!, entry => entry!.AsObject());
      foreach (JsonObject entry in series.Select(node => node!.AsObject()))
      {
        string name = (string)entry["name"]!;
        JsonObject other = loadedSeries.GetValueOrDefault(name) ?? new JsonObject();
        foreach (string group in new[] { "labels", "markers" })
        {
          JsonObject wanted = entry[group]?.AsObject() ?? new JsonObject();
          JsonObject had = other[group]?.AsObject() ?? new JsonObject();
          foreach (string key in wanted.Select(pair => pair.Key).Union(had.Select(pair => pair.Key)))
          {
            if (!Same(key, wanted[key], had[key]))
            {
              residue[$"series/{name}/{key}"] = Wanted(key, wanted[key]);
            }
          }
        }
      }
    }
    if (properties["cells"] is JsonArray)
    {
      Dictionary<(int, int), JsonObject> loadedCells = loaded["cells"] is JsonArray
        ? Cells(loaded).ToDictionary(cell => ((int)cell["row"]!, (int)cell["column"]!))
        : new Dictionary<(int, int), JsonObject>();
      foreach (JsonObject cell in Cells(properties))
      {
        (int row, int column) = ((int)cell["row"]!, (int)cell["column"]!);
        JsonObject wanted = cell["properties"]!.AsObject();
        JsonObject had = loadedCells.GetValueOrDefault((row, column))?["properties"]?.AsObject() ?? new JsonObject();
        foreach (string key in wanted.Select(pair => pair.Key).Union(had.Select(pair => pair.Key)))
        {
          if (!Same(key, wanted[key], had[key]))
          {
            residue[$"cells/{row}/{column}/{key}"] = Wanted(key, wanted[key]);
          }
        }
      }
    }
    return residue;
  }

  /// <summary>States <paramref name="key"/> where the item's level can: on the item, its chart, or its table.
  /// </summary>
  private void State(ItemDraft draft, string key, JsonNode? value)
  {
    if (key.StartsWith("series/", StringComparison.Ordinal))
    {
      string[] parts = key.Split('/', 3);
      if (value is not null)
      {
        charts[draft.Chart!].Series[parts[1]][parts[2]] = value.DeepClone();
      }
      else
      {
        findings.Add($"{draft.Key}: series {parts[1]} can't leave out {parts[2]}");
      }
      return;
    }
    if (key.StartsWith("cells/", StringComparison.Ordinal))
    {
      string[] parts = key.Split('/', 4);
      StateCell(draft, int.Parse(parts[1], CultureInfo.InvariantCulture),
        int.Parse(parts[2], CultureInfo.InvariantCulture), parts[3], value);
      return;
    }
    if (value is null)
    {
      return;
    }
    if (key == "style")
    {
      draft.Style = (string)value!;
      return;
    }
    if (draft.Chart is string stem && ChartKeys.Contains(key))
    {
      charts[stem].Own[key] = value.DeepClone();
      return;
    }
    if (draft.Table is string table)
    {
      TableDraft owned = tables[table];
      switch (key)
      {
        case "widths":
          for (int column = 1; column <= owned.ColumnCount; column++)
          {
            owned.Column(column)["width"] = value[column - 1]?.DeepClone();
          }
          return;
        case "heights":
          for (int row = 1; row <= owned.RowCount; row++)
          {
            if (value[row - 1] is JsonNode height)
            {
              owned.Row(row)["height"] = height.DeepClone();
            }
            else
            {
              owned.Row(row).Remove("height");
            }
          }
          return;
      }
    }
    if (Structure.Contains(key) || (draft.Kind is "chart" or "table" && key != "frame" && key is not
      ("rotate" or "flip" or "opacity")))
    {
      findings.Add($"{draft.Key}: can't state {key}");
      return;
    }
    draft.Own[key] = value.DeepClone();
  }

  /// <summary>States a cell's key on the cell, or on its row for what only rows set, its row style, and a top or bottom
  /// border every cell on that edge of the row wants alike, a bottom border going on the last row a merge spans.
  /// </summary>
  private void StateCell(ItemDraft draft, int row, int column, string key, JsonNode? value)
  {
    TableDraft table = tables[draft.Table!];
    JsonObject cell = Cells(draft.Properties).Single(cell => (int)cell["row"]! == row && (int)cell["column"]! == column);
    int last = row + (int)cell["rows"]! - 1;
    if (key == "row_style")
    {
      if (value is not null)
      {
        table.Row(row)["style"] = value.DeepClone();
      }
      else
      {
        findings.Add($"{draft.Key}: row {row} can't leave out its table style's header");
      }
      return;
    }
    if (key.StartsWith("border_", StringComparison.Ordinal))
    {
      string side = Sides.First(side => key.StartsWith(side, StringComparison.Ordinal));
      int? edge = side switch { "border_top" => row, "border_bottom" => last, _ => null };
      bool alike = edge is int at && Cells(draft.Properties)
        .Where(other => (side == "border_top" ? (int)other["row"]! : (int)other["row"]! + (int)other["rows"]! - 1) == at)
        .All(other => JsonNode.DeepEquals(other["properties"]![key], value));
      OrderedDictionary<string, JsonNode?> target = alike ? table.Row(edge!.Value) : table.Cell(row, column);
      if (value is null)
      {
        if (key == side)
        {
          target.Remove(side);
        }
        return;
      }
      if (key == side)
      {
        foreach (string nested in Borders.Where(border => border.StartsWith($"{side}.", StringComparison.Ordinal)))
        {
          target.Remove(nested);
        }
      }
      else
      {
        target.Remove(side);
      }
      target[key] = value.DeepClone();
      return;
    }
    if (value is null)
    {
      findings.Add($"{draft.Key}: cell {row},{column} can't leave out {key}");
      return;
    }
    table.Cell(row, column)[key] = value.DeepClone();
  }

  /// <summary>What each style sets: what most of the things naming it differ from the first load by, in which every
  /// style was empty.</summary>
  private void InferStyles(JsonObject actual)
  {
    Dictionary<string, JsonObject> loaded = Items(actual).ToDictionary(entry => entry.Key, entry => entry.Item);
    var observed = new Dictionary<string, List<Dictionary<string, JsonNode?>>>();
    foreach (ItemDraft draft in items.Where(draft => draft.Kind is "text" or "shape"))
    {
      if ((string?)draft.Properties["style"] is not string style || !loaded.TryGetValue(draft.Key, out JsonObject? item))
      {
        continue;
      }
      JsonObject properties = item["properties"]!.AsObject();
      var difference = new Dictionary<string, JsonNode?>();
      foreach (string key in TextProperties.Style)
      {
        if (!Same(key, draft.Properties[key], properties[key]))
        {
          difference[key] = Wanted(key, draft.Properties[key]);
        }
      }
      (observed.TryGetValue(style, out var list) ? list : observed[style] = new List<Dictionary<string, JsonNode?>>())
        .Add(difference);
    }
    foreach ((string style, List<Dictionary<string, JsonNode?>> differences) in observed)
    {
      foreach ((string key, JsonNode? value) in Consensus(differences))
      {
        styles[style].TryAdd(key, value);
      }
    }

    List<(ItemDraft Draft, Dictionary<string, JsonNode?> Difference)> charted = items
      .Where(draft => draft.Chart is not null && loaded.ContainsKey(draft.Key))
      .Select(draft => (draft, ChartDifference(draft.Properties, loaded[draft.Key]["properties"]!.AsObject())))
      .ToList();
    if (charted.Count > 1)
    {
      foreach ((string key, JsonNode? value) in charted[0].Difference.Where(pair => charted.All(chart =>
        chart.Difference.TryGetValue(pair.Key, out JsonNode? other) && JsonNode.DeepEquals(other, pair.Value))))
      {
        chartBase[key] = value?.DeepClone();
      }
    }
    foreach (IGrouping<string, (ItemDraft Draft, Dictionary<string, JsonNode?> Difference)> group in charted
      .Where(chart => charts[chart.Draft.Chart!].Style is not null)
      .GroupBy(chart => charts[chart.Draft.Chart!].Style!))
    {
      List<Dictionary<string, JsonNode?>> differences = group
        .Select(chart => chart.Difference.Where(pair => !chartBase.ContainsKey(pair.Key))
          .ToDictionary(pair => pair.Key, pair => pair.Value))
        .ToList();
      foreach ((string key, JsonNode? value) in Consensus(differences))
      {
        chartStyles[group.Key][key] = value;
      }
    }

    foreach (ItemDraft draft in items.Where(draft => draft.Table is not null && loaded.ContainsKey(draft.Key)))
    {
      InferTable(draft, loaded[draft.Key]["properties"]!.AsObject());
    }
  }

  /// <summary>What a table's columns and row styles set, from its first load: a column what most of its cells differ
  /// by, and a left or right border every cell on that edge has alike, a row style what most of its rows' cells differ
  /// by beyond their columns, and banding the fill most even body rows without a row style share.</summary>
  private void InferTable(ItemDraft draft, JsonObject loaded)
  {
    TableDraft table = tables[draft.Table!];
    Dictionary<(int, int), JsonObject> first = Cells(loaded)
      .ToDictionary(cell => ((int)cell["row"]!, (int)cell["column"]!), cell => cell["properties"]!.AsObject());
    List<(int Row, int Column, int Last, JsonObject Wanted, Dictionary<string, JsonNode?> Difference)> cells = Cells(
        draft.Properties)
      .Select(cell => ((int)cell["row"]!, (int)cell["column"]!, (int)cell["column"]! + (int)cell["columns"]! - 1,
        cell["properties"]!.AsObject()))
      .Select(cell => (cell.Item1, cell.Item2, cell.Item3, cell.Item4, Difference(cell.Item4,
        first.GetValueOrDefault((cell.Item1, cell.Item2)) ?? new JsonObject(), CellKeys)))
      .ToList();

    for (int column = 1; column <= table.ColumnCount; column++)
    {
      var inColumn = cells.Where(cell => cell.Column == column).ToList();
      foreach ((string key, JsonNode? value) in Consensus(inColumn.Select(cell => cell.Difference
        .Where(pair => TableFormatting.Text.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value))
        .ToList()))
      {
        table.Column(column)[key] = value;
      }
      var body = inColumn.Where(cell => cell.Row > table.HeaderRows).ToList();
      if (Mode(body.Where(cell => cell.Wanted["format"] is not null).Select(cell => cell.Wanted["format"])) is
        JsonNode format && body.Count(cell => JsonNode.DeepEquals(cell.Wanted["format"], format)) * 2 >
        body.Count(cell => table.Text[cell.Row - 1, cell.Column - 1].Length > 0))
      {
        table.Column(column)["format"] = format.DeepClone();
      }
      // A cell can't clear what its column sets unless the JSON says none, so a column takes only a border every cell
      // on its edge has.
      foreach (string side in new[] { "border_left", "border_right" })
      {
        List<List<KeyValuePair<string, JsonNode?>>> edge = cells
          .Where(cell => (side == "border_left" ? cell.Column : cell.Last) == column)
          .Select(cell => cell.Difference.Where(pair => pair.Key.StartsWith($"{side}.", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList())
          .ToList();
        if (edge.Count > 0 && edge[0].Count > 0 && edge.All(border => border.Count == edge[0].Count &&
          border.Zip(edge[0]).All(pair => pair.First.Key == pair.Second.Key &&
            JsonNode.DeepEquals(pair.First.Value, pair.Second.Value))))
        {
          foreach ((string key, JsonNode? value) in edge[0])
          {
            table.Column(column)[key] = value?.DeepClone();
          }
        }
      }
    }

    // A text property every cell of a row wants alike goes on the row, when some cell doesn't get it from its column.
    for (int row = 1; row <= table.RowCount; row++)
    {
      var inRow = cells.Where(cell => cell.Row == row).ToList();
      if (inRow.Count < 2)
      {
        continue;
      }
      foreach (string key in TableFormatting.Text)
      {
        List<JsonNode?> values = inRow.Select(cell => Wanted(key, cell.Wanted[key])).ToList();
        if (values[0] is null || values.Any(value => !JsonNode.DeepEquals(value, values[0])))
        {
          continue;
        }
        bool missing = inRow.Any(cell => table.Columns.GetValueOrDefault(cell.Column)?.GetValueOrDefault(key) is
          JsonNode set ? !JsonNode.DeepEquals(set, values[0]) : cell.Difference.ContainsKey(key));
        if (missing)
        {
          table.Row(row)[key] = values[0]!.DeepClone();
        }
      }
    }

    if (table.Style is null)
    {
      return;
    }
    TableStyleDraft style = tableStyles[table.Style];
    string? RowStyle(int row) => (string?)cells.FirstOrDefault(cell => cell.Row == row).Wanted?["row_style"];
    foreach (string name in style.Rows.Keys.ToList())
    {
      List<Dictionary<string, JsonNode?>> differences = cells
        .Where(cell => RowStyle(cell.Row) == name)
        .Select(cell => cell.Difference
          .Where(pair => !pair.Key.StartsWith("border_left", StringComparison.Ordinal) &&
            !pair.Key.StartsWith("border_right", StringComparison.Ordinal))
          .Where(pair => !(table.Columns.GetValueOrDefault(cell.Column) is { } own &&
            own.TryGetValue(pair.Key, out JsonNode? set) && JsonNode.DeepEquals(set, pair.Value)))
          .ToDictionary(pair => pair.Key, pair => pair.Value))
        .ToList();
      // Likewise, a row style takes only a border all its cells have.
      foreach ((string key, JsonNode? value) in Consensus(differences).Where(pair =>
        !pair.Key.StartsWith("border_", StringComparison.Ordinal) || differences.All(difference =>
          difference.TryGetValue(pair.Key, out JsonNode? set) && JsonNode.DeepEquals(set, pair.Value))))
      {
        style.Rows[name].TryAdd(key, value);
      }
    }
    var even = cells.Where(cell => cell.Row > table.HeaderRows && (cell.Row - table.HeaderRows) % 2 == 0 &&
      RowStyle(cell.Row) is null).ToList();
    if (style.Banded is null && Mode(even.Select(cell => cell.Wanted["fill"])) is JsonNode banded &&
      even.Count(cell => JsonNode.DeepEquals(cell.Wanted["fill"], banded)) * 2 > even.Count)
    {
      style.Banded = banded.DeepClone();
    }
  }

  private static Dictionary<string, JsonNode?> ChartDifference(JsonObject wanted, JsonObject loaded)
  {
    return ChartProperties.Keys
      .Where(key => wanted[key] is not null && !Same(key, wanted[key], loaded[key]))
      .ToDictionary(key => key, key => wanted[key]);
  }

  /// <summary>Each key and value more than half the differences share.</summary>
  private static OrderedDictionary<string, JsonNode?> Consensus(List<Dictionary<string, JsonNode?>> differences)
  {
    var agreed = new OrderedDictionary<string, JsonNode?>();
    foreach (string key in differences.SelectMany(difference => difference.Keys).Distinct())
    {
      JsonNode? common = Mode(differences.Where(difference => difference.ContainsKey(key))
        .Select(difference => difference[key]));
      int count = differences.Count(difference => difference.TryGetValue(key, out JsonNode? value) &&
        JsonNode.DeepEquals(value, common));
      if (common is not null && count * 2 > differences.Count)
      {
        agreed[key] = common.DeepClone();
      }
    }
    return agreed;
  }

  #endregion

  #region Styling text

  /// <summary>Gives each paragraph the style that turns what it loads as into what it should be, naming one where
  /// none does, and each run the span that does the same for the run.</summary>
  /// <summary>A box's paragraphs, or a table cell's, as wanted and as loaded, and where their styles and spans go.
  /// </summary>
  private sealed record TextBlock(ItemDraft Draft, JsonArray Wanted, JsonArray Loaded,
    Action<List<string?>, List<List<RunSpan>>> Store);

  private IEnumerable<TextBlock> TextBlocks(Dictionary<string, JsonObject> loaded)
  {
    foreach (ItemDraft draft in items.Where(draft => loaded.ContainsKey(draft.Key)))
    {
      if (draft.Role is null && draft.Paragraphs is { Count: > 0 })
      {
        yield return new TextBlock(draft, draft.Paragraphs, loaded[draft.Key]["paragraphs"]!.AsArray(),
          (paragraphStyles, spans) =>
          {
            draft.ParagraphStyles = paragraphStyles;
            draft.Spans = spans;
          });
      }
      if (draft.Table is not string stem || loaded[draft.Key]["properties"]?["cells"] is not JsonArray loadedCells)
      {
        continue;
      }
      TableDraft table = tables[stem];
      Dictionary<(int, int), JsonObject> byPosition = loadedCells.Select(cell => cell!.AsObject())
        .ToDictionary(cell => ((int)cell["row"]!, (int)cell["column"]!));
      foreach (JsonObject cell in Cells(draft.Properties))
      {
        (int row, int column) = ((int)cell["row"]!, (int)cell["column"]!);
        JsonArray paragraphs = cell["paragraphs"]!.AsArray();
        if (byPosition.TryGetValue((row, column), out JsonObject? had) &&
          had["paragraphs"]!.AsArray().Count == paragraphs.Count)
        {
          yield return new TextBlock(draft, paragraphs, had["paragraphs"]!.AsArray(), (paragraphStyles, spans) =>
            table.Text[row - 1, column - 1] = Markup(paragraphs, paragraphStyles, spans, false));
        }
      }
    }
  }

  /// <summary>Gives each paragraph the style that turns what it loads as into what it should be, naming one where
  /// none does, and each run the span that does the same for the run.</summary>
  private void StyleText(Dictionary<string, JsonObject> loaded)
  {
    List<TextBlock> blocks = TextBlocks(loaded).ToList();

    var observed = new Dictionary<string, List<Dictionary<string, JsonNode?>>>();
    void Observe(string style, Dictionary<string, JsonNode?> difference)
    {
      (observed.TryGetValue(style, out var list) ? list : observed[style] = new List<Dictionary<string, JsonNode?>>())
        .Add(difference);
    }
    var defined = styles.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).ToHashSet();
    foreach (TextBlock block in blocks)
    {
      for (int index = 0; index < block.Wanted.Count; index++)
      {
        JsonObject wanted = block.Wanted[index]!.AsObject();
        if ((string?)wanted["style"] is string style && !defined.Contains(style))
        {
          Observe(style, Difference(wanted["properties"]!.AsObject(), block.Loaded[index]!["properties"]!.AsObject(),
            ParagraphKeys));
        }
        foreach (JsonObject run in wanted["runs"]!.AsArray().Select(node => node!.AsObject()))
        {
          if (run["styles"]?.AsArray() is [JsonNode only] && !defined.Contains((string)only!))
          {
            Observe((string)only!, Difference(run["properties"]!.AsObject(), Linked(wanted, run), Character));
          }
        }
      }
    }
    foreach ((string style, List<Dictionary<string, JsonNode?>> differences) in observed)
    {
      foreach ((string key, JsonNode? value) in Consensus(differences))
      {
        styles[style].TryAdd(key, value);
      }
    }

    int generated = 0;
    foreach (TextBlock block in blocks)
    {
      var paragraphStyles = new List<string?>();
      var spans = new List<List<RunSpan>>();
      for (int index = 0; index < block.Wanted.Count; index++)
      {
        JsonObject wanted = block.Wanted[index]!.AsObject();
        JsonObject loadedProperties = block.Loaded[index]!["properties"]!.AsObject();
        Dictionary<string, JsonNode?> difference = Difference(wanted["properties"]!.AsObject(), loadedProperties,
          ParagraphKeys);
        foreach ((string key, JsonNode? value) in BulletDifference(block.Draft, index, wanted,
          block.Loaded[index]!.AsObject()))
        {
          difference[key] = value;
        }
        string? hint = (string?)wanted["style"];
        string? chosen = hint is not null && Satisfies(styles[hint], difference, loadedProperties)
          ? hint
          : difference.Count == 0
            ? null
            : styles.FirstOrDefault(pair => Satisfies(pair.Value, difference, loadedProperties)).Key;
        if (chosen is null && difference.Count > 0)
        {
          chosen = Unique(hint ?? $"paragraph_{++generated}", styles.Keys.ToHashSet());
          styles[chosen] = new OrderedDictionary<string, JsonNode?>(difference);
        }
        paragraphStyles.Add(chosen);
        spans.Add(wanted["runs"]!.AsArray().Select(node => Span(block.Draft, node!.AsObject(), wanted)).ToList());
      }
      block.Store(paragraphStyles, spans);
    }
  }

  /// <summary>The bullet keys a paragraph wants and doesn't load with, as a style's <c>levels</c> state them, for a
  /// level the theme has; a bullet going back to its text's color or size has no key.</summary>
  private Dictionary<string, JsonNode?> BulletDifference(ItemDraft draft, int index, JsonObject wanted,
    JsonObject actual)
  {
    var difference = new Dictionary<string, JsonNode?>();
    int level = (int)wanted["level"]!;
    foreach ((string key, string yaml) in new[] { ("bullet", "bullet"), ("bulletColor", "bullet_color"),
      ("bulletSize", "bullet_size"), ("indent", "indent"), ("hanging", "hanging") })
    {
      // A level's hanging defaults to its gap from the level above, so moving the indent moves it too.
      bool moved = key == "hanging" && !JsonNode.DeepEquals(wanted["indent"], actual["indent"]);
      if (JsonNode.DeepEquals(wanted[key], actual[key]) && !moved)
      {
        continue;
      }
      // A bullet in its text's size or color states the size as 1 and the color as the text's.
      JsonNode? value = wanted[key] ?? key switch
      {
        "bulletSize" => 1.0,
        "bulletColor" => wanted["properties"]?["color"],
        _ => null
      };
      if (level == 0 || !Theme["levels"]!.AsObject().ContainsKey(level.ToString(CultureInfo.InvariantCulture)) ||
        value is null)
      {
        findings.Add($"{draft.Key}: paragraph {index + 1} wants {key} {wanted[key]?.ToJsonString() ?? "none"}, " +
          $"which no style's levels can set");
        continue;
      }
      difference[$"levels.{level}.{yaml}"] = value.DeepClone();
    }
    return difference;
  }

  /// <summary>Points each format at a style the deck already names, when one sets the negative color and nothing
  /// else, in place of the style made for it.</summary>
  private void MergeNegativeStyles()
  {
    foreach ((string color, string made) in negativeStyles.ToList())
    {
      string? named = styles.FirstOrDefault(pair => pair.Key != made && pair.Value.Count == 1 &&
        pair.Value.TryGetValue("color", out JsonNode? set) && set?.ToJsonString() == color).Key;
      if (named is not null)
      {
        negativeStyles[color] = named;
        styles.Remove(made);
      }
    }
  }

  /// <summary>A run's character properties before its span: its paragraph's, with a link's color and underline.
  /// </summary>
  private static JsonObject Linked(JsonObject paragraph, JsonObject run)
  {
    var properties = new JsonObject();
    foreach (string key in Character)
    {
      if (paragraph["properties"]![key] is JsonNode value)
      {
        properties[key] = value.DeepClone();
      }
    }
    if (run["link"] is not null)
    {
      properties["color"] = new JsonObject { ["theme"] = "hyperlink" };
      properties["underline"] = true;
    }
    return properties;
  }

  private RunSpan Span(ItemDraft draft, JsonObject run, JsonObject paragraph)
  {
    JsonObject before = Linked(paragraph, run);
    List<string> names = run["styles"]?.AsArray().Select(node => (string)node!).ToList() ?? new List<string>();
    foreach (string name in names)
    {
      foreach ((string key, JsonNode? value) in styles[name].Where(pair => Character.Contains(pair.Key)))
      {
        before[key] = value?.DeepClone();
      }
    }
    Dictionary<string, JsonNode?> difference = Difference(run["properties"]!.AsObject(), before, Character);
    bool bold = false;
    bool italic = false;
    var own = new OrderedDictionary<string, JsonNode?>();
    foreach ((string key, JsonNode? value) in difference)
    {
      if (key is "bold" or "italic")
      {
        if (value is not null && (bool)value)
        {
          bold |= key == "bold";
          italic |= key == "italic";
        }
        else
        {
          names.Add(Off(key));
        }
        continue;
      }
      if (value is not null)
      {
        own[key] = value.DeepClone();
      }
    }
    return new RunSpan(bold, italic, names, own);
  }

  /// <summary>The style a run takes to turn bold or italic off, one the deck names when it sets nothing else.
  /// </summary>
  private string Off(string key)
  {
    string? named = styles.FirstOrDefault(pair => pair.Value.Count == 1 && pair.Value.TryGetValue(key,
      out JsonNode? set) && set is JsonValue value && value.TryGetValue(out bool on) && !on).Key;
    if (named is not null)
    {
      return named;
    }
    string name = Unique(key == "bold" ? "regular" : "upright", styles.Keys.ToHashSet());
    styles[name] = new OrderedDictionary<string, JsonNode?> { [key] = false };
    return name;
  }

  private static Dictionary<string, JsonNode?> Difference(JsonObject wanted, JsonObject loaded, string[] keys)
  {
    return keys.Where(key => !Same(key, wanted[key], loaded[key]))
      .ToDictionary(key => key, key => Wanted(key, wanted[key]));
  }

  /// <summary>Whether a style turns <paramref name="loaded"/> into what <paramref name="difference"/> wants: it sets
  /// every key that differs as wanted, and any other key as it already was.</summary>
  private static bool Satisfies(OrderedDictionary<string, JsonNode?> style, Dictionary<string, JsonNode?> difference,
    JsonObject loaded)
  {
    foreach ((string key, JsonNode? value) in difference)
    {
      if (!style.TryGetValue(key, out JsonNode? set) || !JsonNode.DeepEquals(set, value))
      {
        return false;
      }
    }
    return style.Where(pair => ParagraphKeys.Contains(pair.Key) && !difference.ContainsKey(pair.Key))
      .All(pair => Same(pair.Key, pair.Value, loaded[pair.Key]));
  }

  private void Verify(JsonObject actual)
  {
    Dictionary<string, JsonObject> loaded = Items(actual).ToDictionary(entry => entry.Key, entry => entry.Item);
    foreach (ItemDraft draft in items)
    {
      if (!loaded.TryGetValue(draft.Key, out JsonObject? item))
      {
        findings.Add($"{draft.Key}: missing from the import");
        continue;
      }
      foreach (string difference in Differences(draft.Wanted, item, ""))
      {
        findings.Add($"{draft.Key}: {difference}");
      }
    }
  }

  private static IEnumerable<string> Differences(JsonNode? wanted, JsonNode? actual, string path)
  {
    if (wanted is JsonObject left && actual is JsonObject right)
    {
      foreach (string key in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key)))
      {
        if (key is "chart" or "data" or "table" && path.EndsWith("properties", StringComparison.Ordinal))
        {
          continue;
        }
        if (left[key] is null || right[key] is null)
        {
          if (!Same(key, left[key], right[key]))
          {
            yield return $"{(path.Length == 0 ? key : $"{path}.{key}")} is {right[key]?.ToJsonString() ?? "missing"}, " +
              $"not {Wanted(key, left[key])?.ToJsonString() ?? "missing"}";
          }
          continue;
        }
        foreach (string difference in Differences(left[key], right[key], path.Length == 0 ? key : $"{path}.{key}"))
        {
          yield return difference;
        }
      }
      yield break;
    }
    if (wanted is JsonArray first && actual is JsonArray second && first.Count == second.Count)
    {
      for (int index = 0; index < first.Count; index++)
      {
        foreach (string difference in Differences(first[index], second[index], $"{path}[{index}]"))
        {
          yield return difference;
        }
      }
      yield break;
    }
    if (!JsonNode.DeepEquals(wanted, actual))
    {
      yield return $"{path} is {actual?.ToJsonString() ?? "missing"}, not {wanted?.ToJsonString() ?? "missing"}";
    }
  }

  #endregion

  #region Writing the folder

  private void Write()
  {
    if (Directory.Exists(folder))
    {
      Directory.Delete(folder, true);
    }
    Directory.CreateDirectory(folder);
    WriteFile("presentation.yaml", YamlWriter.Write(Presentation()));
    WriteFile("theme/theme.yaml", YamlWriter.Write(ThemeFile()));
    WriteFile("theme/styles.yaml", YamlWriter.Write(StylesFile()));
    foreach (JsonObject layout in Layouts)
    {
      WriteFile($"theme/layouts/{layout["id"]}.yaml", YamlWriter.Write(LayoutFile(layout)));
    }
    foreach (JsonObject slide in Slides.Where(slide => slide["error"] is null))
    {
      WriteFile($"slides/{slide["id"]}.yaml", YamlWriter.Write(SlideFile(slide)));
    }
    foreach (ChartDraft chart in charts.Values)
    {
      WriteFile($"charts/{chart.Stem}.yaml", YamlWriter.Write(ChartFile(chart)));
      WriteFile($"charts/{chart.Data}", chart.Csv);
    }
    foreach (TableDraft table in tables.Values)
    {
      WriteFile($"tables/{table.Stem}.yaml", YamlWriter.Write(TableFile(table)));
      WriteFile($"tables/{table.Data}", TableCsv(table));
    }
    foreach ((string written, string from) in pictureSources)
    {
      if (File.Exists(from))
      {
        string target = Path.Combine(folder, written);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(from, target);
      }
    }
  }

  private void WriteFile(string relative, string text)
  {
    string path = Path.Combine(folder, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
  }

  private OrderedDictionary<string, object?> Presentation()
  {
    var map = new OrderedDictionary<string, object?>
    {
      ["name"] = (string)deck["name"]!,
      ["theme"] = "theme",
      ["size"] = Yaml("size", deck["size"])
    };
    if (fields.Count > 0)
    {
      map["fields"] = new OrderedDictionary<string, object?>(fields.Select(pair =>
        KeyValuePair.Create(pair.Key, (object?)pair.Value)));
    }
    var slides = new List<object?>();
    string? section = null;
    foreach (JsonObject slide in Slides.Where(slide => slide["error"] is null))
    {
      string? current = (string?)slide["section"];
      if (current is not null && current != section)
      {
        slides.Add(new OrderedDictionary<string, object?> { ["section"] = current });
      }
      section = current;
      slides.Add((string)slide["id"]!);
    }
    map["slides"] = slides;
    return map;
  }

  private OrderedDictionary<string, object?> ThemeFile()
  {
    JsonObject theme = Theme;
    var map = new OrderedDictionary<string, object?>
    {
      ["name"] = (string)theme["name"]!,
      ["colors"] = Yaml("colors", theme["colors"]),
      ["fonts"] = Yaml("fonts", theme["fonts"]),
      ["background"] = Yaml("background", theme["background"])
    };
    if (theme["areas"] is JsonObject { Count: > 0 } areas)
    {
      map["areas"] = Yaml("areas", areas);
    }
    Scope(map, "theme", 0);
    return map;
  }

  /// <summary>A level's title, placeholders, and elements.</summary>
  private void Scope(OrderedDictionary<string, object?> map, string scope, int level)
  {
    List<ItemDraft> owned = items.Where(item => item.Level == level && item.Key.StartsWith($"{scope}/",
      StringComparison.Ordinal)).ToList();
    if (owned.FirstOrDefault(item => item.Role == "title") is ItemDraft title)
    {
      map["title"] = Placeholder(title);
    }
    var placeholders = new OrderedDictionary<string, object?>();
    foreach (ItemDraft placeholder in owned.Where(item => item.Role == "placeholder" &&
      (item.Level == 2 || item.Defines || item.Style is not null || item.Own.Count > 0)))
    {
      placeholders[placeholder.Id] = Placeholder(placeholder);
    }
    if (placeholders.Count > 0)
    {
      map["placeholders"] = placeholders;
    }
    // A slide's title or placeholder drawn over one of its elements is named in its elements where it's drawn.
    bool stacking = false;
    var elements = new List<object?>();
    foreach (ItemDraft item in owned)
    {
      if (item.Role is null)
      {
        elements.Add(Element(item));
        stacking = level == 2;
      }
      else if (stacking)
      {
        elements.Add(item.Role == "title" ? "title" : item.Id);
      }
    }
    if (elements.Count > 0)
    {
      map["elements"] = elements;
    }
  }

  private OrderedDictionary<string, object?> LayoutFile(JsonObject layout)
  {
    var map = new OrderedDictionary<string, object?> { ["name"] = (string)layout["name"]! };
    if (layout["background"] is JsonNode background)
    {
      map["background"] = Yaml("background", background);
    }
    if ((bool)layout["titleRemoved"]!)
    {
      map["title"] = "none";
    }
    JsonObject theirs = Theme["areas"]?.AsObject() ?? new JsonObject();
    var areas = new OrderedDictionary<string, object?>((layout["areas"]?.AsObject() ?? new JsonObject())
      .Where(pair => !JsonNode.DeepEquals(pair.Value, theirs[pair.Key]))
      .Select(pair => KeyValuePair.Create(pair.Key, Yaml(pair.Key, pair.Value))));
    if (areas.Count > 0)
    {
      map["areas"] = areas;
    }
    Scope(map, $"layout:{layout["id"]}", 1);
    return map;
  }

  private OrderedDictionary<string, object?> SlideFile(JsonObject slide)
  {
    var map = new OrderedDictionary<string, object?> { ["layout"] = (string)slide["layout"]! };
    if (slide["background"] is JsonNode background)
    {
      map["background"] = Yaml("background", background);
    }
    Scope(map, $"slide:{slide["id"]}", 2);
    if (slide["hide"]!.AsArray().Count > 0)
    {
      map["hide"] = slide["hide"]!.AsArray().Select(id => (object?)(string)id!).ToList();
    }
    return map;
  }

  /// <summary>A placeholder at the theme or a layout, or a slide's words with any key it states: plain text when it
  /// states none.</summary>
  private object? Placeholder(ItemDraft draft)
  {
    string? text = draft.Level == 2
      ? Plain(draft.Paragraphs![0]!["runs"]!.AsArray(), true)
      : null;
    if (draft.Level == 2 && draft.Style is null && draft.Own.Count == 0)
    {
      return text;
    }
    var map = new OrderedDictionary<string, object?>();
    if (text is not null)
    {
      map["text"] = text;
    }
    if (draft.Own.TryGetValue("frame", out JsonNode? frame))
    {
      map["frame"] = Yaml("frame", frame);
    }
    if (draft.Style is not null)
    {
      map["style"] = draft.Style;
    }
    foreach ((string key, object? value) in Nest(new OrderedDictionary<string, JsonNode?>(draft.Own.Where(pair =>
      pair.Key != "frame"))))
    {
      map[key] = value;
    }
    return map;
  }

  private OrderedDictionary<string, object?> Element(ItemDraft draft)
  {
    string mediaPrefix = draft.Level == 0 ? "" : "../";
    var map = new OrderedDictionary<string, object?> { ["id"] = draft.Id };
    JsonObject properties = draft.Properties;
    switch (draft.Kind)
    {
      case "text":
        map["text"] = Markup(draft);
        break;
      case "shape":
        map["shape"] = (string)properties["shape"]!;
        break;
      case "line":
        map["line"] = new OrderedDictionary<string, object?>
        {
          ["from"] = Yaml("from", properties["from"]),
          ["to"] = Yaml("to", properties["to"])
        };
        break;
      case "picture":
        string relative = draft.Level == 2 ? draft.Picture! : draft.Picture!["theme/".Length..];
        map["picture"] = $"{mediaPrefix}{relative}";
        break;
      case "chart":
        map["chart"] = draft.Chart;
        break;
      case "table":
        map["table"] = draft.Table;
        break;
    }
    if (draft.Style is not null)
    {
      map["style"] = draft.Style;
    }
    foreach ((string key, object? value) in Nest(draft.Own))
    {
      map[key] = key == "frame" && Area(draft, draft.Own["frame"]) is string area ? area : value;
    }
    if (draft.Kind == "shape" && draft.Paragraphs is { Count: > 0 })
    {
      map["text"] = Markup(draft);
    }
    return map;
  }

  /// <summary>The area of a slide's layout a frame matches, which the slide names in place of the numbers.</summary>
  private string? Area(ItemDraft draft, JsonNode? frame)
  {
    if (draft.Level != 2)
    {
      return null;
    }
    string layout = (string)Slide(draft)["layout"]!;
    JsonObject? areas = Layouts.FirstOrDefault(candidate => (string)candidate["id"]! == layout)?["areas"]?.AsObject();
    return areas?.FirstOrDefault(pair => JsonNode.DeepEquals(pair.Value, frame)).Key;
  }

  private static string Markup(ItemDraft draft)
  {
    return Markup(draft.Paragraphs ?? new JsonArray(), draft.ParagraphStyles, draft.Spans, true);
  }

  /// <summary>Paragraphs as markup, with each paragraph's style and each run's span once the text is styled, and
  /// plain until then. A box's markup of several lines ends in a line break, as a YAML block does.</summary>
  private static string Markup(JsonArray paragraphs, List<string?>? paragraphStyles, List<List<RunSpan>>? spans,
    bool block)
  {
    var lines = new List<string>();
    for (int index = 0; index < paragraphs.Count; index++)
    {
      JsonObject paragraph = paragraphs[index]!.AsObject();
      // The markup styles a paragraph by the line after its style, which an empty one doesn't have.
      if (paragraphStyles?[index] is string style && paragraph["runs"]!.AsArray().Count > 0)
      {
        lines.Add($"{{.{style}}}");
      }
      int level = (int)paragraph["level"]!;
      string prefix = "";
      if (level > 0)
      {
        string? bullet = (string?)paragraph["bullet"];
        prefix = new string(' ', (level - 1) * 2) + (bullet is not null && Numbered().IsMatch(bullet)
          ? $"{bullet} "
          : "- ");
      }
      JsonArray runs = paragraph["runs"]!.AsArray();
      string content = spans is null
        ? Plain(runs, false, true)
        : string.Concat(runs.Select((run, position) => Run(run!.AsObject(), spans[index][position])));
      lines.Add(prefix + (level == 0 ? Pintura.Markup.EscapeLineStart(content) : content));
    }
    string text = string.Join("\n", lines);
    return block && lines.Count > 1 ? text + "\n" : text;
  }

  /// <summary>Runs as markup with no formatting of their own, links aside when <paramref name="links"/> is set, or
  /// as a title's plain text when <paramref name="plain"/> is.</summary>
  /// <summary>The CSV the data was read from, or one named for the chart or table when it came from a book, whose
  /// values the import keeps.</summary>
  private static string CsvPath(JsonObject properties, string stem)
  {
    return (string?)properties["data"] is string data && data.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
      ? data
      : $"{stem}.csv";
  }

  /// <summary>The run's field, but for a book reference, which the imported folder keeps as the text it showed since
  /// it includes no book.</summary>
  private static string? Field(JsonObject run)
  {
    return (string?)run["field"] is string field && !Reference.Looks(field) ? field : null;
  }

  private static string Plain(JsonArray runs, bool plain, bool links = false)
  {
    var text = new StringBuilder();
    foreach (JsonObject run in runs.Select(node => node!.AsObject()))
    {
      string words = Field(run) is string field ? $"${{{field}}}" : Escape((string)run["text"]!, plain);
      text.Append(links && (string?)run["link"] is string link ? $"[{words}]({link})" : words);
    }
    return text.ToString();
  }

  private static string Run(JsonObject run, RunSpan span)
  {
    string text = Field(run) is string field ? $"${{{field}}}" : Escape((string)run["text"]!, false);
    if ((string?)run["link"] is string link)
    {
      text = $"[{text}]({link})";
    }
    var attributes = span.Styles.Select(style => $".{style}").ToList();
    foreach ((string key, JsonNode? value) in span.Own)
    {
      string written = Yaml(key, value) switch
      {
        double number => YamlWriter.Number(number),
        bool flag => flag ? "true" : "false",
        object other => other.ToString()!,
        null => ""
      };
      attributes.Add($"{key}={(written.Contains(' ') ? $"\"{written}\"" : written)}");
    }
    if (attributes.Count > 0)
    {
      text = $"[{text}]{{{string.Join(" ", attributes)}}}";
    }
    if (span.Italic)
    {
      text = $"*{text}*";
    }
    if (span.Bold)
    {
      text = $"**{text}**";
    }
    return text;
  }

  private static string Escape(string text, bool plain)
  {
    var escaped = new StringBuilder();
    for (int index = 0; index < text.Length; index++)
    {
      char character = text[index];
      bool field = character == '$' && index + 1 < text.Length && text[index + 1] == '{';
      if (field || (!plain && character is '\\' or '*' or '[' or ']'))
      {
        escaped.Append('\\');
      }
      escaped.Append(character);
    }
    return escaped.ToString();
  }

  private OrderedDictionary<string, object?> StylesFile()
  {
    JsonObject theme = Theme;
    var text = (OrderedDictionary<string, object?>)Yaml("text", theme["text"])!;
    var levels = new OrderedDictionary<string, object?>();
    foreach ((string number, JsonNode? node) in theme["levels"]!.AsObject())
    {
      var level = new OrderedDictionary<string, object?>();
      foreach ((string key, string yaml) in new[]
        {
          ("bullet", "bullet"), ("bulletColor", "bullet_color"), ("bulletSize", "bullet_size"), ("indent", "indent"),
          ("hanging", "hanging")
        })
      {
        if (node![key] is JsonNode value)
        {
          level[yaml] = Yaml(yaml, value);
        }
      }
      foreach ((string key, object? value) in (OrderedDictionary<string, object?>)Yaml("", node!["properties"])!)
      {
        level[key] = value;
      }
      levels[number] = level;
    }
    if (levels.Count > 0)
    {
      text["levels"] = levels;
    }
    var map = new OrderedDictionary<string, object?> { ["text"] = text };
    if (shapeText.Count > 0)
    {
      map["shape_text"] = Nest(shapeText);
    }
    if (shapeBase.Count > 0)
    {
      map["shape"] = Nest(shapeBase);
    }
    if (pictureFit is not null)
    {
      map["picture"] = new OrderedDictionary<string, object?> { ["fit"] = pictureFit };
    }
    if (lineBase.Count > 0)
    {
      map["line"] = Nest(lineBase);
    }
    if (chartBase.Count > 0)
    {
      map["chart"] = Nest(chartBase);
    }
    if (styles.Count > 0)
    {
      map["styles"] = new OrderedDictionary<string, object?>(styles.Select(pair =>
        KeyValuePair.Create(pair.Key, (object?)Nest(pair.Value))));
    }
    if (tableStyles.Count > 0)
    {
      map["tables"] = new OrderedDictionary<string, object?>(tableStyles.Select(pair =>
      {
        var style = new OrderedDictionary<string, object?>(pair.Value.Rows.Select(row =>
          KeyValuePair.Create(row.Key, (object?)Nest(row.Value))));
        if (pair.Value.Banded is JsonNode banded)
        {
          style["banded"] = Yaml("banded", banded);
        }
        return KeyValuePair.Create(pair.Key, (object?)style);
      }));
    }
    if (chartStyles.Count > 0)
    {
      map["charts"] = new OrderedDictionary<string, object?>(chartStyles.Select(pair =>
        KeyValuePair.Create(pair.Key, (object?)Nest(pair.Value))));
    }
    var formats = new OrderedDictionary<string, object?>();
    foreach ((string name, JsonNode? node) in theme["formats"]!.AsObject())
    {
      var format = new OrderedDictionary<string, object?>();
      foreach ((string key, JsonNode? value) in node!.AsObject())
      {
        if (key == "negativeColor")
        {
          format["negative_style"] = negativeStyles[value!.ToJsonString()];
          continue;
        }
        format[key] = Yaml(key, value);
      }
      formats[name] = format;
    }
    if (formats.Count > 0)
    {
      map["formats"] = formats;
    }
    return map;
  }

  private OrderedDictionary<string, object?> ChartFile(ChartDraft chart)
  {
    var map = new OrderedDictionary<string, object?> { ["type"] = chart.Type };
    if (chart.Style is not null)
    {
      map["style"] = chart.Style;
    }
    map["data"] = chart.Data;
    if (chart.SeriesInColumns)
    {
      map["series_in"] = "columns";
    }
    foreach ((string key, object? value) in Nest(chart.Own))
    {
      map[key] = value;
    }
    var series = new OrderedDictionary<string, object?>(chart.Series.Where(pair => pair.Value.Count > 0)
      .Select(pair => KeyValuePair.Create(pair.Key, (object?)Nest(pair.Value))));
    if (series.Count > 0)
    {
      map["series"] = series;
    }
    return map;
  }

  private OrderedDictionary<string, object?> TableFile(TableDraft table)
  {
    var map = new OrderedDictionary<string, object?> { ["data"] = table.Data };
    if (table.Style is not null)
    {
      map["style"] = table.Style;
    }
    if (table.HeaderRows > 0)
    {
      map["header_rows"] = table.HeaderRows;
    }
    int columns = table.Columns.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key).DefaultIfEmpty(0).Max();
    if (columns > 0)
    {
      map["columns"] = Enumerable.Range(1, columns)
        .Select(column => (object?)Nest(table.Columns.GetValueOrDefault(column) ??
          new OrderedDictionary<string, JsonNode?>()))
        .ToList();
    }
    var rows = new OrderedDictionary<string, object?>(table.Rows
      .Where(pair => pair.Value.Count > 0)
      .OrderBy(pair => pair.Key)
      .Select(pair => KeyValuePair.Create(pair.Key.ToString(CultureInfo.InvariantCulture), (object?)Nest(pair.Value))));
    if (rows.Count > 0)
    {
      map["rows"] = rows;
    }
    List<object?> cells = table.Cells
      .Where(pair => pair.Value.Count > 0)
      .OrderBy(pair => pair.Key.Row)
      .ThenBy(pair => pair.Key.Column)
      .Select(pair =>
      {
        var cell = new OrderedDictionary<string, object?> { ["row"] = pair.Key.Row, ["column"] = pair.Key.Column };
        foreach ((string key, object? value) in Nest(pair.Value))
        {
          cell[key] = value;
        }
        return (object?)cell;
      })
      .ToList();
    if (cells.Count > 0)
    {
      map["cells"] = cells;
    }
    if (table.Merges.Count > 0)
    {
      map["merge"] = table.Merges;
    }
    return map;
  }

  /// <summary>The table's CSV: a cell a format spells as the value it spells, and any other as its text.</summary>
  private string TableCsv(TableDraft table)
  {
    JsonObject formats = Theme["formats"]!.AsObject();
    var csv = new StringBuilder();
    for (int row = 1; row <= table.RowCount; row++)
    {
      var line = new List<string>();
      for (int column = 1; column <= table.ColumnCount; column++)
      {
        string text = table.Text[row - 1, column - 1] ?? "";
        string? format = row <= table.HeaderRows
          ? null
          : new[] { table.Cells.GetValueOrDefault((row, column)), table.Rows.GetValueOrDefault(row),
              table.Columns.GetValueOrDefault(column) }
            .Select(level => (string?)level?.GetValueOrDefault("format"))
            .FirstOrDefault(name => name is not null);
        if (format is not null && formats[format] is JsonObject definition && Unspell(text, definition) is double value)
        {
          text = YamlWriter.Number(value);
        }
        line.Add(CsvCell(text));
      }
      csv.AppendLine(string.Join(",", line));
    }
    return csv.ToString();
  }

  /// <summary>The number a format spells as <paramref name="text"/>, or null when it spells none that way.</summary>
  private static double? Unspell(string text, JsonObject definition)
  {
    if ((string?)definition["date"] is not null)
    {
      return null;
    }
    var format = new NumberFormat
    {
      Scale = (double?)definition["scale"],
      Decimals = (int?)definition["decimals"],
      Separator = (bool?)definition["separator"],
      Prefix = (string?)definition["prefix"],
      Suffix = (string?)definition["suffix"],
      Negative = (string?)definition["negative"]
    };
    string digits = text;
    bool negative = false;
    if (digits.StartsWith('(') && digits.EndsWith(')'))
    {
      negative = true;
      digits = digits[1..^1];
    }
    else if (digits.StartsWith('-'))
    {
      negative = true;
      digits = digits[1..];
    }
    if (format.Prefix is string prefix && digits.StartsWith(prefix, StringComparison.Ordinal))
    {
      digits = digits[prefix.Length..];
    }
    if (format.Suffix is string suffix && digits.EndsWith(suffix, StringComparison.Ordinal))
    {
      digits = digits[..^suffix.Length];
    }
    if (!double.TryParse(digits.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
      out double shown))
    {
      return null;
    }
    double scale = format.Scale ?? 1;
    int places = Math.Clamp((format.Decimals ?? 0) + (int)Math.Round(Math.Log10(scale)), 0, 15);
    double value = Math.Round((negative ? -shown : shown) / scale, places);
    return format.Spell(value) == text ? value : null;
  }

  /// <summary>Dotted keys as nested maps: <c>stroke.color</c> and <c>stroke.width</c> as one <c>stroke</c>.</summary>
  private static OrderedDictionary<string, object?> Nest(OrderedDictionary<string, JsonNode?> own)
  {
    var map = new OrderedDictionary<string, object?>();
    foreach ((string key, JsonNode? value) in own)
    {
      string[] path = KeyPath(key);
      OrderedDictionary<string, object?> target = map;
      foreach (string part in path[..^1])
      {
        if (target.GetValueOrDefault(part) is not OrderedDictionary<string, object?> nested)
        {
          nested = new OrderedDictionary<string, object?>();
          target[part] = nested;
        }
        target = nested;
      }
      target[path[^1]] = Yaml(path[^1], value);
    }
    return map;
  }

  /// <summary>A dotted key's parts, with a point's category whole, dots and all: <c>points.{category}.color</c>, or
  /// <c>.data_labels.</c> or <c>.markers.</c> and a key after it.</summary>
  private static string[] KeyPath(string key)
  {
    const string Points = "points.";
    if (!key.StartsWith(Points, StringComparison.Ordinal))
    {
      return key.Split('.');
    }
    string rest = key[Points.Length..];
    Match grouped = PointGroupKey().Match(rest);
    int end = grouped.Success ? grouped.Index : rest.LastIndexOf('.');
    return ["points", rest[..end], .. rest[(end + 1)..].Split('.')];
  }

  /// <summary>A point's key under <c>data_labels</c> or <c>markers</c>, which ends its dotted key.</summary>
  [GeneratedRegex(@"\.(?:data_labels|markers)\.[^.]+$")]
  private static partial Regex PointGroupKey();

  /// <summary>A JSON value as YAML writes it: colors and fonts as their names, and maps key by key.</summary>
  private static object? Yaml(string key, JsonNode? node)
  {
    switch (node)
    {
      case null:
        return null;
      case JsonArray array:
        return array.Select(item => Yaml(key, item)).ToList();
      case JsonObject map when map["family"] is JsonNode family:
        return (string)family!;
      case JsonObject map when (string?)map["theme"] is "heading" or "body":
        return (string)map["theme"]!;
      case JsonObject map when map.ContainsKey("theme") || map.ContainsKey("hex") || map.ContainsKey("none"):
        return ColorName(map);
      case JsonObject map:
        return new OrderedDictionary<string, object?>(map.Select(pair =>
          KeyValuePair.Create(Snake(pair.Key), Yaml(pair.Key, pair.Value))));
      case JsonValue value when value.TryGetValue(out bool flag):
        return flag;
      case JsonValue value when value.TryGetValue(out string? text):
        return text;
      case JsonValue value:
        return (double)value;
    }
    return null;
  }

  private static string ColorName(JsonObject color)
  {
    if (color["none"] is not null)
    {
      return "none";
    }
    if ((string?)color["hex"] is string hex)
    {
      return hex;
    }
    string name = (string)color["theme"]!;
    return color["lighter"] is JsonNode lighter
      ? $"{name} lighter {(int)lighter}"
      : color["darker"] is JsonNode darker
        ? $"{name} darker {(int)darker}"
        : name;
  }

  private static string Snake(string key)
  {
    return CamelHump().Replace(key, match => $"_{char.ToLowerInvariant(match.Value[0])}");
  }

  [GeneratedRegex("[A-Z]")]
  private static partial Regex CamelHump();

  [GeneratedRegex(@"^\d+\.$")]
  private static partial Regex Numbered();

  #endregion

}

/// <summary><c>pintura --import</c>: a <c>.pptx</c>, read through editide's tools, or the model's JSON, into a
/// presentation folder, or its theme alone.</summary>
public static class ImportCommand
{
  public static int Run(string deck, string folder, bool themeOnly, TextWriter output, SlideSelection? only = null)
  {
    string source = Path.GetFullPath(deck);
    if (!File.Exists(source))
    {
      throw new ArgumentException($"no file at {source}");
    }
    string target = Path.GetFullPath(folder);
    Importer.RefuseUnlessEmpty(target);
    string written = themeOnly ? Importer.Scratch(target) : target;

    var findings = new List<string>();
    string? media = null;
    try
    {
      string json;
      string pictures;
      if (Path.GetExtension(source).ToLowerInvariant() is ".pptx" or ".potx")
      {
        media = Temporary.Folder("import", Guid.NewGuid().ToString("N"));
        JsonObject reads = DeckReads.Read(source, ToolHosts.Default());
        if (only is not null)
        {
          List<JsonNode?> picked = only.Pick(reads["slides"]!.AsArray().ToList(), _ => null);
          reads["slides"] = new JsonArray(picked.Select(slide => slide!.DeepClone()).ToArray());
        }
        PowerPointDeck mapped = PowerPointMapping.Map(reads, source, media, wholeTheme: themeOnly);
        json = mapped.Deck.ToJsonString();
        pictures = mapped.Media;
        findings.AddRange(mapped.Findings);
      }
      else
      {
        json = File.ReadAllText(source);
        pictures = Path.GetDirectoryName(source)!;
        if (only is not null && JsonNode.Parse(json) is JsonObject model)
        {
          List<JsonNode?> picked = only.Pick(model["slides"]!.AsArray().ToList(), slide => (string?)slide?["id"]);
          model["slides"] = new JsonArray(picked.Select(slide => slide!.DeepClone()).ToArray());
          json = model.ToJsonString();
        }
      }
      findings.AddRange(Importer.Import(json, pictures, written).Findings);
      Presentation presentation = PresentationLoader.Load(written);
      if (themeOnly)
      {
        Importer.Place(Path.Combine(written, "theme"), target);
        output.WriteLine($"{target}: {presentation.Theme.Layouts.Count} layouts");
      }
      else
      {
        output.WriteLine($"{target}: {presentation.Slides.Count} slides, {presentation.Theme.Layouts.Count} layouts");
      }
    }
    finally
    {
      if (media is not null && Directory.Exists(media))
      {
        Directory.Delete(media, true);
      }
      if (themeOnly && Directory.Exists(written))
      {
        Directory.Delete(written, true);
      }
    }
    if (findings.Count > 0)
    {
      output.WriteLine();
      output.WriteLine("what didn't land:");
      foreach (string finding in findings.Distinct())
      {
        output.WriteLine($"- {finding}");
      }
    }
    return 0;
  }


}