using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>Notes how export and import work around gaps in editide's tools so each workaround can be dropped as
/// editide lands its fix. Release builds compile every call out, along with its text.</summary>
public sealed class Workarounds
{
  private readonly SortedSet<string> notes = new SortedSet<string>(StringComparer.Ordinal);

  public IReadOnlyCollection<string> Notes => notes;

  [Conditional("DEBUG")]
  public void Add(string note)
  {
    notes.Add(note);
  }

  [Conditional("DEBUG")]
  public void AddRange(IEnumerable<string> items)
  {
    notes.UnionWith(items);
  }


}

/// <summary>Two-way vocabulary shared between <see cref="Exporter"/> and <see cref="PowerPointMapping"/> so every
/// name, enum, and key format between Pintura's model and editide's PowerPoint tools is defined once.</summary>
public static partial class PowerPointVocabulary
{
  private static readonly (string Model, string Tool)[] SchemePairs =
  [
    ("dark1", "dk1"),
    ("light1", "lt1"),
    ("dark2", "dk2"),
    ("light2", "lt2"),
    ("accent1", "accent1"),
    ("accent2", "accent2"),
    ("accent3", "accent3"),
    ("accent4", "accent4"),
    ("accent5", "accent5"),
    ("accent6", "accent6"),
    ("hyperlink", "hlink"),
    ("followed_hyperlink", "fol_hlink")
  ];

  /// <summary>Model theme color name to editide tool scheme name (e.g. <c>dark1</c> → <c>dk1</c>).</summary>
  public static readonly Dictionary<string, string> ModelToToolScheme =
    SchemePairs.ToDictionary(pair => pair.Model, pair => pair.Tool, StringComparer.Ordinal);

  /// <summary>Tool scheme name to model theme color name, including PowerPoint's aliases (<c>tx1</c>, <c>bg1</c>,
  /// <c>tx2</c>, <c>bg2</c>, <c>folHlink</c>).</summary>
  public static readonly Dictionary<string, string> ToolToModelScheme = new(StringComparer.Ordinal)
  {
    ["dk1"] = "dark1", ["lt1"] = "light1", ["dk2"] = "dark2", ["lt2"] = "light2",
    ["tx1"] = "dark1", ["bg1"] = "light1", ["tx2"] = "dark2", ["bg2"] = "light2",
    ["accent1"] = "accent1", ["accent2"] = "accent2", ["accent3"] = "accent3",
    ["accent4"] = "accent4", ["accent5"] = "accent5", ["accent6"] = "accent6",
    ["hlink"] = "hyperlink", ["folHlink"] = "followed_hyperlink", ["fol_hlink"] = "followed_hyperlink"
  };

  /// <summary>The twelve OOXML theme color slots in order, using <c>folHlink</c> as <c>edit_theme</c> expects.
  /// </summary>
  public static readonly string[] ThemeColors =
    ["dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];

  public static readonly string[] Accents = ["accent1", "accent2", "accent3", "accent4", "accent5", "accent6"];

  public static string ToThemeColorSlot(string modelName)
  {
    return modelName == "followed_hyperlink" ? "folHlink" : ModelToToolScheme.GetValueOrDefault(modelName, modelName);
  }

  private static readonly (string Model, string Tool)[] DockPairs =
  [
    ("center", "center"),
    ("inside", "inside_end"),
    ("inside_base", "inside_base"),
    ("outside", "outside_end"),
    ("above", "above"),
    ("below", "below"),
    ("left", "left"),
    ("right", "right")
  ];

  public static readonly Dictionary<string, string> ModelToDock =
    DockPairs.ToDictionary(pair => pair.Model, pair => pair.Tool, StringComparer.Ordinal);

  public static readonly Dictionary<string, string> DockToModel = new(
    DockPairs.Select(pair => KeyValuePair.Create(pair.Tool, pair.Model))
      .Concat([KeyValuePair.Create("top", "above"), KeyValuePair.Create("bottom", "below")]),
    StringComparer.Ordinal);

  private static readonly (string Model, string Tool)[] AutofitPairs =
  [
    ("none", "none"),
    ("grow", "resize"),
    ("shrink", "shrink")
  ];

  public static readonly Dictionary<string, string> ModelToAutofit =
    AutofitPairs.ToDictionary(pair => pair.Model, pair => pair.Tool, StringComparer.Ordinal);

  public static string? AutofitToModel(string? autofit)
  {
    return autofit switch
    {
      "shrink" => "shrink",
      "resize" => "grow",
      _ => null
    };
  }

  private static readonly (string Pattern, string Excel)[] DateCodePairs =
  [
    ("YYYY", "yyyy"),
    ("YY", "yy"),
    ("MMMM", "mmmm"),
    ("MMM", "mmm"),
    ("MM", "mm"),
    ("M", "m"),
    ("DD", "dd"),
    ("D", "d")
  ];

  public static readonly Dictionary<string, string> PatternToExcelDate =
    DateCodePairs.ToDictionary(pair => pair.Pattern, pair => pair.Excel, StringComparer.Ordinal);

  public static readonly Dictionary<string, string> ExcelDateToPattern =
    DateCodePairs.ToDictionary(pair => pair.Excel, pair => pair.Pattern, StringComparer.Ordinal);

  public static readonly Dictionary<string, int> Turn = new(StringComparer.Ordinal)
  {
    ["right"] = 0, ["down"] = 90, ["left"] = 180, ["up"] = 270
  };

  public static readonly Dictionary<string, (string Shape, string? Direction)> ToolToShape = new(StringComparer.Ordinal)
  {
    ["rectangle"] = ("rectangle", null),
    ["round_rectangle"] = ("rounded_rectangle", null),
    ["ellipse"] = ("ellipse", null),
    ["triangle"] = ("triangle", null),
    ["right_triangle"] = ("right_triangle", null),
    ["diamond"] = ("diamond", null),
    ["parallelogram"] = ("parallelogram", null),
    ["trapezoid"] = ("trapezoid", null),
    ["pentagon"] = ("pentagon", null),
    ["hexagon"] = ("hexagon", null),
    ["chevron"] = ("chevron", null),
    ["home_plate"] = ("home_plate", null),
    ["right_arrow"] = ("arrow", "right"),
    ["left_arrow"] = ("arrow", "left"),
    ["up_arrow"] = ("arrow", "up"),
    ["down_arrow"] = ("arrow", "down"),
    ["left_brace"] = ("brace", "left"),
    ["right_brace"] = ("brace", "right"),
    ["left_bracket"] = ("bracket", "left"),
    ["right_bracket"] = ("bracket", "right"),
    ["wedge_rectangle_callout"] = ("callout", null)
  };

  public static readonly (string Model, string Tool)[] CharacterFlags =
  [
    ("bold", "is_bold"),
    ("italic", "is_italic"),
    ("underline", "is_underlined")
  ];

  public static readonly HashSet<string> ArrowMarkers =
    new(Element.Markers, StringComparer.Ordinal);
  public static readonly HashSet<string> ChartMarkerShapes = ["circle", "square", "diamond", "triangle"];
  public static readonly HashSet<string> ChartTypes =
    new(Chart.Types, StringComparer.Ordinal);

  [GeneratedRegex(@"^\d+\.$")]
  public static partial Regex Numbered();

  /// <summary>Encodes a chart category or series key as <c>{index}:{name}</c>.</summary>
  public static string ChartDataKey(int index, string name)
  {
    return string.Create(CultureInfo.InvariantCulture, $"{index}:{name}");
  }

  /// <summary>Decodes a <c>{index}:{name}</c> chart data key into its index and name.</summary>
  public static (int Index, string Name) ParseChartDataKey(string key)
  {
    int colon = key.IndexOf(':');
    return (int.Parse(key[..colon], CultureInfo.InvariantCulture), key[(colon + 1)..]);
  }


}
