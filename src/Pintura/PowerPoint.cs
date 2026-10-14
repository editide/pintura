using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pintura;

/// <summary>What a mapping of the tools' reads made: the model's JSON, with pictures relative to
/// <see cref="Media"/>, and everything in the deck it had no place for.</summary>
public record PowerPointDeck(JsonObject Deck, string Media, List<string> Findings);

/// <summary>editide's reads of a <c>.pptx</c> as the model's JSON, in effective values, for the importer to write.
///
/// The read tools compress their reads: a run's style is the slide's default, then the element's, the paragraph's,
/// and the run's own, merged. The mapping expands every level, and leaves which of the values a file states to the
/// importer. Values PowerPoint and Pintura both default, such as bold off or no space before, are left out.</summary>
public partial class PowerPointMapping
{
  private static readonly Dictionary<string, string> Scheme = new Dictionary<string, string>
  {
    ["dk1"] = "dark1", ["lt1"] = "light1", ["dk2"] = "dark2", ["lt2"] = "light2", ["tx1"] = "dark1",
    ["bg1"] = "light1", ["tx2"] = "dark2", ["bg2"] = "light2", ["accent1"] = "accent1", ["accent2"] = "accent2",
    ["accent3"] = "accent3", ["accent4"] = "accent4", ["accent5"] = "accent5", ["accent6"] = "accent6",
    ["hlink"] = "hyperlink", ["folHlink"] = "followed_hyperlink"
  };
  private static readonly string[] ThemeColors =
    ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
  private static readonly Dictionary<string, (string Shape, string? Direction)> Shapes =
    new Dictionary<string, (string, string?)>
    {
      ["rectangle"] = ("rectangle", null), ["round_rectangle"] = ("rounded_rectangle", null),
      ["ellipse"] = ("ellipse", null), ["triangle"] = ("triangle", null),
      ["right_triangle"] = ("right_triangle", null), ["diamond"] = ("diamond", null),
      ["parallelogram"] = ("parallelogram", null), ["trapezoid"] = ("trapezoid", null),
      ["pentagon"] = ("pentagon", null), ["hexagon"] = ("hexagon", null), ["chevron"] = ("chevron", null),
      ["home_plate"] = ("home_plate", null), ["right_arrow"] = ("arrow", "right"), ["left_arrow"] = ("arrow", "left"),
      ["up_arrow"] = ("arrow", "up"), ["down_arrow"] = ("arrow", "down"), ["left_brace"] = ("brace", "left"),
      ["right_brace"] = ("brace", "right"), ["left_bracket"] = ("bracket", "left"),
      ["right_bracket"] = ("bracket", "right"), ["wedge_rectangle_callout"] = ("callout", null)
    };
  private static readonly HashSet<string> Lines = ["line", "straight_connector1"];
  private static readonly HashSet<string> MarkerShapes = ["circle", "square", "diamond", "triangle"];
  private static readonly HashSet<string> ChartTypes =
  [
    "clustered_column", "stacked_column", "percent_stacked_column", "clustered_bar", "stacked_bar",
    "percent_stacked_bar", "line", "stacked_line", "percent_stacked_line", "pie", "doughnut"
  ];
  private static readonly Dictionary<string, string> LabelPositions = new Dictionary<string, string>
  {
    ["outside_end"] = "outside", ["center"] = "center", ["inside_end"] = "inside", ["inside_base"] = "inside_base",
    ["top"] = "above", ["above"] = "above", ["below"] = "below", ["bottom"] = "below"
  };
  private static readonly string[] Accents = ["accent1", "accent2", "accent3", "accent4", "accent5", "accent6"];

  private readonly JsonObject reads;
  private readonly string name;
  private readonly Package package;
  private readonly List<string> findings = new List<string>();
  private readonly Dictionary<string, string> hex = new Dictionary<string, string>();
  private readonly string headingFont;
  private readonly string bodyFont;
  private readonly OrderedDictionary<string, JsonObject> formats = new OrderedDictionary<string, JsonObject>();
  private readonly HashSet<string> chartStems = new HashSet<string>();
  private readonly HashSet<string> tableStems = new HashSet<string>();
  private JsonObject text = new JsonObject();
  private readonly Dictionary<int, JsonObject> levels = new Dictionary<int, JsonObject>();
  private List<JsonObject> themeElements = new List<JsonObject>();

  private PowerPointMapping(JsonObject reads, string name, Package package)
  {
    this.reads = reads;
    this.name = name;
    this.package = package;
    JsonObject master = Master;
    foreach ((string key, JsonNode? value) in master["theme_colors"]?.AsObject() ?? new JsonObject())
    {
      hex[key] = ((string)value!).ToUpperInvariant();
    }
    headingFont = (string?)master["theme_fonts"]?["major"] ?? "Calibri Light";
    bodyFont = (string?)master["theme_fonts"]?["minor"] ?? "Calibri";
  }

  /// <summary>Maps <paramref name="reads"/>, as <see cref="DeckReads.Read"/> returns them, of the deck at
  /// <paramref name="deck"/>, whose package the pictures are copied out of into <paramref name="media"/>.</summary>
  public static PowerPointDeck Map(JsonObject reads, string deck, string media)
  {
    using var package = new Package(deck, media);
    var mapping = new PowerPointMapping(reads, Path.GetFileNameWithoutExtension(deck), package);
    mapping.findings.AddRange(reads["findings"]?.AsArray().Select(finding => (string)finding!) ?? []);
    JsonObject result = mapping.Build();
    return new PowerPointDeck(result, media, mapping.findings.Distinct().ToList());
  }

  private JsonObject Master => reads["master"]!.AsObject();

  private IEnumerable<JsonObject> LayoutReads => reads["layouts"]!.AsArray().Select(node => node!.AsObject());

  private IEnumerable<JsonObject> SlideReads => reads["slides"]!.AsArray().Select(node => node!.AsObject());

  private JsonObject Build()
  {
    Base();
    JsonObject theme = Theme();
    List<LayoutDraft> layouts = Layouts();
    Hoist(theme, layouts);
    JsonArray slides = Slides(layouts);
    theme["formats"] = new JsonObject(formats.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
    return new JsonObject
    {
      ["name"] = name,
      ["size"] = new JsonArray(Round((double)Master["width"]!), Round((double)Master["height"]!)),
      ["theme"] = theme,
      ["layouts"] = new JsonArray(layouts.Select(layout => (JsonNode)layout.Json).ToArray()),
      ["slides"] = slides
    };
  }

  #region Reading the tools' values

  private sealed record Layers(JsonNode? Text, JsonNode? Paragraph, JsonNode? Bullet);

  private sealed record Run(string Text, string? Field, string? Link, JsonObject Properties);

  /// <summary>A paragraph expanded: its bullet level from 1, or 0 for none, its bullet's keys, and its runs.</summary>
  private sealed record Paragraph(int Level, int Depth, string? Bullet, bool Numbered, double Margin, double Hanging,
    JsonObject? BulletColor, double? BulletSize, JsonObject Properties, List<Run> Runs)
  {
    public int Weight => Math.Max(1, Runs.Sum(run => run.Text.Length));
  }

  private static double Round(double value)
  {
    return Math.Round(value, 2);
  }

  /// <summary>Layers merged key by key, a color replacing the one below it whole, since a theme color without a step
  /// leaves out the step it replaces.</summary>
  private static JsonObject Merge(params JsonNode?[] layers)
  {
    var result = new JsonObject();
    foreach (JsonObject layer in layers.OfType<JsonObject>())
    {
      foreach ((string key, JsonNode? value) in layer)
      {
        result[key] = value is JsonObject nested && result[key] is JsonObject existing && !IsColor(nested)
          ? Merge(existing, nested)
          : value?.DeepClone();
      }
    }
    return result;
  }

  private static bool IsColor(JsonObject value)
  {
    return value.ContainsKey("scheme") || value.ContainsKey("rgb");
  }

  /// <summary>The value most of <paramref name="entries"/> share, each weighed by its weight, ties going to the first
  /// seen; null when none has a value.</summary>
  private static JsonNode? Mode(IEnumerable<(JsonNode? Value, double Weight)> entries)
  {
    var weights = new Dictionary<string, (double Weight, int First, JsonNode Value)>();
    int index = 0;
    foreach ((JsonNode? value, double weight) in entries)
    {
      if (value is null)
      {
        continue;
      }
      string key = value.ToJsonString();
      weights[key] = weights.TryGetValue(key, out var seen)
        ? (seen.Weight + weight, seen.First, seen.Value)
        : (weight, index++, value);
    }
    return weights.Count == 0
      ? null
      : weights.Values.OrderByDescending(entry => entry.Weight).ThenBy(entry => entry.First).First().Value.DeepClone();
  }

  /// <summary>A color the tools read as the model's: a theme color with its step, a hex, or <c>none</c>.</summary>
  private JsonObject? Color(JsonNode? value, string where)
  {
    switch (value)
    {
      case null:
        return null;
      case JsonValue plain when (string?)plain is "transparent":
        return new JsonObject { ["none"] = true };
      case JsonValue plain when (string?)plain is string literal:
        return new JsonObject { ["hex"] = literal.ToUpperInvariant() };
    }
    JsonObject color = value.AsObject();
    double? mod = (double?)color["luminance_mod"];
    double? off = (double?)color["luminance_off"];
    double? tint = (double?)color["tint"];
    string? themed = (string?)color["scheme"] is string scheme ? Scheme.GetValueOrDefault(scheme) : null;
    if (themed is not null && mod is null && off is null && tint is null)
    {
      return new JsonObject { ["theme"] = themed };
    }
    if (themed is not null && tint is null && mod is not null && off is not null && Math.Abs(mod.Value + off.Value -
      100) < 0.5 && Math.Abs(off.Value - Math.Round(off.Value)) < 0.01)
    {
      return new JsonObject { ["theme"] = themed, ["lighter"] = (int)Math.Round(off.Value) };
    }
    if (themed is not null && tint is null && mod is not null && off is null &&
      Math.Abs(mod.Value - Math.Round(mod.Value)) < 0.01)
    {
      return new JsonObject { ["theme"] = themed, ["darker"] = (int)Math.Round(100 - mod.Value) };
    }
    string? baseHex = (string?)color["rgb"] ?? ((string?)color["scheme"] is string key ? hex.GetValueOrDefault(key)
      : null);
    if (baseHex is null)
    {
      findings.Add($"{where}: color {color.ToJsonString()} has no mapping");
      return null;
    }
    if (mod is null && off is null && tint is null)
    {
      return new JsonObject { ["hex"] = baseHex.ToUpperInvariant() };
    }
    if (themed is not null)
    {
      findings.Add($"{where}: {themed} {(tint is null ? $"at luminance {mod ?? 100}/{off ?? 0}" : $"at a {tint}% tint")}" +
        " has no step in the format, so it is fixed as hex");
    }
    (double h, double s, double l) = Hsl(baseHex);
    if (tint is not null)
    {
      l += (1 - l) * (1 - tint.Value / 100);
    }
    l = Math.Clamp(l * (mod ?? 100) / 100 + (off ?? 0) / 100, 0, 1);
    return new JsonObject { ["hex"] = Hex(h, s, l) };
  }

  private static (double, double, double) Hsl(string hex)
  {
    double[] rgb = [.. new[] { 1, 3, 5 }.Select(start => int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber) / 255.0)];
    double max = rgb.Max();
    double min = rgb.Min();
    double l = (max + min) / 2;
    if (max == min)
    {
      return (0, 0, l);
    }
    double d = max - min;
    double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    double h = max == rgb[0] ? (rgb[1] - rgb[2]) / d + (rgb[1] < rgb[2] ? 6 : 0)
      : max == rgb[1] ? (rgb[2] - rgb[0]) / d + 2
      : (rgb[0] - rgb[1]) / d + 4;
    return (h / 6, s, l);
  }

  private static string Hex(double h, double s, double l)
  {
    double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    double p = 2 * l - q;
    double Channel(double t)
    {
      t = (t + 1) % 1;
      return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
    }
    double[] channels = s == 0 ? [l, l, l] : [Channel(h + 1.0 / 3), Channel(h), Channel(h - 1.0 / 3)];
    return "#" + string.Concat(channels.Select(value => ((int)Math.Round(value * 255)).ToString("X2")));
  }

  private JsonObject? Font(string? family)
  {
    if (family is null)
    {
      return null;
    }
    if (family.StartsWith("+mj", StringComparison.Ordinal) || (family == headingFont && family != bodyFont))
    {
      return new JsonObject { ["theme"] = "heading" };
    }
    if (family.StartsWith("+mn", StringComparison.Ordinal) || family == bodyFont)
    {
      return new JsonObject { ["theme"] = "body" };
    }
    return new JsonObject { ["family"] = family };
  }

  /// <summary>A text style the tools read as character properties, with bold, italic, and underline only when on.
  /// </summary>
  private JsonObject Character(JsonObject? style, string where)
  {
    var result = new JsonObject();
    if (style is null)
    {
      return result;
    }
    if (Font((string?)style["font_family"]) is JsonObject font)
    {
      result["font"] = font;
    }
    if ((double?)style["font_size"] is double size)
    {
      result["size"] = Round(size);
    }
    if (Color(style["font_color"], where) is JsonObject color && color["none"] is null)
    {
      result["color"] = color;
    }
    foreach ((string key, string read) in new[] { ("bold", "is_bold"), ("italic", "is_italic"),
      ("underline", "is_underlined") })
    {
      if ((bool?)style[read] == true)
      {
        result[key] = true;
      }
    }
    return result;
  }

  private static JsonObject ParagraphProperties(JsonObject? style)
  {
    var result = new JsonObject();
    if (style is null)
    {
      return result;
    }
    if ((string?)style["alignment"] is string align)
    {
      result["align"] = align is "justified" or "distributed" ? "justify" : align;
    }
    foreach (string key in new[] { "space_before", "space_after" })
    {
      if ((double?)style[key] is double space && Math.Abs(space) > 0.005)
      {
        result[key] = Round(space);
      }
    }
    if ((double?)style["line_spacing"] is double spacing && Math.Abs(spacing - 1) > 0.005)
    {
      result["line_spacing"] = Round(spacing);
    }
    return result;
  }

  private static JsonObject BoxProperties(JsonObject? layout, string? autofit, bool? wrap)
  {
    var result = new JsonObject();
    if (layout is not null && (string?)layout["vertical_alignment"] is string valign)
    {
      result["valign"] = valign is "middle" or "center" ? "middle" : valign;
    }
    string[] margins = ["margin_left", "margin_top", "margin_right", "margin_bottom"];
    if (layout is not null && margins.All(key => layout[key] is not null))
    {
      result["insets"] = new JsonArray(margins.Select(key => (JsonNode)Round((double)layout[key]!)).ToArray());
    }
    if (autofit is "shrink" or "resize")
    {
      result["fit"] = autofit == "shrink" ? "shrink" : "grow";
    }
    if (wrap == false)
    {
      result["wrap"] = false;
    }
    return result;
  }

  private List<Paragraph> ExpandElement(JsonObject element, Layers layers, string where)
  {
    return Expand(element["text_component"], layers, where, package.Breaks(where, (int?)element["id"]));
  }

  /// <summary>The paragraphs of <paramref name="component"/>, with a space for each line break in
  /// <paramref name="breaks"/>, which the read tools leave out.</summary>
  private List<Paragraph> Expand(JsonNode? component, Layers layers, string where,
    List<(int Runs, HashSet<int> Broken)>? breaks = null)
  {
    var paragraphs = new List<Paragraph>();
    foreach ((JsonObject paragraph, int number) in component?["paragraph_list"]?.AsArray()
      .Select((node, number) => (node!.AsObject(), number)) ?? [])
    {
      JsonArray read = paragraph["runs"]?.AsArray() ?? [];
      HashSet<int> broken = breaks is not null && number < breaks.Count && breaks[number].Runs == read.Count
        ? breaks[number].Broken
        : [];
      JsonObject style = Merge(layers.Paragraph, paragraph["style"]);
      JsonObject bullet = Merge(layers.Bullet, paragraph["bullet"]);
      string? type = (string?)bullet["type"];
      bool numbered = type is "number" or "numbered" or "auto_number";
      bool bulleted = numbered || type == "character";
      int depth = (int?)paragraph["indent_level"] ?? 0;
      var runs = new List<Run>();
      foreach ((JsonObject run, int index) in read.Select((node, index) => (node!.AsObject(), index)))
      {
        string? field = (string?)run["field"];
        if (field is not null && field != "slidenum")
        {
          findings.Add($"{where}: the {field} field is kept as the text it showed");
        }
        string? link = (string?)run["hyperlink"] ?? (string?)run["link"] ?? (string?)run["text_style"]?["link"];
        JsonObject character = Character(Merge(layers.Text, run["text_style"]), where);
        if (link is not null)
        {
          // PowerPoint draws a link without a fill of its own in the theme's link color, underlined, and the tools
          // read that missing fill as tx1.
          if (run["text_style"]?["font_color"] is not JsonObject own || (string?)own["scheme"] == "tx1")
          {
            character["color"] = new JsonObject { ["theme"] = "hyperlink" };
          }
          if ((bool?)run["text_style"]?["is_underlined"] != false)
          {
            character["underline"] = true;
          }
        }
        // The format has no line break inside a paragraph, so a break, or a character PowerPoint draws as one, is a
        // space.
        string words = LineBreaks().Replace((string?)run["text"] ?? "", " ");
        if (broken.Contains(index) && index > 0)
        {
          findings.Add($"{where}: a line break inside a paragraph is kept as a space");
          if (!words.StartsWith(' ') && runs.Count > 0 && !runs[^1].Text.EndsWith(' '))
          {
            words = " " + words;
          }
        }
        runs.Add(new Run(words, field == "slidenum" ? "slide" : null, link, character));
      }
      // Neighbours alike are one run, as the markup reads them back.
      for (int index = runs.Count - 1; index > 0; index--)
      {
        Run left = runs[index - 1];
        Run right = runs[index];
        if (left.Field is null && right.Field is null && left.Link == right.Link &&
          JsonNode.DeepEquals(left.Properties, right.Properties))
        {
          runs[index - 1] = left with { Text = left.Text + right.Text };
          runs.RemoveAt(index);
        }
      }
      JsonObject mark = Character(Merge(layers.Text, paragraph["end_paragraph_run_style"]), where);
      JsonObject properties = runs.Any(run => run.Text.Trim().Length > 0) ? Common(runs) : mark;
      foreach ((string key, JsonNode? value) in ParagraphProperties(style))
      {
        properties[key] = value?.DeepClone();
      }
      double? bulletSize = bulleted && (bool?)bullet["font_size_is_percentage"] == true &&
        (double?)bullet["font_size"] is double percent && Math.Abs(percent - 100) > 0.5
          ? Round(percent / 100)
          : null;
      paragraphs.Add(new Paragraph(
        bulleted ? depth + 1 : 0,
        depth,
        bulleted ? numbered ? "1." : (string?)bullet["character"] ?? "•" : null,
        numbered,
        (double?)style["margin_left"] ?? 0,
        -((double?)style["indent"] ?? 0),
        // The tools read a bullet that follows its text's color as tx1.
        bulleted && (bullet["color"] as JsonObject)?["scheme"]?.GetValue<string>() != "tx1"
          ? Color(bullet["color"], where) : null,
        bulletSize,
        properties,
        runs));
    }
    while (paragraphs.Count > 0 && paragraphs[^1].Runs.All(run => run.Text.Length == 0 && run.Field is null))
    {
      paragraphs.RemoveAt(paragraphs.Count - 1);
    }
    return paragraphs;
  }

  /// <summary>The character properties most of the runs' text shares, with a flag on only when every run has it.
  /// </summary>
  private static JsonObject Common(IEnumerable<Run> runs)
  {
    List<Run> worded = runs.Where(run => run.Text.Trim().Length > 0 || run.Field is not null).ToList();
    var result = new JsonObject();
    foreach (string key in new[] { "font", "size", "color" })
    {
      if (Mode(worded.Select(run => (run.Properties[key], (double)Math.Max(1, run.Text.Length)))) is JsonNode value)
      {
        result[key] = value;
      }
    }
    foreach (string key in new[] { "bold", "italic", "underline" })
    {
      if (worded.Count > 0 && worded.All(run => (bool?)run.Properties[key] == true))
      {
        result[key] = true;
      }
    }
    return result;
  }

  private static Layers SlideLayers(JsonObject read)
  {
    return new Layers(read["default_text_style"], read["default_paragraph_style"], read["default_bullet"]);
  }

  private static Layers ElementLayers(Layers slide, JsonObject element)
  {
    return new Layers(Merge(slide.Text, element["default_text_style"]),
      Merge(slide.Paragraph, element["default_paragraph_style"]), Merge(slide.Bullet, element["default_bullet"]));
  }

  private static JsonArray Frame(JsonObject element)
  {
    return new JsonArray(Round((double)element["position"]!["left"]!), Round((double)element["position"]!["top"]!),
      Round((double)element["size"]!["width"]!), Round((double)element["size"]!["height"]!));
  }

  private static string Plain(JsonNode? component)
  {
    return string.Join(" ", (component?["paragraph_list"]?.AsArray() ?? []).Select(paragraph =>
      string.Concat((paragraph!["runs"]?.AsArray() ?? []).Select(run => (string?)run!["text"] ?? "")))).Trim();
  }

  private static string Kebab(string text, string fallback)
  {
    string[] words = NonWord().Replace(text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormKD), " ")
      .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
    return words.Length == 0 ? fallback : string.Join("-", words.Take(5));
  }

  private static string Unique(string name, ISet<string> taken, string separator = "-")
  {
    string candidate = name;
    for (int suffix = 2; taken.Contains(candidate); suffix++)
    {
      candidate = $"{name}{separator}{suffix}";
    }
    taken.Add(candidate);
    return candidate;
  }

  [GeneratedRegex(@"[^\w-]")]
  private static partial Regex NonWord();

  [GeneratedRegex(@"\r\n|[\r\n\v\u2028\u2029]")]
  private static partial Regex LineBreaks();

  #endregion

  #region The theme

  /// <summary>Text boxes and the body placeholders slides fill, whose text sets the base text and bullet levels.
  /// </summary>
  private IEnumerable<(List<Paragraph> Paragraphs, JsonObject Box)> TextBoxes()
  {
    foreach (JsonObject slide in SlideReads)
    {
      JsonObject read = slide["read"]!.AsObject();
      Layers layers = SlideLayers(read);
      foreach (JsonObject element in read["text_elements"]?.AsArray().Select(node => node!.AsObject()) ?? [])
      {
        bool body = (bool?)element["is_placeholder"] == true && (string?)element["placeholder"]?["role"] == "body";
        bool box = (bool?)element["is_placeholder"] != true && ((string?)element["shape_type"] ?? "rectangle") ==
          "rectangle";
        if (element["lock_source"] is not null || !(body || box))
        {
          continue;
        }
        List<Paragraph> paragraphs = ExpandElement(element, ElementLayers(layers, element),
          $"slide {slide["index"]}");
        yield return (paragraphs, BoxProperties(Merge(read["default_text_layout"],
          element["text_component"]?["text_layout"]), null, null));
      }
    }
  }

  /// <summary>The base text and bullet levels: what most of the deck's text boxes share, weighed by characters.
  /// </summary>
  private void Base()
  {
    var boxes = TextBoxes().ToList();
    List<Run> runs = boxes.SelectMany(box => box.Paragraphs.SelectMany(paragraph => paragraph.Runs))
      .Where(run => run.Text.Trim().Length > 0).ToList();
    List<Paragraph> paragraphs = boxes.SelectMany(box => box.Paragraphs).Where(paragraph => paragraph.Runs.Count > 0)
      .ToList();
    text = new JsonObject
    {
      ["font"] = Mode(runs.Select(run => (run.Properties["font"], (double)run.Text.Length))) ??
        new JsonObject { ["theme"] = "body" },
      ["size"] = Mode(runs.Select(run => (run.Properties["size"], (double)run.Text.Length))) ?? 18.0,
      ["color"] = Mode(runs.Select(run => (run.Properties["color"], (double)run.Text.Length))) ??
        new JsonObject { ["theme"] = "dark1" },
      ["align"] = Mode(paragraphs.Select(paragraph => (paragraph.Properties["align"], 1.0))) ?? "left",
      ["valign"] = Mode(boxes.Select(box => (box.Box["valign"], 1.0))) ?? "top",
      ["insets"] = Mode(boxes.Select(box => (box.Box["insets"], 1.0))) ?? new JsonArray(7.2, 3.6, 7.2, 3.6)
    };
    foreach (string key in new[] { "bold", "italic", "underline" })
    {
      if (Mode(runs.Select(run => ((JsonNode?)(run.Properties[key] ?? false), (double)run.Text.Length))) is JsonNode on &&
        (bool)on)
      {
        text[key] = true;
      }
    }
    foreach (string key in new[] { "space_before", "space_after", "line_spacing" })
    {
      JsonNode? absent = key == "line_spacing" ? 1.0 : 0.0;
      if (Mode(paragraphs.Select(paragraph => ((JsonNode?)(paragraph.Properties[key] ?? absent), 1.0))) is JsonNode value &&
        !JsonNode.DeepEquals(value, absent))
      {
        text[key] = value;
      }
    }

    List<Paragraph> bulleted = paragraphs.Where(paragraph => paragraph.Level > 0).ToList();
    int deepest = bulleted.Select(paragraph => paragraph.Level).DefaultIfEmpty(0).Max();
    for (int level = 1; level <= deepest; level++)
    {
      List<Paragraph> at = bulleted.Where(paragraph => paragraph.Level == level).ToList();
      if (at.Count == 0)
      {
        levels[level] = levels[level - 1].DeepClone().AsObject();
        continue;
      }
      var entry = new JsonObject
      {
        ["bullet"] = Mode(at.Select(paragraph => ((JsonNode?)(paragraph.Numbered ? null : paragraph.Bullet),
          (double)paragraph.Weight))) ?? "•"
      };
      if (Mode(at.Select(paragraph => ((JsonNode?)(paragraph.BulletColor ?? (JsonNode)"text"), (double)paragraph.Weight))) is
        JsonObject bulletColor)
      {
        entry["bulletColor"] = bulletColor;
      }
      if (Mode(at.Select(paragraph => ((JsonNode?)(paragraph.BulletSize ?? 1.0), (double)paragraph.Weight))) is
        JsonNode bulletSize && (double)bulletSize != 1)
      {
        entry["bulletSize"] = bulletSize;
      }
      double indent = (double)Mode(at.Select(paragraph => ((JsonNode?)Round(paragraph.Margin),
        (double)paragraph.Weight)))!;
      entry["indent"] = indent;
      double hanging = (double)Mode(at.Select(paragraph => ((JsonNode?)Round(Math.Max(0, paragraph.Hanging)),
        (double)paragraph.Weight)))!;
      double above = level > 1 ? (double)levels[level - 1]["indent"]! : 0;
      if (Math.Abs(hanging - Math.Max(0, indent - above)) > 0.5)
      {
        entry["hanging"] = hanging;
      }
      var properties = new JsonObject();
      List<Run> atRuns = at.SelectMany(paragraph => paragraph.Runs).Where(run => run.Text.Trim().Length > 0).ToList();
      foreach (string key in new[] { "font", "size", "color" })
      {
        if (Mode(atRuns.Select(run => (run.Properties[key], (double)run.Text.Length))) is JsonNode value &&
          !JsonNode.DeepEquals(value, text[key]))
        {
          properties[key] = value;
        }
      }
      foreach (string key in new[] { "align", "space_before", "space_after", "line_spacing" })
      {
        JsonNode? absent = key switch { "align" => null, "line_spacing" => 1.0, _ => 0.0 };
        JsonNode? value = Mode(at.Select(paragraph => (paragraph.Properties[key] ?? absent, (double)paragraph.Weight)));
        if (value is not null && !JsonNode.DeepEquals(value, text[key] ?? absent))
        {
          properties[key] = value;
        }
      }
      entry["properties"] = properties;
      levels[level] = entry;
    }
  }

  private JsonObject Theme()
  {
    JsonObject master = Master;
    var colors = new JsonObject();
    foreach (string key in ThemeColors)
    {
      colors[Scheme[key]] = hex.GetValueOrDefault(key, "#000000");
    }
    JsonObject? background = Color(master["background"]?["color"], "the master");
    var theme = new JsonObject
    {
      ["name"] = (string?)master["theme_name"] ?? name,
      ["colors"] = colors,
      ["fonts"] = new JsonObject { ["heading"] = headingFont, ["body"] = bodyFont },
      ["background"] = background ?? new JsonObject { ["theme"] = "light1" },
      ["text"] = text.DeepClone(),
      ["levels"] = new JsonObject(levels.OrderBy(pair => pair.Key).Select(pair =>
        KeyValuePair.Create(pair.Key.ToString(CultureInfo.InvariantCulture), (JsonNode?)pair.Value.DeepClone()))),
      ["placeholders"] = new JsonArray(),
      ["elements"] = new JsonArray()
    };
    if (MasterPlaceholder("title") is JsonObject title)
    {
      theme["title"] = PlaceholderItem("title", "title", "title", title, null, "the master");
    }
    foreach (string key in new[] { "shape_elements", "picture_elements", "text_elements", "chart_elements",
      "table_elements", "group_elements" })
    {
      if ((master[key]?.AsArray().Count ?? 0) > 0)
      {
        findings.Add($"the master: its {key.Replace('_', ' ')} are left out; only its layouts' are read");
      }
    }
    return theme;
  }

  private JsonObject? MasterPlaceholder(string role)
  {
    string fallback = role switch { "centered_title" => "title", "sub_title" or "body" => "body", _ => role };
    IEnumerable<JsonObject> placeholders = Master["placeholders"]?.AsArray().Select(node => node!.AsObject()) ?? [];
    return placeholders.FirstOrDefault(placeholder => (string?)placeholder["role"] == role) ??
      placeholders.FirstOrDefault(placeholder => (string?)placeholder["role"] == fallback);
  }

  /// <summary>A title or placeholder as its prompt draws it, over the master's placeholder it inherits from; the
  /// tools read a prompt's runs in full.</summary>
  private JsonObject PlaceholderItem(string id, string role, string? style, JsonObject placeholder, JsonObject? above,
    string where)
  {
    JsonObject? Prompt(JsonObject? source) => source?["prompt"]?["paragraph_list"]?[0]?.AsObject();
    JsonObject? own = Prompt(placeholder);
    JsonObject? inherited = Prompt(above);
    JsonObject run = Merge(inherited?["runs"]?[0]?["text_style"], own?["runs"]?[0]?["text_style"]);
    JsonObject paragraphStyle = Merge(inherited?["style"], own?["style"]);
    JsonObject layout = Merge(above?["prompt"]?["text_layout"], placeholder["prompt"]?["text_layout"]);
    JsonObject character = Character(run, where);
    var paragraph = character.DeepClone().AsObject();
    foreach ((string key, JsonNode? value) in ParagraphProperties(paragraphStyle))
    {
      paragraph[key] = value?.DeepClone();
    }
    var properties = new JsonObject { ["frame"] = Frame(placeholder) };
    if (style is not null)
    {
      properties["style"] = style;
    }
    foreach ((string key, JsonNode? value) in paragraph)
    {
      properties[key] = value?.DeepClone();
    }
    foreach ((string key, JsonNode? value) in BoxProperties(layout, (string?)placeholder["autofit"] ??
      (string?)above?["autofit"], (bool?)placeholder["wrap_text"]))
    {
      properties[key] = value?.DeepClone();
    }
    return new JsonObject
    {
      ["id"] = id,
      ["kind"] = "text",
      ["role"] = role,
      ["properties"] = properties,
      ["paragraphs"] = new JsonArray(new JsonObject
      {
        ["level"] = 0, ["hanging"] = 0, ["properties"] = paragraph.DeepClone(), ["runs"] = new JsonArray()
      })
    };
  }

  #endregion

  #region Layouts

  /// <summary>A layout's JSON, with what its slides need to find their placeholders: each by the index PowerPoint
  /// gave it, and each area's frame.</summary>
  private sealed class LayoutDraft
  {
    public required string Id { get; init; }
    public required string ReadId { get; init; }
    public required JsonObject Json { get; init; }
    // PowerPoint numbers some placeholders past what an int holds, such as 4294967295.
    public Dictionary<long, JsonObject> Placeholders { get; } = new Dictionary<long, JsonObject>();
    public JsonObject? Title { get; set; }
    public OrderedDictionary<string, JsonArray> Areas { get; } = new OrderedDictionary<string, JsonArray>();
    public HashSet<string> Ids { get; } = new HashSet<string>();
    public JsonArray Elements => Json["elements"]!.AsArray();
  }

  private List<LayoutDraft> Layouts()
  {
    var drafts = new List<LayoutDraft>();
    var stems = new HashSet<string>();
    foreach (JsonObject summary in LayoutReads)
    {
      JsonObject read = summary["read"]!.AsObject();
      string layoutName = (string)summary["name"]!;
      string where = $"layout {layoutName}";
      var draft = new LayoutDraft
      {
        Id = Unique(Kebab(layoutName, "layout"), stems),
        ReadId = summary["id"]!.ToJsonString(),
        Json = new JsonObject
        {
          ["name"] = layoutName, ["themeElements"] = true, ["titleRemoved"] = false,
          ["placeholders"] = new JsonArray(), ["elements"] = new JsonArray()
        }
      };
      draft.Json.Insert(0, "id", draft.Id);
      var names = new HashSet<string>();
      var ids = new Ids(draft.Ids);
      JsonObject? title = read["placeholders"]?.AsArray().Select(node => node!.AsObject())
        .FirstOrDefault(placeholder => (string?)placeholder["role"] is "title" or "centered_title");
      if (title is null)
      {
        draft.Json["titleRemoved"] = true;
      }
      else
      {
        draft.Title = PlaceholderItem("title", "title", "title", title, MasterPlaceholder("title"), where);
        draft.Json["title"] = draft.Title;
      }

      foreach (JsonObject placeholder in read["placeholders"]?.AsArray().Select(node => node!.AsObject()) ?? [])
      {
        string role = (string?)placeholder["role"] ?? "body";
        if (placeholder == title)
        {
          continue;
        }
        if (role == "slide_number")
        {
          JsonObject number = PlaceholderItem(ids.Next("slide-number"), "slide_number", null, placeholder,
            MasterPlaceholder("slide_number"), where);
          number.Remove("role");
          number["paragraphs"]![0]!["runs"] = new JsonArray(new JsonObject
          {
            ["text"] = "", ["field"] = "slide",
            ["properties"] = Characterless(number["paragraphs"]![0]!["properties"]!.AsObject())
          });
          draft.Elements.Add(number);
          continue;
        }
        if (role is "date_and_time" or "footer" or "header")
        {
          findings.Add($"{where}: its {role.Replace('_', ' ')} placeholder is left out");
          continue;
        }
        string placeholderName = PlaceholderName(placeholder, names);
        JsonArray prompts = placeholder["prompt"]?["paragraph_list"]?.AsArray() ?? [];
        bool bulleted = (string?)prompts.FirstOrDefault()?["bullet"]?["type"] is string bullet && bullet != "none";
        if (prompts.Count != 1 || bulleted || role is "object" or "chart" or "table" or "picture" or "media")
        {
          draft.Areas[placeholderName] = Frame(placeholder);
          continue;
        }
        JsonObject item = PlaceholderItem(placeholderName, "placeholder", null, placeholder,
          MasterPlaceholder(role), where);
        if (Differs(item["properties"]!.AsObject(), text))
        {
          item["properties"]!.AsObject().Insert(1, "style", PlaceholderStyle(item, placeholderName));
        }
        draft.Json["placeholders"]!.AsArray().Add(item);
        if ((long?)placeholder["index"] is long index)
        {
          draft.Placeholders[index] = item;
        }
      }

      Layers layers = SlideLayers(read);
      foreach (JsonObject element in Ordered(read))
      {
        if ((bool?)element["is_placeholder"] == true)
        {
          continue;
        }
        if (Element(element, read, layers, ids, where, 1) is JsonObject item)
        {
          draft.Elements.Add(item);
        }
      }
      // The tools leave follows_theme out when false.
      if (read["background"] is JsonObject background && (bool?)background["follows_theme"] != true &&
        Color(background["color"], where) is JsonObject color)
      {
        draft.Json.Insert(2, "background", color);
      }
      if (draft.Areas.Count > 0)
      {
        draft.Json["areas"] = new JsonObject(draft.Areas.Select(pair =>
          KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value.DeepClone())));
      }
      drafts.Add(draft);
    }
    return drafts;
  }

  /// <summary>The style a placeholder names: one an earlier placeholder drawn alike already named, or its own.
  /// </summary>
  private string PlaceholderStyle(JsonObject item, string placeholderName)
  {
    string signature = new JsonObject(TextProperties.Style.Where(key => item["properties"]![key] is not null)
      .Select(key => KeyValuePair.Create(key, (JsonNode?)item["properties"]![key]!.DeepClone()))).ToJsonString();
    if (placeholderStyles.TryGetValue(signature, out string? named))
    {
      return named;
    }
    return placeholderStyles[signature] = placeholderName;
  }

  private readonly Dictionary<string, string> placeholderStyles = new Dictionary<string, string>();

  /// <summary>Whether a placeholder's text differs from the base text, so that a style of its own is worth naming.
  /// </summary>
  private static bool Differs(JsonObject properties, JsonObject baseText)
  {
    return TextProperties.Style.Any(key => !JsonNode.DeepEquals(properties[key], baseText[key]));
  }

  private static JsonObject Characterless(JsonObject paragraph)
  {
    var run = new JsonObject();
    foreach (string key in TextProperties.Character.Where(paragraph.ContainsKey))
    {
      run[key] = paragraph[key]!.DeepClone();
    }
    return run;
  }

  private static string PlaceholderName(JsonObject placeholder, HashSet<string> taken)
  {
    string prompt = Plain(placeholder["prompt"]);
    string role = (string?)placeholder["role"] ?? "body";
    string name = role == "sub_title" ? "subtitle" : "";
    if (name.Length == 0 && prompt.StartsWith('[') && prompt.EndsWith(']'))
    {
      name = Kebab(prompt[1..^1], "").Replace('-', '_');
    }
    // A prompt of a word or two names the placeholder; PowerPoint's own prompts are sentences.
    if (name.Length == 0 && prompt.Length is > 0 and <= 30 && prompt.Split(' ').Length <= 3 &&
      !prompt.StartsWith("Click", StringComparison.Ordinal))
    {
      name = Kebab(prompt, "").Replace('-', '_');
    }
    if (name.Length == 0)
    {
      name = role == "body" ? "content" : $"{role}_{placeholder["index"]}";
    }
    return Unique(name, taken, "_");
  }

  /// <summary>An element on half the layouts or more moves to the theme, and a layout missing any of them leaves
  /// out the theme's.</summary>
  private void Hoist(JsonObject theme, List<LayoutDraft> layouts)
  {
    string Signature(JsonNode element) => element.ToJsonString();
    var counts = new Dictionary<string, int>();
    foreach (LayoutDraft layout in layouts)
    {
      foreach (JsonNode? element in layout.Elements)
      {
        counts[Signature(element!)] = counts.GetValueOrDefault(Signature(element!)) + 1;
      }
    }
    var hoisted = new List<JsonObject>();
    foreach (JsonObject element in layouts.SelectMany(layout => layout.Elements).Select(node => node!.AsObject()))
    {
      if (counts[Signature(element)] >= 2 && counts[Signature(element)] * 2 >= layouts.Count &&
        hoisted.All(item => Signature(item) != Signature(element)))
      {
        hoisted.Add(element);
      }
    }
    themeElements = hoisted.Select(element => element.DeepClone().AsObject()).ToList();
    theme["elements"] = new JsonArray(themeElements.Select(element => (JsonNode)element.DeepClone()).ToArray());
    var signatures = hoisted.Select(Signature).ToHashSet();
    foreach (LayoutDraft layout in layouts)
    {
      var own = layout.Elements.Select(node => node!.AsObject()).ToList();
      if (signatures.Count > 0 && signatures.All(signature => own.Any(element => Signature(element) == signature)))
      {
        layout.Json["elements"] = new JsonArray(own.Where(element => !signatures.Contains(Signature(element)))
          .Select(element => (JsonNode)element.DeepClone()).ToArray());
      }
      else if (signatures.Count > 0)
      {
        layout.Json["themeElements"] = false;
      }
    }
  }

  #endregion

  #region Elements

  /// <summary>Ids for one scope, clear of the ones the theme and layout already hold.</summary>
  private sealed class Ids(HashSet<string> taken)
  {
    private readonly Dictionary<string, int> counts = new Dictionary<string, int>();

    public string Next(string kind)
    {
      string id;
      do
      {
        int count = counts[kind] = counts.GetValueOrDefault(kind) + 1;
        id = count == 1 && kind is "slide-number" or "title" ? kind : $"{kind}-{count}";
      }
      while (taken.Contains(id));
      taken.Add(id);
      return id;
    }

    /// <summary>An id from a name, made unique.</summary>
    public string Take(string name)
    {
      return Unique(name, taken);
    }


  }

  /// <summary>A read's elements, back to front, with those a slide shows from its layout left out and groups
  /// flattened, as a presentation has none. The tools read a group's children in slide coordinates and in the
  /// slide's stacking order, so each stands on its own.</summary>
  private static IEnumerable<JsonObject> Ordered(JsonObject read)
  {
    string[] kinds = ["text_elements", "shape_elements", "chart_elements", "table_elements", "picture_elements",
      "group_elements", "connector_elements", "smart_art_elements", "media_elements"];
    IEnumerable<JsonObject> Elements(JsonObject source, bool grouped)
    {
      return kinds
        .SelectMany(kind => (source[kind]?.AsArray() ?? []).Select(node =>
        {
          JsonObject element = node!.AsObject();
          element["kind"] ??= kind;
          if (grouped)
          {
            element["grouped"] = true;
          }
          return element;
        }))
        .Where(element => element["lock_source"] is null)
        .SelectMany(element => (string)element["kind"]! == "group_elements" ? Elements(element, true) : [element]);
    }
    return Elements(read, false).OrderBy(element => (double?)element["position"]?["z_index"] ?? 0);
  }

  /// <summary>One element as the model's item, or null when it has nothing to draw.</summary>
  private JsonObject? Element(JsonObject element, JsonObject read, Layers layers, Ids ids, string where, int level,
    string? slideStem = null)
  {
    string kind = (string)element["kind"]!;
    // The tools fold none of a group's children into the slide's defaults, so each carries its own in full.
    JsonObject? defaults = (bool?)element["grouped"] == true ? null : read;
    if (defaults is null)
    {
      layers = new Layers(null, null, null);
    }
    switch (kind)
    {
      case "text_elements":
        return TextElement(element, layers, ids, where, defaults);
      case "shape_elements":
        return ShapeElement(element, layers, ids, where, defaults);
      case "chart_elements":
        return Chart(element, slideStem ?? "chart", ids, where);
      case "table_elements":
        return Table(element, slideStem ?? "table", ids, where);
      case "picture_elements":
        return Picture(element, ids, where);
      default:
        findings.Add($"{where}: {kind.Replace("_elements", "").Replace('_', ' ')} is out of scope and left out");
        return null;
    }
  }

  /// <summary>An element's id: the name it has in the package, unless PowerPoint made it up, or its kind and count.
  /// </summary>
  private string Id(JsonObject element, string kind, Ids ids, string where)
  {
    string? named = package.Name(where, (int?)element["id"]);
    return named is not null && !DefaultName().IsMatch(named) && Kebab(named, "") is { Length: > 0 } id
      ? ids.Take(id)
      : ids.Next(kind);
  }

  [GeneratedRegex(@"^(Title|Subtitle|Text ?Box|Rectangle|Rounded Rectangle|Oval|Ellipse|Picture|Graphic|Image|Chart|Table|Content Placeholder|Text Placeholder|Picture Placeholder|Straight Connector|Straight Arrow Connector|Elbow Connector|Connector|Freeform|Group|Line|Isosceles Triangle|Right Triangle|Right Arrow|Left Arrow|Up Arrow|Down Arrow|Arrow|Chevron|Pentagon|Hexagon|Diamond|Parallelogram|Trapezoid|Right Brace|Left Brace|Right Bracket|Left Bracket|Callout|Rectangular Callout|Speech Bubble|Shape|Slide Number Placeholder|Footer Placeholder|Date Placeholder|Google Shape;.*)\s*\d*$", RegexOptions.IgnoreCase)]
  private static partial Regex DefaultName();

  private JsonObject Item(string id, string kind, JsonObject properties, JsonArray? paragraphs)
  {
    var item = new JsonObject { ["id"] = id, ["kind"] = kind, ["properties"] = properties };
    if (paragraphs is not null)
    {
      item["paragraphs"] = paragraphs;
    }
    return item;
  }

  private JsonObject? TextElement(JsonObject element, Layers layers, Ids ids, string where, JsonObject? read)
  {
    string shapeType = (string?)element["shape_type"] ?? "rectangle";
    Layers own = ElementLayers(layers, element);
    List<Paragraph> paragraphs = ExpandElement(element, own, where);
    JsonObject style = Merge(read?["text_element_shape_style"], element["shape_style"]);
    if (shapeType != "rectangle" || HasOutline(style, where))
    {
      return Shape(element, shapeType, style, paragraphs, own, ids, where, read);
    }
    if (paragraphs.Count == 0)
    {
      return null;
    }
    var properties = new JsonObject { ["frame"] = Frame(element) };
    Rotation(element, properties);
    BoxText(properties, paragraphs, own, Merge(read?["default_text_layout"],
      element["text_component"]?["text_layout"]), element, where);
    return Item(Id(element, "text", ids, where), "text", properties, Paragraphs(paragraphs, where));
  }

  private bool HasOutline(JsonObject style, string where)
  {
    return Fill(style, where) is JsonObject fill && fill["none"] is null ||
      Stroke(style, where) is JsonObject stroke && stroke["stroke"] is null;
  }

  private JsonObject? ShapeElement(JsonObject element, Layers layers, Ids ids, string where, JsonObject? read)
  {
    string shapeType = (string?)element["shape_type"] ?? (string?)read?["default_shape_type"] ?? "rectangle";
    JsonObject style = Merge(read?["shape_element_shape_style"], element["shape_style"]);
    if (Lines.Contains(shapeType))
    {
      return Line(element, style, ids, where);
    }
    Layers own = ElementLayers(layers, element);
    List<Paragraph> paragraphs = ExpandElement(element, own, where);
    return Shape(element, shapeType, style, paragraphs, own, ids, where, read);
  }

  private JsonObject Line(JsonObject element, JsonObject style, Ids ids, string where)
  {
    double left = (double)element["position"]!["left"]!;
    double top = (double)element["position"]!["top"]!;
    double width = (double)element["size"]!["width"]!;
    double height = (double)element["size"]!["height"]!;
    bool horizontal = (bool?)element["position"]!["flip_horizontal"] == true;
    bool vertical = (bool?)element["position"]!["flip_vertical"] == true;
    var properties = new JsonObject
    {
      ["from"] = new JsonArray(Round(horizontal ? left + width : left), Round(vertical ? top + height : top)),
      ["to"] = new JsonArray(Round(horizontal ? left : left + width), Round(vertical ? top : top + height))
    };
    foreach ((string read, string key) in new[] { ("head_arrow", "start"), ("tail_arrow", "end") })
    {
      if ((string?)style[read] is string head && head != "none")
      {
        if (PresentationLoader.Markers.Contains(head))
        {
          properties[key] = head;
        }
        else
        {
          findings.Add($"{where}: a line's {head} arrowhead has no match in the format and is left out");
        }
      }
    }
    foreach ((string key, JsonNode? value) in Stroke(style, where) ?? new JsonObject { ["stroke"] = "none" })
    {
      properties[key] = value?.DeepClone();
    }
    return Item(Id(element, "line", ids, where), "line", properties, null);
  }

  private JsonObject Shape(JsonObject element, string shapeType, JsonObject style, List<Paragraph> paragraphs,
    Layers layers, Ids ids, string where, JsonObject? read)
  {
    var properties = new JsonObject { ["frame"] = Frame(element) };
    double width = (double)element["size"]!["width"]!;
    double height = (double)element["size"]!["height"]!;
    double shorter = Math.Min(width, height);
    JsonObject adjustments = element["shape_adjustments"]?.AsObject() ?? new JsonObject();
    if (!Shapes.TryGetValue(shapeType, out var mapped))
    {
      findings.Add($"{where}: a {shapeType.Replace('_', ' ')} is drawn as a rectangle, which the format has in its place");
      mapped = ("rectangle", null);
    }
    properties["shape"] = mapped.Shape;
    if (mapped.Direction is not null)
    {
      properties["direction"] = mapped.Direction;
    }
    // The tools' adjustments run 0 to 1 across each handle's range, where the format writes points, and a shape left
    // at the format's default states none.
    switch (mapped.Shape)
    {
      case "rounded_rectangle" when (double?)adjustments["corner_radius"] is double corner:
        double radius = Round(corner * shorter / 2);
        if (Math.Abs(radius - shorter * 0.16667) > 0.5)
        {
          properties["adjust.corner"] = Math.Round(radius);
        }
        break;
      case "chevron" or "home_plate" when (double?)adjustments["point_depth"] is double depth:
        Point(properties, depth * width, shorter);
        break;
      case "arrow" when (double?)adjustments["head_length"] is double length:
        Point(properties, length * (mapped.Direction is "left" or "right" ? width : height), shorter);
        break;
      case "callout":
        double tailX = (double?)adjustments["tail_x"] ?? -0.2;
        double tailY = (double?)adjustments["tail_y"] ?? 0.63;
        JsonArray frame = Frame(element);
        properties["tail"] = new JsonArray(Round((double)frame[0]! + width / 2 + tailX * width),
          Round((double)frame[1]! + height / 2 + tailY * height));
        // The tools normalize the tail's handles, so they may not aim where PowerPoint draws it.
        findings.Add($"{where}: a callout's tail may not point where PowerPoint draws it");
        break;
    }
    if (Fill(style, where) is JsonObject fill)
    {
      properties["fill"] = fill;
      if ((double?)(style["fill"] as JsonObject)?["alpha"] is double alpha && alpha < 99.5)
      {
        properties["opacity"] = Round(alpha);
      }
    }
    foreach ((string key, JsonNode? value) in Stroke(style, where) ?? new JsonObject())
    {
      properties[key] = value?.DeepClone();
    }
    Rotation(element, properties);
    BoxText(properties, paragraphs, layers, Merge(read?["default_text_layout"], element["text_component"]?["text_layout"]),
      element, where);
    return Item(Id(element, "shape", ids, where), "shape", properties,
      paragraphs.Count > 0 ? Paragraphs(paragraphs, where) : null);
  }

  private static void Point(JsonObject properties, double point, double shorter)
  {
    if (Math.Abs(point - shorter * 0.5) > 0.5)
    {
      properties["adjust.point"] = Math.Round(point);
    }
  }

  private static void Rotation(JsonObject element, JsonObject properties)
  {
    double rotation = (double?)element["position"]!["rotation"] ?? 0;
    bool horizontal = (bool?)element["position"]!["flip_horizontal"] == true;
    bool vertical = (bool?)element["position"]!["flip_vertical"] == true;
    if (horizontal && vertical)
    {
      rotation = (rotation + 180) % 360;
    }
    else if (horizontal || vertical)
    {
      properties["flip"] = horizontal ? "horizontal" : "vertical";
    }
    if (Math.Abs(rotation) > 0.005)
    {
      properties["rotate"] = Round(rotation);
    }
  }

  private JsonObject? Fill(JsonObject style, string where)
  {
    return style["fill"] is JsonNode fill ? Color(fill, where) : null;
  }

  /// <summary>A stroke as the model's dotted keys, or <c>stroke: none</c>.</summary>
  private JsonObject? Stroke(JsonObject style, string where)
  {
    if (style["border_color"] is not JsonNode border)
    {
      return null;
    }
    if ((string?)(border as JsonValue) == "transparent" || (double?)style["border_weight"] == 0)
    {
      return new JsonObject { ["stroke"] = "none" };
    }
    var stroke = new JsonObject();
    if (Color(border, where) is JsonObject color)
    {
      stroke["stroke.color"] = color;
    }
    if ((double?)style["border_weight"] is double weight)
    {
      stroke["stroke.width"] = Round(weight);
    }
    string dash = (string?)style["border_dash_type"] ?? "solid";
    if (dash != "solid")
    {
      stroke["stroke.dash"] = dash.Contains("dot", StringComparison.Ordinal) ? "dot" : "dash";
    }
    return stroke;
  }

  /// <summary>A box's text properties: what most of its runs and paragraphs share, and its box's own.</summary>
  private void BoxText(JsonObject properties, List<Paragraph> paragraphs, Layers layers, JsonObject layout,
    JsonObject element, string where)
  {
    // A shape without text has no text to state properties for.
    if (paragraphs.Count == 0)
    {
      return;
    }
    // The box takes what most of its paragraphs share, so a big figure under its label doesn't restyle the label.
    List<Paragraph> worded = paragraphs.Where(paragraph => paragraph.Runs.Any(run => run.Text.Trim().Length > 0))
      .DefaultIfEmpty(paragraphs[0]).ToList();
    foreach (string key in new[] { "font", "size", "color", "align", "space_before", "space_after", "line_spacing" })
    {
      JsonNode? value = Mode(worded.Select(paragraph => ((JsonNode?)(paragraph.Properties[key] ?? ""), 1.0)));
      if (value is not null && (value as JsonValue)?.ToString() != "")
      {
        properties[key] = value.DeepClone();
      }
    }
    foreach (string key in new[] { "bold", "italic", "underline" })
    {
      if (worded.All(paragraph => (bool?)paragraph.Properties[key] == true))
      {
        properties[key] = true;
      }
    }
    foreach ((string key, JsonNode? value) in BoxProperties(layout, (string?)element["autofit"],
      (bool?)element["wrap_text"]))
    {
      properties[key] = value?.DeepClone();
    }
  }

  private JsonArray Paragraphs(List<Paragraph> paragraphs, string where)
  {
    var result = new JsonArray();
    // A numbered list counts on at its level, and starts again after anything shallower or unnumbered there.
    var counts = new Dictionary<int, int>();
    foreach (Paragraph paragraph in paragraphs)
    {
      if (paragraph.Level == 0 && (paragraph.Depth > 0 || paragraph.Margin > 1))
      {
        findings.Add($"{where}: an indented paragraph without a bullet loses its indent");
      }
      int level = LevelFor(paragraph);
      foreach (int deeper in counts.Keys.Where(key => key > level).ToList())
      {
        counts.Remove(deeper);
      }
      if (!paragraph.Numbered)
      {
        counts.Remove(level);
      }
      var json = new JsonObject { ["level"] = level };
      if (level > 0)
      {
        json["bullet"] = paragraph.Numbered
          ? $"{counts[level] = counts.GetValueOrDefault(level) + 1}."
          : paragraph.Bullet;
        if (paragraph.BulletColor is not null)
        {
          json["bulletColor"] = paragraph.BulletColor.DeepClone();
        }
        if (paragraph.BulletSize is double size)
        {
          json["bulletSize"] = size;
        }
        json["indent"] = Round(paragraph.Margin);
        json["hanging"] = Round(Math.Max(0, paragraph.Hanging));
      }
      else
      {
        json["hanging"] = 0;
      }
      json["properties"] = paragraph.Properties.DeepClone();
      json["runs"] = new JsonArray(paragraph.Runs
        .Where(run => run.Text.Length > 0 || run.Field is not null)
        .Select(run =>
        {
          var entry = new JsonObject { ["text"] = run.Text };
          if (run.Field is not null)
          {
            entry["field"] = run.Field;
          }
          if (run.Link is not null)
          {
            entry["link"] = run.Link;
          }
          entry["properties"] = run.Properties.DeepClone();
          return (JsonNode)entry;
        })
        .ToArray());
      result.Add(json);
    }
    return result;
  }

  /// <summary>A bulleted paragraph's level: the one PowerPoint gives it, or the theme's level whose bullet and indent
  /// it has, for a deck that indents paragraphs by hand rather than by level.</summary>
  private int LevelFor(Paragraph paragraph)
  {
    if (paragraph.Level == 0)
    {
      return 0;
    }
    bool Matches(JsonObject level) => (double?)level["indent"] is double indent &&
      Math.Abs(indent - paragraph.Margin) < 0.5 && (paragraph.Numbered || (string?)level["bullet"] == paragraph.Bullet);
    if (levels.TryGetValue(paragraph.Level, out JsonObject? own) && Matches(own))
    {
      return paragraph.Level;
    }
    return levels.Where(pair => Matches(pair.Value)).Select(pair => pair.Key).DefaultIfEmpty(paragraph.Level).First();
  }

  private JsonObject? Picture(JsonObject element, Ids ids, string where)
  {
    string id = Id(element, "picture", ids, where);
    string? file = package.Picture(where, (int?)element["id"], id.StartsWith("picture-", StringComparison.Ordinal)
      ? null
      : id);
    if (file is null)
    {
      findings.Add($"{where}: a picture is left out, since its image isn't in the package where its id points");
      return null;
    }
    var properties = new JsonObject { ["frame"] = Frame(element), ["picture"] = file, ["fit"] = "stretch" };
    Rotation(element, properties);
    return Item(id, "picture", properties, null);
  }

  #endregion

  #region Slides

  /// <summary>PowerPoint sizes a table's frame to its rows, where the format's frame is the room they have, so a table
  /// at the top of an area, as wide, takes the area.</summary>
  private static void RoomToGrow(JsonObject item, LayoutDraft layout)
  {
    if ((string?)item["kind"] != "table" || item["properties"]?["frame"] is not JsonArray frame)
    {
      return;
    }
    double[] rect = frame.Select(node => (double)node!).ToArray();
    foreach (JsonArray area in layout.Areas.Values)
    {
      double[] room = area.Select(node => (double)node!).ToArray();
      if (Enumerable.Range(0, 3).All(index => Math.Abs(room[index] - rect[index]) < 0.5) && room[3] >= rect[3] - 0.5)
      {
        item["properties"]!["frame"] = area.DeepClone();
        return;
      }
    }
  }

  private JsonArray Slides(List<LayoutDraft> layouts)
  {
    var slides = new JsonArray();
    var stems = new HashSet<string>();
    Dictionary<int, string> sections = package.Sections();
    foreach (JsonObject slide in SlideReads)
    {
      int index = (int)slide["index"]!;
      JsonObject read = slide["read"]!.AsObject();
      string where = $"slide {index}";
      LayoutDraft? layout = layouts.FirstOrDefault(candidate => candidate.ReadId == slide["layout"]?.ToJsonString());
      if (layout is null)
      {
        findings.Add($"{where}: its layout isn't one the master holds, so the slide is left out");
        continue;
      }
      JsonObject? titleElement = read["text_elements"]?.AsArray().Select(node => node!.AsObject())
        .FirstOrDefault(element => (bool?)element["is_placeholder"] == true &&
          (string?)element["placeholder"]?["role"] is "title" or "centered_title");
      string stem = Unique(Kebab(titleElement is null ? "" : Plain(titleElement["text_component"]), $"slide-{index}"),
        stems);
      var taken = new HashSet<string>(((bool)layout.Json["themeElements"]! ? themeElements : [])
        .Concat(layout.Elements.Select(node => node!.AsObject())).Select(element => (string)element["id"]!));
      var ids = new Ids(taken);
      Layers layers = SlideLayers(read);
      var items = new JsonArray();
      foreach (JsonObject element in Ordered(read))
      {
        if ((bool?)element["is_placeholder"] == true && (string)element["kind"]! == "text_elements")
        {
          (bool matched, JsonObject? filled) = SlidePlaceholder(element, read, layers, layout, where);
          if (filled is not null)
          {
            items.Add(filled);
          }
          if (matched || (string?)element["placeholder"]?["role"] == "slide_number")
          {
            continue;
          }
        }
        if (Element(element, read, layers, ids, where, 2, stem) is JsonObject item)
        {
          RoomToGrow(item, layout);
          items.Add(item);
        }
      }
      var hide = new JsonArray();
      if ((bool?)read["header_footer"]?["slide_number"] == false &&
        taken.FirstOrDefault(id => id.StartsWith("slide-number", StringComparison.Ordinal)) is string number)
      {
        hide.Add(number);
      }
      slides.Add(new JsonObject
      {
        ["id"] = stem,
        ["number"] = index,
        ["section"] = sections.TryGetValue(index, out string? section) ? section : null,
        ["layout"] = layout.Id,
        ["items"] = items,
        ["hide"] = hide
      });
    }
    return slides;
  }

  /// <summary>A slide's title or one of its layout's placeholders, filled, or nothing when it is empty; unmatched for
  /// a placeholder the layout makes an area, which the slide fills with an element.</summary>
  private (bool Matched, JsonObject? Item) SlidePlaceholder(JsonObject element, JsonObject read, Layers layers,
    LayoutDraft layout, string where)
  {
    string role = (string?)element["placeholder"]?["role"] ?? "body";
    bool title = role is "title" or "centered_title";
    JsonObject? target = title
      ? layout.Title
      : (long?)element["placeholder"]?["index"] is long index ? layout.Placeholders.GetValueOrDefault(index) : null;
    if (target is null)
    {
      return (false, null);
    }
    List<Paragraph> paragraphs = ExpandElement(element, ElementLayers(layers, element), where);
    if (paragraphs.Count == 0)
    {
      return (true, null);
    }
    if (paragraphs.Count > 1)
    {
      findings.Add($"{where}: a {(title ? "title" : "placeholder")} of {paragraphs.Count} paragraphs is joined into one");
    }
    List<Run> runs = paragraphs.SelectMany((paragraph, position) => position == 0
      ? paragraph.Runs
      : paragraph.Runs.Prepend(new Run(" ", null, null, paragraph.Runs.FirstOrDefault()?.Properties ??
        new JsonObject()))).ToList();
    var joined = paragraphs[0] with { Runs = runs };
    var properties = new JsonObject { ["frame"] = Frame(element) };
    if ((string?)target["properties"]!["style"] is string style)
    {
      properties["style"] = style;
    }
    BoxText(properties, [joined], ElementLayers(layers, element), Merge(read["default_text_layout"],
      element["text_component"]?["text_layout"]), element, where);
    foreach (string key in new[] { "valign", "insets", "fit", "wrap" })
    {
      if (properties[key] is null && target["properties"]![key] is JsonNode inherited)
      {
        properties[key] = inherited.DeepClone();
      }
    }
    return (true, new JsonObject
    {
      ["id"] = (string)target["id"]!,
      ["kind"] = "text",
      ["role"] = title ? "title" : "placeholder",
      ["properties"] = properties,
      ["paragraphs"] = Paragraphs([joined], where)
    });
  }

  #endregion

  #region Charts

  private static string Family(string type)
  {
    return type switch
    {
      "pie" or "doughnut" => "pie",
      _ when type.EndsWith("line", StringComparison.Ordinal) => "line",
      _ when type.EndsWith("bar", StringComparison.Ordinal) => "bar",
      _ => "column"
    };
  }

  private JsonObject Chart(JsonObject element, string slide, Ids ids, string where)
  {
    string id = Id(element, "chart", ids, where);
    string stem = Unique(slide, chartStems);
    List<JsonObject> series = (element["series_list"]?.AsArray() ?? []).Select(node => node!.AsObject())
      .OrderBy(entry => (int?)entry["order"] ?? (int?)entry["index"] ?? 0).ToList();
    bool IsLine(JsonObject entry) => ((string?)entry["chart_type"] ?? "").Contains("line", StringComparison.Ordinal);
    List<string> types = series.Select(entry => (string?)entry["chart_type"] ?? "clustered_column").Distinct().ToList();
    string type = types.FirstOrDefault(candidate => !candidate.Contains("line", StringComparison.Ordinal)) ??
      types.FirstOrDefault() ?? "clustered_column";
    List<JsonObject> drawn = series.Where(entry => (string?)entry["chart_type"] == type ||
      (IsLine(entry) && type.EndsWith("column", StringComparison.Ordinal))).ToList();
    if (!ChartTypes.Contains(type))
    {
      findings.Add($"{where}: a {type.Replace('_', ' ')} chart is drawn as clustered columns");
      type = "clustered_column";
    }
    foreach (JsonObject dropped in series.Where(entry => !drawn.Contains(entry)))
    {
      findings.Add($"{where}: a combination chart keeps its {type.Replace('_', ' ')} series and leaves out " +
        $"{dropped["name"]}, a {((string?)dropped["chart_type"])?.Replace('_', ' ')}");
    }
    string family = Family(type);
    // PowerPoint draws a bar chart's first category at the bottom, and a clustered group's first series too, where the
    // format draws them first from the top. The tools read no reversed axis (c:orientation maxMin), so none is assumed.
    if (type == "clustered_bar")
    {
      drawn.Reverse();
    }

    bool Shows(JsonNode? label) => (bool?)label?["show_value"] == true || (bool?)label?["show_percentage"] == true;
    bool Unseen(JsonObject entry)
    {
      JsonNode? point = entry["default_data_point"];
      JsonNode? paint = IsLine(entry) ? point?["border"]?["color"] : point?["fill"] ?? point?["border"]?["color"];
      return paint is JsonValue value && (string?)value == "transparent" &&
        (!IsLine(entry) || (string?)entry["default_marker"]?["marker_type"] is null or "none");
    }
    var totals = type is "stacked_column" or "stacked_bar"
      ? drawn.Where(entry => entry != drawn[0] && Unseen(entry) && Shows(entry["default_data_label"])).ToHashSet()
      : new HashSet<JsonObject>();
    string SeriesType(JsonObject entry) => totals.Contains(entry) ? "total"
      : IsLine(entry) && family != "line" ? "line"
      : family;

    JsonObject data = element["chart_data"]?.AsObject() ?? new JsonObject();
    List<string> categoryKeys = data.Select(pair => pair.Key)
      .OrderBy(key => int.Parse(key[..key.IndexOf(':')], CultureInfo.InvariantCulture)).ToList();
    if (family == "bar")
    {
      categoryKeys.Reverse();
    }
    double? Value(string category, JsonObject entry)
    {
      JsonNode? cell = data[category]!.AsObject()
        .FirstOrDefault(pair => pair.Key[(pair.Key.IndexOf(':') + 1)..] == (string?)entry["name"]).Value;
      return cell is JsonValue value && value.TryGetValue(out double number) ? number : null;
    }

    var properties = new JsonObject
    {
      ["frame"] = Frame(element),
      ["chart"] = stem,
      ["type"] = type,
      ["data"] = $"{stem}.csv"
    };
    JsonObject axisText = Character((element["primary_value_axis"]?["labels_text_style"] ??
      element["primary_category_axis"]?["labels_text_style"] ?? element["legend"]?["text_style"]) as JsonObject, where);
    properties["text.font"] = axisText["font"]?.DeepClone() ?? text["font"]!.DeepClone();
    properties["text.size"] = axisText["size"]?.DeepClone() ?? text["size"]!.DeepClone();
    properties["text.color"] = axisText["color"]?.DeepClone() ?? text["color"]!.DeepClone();
    JsonObject? legend = element["legend"] as JsonObject;
    if (legend is not null && (bool?)legend["is_deleted"] != true)
    {
      string dock = (string?)legend["dock"] ?? "bottom";
      if (dock is not ("bottom" or "top" or "left" or "right"))
      {
        findings.Add($"{where}: a legend at {dock.Replace('_', ' ')} moves to the right, the nearest side the format has");
        dock = "right";
      }
      properties["legend"] = dock;
      int hidden = (legend["legend_entry_list"]?.AsArray() ?? []).Count(entry => (bool?)entry!["delete"] == true &&
        (int?)entry["index"] is int at && at < drawn.Count && !totals.Contains(drawn[at]));
      if (hidden > 0)
      {
        findings.Add($"{where}: a legend entry PowerPoint hides for a series it draws shows");
      }
    }
    if (element["title"]?["text_component"] is JsonNode title && Plain(title) is { Length: > 0 } words)
    {
      properties["title"] = words;
    }

    // PowerPoint draws labels at a position their chart can't place them at its default, as the format does, so
    // the default is written rather than left to a series' position the labels would otherwise inherit. A total's
    // labels take the format's place for a total whatever their position.
    string? Position(JsonNode? label, bool line, bool total = false)
    {
      if (total || (string?)label?["dock"] is not string dock ||
        !LabelPositions.TryGetValue(dock, out string? position))
      {
        return null;
      }
      string kind = line ? "line" : type;
      if (PresentationLoader.LabelPositionsFor(kind).Contains(position))
      {
        return position;
      }
      findings.Add($"{where}: labels {position} on a {kind.Replace('_', ' ')} take its default position, " +
        "as PowerPoint draws them");
      return PresentationLoader.DefaultLabelPosition(PresentationLoader.LabelPositionGroup(kind));
    }

    JsonObject Labels(JsonNode? label, bool line = false, bool total = false)
    {
      var labels = new JsonObject();
      if (!Shows(label))
      {
        return labels;
      }
      labels["data_labels.show"] = (bool?)label!["show_percentage"] == true ? "percent" : "value";
      if (NumberFormat((string?)label["number_format"], where) is string format)
      {
        labels["data_labels.format"] = format;
      }
      if (Position(label, line, total) is string position)
      {
        labels["data_labels.position"] = position;
      }
      JsonObject style = label["default_text_style_for_all_runs"] as JsonObject ?? new JsonObject();
      if (Color(style["font_color"], where) is JsonObject color)
      {
        labels["data_labels.color"] = color;
      }
      if ((bool?)style["is_bold"] == true)
      {
        labels["data_labels.bold"] = true;
      }
      return labels;
    }
    List<JsonObject> main = drawn.Where(entry => SeriesType(entry) == family).ToList();
    // A series showing labels on only some points shows none of its own, so the chart's come from one that shows them.
    JsonObject chartLabels = Labels(main.Select(entry => entry["default_data_label"]).FirstOrDefault(Shows) ??
      main.Select(entry => entry["default_data_label"]).FirstOrDefault(label => label is not null));
    foreach ((string key, JsonNode? value) in chartLabels)
    {
      properties[key] = value?.DeepClone();
    }
    // Labels take the chart's text color unless they have one of their own.
    if (JsonNode.DeepEquals(properties["data_labels.color"], properties["text.color"]))
    {
      properties.Remove("data_labels.color");
    }

    if (family != "pie")
    {
      Axis(properties, "value_axis", element["primary_value_axis"] as JsonObject, where, gridlines: true);
      JsonObject? categoryAxis = element["primary_category_axis"] as JsonObject;
      properties["category_axis.visible"] = (bool?)categoryAxis?["is_deleted"] != true;
      if ((string?)categoryAxis?["label_position"] == "none")
      {
        properties["category_axis.labels"] = false;
      }
      if (categoryAxis?["base_time_unit"] is not null)
      {
        if (DatePattern((string?)categoryAxis["number_format"]) is string pattern)
        {
          properties["category_axis.format"] = FormatName(new JsonObject { ["date"] = pattern }, "date");
          if ((bool?)categoryAxis["is_units_auto"] == false && (double?)categoryAxis["units"] is double units)
          {
            string unit = ((string?)categoryAxis["major_time_unit"] ?? (string)categoryAxis["base_time_unit"]!)
              .TrimEnd('s');
            properties["category_axis.step"] = $"{units} {unit}{(units == 1 ? "" : "s")}";
          }
        }
        else
        {
          findings.Add($"{where}: the date format {categoryAxis["number_format"]} has no pattern in the format, so " +
            "the dates show as written");
        }
      }
      if (drawn.Any(entry => (bool?)entry["uses_secondary_axis"] == true && IsLine(entry)))
      {
        Axis(properties, "secondary_axis", element["secondary_value_axis"] as JsonObject, where, gridlines: false);
      }
    }

    // A pie's points are its slices, which series_colors colors in order.
    var colors = new JsonArray();
    JsonArray points = drawn.FirstOrDefault()?["data_point_list"]?.AsArray() ?? [];
    if (family == "pie" && points.Count > 0)
    {
      for (int slice = 0; slice < categoryKeys.Count; slice++)
      {
        JsonNode? point = points.FirstOrDefault(candidate => (int?)candidate!["index"] == slice);
        colors.Add(Color(point?["fill"], where) ?? new JsonObject { ["theme"] = Accents[slice % 6] });
      }
    }
    else
    {
      if (drawn.Any(entry => (entry["data_point_list"]?.AsArray().Count ?? 0) > 0 &&
        SeriesType(entry) is not ("column" or "bar")))
      {
        findings.Add($"{where}: colors on single points of a line are left out");
      }
      List<JsonObject> colored = drawn.ToList();
      while (colored.Count > 1 && totals.Contains(colored[^1]))
      {
        colored.RemoveAt(colored.Count - 1);
      }
      for (int position = 0; position < colored.Count; position++)
      {
        JsonNode? point = colored[position]["default_data_point"];
        JsonObject? color = totals.Contains(colored[position])
          ? null
          : Color(point?["fill"] ?? point?["border"]?["color"], where);
        colors.Add(color ?? new JsonObject { ["theme"] = Accents[position % 6] });
      }
    }
    properties["series_colors"] = colors;
    if (family is "column" or "bar")
    {
      properties["gap_width"] = (double?)element["gap_width"] ?? 150;
      properties["overlap"] = type.StartsWith("clustered", StringComparison.Ordinal)
        ? (double?)element["overlap"] ?? 0
        : 100;
    }

    JsonObject Markers(JsonObject entry)
    {
      JsonNode? marker = entry["default_marker"];
      string? shape = (string?)marker?["marker_type"];
      if (shape is null or "none" || totals.Contains(entry))
      {
        return new JsonObject { ["markers.shape"] = "none" };
      }
      if (!MarkerShapes.Contains(shape))
      {
        findings.Add($"{where}: {entry["name"]}'s {shape} markers aren't in the format, so its line draws none");
        return new JsonObject { ["markers.shape"] = "none" };
      }
      var markers = new JsonObject
      {
        ["markers.shape"] = shape,
        ["markers.size"] = (double?)marker!["size"] ?? 5
      };
      JsonObject? line = Color(entry["default_data_point"]?["border"]?["color"], where);
      if (Color(marker["marker_shape_style"]?["fill"], where) is JsonObject fill && !JsonNode.DeepEquals(fill, line))
      {
        markers["markers.fill"] = fill;
      }
      return markers;
    }
    // A line's markers of its own, as the keys that differ from its series': its color outlines one, and fills it
    // unless the marker's fill differs.
    JsonObject PointMarkers(JsonObject entry, JsonArray list)
    {
      JsonObject series = Markers(entry);
      JsonObject? line = Color(entry["default_data_point"]?["border"]?["color"], where);
      var points = new JsonObject();
      foreach (JsonNode? marker in list)
      {
        string? key = categoryKeys.FirstOrDefault(candidate =>
          int.Parse(candidate[..candidate.IndexOf(':')], CultureInfo.InvariantCulture) == (int?)marker?["index"]);
        if (key is null)
        {
          continue;
        }
        string prefix = $"points.{key[(key.IndexOf(':') + 1)..]}.";
        JsonObject? color = Color(marker!["marker_shape_style"]?["border_color"], where);
        if (color is not null && !JsonNode.DeepEquals(color, line))
        {
          points[$"{prefix}color"] = color;
        }
        if ((string?)marker["marker_type"] is string shape && shape != (string?)series["markers.shape"])
        {
          if (shape != "none" && !MarkerShapes.Contains(shape))
          {
            findings.Add($"{where}: {entry["name"]}'s {shape} marker isn't in the format, so it takes its line's");
            continue;
          }
          points[$"{prefix}markers.shape"] = shape;
        }
        if ((double?)marker["size"] is double size && size != ((double?)series["markers.size"] ?? 5))
        {
          points[$"{prefix}markers.size"] = size;
        }
        JsonNode? seriesFill = series["markers.fill"] ?? (color is null ? line : null);
        if (Color(marker["marker_shape_style"]?["fill"], where) is JsonObject fill &&
          !JsonNode.DeepEquals(fill, seriesFill ?? color))
        {
          points[$"{prefix}markers.fill"] = fill;
        }
      }
      return points;
    }
    // A series' points' own labels, as the keys that differ from what they'd take: the series' labels, or the chart's
    // where the series shows none. A deleted label is shown none.
    JsonObject PointLabels(JsonObject entry, JsonArray list)
    {
      bool shows = Shows(entry["default_data_label"]);
      JsonObject series = shows ? Labels(entry["default_data_label"], IsLine(entry)) :
        Filter(properties, "data_labels.");
      series.Remove("data_labels.show");
      series.TryAdd("data_labels.color", properties["text.color"]?.DeepClone());
      series.TryAdd("data_labels.bold", false);
      var points = new JsonObject();
      foreach (JsonNode? label in list)
      {
        string? key = categoryKeys.FirstOrDefault(candidate =>
          int.Parse(candidate[..candidate.IndexOf(':')], CultureInfo.InvariantCulture) == (int?)label?["index"]);
        if (key is null)
        {
          continue;
        }
        string prefix = $"points.{key[(key.IndexOf(':') + 1)..]}.data_labels.";
        bool? value = (bool?)label!["show_value"];
        if ((bool?)label["is_deleted"] == true || (shows && value == false))
        {
          if (shows)
          {
            points[$"{prefix}show"] = "none";
          }
          continue;
        }
        if (!shows && value != true)
        {
          continue;
        }
        var own = new JsonObject();
        if (!shows)
        {
          own["data_labels.show"] = "value";
        }
        if (!totals.Contains(entry) && Position(label, IsLine(entry)) is string position)
        {
          own["data_labels.position"] = position;
        }
        if (NumberFormat((string?)label["number_format"], where) is string format)
        {
          own["data_labels.format"] = format;
        }
        JsonNode? style = label["text_component"]?["paragraph_list"]?[0]?["runs"]?[0]?["text_style"];
        if (Color(style?["font_color"], where) is JsonObject color)
        {
          own["data_labels.color"] = color;
        }
        if ((bool?)style?["is_bold"] is bool bold)
        {
          own["data_labels.bold"] = bold;
        }
        foreach ((string name, JsonNode? setting) in own)
        {
          if (!JsonNode.DeepEquals(setting, series[name]))
          {
            points[$"points.{key[(key.IndexOf(':') + 1)..]}.{name}"] = setting?.DeepClone();
          }
        }
      }
      return points;
    }
    static void AddPoints(JsonObject json, JsonObject keys)
    {
      if (json["points"] is not JsonObject points)
      {
        json["points"] = points = new JsonObject();
      }
      foreach ((string key, JsonNode? value) in keys)
      {
        points[key] = value?.DeepClone();
      }
    }
    List<JsonObject> lines = drawn.Where(entry => IsLine(entry) && !totals.Contains(entry)).ToList();
    if (family == "line" || lines.Count > 0)
    {
      JsonObject common = family == "line" && lines.Count > 0
        ? (JsonObject)Mode(lines.Select(entry => ((JsonNode?)Markers(entry), 1.0)))!
        : new JsonObject { ["markers.shape"] = "none" };
      foreach ((string key, JsonNode? value) in common)
      {
        properties[key] = value?.DeepClone();
      }
    }

    var seriesJson = new JsonArray();
    var named = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonObject entry in drawn)
    {
      string seriesType = SeriesType(entry);
      bool secondary = (bool?)entry["uses_secondary_axis"] == true && seriesType == "line";
      if ((bool?)entry["uses_secondary_axis"] == true && !secondary)
      {
        findings.Add($"{where}: {entry["name"]} is on the secondary axis, which only a line takes, and moves to the " +
          "value axis");
      }
      // A series is a column of the chart's CSV, named by its header, so one without a name, or with another's,
      // takes a name of its own.
      string name = ((string?)entry["name"] ?? "").Trim();
      string unique = name.Length > 0 ? name : $"Series {seriesJson.Count + 1}";
      for (int number = 2; !named.Add(unique); number++)
      {
        unique = $"{(name.Length > 0 ? name : "Series")} {number}";
      }
      var json = new JsonObject
      {
        ["name"] = unique,
        ["values"] = new JsonArray(categoryKeys.Select(key => (JsonNode?)Value(key, entry)).ToArray()),
        ["type"] = seriesType,
        ["secondary"] = secondary
      };
      JsonObject own = Labels(entry["default_data_label"], IsLine(entry), totals.Contains(entry));
      bool typed = seriesType != family;
      if (typed || !JsonNode.DeepEquals(own, chartLabels))
      {
        if (seriesType == "total")
        {
          own.Remove("data_labels.position");
          own.Remove("data_labels.color");
          own["data_labels.show"] ??= "value";
        }
        json["labels"] = own;
      }
      if (seriesType == "line" && (typed || family == "line"))
      {
        JsonObject markers = Markers(entry);
        bool chartMarkers = family == "line" && JsonNode.DeepEquals(markers, Filter(properties, "markers."));
        if (typed || !chartMarkers)
        {
          json["markers"] = markers;
          json["labels"] ??= own;
        }
      }
      if (seriesType == "line" && entry["marker_list"] is JsonArray { Count: > 0 } markerList &&
        PointMarkers(entry, markerList) is { Count: > 0 } pointMarkers)
      {
        json["points"] = pointMarkers;
        json["labels"] ??= own;
      }
      if (family != "pie" && entry["data_label_list"] is JsonArray { Count: > 0 } labelList &&
        PointLabels(entry, labelList) is { Count: > 0 } pointLabels)
      {
        AddPoints(json, pointLabels);
        json["labels"] ??= own;
      }
      if (SeriesType(entry) is "column" or "bar" && entry["data_point_list"] is JsonArray { Count: > 0 } list)
      {
        JsonObject? color = Color(entry["default_data_point"]?["fill"], where);
        var colored = new JsonObject();
        foreach (JsonNode? point in list)
        {
          string? key = categoryKeys.FirstOrDefault(candidate =>
            int.Parse(candidate[..candidate.IndexOf(':')], CultureInfo.InvariantCulture) == (int?)point?["index"]);
          if (key is not null && Color(point?["fill"], where) is JsonObject fill && !JsonNode.DeepEquals(fill, color))
          {
            colored[$"points.{key[(key.IndexOf(':') + 1)..]}.color"] = fill;
          }
        }
        if (colored.Count > 0)
        {
          AddPoints(json, colored);
          json["labels"] ??= own;
        }
      }
      // A series with keys of its own carries its markers too, or it would read as having none.
      if (json["labels"] is not null && seriesType == "line")
      {
        json["markers"] ??= Markers(entry);
      }
      seriesJson.Add(json);
    }
    properties["categories"] = new JsonArray(categoryKeys.Select(key => (JsonNode)key[(key.IndexOf(':') + 1)..])
      .ToArray());
    properties["series"] = seriesJson;
    FixAxes(properties, type);
    return Item(id, "chart", properties, null);
  }

  /// <summary>Fixes each axis as PowerPoint draws it, estimating what it leaves automatic, then keeps the fewest of
  /// min, max, and step that Pintura's own scaling draws the same axis from.</summary>
  public static void FixAxes(JsonObject properties, string type)
  {
    var chart = new Chart
    {
      Type = type,
      Categories = properties["categories"]!.AsArray().Select(category => (string)category!).ToList(),
      Series = properties["series"]!.AsArray().Select(series => new ChartSeries
      {
        Name = (string)series!["name"]!,
        Values = series["values"]!.AsArray().Select(value => (double?)value).ToList()
      }).ToList()
    };
    foreach (JsonNode? series in properties["series"]!.AsArray())
    {
      chart.SeriesOptions[(string)series!["name"]!] = new ChartSeriesOptions
      {
        Type = (string?)series["type"],
        Axis = (bool?)series["secondary"] == true ? "secondary" : null
      };
    }
    string[] keys = ["min", "max", "step"];
    foreach ((string prefix, bool secondary) in new[] { ("value_axis", false), ("secondary_axis", true) })
    {
      if (!properties.ContainsKey($"{prefix}.visible"))
      {
        continue;
      }
      double?[] fixes = keys.Select(key => (double?)properties[$"{prefix}.{key}"]).ToArray();
      Scale target = AxisScale.PowerPoint(chart, secondary, fixes[0], fixes[1], fixes[2]);
      double[] written = [target.Min, target.Max, target.Step];
      double? Kept(int mask, int key) => (mask & (1 << key)) != 0 ? written[key] : null;
      Scale Drawn(int mask) => AxisScale.Of(chart, secondary, Kept(mask, 0), Kept(mask, 1), Kept(mask, 2));
      int fewest = Enumerable.Range(0, 8).OrderBy(mask => BitOperations.PopCount((uint)mask))
        .First(mask => Drawn(mask) == target);
      for (int key = 0; key < keys.Length; key++)
      {
        if ((fewest & (1 << key)) == 0)
        {
          properties.Remove($"{prefix}.{keys[key]}");
        }
        else
        {
          properties[$"{prefix}.{keys[key]}"] = written[key];
        }
      }
    }
  }

  private static JsonObject Filter(JsonObject properties, string prefix)
  {
    return new JsonObject(properties.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
      .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
  }

  private void Axis(JsonObject properties, string prefix, JsonObject? axis, string where, bool gridlines)
  {
    // A value axis draws only its labels, so one with no label position is as good as deleted.
    properties[$"{prefix}.visible"] = axis is not null && (bool?)axis["is_deleted"] != true &&
      (string?)axis["label_position"] != "none";
    if (axis is null)
    {
      return;
    }
    foreach ((string read, string auto, string key) in new[] { ("min", "is_min_auto", "min"),
      ("max", "is_max_auto", "max"), ("units", "is_units_auto", "step") })
    {
      if ((bool?)axis[auto] == false && (double?)axis[read] is double value)
      {
        properties[$"{prefix}.{key}"] = value;
      }
    }
    if (NumberFormat((string?)axis["number_format"], where) is string format)
    {
      properties[$"{prefix}.format"] = format;
    }
    if (gridlines)
    {
      properties[$"{prefix}.gridlines"] = axis["major_gridlines"] is not null;
    }
  }

  #endregion

  #region Number formats

  /// <summary>An Excel number code as a named format, or null for <c>General</c> or a code the format can't spell.
  /// </summary>
  private string? NumberFormat(string? code, string where)
  {
    if (code is null or "General")
    {
      return null;
    }
    string[] sections = code.Split(';');
    string positive = sections[0];
    string suffix = string.Concat(Quoted().Matches(positive).Select(match => match.Groups[1].Value));
    string prefix = "";
    int quote = positive.IndexOf('"');
    int digit = positive.IndexOfAny(['#', '0']);
    if (quote >= 0 && quote < digit)
    {
      Match first = Quoted().Match(positive);
      prefix = first.Groups[1].Value;
      suffix = string.Concat(Quoted().Matches(positive).Skip(1).Select(match => match.Groups[1].Value));
    }
    string bare = Quoted().Replace(positive, "").Replace("_)", "").Replace("\\)", "").Replace("\\(", "")
      .Replace("\\", "").Replace("(", "").Replace(")", "");
    if (bare.StartsWith('$'))
    {
      prefix = "$" + prefix;
      bare = bare[1..];
    }
    if (!NumberCode().IsMatch(bare))
    {
      findings.Add($"{where}: the number format {code} has no match in the format, so its numbers show unformatted");
      return null;
    }
    // Each comma after the last digit divides by a thousand.
    int thousands = bare.Length - bare.TrimEnd(',').Length;
    bare = bare.TrimEnd(',');
    int decimals = bare.Contains('.') ? bare[(bare.IndexOf('.') + 1)..].Count(character => character == '0') : 0;
    bool percent = bare.Contains('%');
    var format = new JsonObject();
    double scale = (percent ? 100 : 1) / Math.Pow(1000, thousands);
    if (scale != 1)
    {
      format["scale"] = scale;
    }
    format["decimals"] = decimals;
    format["separator"] = bare.Contains(',');
    if (prefix.Length > 0)
    {
      format["prefix"] = prefix;
    }
    if (percent || suffix.Length > 0)
    {
      format["suffix"] = percent ? "%" + suffix : suffix;
    }
    format["negative"] = sections.Length > 1 && sections[1].Contains('(') ? "parentheses" : "minus";
    return FormatName(format, Describe(format));
  }

  /// <summary>A name for a format from what it spells, such as <c>percent</c> or <c>dollars_billions_1</c>.</summary>
  private static string Describe(JsonObject format)
  {
    var words = new List<string>();
    string prefix = (string?)format["prefix"] ?? "";
    string suffix = (string?)format["suffix"] ?? "";
    words.Add(prefix switch { "$" => "dollars", "€" => "euros", "£" => "pounds", "¥" => "yen", _ => "" });
    words.Add(suffix.TrimStart('%').Trim() switch
    {
      "B" or "bn" => "billions", "M" or "m" or "mm" => "millions", "K" or "k" => "thousands", "x" => "multiple",
      _ => ""
    });
    if (suffix.StartsWith('%'))
    {
      words.Add("percent");
    }
    words.RemoveAll(word => word.Length == 0);
    if (words.Count == 0)
    {
      words.Add("number");
    }
    if ((int?)format["decimals"] is int decimals and > 0)
    {
      words.Add(decimals.ToString(CultureInfo.InvariantCulture));
    }
    return string.Join("_", words);
  }

  private string FormatName(JsonObject format, string hint)
  {
    foreach ((string known, JsonObject existing) in formats)
    {
      if (JsonNode.DeepEquals(existing, format))
      {
        return known;
      }
    }
    string name = Unique(hint, formats.Keys.ToHashSet(), "_");
    formats[name] = format;
    return name;
  }

  private static readonly Dictionary<string, string> DateTokens = new Dictionary<string, string>
  {
    ["yyyy"] = "YYYY", ["yy"] = "YY", ["mmmm"] = "MMMM", ["mmm"] = "MMM", ["mm"] = "MM", ["m"] = "M", ["dd"] = "DD",
    ["d"] = "D"
  };

  /// <summary>An Excel date code as a date pattern, or null when it has a time, a weekday, or a literal Y, M, or D.
  /// </summary>
  public static string? DatePattern(string? code)
  {
    if (code is null or "General")
    {
      return null;
    }
    var pattern = new System.Text.StringBuilder();
    for (int index = 0; index < code.Length;)
    {
      string rest = code[index..];
      Match escaped = Escaped().Match(rest);
      Match token = DateToken().Match(rest);
      if (escaped.Success)
      {
        string literal = escaped.Groups[1].Success ? escaped.Groups[1].Value : escaped.Groups[2].Value;
        if (literal.IndexOfAny(['Y', 'M', 'D']) >= 0)
        {
          return null;
        }
        pattern.Append(literal);
        index += escaped.Length;
      }
      else if (token.Success)
      {
        if (!DateTokens.TryGetValue(token.Value.ToLowerInvariant(), out string? mapped))
        {
          return null;
        }
        pattern.Append(mapped);
        index += token.Length;
      }
      else if ("-/ ,.'".Contains(rest[0]))
      {
        pattern.Append(rest[0]);
        index++;
      }
      else
      {
        return null;
      }
    }
    string result = pattern.ToString();
    return result.IndexOfAny(['Y', 'M', 'D']) >= 0 ? result : null;
  }

  /// <summary>A cell's text as a number and the format that spells it back exactly, or null when none can.</summary>
  public static (double Value, JsonObject Format)? ReadNumber(string text)
  {
    Match match = Spelled().Match(text);
    if (!match.Success || match.Groups["open"].Success != match.Groups["close"].Success ||
      (match.Groups["open"].Success && match.Groups["minus"].Success))
    {
      return null;
    }
    string whole = match.Groups["whole"].Value;
    string fraction = match.Groups["fraction"].Value;
    string suffix = match.Groups["suffix"].Value;
    string digits = whole.Replace(",", "");
    bool negative = match.Groups["open"].Success || match.Groups["minus"].Success;
    bool percent = suffix == "%";
    double signed = double.Parse($"{(negative ? "-" : "")}{digits}.{(fraction.Length > 0 ? fraction : "0")}",
      CultureInfo.InvariantCulture);
    double value = percent ? Math.Round(signed / 100, fraction.Length + 2) : signed;
    var format = new JsonObject();
    if (percent)
    {
      format["scale"] = 100.0;
    }
    format["decimals"] = fraction.Length;
    format["separator"] = whole.Contains(',') || digits.Length <= 3;
    if (match.Groups["prefix"].Value.Length > 0)
    {
      format["prefix"] = match.Groups["prefix"].Value;
    }
    if (suffix.Length > 0)
    {
      format["suffix"] = suffix;
    }
    format["negative"] = match.Groups["minus"].Success ? "minus" : "parentheses";
    var spelled = new NumberFormat
    {
      Scale = (double?)format["scale"],
      Decimals = (int)format["decimals"]!,
      Separator = (bool)format["separator"]!,
      Prefix = (string?)format["prefix"],
      Suffix = (string?)format["suffix"],
      Negative = (string)format["negative"]!
    };
    return spelled.Spell(value) == text ? (value, format) : null;
  }

  [GeneratedRegex("\"([^\"]*)\"")]
  private static partial Regex Quoted();

  [GeneratedRegex(@"^[#,0.%]+$")]
  private static partial Regex NumberCode();

  [GeneratedRegex(@"^(?:\\(.)|""([^""]*)"")")]
  private static partial Regex Escaped();

  [GeneratedRegex("^(y+|m+|d+)", RegexOptions.IgnoreCase)]
  private static partial Regex DateToken();

  [GeneratedRegex(@"^(?<open>\()?(?<minus>-)?(?<prefix>[$€£¥]?)(?<whole>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<fraction>\d+))?(?<suffix>%|x|K|M|B)?(?<close>\))?$")]
  private static partial Regex Spelled();

  #endregion

  #region Tables

  /// <summary>PowerPoint marks only the first row a header, so the header runs on through the rows a cell of the first
  /// spans, and those filled as the first is.</summary>
  private static int HeaderRows(JsonObject element, List<JsonObject> rows)
  {
    List<JsonObject> Cells(JsonObject row) => (row["cells"]?.AsArray() ?? []).Select(node => node!.AsObject())
      .Where(cell => (bool?)cell["is_covered"] != true).ToList();
    if (rows.Count == 0)
    {
      return 0;
    }
    int count = Cells(rows[0]).Select(cell => Math.Max(1, (int?)cell["row_span"] ?? 1)).DefaultIfEmpty(1).Max();
    JsonNode? fill = Cells(rows[0]).Select(cell => CellFill(element, rows[0], cell)).FirstOrDefault();
    bool filled = fill is JsonObject;
    while (filled && count < rows.Count - 1 && Cells(rows[count]).Count > 0 &&
      Cells(rows[count]).All(cell => JsonNode.DeepEquals(CellFill(element, rows[count], cell), fill)))
    {
      count++;
    }
    return Math.Min(count, rows.Count);
  }

  private static JsonNode? CellFill(JsonObject element, JsonObject row, JsonObject cell)
  {
    return cell["style"]?["fill"] ?? cell["fill"] ?? row["default_cell_fill"] ?? element["default_cell_fill"];
  }

  private JsonObject Table(JsonObject element, string slide, Ids ids, string where)
  {
    string id = Id(element, "table", ids, where);
    string stem = Unique(slide, tableStems);
    var tableLayers = new Layers(element["default_text_style"], element["default_paragraph_style"],
      element["default_bullet"]);
    List<JsonObject> rows = (element["rows"]?.AsArray() ?? []).Select(node => node!.AsObject()).ToList();
    List<JsonObject> columns = (element["columns"]?.AsArray() ?? []).Select(node => node!.AsObject()).ToList();
    var properties = new JsonObject
    {
      ["frame"] = Frame(element),
      ["table"] = stem,
      ["data"] = $"{stem}.csv"
    };
    int headerRows = (bool?)element["properties"]?["first_row"] == true ? HeaderRows(element, rows) : 0;
    if (headerRows > 0)
    {
      properties["header_rows"] = headerRows;
    }
    foreach ((string key, JsonNode? value) in text)
    {
      properties[key] = value?.DeepClone();
    }
    properties["widths"] = new JsonArray(columns.Select(column => (JsonNode)Round((double)column["width"]!)).ToArray());

    var cells = new JsonArray();
    var heights = new JsonArray();
    var numbers = new List<(int Column, JsonObject Properties, double Value, JsonObject Format)>();
    foreach (JsonObject row in rows)
    {
      int number = (int)row["index"]! + 1;
      foreach (JsonObject cell in row["cells"]?.AsArray().Select(node => node!.AsObject()) ?? [])
      {
        if ((bool?)cell["is_covered"] == true)
        {
          continue;
        }
        int column = (int)cell["index"]! + 1;
        JsonNode component = cell["text_component"] ?? new JsonObject
        {
          ["paragraph_list"] = new JsonArray(new JsonObject
          {
            ["runs"] = new JsonArray(new JsonObject { ["text"] = (string?)cell["text"] ?? "" })
          })
        };
        var layers = new Layers(Merge(tableLayers.Text, row["default_text_style"], cell["default_text_style"]),
          Merge(tableLayers.Paragraph, row["default_paragraph_style"], cell["default_paragraph_style"]),
          tableLayers.Bullet);
        List<Paragraph> paragraphs = Expand(component, layers, where);
        if (paragraphs.Count == 0)
        {
          paragraphs.Add(new Paragraph(0, 0, null, false, 0, 0, null, null,
            Character(layers.Text as JsonObject, where), []));
          foreach ((string key, JsonNode? value) in ParagraphProperties(layers.Paragraph as JsonObject))
          {
            paragraphs[0].Properties[key] = value?.DeepClone();
          }
        }
        JsonObject cellProperties = new JsonObject();
        JsonObject character = paragraphs.SelectMany(paragraph => paragraph.Runs).Any(run => run.Text.Trim().Length > 0)
          ? Common(paragraphs.SelectMany(paragraph => paragraph.Runs))
          : paragraphs[0].Properties;
        foreach (string key in TextProperties.Character.Where(character.ContainsKey))
        {
          cellProperties[key] = character[key]!.DeepClone();
        }
        if (paragraphs[0].Properties["align"] is JsonNode align)
        {
          cellProperties["align"] = align.DeepClone();
        }
        JsonObject box = BoxProperties(Merge(element["default_text_layout"], row["default_text_layout"],
          component["text_layout"]), null, null);
        foreach ((string key, JsonNode? value) in box)
        {
          cellProperties[key] = value?.DeepClone();
        }
        if (Color(CellFill(element, row, cell), where) is JsonObject fill && fill["none"] is null)
        {
          cellProperties["fill"] = fill;
        }
        JsonObject borders = Merge(row["default_borders"], cell["style"]?["borders"], cell["borders"]);
        foreach (string side in new[] { "top", "bottom", "left", "right" })
        {
          if (borders[side] is JsonObject border && Stroke(new JsonObject
            {
              ["border_color"] = border["color"]?.DeepClone(), ["border_weight"] = border["weight"]?.DeepClone(),
              ["border_dash_type"] = border["dash_type"]?.DeepClone()
            }, where) is JsonObject stroke && stroke["stroke"] is null)
          {
            foreach ((string part, JsonNode? value) in stroke)
            {
              cellProperties[$"border_{side}{part["stroke".Length..]}"] = value?.DeepClone();
            }
          }
        }
        string cellText = string.Join(" ", paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run =>
          run.Text))));
        if (number > headerRows && ReadNumber(cellText.Trim()) is (double spelled, JsonObject format))
        {
          numbers.Add((column, cellProperties, spelled, format));
        }
        cells.Add(new JsonObject
        {
          ["row"] = number,
          ["column"] = column,
          ["rows"] = Math.Max(1, (int?)cell["row_span"] ?? 1),
          ["columns"] = Math.Max(1, (int?)cell["column_span"] ?? (int?)cell["grid_span"] ?? 1),
          ["properties"] = cellProperties,
          ["paragraphs"] = Paragraphs(paragraphs, where)
        });
      }
      double height = (double?)row["height"] ?? 0;
      // The tools' content height includes the cells' insets.
      double content = (double?)row["content_height"] ?? height;
      heights.Add(height - content > 1 ? Round(height) : null);
    }
    properties["heights"] = heights;
    properties["cells"] = cells;
    SharedRules(cells, headerRows);
    NameFormats(numbers);
    return Item(id, "table", properties, null);
  }

  /// <summary>PowerPoint keeps a rule between two cells on both, so a rule between rows stays on the one whose row shows
  /// more: its other rule, bold, or being the header; the lower one when they show as much, as a rule above a total. A
  /// rule between columns stays on the left one, as its right border.</summary>
  private static void SharedRules(JsonArray cells, int headerRows)
  {
    List<JsonObject> all = cells.Select(node => node!.AsObject()).ToList();
    string? Rule(JsonObject cell, string side)
    {
      JsonObject properties = cell["properties"]!.AsObject();
      string[] keys = properties.Select(pair => pair.Key).Where(key => key.StartsWith(side, StringComparison.Ordinal))
        .ToArray();
      return keys.Length == 0 ? null
        : string.Join(";", keys.Order().Select(key => $"{key[side.Length..]}={properties[key]!.ToJsonString()}"));
    }
    int Weight(JsonObject cell)
    {
      JsonObject properties = cell["properties"]!.AsObject();
      return (Rule(cell, "border_top") is null ? 0 : 1) + (Rule(cell, "border_bottom") is null ? 0 : 1) +
        ((bool?)properties["bold"] == true ? 1 : 0) + ((int)cell["row"]! <= headerRows ? 1 : 0);
    }
    var drops = new List<(JsonObject Cell, string Side)>();
    var shared = new List<(int Below, JsonObject Upper, JsonObject Lower)>();
    foreach (JsonObject upper in all)
    {
      int below = (int)upper["row"]! + (int)upper["rows"]!;
      int column = (int)upper["column"]!;
      JsonObject? lower = all.FirstOrDefault(cell => (int)cell["row"]! == below && (int)cell["column"]! == column &&
        (int)cell["columns"]! == (int)upper["columns"]!);
      if (lower is not null && Rule(upper, "border_bottom") is string rule && Rule(lower, "border_top") == rule)
      {
        shared.Add((below, upper, lower));
      }
    }
    // One side for every rule along a boundary, so a rule across the row stays the row's.
    foreach (IGrouping<int, (int Below, JsonObject Upper, JsonObject Lower)> boundary in shared.GroupBy(pair =>
      pair.Below))
    {
      bool lower = boundary.Sum(pair => Weight(pair.Lower)) >= boundary.Sum(pair => Weight(pair.Upper));
      drops.AddRange(boundary.Select(pair => lower ? (pair.Upper, "border_bottom") : (pair.Lower, "border_top")));
    }
    foreach (JsonObject left in all)
    {
      int beside = (int)left["column"]! + (int)left["columns"]!;
      int row = (int)left["row"]!;
      JsonObject? right = all.FirstOrDefault(cell => (int)cell["column"]! == beside && (int)cell["row"]! == row &&
        (int)cell["rows"]! == (int)left["rows"]!);
      if (right is not null && Rule(left, "border_right") is string rule && Rule(right, "border_left") == rule)
      {
        drops.Add((right, "border_left"));
      }
    }
    foreach ((JsonObject cell, string side) in drops)
    {
      JsonObject properties = cell["properties"]!.AsObject();
      foreach (string key in properties.Select(pair => pair.Key).Where(key => key.StartsWith(side,
        StringComparison.Ordinal)).ToList())
      {
        properties.Remove(key);
      }
    }
  }

  /// <summary>Names a table's number formats. A positive number doesn't show how its format spells a negative, so it
  /// takes what its column's negatives show, or the table's; and negatives in a color of their own, where the
  /// positives beside them have another, give their format that color.</summary>
  private void NameFormats(List<(int Column, JsonObject Properties, double Value, JsonObject Format)> numbers)
  {
    string Shape(JsonObject format) => new JsonObject(format.Where(pair => pair.Key != "negative")
      .Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))).ToJsonString();
    var negatives = numbers.Where(number => number.Value < 0).ToList();
    foreach (var number in numbers.Where(number => number.Value >= 0))
    {
      string? shown = (string?)(negatives.FirstOrDefault(other => other.Column == number.Column &&
          Shape(other.Format) == Shape(number.Format)).Format?["negative"] ??
        negatives.FirstOrDefault(other => Shape(other.Format) == Shape(number.Format)).Format?["negative"] ??
        negatives.FirstOrDefault().Format?["negative"]);
      number.Format["negative"] = shown ?? "parentheses";
    }
    List<string?> negativeColors = negatives.Select(number => number.Properties["color"]?.ToJsonString())
      .Distinct().ToList();
    var negativeColumns = negatives.Select(number => number.Column).ToHashSet();
    bool colored = negativeColors is [string color] && numbers.Where(number => number.Value >= 0 &&
      negativeColumns.Contains(number.Column)).All(number => number.Properties["color"]?.ToJsonString() != color);
    var reddened = negatives.Select(negative => negative.Format.ToJsonString()).ToHashSet();
    foreach (var number in numbers)
    {
      JsonObject format = number.Format;
      if (colored && reddened.Contains(format.ToJsonString()))
      {
        format["negativeColor"] = negatives[0].Properties["color"]!.DeepClone();
      }
      number.Properties["format"] = FormatName(format, Describe(format));
    }
  }

  #endregion

  #region The package

  /// <summary>The <c>.pptx</c> itself, for what the tools' reads leave out: where a picture's image is, and sections.
  /// </summary>
  private sealed class Package : IDisposable
  {
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive zip;
    private readonly string media;
    private readonly HashSet<string> written = new HashSet<string>();
    private readonly Dictionary<string, string> copied = new Dictionary<string, string>();
    private readonly Dictionary<string, XDocument> documents = new Dictionary<string, XDocument>();

    public Package(string deck, string media)
    {
      zip = ZipFile.OpenRead(deck);
      this.media = media;
      Directory.CreateDirectory(media);
    }

    public void Dispose()
    {
      zip.Dispose();
    }

    private XDocument? Xml(string part)
    {
      if (documents.TryGetValue(part, out XDocument? known))
      {
        return known;
      }
      ZipArchiveEntry? entry = zip.GetEntry(part);
      if (entry is null)
      {
        return null;
      }
      using Stream stream = entry.Open();
      return documents[part] = XDocument.Load(stream);
    }

    /// <summary>The name PowerPoint shows in its selection pane for the element with <paramref name="id"/>.</summary>
    public string? Name(string where, int? id)
    {
      return id is not null && Part(where) is string part
        ? (string?)Xml(part)?.Descendants().FirstOrDefault(element => element.Name.LocalName == "cNvPr" &&
          (int?)element.Attribute("id") == id)?.Attribute("name")
        : null;
    }

    /// <summary>For each paragraph of the element with <paramref name="id"/>, how many runs it has and which of them
    /// follow a line break, or null when it has no break.</summary>
    public List<(int Runs, HashSet<int> Broken)>? Breaks(string where, int? id)
    {
      XElement? body = id is not null && Part(where) is string part
        ? Xml(part)?.Descendants(P + "cNvPr").FirstOrDefault(element => (int?)element.Attribute("id") == id)
          ?.Parent?.Parent?.Element(P + "txBody")
        : null;
      if (body is null || !body.Descendants(A + "br").Any())
      {
        return null;
      }
      return body.Elements(A + "p").Select(paragraph =>
      {
        var broken = new HashSet<int>();
        int runs = 0;
        foreach (XElement child in paragraph.Elements())
        {
          if (child.Name == A + "br")
          {
            broken.Add(runs);
          }
          else if (child.Name == A + "r" || child.Name == A + "fld")
          {
            runs++;
          }
        }
        return (runs, broken);
      }).ToList();
    }

    /// <summary>Each relationship of a part, by id, as a path in the package.</summary>
    private Dictionary<string, string> Relationships(string part)
    {
      string folder = Path.GetDirectoryName(part)!.Replace('\\', '/');
      string rels = $"{folder}/_rels/{Path.GetFileName(part)}.rels";
      return (Xml(rels)?.Root?.Elements(Rels + "Relationship") ?? [])
        .Where(relationship => (string?)relationship.Attribute("TargetMode") != "External")
        .ToDictionary(relationship => (string)relationship.Attribute("Id")!, relationship =>
          Resolve(folder, (string)relationship.Attribute("Target")!));
    }

    private static string Resolve(string folder, string target)
    {
      if (target.StartsWith('/'))
      {
        return target[1..];
      }
      var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
      foreach (string piece in target.Split('/'))
      {
        if (piece == "..")
        {
          parts.RemoveAt(parts.Count - 1);
        }
        else if (piece != ".")
        {
          parts.Add(piece);
        }
      }
      return string.Join("/", parts);
    }

    private List<string> SlideParts()
    {
      Dictionary<string, string> relationships = Relationships("ppt/presentation.xml");
      return (Xml("ppt/presentation.xml")?.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        .Select(slide => relationships.GetValueOrDefault((string)slide.Attribute(R + "id")!) ?? "")
        .ToList();
    }

    /// <summary>The part a read's <paramref name="where"/> names: a slide by its number, or a layout by its name.
    /// </summary>
    private string? Part(string where)
    {
      if (where.StartsWith("slide ", StringComparison.Ordinal))
      {
        int number = int.Parse(where["slide ".Length..], CultureInfo.InvariantCulture);
        List<string> slides = SlideParts();
        return number <= slides.Count ? slides[number - 1] : null;
      }
      string layout = where["layout ".Length..];
      return zip.Entries.Select(entry => entry.FullName)
        .Where(part => part.StartsWith("ppt/slideLayouts/", StringComparison.Ordinal) && part.EndsWith(".xml") &&
          !part.Contains("/_rels/"))
        .FirstOrDefault(part => (string?)Xml(part)?.Root?.Element(P + "cSld")?.Attribute("name") == layout);
    }

    /// <summary>Copies the image of the picture with <paramref name="id"/> into the media folder, the SVG where there
    /// is one, returning its file name there, named <paramref name="stem"/> when given. An image several pictures
    /// show is copied once.</summary>
    public string? Picture(string where, int? id, string? stem)
    {
      if (id is null || Part(where) is not string part || Xml(part) is not XDocument document)
      {
        return null;
      }
      XElement? picture = document.Descendants(P + "pic").FirstOrDefault(candidate =>
        (int?)candidate.Element(P + "nvPicPr")?.Element(P + "cNvPr")?.Attribute("id") == id);
      XElement? blip = picture?.Descendants(A + "blip").FirstOrDefault();
      if (blip is null)
      {
        return null;
      }
      string? embed = blip.Descendants().FirstOrDefault(element => element.Name.LocalName == "svgBlip")
        ?.Attribute(R + "embed")?.Value ?? (string?)blip.Attribute(R + "embed");
      if (embed is null || !Relationships(part).TryGetValue(embed, out string? image) ||
        zip.GetEntry(image) is not ZipArchiveEntry entry)
      {
        return null;
      }
      string hash;
      using (Stream stream = entry.Open())
      {
        hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
      }
      if (copied.TryGetValue(hash, out string? known))
      {
        return known;
      }
      string extension = Path.GetExtension(image);
      string name = stem ?? Path.GetFileNameWithoutExtension(image);
      string file = name + extension;
      for (int suffix = 2; written.Contains(file); suffix++)
      {
        file = $"{name}-{suffix}{extension}";
      }
      written.Add(file);
      entry.ExtractToFile(Path.Combine(media, file), true);
      copied[hash] = file;
      return file;
    }

    /// <summary>Each slide's section, by slide number, from PowerPoint's section list.</summary>
    public Dictionary<int, string> Sections()
    {
      XDocument? presentation = Xml("ppt/presentation.xml");
      List<string> ids = (presentation?.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        .Select(slide => (string)slide.Attribute("id")!).ToList();
      var sections = new Dictionary<int, string>();
      foreach (XElement section in presentation?.Descendants(P14 + "section") ?? [])
      {
        foreach (XElement slide in section.Descendants(P14 + "sldId"))
        {
          int index = ids.IndexOf((string)slide.Attribute("id")!);
          if (index >= 0)
          {
            sections[index + 1] = (string)section.Attribute("name")!;
          }
        }
      }
      return sections;
    }


  }

  #endregion

}