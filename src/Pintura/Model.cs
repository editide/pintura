using System.Globalization;
using System.Text.Json.Serialization;

namespace Pintura;

/// <summary>The presentation as loaded: theme, layouts, and slides kept apart, with fields, theme colors, fonts, and
/// style names still references. Serialized as JSON, it is the export contract.</summary>
public class Presentation
{
  public string Name { get; init; } = "";
  public double[] Size { get; init; } = [960, 540];
  public Dictionary<string, Field> Fields { get; init; } = new Dictionary<string, Field>();
  public Dictionary<string, Book> Books { get; init; } = new Dictionary<string, Book>();
  /// <summary>Every Libreta reference the text makes, by the field name its runs keep.</summary>
  public Dictionary<string, BookValue> References { get; init; } = new Dictionary<string, BookValue>();
  public Theme Theme { get; init; } = new Theme();
  public List<Section> Sections { get; init; } = new List<Section>();
  public List<Slide> Slides { get; init; } = new List<Slide>();
  /// <summary>Every chart a slide, layout, or the theme places, by file stem.</summary>
  public Dictionary<string, Chart> Charts { get; init; } = new Dictionary<string, Chart>();
  /// <summary>Every table a slide, layout, or the theme places, by file stem.</summary>
  public Dictionary<string, Table> Tables { get; init; } = new Dictionary<string, Table>();
  /// <summary>What reviewers wrote on the slides, from <c>comments.yaml</c>, in file order.</summary>
  [JsonIgnore] public List<Comment> Comments { get; init; } = new List<Comment>();
  [JsonIgnore] public string Root { get; init; } = "";
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class Field
{
  public string Value { get; init; } = "";
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>A Libreta book the presentation includes, by the id references name it with.</summary>
public class Book
{
  public string Id { get; init; } = "";
  /// <summary>The folder as written, relative to <c>presentation.yaml</c>.</summary>
  public string Path { get; init; } = "";
  [JsonIgnore] public string Root { get; init; } = "";
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>One cell a reference reads from a book, as the reference spells it.</summary>
public class BookValue
{
  public string Text { get; init; } = "";
  public string Book { get; init; } = "";
  public string Line { get; init; } = "";
  public string Column { get; init; } = "";
  /// <summary>The book's entry in <c>presentation.yaml</c>, at the reference.</summary>
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>A run of slides from a <c>section</c> entry to the next; slides before the first have no name.</summary>
public class Section
{
  public string? Name { get; init; }
  /// <summary>Where the name is written; null for the slides before the first section.</summary>
  [JsonIgnore] public Origin? Origin { get; init; }
  public List<string> Slides { get; init; } = new List<string>();
}

public class Theme
{
  public string Name { get; init; } = "";
  public Dictionary<string, string> Colors { get; init; } = new Dictionary<string, string>();
  public Dictionary<string, Color> NamedColors { get; init; } = new Dictionary<string, Color>();
  public Dictionary<string, string> Fonts { get; init; } = new Dictionary<string, string>();
  /// <summary>The font files in the theme's <c>fonts/</c>, which draw in place of installed ones.</summary>
  [JsonIgnore] public List<FontFace> Faces { get; init; } = new List<FontFace>();
  public Color Background { get; init; } = new Color { Theme = "light1" };
  public Placeholder? Title { get; init; }
  public Dictionary<string, Placeholder> Placeholders { get; init; } = new Dictionary<string, Placeholder>();
  public Dictionary<string, Area> Areas { get; init; } = new Dictionary<string, Area>();
  public StyleSheet Styles { get; init; } = new StyleSheet();
  public Dictionary<string, Layout> Layouts { get; init; } = new Dictionary<string, Layout>();
  [JsonIgnore] public ThemeChecks Checks { get; init; } = new ThemeChecks();
  [JsonIgnore] public string Root { get; init; } = "";
  /// <summary>The folder as origins show it, which begins the file of every value the theme or a layout sets.
  /// </summary>
  [JsonIgnore] public string Display { get; init; } = "";
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>Thresholds <c>--check</c> reads from the theme; a missing one leaves its check off.</summary>
public class ThemeChecks
{
  /// <summary>The percent below which <c>fit: shrink</c> text is a warning.</summary>
  public double? Shrink { get; init; }
}

public class Layout
{
  public string Id { get; init; } = "";
  public string Name { get; init; } = "";
  public Color? Background { get; init; }
  /// <summary>Null inherits the theme's title; <see cref="TitleRemoved"/> is <c>title: none</c>.</summary>
  public Placeholder? Title { get; init; }
  public bool TitleRemoved { get; init; }
  public Dictionary<string, Placeholder> Placeholders { get; init; } = new Dictionary<string, Placeholder>();
  public Dictionary<string, Area> Areas { get; init; } = new Dictionary<string, Area>();
  public List<Element> Elements { get; init; } = new List<Element>();
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class Slide
{
  public string Id { get; init; } = "";
  public int Number { get; init; }
  public string? Section { get; init; }
  public string Layout { get; init; } = "";
  /// <summary>Null takes the layout's, or else the theme's.</summary>
  public Color? Background { get; init; }
  public Placeholder? Title { get; init; }
  public Dictionary<string, Placeholder> Placeholders { get; init; } = new Dictionary<string, Placeholder>();
  public List<Element> Elements { get; init; } = new List<Element>();
  /// <summary>The title and placeholders the elements list names, in its order, each drawn where its entry sits.
  /// </summary>
  public List<Stacked> Stacked { get; init; } = new List<Stacked>();
  /// <summary>Ids of the layout's elements the slide leaves out.</summary>
  public List<string> Hide { get; init; } = new List<string>();
  /// <summary>Set when the slide's file failed to load; the rest of the presentation still renders.</summary>
  [JsonIgnore] public LoadException? Error { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Origin? BackgroundOrigin { get; init; }
}

/// <summary>The title, or a placeholder by name, drawn in front of the slide's first <paramref name="Over"/> elements.
/// </summary>
public record Stacked(string Id, int Over);

public class Area
{
  public double[] Rect { get; init; } = [0, 0, 0, 0];
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>A rectangle, and the area it came from when the author named one.</summary>
public class Frame
{
  public double[] Rect { get; init; } = [0, 0, 0, 0];
  public string? Area { get; init; }
}

/// <summary>The title or a named placeholder at any level: the theme and layout own its frame and style, and the
/// slide writes its words and may override the rest.</summary>
public class Placeholder
{
  public Frame? Frame { get; init; }
  public string? Style { get; init; }
  public TextProperties Properties { get; init; } = new TextProperties();
  public List<Run>? Text { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class Element
{
  public string Id { get; init; } = "";
  public string Kind { get; init; } = "";
  public Frame? Frame { get; init; }
  public double? Rotate { get; init; }
  public string? Flip { get; init; }
  public string? Style { get; init; }
  public Color? Fill { get; init; }
  public Stroke? Stroke { get; init; }
  public Shadow? Shadow { get; init; }
  public double? Opacity { get; init; }
  /// <summary>Decoration rather than content, such as a watermark: the geometry checks leave it out.</summary>
  public bool? Decorative { get; init; }
  public TextProperties Properties { get; init; } = new TextProperties();
  public List<Paragraph>? Text { get; init; }
  public string? Shape { get; init; }
  public string? Direction { get; init; }
  public Dictionary<string, double>? Adjust { get; init; }
  public double[]? Tail { get; init; }
  public double[]? From { get; init; }
  public double[]? To { get; init; }
  /// <summary>An arc's start and end, in degrees clockwise from the right; it runs clockwise between them.</summary>
  public double[]? Arc { get; init; }
  public string? Start { get; init; }
  public string? End { get; init; }
  /// <summary>The media path as written, relative to the file naming it.</summary>
  public string? Picture { get; init; }
  public string? Fit { get; init; }
  public double[]? Crop { get; init; }
  public string? Alt { get; init; }
  public string? Chart { get; init; }
  public string? Table { get; init; }
  [JsonIgnore] public string? PicturePath { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class Stroke
{
  public Color? Color { get; set; }
  public double? Width { get; set; }
  public string? Dash { get; set; }
  public bool? None { get; set; }
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>A drop shadow: <see cref="Distance"/> points off toward <see cref="Angle"/>, degrees clockwise from
/// the right.</summary>
public class Shadow
{
  public Color? Color { get; set; }
  public double? Opacity { get; set; }
  public double? Blur { get; set; }
  public double? Distance { get; set; }
  public double? Angle { get; set; }
  public bool? None { get; set; }
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>Text properties set at one level. Each set property remembers where it was written, so the cascade can
/// report the file and line behind every effective value.</summary>
public class TextProperties
{
  public static readonly string[] Character = ["font", "size", "color", "bold", "italic", "underline"];
  public static readonly string[] Paragraph = ["align", "space_before", "space_after", "line_spacing"];
  public static readonly string[] Box = ["valign", "insets", "fit", "wrap", "columns"];
  public static readonly string[] Style = [.. Character, .. Paragraph, "valign"];
  /// <summary>What a named style in <c>styles.yaml</c> can hold of text.</summary>
  public static readonly string[] Named = [.. Style, "insets"];
  public static readonly string[] All = [.. Character, .. Paragraph, .. Box];

  public string? Font { get; set; }
  public double? Size { get; set; }
  public Color? Color { get; set; }
  public bool? Bold { get; set; }
  public bool? Italic { get; set; }
  public bool? Underline { get; set; }
  public string? Align { get; set; }
  public string? Valign { get; set; }
  public double? SpaceBefore { get; set; }
  public double? SpaceAfter { get; set; }
  public double? LineSpacing { get; set; }
  public double[]? Insets { get; set; }
  public string? Fit { get; set; }
  public bool? Wrap { get; set; }
  public int? Columns { get; set; }
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();

  [JsonIgnore]
  public bool IsEmpty => Origins.Count == 0;

  public object? Get(string key)
  {
    return key switch
    {
      "font" => Font,
      "size" => Size,
      "color" => Color,
      "bold" => Bold,
      "italic" => Italic,
      "underline" => Underline,
      "align" => Align,
      "valign" => Valign,
      "space_before" => SpaceBefore,
      "space_after" => SpaceAfter,
      "line_spacing" => LineSpacing,
      "insets" => Insets,
      "fit" => Fit,
      "wrap" => Wrap,
      "columns" => Columns,
      _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };
  }


}

/// <summary>A bullet level in the theme's text, or a named style's bullets and indents at one level, which a box or
/// paragraph in that style takes over the theme's key by key.</summary>
public class Level
{
  public string? Bullet { get; init; }
  public Color? BulletColor { get; init; }
  /// <summary>A multiple of the paragraph's text size.</summary>
  public double? BulletSize { get; init; }
  public double? Indent { get; init; }
  public double? Hanging { get; init; }
  public TextProperties Properties { get; init; } = new TextProperties();
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>What a named style sets of the box itself, which only a text box or shape in that style takes.
/// </summary>
public class StyleShape
{
  public Color? Fill { get; init; }
  public Stroke? Stroke { get; init; }
  public Shadow? Shadow { get; init; }
  public double? Opacity { get; init; }
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class StyleSheet
{
  public TextProperties Text { get; init; } = new TextProperties();
  public Dictionary<int, Level> Levels { get; init; } = new Dictionary<int, Level>();
  public TextProperties ShapeText { get; init; } = new TextProperties();
  public Color? ShapeFill { get; init; }
  public Stroke? ShapeStroke { get; init; }
  public Shadow? ShapeShadow { get; init; }
  public string? PictureFit { get; init; }
  public Stroke? LineStroke { get; init; }
  public Dictionary<string, TextProperties> Styles { get; init; } = new Dictionary<string, TextProperties>();
  /// <summary>Each named style's fill, stroke, shadow, and opacity, for the styles that set any.</summary>
  public Dictionary<string, StyleShape> StyleShapes { get; init; } = new Dictionary<string, StyleShape>();
  /// <summary>Each named style's own levels, for the styles that set any.</summary>
  public Dictionary<string, Dictionary<int, Level>> StyleLevels { get; init; } =
    new Dictionary<string, Dictionary<int, Level>>();
  public Dictionary<string, NumberFormat> Formats { get; init; } = new Dictionary<string, NumberFormat>();
  public ChartProperties Chart { get; init; } = new ChartProperties();
  public Dictionary<string, ChartProperties> Charts { get; init; } = new Dictionary<string, ChartProperties>();
  public Dictionary<string, TableStyle> Tables { get; init; } = new Dictionary<string, TableStyle>();
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class NumberFormat
{
  public double? Scale { get; init; }
  public int? Decimals { get; init; }
  public bool? Separator { get; init; }
  public string? Prefix { get; init; }
  public string? Suffix { get; init; }
  public string? Negative { get; init; }
  public string? NegativeStyle { get; init; }
  /// <summary>A date pattern, set only on a format that spells dates and takes no other key.</summary>
  public string? Date { get; init; }

  /// <summary>A date as <see cref="Date"/> spells it: <c>YYYY</c> and <c>YY</c> for the year, <c>MMMM</c>, <c>MMM</c>,
  /// <c>MM</c>, and <c>M</c> for the month's name, its short name, and its number with and without a leading zero,
  /// <c>DD</c> and <c>D</c> for the day, and any other character as itself.</summary>
  public string SpellDate(DateOnly date)
  {
    var text = new System.Text.StringBuilder();
    string pattern = Date!;
    for (int index = 0; index < pattern.Length;)
    {
      char letter = pattern[index];
      int run = 1;
      while (index + run < pattern.Length && pattern[index + run] == letter)
      {
        run++;
      }
      string? token = (letter, run) switch
      {
        ('Y', 4) => date.Year.ToString("D4", CultureInfo.InvariantCulture),
        ('Y', 2) => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
        ('M', 4) => date.ToString("MMMM", CultureInfo.InvariantCulture),
        ('M', 3) => date.ToString("MMM", CultureInfo.InvariantCulture),
        ('M', 2) => date.Month.ToString("D2", CultureInfo.InvariantCulture),
        ('M', 1) => date.Month.ToString(CultureInfo.InvariantCulture),
        ('D', 2) => date.Day.ToString("D2", CultureInfo.InvariantCulture),
        ('D', 1) => date.Day.ToString(CultureInfo.InvariantCulture),
        _ => null
      };
      text.Append(token ?? pattern.Substring(index, run));
      index += run;
    }
    return text.ToString();
  }

  /// <summary>The number as this format writes it, with Libreta's defaults for what the format leaves out, as
  /// <c>formatNumber</c> in <c>charts.js</c> writes it.</summary>
  public string Spell(double value)
  {
    double scaled = value * (Scale ?? 1);
    // "F" alone rounds halves to even, which neither toFixed nor PowerPoint does.
    string digits = Math.Round(Math.Abs(scaled), Decimals ?? 0, MidpointRounding.AwayFromZero)
      .ToString($"F{Decimals ?? 0}", CultureInfo.InvariantCulture);
    string[] parts = digits.Split('.');
    string whole = Separator ?? true ? Grouped(parts[0]) : parts[0];
    string text = $"{Prefix}{whole}{(parts.Length > 1 ? $".{parts[1]}" : "")}{Suffix}";
    if (scaled >= 0)
    {
      return text;
    }
    return (Negative ?? "parentheses") == "parentheses" ? $"({text})" : $"-{text}";
  }

  /// <summary>Digits with a comma every three from the right, at any length, where a long would overflow.</summary>
  private static string Grouped(string digits)
  {
    var text = new System.Text.StringBuilder();
    for (int index = 0; index < digits.Length; index++)
    {
      if (index > 0 && (digits.Length - index) % 3 == 0)
      {
        text.Append(',');
      }
      text.Append(digits[index]);
    }
    return text.ToString();
  }


}

/// <summary>Row styles by name, <c>header</c> among them when header rows have one, and the color of every second
/// body row without a row style.</summary>
public class TableStyle
{
  public Dictionary<string, TableFormatting> Rows { get; init; } = new Dictionary<string, TableFormatting>();
  public Color? Banded { get; init; }
  /// <summary>How far in a view row's label sits per step of its indent in the view.</summary>
  public double? Indent { get; init; }
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>What a row style, column, row, or cell sets. <c>Style</c> names a row style on a row and a text style on a
/// column or cell; row styles and rows have top and bottom borders, columns left and right ones, cells all four, and
/// columns no fill.</summary>
public class TableFormatting
{
  public static readonly string[] Text = [.. TextProperties.Style, "insets"];

  public string? Style { get; init; }
  public string? Format { get; init; }
  public Color? Fill { get; init; }
  public Stroke? BorderTop { get; init; }
  public Stroke? BorderBottom { get; init; }
  public Stroke? BorderLeft { get; init; }
  public Stroke? BorderRight { get; init; }
  public TextProperties Properties { get; init; } = new TextProperties();
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

public class TableColumn : TableFormatting
{
  public double? Width { get; init; }
}

public class TableRow : TableFormatting
{
  public double? Height { get; init; }
}

public class TableCell : TableFormatting
{
  public int Row { get; init; }
  public int Column { get; init; }
}

/// <summary>A merge from its first cell, numbered from 1, across <c>Rows</c> and <c>Columns</c>.</summary>
public class TableMerge
{
  public int Row { get; init; }
  public int Column { get; init; }
  public int Rows { get; init; } = 1;
  public int Columns { get; init; } = 1;
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);

  public bool Covers(int row, int column)
  {
    return row >= Row && row < Row + Rows && column >= Column && column < Column + Columns;
  }


}

/// <summary>A file in <c>tables/</c> with its CSV as written, every row a table row. Rows and columns are numbered
/// from 1, header included.</summary>
public class Table
{
  public string Id { get; init; } = "";
  public string? Style { get; init; }
  /// <summary>The CSV path as written, relative to <c>tables/</c>; empty for a table from a view.</summary>
  public string Data { get; init; } = "";
  /// <summary>The Libreta view the table presents, as <c>book::view</c>.</summary>
  public string? View { get; init; }
  public int HeaderRows { get; init; }
  public List<TableColumn> Columns { get; init; } = new List<TableColumn>();
  public Dictionary<int, TableRow> Rows { get; init; } = new Dictionary<int, TableRow>();
  public List<TableCell> Cells { get; init; } = new List<TableCell>();
  public List<TableMerge> Merges { get; init; } = new List<TableMerge>();
  public List<List<string>> Values { get; init; } = new List<List<string>>();
  /// <summary>A view's exact values under <see cref="Values"/>, its displayed ones, for a format to spell; null for a
  /// CSV and for cells with no number.</summary>
  [JsonIgnore] public List<List<string?>>? Exact { get; init; }
  /// <summary>The book line and column behind each cell of a view, for tracing a number to it.</summary>
  [JsonIgnore] public List<List<(string Line, string Column)?>>? Sources { get; init; }
  /// <summary>Each row's indent in the view, in steps; null for a CSV.</summary>
  [JsonIgnore] public List<int>? Indents { get; init; }
  /// <summary>Each cell's markup, parsed at load so an error names its CSV line.</summary>
  [JsonIgnore] public List<List<List<Paragraph>>> Paragraphs { get; init; } = new List<List<List<Paragraph>>>();
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();

  [JsonIgnore]
  public int ColumnCount => Values.Count == 0 ? 0 : Values[0].Count;

  /// <summary>What the table reads, for messages: its CSV or its view.</summary>
  [JsonIgnore]
  public string Source => View ?? Data;


}

/// <summary>A file in <c>charts/</c>, with its CSV already read into series and categories as <c>series_in</c>
/// arranges them.</summary>
public class Chart
{
  public static readonly string[] Types =
  [
    "clustered_column", "stacked_column", "percent_stacked_column", "clustered_bar", "stacked_bar",
    "percent_stacked_bar", "line", "stacked_line", "percent_stacked_line", "pie", "doughnut"
  ];

  public string Id { get; init; } = "";
  public string Type { get; init; } = "";
  public string? Style { get; init; }
  /// <summary>The CSV path as written, relative to <c>charts/</c>; empty for a chart from a book's lines.</summary>
  public string Data { get; init; } = "";
  /// <summary>The Libreta book the series' lines come from.</summary>
  public string? Book { get; init; }
  /// <summary>Each series' line in <see cref="Book"/>, in series order.</summary>
  public List<string>? Lines { get; init; }
  /// <summary>Each category's column in <see cref="Book"/>, or with <c>series_in: columns</c> each series', in order;
  /// the categories or series names are the columns' labels.</summary>
  public List<string>? Columns { get; init; }
  public string SeriesIn { get; init; } = "rows";
  public ChartProperties Properties { get; init; } = new ChartProperties();
  public List<string> Categories { get; init; } = new List<string>();
  public List<ChartSeries> Series { get; init; } = new List<ChartSeries>();
  /// <summary>What the chart file sets for single series, by their names in the CSV.</summary>
  public Dictionary<string, ChartSeriesOptions> SeriesOptions { get; init; } =
    new Dictionary<string, ChartSeriesOptions>(StringComparer.Ordinal);
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();

  /// <summary>What the chart reads, for messages: its CSV or its book.</summary>
  [JsonIgnore]
  public string Source => Book is null ? Data : $"book {Book}";

  [JsonIgnore]
  public string Family => FamilyOf(Type);

  /// <summary><c>clustered</c>, <c>stacked</c>, or <c>percent_stacked</c>; pies are clustered.</summary>
  [JsonIgnore]
  public string Grouping => GroupingOf(Type);

  public static string FamilyOf(string type) => type switch
  {
    "pie" or "doughnut" => "pie",
    _ when type.EndsWith("line", StringComparison.Ordinal) => "line",
    _ when type.EndsWith("bar", StringComparison.Ordinal) => "bar",
    _ => "column"
  };

  public static string GroupingOf(string type) => type.StartsWith("percent_stacked", StringComparison.Ordinal)
    ? "percent_stacked"
    : type.StartsWith("stacked", StringComparison.Ordinal) ? "stacked"
    : "clustered";
}

/// <summary>A series and its values in category order; a null is an empty cell, drawn as a gap.</summary>
public class ChartSeries
{
  public string Name { get; init; } = "";
  public List<double?> Values { get; init; } = new List<double?>();
}

/// <summary>A series' own keys: <c>type</c> draws it as a <c>line</c> over columns or as a stack's <c>total</c>,
/// <c>axis</c> puts a line on the secondary axis, and <c>data_labels</c> and <c>markers</c> layer over the chart's.
/// </summary>
public class ChartSeriesOptions
{
  public string? Type { get; set; }
  public string? Axis { get; set; }
  public ChartLabels DataLabels { get; init; } = new ChartLabels();
  public ChartMarkers Markers { get; init; } = new ChartMarkers();
  /// <summary>Single points by category: a column's or bar's own fill, a line's own marker, or a label of its own.
  /// </summary>
  public Dictionary<string, ChartPoint> Points { get; init; } =
    new Dictionary<string, ChartPoint>(StringComparer.Ordinal);
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();
}

/// <summary>Chart keys set at one level: the theme's <c>chart</c> base, a named chart style, or a chart file. Maps
/// merge key by key, so each nested key remembers where it was written under its dotted path.</summary>
public class ChartProperties
{
  public static readonly string[] Keys =
  [
    "title", "legend", "data_labels.show", "data_labels.format", "data_labels.position", "data_labels.color",
    "data_labels.bold", "value_axis.visible", "value_axis.min", "value_axis.max", "value_axis.step",
    "value_axis.format", "value_axis.gridlines", "value_axis.reverse", "secondary_axis.visible", "secondary_axis.min",
    "secondary_axis.max", "secondary_axis.step", "secondary_axis.format", "secondary_axis.reverse",
    "category_axis.visible", "category_axis.labels", "category_axis.format", "category_axis.step", "series_colors",
    "gap_width", "overlap", "markers.shape", "markers.size", "markers.fill", "plot.insets",
    "text.font", "text.size", "text.color"
  ];

  public string? Title { get; set; }
  public string? Legend { get; set; }
  public ChartLabels DataLabels { get; init; } = new ChartLabels();
  public ChartValueAxis ValueAxis { get; init; } = new ChartValueAxis();
  /// <summary>The axis on the right that lines with <c>axis: secondary</c> are drawn against; it takes no gridlines.
  /// </summary>
  public ChartValueAxis SecondaryAxis { get; init; } = new ChartValueAxis();
  public ChartCategoryAxis CategoryAxis { get; init; } = new ChartCategoryAxis();
  public List<Color>? SeriesColors { get; set; }
  public double? GapWidth { get; set; }
  public double? Overlap { get; set; }
  /// <summary>What sits on each point of the chart's lines.</summary>
  public ChartMarkers Markers { get; init; } = new ChartMarkers();
  /// <summary>Room between the chart's frame and its plot and axis labels, <c>[left, top, right, bottom]</c>.</summary>
  public double[]? PlotInsets { get; set; }
  public ChartText Text { get; init; } = new ChartText();
  [JsonIgnore] public Dictionary<string, Origin> Origins { get; init; } = new Dictionary<string, Origin>();

  public object? Get(string key)
  {
    return key switch
    {
      "title" => Title,
      "legend" => Legend,
      "data_labels.show" => DataLabels.Show,
      "data_labels.format" => DataLabels.Format,
      "data_labels.position" => DataLabels.Position,
      "data_labels.color" => DataLabels.Color,
      "data_labels.bold" => DataLabels.Bold,
      "value_axis.visible" => ValueAxis.Visible,
      "value_axis.min" => ValueAxis.Min,
      "value_axis.max" => ValueAxis.Max,
      "value_axis.step" => ValueAxis.Step,
      "value_axis.format" => ValueAxis.Format,
      "value_axis.gridlines" => ValueAxis.Gridlines,
      "value_axis.reverse" => ValueAxis.Reverse,
      "secondary_axis.visible" => SecondaryAxis.Visible,
      "secondary_axis.min" => SecondaryAxis.Min,
      "secondary_axis.max" => SecondaryAxis.Max,
      "secondary_axis.step" => SecondaryAxis.Step,
      "secondary_axis.format" => SecondaryAxis.Format,
      "secondary_axis.reverse" => SecondaryAxis.Reverse,
      "category_axis.visible" => CategoryAxis.Visible,
      "category_axis.labels" => CategoryAxis.Labels,
      "category_axis.format" => CategoryAxis.Format,
      "category_axis.step" => CategoryAxis.Step,
      "series_colors" => SeriesColors,
      "gap_width" => GapWidth,
      "overlap" => Overlap,
      "markers.shape" => Markers.Shape,
      "markers.size" => Markers.Size,
      "markers.fill" => Markers.Fill,
      "plot.insets" => PlotInsets,
      "text.font" => Text.Font,
      "text.size" => Text.Size,
      "text.color" => Text.Color,
      _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };
  }


}

/// <summary>On a line, <see cref="Color"/> outlines the point's marker and fills it unless the marker names a fill.
/// </summary>
public class ChartPoint
{
  public Color? Color { get; set; }
  public ChartMarkers Markers { get; init; } = new ChartMarkers();
  public ChartLabels DataLabels { get; init; } = new ChartLabels();
}

public class ChartLabels
{
  public string? Show { get; set; }
  public string? Format { get; set; }
  public string? Position { get; set; }
  public Color? Color { get; set; }
  public bool? Bold { get; set; }
}

/// <summary>A line's markers, outlined in the line's color and width; the fill defaults to the line's color.
/// </summary>
public class ChartMarkers
{
  public string? Shape { get; set; }
  public double? Size { get; set; }
  public Color? Fill { get; set; }
}

public class ChartValueAxis
{
  public bool? Visible { get; set; }
  public double? Min { get; set; }
  public double? Max { get; set; }
  /// <summary>The distance between ticks, fixed; Excel's automatic scale picks it when null.</summary>
  public double? Step { get; set; }
  public string? Format { get; set; }
  public bool? Gridlines { get; set; }
  /// <summary>Runs the values top down, or right to left on a bar chart, as PowerPoint's maximum-to-minimum
  /// orientation does.</summary>
  public bool? Reverse { get; set; }
}

public class ChartCategoryAxis
{
  public bool? Visible { get; set; }
  /// <summary>False keeps the axis's line but drops its labels, such as for pictures placed beneath the categories.
  /// </summary>
  public bool? Labels { get; set; }
  /// <summary>A date format, which makes the axis a time line over categories written as ISO dates.</summary>
  public string? Format { get; set; }
  public DateStep? Step { get; set; }
}

/// <summary>The time between a date axis's labels, such as 7 days or 1 month.</summary>
public record DateStep(int Count, string Unit)
{
  public override string ToString()
  {
    return $"{Count} {(Count == 1 ? Unit.TrimEnd('s') : Unit)}";
  }
}

public class ChartText
{
  public string? Font { get; set; }
  public double? Size { get; set; }
  public Color? Color { get; set; }
}

public class Paragraph
{
  /// <summary>0 for a plain paragraph, otherwise the bullet or numbering level.</summary>
  public int Level { get; init; }
  public int? Number { get; init; }
  public string? Style { get; init; }
  public List<Run> Runs { get; init; } = new List<Run>();
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  /// <summary>The paragraph's line in its markup, counted from 0.</summary>
  [JsonIgnore] public int Line { get; init; }
  /// <summary>Where the text starts on that line, after any bullet or number.</summary>
  [JsonIgnore] public int Start { get; init; }
}

/// <summary>A stretch of text with one formatting. A field run has no text of its own until a slide fills it in.
/// </summary>
public class Run
{
  public string Text { get; init; } = "";
  public string? Field { get; init; }
  public bool? Bold { get; init; }
  public bool? Italic { get; init; }
  public string? Link { get; init; }
  public List<string>? Styles { get; init; }
  public TextProperties? Properties { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
  /// <summary>The characters of the markup line each character of <see cref="Text"/> was read from, an escape's two
  /// included; a field has one span, from <c>$</c> to <c>}</c>.</summary>
  [JsonIgnore] public (int Start, int End)[] Sources { get; init; } = [];
}