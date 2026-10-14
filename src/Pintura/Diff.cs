using System.Globalization;
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
        diff.Items.Add(new ItemDiff(item.Id, item.Owner, item.Kind, "added", []));
        continue;
      }
      var changes = new List<ChangeEntry>();
      Dictionary<string, (string, string?)> was = Values(before, earlier, previous, byPosition);
      Dictionary<string, (string, string?)> now = Values(after, later, item, byPosition);
      if (byPosition)
      {
        Contained(was, now);
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

  private static string Kind(SceneItem item)
  {
    return item.Role == "title" ? "title" : item.Kind;
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
    return asDrawn ? Drawn(values) : values;
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

  private static double[] Numbers(string display)
  {
    return NumberPattern().Matches(display).Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
      .ToArray();
  }

  /// <summary>The values as PowerPoint keeps them: without the names of styles, formats, and files, which it doesn't
  /// carry, and with each color by its hex, whatever the theme calls it.</summary>
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
    for (int index = 0; index < paragraphs.Count; index++)
    {
      SceneParagraph paragraph = paragraphs[index];
      string name = $"{prefix}paragraph {index + 1}";
      // An unset bullet takes the text's color.
      string bulletColor = Show("bullet_color", paragraph.BulletColor ?? (paragraph.Bullet is null ? null
        : paragraph.Runs.FirstOrDefault()?.Properties.GetValueOrDefault("color")));
      values[$"{name}.bullet"] = ($"{Show("bullet", paragraph.Bullet)} {bulletColor} " +
        $"{Show("bullet_size", paragraph.BulletSize)} {Show("indent", paragraph.Indent)} " +
        $"{Show("hanging", paragraph.Hanging)}".Trim(), null);
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

  [GeneratedRegex(@"^(?<file>.*?):\d+(?: (?<path>\S.*))?$")]
  private static partial Regex OriginPattern();


}

/// <summary><c>--diff</c>: what changed since a revision, or between two, as a listing or JSON.</summary>
public static partial class DiffCommand
{
  public static int Run(string root, string revision, bool json, TextWriter output)
  {
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
    int width = changes.Count == 0 ? 0 : changes.Max(change => change.Key.Length);
    foreach (ChangeEntry change in changes)
    {
      (string? before, string? after) = change.Before is not null && change.After is not null &&
        Math.Max(change.Before.Length, change.After.Length) > 60
          ? Excerpts(change.Before, change.After)
          : (change.Before, change.After);
      output.WriteLine($"{indent}{change.Key.PadRight(width)}  {Value(before)} → {Value(after)}");
    }
  }

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