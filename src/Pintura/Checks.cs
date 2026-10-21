using System.Globalization;

namespace Pintura;

/// <summary>What a check found and where: <c>revenue/takeaway</c> for an element, the slide alone, a font, or the
/// presentation. A structure error also names the file, the line, and the docs topic with the fix.</summary>
public record Finding(string Level, string Kind, string Where, string Message)
{
  public string? Slide { get; init; }
  public string? File { get; init; }
  public int? Line { get; init; }
  public string? Topic { get; init; }

  public override string ToString()
  {
    return File is null ? Message : $"{Message} in {File}:{Line}; see pintura --docs {Topic}";
  }
}

/// <summary>How the machine finds fonts, which decides the fix a missing one names.</summary>
public record FontSetting(bool Wsl, bool WindowsFontsVisible)
{
  public static FontSetting Current(IEnumerable<string> fontFiles)
  {
    bool wsl = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null;
    return new FontSetting(wsl, FontFolders.WindowsFontsVisible(fontFiles));
  }
}

public static class Checks
{
  // Layout rounds to fractions of a point, so less than this past the frame is noise rather than text.
  private const double OverflowTolerance = 0.5;
  // Edges this close meet, since the mouse rounds to whole points.
  private const double Meets = 0.5;
  private const double NearMiss = 3;
  // Below this many other slides agreeing, there is no rest of the presentation for one slide to differ from.
  private const int Agreeing = 2;
  private const int RepeatedOnSlides = 3;

  /// <summary>Every check but the export's, which <paramref name="export"/> adds, in the order they print.</summary>
  /// <summary>What a change introduced and what it cleared. A finding is the same one when the other side has it word
  /// for word, or failing that the same kind at the same level on the same element, so text overflowing by more than
  /// it did is still the overflow that was there.</summary>
  public static (List<Finding> Introduced, List<Finding> Gone) Since(List<Finding> before, List<Finding> after)
  {
    var unmatched = new List<Finding>(before);
    var remaining = new List<Finding>();
    foreach (Finding finding in after)
    {
      if (!unmatched.Remove(finding))
      {
        remaining.Add(finding);
      }
    }
    var introduced = new List<Finding>();
    foreach (Finding finding in remaining)
    {
      Finding? same = unmatched.FirstOrDefault(candidate => candidate.Level == finding.Level &&
        candidate.Kind == finding.Kind && candidate.Where == finding.Where);
      if (same is null)
      {
        introduced.Add(finding);
      }
      else
      {
        unmatched.Remove(same);
      }
    }
    return (introduced, unmatched);
  }

  public static List<Finding> All(Presentation presentation, Measurement measurement, FontSetting fonts, bool export)
  {
    List<Finding> findings = Structure(presentation);
    findings.AddRange(Text(measurement, fonts, presentation.Theme.Checks.Shrink));
    findings.AddRange(Geometry(presentation, measurement));
    findings.AddRange(Consistency(presentation));
    findings.AddRange(Comments(presentation));
    if (export)
    {
      findings.AddRange(Export(presentation));
    }
    return findings;
  }

  #region Structure

  public static List<Finding> Structure(Presentation presentation)
  {
    return presentation.Slides
      .Where(slide => slide.Error is not null)
      .Select(slide => Structure(slide.Error!, slide.Id) with { Slide = slide.Id })
      .Concat(presentation.Slides.Where(slide => slide.Error is null)
        .SelectMany(slide => Flattened(presentation, slide)))
      .ToList();
  }

  /// <summary>A fill on an SVG of several colors, such as a logo, which it paints in one.</summary>
  private static IEnumerable<Finding> Flattened(Presentation presentation, Slide slide)
  {
    foreach (SceneItem item in SceneBuilder.Build(presentation, slide).Items.Where(item => item.Owner == "slide" &&
      item.Kind == "picture" && item.Properties.GetValueOrDefault("fill")?.Value is Color { IsNone: false }))
    {
      int paints = SvgPaint.Paints(File.ReadAllText(item.Element!.PicturePath!));
      if (paints > 1)
      {
        yield return new Finding("warning", "structure", $"{slide.Id}/{item.Id}",
          $"fill paints an SVG picture of {paints} colors in one") { Slide = slide.Id };
      }
    }
  }

  /// <summary>A load error, on a slide or, when the theme or <c>presentation.yaml</c> failed, the presentation.
  /// </summary>
  public static Finding Structure(LoadException error, string where = "presentation")
  {
    return new Finding("error", "structure", where, error.Problem)
    {
      File = error.Origin.File,
      Line = error.Origin.Line,
      Topic = error.Topic
    };
  }

  #endregion

  #region Comments

  /// <summary>A comment on a slide or element that no longer exists, which renaming or deleting it by hand leaves.
  /// </summary>
  public static List<Finding> Comments(Presentation presentation)
  {
    var findings = new List<Finding>();
    foreach (Comment comment in presentation.Comments)
    {
      Slide? slide = presentation.Slides.FirstOrDefault(candidate => candidate.Id == comment.Slide);
      string? problem = null;
      string where = comment.Slide;
      if (slide is null)
      {
        problem = $"comment {comment.Id} is on slide {comment.Slide}, which no longer exists; move it to a slide or " +
          "delete it";
      }
      else if (comment.Element is string element && slide.Error is null &&
        SceneBuilder.Build(presentation, slide).Items.All(item => item.Id != element))
      {
        where = $"{comment.Slide}/{element}";
        problem = $"comment {comment.Id} is on element {element}, which no longer exists; move it to an element or " +
          "delete it";
      }
      if (problem is not null)
      {
        findings.Add(new Finding("warning", "comments", where, problem)
        {
          Slide = slide?.Id,
          File = comment.Origin.File,
          Line = comment.Origin.Line,
          Topic = "comments"
        });
      }
    }
    return findings;
  }

  #endregion

  #region Text

  public static List<Finding> Text(Measurement measurement, FontSetting fonts, double? shrinkLimit = null)
  {
    var candidates = new List<Candidate>();
    foreach (BoxMeasure box in measurement.Boxes)
    {
      if (box.Kind == "table" && box.Overflow > OverflowTolerance)
      {
        candidates.Add(BoxCandidate(box, "error", "overflow",
          $"its rows grow {{up to }}{Points(box.Overflow)}pt past the bottom of its frame", box.Overflow));
      }
      else if (box.Overflow > OverflowTolerance)
      {
        // The largest overflow names the group, so the worst slide's figure is the one reported.
        string message = box.Fit == "shrink"
          ? $"text overflows its box by {{up to }}{Points(box.Overflow)}pt even shrunk to {Percent(box.Scale ?? 1)}"
          : $"text runs past its frame by {{up to }}{Points(box.Overflow)}pt, at {box.Lines} " +
            $"{(box.Lines == 1 ? "line" : "lines")}";
        candidates.Add(BoxCandidate(box, "error", "overflow", message, box.Overflow));
      }
      else if (shrinkLimit is double limit && box.Scale is double scale && scale * 100 < limit - 0.05)
      {
        candidates.Add(BoxCandidate(box, "warning", "shrink",
          $"text shrunk to {{as little as }}{Percent(scale)}, below the theme's {Points(limit)}%", -scale));
      }
    }
    List<Finding> findings = Collapse(candidates);
    foreach (FontMeasure font in measurement.Fonts.Where(font => !font.Available))
    {
      string message = "isn't installed or in the theme's fonts/, so the text in it was drawn in a fallback font";
      if (fonts.Wsl && !fonts.WindowsFontsVisible)
      {
        message += "; to use the Windows fonts, add <dir>/mnt/c/Windows/Fonts</dir> inside <fontconfig> in " +
          "/etc/fonts/local.conf and run fc-cache -f";
      }
      findings.Add(new Finding("warning", "text", $"font {font.Family}", message));
    }
    return findings;
  }

  private static Candidate BoxCandidate(BoxMeasure box, string level, string check, string message, double worst)
  {
    return new Candidate(box.Owner == "slide" ? $"{check}/{box.Slide}/{box.Id}" : $"{check}/{box.Owner}/{box.Id}",
      box.Owner, new Finding(level, "text", $"{box.Slide}/{box.Id}", message) { Slide = box.Slide }, worst);
  }

  #endregion

  #region Geometry

  public static List<Finding> Geometry(Presentation presentation, Measurement? measurement = null)
  {
    Dictionary<(string, string), BoxMeasure> measured = (measurement?.Boxes ?? [])
      .GroupBy(box => (box.Slide, box.Id))
      .ToDictionary(group => group.Key, group => group.First());
    var candidates = new List<Candidate>();
    foreach (Slide slide in presentation.Slides.Where(slide => slide.Error is null))
    {
      List<Placed> items = Place(presentation, slide, measured);
      List<Placed> areas = Areas(presentation.Theme, slide);
      candidates.AddRange(WideTables(presentation, slide));
      candidates.AddRange(OffSlide(presentation, slide, items));
      candidates.AddRange(Overlaps(slide, items));
      candidates.AddRange(ChartLabels(slide, items, measurement));
      candidates.AddRange(NearMisses(slide, items, areas));
    }
    return Collapse(candidates);
  }

  /// <summary>Tables whose columns need more than the frame gives them: wider than it, or leaving nothing for the
  /// columns without a width.</summary>
  private static IEnumerable<Candidate> WideTables(Presentation presentation, Slide slide)
  {
    foreach (SceneItem item in SceneBuilder.Build(presentation, slide).Items.Where(item => item.Table is not null))
    {
      Table table = presentation.Tables[item.Element!.Table!];
      double frame = ((Frame)item.Properties["frame"].Value).Rect[2];
      double given = table.Columns.Sum(column => column.Width ?? 0);
      int shared = table.ColumnCount - table.Columns.Count(column => column.Width is not null);
      string? problem = given - frame > OverflowTolerance
        ? $"its columns add up to {Points(given)}pt, {Points(given - frame)}pt wider than its {Points(frame)}pt frame"
        : shared > 0 && frame - given < OverflowTolerance
          ? $"its columns with a width fill its {Points(frame)}pt frame, leaving nothing for the {shared} without one"
          : null;
      if (problem is not null)
      {
        Candidate candidate = new Placed(item.Id, item.Id, item.Owner, [])
          .Report(slide, "wide", "geometry", problem);
        yield return candidate with { Finding = candidate.Finding with { Level = "error" } };
      }
    }
  }

  private static IEnumerable<Candidate> OffSlide(Presentation presentation, Slide slide, List<Placed> items)
  {
    (double width, double height) = (presentation.Size[0], presentation.Size[1]);
    foreach (Placed item in items)
    {
      (double left, double top, double right, double bottom) = item.Reach;
      var sides = new List<string>();
      void Past(double distance, string side)
      {
        if (distance > OverflowTolerance)
        {
          sides.Add($"{Points(distance)}pt past the slide's {side} edge");
        }
      }
      Past(-left, "left");
      Past(-top, "top");
      Past(right - width, "right");
      Past(bottom - height, "bottom");
      if (sides.Count > 0)
      {
        yield return item.Report(slide, "off", "geometry", $"runs {string.Join(" and ", sides)}");
      }
    }
  }

  /// <summary>Partial overlaps only: a box inside another, such as a label on a card, is layering by design, and
  /// lines meet what they connect.</summary>
  private static IEnumerable<Candidate> Overlaps(Slide slide, List<Placed> items)
  {
    List<Placed> boxes = items.Where(item => !item.IsLine && !item.Rotated).ToList();
    for (int later = 1; later < boxes.Count; later++)
    {
      for (int earlier = 0; earlier < later; earlier++)
      {
        (Placed top, Placed under) = (boxes[later], boxes[earlier]);
        double width = Math.Min(top.Right, under.Right) - Math.Max(top.Left, under.Left);
        double height = Math.Min(top.Bottom, under.Bottom) - Math.Max(top.Top, under.Top);
        if (width <= OverflowTolerance || height <= OverflowTolerance || top.Contains(under) ||
          under.Contains(top))
        {
          continue;
        }
        yield return top.Report(slide, $"overlap/{under.Id}", "geometry",
          $"overlaps {under.Name} by {Points(width)} × {Points(height)}pt", other: under);
      }
    }
  }

  /// <summary>Text crossing a chart's labels. Its frame inside the chart's passes <see cref="Overlaps"/> as layering,
  /// so the lines of text as drawn are what meet the labels.</summary>
  private static IEnumerable<Candidate> ChartLabels(Slide slide, List<Placed> items, Measurement? measurement)
  {
    foreach (ChartMeasure chart in (measurement?.Charts ?? []).Where(chart => chart.Slide == slide.Id))
    {
      foreach (BoxMeasure box in (measurement?.Boxes ?? []).Where(box => box.Slide == slide.Id && box.Ink is not null))
      {
        List<string> crossed = chart.Labels
          .Where(label => box.Ink!.Any(line => Crosses(line, label.Rect)))
          .Select(label => label.Text)
          .Distinct()
          .ToList();
        Placed? item = items.FirstOrDefault(item => item.Id == box.Id);
        Placed? under = items.FirstOrDefault(item => item.Id == chart.Id);
        if (crossed.Count == 0 || item is null || under is null)
        {
          continue;
        }
        string labels = crossed.Count == 1 ? $"label {crossed[0]}"
          : $"labels {string.Join(", ", crossed.SkipLast(1))} and {crossed[^1]}";
        yield return item.Report(slide, $"chart-label/{chart.Id}", "geometry",
          $"its text crosses {under.Name}'s {labels}", other: under);
      }
    }
  }

  private static bool Crosses(double[] first, double[] second)
  {
    double width = Math.Min(first[0] + first[2], second[0] + second[2]) - Math.Max(first[0], second[0]);
    double height = Math.Min(first[1] + first[3], second[1] + second[3]) - Math.Max(first[1], second[1]);
    return width > OverflowTolerance && height > OverflowTolerance;
  }

  /// <summary>An edge a few points from the same side of another item or area without meeting it: a left edge
  /// against left edges, a top against tops. Facing edges a few points apart are spacing. The later item of a pair
  /// is the one reported, since it is the one placed against the other, but an edge meeting anything is aligned.
  /// </summary>
  private static IEnumerable<Candidate> NearMisses(Slide slide, List<Placed> items, List<Placed> areas)
  {
    // Once each, since comparing every pair would otherwise search for containers again per pair.
    var containers = new Dictionary<Placed, Placed?>(ReferenceEqualityComparer.Instance);
    foreach (Placed each in items)
    {
      containers[each] = Container(each, items);
    }
    for (int index = 0; index < items.Count; index++)
    {
      Placed item = items[index];
      if (item.Rotated)
      {
        continue;
      }
      Placed? container = containers[item];
      bool Comparable(Placed other) => !other.Rotated && (containers[other] == container || other == container ||
        containers[other] == item);
      List<Placed> others = items.Where((_, position) => position != index).Where(Comparable).Concat(areas).ToList();
      // Areas first, so a tie names the area, which is what an element is usually meant to meet.
      List<Placed> before = areas.Concat(items.Take(index).Where(Comparable)).ToList();
      var misses = new List<string>();
      foreach (Edge edge in item.Edges())
      {
        if (others.SelectMany(other => other.Edges()).Any(other => edge.Matches(other) &&
          Math.Abs(other.At - edge.At) <= Meets))
        {
          continue;
        }
        (Placed Owner, Edge Edge)? nearest = before
          .SelectMany(other => other.Edges().Select(candidate => (Owner: other, Edge: candidate)))
          .Where(pair => edge.Matches(pair.Edge) && Math.Abs(pair.Edge.At - edge.At) <= NearMiss)
          .OrderBy(pair => Math.Abs(pair.Edge.At - edge.At))
          .Select(pair => ((Placed, Edge)?)pair)
          .FirstOrDefault();
        if (nearest is ({ } other, { } near))
        {
          string axis = edge.Vertical ? "x" : "y";
          misses.Add($"{edge.Name} at {axis} {Points(edge.At)} is {Points(Math.Abs(near.At - edge.At))}pt from " +
            $"{other.Name}'s {near.Name} at {axis} {Points(near.At)}");
        }
      }
      if (misses.Count > 0)
      {
        yield return item.Report(slide, "near", "geometry", string.Join("; ", misses));
      }
    }
  }

  /// <summary>The smallest item an item is layered inside, such as a chart under a highlight. Its edges follow what
  /// it sits on, so it only lines up with that item and others layered on it.</summary>
  private static Placed? Container(Placed item, List<Placed> items)
  {
    return items.Where(other => other != item && !other.IsLine && !other.Rotated && other.Contains(item) &&
        !item.Contains(other))
      .MinBy(other => (other.Right - other.Left) * (other.Bottom - other.Top));
  }

  private static List<Placed> Place(Presentation presentation, Slide slide,
    Dictionary<(string, string), BoxMeasure> measured)
  {
    Layout layout = presentation.Theme.Layouts[slide.Layout];
    var placed = new List<Placed>();
    foreach (SceneItem item in SceneBuilder.Build(presentation, slide).Items
      .Where(item => item.Element?.Decorative != true))
    {
      string owner = item.Role switch
      {
        null => item.Owner,
        _ when (item.Role == "title" ? slide.Title : slide.Placeholders[item.Id])?.Frame is not null => "slide",
        _ when (item.Role == "title" ? layout.Title : layout.Placeholders.GetValueOrDefault(item.Id))?.Frame
          is not null => "layout",
        _ => "theme"
      };
      string name = item.Role == "title" ? "the title" : item.Id;
      if (item.Properties.TryGetValue("from", out Resolved? from))
      {
        double[] start = (double[])from.Value;
        double[] end = (double[])item.Properties["to"].Value;
        placed.Add(new Placed(item.Id, name, owner, [start[0], start[1], end[0] - start[0], end[1] - start[1]])
        {
          Line = [start, end]
        });
        continue;
      }
      if (!item.Properties.TryGetValue("frame", out Resolved? frame))
      {
        continue;
      }
      double[] rect = [.. ((Frame)frame.Value).Rect];
      if (measured.GetValueOrDefault((slide.Id, item.Id))?.Height is double grown)
      {
        rect[3] = grown;
      }
      double rotate = item.Properties.TryGetValue("rotate", out Resolved? turn) ? (double)turn.Value : 0;
      placed.Add(new Placed(item.Id, name, owner, rect)
      {
        Rotate = rotate % 360,
        Grown = measured.GetValueOrDefault((slide.Id, item.Id))?.Height is not null,
        Tail = item.Properties.TryGetValue("tail", out Resolved? tail) ? (double[])tail.Value : null
      });
    }
    return placed;
  }

  private static List<Placed> Areas(Theme theme, Slide slide)
  {
    Layout layout = theme.Layouts[slide.Layout];
    return theme.Areas.Keys.Concat(layout.Areas.Keys).Distinct()
      .Select(name => new Placed(name, $"the {name} area", "area",
        (layout.Areas.GetValueOrDefault(name) ?? theme.Areas[name]).Rect))
      .ToList();
  }

  #endregion

  #region Consistency

  public static List<Finding> Consistency(Presentation presentation)
  {
    var findings = new List<Finding>();
    List<Slide> slides = presentation.Slides.Where(slide => slide.Error is null).ToList();
    findings.AddRange(slides.SelectMany(slide => Moved(presentation, slide)));
    findings.AddRange(Sizes(presentation, slides));
    findings.AddRange(RepeatedOneOffs(presentation, slides));
    findings.AddRange(slides.SelectMany(slide => Totals(presentation, slide)));
    return findings;
  }

  /// <summary>A total whose value isn't what its stack adds up to, to within the last digit its labels show, since the
  /// CSV holds both and nothing else keeps them in step.</summary>
  private static IEnumerable<Finding> Totals(Presentation presentation, Slide slide)
  {
    SlideScene scene = SceneBuilder.Build(presentation, slide);
    foreach (SceneItem item in scene.Items.Where(item => item.Chart is not null))
    {
      Chart chart = item.Chart!;
      foreach (ChartSeries total in chart.Series.Where(series =>
        chart.SeriesOptions.GetValueOrDefault(series.Name)?.Type == "total"))
      {
        List<ChartSeries> stack = chart.Series.Where(series => chart.SeriesOptions.GetValueOrDefault(series.Name)?.Type
          is null).ToList();
        var off = new List<(string Category, double Value, double Sum)>();
        for (int point = 0; point < chart.Categories.Count; point++)
        {
          string labels = $"series.{total.Name}.points.{chart.Categories[point]}.data_labels.";
          if (!item.Properties.ContainsKey($"{labels}show"))
          {
            labels = $"series.{total.Name}.data_labels.";
          }
          if (item.Properties.GetValueOrDefault($"{labels}show")?.Value is "none")
          {
            continue;
          }
          NumberFormat? format = item.Properties.GetValueOrDefault($"{labels}format")?.Value is string name
            ? presentation.Theme.Styles.Formats[name]
            : null;
          double tolerance = format is null ? 1e-9 : 0.5 * Math.Pow(10, -(format.Decimals ?? 0)) / (format.Scale ?? 1);
          double sum = stack.Sum(series => series.Values[point] ?? 0);
          if (total.Values[point] is double value && Math.Abs(value - sum) > tolerance + 1e-9)
          {
            off.Add((chart.Categories[point], value, sum));
          }
        }
        if (off.Count == 0)
        {
          continue;
        }
        (string category, double first, double stacked) = off[0];
        yield return new Finding("warning", "consistency", $"{slide.Id}/{item.Id}",
          $"{total.Name} is {Plain(first)} in {category}, where its stack adds up to {Plain(stacked)}" +
          (off.Count > 1 ? $", and {off.Count - 1} more like it" : "") + $", in {chart.Source}")
        {
          Slide = slide.Id
        };
      }
    }
  }

  /// <summary>A title or placeholder the slide moves from where its layout puts it.</summary>
  private static IEnumerable<Finding> Moved(Presentation presentation, Slide slide)
  {
    Theme theme = presentation.Theme;
    Layout layout = theme.Layouts[slide.Layout];
    var placed = new List<(string Id, string Name, Placeholder? Own, Placeholder? Layout, Placeholder? Theme)>
    {
      ("title", "the title", slide.Title, layout.Title, theme.Title)
    };
    placed.AddRange(slide.Placeholders.Select(pair => (pair.Key, pair.Key, (Placeholder?)pair.Value,
      layout.Placeholders.GetValueOrDefault(pair.Key), theme.Placeholders.GetValueOrDefault(pair.Key))));
    foreach ((string id, string name, Placeholder? own, Placeholder? layoutLevel, Placeholder? themeLevel) in placed)
    {
      Frame? inherited = layoutLevel?.Frame ?? themeLevel?.Frame;
      if (own?.Frame is not { } frame || inherited is null || frame.Rect.SequenceEqual(inherited.Rect))
      {
        continue;
      }
      yield return new Finding("warning", "consistency", $"{slide.Id}/{id}",
        $"{name} sits at {Numbers(frame.Rect)}, not at {Numbers(inherited.Rect)} where layout {slide.Layout} puts " +
        "it")
      {
        Slide = slide.Id
      };
    }
  }

  /// <summary>Text of one kind at a size on one slide that the rest of the presentation agrees on another size for.
  /// The kind is everything that decides a size without a one-off: the box's style, level, and paragraph style, and
  /// the layout when it sets the size.
  /// </summary>
  private static IEnumerable<Finding> Sizes(Presentation presentation, List<Slide> slides)
  {
    var seen = new List<Sized>();
    foreach (Slide slide in slides)
    {
      Layout? layout = presentation.Theme.Layouts.GetValueOrDefault(slide.Layout);
      foreach (SceneItem item in SceneBuilder.Build(presentation, slide).Items.Where(item => item.Owner == "slide"))
      {
        foreach (SceneParagraph paragraph in item.Paragraphs ?? [])
        {
          if (!paragraph.Properties.TryGetValue("size", out Resolved? size) || paragraph.Runs.Count == 0)
          {
            continue;
          }
          string? style = item.Properties.TryGetValue("style", out Resolved? boxStyle) ? (string)boxStyle.Value : null;
          Placeholder? layoutLevel = item.Role == "title"
            ? layout?.Title
            : layout?.Placeholders.GetValueOrDefault(item.Id);
          string? sizedBy = layoutLevel is not null && size.Origin?.File == layoutLevel.Origin.File ? slide.Layout : null;
          (string kind, string label) = item.Role switch
          {
            "title" => ($"title/{style}/{sizedBy}", "the title"),
            "placeholder" => ($"placeholder/{item.Id}/{style}/{sizedBy}", item.Id),
            _ => ($"{item.Kind}/{style}/{paragraph.Level}/{paragraph.Style}",
              $"{paragraph.Style ?? style ?? "body"} text" + (paragraph.Level > 0 ? $" at level {paragraph.Level}" : ""))
          };
          seen.Add(new Sized(kind, label, slide.Id, item.Id, (double)size.Value));
        }
      }
    }
    var reported = new HashSet<(string, string)>();
    foreach (IGrouping<string, Sized> group in seen.GroupBy(entry => entry.Kind))
    {
      Dictionary<double, HashSet<string>> slidesBySize = group
        .GroupBy(entry => entry.Size)
        .ToDictionary(sizes => sizes.Key, sizes => sizes.Select(entry => entry.Slide).ToHashSet());
      (double usual, HashSet<string> agreeing) = slidesBySize.MaxBy(pair => pair.Value.Count);
      foreach (Sized entry in group.Where(entry => entry.Size != usual))
      {
        bool alone = slidesBySize[entry.Size].Count == 1;
        int others = agreeing.Count(id => id != entry.Slide);
        if (!alone || others < Agreeing || !reported.Add((entry.Slide, entry.Id)))
        {
          continue;
        }
        yield return new Finding("warning", "consistency", $"{entry.Slide}/{entry.Id}",
          $"{entry.Label} at {Points(entry.Size)}pt, where {others} other slides have it at {Points(usual)}pt")
        {
          Slide = entry.Slide
        };
      }
    }
  }

  /// <summary>The same one-off formatting on several slides, which wants to be a style. Only what a style can hold
  /// counts.</summary>
  private static IEnumerable<Finding> RepeatedOneOffs(Presentation presentation, List<Slide> slides)
  {
    var seen = new List<OneOff>();
    void Add(TextProperties properties, string[] keys, Slide slide, string id, Element? box = null)
    {
      List<string> set = keys.Where(properties.Origins.ContainsKey)
        .Select(key => $"{key}: {Display(properties.Get(key)!)}")
        .Concat(box is null ? [] : BoxOneOffs(box))
        .ToList();
      if (set.Count > 0)
      {
        seen.Add(new OneOff(string.Join(", ", set), box?.Style, slide.Id, id));
      }
    }
    foreach (Slide slide in slides)
    {
      if (slide.Title is not null)
      {
        Add(slide.Title.Properties, TextProperties.Style, slide, "title");
      }
      foreach ((string name, Placeholder placeholder) in slide.Placeholders)
      {
        Add(placeholder.Properties, TextProperties.Style, slide, name);
      }
      foreach (Element element in slide.Elements)
      {
        Add(element.Properties, TextProperties.Named, slide, element.Id,
          element.Kind is "text" or "shape" ? element : null);
        IEnumerable<Run> runs = (element.Text ?? []).SelectMany(paragraph => paragraph.Runs);
        foreach (TextProperties properties in runs.Select(run => run.Properties).OfType<TextProperties>())
        {
          Add(properties, TextProperties.Character, slide, element.Id);
        }
      }
    }
    // Boxes in different styles that happen to share a color are different things, not one style waiting to be named.
    foreach (IGrouping<(string Formatting, string? Style), OneOff> group in
      seen.GroupBy(entry => (entry.Formatting, entry.Style)))
    {
      List<string> on = group.Select(entry => entry.Slide).Distinct().ToList();
      if (on.Count < RepeatedOnSlides)
      {
        continue;
      }
      OneOff first = group.First();
      yield return new Finding("warning", "consistency", $"{first.Slide}/{first.Id}",
        $"the same one-off formatting ({group.Key.Formatting}) is on {on.Count} slides, {string.Join(", ", on)}; name a " +
        "style for it in styles.yaml")
      {
        Slide = first.Slide
      };
    }
  }

  /// <summary>What a text box or shape sets of itself that a style could hold instead.</summary>
  private static IEnumerable<string> BoxOneOffs(Element element)
  {
    if (element.Fill is not null)
    {
      yield return $"fill: {element.Fill}";
    }
    if (element.Stroke is { } stroke)
    {
      if (stroke.None == true)
      {
        yield return "stroke: none";
      }
      foreach ((string key, object? value) in new (string, object?)[]
        { ("color", stroke.Color), ("width", stroke.Width), ("dash", stroke.Dash) })
      {
        if (value is not null)
        {
          yield return $"stroke.{key}: {Display(value)}";
        }
      }
    }
    if (element.Shadow is { } shadow)
    {
      if (shadow.None == true)
      {
        yield return "shadow: none";
      }
      foreach ((string key, object? value) in new (string, object?)[]
      {
        ("color", shadow.Color), ("opacity", shadow.Opacity), ("blur", shadow.Blur), ("distance", shadow.Distance),
        ("angle", shadow.Angle)
      })
      {
        if (value is not null)
        {
          yield return $"shadow.{key}: {Display(value)}";
        }
      }
    }
    if (element.Opacity is double opacity)
    {
      yield return $"opacity: {Display(opacity)}";
    }
  }

  private record Sized(string Kind, string Label, string Slide, string Id, double Size);

  private record OneOff(string Formatting, string? Style, string Slide, string Id);

  #endregion

  #region Export

  /// <summary>What the PowerPoint export can't reproduce, from the gaps the exporter reports.</summary>
  public static List<Finding> Export(Presentation presentation)
  {
    var findings = new List<Finding>();
    void Whole(string message)
    {
      findings.Add(new Finding("warning", "export", "presentation", message));
    }
    if (presentation.Sections.Any(section => section.Name is not null))
    {
      Whole("sections: the export has none, so the slides come out in one run");
    }

    var candidates = new List<Candidate>();
    foreach (Slide slide in presentation.Slides.Where(slide => slide.Error is null))
    {
      foreach (SceneItem item in SceneBuilder.Build(presentation, slide).Items)
      {
        foreach (string gap in Gaps(item))
        {
          string key = item.Owner == "slide" ? $"{slide.Id}/{item.Id}/{gap}" : $"{item.Owner}/{item.Id}/{gap}";
          candidates.Add(new Candidate(key, item.Owner,
            new Finding("warning", "export", $"{slide.Id}/{item.Id}", gap) { Slide = slide.Id }, 0));
        }
      }
    }
    findings.AddRange(Collapse(candidates));
    return findings;
  }

  private static IEnumerable<string> Gaps(SceneItem item)
  {
    Element? element = item.Element;
    switch (item.Kind)
    {
      case "picture":
        if (element?.Crop is not null)
        {
          yield return "picture crop isn't exported";
        }
        if (element?.Alt is not null)
        {
          yield return "picture alt text isn't exported";
        }
        if (element?.Opacity is not null)
        {
          yield return "picture opacity isn't exported";
        }
        if (item.Properties.TryGetValue("fit", out Resolved? fit) && (string)fit.Value == "cover")
        {
          yield return "fit: cover needs a crop, which isn't exported";
        }
        break;
      case "shape" when element?.Shape == "callout":
        yield return "a callout's tail isn't exported, so it points elsewhere";
        break;
      case "shape" when element?.Shape is "brace" or "bracket":
        yield return $"a {element!.Shape} exports with PowerPoint's curl rather than a quarter of its thickness";
        break;
    }
    if (item.Properties.TryGetValue("columns", out Resolved? columns) && (int)columns.Value > 1)
    {
      yield return "text columns aren't exported";
    }
    // A layout's page number becomes PowerPoint's slide-number placeholder; a slide's own field has no such home.
    bool slideNumber = (item.Paragraphs ?? []).Any(paragraph => paragraph.Runs.Any(run => run.Field == "slide"));
    if (slideNumber && item.Owner == "slide" && item.Role is null)
    {
      yield return "${slide} in a slide's own text exports as plain text, or not at all when it is the only text";
    }
  }

  #endregion

  #region Shared

  /// <summary>A finding and the key that makes it one: a theme or layout element is the same on every slide showing
  /// it, so its findings collapse into one named by its first slide. The largest <c>Worst</c> leads the group.
  /// </summary>
  private record Candidate(string Key, string Owner, Finding Finding, double Worst);

  /// <summary>Messages carry an optional <c>{…}</c> phrase, kept only when a group spans several slides.</summary>
  private static List<Finding> Collapse(List<Candidate> candidates)
  {
    var findings = new List<Finding>();
    foreach (IGrouping<string, Candidate> group in candidates.GroupBy(candidate => candidate.Key))
    {
      Candidate first = group.First();
      Candidate worst = group.MaxBy(candidate => candidate.Worst)!;
      int count = group.Select(candidate => candidate.Finding.Slide).Distinct().Count();
      string message = Optional(worst.Finding.Message, count > 1);
      if (count > 1)
      {
        message += $", on {count} slides from the {first.Owner}";
      }
      findings.Add(first.Finding with { Message = message });
    }
    return findings;
  }

  private static string Optional(string message, bool keep)
  {
    int open = message.IndexOf('{');
    int close = message.IndexOf('}');
    if (open < 0 || close < open)
    {
      return message;
    }
    return message[..open] + (keep ? message[(open + 1)..close] : "") + message[(close + 1)..];
  }

  private static string Plain(double number)
  {
    return Math.Round(number, 9).ToString(CultureInfo.InvariantCulture);
  }

  private static string Display(object value)
  {
    return value switch
    {
      double number => Points(number),
      bool flag => flag ? "true" : "false",
      double[] numbers => Numbers(numbers),
      _ => value.ToString() ?? ""
    };
  }

  private static string Points(double value)
  {
    return Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
  }

  private static string Percent(double scale)
  {
    return scale.ToString("0%", CultureInfo.InvariantCulture);
  }

  private static string Numbers(double[] numbers)
  {
    return $"[{string.Join(", ", numbers.Select(Points))}]";
  }

  /// <summary>A line's endpoint is every side at once, since it can align with any of them.</summary>
  private record Edge(string Name, bool Vertical, double At, string Side)
  {
    public bool Matches(Edge other)
    {
      return Vertical == other.Vertical && (Side == other.Side || Side == "point" || other.Side == "point");
    }
  }

  /// <summary>An item's rectangle on one slide, a line's between its endpoints, or an area's.</summary>
  private record Placed(string Id, string Name, string Owner, double[] Rect)
  {
    public double[][]? Line { get; init; }
    public double[]? Tail { get; init; }
    public double Rotate { get; init; }
    /// <summary>A <c>grow</c> box's bottom follows its text rather than a number anyone wrote.</summary>
    public bool Grown { get; init; }

    public bool IsLine => Line is not null;
    public bool Rotated => Rotate != 0;
    public double Left => Math.Min(Rect[0], Rect[0] + Rect[2]);
    public double Top => Math.Min(Rect[1], Rect[1] + Rect[3]);
    public double Right => Math.Max(Rect[0], Rect[0] + Rect[2]);
    public double Bottom => Math.Max(Rect[1], Rect[1] + Rect[3]);

    /// <summary>How far the item draws: the rotated box's bounds, and a callout's tail.</summary>
    public (double Left, double Top, double Right, double Bottom) Reach
    {
      get
      {
        double radians = Rotate * Math.PI / 180;
        double halfWidth = (Math.Abs(Rect[2] * Math.Cos(radians)) + Math.Abs(Rect[3] * Math.Sin(radians))) / 2;
        double halfHeight = (Math.Abs(Rect[2] * Math.Sin(radians)) + Math.Abs(Rect[3] * Math.Cos(radians))) / 2;
        (double x, double y) = ((Left + Right) / 2, (Top + Bottom) / 2);
        (double left, double top, double right, double bottom) = (x - halfWidth, y - halfHeight, x + halfWidth,
          y + halfHeight);
        if (Tail is [double tailX, double tailY])
        {
          (left, top, right, bottom) = (Math.Min(left, tailX), Math.Min(top, tailY), Math.Max(right, tailX),
            Math.Max(bottom, tailY));
        }
        return (left, top, right, bottom);
      }
    }

    public bool Contains(Placed other)
    {
      return other.Left >= Left - Meets && other.Top >= Top - Meets && other.Right <= Right + Meets &&
        other.Bottom <= Bottom + Meets;
    }

    public IEnumerable<Edge> Edges()
    {
      if (Line is [double[] start, double[] end])
      {
        yield return new Edge("start", true, start[0], "point");
        yield return new Edge("start", false, start[1], "point");
        yield return new Edge("end", true, end[0], "point");
        yield return new Edge("end", false, end[1], "point");
        yield break;
      }
      yield return new Edge("left edge", true, Left, "left");
      yield return new Edge("right edge", true, Right, "right");
      yield return new Edge("top edge", false, Top, "top");
      if (!Grown)
      {
        yield return new Edge("bottom edge", false, Bottom, "bottom");
      }
    }

    /// <summary>A finding on this item; a pair of theme or layout items collapses across slides like one item.
    /// </summary>
    public Candidate Report(Slide slide, string check, string kind, string message, Placed? other = null)
    {
      bool shared = Owner is "theme" or "layout" && (other is null || other.Owner is "theme" or "layout");
      string owner = Owner == "layout" || other?.Owner == "layout" ? "layout" : Owner;
      string key = shared ? $"{check}/{Id}" : $"{check}/{slide.Id}/{Id}";
      return new Candidate(key, owner,
        new Finding("warning", kind, $"{slide.Id}/{Id}", message) { Slide = slide.Id }, 0);
    }
  }

  #endregion


}