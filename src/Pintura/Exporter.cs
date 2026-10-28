using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pintura;

public record ExportReport(string File, int Slides, int Calls, TimeSpan Elapsed, IReadOnlyCollection<string> Gaps,
  int Reused, IReadOnlyCollection<string> Workarounds)
{
  /// <summary>Where the deck went, saved since its last export, such as in PowerPoint, before this one replaced it.
  /// </summary>
  public string? Edited { get; init; }

  public ExportBilling Billed { get; init; } = new ExportBilling(0, 0, 0);
}

/// <summary>What editide bills an export: each slide, layout, and master it writes.</summary>
public record ExportBilling(int Slides, int Layouts, int Masters);

/// <summary>Writes a <c>.pptx</c> by replaying the model's JSON, as <c>--export-json</c> writes it, as editide's write
/// tools on a copy of a blank deck Pintura carries. What has no home in PowerPoint or in editide's tools is kept as
/// a gap.</summary>
public partial class Exporter
{
  // The tools' scheme names for Pintura's theme colors; edit_theme spells the last one folHlink.
  private static readonly Dictionary<string, string> Scheme = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["dark1"] = "dk1", ["light1"] = "lt1", ["dark2"] = "dk2", ["light2"] = "lt2", ["accent1"] = "accent1",
    ["accent2"] = "accent2", ["accent3"] = "accent3", ["accent4"] = "accent4", ["accent5"] = "accent5",
    ["accent6"] = "accent6", ["hyperlink"] = "hlink", ["followed_hyperlink"] = "fol_hlink"
  };

  private static readonly HashSet<string> Markers = ["arrow", "triangle", "stealth", "diamond", "oval"];

  private static readonly Dictionary<string, string> Autofit = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["none"] = "none", ["grow"] = "resize", ["shrink"] = "shrink"
  };

  private static readonly Dictionary<string, int> Turn = new Dictionary<string, int>(StringComparer.Ordinal)
  {
    ["right"] = 0, ["down"] = 90, ["left"] = 180, ["up"] = 270
  };

  private static readonly Dictionary<string, string> Dock = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["center"] = "center", ["inside"] = "inside_end", ["inside_base"] = "inside_base", ["outside"] = "outside_end",
    ["above"] = "above", ["below"] = "below"
  };

  private static readonly Dictionary<string, string> DateCodes = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["YYYY"] = "yyyy", ["YY"] = "yy", ["MMMM"] = "mmmm", ["MMM"] = "mmm", ["MM"] = "mm", ["M"] = "m", ["DD"] = "dd",
    ["D"] = "d"
  };

  private static readonly (string Key, string Name, Func<JsonNode, JsonNode?> Convert, bool Flag)[] Character =
  [
    ("font", "font_family", Font, false),
    ("size", "font_size", node => node.DeepClone(), true),
    ("color", "font_color", node => Color(node), false),
    ("bold", "is_bold", node => node.DeepClone(), true),
    ("italic", "is_italic", node => node.DeepClone(), true),
    ("underline", "is_underlined", node => node.DeepClone(), true)
  ];

  /// <summary>What PowerPoint names the slides before the first section, and so what a presentation's are named.
  /// </summary>
  public const string DefaultSection = "Default Section";

  private readonly ITools tools;
  private readonly Func<string, string?, (string Name, string Source)> media;
  private SortedSet<string> gaps = new SortedSet<string>(StringComparer.Ordinal);
  private int written;
  private int layoutsWritten;
  private int mastersWritten;
  private readonly SortedSet<string> notes = new SortedSet<string>(StringComparer.Ordinal);
  private readonly Dictionary<int, JsonArray> tableFixes = new Dictionary<int, JsonArray>();
  private JsonObject deck = new JsonObject();

  /// <summary><paramref name="media"/> places a picture, named relative to the presentation and recolored by a fill,
  /// where the tools find it, answering the uri they know it by and the file it came from.</summary>
  public Exporter(ITools tools, Func<string, string?, (string Name, string Source)> media)
  {
    this.tools = tools;
    this.media = media;
  }

  /// <summary>The API's largest attachment.</summary>
  private const long MostPictureBytes = 10 * 1024 * 1024;

  public IReadOnlyCollection<string> Gaps => gaps;

  /// <summary>How the exporter works around editide's tools where the deck still comes out right, for whoever drops
  /// each workaround once the tools no longer need it.</summary>
  public IReadOnlyCollection<string> Workarounds => notes;

  /// <summary>A release compiles each call out, its text included, since the notes are for development.</summary>
  [Conditional("DEBUG")]
  private void Workaround(string note)
  {
    notes.Add(note);
  }

  /// <summary>The gaps the theme and layouts raise, kept so a later export that leaves them alone reports them.
  /// </summary>
  public IReadOnlyList<string> DesignGaps { get; private set; } = [];

  /// <summary>Each written or reused slide's gaps, by id.</summary>
  public Dictionary<string, IReadOnlyList<string>> SlideGaps { get; } =
    new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

  /// <summary>How many slides <see cref="Update"/> left as an earlier export wrote them.</summary>
  public int Reused { get; private set; }

  public ExportBilling Billed => new ExportBilling(written, layoutsWritten, mastersWritten);

  /// <summary>The presentation's folder, which picture paths in the model are relative to.</summary>
  public string Root { get; init; } = "";

  /// <summary>The theme's named number formats, which a chart spells as Excel codes; <see cref="Translate"/> sets
  /// them from the deck.</summary>
  public JsonObject Formats { get; set; } = new JsonObject();

  /// <summary>Writes <paramref name="presentation"/> to <paramref name="output"/> through <paramref name="tools"/>,
  /// attaching each picture once. A deck an earlier export wrote, unedited since and with the same design, keeps its
  /// unchanged slides, unless <paramref name="fresh"/>; any other starts from the blank deck at the presentation's
  /// size. <paramref name="progress"/> hears the share of the calls made, against an estimate, so it stops short of 1.
  /// <paramref name="only"/> writes just the slides it names: into a deck an earlier export wrote, alongside its
  /// other slides, left as they were, and refusing one it would have to replace.</summary>
  public static ExportReport Write(Presentation presentation, string output, IToolHost tools,
    Action<double>? progress = null, bool fresh = false, SlideSelection? only = null)
  {
    Stopwatch watch = Stopwatch.StartNew();
    string file = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    JsonObject deck = Model(presentation);
    ExportManifest? previous = fresh ? null : ExportManifest.Read(file);
    HashSet<string> kept = only is null ? [] : Narrow(deck, file, only, previous, presentation.Root);
    if (deck["slides"]!.AsArray().Count == 0)
    {
      throw new InvalidOperationException("the presentation has no slides to export");
    }
    List<string> broken = Items(deck["slides"])
      .Where(slide => Text(Get(slide, "error")) is not null)
      .Select(slide => $"slide {Text(Get(slide, "id"))} failed to load: {Text(Get(slide, "error"))}")
      .ToList();
    if (broken.Count > 0)
    {
      throw new InvalidOperationException($"{string.Join("; ", broken)}; fix {(broken.Count == 1 ? "it" : "them")} " +
        "and export again");
    }
    string design = ExportManifest.DesignHash(deck, presentation.Root);
    // Hashed before anything is uploaded, so a picture replaced during the export is exported again next time.
    Dictionary<string, string> hashes = Items(deck["slides"]).OfType<JsonNode>().ToDictionary(
      slide => Text(Get(slide, "id"))!, slide => ExportManifest.SlideHash(slide, presentation.Root),
      StringComparer.Ordinal);
    foreach (ExportedSlide slide in previous?.Slides.Where(slide => kept.Contains(slide.Id)) ?? [])
    {
      hashes[slide.Id] = slide.Hash;
    }
    bool incremental = previous?.Design == design;
    if (incremental && Unchanged(deck, previous!, hashes))
    {
      return new ExportReport(file, deck["slides"]!.AsArray().Count, 0, watch.Elapsed,
        previous!.DesignGaps.Concat(previous.Slides.SelectMany(slide => slide.Gaps)).ToHashSet(StringComparer.Ordinal),
        previous.Slides.Count, []);
    }
    // An SVG in two fills is two uploads.
    List<(string File, string Source, string Key)> pictures = PictureProperties(deck)
      .Select(properties => (File: Text(Get(properties, "picture"))!, Fill: PictureFill(properties, deck)))
      .Select(picture =>
      {
        string source = Path.GetFullPath(Path.Combine(presentation.Root, picture.File));
        return (picture.File, source, Drawing(source, picture.Fill));
      })
      .DistinctBy(picture => picture.Item3, StringComparer.Ordinal)
      .ToList();
    if (pictures.Count > tools.MostPictures)
    {
      throw new InvalidOperationException($"the presentation uses {pictures.Count} different pictures, and an " +
        $"export takes at most {tools.MostPictures}");
    }
    Writable(file);
    bool edited = ExportManifest.Edited(file);
    DirectoryInfo scratch = Directory.CreateTempSubdirectory("pintura-export-");
    try
    {
      string start = Path.Combine(scratch.FullName, Path.GetFileName(file));
      if (incremental)
      {
        File.Copy(file, start);
      }
      else
      {
        using (Stream source = typeof(Exporter).Assembly.GetManifestResourceStream("Pintura.blank.pptx")!)
        using (FileStream target = File.Create(start))
        {
          source.CopyTo(target);
        }
        Resize(start, Items(deck["size"]).Select(side => Number(side) ?? 0).ToArray());
      }
      Describe(start, presentation.Name);
      Dictionary<string, string> drawn = DrawSvgs(deck, presentation.Root);
      foreach ((string picture, string source, string key) in pictures)
      {
        if (new FileInfo(drawn.GetValueOrDefault(key, source)).Length > MostPictureBytes)
        {
          throw new InvalidOperationException($"picture {picture} is over {MostPictureBytes / 1024 / 1024} MB, the " +
            "most an export takes");
        }
      }
      using IToolSession session = tools.Open(start);
      // About five calls a slide, and twenty for the theme and layouts.
      double expected = (incremental ? 0 : 20) + 5 * deck["slides"]!.AsArray().Count;
      var counting = new CountingTools(session, calls => progress?.Invoke(Math.Min(0.99, calls / expected)));
      var attached = new Dictionary<string, string>(StringComparer.Ordinal);
      var exporter = new Exporter(counting, (relative, fill) =>
      {
        string source = Path.GetFullPath(Path.Combine(presentation.Root, relative));
        string key = Drawing(source, fill);
        if (!attached.TryGetValue(key, out string? uri))
        {
          uri = session.Attach(drawn.GetValueOrDefault(key, source));
          attached[key] = uri;
        }
        return (uri, source);
      })
      {
        Root = presentation.Root
      };
      if (incremental)
      {
        exporter.Update(deck, previous!.Slides, previous.DesignGaps, kept);
      }
      else
      {
        exporter.Translate(deck);
      }
      // Saved beside the scratch copy first, so a failed download leaves the last export and its manifest whole.
      string saved = Path.Combine(scratch.FullName, "saved", Path.GetFileName(file));
      Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
      session.Save(saved);
      List<ExportedSlide> slides = Items(deck["slides"]).OfType<JsonNode>()
        .Select(slide => Text(Get(slide, "id"))!)
        .Where(exporter.SlideGaps.ContainsKey)
        .Select(id => new ExportedSlide(id, hashes[id], exporter.SlideGaps[id]))
        .ToList();
      new ExportManifest("", design, exporter.DesignGaps, slides).Write(saved);
      string? aside = edited ? Free(file, "edited in PowerPoint") : null;
      Place(saved, file, aside);
      return new ExportReport(file, deck["slides"]!.AsArray().Count, counting.Calls, watch.Elapsed, exporter.Gaps,
        exporter.Reused, exporter.Workarounds)
      {
        Edited = aside,
        Billed = exporter.Billed
      };
    }
    finally
    {
      scratch.Delete(true);
    }
  }

  /// <summary>Refuses before anything is billed when another program, such as PowerPoint on Windows, holds the deck
  /// open, as the export couldn't replace it at the end.</summary>
  private static void Writable(string file)
  {
    if (!File.Exists(file))
    {
      return;
    }
    try
    {
      using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
      throw new InvalidOperationException($"can't write {file}, as another program, such as PowerPoint, has it " +
        "open; close it and export again");
    }
    catch (UnauthorizedAccessException exception)
    {
      throw new InvalidOperationException($"can't write {file}: {exception.Message}");
    }
  }

  /// <summary>Puts the saved deck and its manifest where the last export was, first moving a deck edited since to
  /// <paramref name="aside"/>. A deck that can't go there is kept beside it, as it has been billed.</summary>
  private static void Place(string saved, string file, string? aside)
  {
    try
    {
      if (aside is not null)
      {
        File.Move(file, aside);
      }
      File.Move(saved, file, true);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      string kept = Free(file, "exported");
      File.Move(saved, kept);
      throw new InvalidOperationException($"can't write {file} ({exception.Message}); the exported deck is at " +
        $"{kept}", exception);
    }
    File.Move(ExportManifest.PathFor(saved), ExportManifest.PathFor(file), true);
  }

  /// <summary>A name beside <paramref name="file"/> no file has yet, labelled, and numbered past the first.</summary>
  private static string Free(string file, string label)
  {
    string folder = Path.GetDirectoryName(file)!;
    string name = Path.GetFileNameWithoutExtension(file);
    for (int number = 1; ; number++)
    {
      string candidate = Path.Combine(folder,
        $"{name} ({label}{(number == 1 ? "" : $" {number}")}){Path.GetExtension(file)}");
      if (!File.Exists(candidate))
      {
        return candidate;
      }
    }
  }

  /// <summary>Whether the deck already holds every slide as it is, in order, with none failing to load.</summary>
  private static bool Unchanged(JsonObject deck, ExportManifest previous, Dictionary<string, string> hashes)
  {
    List<JsonNode> slides = Items(deck["slides"]).OfType<JsonNode>().ToList();
    return slides.Count == previous.Slides.Count && slides.Zip(previous.Slides).All(pair =>
      Text(Get(pair.First, "error")) is null && Text(Get(pair.First, "id")) == pair.Second.Id &&
      hashes[pair.Second.Id] == pair.Second.Hash);
  }

  /// <summary>Narrows <paramref name="deck"/> to the slides <paramref name="only"/> names and, when
  /// <paramref name="file"/> is a deck an earlier export wrote, the others it holds, returning those to keep as they
  /// were. Refuses a deck it would have to replace: one edited since, one on a design that changed, or one Pintura
  /// didn't export.</summary>
  private static HashSet<string> Narrow(JsonObject deck, string file, SlideSelection only, ExportManifest? previous,
    string root)
  {
    HashSet<string> named = [.. only.Pick(Items(deck["slides"]), slide => Text(Get(slide, "id"))).Select(slide =>
      Text(Get(slide, "id"))!)];
    HashSet<string> kept = [];
    if (File.Exists(file))
    {
      string name = Path.GetFileName(file);
      if (ExportManifest.Edited(file))
      {
        throw new InvalidOperationException($"{name} was edited in PowerPoint since its export, which --slides would " +
          "lose; export to a new file, or bring the edits in first, as --diff lists them");
      }
      if (previous is null)
      {
        throw new InvalidOperationException($"Pintura didn't export {name}, so --slides would replace it; export to " +
          "a new file");
      }
      if (previous.Design != ExportManifest.DesignHash(deck, root))
      {
        throw new InvalidOperationException($"the theme or layouts changed since {name}'s export, so its other " +
          "slides can't stay as they were; export every slide, or to a new file");
      }
      kept = [.. previous.Slides.Select(slide => slide.Id).Where(id => !named.Contains(id))];
    }
    deck["slides"] = new JsonArray(Items(deck["slides"])
      .Where(slide => Text(Get(slide, "id")) is string id && (named.Contains(id) || kept.Contains(id)))
      .Select(slide => slide!.DeepClone()).ToArray());
    return kept;
  }

  /// <summary>Sets the deck's slide size, in points, which editide's tools can't set.</summary>
  public static void Resize(string deck, double[] size)
  {
    XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
    using ZipArchive zip = ZipFile.Open(deck, ZipArchiveMode.Update);
    ZipArchiveEntry entry = zip.GetEntry("ppt/presentation.xml")!;
    XDocument xml;
    using (Stream stream = entry.Open())
    {
      xml = XDocument.Load(stream);
    }
    XElement slideSize = xml.Root!.Element(p + "sldSz")!;
    slideSize.SetAttributeValue("cx", (long)Math.Round(size[0] * 12700));
    slideSize.SetAttributeValue("cy", (long)Math.Round(size[1] * 12700));
    slideSize.SetAttributeValue("type", null);
    entry.Delete();
    using Stream written = zip.CreateEntry("ppt/presentation.xml").Open();
    xml.Save(written, SaveOptions.DisableFormatting);
  }

  /// <summary>Titles the deck as the presentation and dates it now, keeping when an earlier export created it, with no
  /// author, since the deck is the customer's.</summary>
  public static void Describe(string deck, string title)
  {
    XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    XNamespace dc = "http://purl.org/dc/elements/1.1/";
    XNamespace terms = "http://purl.org/dc/terms/";
    XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
    using ZipArchive zip = ZipFile.Open(deck, ZipArchiveMode.Update);
    ZipArchiveEntry entry = zip.GetEntry("docProps/core.xml")!;
    XDocument xml;
    using (Stream stream = entry.Open())
    {
      xml = XDocument.Load(stream);
    }
    XElement core = xml.Root!;
    string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    string created = core.Element(terms + "created")?.Value ?? now;
    core.Elements().Where(element => element.Name == dc + "title" || element.Name == dc + "creator" ||
      element.Name == cp + "lastModifiedBy" || element.Name == terms + "created" || element.Name == terms + "modified")
      .Remove();
    core.Add(new XElement(dc + "title", title),
      new XElement(terms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), created),
      new XElement(terms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now));
    entry.Delete();
    using Stream written = zip.CreateEntry("docProps/core.xml").Open();
    xml.Save(written, SaveOptions.DisableFormatting);
  }

  /// <summary>The presentation as the export writes it, without the layouts no slide uses, as editide bills each
  /// layout written.</summary>
  public static JsonObject Model(Presentation presentation)
  {
    JsonObject deck = JsonSerializer.SerializeToNode(ExportBuilder.Build(presentation), PayloadJson.Options)!.AsObject();
    HashSet<string?> used = Items(deck["slides"]).Select(slide => Text(Get(slide, "layout"))).ToHashSet();
    deck["layouts"] = new JsonArray(Items(deck["layouts"])
      .Where(layout => used.Contains(Text(Get(layout, "id"))))
      .Select(layout => layout?.DeepClone())
      .ToArray());
    return deck;
  }

  /// <summary>Calls a tool with what the model leaves unset left out, as the tools read a missing key and not a null.
  /// </summary>
  private JsonNode? Call(string tool, JsonObject input)
  {
    return tools.Call(tool, Clean(input).AsObject());
  }

  private static JsonNode Clean(JsonNode node)
  {
    if (node is JsonObject map)
    {
      var clean = new JsonObject();
      foreach ((string key, JsonNode? value) in map)
      {
        if (value is not null)
        {
          clean[key] = Clean(value.DeepClone());
        }
      }
      return clean;
    }
    if (node is JsonArray array)
    {
      return Array(array.Select(item => item is null ? null : Clean(item.DeepClone())));
    }
    return node;
  }

  #region Values

  private static JsonNode? Get(JsonNode? properties, string key)
  {
    return properties is JsonObject map && map.TryGetPropertyValue(key, out JsonNode? found) ? found : null;
  }

  private static string? Text(JsonNode? node)
  {
    return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
  }

  private static double? Number(JsonNode? node)
  {
    return node is JsonValue value && value.TryGetValue(out double number) ? number : null;
  }

  private static bool IsFalse(JsonNode? node)
  {
    return node is JsonValue value && value.TryGetValue(out bool flag) && !flag;
  }

  private static bool IsBoolean(JsonNode? node)
  {
    return node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
  }

  /// <summary>The model's notion of set: not missing, false, zero, or empty.</summary>
  private static bool Truthy(JsonNode? node)
  {
    if (node is not JsonValue value)
    {
      return node is not null;
    }
    return value.GetValueKind() switch
    {
      JsonValueKind.False or JsonValueKind.Null => false,
      JsonValueKind.Number => value.GetValue<double>() != 0,
      JsonValueKind.String => value.GetValue<string>().Length > 0,
      _ => true
    };
  }

  private static bool Same(JsonNode? left, JsonNode? right)
  {
    return left is null ? right is null : right is not null && JsonNode.DeepEquals(left, right);
  }

  private static List<JsonNode?> Items(JsonNode? node)
  {
    return node is JsonArray array ? array.ToList() : [];
  }

  private static JsonArray Array(IEnumerable<JsonNode?> nodes)
  {
    return new JsonArray(nodes.ToArray());
  }

  /// <summary>JavaScript's rounding, half up, which Math.Round's half to even is not.</summary>
  private static double Round(double value)
  {
    return Math.Floor(value + 0.5);
  }

  /// <summary>A number that never writes as <c>-0</c>.</summary>
  private static double Negate(double value)
  {
    return 0 - value;
  }

  #endregion

  #region Text

  // Lighter n is PowerPoint's "Lighter n%": luminance modulated by 100 - n and offset by n.
  public static JsonNode Color(JsonNode? model, JsonNode? opacity = null)
  {
    if (model is null || Truthy(Get(model, "none")))
    {
      return "transparent";
    }
    var result = new JsonObject();
    if (Truthy(Get(model, "hex")))
    {
      result["rgb"] = Get(model, "hex")!.DeepClone();
    }
    else if (Text(Get(model, "theme")) is string theme && Scheme.TryGetValue(theme, out string? scheme))
    {
      result["scheme"] = scheme;
    }
    if (Number(Get(model, "lighter")) is double lighter)
    {
      result["luminance_mod"] = 100 - lighter;
      result["luminance_off"] = lighter;
    }
    if (Number(Get(model, "darker")) is double darker)
    {
      result["luminance_mod"] = 100 - darker;
    }
    if (opacity is not null)
    {
      result["alpha"] = opacity.DeepClone();
    }
    return result;
  }

  public static JsonNode? Font(JsonNode model)
  {
    return Text(Get(model, "theme")) switch
    {
      "heading" => "+mj-lt",
      "body" => "+mn-lt",
      _ => Get(model, "family")?.DeepClone()
    };
  }

  // With a base, only what differs from it: what a placeholder inherits stays inherited.
  public static JsonObject TextStyle(JsonNode? properties, JsonNode? baseline = null)
  {
    var style = new JsonObject();
    foreach ((string key, string name, Func<JsonNode, JsonNode?> convert, bool flag) in Character)
    {
      JsonNode? own = Get(properties, key);
      JsonNode? inherited = Get(baseline, key);
      if (baseline is not null && Same(own, inherited))
      {
        continue;
      }
      if (own is not null)
      {
        style[name] = convert(own);
      }
      else if (IsBoolean(inherited) || (baseline is null && flag))
      {
        style[name] = false;
      }
    }
    return style;
  }

  private static JsonObject ParagraphStyle(JsonNode? properties, JsonNode? paragraph = null, JsonNode? baseline = null)
  {
    var style = new JsonObject();
    foreach ((string key, string name) in new[]
      { ("align", "alignment"), ("space_before", "space_before"), ("space_after", "space_after"),
        ("line_spacing", "line_spacing") })
    {
      JsonNode? own = Get(properties, key);
      if (own is not null && !(baseline is not null && Same(own, Get(baseline, key))))
      {
        style[name] = own.DeepClone();
      }
    }
    if (paragraph is not null && Number(Get(paragraph, "level")) > 0)
    {
      style["margin_left"] = Number(Get(paragraph, "indent")) ?? 0;
      style["indent"] = Negate(Number(Get(paragraph, "hanging")) ?? 0);
    }
    else if (paragraph is not null && baseline is null)
    {
      style["margin_left"] = 0;
      style["indent"] = 0;
    }
    if (baseline is null)
    {
      // Unstated, the blank master's 90% line spacing shows through.
      style["space_before"] = Truthy(style["space_before"]) ? style["space_before"]!.DeepClone() : 0;
      style["space_after"] = Truthy(style["space_after"]) ? style["space_after"]!.DeepClone() : 0;
      style["line_spacing"] = style["line_spacing"]?.DeepClone() ?? 1;
    }
    return style;
  }

  // A paragraph or a theme level: its bullet or number, in its own color and size when it sets them.
  public static JsonObject Bullet(JsonNode? paragraph)
  {
    string? character = Text(Get(paragraph, "bullet"));
    if (string.IsNullOrEmpty(character))
    {
      return new JsonObject { ["type"] = "none" };
    }
    JsonObject result = Numbered().IsMatch(character)
      ? new JsonObject { ["type"] = "numbered", ["numbering_scheme"] = "arabic_period" }
      : new JsonObject { ["type"] = "character", ["character"] = character };
    if (Truthy(Get(paragraph, "bulletColor")))
    {
      result["color"] = Color(Get(paragraph, "bulletColor"));
    }
    if (Number(Get(paragraph, "bulletSize")) is double size and not 0)
    {
      result["font_size"] = Round(size * 100);
      result["font_size_is_percentage"] = true;
    }
    return result;
  }

  private static JsonObject TextLayout(JsonNode? properties)
  {
    var layout = new JsonObject();
    if (Get(properties, "insets") is JsonArray insets)
    {
      layout["margin_left"] = insets[0]?.DeepClone();
      layout["margin_top"] = insets[1]?.DeepClone();
      layout["margin_right"] = insets[2]?.DeepClone();
      layout["margin_bottom"] = insets[3]?.DeepClone();
    }
    if (Truthy(Get(properties, "valign")))
    {
      layout["vertical_alignment"] = Get(properties, "valign")!.DeepClone();
    }
    return layout;
  }

  private static JsonObject Run(JsonNode model, int index, JsonNode? baseline)
  {
    JsonObject style = TextStyle(Get(model, "properties"), baseline);
    if (Truthy(Get(model, "link")))
    {
      style["link"] = Get(model, "link")!.DeepClone();
      // The link's own color and underline are PowerPoint's to draw.
      style.Remove("font_color");
      style.Remove("is_underlined");
    }
    var result = new JsonObject { ["index"] = index };
    if (Text(Get(model, "field")) == "slide")
    {
      result["field"] = "slidenum";
    }
    else if (Get(model, "text") is JsonNode text)
    {
      result["text"] = text.DeepClone();
    }
    if (style.Count > 0)
    {
      result["text_style"] = style;
    }
    return result;
  }

  // A base is the placeholder's own paragraph as the layout defines it; without one every property is written.
  private static JsonObject TextComponent(JsonNode item, JsonNode? baseline = null)
  {
    var paragraphs = new JsonArray();
    int index = 0;
    foreach (JsonNode? paragraph in Items(Get(item, "paragraphs")))
    {
      List<JsonNode?> runs = Items(Get(paragraph, "runs"));
      // An empty paragraph's line is as tall as its own size, which PowerPoint reads only from a run.
      if (runs.Count == 0)
      {
        runs = [new JsonObject { ["text"] = "", ["properties"] = Get(paragraph, "properties")?.DeepClone() }];
      }
      var entry = new JsonObject
      {
        ["index"] = index++,
        ["runs"] = Array(runs.Select((model, at) => Run(model!, at, baseline)))
      };
      JsonObject style = ParagraphStyle(Get(paragraph, "properties"), paragraph, baseline);
      if (style.Count > 0)
      {
        entry["style"] = style;
      }
      if (baseline is null)
      {
        entry["bullet"] = Bullet(paragraph);
      }
      paragraphs.Add(entry);
    }
    var component = new JsonObject { ["paragraph_list"] = paragraphs };
    if (baseline is null && TextLayout(Get(item, "properties")) is { Count: > 0 } layout)
    {
      component["text_layout"] = layout;
    }
    return component;
  }

  #endregion

  #region Shapes

  private static JsonObject Box(JsonNode? frame)
  {
    return Box(Items(frame).Select(side => Number(side) ?? 0).ToArray());
  }

  private static JsonObject Box(double[] frame)
  {
    return new JsonObject
    {
      ["position"] = new JsonObject { ["left"] = frame[0], ["top"] = frame[1] },
      ["size"] = new JsonObject { ["width"] = frame[2], ["height"] = frame[3] }
    };
  }

  private JsonObject ShapeStyle(JsonNode? properties)
  {
    var style = new JsonObject();
    JsonNode? opacity = Get(properties, "opacity");
    if (Truthy(Get(properties, "fill")))
    {
      style["fill"] = Color(Get(properties, "fill"), opacity);
    }
    else if (opacity is not null)
    {
      gaps.Add("opacity on an element with no fill isn't exported");
    }
    Border(style, properties);
    Effects(style, properties);
    return style;
  }

  private static void Border(JsonObject style, JsonNode? properties)
  {
    if (Text(Get(properties, "stroke")) == "none")
    {
      style["border_color"] = "transparent";
      style["border_weight"] = 0;
    }
    if (Truthy(Get(properties, "stroke.color")))
    {
      style["border_color"] = Color(Get(properties, "stroke.color"));
      style["border_weight"] = Get(properties, "stroke.width")?.DeepClone();
      if (Text(Get(properties, "stroke.dash")) is string dash and not "" and not "solid")
      {
        style["border_dash_type"] = dash;
      }
    }
  }

  /// <summary>A shadow as the shape style's effects; one with no color draws nothing, as in the preview.</summary>
  private static void Effects(JsonObject style, JsonNode? properties)
  {
    if (!Truthy(Get(properties, "shadow.color")))
    {
      return;
    }
    var shadow = new JsonObject
    {
      ["color"] = Color(Get(properties, "shadow.color"), Get(properties, "shadow.opacity"))
    };
    foreach (string key in new[] { "blur", "distance", "angle" })
    {
      shadow[key] = Number(Get(properties, $"shadow.{key}")) ?? 0;
    }
    style["effects"] = new JsonObject { ["shadow"] = shadow };
  }

  public record Geometry(string? Type, int Rotation, JsonObject Adjustments, bool FlipVertical);

  // The tools take adjustments as 0 to 1 across each handle's range, where Pintura writes points.
  public Geometry Preset(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    double[] frame = Items(Get(properties, "frame")).Select(side => Number(side) ?? 0).ToArray();
    double width = frame[2];
    double height = frame[3];
    double shorter = Math.Min(width, height);
    string? shape = Text(Get(properties, "shape"));
    string? direction = Text(Get(properties, "direction"));
    double? Adjust(string name) => Number(Get(properties, $"adjust.{name}"));
    string? type = shape;
    int rotation = 0;
    bool flipVertical = false;
    var adjustments = new JsonObject();

    // Directional presets point right; the rest of the turn is a rotation, whose unrotated box swaps the sides.
    double Turned(string turnedType, string along, string from = "right")
    {
      type = turnedType;
      rotation = ((direction is not null && Turn.TryGetValue(direction, out int to) ? to : 0) - Turn[from] + 360) % 360;
      double length = along == "width" ? width : height;
      return rotation % 180 == 0 ? length : along == "width" ? height : width;
    }

    switch (shape)
    {
      case "rectangle":
      case "ellipse":
      case "diamond":
      case "pentagon":
      case "hexagon":
        break;
      case "rounded_rectangle":
        type = "round_rectangle";
        if (Adjust("corner") is double corner)
        {
          adjustments["corner_radius"] = Math.Min(1, corner / (shorter / 2));
        }
        break;
      case "parallelogram":
        if (Adjust("slant") is double slant)
        {
          adjustments["adjustment"] = Math.Min(1, slant / width);
          Workaround("parallelogram slant: the tools' handle name unconfirmed");
        }
        break;
      case "trapezoid":
        if (Adjust("slant") is double trapezoidSlant)
        {
          adjustments["adjustment"] = Math.Min(1, trapezoidSlant / (width / 2));
          Workaround("trapezoid slant: the tools' handle name unconfirmed");
        }
        break;
      case "triangle":
        if (direction == "down")
        {
          flipVertical = true;
        }
        else if (direction != "up")
        {
          Turned("triangle", "height", "up");
        }
        break;
      case "right_triangle":
        if (direction != "up")
        {
          gaps.Add($"a right triangle facing {direction} exports facing right, as PowerPoint has no such shape and a " +
            "turn moves its right angle");
        }
        break;
      case "chevron":
      case "home_plate":
      {
        double length = Turned(shape, "width");
        if (Adjust("point") is double point)
        {
          adjustments["point_depth"] = Math.Min(1, point / length);
        }
        break;
      }
      case "arrow":
        type = direction switch
        {
          "right" => "right_arrow",
          "left" => "left_arrow",
          "up" => "up_arrow",
          "down" => "down_arrow",
          _ => null
        };
        if (Adjust("point") is double head)
        {
          double length = direction is "left" or "right" ? width : height;
          adjustments["head_length"] = Math.Min(1, head / length);
        }
        break;
      case "brace":
      case "bracket":
        if (direction == "left")
        {
          type = $"left_{shape}";
        }
        else
        {
          Turned($"right_{shape}", "width");
        }
        gaps.Add($"a {shape} exports with PowerPoint's curl rather than a quarter of its thickness");
        break;
      case "callout":
        type = "wedge_rectangle_callout";
        // The tools normalize the unbounded tail_x/tail_y handles to ±2147483647.
        gaps.Add("a callout's tail isn't exported, so it points elsewhere");
        break;
      default:
        gaps.Add($"a {shape} shape isn't exported");
        break;
    }
    return new Geometry(type, rotation, adjustments, flipVertical);
  }

  private JsonObject Placed(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    JsonNode? frame = Get(properties, "frame");
    JsonObject result = Box(frame);
    JsonObject position = result["position"]!.AsObject();
    double rotate = Number(Get(properties, "rotate")) ?? 0;
    string? flip = Text(Get(properties, "flip"));
    if (Text(Get(item, "kind")) == "shape")
    {
      Geometry geometry = Preset(item);
      result["shape_type"] = geometry.Type;
      if (geometry.Adjustments.Count > 0)
      {
        result["shape_adjustments"] = geometry.Adjustments;
      }
      if (geometry.Rotation % 180 != 0)
      {
        result["size"] = new JsonObject { ["width"] = frame![3]!.DeepClone(), ["height"] = frame[2]!.DeepClone() };
      }
      double rotation = (rotate + geometry.Rotation) % 360;
      if (rotation != 0)
      {
        position["rotation"] = rotation;
      }
      if (geometry.FlipVertical)
      {
        position["flip_vertical"] = true;
      }
    }
    else if (rotate != 0)
    {
      position["rotation"] = rotate;
    }
    if (flip is "horizontal" or "both")
    {
      position["flip_horizontal"] = !Truthy(position["flip_horizontal"]);
    }
    if (flip is "vertical" or "both")
    {
      position["flip_vertical"] = !Truthy(position["flip_vertical"]);
    }
    return result;
  }

  // A line's box runs between its endpoints, and flips say which corner each end sits in.
  public static JsonObject Line(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    double x1 = Number(Get(properties, "from")![0]) ?? 0;
    double y1 = Number(Get(properties, "from")![1]) ?? 0;
    double x2 = Number(Get(properties, "to")![0]) ?? 0;
    double y2 = Number(Get(properties, "to")![1]) ?? 0;
    var position = new JsonObject { ["left"] = Math.Min(x1, x2), ["top"] = Math.Min(y1, y2) };
    var element = new JsonObject
    {
      ["shape_type"] = "line",
      ["position"] = position,
      ["size"] = new JsonObject { ["width"] = Math.Abs(x2 - x1), ["height"] = Math.Abs(y2 - y1) }
    };
    if (x2 < x1)
    {
      position["flip_horizontal"] = true;
    }
    if (y2 < y1)
    {
      position["flip_vertical"] = true;
    }
    var style = new JsonObject();
    if (Truthy(Get(properties, "stroke.color")))
    {
      style["border_color"] = Color(Get(properties, "stroke.color"));
      style["border_weight"] = Get(properties, "stroke.width")?.DeepClone();
    }
    if (Text(Get(properties, "stroke.dash")) is string dash and not "" and not "solid")
    {
      style["border_dash_type"] = dash;
    }
    foreach ((string key, string name) in new[] { ("start", "head_arrow"), ("end", "tail_arrow") })
    {
      if (Text(Get(properties, key)) is string marker && Markers.Contains(marker))
      {
        style[name] = marker;
      }
    }
    element["shape_style"] = style;
    return element;
  }

  #endregion

  #region Pictures

  private const int SvgPixelsAPoint = 4;
  private const int MostSvgPixels = 4096;

  /// <summary>Each SVG picture's full path, with the size to draw it at, in pixels: its own proportions, covering its
  /// largest frame at four pixels a point, sharp in print, and at most 4096 pixels a side.</summary>
  public static Dictionary<string, (int Width, int Height)> SvgSizes(JsonObject deck, string root)
  {
    var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.Ordinal);
    foreach (JsonNode properties in PictureProperties(deck))
    {
      string file = Path.GetFullPath(Path.Combine(root, Text(Get(properties, "picture"))!));
      if (!file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
      {
        continue;
      }
      double[] frame = Items(Get(properties, "frame")).Select(side => Number(side) ?? 0).ToArray();
      double[] own = Intrinsic(file) ?? [frame[2], frame[3]];
      double scale = SvgPixelsAPoint * Math.Max(frame[2] / own[0], frame[3] / own[1]);
      scale = Math.Min(scale, MostSvgPixels / Math.Max(own[0], own[1]));
      (int Width, int Height) size = ((int)Math.Round(own[0] * scale), (int)Math.Round(own[1] * scale));
      if (!sizes.TryGetValue(file, out (int Width, int Height) drawn) || size.Width > drawn.Width)
      {
        sizes[file] = size;
      }
    }
    return sizes;
  }

  private static IEnumerable<JsonNode> PictureProperties(JsonNode? node)
  {
    if (node is JsonArray array)
    {
      return array.SelectMany(PictureProperties);
    }
    if (node is not JsonObject json)
    {
      return [];
    }
    if (Text(json["kind"]) == "picture" && Text(Get(json["properties"], "picture")) is not null)
    {
      return [json["properties"]!];
    }
    return json.Select(pair => pair.Value).SelectMany(PictureProperties);
  }

  /// <summary>The hex a picture's fill resolves to against the deck's theme colors, or null for none.</summary>
  public static string? PictureFill(JsonNode? properties, JsonNode deck)
  {
    JsonNode? fill = Get(properties, "fill");
    if (fill is null || Truthy(Get(fill, "none")))
    {
      return null;
    }
    if (Text(Get(fill, "hex")) is string hex)
    {
      return hex;
    }
    string themed = Text(Get(Get(Get(deck, "theme"), "colors"), Text(Get(fill, "theme"))!))!;
    if (Number(Get(fill, "lighter")) is double lighter)
    {
      return ColorMath.Luminance(themed, (100 - lighter) / 100, lighter / 100);
    }
    if (Number(Get(fill, "darker")) is double darker)
    {
      return ColorMath.Luminance(themed, (100 - darker) / 100, 0);
    }
    return themed;
  }

  private static string Drawing(string file, string? fill)
  {
    return fill is null ? file : $"{file}|{fill}";
  }

  /// <summary>Each SVG picture in each of its fills, keyed by <see cref="Drawing"/>, with a PNG of it, drawn by the
  /// headless browser, since an SVG can carry script and the tools take none. PNGs are kept under the temp folder by
  /// the SVG's hash, fill, and size, so only a new or changed SVG starts the browser.</summary>
  private static Dictionary<string, string> DrawSvgs(JsonObject deck, string root)
  {
    string cache = Temporary.Folder("pictures");
    var drawn = new Dictionary<string, string>(StringComparer.Ordinal);
    var missing = new List<(string File, byte[] Svg, int Width, int Height)>();
    Dictionary<string, (int Width, int Height)> sizes = SvgSizes(deck, root);
    IEnumerable<(string File, string? Fill)> drawings = PictureProperties(deck)
      .Select(properties => (File: Path.GetFullPath(Path.Combine(root, Text(Get(properties, "picture"))!)),
        Fill: PictureFill(properties, deck)))
      .Where(drawing => sizes.ContainsKey(drawing.File))
      .Distinct();
    foreach ((string file, string? fill) in drawings)
    {
      (int width, int height) = sizes[file];
      byte[] svg = fill is null
        ? File.ReadAllBytes(file)
        : Encoding.UTF8.GetBytes(SvgPaint.Recolor(File.ReadAllText(file), fill));
      string hash = Convert.ToHexString(SHA256.HashData([.. svg, .. Encoding.UTF8.GetBytes($"|{width}x{height}")]));
      string png = Path.Combine(cache, $"{hash[..16].ToLowerInvariant()}.png");
      drawn[Drawing(file, fill)] = png;
      if (!File.Exists(png))
      {
        missing.Add((png, svg, width, height));
      }
    }
    if (missing.Count > 0)
    {
      List<byte[]> pngs = HeadlessBrowser.Rasterize(missing.Select(picture => (picture.Svg, picture.Width,
        picture.Height)).ToList()).GetAwaiter().GetResult();
      Directory.CreateDirectory(cache);
      foreach (((string png, _, _, _), byte[] bytes) in missing.Zip(pngs))
      {
        File.WriteAllBytes(png, bytes);
      }
    }
    return drawn;
  }

  // The picture's own size, for fitting it inside its frame as the preview does.
  public static double[]? Intrinsic(string file)
  {
    byte[] bytes = File.ReadAllBytes(file);
    if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
    {
      string text = System.Text.Encoding.UTF8.GetString(bytes);
      Match box = ViewBox().Match(text);
      if (box.Success)
      {
        return [Parse(box.Groups[1].Value), Parse(box.Groups[2].Value)];
      }
      Match width = SvgWidth().Match(text);
      Match height = SvgHeight().Match(text);
      return width.Success && height.Success ? [Parse(width.Groups[1].Value), Parse(height.Groups[1].Value)] : null;
    }
    if (bytes.Length >= 24 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x89504e47)
    {
      return [BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))];
    }
    if (bytes.Length >= 10 && bytes.AsSpan(0, 3).SequenceEqual("GIF"u8))
    {
      return [BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8))];
    }
    if (bytes.Length >= 30 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
    {
      return WebPSize(bytes);
    }
    return bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 ? JpegSize(bytes) : null;
  }

  private static double[]? WebPSize(byte[] bytes)
  {
    ReadOnlySpan<byte> chunk = bytes.AsSpan(12, 4);
    if (chunk.SequenceEqual("VP8 "u8))
    {
      return [BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) & 0x3fff,
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) & 0x3fff];
    }
    if (chunk.SequenceEqual("VP8L"u8))
    {
      uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21));
      return [(packed & 0x3fff) + 1, (packed >> 14 & 0x3fff) + 1];
    }
    if (chunk.SequenceEqual("VP8X"u8))
    {
      return [(bytes[24] | bytes[25] << 8 | bytes[26] << 16) + 1, (bytes[27] | bytes[28] << 8 | bytes[29] << 16) + 1];
    }
    return null;
  }

  /// <summary>A JPEG's size, from the first start-of-frame segment; markers C4, C8, and CC share the range but
  /// aren't frames.</summary>
  private static double[]? JpegSize(byte[] bytes)
  {
    int at = 2;
    while (at + 9 <= bytes.Length && bytes[at] == 0xff)
    {
      byte marker = bytes[at + 1];
      if (marker is >= 0xc0 and <= 0xcf and not 0xc4 and not 0xc8 and not 0xcc)
      {
        return [BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 7)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 5))];
      }
      at += 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2));
    }
    return null;
  }

  private static double Parse(string text)
  {
    return double.Parse(text, CultureInfo.InvariantCulture);
  }

  public static double[] Contain(double[] frame, double[]? size)
  {
    if (size is null)
    {
      return frame;
    }
    double scale = Math.Min(frame[2] / size[0], frame[3] / size[1]);
    double width = size[0] * scale;
    double height = size[1] * scale;
    return [frame[0] + (frame[2] - width) / 2, frame[1] + (frame[3] - height) / 2, width, height];
  }

  /// <summary>Whether to lock a picture's aspect ratio, which the tools keep by refitting the frame, so only a frame
  /// already at the picture's own ratio locks.</summary>
  public static bool LocksAspect(double[] frame, double[]? size)
  {
    return size is null || Math.Abs(frame[2] / frame[3] / (size[0] / size[1]) - 1) < 0.01;
  }

  public JsonObject Picture(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    string? fill = PictureFill(properties, deck);
    (string name, string source) = media(Text(Get(properties, "picture"))!, fill);
    double[] frame = Items(Get(properties, "frame")).Select(side => Number(side) ?? 0).ToArray();
    string? fit = Text(Get(properties, "fit"));
    if (fit == "contain")
    {
      frame = Contain(frame, Intrinsic(source));
    }
    else if (fit == "cover")
    {
      // The tools write no a:srcRect.
      gaps.Add("fit: cover needs a crop, which isn't exported");
    }
    if (Truthy(Get(properties, "crop")))
    {
      gaps.Add("picture crop isn't exported");
    }
    if (Truthy(Get(properties, "alt")))
    {
      gaps.Add("picture alt text isn't exported");
    }
    JsonObject placed = Box(frame);
    var element = new JsonObject
    {
      ["uri"] = name,
      ["position"] = placed["position"]!.DeepClone(),
      ["size"] = placed["size"]!.DeepClone(),
      ["lock_aspect_ratio"] = LocksAspect(frame, Intrinsic(source))
    };
    if (Get(properties, "opacity") is not null)
    {
      gaps.Add("picture opacity isn't exported");
    }
    var style = new JsonObject();
    Border(style, properties);
    Effects(style, properties);
    if (style.Count > 0)
    {
      element["shape_style"] = style;
    }
    return element;
  }

  #endregion

  #region Charts

  // A date pattern as an Excel code, every character outside its tokens escaped.
  private static string DateCode(string pattern)
  {
    return DateTokens().Replace(pattern, match => DateCodes.TryGetValue(match.Value, out string? code)
      ? code
      : string.Concat(match.Value.Select(letter => $"\\{letter}")));
  }

  // Excel scales by a thousand per trailing comma and by a hundred with %, so other scales have no code.
  public string NumberFormat(JsonNode format)
  {
    if (Text(Get(format, "date")) is string { Length: > 0 } date)
    {
      return DateCode(date);
    }
    double scale = Number(Get(format, "scale")) ?? 1;
    string percent = "";
    if (Text(Get(format, "suffix")) == "%" && Math.Abs(scale - 100) < 1e-9)
    {
      percent = "%";
      scale = 1;
    }
    string commas = "";
    while (scale < 1 - 1e-9 && commas.Length < 3)
    {
      scale *= 1000;
      commas += ",";
    }
    if (Math.Abs(scale - 1) > 1e-9)
    {
      gaps.Add($"a chart's format scale of {Number(Get(format, "scale"))?.ToString(CultureInfo.InvariantCulture)} " +
        "isn't exported, as PowerPoint scales only by thousands, or by a hundred as a percent");
    }
    static string Quote(string? text) => string.IsNullOrEmpty(text) ? "" : $"\"{text.Replace("\"", "")}\"";
    int decimals = (int)(Number(Get(format, "decimals")) ?? 0);
    bool separator = Get(format, "separator") is not JsonNode flag || Truthy(flag);
    string digits = (separator ? "#,##0" : "0") + (decimals != 0 ? $".{new string('0', decimals)}" : "");
    string positive = $"{Quote(Text(Get(format, "prefix")))}{digits}{commas}" +
      (percent.Length > 0 ? percent : Quote(Text(Get(format, "suffix"))));
    if (Truthy(Get(format, "negativeColor")))
    {
      gaps.Add("a chart's negative_style isn't exported, as PowerPoint colors negatives only from a fixed palette");
    }
    return (Text(Get(format, "negative")) ?? "parentheses") == "parentheses" ? $"{positive};({positive})" : positive;
  }

  private static JsonObject ChartText(JsonNode? properties)
  {
    var style = new JsonObject();
    if (Truthy(Get(properties, "text.font")))
    {
      style["font_family"] = Font(Get(properties, "text.font")!);
    }
    if (Get(properties, "text.size") is JsonNode size)
    {
      style["font_size"] = size.DeepClone();
    }
    if (Truthy(Get(properties, "text.color")))
    {
      style["font_color"] = Color(Get(properties, "text.color"));
    }
    return style;
  }

  private string? Code(string? name)
  {
    if (string.IsNullOrEmpty(name))
    {
      return null;
    }
    if (Get(Formats, name) is not JsonNode format)
    {
      gaps.Add($"a chart's format {name} isn't in the theme, so its numbers export unformatted");
      return null;
    }
    return NumberFormat(format);
  }

  private record Series(JsonNode Entry, List<JsonNode?> Values, int Color)
  {
    public string? Type => Text(Get(Entry, "type"));
  }

  // What Pintura leaves unset is PowerPoint's default, except the legend, the title, and gridlines, which stay off.
  public JsonObject Chart(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    string type = Text(Get(properties, "type")) ?? "";
    bool pie = type is "pie" or "doughnut";
    bool line = type.EndsWith("line", StringComparison.Ordinal);
    List<JsonNode?> colors = Items(Get(properties, "series_colors"));
    // PowerPoint draws a bar chart's first category at the bottom, and a clustered group's first series too. The
    // tools have no reversed category axis (c:orientation maxMin), so the data goes in last first and the embedded
    // sheet is upside down.
    bool bar = type.EndsWith("bar", StringComparison.Ordinal);
    List<JsonNode?> categories = Items(Get(properties, "categories"));
    if (bar)
    {
      categories.Reverse();
    }
    List<Series> series = Items(Get(properties, "series")).Select((entry, index) =>
    {
      List<JsonNode?> values = Items(Get(entry, "values"));
      if (bar)
      {
        values.Reverse();
      }
      return new Series(entry!, values, index);
    }).ToList();
    if (type == "clustered_bar")
    {
      series.Reverse();
    }
    JsonObject text = ChartText(properties);
    JsonNode? OwnColor(Series entry) => colors.Count > 0 ? Color(colors[entry.Color % colors.Count]) : null;

    // A series with keys of its own carries its labels resolved; the rest take the chart's.
    JsonObject? LabelsOf(Series entry)
    {
      return Labels(entry, key => Get(entry.Entry, "labels") is JsonNode own
        ? Get(own, $"data_labels.{key}")
        : Get(properties, $"data_labels.{key}"));
    }

    JsonObject? Labels(Series entry, Func<string, JsonNode?> Lookup)
    {
      string? show = Text(Lookup("show"));
      if (show is not ("value" or "percent"))
      {
        return null;
      }
      var labels = new JsonObject { [show == "value" ? "show_value" : "show_percentage"] = true };
      foreach (string other in new[]
        { "show_value", "show_percentage", "show_category_name", "show_series_name", "show_legend_key" })
      {
        labels[other] = Truthy(labels[other]);
      }
      string? position = entry.Type == "total" ? "above" : Text(Lookup("position"));
      if (!string.IsNullOrEmpty(position) && type != "doughnut" && Dock.TryGetValue(position, out string? dock))
      {
        labels["dock"] = dock;
      }
      string? format = show == "percent" ? "0%" : Code(Text(Lookup("format")));
      if (format is not null)
      {
        labels["number_format"] = format;
      }
      JsonObject runs = text.DeepClone().AsObject();
      if (Truthy(Lookup("color")))
      {
        runs["font_color"] = Color(Lookup("color"));
      }
      if (Truthy(Lookup("bold")))
      {
        runs["is_bold"] = true;
      }
      labels["default_text_style_for_all_runs"] = runs;
      return labels;
    }

    // A marker is outlined in its line's color and width, and filled with the line's color unless it names a fill; a
    // point's own keys go over its series'.
    JsonObject MarkersOf(Series entry, JsonNode? own, string? point = null)
    {
      JsonNode? Lookup(string key) =>
        (point is not null && Get(entry.Entry, "points") is JsonNode points
          ? Get(points, $"points.{point}.markers.{key}")
          : null)
        ?? (Get(entry.Entry, "markers") is JsonNode markers
          ? Get(markers, $"markers.{key}")
          : Get(properties, $"markers.{key}"));
      string shape = entry.Type == "total" ? "none" : Text(Lookup("shape")) is string { Length: > 0 } named
        ? named
        : "none";
      if (shape == "none")
      {
        return new JsonObject { ["marker_type"] = "none" };
      }
      var style = new JsonObject { ["border_weight"] = 2.25 };
      if (own is not null)
      {
        style["border_color"] = own.DeepClone();
      }
      JsonNode? fill = Truthy(Lookup("fill")) ? Color(Lookup("fill")) : own?.DeepClone();
      if (fill is not null)
      {
        style["fill"] = fill;
      }
      var marker = new JsonObject { ["marker_type"] = shape };
      if (Lookup("size") is JsonNode size)
      {
        marker["size"] = size.DeepClone();
      }
      marker["marker_shape_style"] = style;
      return marker;
    }

    bool secondary = series.Any(entry => Truthy(Get(entry.Entry, "secondary")));

    var seriesList = new JsonArray();
    for (int index = 0; index < series.Count; index++)
    {
      Series entry = series[index];
      // A total over columns is a line in no color whose labels sit above its points, at the stacks' ends, so the
      // value axis scales to the stacks alone.
      bool drawnAsLine = line || entry.Type == "line" || (entry.Type == "total" && !bar);
      var result = new JsonObject
      {
        ["index"] = index,
        ["name"] = Get(entry.Entry, "name")?.DeepClone(),
        ["chart_type"] = drawnAsLine && !line ? "line" : type
      };
      if (Truthy(Get(entry.Entry, "secondary")))
      {
        result["uses_secondary_axis"] = true;
      }
      if (entry.Type == "total")
      {
        result["default_data_point"] = bar
          ? new JsonObject { ["fill"] = "transparent" }
          : new JsonObject { ["border"] = new JsonObject { ["color"] = "transparent" } };
      }
      else if (pie && colors.Count > 0)
      {
        result["data_points"] = Array(categories.Select((_, point) => (JsonNode?)new JsonObject
        {
          ["index"] = point,
          ["fill"] = Color(colors[point % colors.Count])
        }));
      }
      else if (colors.Count > 0)
      {
        JsonNode own = OwnColor(entry)!;
        result["default_data_point"] = drawnAsLine
          ? new JsonObject { ["border"] = new JsonObject { ["color"] = own, ["weight"] = 2.25 } }
          : new JsonObject { ["fill"] = own };
      }
      if (Get(entry.Entry, "points") is JsonNode points && !drawnAsLine)
      {
        result["data_points"] = Array(categories
          .Select((category, point) => (Point: point, Color: Get(points, $"points.{Text(category)}.color")))
          .Where(pair => pair.Color is not null)
          .Select(pair => (JsonNode?)new JsonObject { ["index"] = pair.Point, ["fill"] = Color(pair.Color) }));
      }
      if (drawnAsLine)
      {
        result["default_marker"] = MarkersOf(entry, OwnColor(entry));
        if (Get(entry.Entry, "points") is JsonObject linePoints)
        {
          var markerList = new JsonArray();
          for (int point = 0; point < categories.Count; point++)
          {
            string category = Text(categories[point])!;
            if (linePoints.Any(pair => pair.Key.StartsWith($"points.{category}.", StringComparison.Ordinal)))
            {
              JsonObject marker = MarkersOf(entry,
                Get(linePoints, $"points.{category}.color") is JsonNode color ? Color(color) : OwnColor(entry), category);
              var indexed = new JsonObject { ["index"] = point };
              foreach ((string key, JsonNode? value) in marker.ToList())
              {
                marker.Remove(key);
                indexed[key] = value;
              }
              markerList.Add(indexed);
            }
          }
          result["marker_list"] = markerList;
        }
      }
      JsonObject? seriesLabels = LabelsOf(entry);
      if (seriesLabels is not null)
      {
        if (entry.Type == "total" && bar)
        {
          seriesLabels["dock"] = "inside_base";
        }
        result["default_data_label"] = seriesLabels;
      }
      else
      {
        result["delete_all_data_labels"] = true;
      }
      // A point's own label is a data label by index, styled in its text component's one run, which keeps it linked
      // to the value; one shown none on a series that shows the rest shows nothing.
      if (Get(entry.Entry, "points") is JsonObject labelled)
      {
        var byIndex = new JsonArray();
        for (int point = 0; point < categories.Count; point++)
        {
          string prefix = $"points.{Text(categories[point])}.data_labels.";
          if (Get(labelled, $"{prefix}show") is null)
          {
            continue;
          }
          if (Labels(entry, key => Get(labelled, prefix + key)) is not JsonObject own)
          {
            if (seriesLabels is not null)
            {
              Workaround("a point's hidden label: data_labels_to_delete does nothing before the series has labels, " +
                "which default_data_label only gives it later in the same call, so it goes as a label showing nothing");
              byIndex.Add(new JsonObject { ["index"] = point, ["show_value"] = false });
            }
            continue;
          }
          // A total's labels keep the dock its series gives them.
          var label = new JsonObject { ["index"] = point, ["show_value"] = true };
          if (entry.Type != "total" && own["dock"] is JsonNode dock)
          {
            label["dock"] = dock.DeepClone();
          }
          if (own["number_format"] is JsonNode format)
          {
            label["number_format"] = format.DeepClone();
          }
          // A series without labels gives a point's label no style to inherit, so it carries the chart's text whole.
          JsonObject style = own["default_text_style_for_all_runs"]!.DeepClone().AsObject();
          style["is_bold"] = Truthy(Get(labelled, $"{prefix}bold"));
          label["text_component"] = new JsonObject
          {
            ["paragraph_list"] = new JsonArray(new JsonObject
            {
              ["index"] = 0,
              ["runs"] = new JsonArray(new JsonObject { ["index"] = 0, ["text_style"] = style })
            })
          };
          byIndex.Add(label);
        }
        if (byIndex.Count > 0)
        {
          result["data_labels"] = byIndex;
        }
      }
      seriesList.Add(result);
    }

    var data = new JsonObject();
    for (int point = 0; point < categories.Count; point++)
    {
      var values = new JsonObject();
      for (int index = 0; index < series.Count; index++)
      {
        if (point < series[index].Values.Count && series[index].Values[point] is JsonNode number)
        {
          values[$"{index}:{Label(Get(series[index].Entry, "name"))}"] = number.DeepClone();
        }
      }
      data[$"{point}:{Label(categories[point])}"] = values;
    }

    JsonObject element = Box(Get(properties, "frame"));
    element["series_list"] = seriesList;
    element["chart_data"] = data;

    if (Text(Get(properties, "title")) is string { Length: > 0 } title)
    {
      JsonObject style = text.DeepClone().AsObject();
      style["is_bold"] = true;
      if (Number(text["font_size"]) is double size and not 0)
      {
        style["font_size"] = size * 1.2;
      }
      element["chart_title"] = new JsonObject
      {
        ["overlay"] = false,
        ["text_component"] = new JsonObject
        {
          ["paragraph_list"] = new JsonArray(new JsonObject
          {
            ["index"] = 0,
            ["runs"] = new JsonArray(new JsonObject { ["index"] = 0, ["text"] = title, ["text_style"] = style })
          })
        }
      };
    }
    else
    {
      element["remove_chart_title"] = true;
    }

    string? legend = Text(Get(properties, "legend"));
    if (!string.IsNullOrEmpty(legend) && legend != "none")
    {
      var chartLegend = new JsonObject { ["dock"] = legend, ["overlay"] = false, ["text_style"] = text.DeepClone() };
      // An entry for a series or slice drawn in no color comes off, as the preview leaves it out.
      int count = pie ? categories.Count : series.Count;
      List<int> hidden = Enumerable.Range(0, count)
        .Where(index => (!pie && series[index].Type == "total") ||
          (colors.Count > 0 && Truthy(Get(colors[(pie ? index : series[index].Color) % colors.Count], "none"))))
        .ToList();
      if (hidden.Count > 0)
      {
        chartLegend["legend_entries"] = Array(hidden.Select(index =>
          (JsonNode?)new JsonObject { ["index"] = index, ["delete"] = true }));
      }
      element["chart_legend"] = chartLegend;
    }
    else
    {
      element["remove_chart_legend"] = true;
    }

    if (!pie)
    {
      JsonObject Axis(string name, bool percent)
      {
        var result = new JsonObject { ["labels_text_style"] = text.DeepClone() };
        if (Truthy(Get(properties, $"{name}.reverse")))
        {
          // No field for a reversed value axis (c:orientation maxMin).
          gaps.Add($"a reversed {(name == "value_axis" ? "value" : "secondary")} axis isn't exported, " +
            "so it runs bottom up in PowerPoint");
        }
        if (IsFalse(Get(properties, $"{name}.visible")))
        {
          result["is_deleted"] = true;
        }
        foreach (string key in new[] { "min", "max" })
        {
          if (Get(properties, $"{name}.{key}") is JsonNode limit)
          {
            result[key] = limit.DeepClone();
          }
        }
        if (Get(properties, $"{name}.step") is JsonNode step)
        {
          result["units"] = step.DeepClone();
        }
        string? format = percent ? "0%" : Code(Text(Get(properties, $"{name}.format")));
        if (format is not null)
        {
          result["number_format"] = format;
        }
        if (Truthy(Get(properties, $"{name}.gridlines")))
        {
          result["major_gridlines"] = new JsonObject();
        }
        else
        {
          result["remove_major_gridlines"] = true;
        }
        return result;
      }
      element["primary_value_axis"] = Axis("value_axis", type.StartsWith("percent_stacked", StringComparison.Ordinal));
      if (secondary)
      {
        element["secondary_value_axis"] = Axis("secondary_axis", false);
        element["secondary_category_axis"] = new JsonObject { ["is_deleted"] = true };
      }
      JsonObject categoryAxis = IsFalse(Get(properties, "category_axis.visible"))
        ? new JsonObject { ["is_deleted"] = true }
        : new JsonObject
        {
          ["labels_text_style"] = text.DeepClone(),
          ["label_position"] = IsFalse(Get(properties, "category_axis.labels")) ? "none" : "low"
        };
      element["primary_category_axis"] = categoryAxis;
      if (Text(Get(properties, "category_axis.format")) is string { Length: > 0 } dates)
      {
        element["category_axis_type"] = "timeline";
        categoryAxis["number_format"] = Code(dates);
        if (Text(Get(properties, "category_axis.step")) is string { Length: > 0 } step)
        {
          string[] parts = step.Split(' ');
          categoryAxis["units"] = Parse(parts[0]);
          categoryAxis["major_time_unit"] = parts[1].EndsWith('s') ? parts[1] : $"{parts[1]}s";
        }
      }
    }
    if (!pie && !line)
    {
      foreach (string key in new[] { "gap_width", "overlap" })
      {
        if (Number(Get(properties, key)) is double amount)
        {
          element[key] = Round(amount);
        }
      }
    }
    if (type == "doughnut")
    {
      element["doughnut_hole_size"] = 50;
    }
    return element;
  }

  /// <summary>A category or series name as a chart_data key spells it.</summary>
  private static string Label(JsonNode? node)
  {
    return Text(node) ?? node?.ToJsonString() ?? "";
  }

  #endregion

  #region Tables

  private static JsonObject? Border(JsonNode? properties, string side)
  {
    JsonNode? width = Get(properties, $"border_{side}.width");
    JsonNode? stroke = Get(properties, $"border_{side}.color");
    if (!Truthy(stroke) || !Truthy(width))
    {
      return null;
    }
    var result = new JsonObject { ["color"] = Color(stroke), ["weight"] = width!.DeepClone() };
    if (Text(Get(properties, $"border_{side}.dash")) is string dash and not "" and not "solid")
    {
      result["dash_type"] = dash;
    }
    return result;
  }

  // The tools ignore row sizes and spans on an add, so they come back in a second edit as each row's fix; cells
  // merged over come out empty. An empty cell has no run to carry a size, so the table's own text is the default.
  public (JsonObject Element, JsonArray Fix) Table(JsonNode item)
  {
    JsonNode? properties = Get(item, "properties");
    List<JsonNode?> widths = Items(Get(properties, "widths"));
    List<JsonNode?> heights = Items(Get(properties, "heights"));
    var rows = Array(heights.Select((_, index) => (JsonNode?)new JsonObject
    {
      ["index"] = index,
      ["cells"] = Array(widths.Select((_, column) => (JsonNode?)new JsonObject { ["index"] = column, ["text"] = "" }))
    }));
    var element = new JsonObject
    {
      ["position"] = Box(Get(properties, "frame"))["position"]!.DeepClone(),
      ["properties"] = new JsonObject
      {
        ["first_row"] = (Number(Get(properties, "header_rows")) ?? 0) > 0,
        ["band_row"] = false
      },
      ["side_effect_handling"] = "lazy",
      ["default_cell_style"] = new JsonObject
      {
        ["text_style"] = TextStyle(properties),
        ["text_layout"] = TextLayout(properties),
        ["borders"] = new JsonObject { ["all_borders"] = NoBorder() },
        ["fill"] = "transparent"
      },
      ["columns"] = Array(widths.Select((width, index) => (JsonNode?)new JsonObject
      {
        ["index"] = index,
        ["width"] = width?.DeepClone()
      })),
      ["rows"] = rows
    };
    JsonArray fix = Array(heights.Select((height, index) => (JsonNode?)(height is null
      ? new JsonObject { ["index"] = index, ["minimize_row_whitespace"] = true }
      : new JsonObject { ["index"] = index, ["height"] = height.DeepClone() })));
    foreach (JsonNode? cell in Items(Get(properties, "cells")))
    {
      int row = (int)Number(Get(cell, "row"))!;
      int column = (int)Number(Get(cell, "column"))!;
      JsonObject target = rows[row - 1]!["cells"]![column - 1]!.AsObject();
      target.Remove("text");
      target["text_component"] = TextComponent(cell!);
      var style = new JsonObject();
      if (Truthy(Get(Get(cell, "properties"), "fill")))
      {
        style["fill"] = Color(Get(Get(cell, "properties"), "fill"));
      }
      var borders = new JsonObject();
      foreach (string side in new[] { "top", "bottom", "left", "right" })
      {
        if (Border(Get(cell, "properties"), side) is JsonObject border)
        {
          borders[side] = border;
        }
      }
      if (borders.Count > 0)
      {
        style["borders"] = borders;
      }
      if (style.Count > 0)
      {
        target["style"] = style;
      }
      int spanRows = (int)(Number(Get(cell, "rows")) ?? 1);
      int spanColumns = (int)(Number(Get(cell, "columns")) ?? 1);
      if (spanRows > 1 || spanColumns > 1)
      {
        JsonObject fixRow = fix[row - 1]!.AsObject();
        if (fixRow["cells"] is not JsonArray spans)
        {
          spans = new JsonArray();
          fixRow["cells"] = spans;
        }
        var span = new JsonObject { ["index"] = column - 1 };
        if (spanRows > 1)
        {
          span["row_span"] = spanRows;
        }
        if (spanColumns > 1)
        {
          span["column_span"] = spanColumns;
        }
        spans.Add(span);
      }
    }
    Workaround("table rows: heights, minimize_row_whitespace, and spans are ignored on an add, so they take a second " +
      "edit");
    return (element, fix);
  }

  private static JsonObject NoBorder()
  {
    return new JsonObject { ["color"] = "transparent", ["weight"] = 0 };
  }

  #endregion

  #region Slides

  // Elements for one edit, in the order Pintura draws them, each at its slot when given one.
  private JsonObject Elements(IEnumerable<JsonNode> items, int start, Func<int, int>? slot = null)
  {
    var edit = new JsonObject();
    int z = start;
    int index = -1;
    foreach (JsonNode item in items)
    {
      index++;
      if (slot is not null)
      {
        z = slot(index);
      }
      string? kind = Text(Get(item, "kind"));
      JsonNode? properties = Get(item, "properties");
      string key;
      JsonObject element;
      if (kind == "text" || (kind == "shape" && Items(Get(item, "paragraphs")).Count > 0))
      {
        key = "text_elements_to_add";
        element = kind == "shape" ? Placed(item) : Box(Get(properties, "frame"));
        if (kind == "text" && Truthy(Get(properties, "rotate")))
        {
          element["position"]!["rotation"] = Get(properties, "rotate")!.DeepClone();
        }
        element["text_component"] = TextComponent(item);
        if (ShapeStyle(properties) is { Count: > 0 } style)
        {
          element["shape_style"] = style;
        }
        element["autofit"] = Autofit.GetValueOrDefault(Text(Get(properties, "fit")) is string { Length: > 0 } fit
          ? fit
          : "none");
        if (IsFalse(Get(properties, "wrap")))
        {
          element["wrap_text"] = false;
        }
        if ((Number(Get(properties, "columns")) is double columns and not 0 ? columns : 1) > 1)
        {
          gaps.Add("text columns aren't exported");
        }
      }
      else if (kind == "shape")
      {
        key = "shape_elements_to_add";
        element = Placed(item);
        element["shape_style"] = ShapeStyle(properties);
      }
      else if (kind == "line")
      {
        key = "shape_elements_to_add";
        element = Line(item);
      }
      else if (kind == "picture")
      {
        key = "picture_elements_to_add";
        element = Picture(item);
      }
      else if (kind == "chart")
      {
        key = "chart_elements_to_add";
        element = Chart(item);
      }
      else if (kind == "table")
      {
        key = "table_elements_to_add";
        (element, JsonArray fix) = Table(item);
        tableFixes[z] = fix;
      }
      else
      {
        gaps.Add($"{kind} elements aren't exported");
        continue;
      }
      element["position"]!["z_index"] = z++;
      if (edit[key] is not JsonArray list)
      {
        list = new JsonArray();
        edit[key] = list;
      }
      list.Add(element);
    }
    return edit;
  }

  // PowerPoint's body level 1 is a plain paragraph, which Pintura writes with no level; Pintura's bullet levels 1
  // to 3 are PowerPoint's 2 to 4.
  public static JsonObject MasterStyles(JsonNode theme)
  {
    JsonNode? title = Get(Items(Get(Get(theme, "title"), "paragraphs"))[0], "properties");
    JsonObject Flush(JsonObject style)
    {
      style["margin_left"] = 0;
      style["indent"] = 0;
      return style;
    }
    JsonNode? text = Get(theme, "text");
    var levels = new JsonArray(new JsonObject
    {
      ["level"] = 1,
      ["text_style"] = TextStyle(text),
      ["paragraph_style"] = Flush(ParagraphStyle(text)),
      ["bullet"] = new JsonObject { ["type"] = "none" }
    });
    double previous = 0;
    IEnumerable<KeyValuePair<string, JsonNode?>> numbered = Get(theme, "levels") is JsonObject map
      ? map.OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture))
      : [];
    foreach ((string number, JsonNode? level) in numbered)
    {
      JsonObject merged = text?.DeepClone().AsObject() ?? new JsonObject();
      if (Get(level, "properties") is JsonObject own)
      {
        foreach ((string key, JsonNode? value) in own)
        {
          merged[key] = value?.DeepClone();
        }
      }
      double indent = Number(Get(level, "indent")) ?? 0;
      double hanging = Number(Get(level, "hanging")) ?? Math.Max(0, indent - previous);
      JsonObject paragraph = ParagraphStyle(merged);
      paragraph["margin_left"] = indent;
      paragraph["indent"] = Negate(hanging);
      levels.Add(new JsonObject
      {
        ["level"] = int.Parse(number, CultureInfo.InvariantCulture) + 1,
        ["text_style"] = TextStyle(merged),
        ["paragraph_style"] = paragraph,
        ["bullet"] = Bullet(Text(Get(level, "bullet")) == "none" ? null : level)
      });
      previous = indent;
    }
    return new JsonObject
    {
      ["title"] = new JsonArray(new JsonObject
      {
        ["level"] = 1,
        ["text_style"] = TextStyle(title),
        ["paragraph_style"] = Flush(ParagraphStyle(title)),
        ["bullet"] = new JsonObject { ["type"] = "none" }
      }),
      ["body"] = levels,
      ["other"] = new JsonArray(levels[0]!.DeepClone())
    };
  }

  // The tools write a slide-number field only into placeholders, so a box holding nothing but ${slide} becomes one.
  private static bool IsPageNumber(JsonNode item)
  {
    List<JsonNode?> runs = Text(Get(item, "kind")) == "text"
      ? Items(Get(item, "paragraphs")).SelectMany(paragraph => Items(Get(paragraph, "runs"))).ToList()
      : [];
    return runs.Count > 0 && runs.All(model => Text(Get(model, "field")) == "slide");
  }

  private static JsonObject Placeholder(JsonNode item, string role, JsonNode? masterBase, string prompt)
  {
    JsonNode? properties = Get(item, "properties");
    JsonNode? paragraph = Get(Items(Get(item, "paragraphs"))[0], "properties");
    JsonObject entry = Box(Get(properties, "frame"));
    var placeholder = new JsonObject
    {
      ["role"] = role,
      ["position"] = entry["position"]!.DeepClone(),
      ["size"] = entry["size"]!.DeepClone(),
      ["autofit"] = Autofit.GetValueOrDefault(Text(Get(properties, "fit")) is string { Length: > 0 } fit ? fit : "none"),
      ["inheritable_styles"] = new JsonArray(new JsonObject
      {
        ["level"] = 1,
        ["text_style"] = TextStyle(paragraph, masterBase),
        ["paragraph_style"] = ParagraphStyle(paragraph, null, masterBase),
        ["bullet"] = new JsonObject { ["type"] = "none" }
      }),
      ["prompt"] = new JsonObject
      {
        ["text_layout"] = TextLayout(properties),
        ["paragraph_list"] = new JsonArray(new JsonObject
        {
          ["index"] = 0,
          ["runs"] = new JsonArray(role == "slide_number"
            ? new JsonObject { ["index"] = 0, ["field"] = "slidenum" }
            : new JsonObject { ["index"] = 0, ["text"] = prompt })
        })
      }
    };
    return placeholder;
  }

  private static JsonObject With(JsonObject address, string key, JsonNode value)
  {
    JsonObject input = address.DeepClone().AsObject();
    input[key] = value;
    return input;
  }

  // The tools build each new paragraph by cloning the one before it, runs and all, and refuses runs_to_delete in the
  // same add, so the clones' leftover runs come off in a second edit.
  private void Unclone(JsonObject address, JsonObject edit)
  {
    var extras = new Dictionary<int, JsonArray>();
    foreach (JsonNode? element in Items(edit["text_elements_to_add"]))
    {
      int had = 0;
      var deletes = new JsonArray();
      foreach (JsonNode? paragraph in Items(element!["text_component"]!["paragraph_list"]))
      {
        int count = paragraph!["runs"]!.AsArray().Count;
        if (had > count)
        {
          deletes.Add(new JsonObject
          {
            ["index"] = paragraph["index"]!.DeepClone(),
            ["runs_to_delete"] = Array(Enumerable.Range(count, had - count).Select(run => (JsonNode?)run))
          });
        }
        had = Math.Max(had, count);
      }
      if (deletes.Count > 0)
      {
        extras[(int)element["position"]!["z_index"]!] = deletes;
      }
    }
    if (extras.Count == 0)
    {
      return;
    }
    Workaround("new paragraphs clone the previous paragraph's runs, and runs_to_delete in the same add drops the " +
      "element, so leftover runs take a second edit");
    JsonNode? read = Call("read_slide", address.DeepClone().AsObject());
    JsonArray fixes = Array(Items(Get(read, "text_elements"))
      .Where(element => Get(element, "id") is not null &&
        extras.ContainsKey((int)(Number(Get(Get(element, "position"), "z_index")) ?? -1)))
      .Select(element => (JsonNode?)new JsonObject
      {
        ["id"] = element!["id"]!.DeepClone(),
        ["text_component"] = new JsonObject
        {
          ["paragraph_list"] = extras[(int)Number(element["position"]!["z_index"])!].DeepClone()
        }
      }));
    if (fixes.Count > 0)
    {
      Call("edit_slide", With(address, "slide", new JsonObject { ["text_elements"] = fixes }));
    }
  }

  // Tables are found again by their z-index, the only thing an add lets the replay choose.
  private void FixTables(JsonObject address)
  {
    if (tableFixes.Count == 0)
    {
      return;
    }
    JsonNode? read = Call("read_slide", address.DeepClone().AsObject());
    JsonArray fixes = Array(Items(Get(read, "table_elements"))
      .Where(element => tableFixes.ContainsKey((int)(Number(Get(Get(element, "position"), "z_index")) ?? -1)))
      .Select(element => (JsonNode?)new JsonObject
      {
        ["id"] = element!["id"]?.DeepClone(),
        ["side_effect_handling"] = "lazy",
        ["rows"] = tableFixes[(int)Number(element["position"]!["z_index"])!].DeepClone()
      }));
    tableFixes.Clear();
    if (fixes.Count > 0)
    {
      Call("edit_slide", With(address, "slide", new JsonObject { ["table_elements"] = fixes }));
    }
  }

  /// <summary>A slide's words for a placeholder the layout defines, moved where the slide puts it when that isn't the
  /// layout's frame.</summary>
  public static JsonObject PlaceholderFill(JsonNode id, JsonNode item, JsonNode? placeholder)
  {
    var fill = new JsonObject
    {
      ["id"] = id.DeepClone(),
      ["text_component"] = TextComponent(item, Get(Items(Get(placeholder, "paragraphs"))[0], "properties"))
    };
    JsonNode? frame = Get(Get(item, "properties"), "frame");
    if (frame is not null && !Same(frame, Get(Get(placeholder, "properties"), "frame")))
    {
      foreach ((string key, JsonNode? value) in Box(frame))
      {
        fill[key] = value!.DeepClone();
      }
    }
    return fill;
  }

  private static bool Near(JsonNode? position, JsonNode? frame)
  {
    return Math.Abs((Number(Get(position, "left")) ?? 0) - (Number(frame?[0]) ?? 0)) < 0.5 &&
      Math.Abs((Number(Get(position, "top")) ?? 0) - (Number(frame?[1]) ?? 0)) < 0.5;
  }

  private static JsonNode WithSection(JsonNode item, JsonNode slide)
  {
    JsonObject copy = item.DeepClone().AsObject();
    foreach (JsonNode? run in Items(copy["paragraphs"]).SelectMany(paragraph => Items(Get(paragraph, "runs"))))
    {
      if (Text(Get(run, "field")) == "section")
      {
        run!["text"] = Text(Get(slide, "section")) ?? "";
      }
    }
    return copy;
  }

  private record Defined(JsonNode? Title, List<JsonNode?> Placeholders, bool Pages);

  /// <summary>What the slides need from the theme and layouts, worked out from the model without calls, so a slide
  /// can be written into a deck whose design an earlier export wrote.</summary>
  private record Design(List<JsonNode?> Layouts, Dictionary<string, Defined> Defined);

  private Design Prepare(JsonObject deck)
  {
    this.deck = deck;
    JsonNode theme = deck["theme"]!;
    Formats = Get(theme, "formats") as JsonObject ?? new JsonObject();
    List<JsonNode?> layouts = Items(deck["layouts"]);
    JsonNode? themeTitle = Get(theme, "title");
    var defined = new Dictionary<string, Defined>(StringComparer.Ordinal);
    foreach (JsonNode? layout in layouts)
    {
      JsonNode? title = Truthy(Get(layout, "titleRemoved"))
        ? null
        : Truthy(Get(layout, "title")) ? Get(layout, "title") : themeTitle;
      defined[Text(Get(layout, "id"))!] = new Defined(title, Items(Get(layout, "placeholders")),
        DrawnByLayout(layout).Any(IsPageNumber));
    }
    return new Design(layouts, defined);
  }

  /// <summary>Writes the whole deck into the blank one.</summary>
  public void Translate(JsonObject deck)
  {
    Design design = Prepare(deck);
    mastersWritten = 1;
    layoutsWritten = design.Layouts.Count;
    JsonNode theme = deck["theme"]!;
    string themeName = Text(Call("list_slide_layouts", new JsonObject())![0]!["theme_name"])!;
    // Office's layouts would collide with Pintura's names, so all but the first slide's go before any are made.
    List<JsonNode?> office = Items(Call("list_slide_layouts", new JsonObject())![0]!["layouts"]);
    string kept = Text(office.First(layout => Items(Get(layout, "slides")).Count > 0)!["name"])!;
    foreach (JsonNode? layout in office.Where(candidate => Text(Get(candidate, "name")) != kept))
    {
      Call("delete_layout", new JsonObject
      {
        ["layout_name"] = Get(layout, "name")!.DeepClone(),
        ["theme_name"] = themeName
      });
    }

    var colors = new JsonObject();
    foreach ((string name, JsonNode? hex) in Get(theme, "colors") as JsonObject ?? new JsonObject())
    {
      colors[name == "followed_hyperlink" ? "folHlink" : Scheme.GetValueOrDefault(name, name)] = hex?.DeepClone();
    }
    Call("edit_theme", new JsonObject
    {
      ["theme_name"] = themeName,
      ["colors"] = colors,
      ["major_font"] = Get(Get(theme, "fonts"), "heading")?.DeepClone(),
      ["minor_font"] = Get(Get(theme, "fonts"), "body")?.DeepClone()
    });

    JsonObject styles = MasterStyles(theme);
    JsonNode? master = Call("read_slide", new JsonObject { ["theme_name"] = themeName, ["target"] = "master" });
    JsonNode? masterTitle = Items(Get(master, "text_elements"))
      .FirstOrDefault(element => Text(Get(Get(element, "placeholder"), "role")) == "title");
    string named = Text(Get(theme, "name")) is { Length: > 0 } own ? own : themeName;
    var masterEdit = new JsonObject
    {
      ["name"] = named,
      ["background"] = new JsonObject { ["color"] = Color(Get(theme, "background")) },
      ["master_text_styles"] = styles
    };
    JsonNode? themeTitle = Get(theme, "title");
    if (masterTitle is not null && themeTitle is not null)
    {
      JsonObject placed = Box(Get(Get(themeTitle, "properties"), "frame"));
      masterEdit["placeholders"] = new JsonArray(new JsonObject
      {
        ["id"] = masterTitle["id"]!.DeepClone(),
        ["position"] = placed["position"]!.DeepClone(),
        ["size"] = placed["size"]!.DeepClone(),
        ["prompt"] = new JsonObject { ["text_layout"] = TextLayout(Get(themeTitle, "properties")) }
      });
    }
    Call("edit_slide", new JsonObject
    {
      ["theme_name"] = themeName,
      ["target"] = "master",
      ["slide"] = masterEdit
    });
    themeName = named;

    JsonNode? masterBody = Get(theme, "text");
    JsonNode? masterTitleStyle = themeTitle is null ? null : Get(Items(Get(themeTitle, "paragraphs"))[0], "properties");
    foreach (JsonNode? layout in design.Layouts)
    {
      string name = Text(Get(layout, "name"))!;
      Defined definition = design.Defined[Text(Get(layout, "id"))!];
      Call("create_layout", new JsonObject { ["new_layout_name"] = name });
      var edit = new JsonObject();
      if (Truthy(Get(layout, "background")))
      {
        edit["background"] = new JsonObject { ["color"] = Color(Get(layout, "background")) };
      }
      var adds = new JsonArray();
      if (definition.Title is not null)
      {
        adds.Add(Placeholder(definition.Title, "title", masterTitleStyle, "Title"));
      }
      foreach (JsonNode? item in definition.Placeholders)
      {
        adds.Add(Placeholder(item!, "body", masterBody, Text(Get(item, "id")) ?? ""));
      }
      List<JsonNode> drawn = DrawnByLayout(layout);
      List<JsonNode> pages = drawn.Where(IsPageNumber).ToList();
      foreach (JsonNode item in pages)
      {
        adds.Add(Placeholder(item, "slide_number", null, ""));
      }
      if (adds.Count > 0)
      {
        edit["placeholders_to_add"] = adds;
      }
      if (pages.Count > 0)
      {
        edit["header_footer"] = new JsonObject { ["slide_number"] = true };
      }
      foreach ((string key, JsonNode? value) in Elements(drawn.Where(item => !IsPageNumber(item)), adds.Count)
        .ToList())
      {
        edit[key] = value?.DeepClone();
      }
      var address = new JsonObject { ["layout_name"] = name, ["target"] = "layout" };
      Call("edit_slide", With(address, "slide", edit));
      Unclone(address, edit);
      FixTables(address);
    }
    DesignGaps = gaps.ToList();

    int count = Call("list_slides", new JsonObject()) is JsonArray existing ? existing.Count : 1;
    foreach (JsonNode? slide in Items(deck["slides"]))
    {
      if (Loaded(slide!))
      {
        count += 1;
        Slide(design, slide!, count, false);
      }
    }

    Call("delete_slides", new JsonObject { ["slide_indices"] = new JsonArray(1) });
    Call("delete_layout", new JsonObject { ["layout_name"] = kept, ["theme_name"] = themeName });
    Sections(deck, false);
  }

  /// <summary>Brings a deck an earlier export wrote, with the same design, up to <paramref name="deck"/>: slides
  /// whose hash is unchanged stay and move into place, the rest are written afresh where they go, and what's left
  /// over is deleted. <paramref name="previous"/> lists the deck's slides in order, with their hashes and gaps, and
  /// <paramref name="kept"/> names those to leave as they were, whatever their hash.
  /// </summary>
  public void Update(JsonObject deck, IReadOnlyList<ExportedSlide> previous, IReadOnlyList<string> designGaps,
    IReadOnlySet<string>? kept = null)
  {
    Design design = Prepare(deck);
    gaps.UnionWith(designGaps);
    DesignGaps = gaps.ToList();
    List<JsonNode> slides = Items(deck["slides"]).OfType<JsonNode>().Where(Loaded).ToList();
    Dictionary<string, string> hashes = slides.ToDictionary(slide => Text(Get(slide, "id"))!,
      slide => ExportManifest.SlideHash(slide, Root), StringComparer.Ordinal);
    foreach (ExportedSlide slide in previous.Where(slide => kept?.Contains(slide.Id) == true))
    {
      hashes[slide.Id] = slide.Hash;
    }
    // The deck's slides as they stand, by id, with a null for one that changed or went.
    List<string?> current = previous
      .Select(slide => hashes.GetValueOrDefault(slide.Id) == slide.Hash ? slide.Id : null)
      .ToList();
    // Stale slides go first, unless every one is, since a deck needs a slide; then they go last, from the end.
    if (current.Contains(null) && current.Any(id => id is not null))
    {
      Call("delete_slides", new JsonObject
      {
        ["slide_indices"] = Array(current.Select((id, index) => (id, index)).Where(slide => slide.id is null)
          .Select(slide => (JsonNode?)(slide.index + 1)))
      });
      current.RemoveAll(id => id is null);
    }
    for (int position = 0; position < slides.Count; position++)
    {
      string id = Text(Get(slides[position], "id"))!;
      int at = current.IndexOf(id);
      if (at < 0)
      {
        current.Insert(position, id);
        Slide(design, slides[position], position + 1, position + 1 < current.Count);
        continue;
      }
      Reused += 1;
      ExportedSlide earlier = previous.First(slide => slide.Id == id);
      SlideGaps[id] = earlier.Gaps;
      gaps.UnionWith(earlier.Gaps);
      if (at != position)
      {
        Call("move_slide", new JsonObject { ["from_index"] = at + 1, ["to_index"] = position + 1 });
        current.RemoveAt(at);
        current.Insert(position, id);
      }
    }
    if (current.Count > slides.Count)
    {
      Call("delete_slides", new JsonObject
      {
        ["slide_indices"] = Array(Enumerable.Range(slides.Count + 1, current.Count - slides.Count)
          .Select(index => (JsonNode?)index))
      });
    }
    Sections(deck, true);
  }

  /// <summary>Gives the deck the presentation's sections, naming the slides before the first one as PowerPoint
  /// does, or with <paramref name="clear"/>, removes any an earlier export wrote from a presentation that has none.
  /// </summary>
  private void Sections(JsonObject deck, bool clear)
  {
    List<string?> names = Items(deck["slides"]).OfType<JsonNode>()
      .Where(slide => Text(Get(slide, "error")) is null)
      .Select(slide => Text(Get(slide, "section")))
      .ToList();
    bool named = names.Any(name => name is not null);
    if (!named && !clear)
    {
      return;
    }
    var sections = new JsonArray();
    for (int index = 0; named && index < names.Count; index++)
    {
      if (index == 0 || names[index] != names[index - 1])
      {
        sections.Add(new JsonObject { ["name"] = names[index] ?? DefaultSection, ["first_slide"] = index + 1 });
      }
    }
    try
    {
      Call("edit_sections", new JsonObject { ["sections"] = sections });
    }
    catch (InvalidOperationException error) when (error.Message.Contains("Unknown tool"))
    {
      Workaround("sections: editide before 1.0.7 has no edit_sections");
      if (named)
      {
        gaps.Add("sections aren't exported, as this editide has no edit_sections");
      }
    }
  }

  /// <summary>Whether a slide loaded, keeping why not as a gap when it didn't.</summary>
  private bool Loaded(JsonNode slide)
  {
    if (Text(Get(slide, "error")) is string error)
    {
      gaps.Add($"slide {Text(Get(slide, "id"))} failed to load: {error}");
      return false;
    }
    return true;
  }

  /// <summary>Adds <paramref name="slide"/> at <paramref name="index"/>, inserting it before the slide there when
  /// <paramref name="insert"/>, and fills it, keeping the gaps it raises as its own.</summary>
  private void Slide(Design design, JsonNode slide, int index, bool insert)
  {
    written += 1;
    SortedSet<string> deckGaps = gaps;
    gaps = new SortedSet<string>(StringComparer.Ordinal);
    try
    {
      JsonNode layout = design.Layouts.First(candidate => Text(Get(candidate, "id")) == Text(Get(slide, "layout")))!;
      var add = new JsonObject { ["layout_name"] = Get(layout, "name")!.DeepClone() };
      if (insert)
      {
        add["slide_index"] = index;
      }
      Call("add_slide", add);
      JsonNode? read = Call("read_slide", new JsonObject { ["slide_index"] = index });
      List<JsonNode?> holders = Items(Get(read, "text_elements"))
        .Where(element => Truthy(Get(element, "is_placeholder"))).ToList();
      Defined definition = design.Defined[Text(Get(slide, "layout"))!];
      var fills = new JsonArray();
      var used = new HashSet<string>(StringComparer.Ordinal);
      var own = new List<JsonNode>();
      // Placeholders drawn over some of the slide's elements, with how many, which the edit moves up among them.
      var raised = new List<(JsonObject Fill, int Over)>();
      foreach (JsonNode? item in Items(Get(slide, "items")))
      {
        string? role = Text(Get(item, "role"));
        if (string.IsNullOrEmpty(role))
        {
          own.Add(item!);
          continue;
        }
        JsonNode? placeholder = role == "title"
          ? definition.Title
          : definition.Placeholders.FirstOrDefault(candidate => Text(Get(candidate, "id")) == Text(Get(item, "id")));
        JsonNode? frame = Get(Get(placeholder, "properties"), "frame");
        JsonNode? target = holders.FirstOrDefault(element => !used.Contains(Get(element, "id")!.ToJsonString()) &&
          (role == "title"
            ? Text(Get(Get(element, "placeholder"), "role")) == "title"
            : Near(Get(element, "position"), frame)));
        if (target is null)
        {
          gaps.Add($"slide {Text(Get(slide, "id"))}: no placeholder on the slide for {Text(Get(item, "id"))}");
          continue;
        }
        used.Add(Get(target, "id")!.ToJsonString());
        JsonObject fill = PlaceholderFill(Get(target, "id")!, item!, placeholder);
        if (own.Count > 0)
        {
          raised.Add((fill, own.Count));
        }
        fills.Add(fill);
      }
      var edit = new JsonObject();
      if (Truthy(Get(slide, "background")))
      {
        edit["background"] = new JsonObject { ["color"] = Color(Get(slide, "background")) };
      }
      if (fills.Count > 0)
      {
        edit["text_elements"] = fills;
      }
      JsonArray unused = Array(holders
        .Where(element => !used.Contains(Get(element, "id")!.ToJsonString()) &&
          Text(Get(Get(element, "placeholder"), "role")) != "slide_number")
        .Select(element => Get(element, "id")!.DeepClone()));
      HashSet<string> hide = Items(Get(slide, "hide")).Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
      List<JsonNode> drawnByLayout = DrawnByLayout(layout);
      if (definition.Pages)
      {
        bool pageHidden = drawnByLayout.Any(item => IsPageNumber(item) && hide.Contains(Text(Get(item, "id")) ?? ""));
        edit["header_footer"] = new JsonObject { ["slide_number"] = !pageHidden };
      }
      foreach (string id in Items(Get(slide, "hide")).Select(Text).OfType<string>()
        .Where(id => drawnByLayout.Any(item => Text(Get(item, "id")) == id && !IsPageNumber(item))))
      {
        gaps.Add($"slide {Text(Get(slide, "id"))} hides {id}, which PowerPoint shows, as a slide can't leave out one " +
          "of its layout's shapes");
      }
      if (unused.Count > 0)
      {
        edit["elements_to_delete"] = unused;
      }
      List<JsonNode> sections = Items(Get(layout, "elements")).OfType<JsonNode>()
        .Where(item => ShowsSection(item) && !hide.Contains(Text(Get(item, "id")) ?? ""))
        .Select(item => WithSection(item, slide))
        .ToList();
      int start = used.Count - raised.Count + sections.Count;
      for (int at = 0; at < raised.Count; at++)
      {
        (JsonObject fill, int over) = raised[at];
        (fill["position"] ??= new JsonObject())["z_index"] = start + over + at;
      }
      int Slot(int index)
      {
        if (index < sections.Count)
        {
          return used.Count - raised.Count + index;
        }
        int element = index - sections.Count;
        return start + element + raised.Count(entry => entry.Over <= element);
      }
      foreach ((string key, JsonNode? value) in Elements(sections.Concat(own), used.Count, Slot).ToList())
      {
        edit[key] = value?.DeepClone();
      }
      var address = new JsonObject { ["slide_index"] = index };
      Call("edit_slide", With(address, "slide", edit));
      Unclone(address, edit);
      FixTables(address);
    }
    finally
    {
      SlideGaps[Text(Get(slide, "id"))!] = gaps.ToList();
      deckGaps.UnionWith(gaps);
      gaps = deckGaps;
    }
  }

  /// <summary>A layout's elements but those showing the section, which differs from slide to slide, so each slide
  /// draws its own.</summary>
  private static List<JsonNode> DrawnByLayout(JsonNode? layout)
  {
    return Items(Get(layout, "elements")).OfType<JsonNode>().Where(item => !ShowsSection(item)).ToList();
  }

  private static bool ShowsSection(JsonNode item)
  {
    return Items(Get(item, "paragraphs"))
      .Any(paragraph => Items(Get(paragraph, "runs")).Any(model => Text(Get(model, "field")) == "section"));
  }

  #endregion

  private sealed class CountingTools : ITools
  {
    private readonly ITools inner;
    private readonly Action<int> called;

    public CountingTools(ITools inner, Action<int> called)
    {
      this.inner = inner;
      this.called = called;
    }

    public int Calls { get; private set; }

    public JsonNode? Call(string tool, JsonObject input)
    {
      JsonNode? answer = inner.Call(tool, input);
      Calls++;
      called(Calls);
      return answer;
    }


  }

  [GeneratedRegex(@"^\d+\.$")]
  private static partial Regex Numbered();

  [GeneratedRegex(@"Y+|M+|D+|[^YMD]")]
  private static partial Regex DateTokens();

  [GeneratedRegex(@"viewBox=""\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)")]
  private static partial Regex ViewBox();

  [GeneratedRegex(@"\bwidth=""([\d.]+)")]
  private static partial Regex SvgWidth();

  [GeneratedRegex(@"\bheight=""([\d.]+)")]
  private static partial Regex SvgHeight();


}