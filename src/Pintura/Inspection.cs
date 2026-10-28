using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pintura;

public record SlideEntry(int Number, string Id, string? Layout, string? Title, string? Section, string? Error);

public record ElementEntry(
  string Id,
  string Owner,
  string Kind,
  string? Role,
  double[]? Frame,
  string? Area,
  double[]? From,
  double[]? To,
  double[]? Arc,
  string? Text,
  string Origin);

public record PropertyEntry(string Key, object Value, string Display, string? Origin);

public record ParagraphEntry(
  int Number,
  string Text,
  int Level,
  string? Style,
  List<PropertyEntry> Properties,
  List<RunEntry> Runs);

public record RunEntry(string Text, List<string>? Styles, string? Field, List<PropertyEntry> Properties)
{
  public TracePayload? Trace => Reference.Trace(Field);
}

public record ElementDetail(
  string Slide,
  string Id,
  string Owner,
  string Kind,
  string? Role,
  string Origin,
  List<PropertyEntry> Properties,
  List<ParagraphEntry>? Paragraphs,
  List<CellEntry>? Cells = null);

/// <summary>A table cell, or a merge from its first cell, with what differs from the table's base text.</summary>
public record CellEntry(
  int Row,
  int Column,
  int Rows,
  int Columns,
  string Text,
  List<PropertyEntry> Properties,
  List<ParagraphEntry> Paragraphs,
  TracePayload? Trace);

/// <summary>The headless views of a presentation: <c>--list</c>, <c>--slide</c>, and <c>--element</c>, each as text
/// or JSON.</summary>
public static class HeadlessRunner
{
  public static int Run(string root, HeadlessCommand command, string? slide, string? element, bool json,
    TextWriter output, bool resolved = false, IReadOnlyList<string>? ids = null, SlideSelection? only = null)
  {
    if (command == HeadlessCommand.Comments && resolved)
    {
      return CommentList.WriteResolved(root, json, output);
    }
    Presentation presentation = PresentationLoader.Load(root);
    return command switch
    {
      HeadlessCommand.List => List(presentation, json, output),
      HeadlessCommand.Slide => Slide(presentation, slide!, json, output),
      HeadlessCommand.Element => Element(presentation, slide!, element!, json, output),
      HeadlessCommand.Export => Export(presentation, output),
      HeadlessCommand.Comments => CommentList.Write(presentation, json, output, only),
      HeadlessCommand.Resolve => Resolve(presentation, ids!, output),
      _ => throw new ArgumentOutOfRangeException(nameof(command), command, "not a console command")
    };
  }

  /// <summary>Resolves the comments named, all of them or none.</summary>
  private static int Resolve(Presentation presentation, IReadOnlyList<string> ids, TextWriter output)
  {
    var change = new CommentChange(Many: [.. ids.Select(id => new CommentChange(Resolve: id))]);
    try
    {
      CommentFile.Apply(presentation, change, CommentFile.Author(presentation.Root), DateTime.Now,
        Saves.Snapshot(presentation.Root));
    }
    catch (PatchException exception)
    {
      throw new InvalidDataException(exception.Message);
    }
    Dictionary<string, string> outcomes = CommentFile.LoadResolved(presentation.Root, presentation.Root)
      .GroupBy(comment => comment.Id).ToDictionary(group => group.Key, group => group.Last().Resolved!.Outcome);
    int left = CommentFile.Load(presentation.Root, presentation.Root).Count;
    output.WriteLine($"resolved {string.Join(", ", ids.Select(id => $"{id} ({outcomes[id]})"))} into " +
      $"{CommentFile.ResolvedName}; {left} comment{(left == 1 ? "" : "s")} left");
    return 0;
  }

  private static int Export(Presentation presentation, TextWriter output)
  {
    ExportDeck deck = ExportBuilder.Build(presentation);
    output.WriteLine(JsonSerializer.Serialize(deck, PayloadJson.Indented));
    return deck.Slides.Any(slide => slide.Error is not null) ? 1 : 0;
  }

  public static List<SlideEntry> Entries(Presentation presentation)
  {
    return presentation.Slides
      .Select(slide => new SlideEntry(slide.Number, slide.Id, slide.Error is null ? slide.Layout : null,
        slide.Error is null ? PayloadBuilder.Title(presentation, slide) : null, slide.Section, slide.Error?.Message))
      .ToList();
  }

  private static int List(Presentation presentation, bool json, TextWriter output)
  {
    List<SlideEntry> entries = Entries(presentation);
    bool failed = entries.Any(entry => entry.Error is not null);
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(entries, PayloadJson.Indented));
      return failed ? 1 : 0;
    }
    int width = entries.Count == 0 ? 0 : entries.Max(entry => entry.Id.Length);
    int layoutWidth = entries.Count == 0 ? 0 : entries.Max(entry => entry.Layout?.Length ?? 0);
    string? section = null;
    foreach (SlideEntry entry in entries)
    {
      if (entry.Section != section)
      {
        section = entry.Section;
        output.WriteLine($"   {section}");
      }
      string detail = entry.Error is null
        ? $"{(entry.Layout ?? "").PadRight(layoutWidth)}  {entry.Title}".TrimEnd()
        : $"error: {entry.Error}";
      output.WriteLine($"{entry.Number,2} {entry.Id.PadRight(width)}  {detail}");
    }
    return failed ? 1 : 0;
  }

  private static int Slide(Presentation presentation, string id, bool json, TextWriter output)
  {
    Slide slide = FindSlide(presentation, id);
    SlideScene scene = SceneBuilder.Build(presentation, slide);
    List<ElementEntry> entries = scene.Items.Select(ElementEntry).ToList();
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(entries, PayloadJson.Indented));
      return 0;
    }
    output.WriteLine($"{slide.Id}: slide {slide.Number} on layout {slide.Layout}" +
      (slide.Section is null ? "" : $", in {slide.Section}"));
    int idWidth = entries.Max(entry => entry.Id.Length);
    int kindWidth = entries.Max(entry => entry.Kind.Length);
    foreach (ElementEntry entry in entries)
    {
      string owner = entry.Role ?? entry.Owner;
      string geometry = entry.From is not null
        ? $"{Numbers(entry.From)} → {Numbers(entry.To!)}"
        : (entry.Area is null ? "" : $"{entry.Area} ") + (entry.Frame is null ? "" : Numbers(entry.Frame)) +
          (entry.Arc is [double from, double to] ? $" {Number(from)}° → {Number(to)}°" : "");
      string text = entry.Text is null ? "" : $"  \"{Shorten(entry.Text, 48)}\"";
      output.WriteLine($"  {owner,-11} {entry.Id.PadRight(idWidth)}  {entry.Kind.PadRight(kindWidth)}  " +
        $"{geometry}{text}");
    }
    return 0;
  }

  private static ElementEntry ElementEntry(SceneItem item)
  {
    var frame = item.Properties.GetValueOrDefault("frame")?.Value as Frame;
    return new ElementEntry(
      item.Id,
      item.Owner,
      item.Kind,
      item.Role,
      frame?.Rect,
      frame?.Area,
      item.Properties.GetValueOrDefault("from")?.Value as double[],
      item.Properties.GetValueOrDefault("to")?.Value as double[],
      item.Kind == "arc"
        ? [Convert.ToDouble(item.Properties["arc.from"].Value), Convert.ToDouble(item.Properties["arc.to"].Value)]
        : null,
      item.Paragraphs is null ? null : PlainText(item.Paragraphs),
      item.Origin.ToString());
  }

  private static int Element(Presentation presentation, string slideId, string id, bool json, TextWriter output)
  {
    Slide slide = FindSlide(presentation, slideId);
    SlideScene scene = SceneBuilder.Build(presentation, slide);
    SceneItem item = scene.Items.FirstOrDefault(candidate => candidate.Id == id)
      ?? throw new InvalidDataException($"No element '{id}' on slide '{slideId}'. Available: " +
        string.Join(", ", scene.Items.Select(candidate => candidate.Id)));
    ElementDetail detail = Detail(presentation.Theme, slide, item);
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(detail, PayloadJson.Indented));
      return 0;
    }

    string what = item.Role switch
    {
      "title" => "the title",
      "placeholder" => $"placeholder {item.Id}",
      _ => $"{item.Kind} on the {item.Owner}"
    };
    output.WriteLine($"{slide.Id}/{item.Id}: {what}, {item.Origin}");
    string? unset = item.Kind switch
    {
      "chart" => "(PowerPoint's default)",
      "table" => "(an even share of what the other columns leave)",
      _ => null
    };
    WriteProperties(detail.Properties, "  ", output, unset);
    WriteParagraphs(detail.Paragraphs ?? [], "  ", output);
    foreach (CellEntry cell in detail.Cells ?? [])
    {
      string[] across = new[] { (cell.Rows, "rows"), (cell.Columns, "columns") }
        .Where(pair => pair.Item1 > 1)
        .Select(pair => $"{pair.Item1} {pair.Item2}")
        .ToArray();
      string span = across.Length == 0 ? "" : $", merged across {string.Join(" and ", across)}";
      output.WriteLine();
      output.WriteLine($"  cell {cell.Row}, {cell.Column}{span}: \"{Shorten(cell.Text, 60)}\"");
      WriteProperties(cell.Properties, "    ", output);
      WriteParagraphs(cell.Paragraphs.Where(paragraph => paragraph.Properties.Count > 0 || paragraph.Runs.Count > 0)
        .ToList(), "    ", output);
    }
    return 0;
  }

  private static void WriteParagraphs(List<ParagraphEntry> paragraphs, string indent, TextWriter output)
  {
    foreach (ParagraphEntry paragraph in paragraphs)
    {
      string level = paragraph.Level == 0 ? "" : $" level {paragraph.Level}";
      string style = paragraph.Style is null ? "" : $" {{.{paragraph.Style}}}";
      output.WriteLine();
      output.WriteLine($"{indent}paragraph {paragraph.Number}{level}{style}: \"{Shorten(paragraph.Text, 60)}\"");
      WriteProperties(paragraph.Properties, $"{indent}  ", output);
      foreach (RunEntry run in paragraph.Runs)
      {
        string styles = run.Styles is null ? "" : " " + string.Join(" ", run.Styles.Select(name => $".{name}"));
        string field = run.Field is null ? "" : $" ${{{run.Field}}}";
        output.WriteLine($"{indent}  run \"{Shorten(run.Text, 50)}\"{styles}{field}");
        WriteProperties(run.Properties, $"{indent}    ", output);
      }
    }
  }

  /// <summary>The item drawn <paramref name="index"/>th on a slide, as the page numbers them; null when the slide has
  /// no such item or fails to load.</summary>
  public static ElementDetail? Detail(Presentation presentation, string slideId, int index)
  {
    Slide? slide = presentation.Slides.FirstOrDefault(candidate => candidate.Id == slideId);
    if (slide is null || slide.Error is not null)
    {
      return null;
    }
    List<SceneItem> items = SceneBuilder.Build(presentation, slide).Items;
    return index >= 0 && index < items.Count ? Detail(presentation.Theme, slide, items[index]) : null;
  }

  /// <summary>An item on a layout's specimen, as the layout's stage draws it.</summary>
  public static ElementDetail? LayoutDetail(Presentation presentation, string layoutId, int index)
  {
    if (!presentation.Theme.Layouts.TryGetValue(layoutId, out Layout? layout))
    {
      return null;
    }
    Slide specimen = ThemeView.Specimen(presentation.Theme, layout);
    List<SceneItem> items = SceneBuilder.Build(presentation, specimen).Items;
    return index >= 0 && index < items.Count ? Detail(presentation.Theme, specimen, items[index]) : null;
  }

  /// <summary>The box's properties in full, then for each paragraph and run only what differs from the level above,
  /// so every property drawn is accounted for once.</summary>
  public static ElementDetail Detail(Theme theme, Slide slide, SceneItem item)
  {
    IEnumerable<KeyValuePair<string, Resolved>> own = item.Properties;
    if (item.Table is not null)
    {
      own = own.Concat(item.Table.Widths.Select((width, index) =>
        KeyValuePair.Create($"columns[{index + 1}].width", width)));
    }
    List<PropertyEntry> box = own
      .OrderBy(pair => Rank(pair.Key))
      .Select(pair => Property(pair.Key, pair.Value, theme))
      .ToList();
    List<ParagraphEntry>? paragraphs = item.Paragraphs is null
      ? null
      : Paragraphs(item.Paragraphs, item.Properties, theme);
    List<CellEntry>? cells = item.Table?.Cells
      .Select(cell => new CellEntry(cell.Row, cell.Column, cell.Rows, cell.Columns,
        string.Join(" / ", cell.Paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text)))),
        Differences(cell.Properties, item.Properties, theme), Paragraphs(cell.Paragraphs, cell.Properties, theme),
        cell.Trace))
      .ToList();
    return new ElementDetail(slide.Id, item.Id, item.Owner, item.Kind, item.Role, item.Origin.ToString(), box,
      paragraphs, cells);
  }

  /// <summary>Each paragraph with what differs from its box, and its runs with what differs from the paragraph.
  /// </summary>
  private static List<ParagraphEntry> Paragraphs(List<SceneParagraph> scene, Dictionary<string, Resolved> box,
    Theme theme)
  {
    var paragraphs = new List<ParagraphEntry>();
    for (int index = 0; index < scene.Count; index++)
    {
      SceneParagraph paragraph = scene[index];
      List<PropertyEntry> own = Differences(paragraph.Properties, box, theme);
      Resolved? bulleted(Resolved? value) => paragraph.Bullet is null ? null : value;
      own.InsertRange(0, new (string Key, Resolved? Value)[]
        {
          ("bullet", paragraph.Bullet), ("bullet_color", bulleted(paragraph.BulletColor)),
          ("bullet_size", bulleted(paragraph.BulletSize)), ("indent", paragraph.Indent),
          ("hanging", paragraph.Hanging)
        }
        .Where(pair => pair.Value is not null)
        .Select(pair => Property(pair.Key, pair.Value!, theme)));
      List<RunEntry> runs = paragraph.Runs
        .Select(run => new RunEntry(run.Text, run.Styles, run.Field,
          Differences(run.Properties, paragraph.Properties, theme)))
        .Where(run => run.Properties.Count > 0 || run.Field is not null || paragraph.Runs.Count > 1)
        .ToList();
      paragraphs.Add(new ParagraphEntry(index + 1, string.Concat(paragraph.Runs.Select(run => run.Text)),
        paragraph.Level, paragraph.Style, own, runs));
    }
    return paragraphs;
  }

  private static List<PropertyEntry> Differences(
    Dictionary<string, Resolved> properties,
    Dictionary<string, Resolved> parent,
    Theme theme)
  {
    return properties
      .Where(pair => !parent.TryGetValue(pair.Key, out Resolved? inherited) || inherited != pair.Value)
      .OrderBy(pair => Rank(pair.Key))
      .Select(pair => Property(pair.Key, pair.Value, theme))
      .ToList();
  }

  /// <summary>Text keys in the order of <see cref="TextProperties.All"/>, then chart keys in the order the docs list
  /// them; anything else, such as the frame, first.</summary>
  private static int Rank(string key)
  {
    int text = Array.IndexOf(TextProperties.All, key);
    int chart = Array.IndexOf(ChartProperties.Keys, key);
    return text >= 0 ? text : chart >= 0 ? TextProperties.All.Length + chart : -1;
  }

  public static PropertyEntry Property(string key, Resolved resolved, Theme theme)
  {
    object value = resolved.Value switch
    {
      Color color => color.ToString(),
      Frame frame => frame.Area is null ? frame.Rect : new { frame.Area, frame.Rect },
      List<Color> colors => colors.Select(color => color.ToString()).ToList(),
      _ => resolved.Value
    };
    return new PropertyEntry(key, value, Display(key, resolved.Value, theme), resolved.Origin?.ToString());
  }

  private static string Display(string key, object value, Theme theme)
  {
    return value switch
    {
      Color { IsNone: true } => "none",
      Color { Hex: not null } color => color.Hex,
      Color color => $"{color} ({ColorMath.Resolve(color, theme)})",
      Frame frame => (frame.Area is null ? "" : $"{frame.Area} ") + Numbers(frame.Rect),
      double[] numbers => Numbers(numbers),
      List<Color> colors => string.Join(", ", colors.Select(color => Display(key, color, theme))),
      double number => Number(number),
      int number => number.ToString(CultureInfo.InvariantCulture),
      bool flag => flag ? "true" : "false",
      string font when key == "font" && theme.Fonts.TryGetValue(font, out string? family) => $"{font} ({family})",
      string text => text,
      _ => value.ToString() ?? ""
    };
  }

  private static readonly Regex AutomaticScale = new Regex(@"^(value|secondary)_axis\.(min|max|step)$");

  private static void WriteProperties(List<PropertyEntry> properties, string indent, TextWriter output,
    string? unset = null)
  {
    if (properties.Count == 0)
    {
      return;
    }
    int keyWidth = properties.Max(property => property.Key.Length);
    int valueWidth = Math.Min(40, properties.Max(property => property.Display.Length));
    foreach (PropertyEntry property in properties)
    {
      string origin = property.Origin ?? (AutomaticScale.IsMatch(property.Key)
        ? "(the automatic scale)"
        : unset ?? "(the shape's geometry)");
      output.WriteLine($"{indent}{property.Key.PadRight(keyWidth)}  {property.Display.PadRight(valueWidth)}  " +
        $"← {origin}");
    }
  }

  public static Slide FindSlide(Presentation presentation, string id)
  {
    Slide slide = SlideNamed(presentation, id)
      ?? throw new InvalidDataException($"No slide '{id}'. Available: " +
        string.Join(", ", presentation.Slides.Select(candidate => candidate.Id)) +
        $", or 1-{presentation.Slides.Count}");
    if (slide.Error is not null)
    {
      throw slide.Error;
    }
    return slide;
  }

  /// <summary>The slide with the id, or failing that, the slide at that number from 1.</summary>
  public static Slide? SlideNamed(Presentation presentation, string id)
  {
    List<Slide> slides = presentation.Slides;
    return slides.FirstOrDefault(candidate => candidate.Id == id)
      ?? (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 &&
        number <= slides.Count ? slides[number - 1] : null);
  }

  private static string PlainText(List<SceneParagraph> paragraphs)
  {
    return string.Join(" / ", paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text)))
      .Where(text => text.Length > 0));
  }

  private static string Shorten(string text, int length)
  {
    return text.Length <= length ? text : $"{text[..(length - 1)]}…";
  }

  private static string Numbers(double[] numbers)
  {
    return $"[{string.Join(", ", numbers.Select(Number))}]";
  }

  private static string Number(double number)
  {
    return number.ToString("0.###", CultureInfo.InvariantCulture);
  }


}