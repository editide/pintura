using System.Globalization;
using System.Text.Json;

namespace Pintura;

/// <summary>A slide's words as <c>--outline</c> shows them; <paramref name="Pick"/> and <paramref name="Intent"/> name
/// the open pick offering it and what the variant tries to show.</summary>
public record OutlineSlide(int Number, string Id, string? Section, string? Title, List<OutlineElement> Elements,
  string? Pick = null, string? Intent = null, Rejection? Rejected = null, string? Error = null);

/// <summary>The title aside, the slide's own content and its placeholders: text by paragraph, a chart's data, a
/// table's cells by row, or a picture's alt text, falling back to its file name.</summary>
public record OutlineElement(string Id, string Kind, List<OutlineParagraph>? Paragraphs = null,
  OutlineChart? Chart = null, OutlineTable? Table = null, string? Picture = null);

public record OutlineTable(string Stem, List<List<string>> Rows);

public record OutlineParagraph(int Level, string Text);

/// <summary>A chart's values, each series' spelled in its labels' format, or failing that its axis's; a null is an
/// empty cell.</summary>
public record OutlineChart(string Stem, string Type, string? Title, List<string> Categories,
  List<OutlineSeries> Series);

public record OutlineSeries(string Name, List<string?> Values);

/// <summary><c>--outline</c>: what a reader of the deck reads, slide by slide, for reviewing the story and its wording
/// without the frames and styles <c>--slide</c> prints.</summary>
public static class Outline
{
  public static int Write(Presentation presentation, bool json, TextWriter output, SlideSelection? only = null)
  {
    IEnumerable<Slide> slides = only is null
      ? presentation.Offered
      : presentation.WithVariants(only.Pick(presentation.Slides, slide => slide.Id));
    List<OutlineSlide> outline = [.. slides.Select(slide => Build(presentation, slide))];
    int exit = outline.Any(slide => slide.Error is not null) ? 1 : 0;
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(outline, PayloadJson.Indented));
      return exit;
    }
    string? section = null;
    string? pick = null;
    foreach (OutlineSlide slide in outline)
    {
      if (slide.Section != section)
      {
        section = slide.Section;
        output.WriteLine($"   {section}");
      }
      if (slide.Pick is not null && slide.Pick != pick)
      {
        output.WriteLine($"   pick {slide.Pick}, waiting on the reviewer to keep one of:");
      }
      pick = slide.Pick;
      output.WriteLine($"{slide.Number,2} {slide.Id}  {(slide.Error is null ? slide.Title : $"error: {slide.Error}")}"
        .TrimEnd());
      if (slide.Intent is not null)
      {
        output.WriteLine($"     intent: {slide.Intent}");
      }
      if (slide.Rejected is Rejection rejected)
      {
        output.WriteLine($"     rejected in pick {rejected.Pick}" + (rejected.Why is null ? "" : $": {rejected.Why}"));
      }
      foreach (OutlineElement element in slide.Elements)
      {
        WriteElement(element, output);
      }
    }
    return exit;
  }

  public static OutlineSlide Build(Presentation presentation, Slide slide)
  {
    string? intent = slide.Pick is null
      ? null
      : presentation.Picks.FirstOrDefault(pick => pick.Id == slide.Pick)?.Variants
        .FirstOrDefault(variant => variant.Slide == slide.Id)?.Intent;
    if (slide.Error is not null)
    {
      return new OutlineSlide(slide.Number, slide.Id, slide.Section, null, [], slide.Pick, intent, slide.Rejected,
        slide.Error.Message);
    }
    SlideScene scene = SceneBuilder.Build(presentation, slide);
    List<OutlineElement> elements = [.. ReadingOrder(scene.Items.Where(Read).ToList())
      .Select(item => Element(presentation, item))
      .OfType<OutlineElement>()];
    string title = PayloadBuilder.Title(presentation, slide).ReplaceLineEndings(" ");
    return new OutlineSlide(slide.Number, slide.Id, slide.Section, title.Length == 0 ? null : title, elements,
      slide.Pick, intent, slide.Rejected);
  }

  /// <summary>The slide's placeholders and own content; the layout's and theme's elements repeat on every slide using
  /// them, and decoration is not read.</summary>
  private static bool Read(SceneItem item)
  {
    return item.Role == "placeholder" ||
      item.Role is null && item.Owner == "slide" && item.Element?.Decorative != true;
  }

  /// <summary>Rows of items whose frames overlap vertically, top to bottom, each left to right, so a column beside a
  /// chart reads before the chart and the source under both reads last.</summary>
  private static List<SceneItem> ReadingOrder(List<SceneItem> items)
  {
    var framed = items
      .Select(item => (Item: item, Rect: (item.Properties.GetValueOrDefault("frame")?.Value as Frame)?.Rect ??
        [0, 0, 0, 0]))
      .OrderBy(each => each.Rect[1])
      .ThenBy(each => each.Rect[0])
      .ToList();
    var ordered = new List<SceneItem>();
    int start = 0;
    while (start < framed.Count)
    {
      double bottom = framed[start].Rect[1] + framed[start].Rect[3];
      int end = start + 1;
      while (end < framed.Count && framed[end].Rect[1] < bottom)
      {
        bottom = Math.Max(bottom, framed[end].Rect[1] + framed[end].Rect[3]);
        end++;
      }
      ordered.AddRange(framed.Skip(start).Take(end - start).OrderBy(each => each.Rect[0]).ThenBy(each => each.Rect[1])
        .Select(each => each.Item));
      start = end;
    }
    return ordered;
  }

  private static OutlineElement? Element(Presentation presentation, SceneItem item)
  {
    string kind = item.Role ?? item.Kind;
    if (item.Chart is Chart chart)
    {
      return new OutlineElement(item.Id, kind, Chart: Chart(presentation, item, chart));
    }
    if (item.Table is SceneTable table)
    {
      return new OutlineElement(item.Id, kind, Table: new OutlineTable(item.Element?.Table ?? "", Rows(table)));
    }
    if (item.Element?.Picture is string path)
    {
      return new OutlineElement(item.Id, kind, Picture: item.Element.Alt ?? Path.GetFileName(path));
    }
    List<OutlineParagraph> paragraphs = [.. (item.Paragraphs ?? [])
      .Select(paragraph => new OutlineParagraph(paragraph.Level,
        string.Concat(paragraph.Runs.Select(run => run.Text)).ReplaceLineEndings(" ")))
      .Where(paragraph => paragraph.Text.Trim().Length > 0)];
    return paragraphs.Count == 0 ? null : new OutlineElement(item.Id, kind, paragraphs);
  }

  /// <summary>Each row's cells, a merge's text in its first cell and an empty one in each other it covers, so a
  /// column reads down the rows.</summary>
  private static List<List<string>> Rows(SceneTable table)
  {
    if (table.Cells.Count == 0)
    {
      return [];
    }
    Dictionary<(int, int), string> first = table.Cells.ToDictionary(cell => (cell.Row, cell.Column),
      cell => Text(cell.Paragraphs));
    int top = table.Cells.Min(cell => cell.Row);
    int left = table.Cells.Min(cell => cell.Column);
    int bottom = table.Cells.Max(cell => cell.Row + cell.Rows - 1);
    int right = table.Cells.Max(cell => cell.Column + cell.Columns - 1);
    return [.. Enumerable.Range(top, bottom - top + 1).Select(row => Enumerable.Range(left, right - left + 1)
      .Select(column => first.GetValueOrDefault((row, column), "")).ToList())];
  }

  private static OutlineChart Chart(Presentation presentation, SceneItem item, Chart chart)
  {
    string? Named(string key) => item.Properties.GetValueOrDefault(key)?.Value as string;
    string Spell(string series, double value)
    {
      string axis = Named($"series.{series}.axis") == "secondary" ? "secondary_axis" : "value_axis";
      string? name = Named($"series.{series}.data_labels.format") ?? Named("data_labels.format") ??
        Named($"{axis}.format");
      return name is not null && presentation.Theme.Styles.Formats.GetValueOrDefault(name) is { Date: null } format
        ? format.Spell(value)
        : value.ToString("0.###", CultureInfo.InvariantCulture);
    }
    return new OutlineChart(chart.Id, chart.Type, Named("title"), chart.Categories,
      [.. chart.Series.Select(series => new OutlineSeries(series.Name,
        [.. series.Values.Select(value => value is double number ? Spell(series.Name, number) : null)]))]);
  }

  private static string Text(List<SceneParagraph> paragraphs)
  {
    return string.Join(" / ", paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text)))
      .Where(text => text.Length > 0));
  }

  private static void WriteElement(OutlineElement element, TextWriter output)
  {
    const string Indent = "     ";
    const string Nested = "       ";
    if (element.Chart is OutlineChart chart)
    {
      string title = chart.Title is null ? "" : $", titled {chart.Title}";
      output.WriteLine($"{Indent}{element.Id}: chart {chart.Stem}, {chart.Type}{title}");
      output.WriteLine($"{Nested}categories: {string.Join(" | ", chart.Categories)}");
      foreach (OutlineSeries series in chart.Series)
      {
        output.WriteLine($"{Nested}{series.Name}: {string.Join(" | ", series.Values.Select(value => value ?? "–"))}");
      }
      return;
    }
    if (element.Table is OutlineTable table)
    {
      output.WriteLine($"{Indent}{element.Id}: table {table.Stem}");
      foreach (List<string> row in table.Rows)
      {
        output.WriteLine($"{Nested}{string.Join(" | ", row)}".TrimEnd());
      }
      return;
    }
    if (element.Picture is string picture)
    {
      output.WriteLine($"{Indent}{element.Id}: picture {picture}");
      return;
    }
    List<OutlineParagraph> paragraphs = element.Paragraphs!;
    if (paragraphs is [{ Level: 0 } only])
    {
      output.WriteLine($"{Indent}{element.Id}: {only.Text}");
      return;
    }
    output.WriteLine($"{Indent}{element.Id}");
    foreach (OutlineParagraph paragraph in paragraphs)
    {
      string bullet = paragraph.Level == 0 ? "" : new string(' ', 2 * (paragraph.Level - 1)) + "- ";
      output.WriteLine($"{Nested}{bullet}{paragraph.Text}");
    }
  }


}