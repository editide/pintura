using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>What changed between two versions of a presentation: the slides that differ, and the theme and layout
/// values that restyled elements, each named once.</summary>
public record DeckDiff(List<SlideDiff> Slides, List<CauseDiff> Causes);

/// <summary>A slide added, removed, changed, or only moved. Moved is whether the slide left the order the others
/// keep, so inserting a slide doesn't move the ones after it.</summary>
public record SlideDiff
{
  public string Id { get; init; } = "";
  public string Status { get; init; } = "";
  public int? Before { get; init; }
  public int? After { get; init; }
  public bool Moved { get; init; }
  /// <summary>The slide's own values: its layout, section, background, or load error.</summary>
  public List<ChangeEntry> Changes { get; init; } = new List<ChangeEntry>();
  public List<ItemDiff> Items { get; init; } = new List<ItemDiff>();
}

/// <summary>An element, the title, or a placeholder, added, removed, or changed.</summary>
public record ItemDiff(string Id, string Owner, string Kind, string Status, List<ChangeEntry> Changes);

/// <summary>A value as displayed on each side, with the line that set it. Cause is the theme or layout value both
/// sides took it from, when the slide itself didn't change it.</summary>
public record ChangeEntry(string Key, string? Before, string? After, string? BeforeOrigin, string? AfterOrigin)
{
  public string? Cause { get; init; }
}

/// <summary>A theme or layout value, named by its file and key path, and the elements it restyled.</summary>
public record CauseDiff(string Origin, string Key, string? Before, string? After, int Items, List<string> Slides);

public static partial class Differ
{
  /// <summary>Matches slides by file and elements by id, or with <paramref name="byPosition"/>, slides by number and
  /// items by kind and drawing order, for a deck whose ids didn't survive a trip through PowerPoint.</summary>
  public static DeckDiff Compare(Presentation before, Presentation after, bool byPosition = false)
  {
    Func<Slide, string> identity = byPosition ? slide => slide.Number.ToString(CultureInfo.InvariantCulture)
      : slide => slide.Id;
    Dictionary<string, Slide> earlier = before.Slides.ToDictionary(identity);
    Dictionary<string, Slide> later = after.Slides.ToDictionary(identity);
    HashSet<string> kept = Kept(
      before.Slides.Select(identity).Where(later.ContainsKey).ToList(),
      after.Slides.Select(identity).Where(earlier.ContainsKey).ToList());

    var slides = new List<SlideDiff>();
    foreach (Slide slide in after.Slides)
    {
      if (!earlier.TryGetValue(identity(slide), out Slide? previous))
      {
        slides.Add(new SlideDiff { Id = slide.Id, Status = "added", After = slide.Number });
        continue;
      }
      SlideDiff diff = Slide(before, previous, after, slide, byPosition) with
      {
        Moved = !kept.Contains(identity(slide))
      };
      if (diff.Changes.Count > 0 || diff.Items.Count > 0)
      {
        slides.Add(diff with { Status = "changed" });
      }
      else if (diff.Moved)
      {
        slides.Add(diff with { Status = "moved" });
      }
    }
    slides.AddRange(before.Slides
      .Where(slide => !later.ContainsKey(identity(slide)))
      .Select(slide => new SlideDiff { Id = slide.Id, Status = "removed", Before = slide.Number }));
    return new DeckDiff(slides, Causes(slides));
  }

  /// <summary>The longest run of slides both orders share, so the rest are the ones that moved.</summary>
  private static HashSet<string> Kept(List<string> before, List<string> after)
  {
    int[,] lengths = new int[before.Count + 1, after.Count + 1];
    for (int i = before.Count - 1; i >= 0; i--)
    {
      for (int j = after.Count - 1; j >= 0; j--)
      {
        lengths[i, j] = before[i] == after[j]
          ? lengths[i + 1, j + 1] + 1
          : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
      }
    }
    var kept = new HashSet<string>(StringComparer.Ordinal);
    int x = 0;
    int y = 0;
    while (x < before.Count && y < after.Count)
    {
      if (before[x] == after[y])
      {
        kept.Add(before[x]);
        x++;
        y++;
      }
      else if (lengths[x + 1, y] >= lengths[x, y + 1])
      {
        x++;
      }
      else
      {
        y++;
      }
    }
    return kept;
  }

  private static SlideDiff Slide(Presentation before, Slide earlier, Presentation after, Slide later,
    bool byPosition)
  {
    var diff = new SlideDiff { Id = earlier.Id, Before = earlier.Number, After = later.Number };
    if (earlier.Error is not null || later.Error is not null)
    {
      if (earlier.Error?.Message != later.Error?.Message)
      {
        diff.Changes.Add(new ChangeEntry("error", earlier.Error?.Message, later.Error?.Message, null, null));
      }
      return diff;
    }
    SlideScene first = SceneBuilder.Build(before, earlier);
    SlideScene second = SceneBuilder.Build(after, later);
    Compare(Values(before, earlier, first), Values(after, later, second), diff.Changes, before, after);

    Dictionary<SceneItem, SceneItem> pairs = Pairs(first.Items, second.Items, byPosition);
    foreach (SceneItem item in second.Items)
    {
      if (!pairs.TryGetValue(item, out SceneItem? previous))
      {
        // Ids that came back from PowerPoint name nothing in the files, so an added item shows what it draws.
        List<ChangeEntry> drawn = byPosition
          ? Values(after, later, item, true)
            .Where(value => value.Key != "kind" && (!value.Key.StartsWith("paragraph ", StringComparison.Ordinal) ||
              value.Key.Contains(" run ", StringComparison.Ordinal) ||
              value.Key.EndsWith(".align", StringComparison.Ordinal)))
            .Select(value => new ChangeEntry(value.Key, null, value.Value.Item1, null, null))
            .ToList()
          : [];
        diff.Items.Add(new ItemDiff(item.Id, item.Owner, item.Kind, "added", drawn));
        continue;
      }
      var changes = new List<ChangeEntry>();
      Dictionary<string, (string, string?)> was = Values(before, earlier, previous, byPosition);
      Dictionary<string, (string, string?)> now = Values(after, later, item, byPosition);
      if (byPosition)
      {
        Contained(was, now);
        Boxed(was, now);
        Near(was, now);
      }
      Compare(was, now, changes, before, after);
      if (changes.Count > 0)
      {
        diff.Items.Add(new ItemDiff(previous.Id, item.Owner, item.Kind, "changed", changes));
      }
    }
    HashSet<SceneItem> matched = [.. pairs.Values];
    diff.Items.AddRange(first.Items
      .Where(item => !matched.Contains(item))
      .Select(item => new ItemDiff(item.Id, item.Owner, item.Kind, "removed", [])));
    return diff;
  }

  /// <summary>Each later item with the earlier one it continues: the one with its owner and id, or by position, the
  /// first of its kind left with its id, then its frame, then its text, then in drawing order.</summary>
  private static Dictionary<SceneItem, SceneItem> Pairs(List<SceneItem> earlier, List<SceneItem> later,
    bool byPosition)
  {
    var pairs = new Dictionary<SceneItem, SceneItem>();
    if (!byPosition)
    {
      Dictionary<string, SceneItem> keyed = earlier.ToDictionary(item => $"{item.Owner}/{item.Id}");
      foreach (SceneItem item in later)
      {
        if (keyed.TryGetValue($"{item.Owner}/{item.Id}", out SceneItem? previous))
        {
          pairs[item] = previous;
        }
      }
      return pairs;
    }
    var left = new List<SceneItem>(earlier);
    Func<SceneItem, string?>[] keys =
    [
      item => item.Id,
      item => item.Properties.GetValueOrDefault("frame")?.Value is Frame frame
        ? string.Join(",", frame.Rect.Select(value => Math.Round(value))) : null,
      item => item.Paragraphs is null ? null : Text(item.Paragraphs),
      _ => ""
    ];
    foreach (Func<SceneItem, string?> key in keys)
    {
      foreach (SceneItem item in later.Where(item => !pairs.ContainsKey(item)))
      {
        SceneItem? previous = left.FirstOrDefault(candidate => Kind(candidate) == Kind(item) &&
          key(candidate) is string value && value == key(item));
        if (previous is not null)
        {
          pairs[item] = previous;
          left.Remove(previous);
        }
      }
    }
    return pairs;
  }

  /// <summary>A text element with a box draws as a shape holding text, which is how PowerPoint keeps it, so the two
  /// pair.</summary>
  private static string Kind(SceneItem item)
  {
    return item.Role == "title" ? "title" : item.Kind == "text" ? "shape" : item.Kind;
  }

  private static void Compare(Dictionary<string, (string Display, string? Origin)> before,
    Dictionary<string, (string Display, string? Origin)> after, List<ChangeEntry> changes, Presentation earlier,
    Presentation later)
  {
    foreach (string key in after.Keys.Concat(before.Keys.Where(key => !after.ContainsKey(key))))
    {
      bool had = before.TryGetValue(key, out (string Display, string? Origin) old);
      bool has = after.TryGetValue(key, out (string Display, string? Origin) current);
      if (had && has && old.Display == current.Display)
      {
        continue;
      }
      string? first = had ? Cause(old.Origin, earlier.Theme) : null;
      string? second = has ? Cause(current.Origin, later.Theme) : null;
      string? shared = had && has ? (first == second ? second : null) : first ?? second;
      string? cause = shared is null ? null : $"{later.Theme.Display}/{shared}";
      changes.Add(new ChangeEntry(key, had ? old.Display : null, has ? current.Display : null,
        had ? old.Origin : null, has ? current.Origin : null) { Cause = cause });
    }
  }

  /// <summary>The file within the theme and the key path of a value the theme or a layout sets, leaving out the line,
  /// which moves with any edit above it, and the theme's folder, which each side may name differently.</summary>
  private static string? Cause(string? origin, Theme theme)
  {
    if (origin is null || OriginPattern().Match(origin) is not { Success: true } match)
    {
      return null;
    }
    string file = match.Groups["file"].Value;
    return file.StartsWith($"{theme.Display}/", StringComparison.Ordinal)
      ? $"{file[(theme.Display.Length + 1)..]} {match.Groups["path"].Value}".TrimEnd()
      : null;
  }

  private static Dictionary<string, (string, string?)> Values(Presentation presentation, Slide slide,
    SlideScene scene)
  {
    var values = new Dictionary<string, (string, string?)>
    {
      ["layout"] = (slide.Layout, null),
      ["section"] = (slide.Section ?? "", null)
    };
    PropertyEntry background = HeadlessRunner.Property("background", scene.Background, presentation.Theme);
    values["background"] = (background.Display, background.Origin);
    return values;
  }

  /// <summary>Every value drawn for an item, as the inspector displays it, keyed by where it sits in the item.
  /// </summary>
  private static Dictionary<string, (string, string?)> Values(Presentation presentation, Slide slide,
    SceneItem item, bool asDrawn)
  {
    var values = new Dictionary<string, (string, string?)> { ["kind"] = (item.Kind, null) };
    ElementDetail detail = HeadlessRunner.Detail(presentation.Theme, slide, item);
    if (asDrawn)
    {
      // Where text takes a value from matters less than what it draws, which each paragraph and run shows.
      Add(values, "", detail.Properties.Where(property => !TextKeys.Contains(property.Key)));
      if (item.Paragraphs is not null)
      {
        values["text"] = (Text(item.Paragraphs), null);
        Add(values, "", item.Paragraphs, presentation.Theme);
      }
      foreach (SceneCell cell in item.Table?.Cells ?? [])
      {
        string name = $"cell {cell.Row},{cell.Column}";
        values[name] = (Text(cell.Paragraphs), null);
        Add(values, $"{name}.", cell.Properties.Where(pair => !TextKeys.Contains(pair.Key))
          .Select(pair => HeadlessRunner.Property(pair.Key, pair.Value, presentation.Theme)));
        Add(values, $"{name} ", cell.Paragraphs, presentation.Theme);
      }
    }
    else
    {
      Add(values, "", detail.Properties);
    }
    if (!asDrawn && item.Paragraphs is not null)
    {
      values["text"] = (Text(item.Paragraphs), null);
      foreach (ParagraphEntry paragraph in detail.Paragraphs!)
      {
        Add(values, $"paragraph {paragraph.Number}.", paragraph.Properties);
        for (int index = 0; index < paragraph.Runs.Count; index++)
        {
          Add(values, $"paragraph {paragraph.Number} run {index + 1}.", paragraph.Runs[index].Properties);
        }
      }
    }
    foreach (CellEntry cell in asDrawn ? [] : detail.Cells ?? [])
    {
      string name = $"cell {cell.Row},{cell.Column}";
      values[name] = (cell.Text, null);
      Add(values, $"{name}.", cell.Properties);
    }
    if (item.Chart is Chart chart)
    {
      // Colors past the last series draw nothing.
      if (asDrawn && values.TryGetValue("series_colors", out (string Display, string? Origin) colors))
      {
        values["series_colors"] = (string.Join(", ", colors.Display.Trim('[', ']').Split(", ")
          .Take(chart.Series.Count)), colors.Origin);
      }
      string? data = chart.Origins.GetValueOrDefault("data")?.ToString();
      values["categories"] = (string.Join(", ", chart.Categories), data);
      foreach (ChartSeries series in chart.Series)
      {
        values[$"series {series.Name}"] = (string.Join(", ", series.Values.Select(Number)), data);
      }
    }
    if (asDrawn && item.Paragraphs is not null)
    {
      values.TryAdd("fit", ("none", null));
    }
    if (asDrawn && item.Kind == "table" && values.TryGetValue("frame", out (string Display, string? Origin) frame))
    {
      // A table's height is only the room its rows have.
      values["frame"] = (string.Join(", ", Numbers(frame.Display).Take(3)), frame.Origin);
    }
    if (asDrawn && item.Chart is not null)
    {
      Labels(values);
    }
    if (asDrawn && item.Table is not null)
    {
      Shared(values);
      // Each cell draws with its own.
      values.Remove("valign");
      values.Remove("insets");
    }
    return asDrawn ? Drawn(values) : values;
  }

  /// <summary>A border between two rows draws as the upper cell's bottom or the lower one's top, which PowerPoint
  /// keeps as both, so it counts as the upper cell's bottom.</summary>
  private static void Shared(Dictionary<string, (string Display, string? Origin)> values)
  {
    foreach (string key in values.Keys.Where(key => key.StartsWith("cell ", StringComparison.Ordinal) &&
      key.Contains(".border_top", StringComparison.Ordinal)).ToList())
    {
      Match cell = CellPattern().Match(key);
      int row = int.Parse(cell.Groups["row"].Value, CultureInfo.InvariantCulture);
      string above = $"cell {row - 1},{cell.Groups["column"].Value}" +
        key[cell.Length..].Replace(".border_top", ".border_bottom", StringComparison.Ordinal);
      if (row > 1 && !values.ContainsKey(above))
      {
        values[above] = values[key];
      }
      if (row > 1 && values[above].Display == values[key].Display)
      {
        values.Remove(key);
      }
    }
  }

  /// <summary>Labels as drawn: those not shown, on the chart or a series that takes the chart's, draw nothing, and
  /// those shown with no color of their own draw in the chart's text color.</summary>
  private static void Labels(Dictionary<string, (string Display, string? Origin)> values)
  {
    string? Shown(string prefix) => values.TryGetValue($"{prefix}data_labels.show", out (string Display, string?) show)
      ? show.Display
      : prefix.Length > 0 ? Shown("") : null;
    List<string> prefixes = values.Keys.Where(key => key.Contains("data_labels.", StringComparison.Ordinal))
      .Select(key => key[..key.IndexOf("data_labels.", StringComparison.Ordinal)]).Distinct().ToList();
    foreach (string prefix in prefixes)
    {
      if (Shown(prefix) is null or "none")
      {
        foreach (string key in values.Keys
          .Where(key => key.StartsWith($"{prefix}data_labels.", StringComparison.Ordinal)).ToList())
        {
          values.Remove(key);
        }
      }
      else if (values.TryGetValue("text.color", out (string Display, string? Origin) color))
      {
        values.TryAdd($"{prefix}data_labels.color", color);
      }
    }
  }

  /// <summary>A picture fit inside its frame draws as one stretched to the rectangle it fills, as PowerPoint keeps it,
  /// so the two are the same where one's rectangle is the other's, centred, touching two sides.</summary>
  private static void Contained(Dictionary<string, (string Display, string? Origin)> before,
    Dictionary<string, (string Display, string? Origin)> after)
  {
    string? Fit(Dictionary<string, (string Display, string? Origin)> values) =>
      values.TryGetValue("fit", out (string Display, string? Origin) fit) ? fit.Display : null;
    if (before["kind"].Display != "picture" || after["kind"].Display != "picture" || Fit(before) == Fit(after) ||
      !before.TryGetValue("frame", out (string Display, string? Origin) outer) ||
      !after.TryGetValue("frame", out (string Display, string? Origin) inner))
    {
      return;
    }
    (double[] room, double[] drawn) = Fit(before) == "contain" ? (Numbers(outer.Display), Numbers(inner.Display))
      : (Numbers(inner.Display), Numbers(outer.Display));
    bool centred = Math.Abs(room[0] + room[2] / 2 - drawn[0] - drawn[2] / 2) < 0.5 &&
      Math.Abs(room[1] + room[3] / 2 - drawn[1] - drawn[3] / 2) < 0.5;
    if (room.Length == 4 && drawn.Length == 4 && centred &&
      (Math.Abs(room[2] - drawn[2]) < 0.5 || Math.Abs(room[3] - drawn[3]) < 0.5))
    {
      foreach (string key in new[] { "frame", "fit" })
      {
        before.Remove(key);
        after.Remove(key);
      }
    }
  }

  /// <summary>A text element draws as a rectangle holding its text, so the two differ in neither kind nor shape.
  /// </summary>
  private static void Boxed(Dictionary<string, (string Display, string? Origin)> before,
    Dictionary<string, (string Display, string? Origin)> after)
  {
    string[] kinds = [before["kind"].Display, after["kind"].Display];
    Dictionary<string, (string Display, string? Origin)> shape = kinds[0] == "shape" ? before : after;
    if (kinds.Order().SequenceEqual(["shape", "text"]) &&
      (!shape.TryGetValue("shape", out (string Display, string? Origin) drawn) || drawn.Display == "rectangle"))
    {
      foreach (string key in new[] { "kind", "shape" })
      {
        before.Remove(key);
        after.Remove(key);
      }
    }
  }

  /// <summary>A frame that comes back within a point of where it was hasn't moved: PowerPoint keeps frames in EMUs,
  /// and fits a picture to its own aspect.</summary>
  private static void Near(Dictionary<string, (string Display, string? Origin)> before,
    Dictionary<string, (string Display, string? Origin)> after)
  {
    if (before.TryGetValue("frame", out (string Display, string? Origin) was) &&
      after.TryGetValue("frame", out (string Display, string? Origin) now))
    {
      double[] first = Numbers(was.Display);
      double[] second = Numbers(now.Display);
      if (first.Length == second.Length && first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < 1))
      {
        after["frame"] = was;
      }
    }
  }

  private static double[] Numbers(string display)
  {
    return NumberPattern().Matches(display).Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
      .ToArray();
  }

  /// <summary>The values as PowerPoint keeps them: without the names of styles, formats, and files, which it doesn't
  /// carry, with each color by its hex, whatever the theme calls it, and without what draws nothing, as unset doesn't.
  /// </summary>
  private static Dictionary<string, (string, string?)> Drawn(Dictionary<string, (string, string?)> values)
  {
    var drawn = new Dictionary<string, (string, string?)>();
    foreach ((string key, (string display, string? origin)) in values)
    {
      string last = key[(key.LastIndexOf('.') + 1)..];
      if (last is "style" or "row_style" or "column_style" or "format" or "chart" or "table" or "data" or "picture" or
        "series_in")
      {
        continue;
      }
      string value = Hex(display);
      if (last is "fill" or "stroke" or "shadow" or "legend" or "show" or "fit" && value == "none" ||
        last == "gridlines" && value == "false")
      {
        continue;
      }
      // A frame in an area shows the area's name before its rectangle.
      drawn[key] = (last == "frame" && value.IndexOf('[') is > 0 and int bracket ? value[bracket..] : value, origin);
    }
    return drawn;
  }

  private static readonly Dictionary<string, string> Unset = new Dictionary<string, string>
  {
    ["space_before"] = "0", ["space_after"] = "0", ["line_spacing"] = "1", ["bold"] = "false", ["italic"] = "false",
    ["underline"] = "false", ["bullet_size"] = "1"
  };

  private static readonly HashSet<string> TextKeys = [.. TextProperties.Character, .. TextProperties.Paragraph];

  /// <summary>Each paragraph as drawn: its bullet and indents, its paragraph values, and its runs, neighbours drawn
  /// alike joined, whatever styles or markup gave them their values.</summary>
  private static void Add(Dictionary<string, (string, string?)> values, string prefix,
    List<SceneParagraph> paragraphs, Theme theme)
  {
    string Show(string key, Resolved? resolved) => resolved is null ? Unset.GetValueOrDefault(key, "")
      : Hex(HeadlessRunner.Property(key, resolved, theme).Display);
    // Text with no words draws nothing, however its paragraphs are set.
    if (paragraphs.All(paragraph => paragraph.Runs.All(run => run.Text.Length == 0 && run.Field is null)))
    {
      return;
    }
    for (int index = 0; index < paragraphs.Count; index++)
    {
      SceneParagraph paragraph = paragraphs[index];
      string name = $"{prefix}paragraph {index + 1}";
      // An unset bullet takes the text's color.
      string bulletColor = Show("bullet_color", paragraph.BulletColor ?? (paragraph.Bullet is null ? null
        : paragraph.Runs.FirstOrDefault()?.Properties.GetValueOrDefault("color")));
      string glyph = Show("bullet", paragraph.Bullet);
      string size = Show("bullet_size", paragraph.BulletSize);
      List<string> bullet = [glyph.Length == 0 || glyph == "none" ? "none"
        : $"{glyph} {bulletColor}{(size == "1" ? "" : $" ×{size}")}"];
      foreach ((string key, Resolved? value) in new[] { ("indent", paragraph.Indent), ("hanging", paragraph.Hanging) })
      {
        if (Show(key, value) is { Length: > 0 } shown && shown != "0")
        {
          bullet.Add($"{key} {shown}");
        }
      }
      values[$"{name}.bullet"] = (string.Join(", ", bullet), null);
      foreach (string key in TextProperties.Paragraph)
      {
        values[$"{name}.{key}"] = (Show(key, paragraph.Properties.GetValueOrDefault(key)), null);
      }
      var runs = new List<(string Text, string Look)>();
      foreach (SceneRun run in paragraph.Runs)
      {
        string look = string.Join(" ", TextProperties.Character.Select(key =>
          $"{key}={Show(key, run.Properties.GetValueOrDefault(key))}"));
        runs.Add(runs.Count > 0 && runs[^1].Look == look ? (runs[^1].Text + run.Text, look) : (run.Text, look));
        if (runs.Count > 1 && runs[^2].Look == look)
        {
          runs.RemoveAt(runs.Count - 2);
        }
      }
      for (int number = 0; number < runs.Count; number++)
      {
        values[$"{name} run {number + 1}"] = ($"{runs[number].Text} {{{runs[number].Look}}}", null);
      }
    }
  }

  private static string Hex(string display)
  {
    return HexPattern().Replace(display, "$1");
  }

  private static void Add(Dictionary<string, (string, string?)> values, string prefix,
    IEnumerable<PropertyEntry> properties)
  {
    foreach (PropertyEntry property in properties)
    {
      values[$"{prefix}{property.Key}"] = (property.Display, property.Origin);
    }
  }

  /// <summary>The words as drawn, with the slide number left as its field, since it changes whenever a slide is
  /// added or removed before it.</summary>
  private static string Text(List<SceneParagraph> paragraphs)
  {
    return string.Join("\n", paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run =>
      run.Field == "slide" ? "${slide}" : run.Text))));
  }

  private static string Number(double? value)
  {
    return value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "–";
  }

  private static List<CauseDiff> Causes(List<SlideDiff> slides)
  {
    return slides
      .SelectMany(slide => slide.Items.SelectMany(item => item.Changes
        .Where(change => change.Cause is not null)
        .Select(change => (Slide: slide.Id, Item: $"{slide.Id}/{item.Owner}/{item.Id}", Change: change))))
      .GroupBy(entry => entry.Change.Cause!)
      .Select(group =>
      {
        ChangeEntry first = group.First().Change;
        return new CauseDiff(group.Key, first.Key, first.Before, first.After,
          group.Select(entry => entry.Item).Distinct().Count(), group.Select(entry => entry.Slide).Distinct().ToList());
      })
      .ToList();
  }

  [GeneratedRegex(@"[A-Za-z_0-9]+(?: (?:lighter|darker) \d+)? \((#[0-9A-Fa-f]{6,8})\)")]
  private static partial Regex HexPattern();

  [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
  private static partial Regex NumberPattern();

  [GeneratedRegex(@"^cell (?<row>\d+),(?<column>\d+)")]
  private static partial Regex CellPattern();

  [GeneratedRegex(@"^(?<file>.*?):\d+(?: (?<path>\S.*))?$")]
  private static partial Regex OriginPattern();


}

/// <summary><c>--diff</c>: what changed since a revision, or between two, as a listing or JSON.</summary>
public static partial class DiffCommand
{
  /// <summary>What the export can't carry yet: the tools write no sections or alt text and normalize a callout's
  /// tail, so a deck shows these changed though no one changed them.</summary>
  public static readonly string[] ExportGaps = ["section", "tail", "alt", "decorative"];

  public static bool IsDeck(string revision)
  {
    return Path.GetExtension(revision).ToLowerInvariant() is ".pptx" or ".potx";
  }

  public static int Run(string root, string revision, bool json, TextWriter output, SlideSelection? only = null)
  {
    if (IsDeck(revision))
    {
      return RunDeck(root, revision, json, only, output);
    }
    string[] ends = revision.Split("..", 2);
    string first = ends[0].Length == 0 ? "HEAD" : ends[0];
    Presentation before = Revisions.Load(root, first);
    Presentation after = ends.Length == 1 ? PresentationLoader.Load(root) : Revisions.Load(root, ends[1]);
    DeckDiff diff = Differ.Compare(before, after);
    if (json)
    {
      output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diff, PayloadJson.Indented));
      return 0;
    }
    string Name(string name)
    {
      return $"{name} ({Revisions.Commit(root, name)[..7]})";
    }
    Write(diff, $"{Name(first)} → {(ends.Length == 1 ? "the working tree" : Name(ends[1]))}", output);
    return 0;
  }

  private static int RunDeck(string root, string deck, bool json, SlideSelection? only, TextWriter output)
  {
    string source = Path.GetFullPath(deck);
    if (!File.Exists(source))
    {
      throw new ArgumentException($"no file at {source}");
    }
    Presentation presentation = PresentationLoader.Load(root);
    (DeckDiff diff, List<string> findings) = Against(presentation, DeckReads.Read(source, ToolHosts.Default()),
      source);
    if (only is not null)
    {
      diff = Only(diff, presentation, only);
    }
    if (json)
    {
      output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { diff.Slides, diff.Causes, Unread = findings },
        PayloadJson.Indented));
      return 0;
    }
    string heading = $"{Path.GetFileName(Path.GetFullPath(root))} → {Path.GetFileName(source)}";
    if (only is null)
    {
      Overview(diff, heading, output);
      if (diff.Slides.Count > 0)
      {
        output.WriteLine();
        output.WriteLine($"--diff {deck} --slides <list> lists each change on the slides it names.");
      }
    }
    else
    {
      Write(diff, heading, output);
    }
    if (findings.Count > 0)
    {
      output.WriteLine();
      output.WriteLine("not compared, since the deck's reading of them didn't land:");
      foreach (string finding in findings)
      {
        output.WriteLine($"- {finding}");
      }
    }
    return 0;
  }

  /// <summary>What <paramref name="deck"/>, as editide's <paramref name="reads"/> see it, changed from
  /// <paramref name="presentation"/>: slides paired by number and items by kind, frame, and text, since the export
  /// keeps no ids, named by the presentation's ids. Values compare as drawn, so a number a reference renders
  /// matches the same number typed in PowerPoint. The findings are what the deck's import couldn't read.</summary>
  public static (DeckDiff Diff, List<string> Findings) Against(Presentation presentation, JsonObject reads,
    string deck)
  {
    string scratch = Temporary.Folder("compare", Guid.NewGuid().ToString("N"));
    try
    {
      PowerPointDeck mapped = PowerPointMapping.Map(reads, deck, Path.Combine(scratch, "media"));
      ImportResult imported = Importer.Import(mapped.Deck.ToJsonString(), mapped.Media,
        Path.Combine(scratch, "presentation"));
      Presentation read = PresentationLoader.Load(Path.Combine(scratch, "presentation"));
      DeckDiff diff = Differ.Compare(presentation, read, byPosition: true);
      // The import's findings name its own slide ids, which mean nothing in the presentation.
      string Named(string finding)
      {
        foreach (Slide slide in read.Slides)
        {
          string ours = presentation.Slides.ElementAtOrDefault(slide.Number - 1)?.Id ?? $"slide {slide.Number}";
          finding = finding.Replace($"slide:{slide.Id}/", $"{ours}/", StringComparison.Ordinal);
        }
        return finding;
      }
      // The deck's side takes its values from a theme the import made up, so no change is the theme's doing.
      var own = new DeckDiff(diff.Slides.Select(slide => slide with
      {
        Items = slide.Items.Select(item => item with
        {
          Changes = item.Changes.Select(change => change with { Cause = null }).ToList()
        }).ToList()
      }).ToList(), []);
      // The rest of the import's findings say how its files differ from the deck, which the diff already compares.
      IEnumerable<string> unread = imported.Findings
        .Where(finding => finding.Contains("left out", StringComparison.Ordinal));
      return (Without(own, ExportGaps), [.. mapped.Findings.Concat(unread).Select(Named).Distinct()]);
    }
    finally
    {
      if (Directory.Exists(scratch))
      {
        Directory.Delete(scratch, true);
      }
    }
  }

  /// <summary>The slides of <paramref name="diff"/> that <paramref name="only"/> names in
  /// <paramref name="presentation"/>, by the number each had there, or for a slide the deck added, has.</summary>
  public static DeckDiff Only(DeckDiff diff, Presentation presentation, SlideSelection only)
  {
    HashSet<int> numbers = [.. only.Pick(presentation.Slides, slide => slide.Id).Select(slide => slide.Number)];
    List<SlideDiff> slides = diff.Slides.Where(slide => numbers.Contains(slide.Before ?? slide.After ?? 0)).ToList();
    return new DeckDiff(slides, []);
  }

  /// <summary>A line a slide, counting what changed on it, for choosing the slides to list in full.</summary>
  public static void Overview(DeckDiff diff, string heading, TextWriter output)
  {
    output.WriteLine(heading);
    output.WriteLine();
    if (diff.Slides.Count == 0)
    {
      output.WriteLine("no changes");
      return;
    }
    int width = diff.Slides.Max(slide => slide.Id.Length);
    foreach (SlideDiff slide in diff.Slides)
    {
      string number = (slide.After ?? slide.Before)?.ToString(CultureInfo.InvariantCulture) ?? "–";
      var counts = new OrderedDictionary<string, int>
      {
        ["reworded"] = 0, ["moved"] = 0, ["restyled"] = 0, ["added"] = 0, ["removed"] = 0
      };
      foreach (ItemDiff item in slide.Items)
      {
        if (item.Status != "changed")
        {
          counts[item.Status]++;
          continue;
        }
        (bool reworded, bool moved, bool restyled) = Kinds(item.Changes);
        counts["reworded"] += reworded ? 1 : 0;
        counts["moved"] += moved ? 1 : 0;
        counts["restyled"] += restyled ? 1 : 0;
      }
      List<string> parts = [.. slide.Changes.Select(change => $"{change.Key} changed"),
        .. counts.Where(pair => pair.Value > 0).Select(pair => $"{pair.Value} {pair.Key}")];
      string status = slide.Status is "added" or "removed" or "moved" ? slide.Status : string.Join(", ", parts);
      output.WriteLine($"{number,2} {slide.Id.PadRight(width)}  {status}");
    }
  }

  /// <summary>Whether an item's changes reword it, move it, or restyle it; a run's look changing with its words is
  /// the rewording.</summary>
  public static (bool Reworded, bool Moved, bool Restyled) Kinds(List<ChangeEntry> changes)
  {
    bool reworded = changes.Any(change => change.Key == "text" || CellText().IsMatch(change.Key));
    bool moved = changes.Any(change => change.Key is "frame" or "from" or "to");
    bool restyled = changes.Any(change => change.Key is not ("text" or "frame" or "from" or "to") &&
      !CellText().IsMatch(change.Key) && !(reworded && change.Key.Contains(" run ", StringComparison.Ordinal)));
    return (reworded, moved, restyled);
  }

  [GeneratedRegex(@"^cell \d+,\d+$")]
  private static partial Regex CellText();

  /// <summary><paramref name="diff"/> without changes to <paramref name="keys"/>, and without the items and slides
  /// left unchanged once they go.</summary>
  private static DeckDiff Without(DeckDiff diff, string[] keys)
  {
    var slides = new List<SlideDiff>();
    foreach (SlideDiff slide in diff.Slides)
    {
      List<ChangeEntry> changes = slide.Changes.Where(change => !keys.Contains(change.Key)).ToList();
      List<ItemDiff> items = slide.Items
        .Select(item => item with { Changes = item.Changes.Where(change => !keys.Contains(change.Key)).ToList() })
        .Where(item => item.Status != "changed" || item.Changes.Count > 0)
        .ToList();
      if (slide.Status != "changed" || changes.Count > 0 || items.Count > 0)
      {
        slides.Add(slide with { Changes = changes, Items = items });
      }
      else if (slide.Moved)
      {
        slides.Add(slide with { Status = "moved", Changes = changes, Items = items });
      }
    }
    return new DeckDiff(slides, diff.Causes.Where(cause => !keys.Contains(cause.Key)).ToList());
  }

  public static void Write(DeckDiff diff, string heading, TextWriter output)
  {
    output.WriteLine(heading);
    output.WriteLine();
    if (diff.Slides.Count == 0)
    {
      output.WriteLine("no changes");
      return;
    }
    int width = diff.Slides.Max(slide => slide.Id.Length);
    foreach (SlideDiff slide in diff.Slides)
    {
      string number = slide.After?.ToString(CultureInfo.InvariantCulture) ?? "–";
      string status = slide.Status switch
      {
        "removed" => $"removed, was {slide.Before}",
        "moved" => $"moved from {slide.Before}",
        "changed" when slide.Moved => $"changed, moved from {slide.Before}",
        _ => slide.Status
      };
      output.WriteLine($"{number,2} {slide.Id.PadRight(width)}  {status}");
      Changes(slide.Changes, "    ", output);
      int restyled = 0;
      foreach (ItemDiff item in slide.Items)
      {
        List<ChangeEntry> own = item.Changes.Where(change => change.Cause is null).ToList();
        if (item.Status == "changed" && own.Count == 0)
        {
          restyled++;
          continue;
        }
        string mark = item.Status switch { "added" => "+", "removed" => "-", _ => "~" };
        output.WriteLine(item.Status == "changed" ? $"    {mark} {item.Id}" : $"    {mark} {item.Id}  {item.Kind}");
        if (item.Status == "added")
        {
          int keys = own.Count == 0 ? 0 : own.Max(change => change.Key.Length);
          foreach (ChangeEntry change in own)
          {
            output.WriteLine($"        {change.Key.PadRight(keys)}  {Value(change.After)}");
          }
          continue;
        }
        Changes(own, "        ", output);
      }
      if (restyled > 0)
      {
        output.WriteLine($"    restyled by the theme: {Count(restyled, "element")}");
      }
    }
    if (diff.Causes.Count > 0)
    {
      output.WriteLine();
    }
    foreach (CauseDiff cause in diff.Causes)
    {
      output.WriteLine($"{cause.Origin}: {Value(cause.Before)} → {Value(cause.After)} on " +
        $"{Count(cause.Items, "element")} across {Count(cause.Slides.Count, "slide")}");
    }
  }

  private static void Changes(List<ChangeEntry> changes, string indent, TextWriter output)
  {
    changes = Collapsed(changes);
    int width = changes.Count == 0 ? 0 : changes.Max(change => change.Key.Length);
    foreach (ChangeEntry change in changes)
    {
      if (change.Before is null && ParagraphKey().Match($"{change.Key}.").Groups["paragraph"].Value == change.Key)
      {
        output.WriteLine($"{indent}{change.Key.PadRight(width)}  {change.After}");
        continue;
      }
      (string? before, string? after) = change.Before is not null && change.After is not null &&
        Math.Max(change.Before.Length, change.After.Length) > 60
          ? Excerpts(change.Before, change.After)
          : (change.Before, change.After);
      output.WriteLine($"{indent}{change.Key.PadRight(width)}  {Value(before)} → {Value(after)}");
    }
  }

  /// <summary>The changes as read: a paragraph added or removed whole as one line with its words, and a run that
  /// only rewords left to the text's own line.</summary>
  private static List<ChangeEntry> Collapsed(List<ChangeEntry> changes)
  {
    bool reworded = changes.Any(change => change.Key == "text" || CellText().IsMatch(change.Key));
    var whole = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (IGrouping<string, ChangeEntry> paragraph in changes
      .Where(change => ParagraphKey().IsMatch(change.Key))
      .GroupBy(change => ParagraphKey().Match(change.Key).Groups["paragraph"].Value))
    {
      string words = string.Concat(paragraph.Where(change => change.Key.Contains(" run ", StringComparison.Ordinal))
        .Select(change => Look().Replace(change.Before ?? change.After ?? "", "")));
      if (paragraph.All(change => change.After is null))
      {
        whole[paragraph.Key] = $"removed: {words}".TrimEnd();
      }
      else if (paragraph.All(change => change.Before is null))
      {
        whole[paragraph.Key] = $"added: {words}".TrimEnd();
      }
    }
    var collapsed = new List<ChangeEntry>();
    var listed = new HashSet<string>(StringComparer.Ordinal);
    foreach (ChangeEntry change in changes)
    {
      Match match = ParagraphKey().Match(change.Key);
      string? paragraph = match.Success ? match.Groups["paragraph"].Value : null;
      if (paragraph is not null && whole.TryGetValue(paragraph, out string? line))
      {
        if (listed.Add(paragraph))
        {
          collapsed.Add(new ChangeEntry(paragraph, null, line, null, null));
        }
        continue;
      }
      bool rewordOnly = reworded && change.Key.Contains(" run ", StringComparison.Ordinal) &&
        change.Before is not null && change.After is not null &&
        Look().Match(change.Before).Value == Look().Match(change.After).Value;
      if (!rewordOnly)
      {
        collapsed.Add(change);
      }
    }
    return collapsed;
  }

  [GeneratedRegex(@"^(?<paragraph>(?:cell \d+,\d+ )?paragraph \d+)(?:\.| run )")]
  private static partial Regex ParagraphKey();

  [GeneratedRegex(@" ?\{[^{}]*\}$")]
  private static partial Regex Look();

  /// <summary>A long value by the words that changed, with two words of what each side shares around them.</summary>
  private static (string, string) Excerpts(string before, string after)
  {
    string[] first = Words().Matches(before).Select(match => match.Value).ToArray();
    string[] second = Words().Matches(after).Select(match => match.Value).ToArray();
    int prefix = 0;
    while (prefix < first.Length && prefix < second.Length && first[prefix] == second[prefix])
    {
      prefix++;
    }
    int suffix = 0;
    while (suffix < first.Length - prefix && suffix < second.Length - prefix &&
      first[^(suffix + 1)] == second[^(suffix + 1)])
    {
      suffix++;
    }
    int lead = Math.Max(0, prefix - 4);
    string Excerpt(string[] words)
    {
      int end = Math.Min(words.Length, words.Length - suffix + 4);
      return $"{(lead > 0 ? "…" : "")}{string.Concat(words[lead..end])}{(end < words.Length ? "…" : "")}";
    }
    return (Excerpt(first), Excerpt(second));
  }

  private static string Value(string? value)
  {
    if (value is null)
    {
      return "(unset)";
    }
    string line = value.Replace("\n", " / ");
    return line.Length <= 60 ? line : $"{line[..59]}…";
  }

  private static string Count(int count, string noun)
  {
    return $"{count} {noun}{(count == 1 ? "" : "s")}";
  }

  [GeneratedRegex(@"\s+|\S+")]
  private static partial Regex Words();


}