using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>Variants of one slide an agent offers for the reviewer to keep one of, from <c>picks.yaml</c>, or once
/// resolved, from <c>picks-resolved.yaml</c> with what was kept and why.</summary>
public record Pick
{
  public string Id { get; init; } = "";
  public string? By { get; init; }
  public string? Date { get; init; }
  public string? Rev { get; init; }
  public List<Variant> Variants { get; init; } = [];
  public PickResolution? Resolved { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>A slide offered in a pick, with one neutral line on what it tries to show.</summary>
public record Variant(string Slide, string Intent);

/// <summary>Who resolved a pick, when, the commit the deck was then at, the order the variants were shown in, which
/// lettered them A, B and C, the slide kept or <c>none</c>, and why.</summary>
public record PickResolution(string? By, string? Date, string? Rev, List<string>? Shown, string Chosen, string? Why);

/// <summary>A reviewer resolving a pick: keeping <paramref name="Keep"/>, or none of its variants when it is null,
/// for the reason in <paramref name="Why"/>, having seen the variants in the order <paramref name="Shown"/>; with
/// <paramref name="Reopen"/>, undoing that; or with <paramref name="Revisit"/>, reopening a pick that kept none to
/// choose again.</summary>
public record PickChange(string? Pick = null, string? Keep = null, string? Why = null, PickUndo? Reopen = null,
  List<string>? Shown = null, bool Revisit = false);

/// <summary>What resolving a pick removed, for an undo to put back: the slide its entry in <c>slides</c> became, the
/// open pick it went before, the other variants' files by stem, and the comments on them it resolved.</summary>
public record PickUndo(string Pick, string Kept, string? Before, Dictionary<string, string> Files,
  List<string> Comments);

/// <summary>Reads <c>picks.yaml</c>, the picks waiting on the reviewer, and resolves them into
/// <c>picks-resolved.yaml</c>, which records each offer with what was kept and why.</summary>
public static class PickFile
{
  public const string Name = "picks.yaml";
  public const string ResolvedName = "picks-resolved.yaml";
  public const string None = "none";

  private static readonly object Gate = new object();

  /// <summary>An entry and its lines, from the start of the line its dash is on; <paramref name="Indent"/> is the
  /// dash's column.</summary>
  private record Entry(Pick Pick, int Start, int End, int Indent);

  public static List<Pick> Load(string root, string displayBase, IFiles? files = null)
  {
    return Load(Path.Combine(root, Name), displayBase, false, files ?? DiskFiles.Instance);
  }

  /// <summary>The resolved picks, in the order they were resolved.</summary>
  public static List<Pick> LoadResolved(string root, string displayBase, IFiles? files = null)
  {
    return Load(Path.Combine(root, ResolvedName), displayBase, true, files ?? DiskFiles.Instance);
  }

  private static List<Pick> Load(string path, string displayBase, bool archive, IFiles files)
  {
    if (!files.Exists(path))
    {
      return [];
    }
    return Read(SourceFile.For(path, displayBase, files).Load("picks"), archive).Select(entry => entry.Pick).ToList();
  }

  private static List<Entry> Read(Node document, bool archive)
  {
    if (document.Yaml is YamlMappingNode { Children.Count: 0 } or YamlScalarNode { Value: "" or null })
    {
      return [];
    }
    string text = document.File.Text;
    var entries = new List<Entry>();
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (Node node in document.AsList())
    {
      MapNode map = node.AsMap();
      Node variantsNode = map.Require("variants");
      var pick = new Pick
      {
        Id = map.Require("id").AsString(),
        By = map.Take("by")?.AsString(),
        Date = map.Take("date")?.AsString(),
        Rev = map.Take("rev")?.AsString(),
        Variants = variantsNode.AsList().Select(ReadVariant).ToList(),
        Origin = new Origin(document.File.Display, node.Origin.Line, node.Origin.Column)
      };
      if (archive)
      {
        pick = pick with { Resolved = ReadResolution(map.Require("resolved"), pick) };
      }
      map.Done();
      if (pick.Variants.Count == 0)
      {
        throw variantsNode.Error($"pick {pick.Id} offers no variants");
      }
      if (pick.Variants.GroupBy(variant => variant.Slide).FirstOrDefault(group => group.Count() > 1) is { } twice)
      {
        throw variantsNode.Error($"pick {pick.Id} offers {twice.Key} twice");
      }
      // A pick reopened and resolved again is in the archive twice, each time with how it went.
      if (!ids.Add(pick.Id) && !archive)
      {
        throw node.Error($"pick {pick.Id} is written twice");
      }
      (int start, int end) = Node.Extent(text, node.Yaml);
      int line = text.LastIndexOf('\n', Math.Max(start - 1, 0)) + 1;
      int dash = text.IndexOf('-', line);
      entries.Add(new Entry(pick, line, end, dash - line));
    }
    return entries;
  }

  private static Variant ReadVariant(Node node)
  {
    MapNode map = node.AsMap();
    Node slideNode = map.Require("slide");
    var variant = new Variant(slideNode.AsString(), map.Require("intent").AsString());
    map.Done();
    if (!PresentationLoader.IsSlideStem(variant.Slide))
    {
      throw slideNode.Error($"name a variant by its slide's file stem, not {variant.Slide}");
    }
    return variant;
  }

  private static PickResolution ReadResolution(Node node, Pick pick)
  {
    MapNode map = node.AsMap();
    Node chosenNode = map.Require("chosen");
    string? by = map.Take("by")?.AsString();
    string? date = map.Take("date")?.AsString();
    string? rev = map.Take("rev")?.AsString();
    Node? shownNode = map.Take("shown");
    var resolution = new PickResolution(by, date, rev, shownNode is null ? null : ReadShown(shownNode, pick),
      chosenNode.AsString(), map.Take("why")?.AsString());
    map.Done();
    if (resolution.Shown is not null && !Shuffled(pick, resolution.Shown))
    {
      throw shownNode!.Error($"shown is the variants, {string.Join(", ", Slides(pick))}, in the order shown");
    }
    if (resolution.Chosen != None && pick.Variants.All(variant => variant.Slide != resolution.Chosen))
    {
      throw chosenNode.Error($"chosen is one of the variants, {string.Join(", ", Slides(pick))}, or {None}");
    }
    return resolution;
  }

  /// <summary>The variants by the letters the reviewer saw them under, which their why may name, in order.</summary>
  private static List<string> ReadShown(Node node, Pick pick)
  {
    if (!node.IsMap)
    {
      throw node.Error($"write shown as each letter the reviewer saw with its variant, as A: {pick.Variants[0].Slide}");
    }
    var shown = new List<string>();
    foreach ((string letter, Node slide) in node.AsMap().Entries())
    {
      if (letter != Letter(shown.Count))
      {
        throw slide.Error($"shown letters the variants A, B, C in order, so {letter} should be {Letter(shown.Count)}");
      }
      shown.Add(slide.AsString());
    }
    return shown;
  }

  /// <summary>The letter the preview shows the variant at <paramref name="index"/> under.</summary>
  public static string Letter(int index)
  {
    return ((char)('A' + index)).ToString();
  }

  public static IEnumerable<string> Slides(Pick pick)
  {
    return pick.Variants.Select(variant => variant.Slide);
  }

  /// <summary>Whether <paramref name="shown"/> is the pick's variants, each once, in any order.</summary>
  private static bool Shuffled(Pick pick, List<string> shown)
  {
    return shown.Order(StringComparer.Ordinal).SequenceEqual(Slides(pick).Order(StringComparer.Ordinal));
  }

  /// <summary>What an export says of each placed pick still open, that it drew the first variant in its place, and of
  /// each slide the reviewer rejected.</summary>
  public static List<string> Warnings(Presentation presentation)
  {
    return
    [
      .. presentation.Slides.Select(slide =>
      {
        if (slide.Rejected is Rejection rejected)
        {
          return $"slide {slide.Number} is {slide.Id}, which the reviewer rejected in pick {rejected.Pick}";
        }
        if (slide.Pick is null)
        {
          return null;
        }
        IEnumerable<string> variants = presentation.Offered.Where(each => each.Pick == slide.Pick)
          .Select(each => each.Id);
        return $"slide {slide.Number} is {slide.Id}, the first variant of pick {slide.Pick}, which waits on the " +
          $"reviewer to keep one of {string.Join(", ", variants)}";
      }).OfType<string>()
    ];
  }

  /// <summary>Resolves an open pick, or reopens one, answering with the change that undoes it and the files it wrote,
  /// each with what it read before, null when it didn't exist.</summary>
  public static (PickChange Undo, Dictionary<string, string?> Wrote) Apply(Presentation presentation,
    PickChange change, string author, DateTime now, Snapshot? seen = null)
  {
    if (change.Reopen is PickUndo reopen)
    {
      return Reopen(presentation, reopen, now);
    }
    if (change.Revisit)
    {
      return Revisit(presentation, change.Pick ?? throw new PatchException("name the pick to choose again"), now);
    }
    return Resolve(presentation, change.Pick ?? throw new PatchException("name the pick to resolve"), change.Keep,
      change.Why, change.Shown, author, now, seen);
  }

  /// <summary>Keeps one variant of a pick, or none of them, which keeps the first in place, marked rejected, so the
  /// deck still draws until it's redone: the pick's entry in <c>slides</c> becomes the slide kept, the other variants'
  /// files go with their open comments, and the pick moves to the end of <c>picks-resolved.yaml</c>. Every file is
  /// written or none is.</summary>
  private static (PickChange Undo, Dictionary<string, string?> Wrote) Resolve(Presentation presentation, string id,
    string? keep, string? why, List<string>? shown, string author, DateTime now, Snapshot? seen)
  {
    why = string.IsNullOrWhiteSpace(why) ? null : why.Trim().ReplaceLineEndings(" ");
    if (keep is null && why is null)
    {
      throw new PatchException("say why none of the variants will do");
    }
    lock (Gate)
    {
      Touched files = new Touched(presentation.Root);
      (string live, List<Entry> entries) = files.Picks();
      (string archive, List<Entry> archived) = files.Resolved();
      int at = entries.FindIndex(entry => entry.Pick.Id == id);
      if (at < 0)
      {
        throw new PatchException($"pick {id} is gone; someone resolved it", true);
      }
      Pick pick = entries[at].Pick;
      if (keep is not null && Slides(pick).All(slide => slide != keep))
      {
        throw new PatchException($"{keep} isn't one of pick {id}'s variants");
      }
      if (shown is not null && !Shuffled(pick, shown))
      {
        throw new PatchException($"pick {id}'s variants changed since they were shown", true);
      }
      string kept = keep ?? pick.Variants[0].Slide;
      List<string> dropped = [.. Slides(pick).Where(slide => slide != kept)];
      string? before = at + 1 < entries.Count ? entries[at + 1].Pick.Id : null;
      var deleted = new Dictionary<string, string>(StringComparer.Ordinal);
      var comments = new List<string>();
      try
      {
        files.Write(files.List, Patcher.Settle(files.Read(files.List)!, id, kept));
        var resolution = new PickResolution(author, Stamp(now), seen?.Rev, shown, keep ?? None, why);
        files.Write(files.PicksPath, Without(live, entries, at));
        files.Write(files.ResolvedPath, Append(archive, archived, Archived(live, entries[at], resolution)));
        foreach (string slide in dropped)
        {
          string path = Path.Combine(presentation.Root, "slides", $"{slide}.yaml");
          if (files.Read(path) is string text)
          {
            deleted[slide] = text;
            files.Write(path, null);
          }
          files.Track(Path.Combine(presentation.Root, CommentFile.Name));
          files.Track(Path.Combine(presentation.Root, CommentFile.ResolvedName));
          comments.AddRange(CommentFile.RemoveSlide(presentation, slide, author, now, seen).Select(each => each.Id));
        }
        string first = Path.Combine(presentation.Root, "slides", $"{kept}.yaml");
        if (keep is null && files.Read(first) is string slideText)
        {
          files.Write(first, $"rejected: {YamlWriter.Text(id)}\n{Unmarked(slideText, null)}");
        }
      }
      catch (Exception exception) when (exception is PatchException or IOException or UnauthorizedAccessException)
      {
        files.Restore();
        throw;
      }
      return (new PickChange(Reopen: new PickUndo(id, kept, before, deleted, comments)), files.Before);
    }
  }

  /// <summary>Puts a resolved pick back as it was before it was resolved: its entry in <c>slides</c>, the variants'
  /// files, its entry in <c>picks.yaml</c>, and the comments resolving it resolved.</summary>
  private static (PickChange Undo, Dictionary<string, string?> Wrote) Reopen(Presentation presentation, PickUndo undo,
    DateTime now)
  {
    lock (Gate)
    {
      Touched files = new Touched(presentation.Root);
      (string live, List<Entry> entries) = files.Picks();
      (string archive, List<Entry> archived) = files.Resolved();
      int at = archived.FindLastIndex(entry => entry.Pick.Id == undo.Pick);
      if (at < 0 || entries.Any(entry => entry.Pick.Id == undo.Pick))
      {
        throw new PatchException($"pick {undo.Pick} isn't resolved; someone reopened it", true);
      }
      Pick pick = archived[at].Pick;
      string? why = pick.Resolved!.Why;
      string? keep = pick.Resolved.Chosen == None ? null : pick.Resolved.Chosen;
      try
      {
        files.Write(files.List, Patcher.Unsettle(files.Read(files.List)!, undo.Kept, undo.Pick));
        foreach ((string slide, string text) in undo.Files)
        {
          string path = Path.Combine(presentation.Root, "slides", $"{slide}.yaml");
          if (File.Exists(path))
          {
            throw new PatchException($"slides/{slide}.yaml exists again, so reopening pick {undo.Pick} was dropped",
              true);
          }
          files.Write(path, text);
        }
        int index = undo.Before is not null && entries.FindIndex(entry => entry.Pick.Id == undo.Before) is
          >= 0 and int found
          ? found
          : entries.Count;
        string first = Path.Combine(presentation.Root, "slides", $"{undo.Kept}.yaml");
        if (keep is null && files.Read(first) is string slideText)
        {
          files.Write(first, Unmarked(slideText, undo.Pick));
        }
        files.Write(files.PicksPath, Insert(live, entries, index, Unresolved(archive, archived[at])));
        files.Write(files.ResolvedPath, Without(archive, archived, at));
        if (undo.Comments.Count > 0)
        {
          files.Track(Path.Combine(presentation.Root, CommentFile.Name));
          files.Track(Path.Combine(presentation.Root, CommentFile.ResolvedName));
          CommentFile.Apply(presentation,
            new CommentChange(Many: [.. undo.Comments.Select(comment => new CommentChange(Reopen: comment))]), "",
            now);
        }
      }
      catch (Exception exception) when (exception is PatchException or IOException or UnauthorizedAccessException)
      {
        files.Restore();
        throw;
      }
      return (new PickChange(undo.Pick, keep, why ?? "", Shown: pick.Resolved.Shown), files.Before);
    }
  }

  /// <summary>Reopens a pick that kept none, as its undo would, with the variants it dropped read from the commit it
  /// was resolved at and the comments resolving it resolved, so the reviewer can choose again after the undo is gone.
  /// </summary>
  private static (PickChange Undo, Dictionary<string, string?> Wrote) Revisit(Presentation presentation, string id,
    DateTime now)
  {
    Pick pick = LoadResolved(presentation.Root, presentation.Root).LastOrDefault(each => each.Id == id)
      ?? throw new PatchException($"pick {id} isn't resolved; someone reopened it", true);
    PickResolution resolved = pick.Resolved!;
    if (resolved.Chosen != None)
    {
      throw new PatchException($"pick {id} kept {resolved.Chosen}, so there's nothing to choose again");
    }
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (string slide in Slides(pick).Skip(1))
    {
      (int exit, string text, _) = resolved.Rev is null ? (1, "", "")
        : Revisions.Run(presentation.Root, "show", $"{resolved.Rev}:./slides/{slide}.yaml");
      if (exit != 0)
      {
        throw new PatchException($"slides/{slide}.yaml wasn't saved when pick {id} was resolved, so its variants " +
          "can't be brought back");
      }
      files[slide] = text;
    }
    List<string> comments = [.. CommentFile.LoadResolved(presentation.Root, presentation.Root)
      .Where(comment => comment.Resolved is { Outcome: "removed" } done && done.Date == resolved.Date &&
        files.ContainsKey(comment.Slide))
      .Select(comment => comment.Id)];
    return Reopen(presentation, new PickUndo(id, pick.Variants[0].Slide, null, files, comments), now);
  }

  private static string Stamp(DateTime now)
  {
    return now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
  }

  /// <summary>An entry's lines as written, moved to the dash's column 0, with the resolution as its last key.
  /// </summary>
  private static string Archived(string text, Entry entry, PickResolution resolution)
  {
    var map = new OrderedDictionary<string, object?>();
    if (resolution.By is not null)
    {
      map["by"] = resolution.By;
    }
    if (resolution.Date is not null)
    {
      map["date"] = resolution.Date;
    }
    if (resolution.Rev is not null)
    {
      map["rev"] = resolution.Rev;
    }
    if (resolution.Shown is not null)
    {
      map["shown"] = new OrderedDictionary<string, object?>(resolution.Shown
        .Select((slide, index) => KeyValuePair.Create(Letter(index), (object?)slide)));
    }
    map["chosen"] = resolution.Chosen;
    if (resolution.Why is not null)
    {
      map["why"] = resolution.Why;
    }
    string lines = Dedented(text[entry.Start..entry.End].TrimEnd(), entry.Indent);
    int keys = 1;
    while (keys < lines.Length && lines[keys] == ' ')
    {
      keys++;
    }
    string resolved = YamlWriter.Item(new OrderedDictionary<string, object?> { ["resolved"] = map })[2..];
    // Rendered for keys two columns in, which the entry's may not be.
    string shifted = string.Join("\n", resolved.Split('\n')
      .Select((line, index) => index == 0 || line.Length == 0 ? line : new string(' ', keys - 2) + line));
    return $"{lines}\n{new string(' ', keys)}{shifted}";
  }

  /// <summary>Whether a rejected slide's file, its mark aside, differs from the commit its pick was resolved at; false
  /// when that can't be told, as without git or for a slide that commit doesn't have.</summary>
  public static bool ChangedSinceRejected(Presentation presentation, Slide slide)
  {
    string pick = slide.Rejected!.Pick;
    string? rev = LoadResolved(presentation.Root, presentation.Root).LastOrDefault(each => each.Id == pick)?.Resolved
      ?.Rev;
    string path = Path.Combine(presentation.Root, "slides", $"{slide.Id}.yaml");
    if (rev is null || !File.Exists(path))
    {
      return false;
    }
    (int exit, string then, _) = (1, "", "");
    try
    {
      (exit, then, _) = Revisions.Run(presentation.Root, "show", $"{rev}:./slides/{slide.Id}.yaml");
    }
    catch (InvalidDataException)
    {
      return false;
    }
    string Normal(string text) => Unmarked(text, pick).ReplaceLineEndings("\n").TrimEnd();
    return exit == 0 && Normal(File.ReadAllText(path)) != Normal(then);
  }

  /// <summary>A slide's text without its <c>rejected</c> line; with <paramref name="pick"/>, only a line naming that
  /// pick.</summary>
  private static string Unmarked(string text, string? pick)
  {
    var stream = new YamlStream();
    try
    {
      stream.Load(new StringReader(text));
    }
    catch (YamlDotNet.Core.YamlException)
    {
      return text;
    }
    if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode map ||
      map.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: "rejected" }) is not
      { Key: YamlScalarNode key, Value: YamlScalarNode value } || (pick is not null && value.Value != pick))
    {
      return text;
    }
    int start = text.LastIndexOf('\n', Math.Max((int)key.Start.Index - 1, 0)) + 1;
    int newline = text.IndexOf('\n', (int)value.End.Index);
    return text[..start] + (newline < 0 ? "" : text[(newline + 1)..]);
  }

  /// <summary>An archived entry's lines without the resolution it was given.</summary>
  private static string Unresolved(string text, Entry entry)
  {
    string lines = Dedented(text[entry.Start..entry.End].TrimEnd(), entry.Indent);
    var stream = new YamlStream();
    stream.Load(new StringReader(lines));
    var map = (YamlMappingNode)((YamlSequenceNode)stream.Documents[0].RootNode).Children[0];
    (YamlNode key, YamlNode value) = map.Children.First(pair => pair.Key is YamlScalarNode { Value: "resolved" });
    int start = lines.LastIndexOf('\n', (int)key.Start.Index) + 1;
    int end = Node.Extent(lines, value).End;
    int newline = lines.IndexOf('\n', end);
    end = newline < 0 ? lines.Length : newline + 1;
    return (lines[..start] + lines[end..]).TrimEnd();
  }

  private static string Dedented(string lines, int indent)
  {
    if (indent == 0)
    {
      return lines;
    }
    return string.Join("\n", lines.Split('\n').Select(line => line.Length >= indent &&
      line[..indent].Trim().Length == 0 ? line[indent..] : line.TrimStart()));
  }

  private static string Indented(string lines, int indent)
  {
    string margin = new string(' ', indent);
    return string.Join("\n", lines.Split('\n').Select(line => line.Length == 0 ? line : margin + line));
  }

  private static string Append(string text, List<Entry> entries, string lines)
  {
    return entries.Count == 0 ? lines + "\n" : $"{text.TrimEnd()}\n\n{lines}\n";
  }

  /// <summary>An entry's lines written before the <paramref name="index"/>th, indented as the file's entries are.
  /// </summary>
  private static string Insert(string text, List<Entry> entries, int index, string lines)
  {
    if (entries.Count == 0)
    {
      return lines + "\n";
    }
    lines = Indented(lines, entries[0].Indent);
    return index < entries.Count
      ? text.Insert(entries[index].Start, lines + "\n\n")
      : $"{text.TrimEnd()}\n\n{lines}\n";
  }

  /// <summary>The text without an entry and the blank lines after it, or null when it was the only one.</summary>
  private static string? Without(string text, List<Entry> entries, int index)
  {
    if (entries.Count == 1)
    {
      return null;
    }
    return index + 1 < entries.Count
      ? text.Remove(entries[index].Start, entries[index + 1].Start - entries[index].Start)
      : text[..entries[index].Start].TrimEnd() + "\n";
  }

  /// <summary>The presentation's files as one change reads and writes them, remembering how each read first, so a
  /// failing step puts every one back.</summary>
  private class Touched(string root)
  {
    public string List { get; } = Path.Combine(root, "presentation.yaml");
    public string PicksPath { get; } = Path.Combine(root, Name);
    public string ResolvedPath { get; } = Path.Combine(root, ResolvedName);

    /// <summary>Each file touched, with its text before, null when it didn't exist.</summary>
    public Dictionary<string, string?> Before { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    public (string Text, List<Entry> Entries) Picks()
    {
      return Parsed(PicksPath, false);
    }

    public (string Text, List<Entry> Entries) Resolved()
    {
      return Parsed(ResolvedPath, true);
    }

    private (string Text, List<Entry> Entries) Parsed(string path, bool archive)
    {
      if (Read(path) is not string text)
      {
        return ("", []);
      }
      try
      {
        return (text, PickFile.Read(SourceFile.For(path, root).Load("picks"), archive));
      }
      catch (LoadException exception)
      {
        throw new PatchException(exception.Message);
      }
    }

    public string? Read(string path)
    {
      return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Remembers a file something else will write, to put it back too.</summary>
    public void Track(string path)
    {
      Before.TryAdd(path, Read(path));
    }

    /// <summary>Writes a file, or with null deletes it.</summary>
    public void Write(string path, string? text)
    {
      Track(path);
      if (text is null)
      {
        File.Delete(path);
      }
      else
      {
        Pintura.Files.Replace(path, Before[path] is string before ? Patcher.KeepLineEndings(before, text) : text);
      }
    }

    public void Restore()
    {
      foreach ((string path, string? text) in Before)
      {
        try
        {
          if (text is null)
          {
            File.Delete(path);
          }
          else if (Read(path) != text)
          {
            Pintura.Files.Replace(path, text);
          }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
      }
    }


  }


}

/// <summary><c>--picks</c>: the open picks with their variants and intents, or with <c>--resolved</c>, the resolved
/// ones in the order they were resolved.</summary>
public static class PickList
{
  public static int Write(Presentation presentation, bool json, TextWriter output)
  {
    List<Pick> picks = presentation.Picks;
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(picks, PayloadJson.Indented));
      return 0;
    }
    if (picks.Count == 0)
    {
      output.WriteLine("no open picks");
      return 0;
    }
    foreach (Pick pick in picks)
    {
      Slide? placed = presentation.Slides.FirstOrDefault(slide => slide.Pick == pick.Id);
      string where = placed is null ? "not in slides" : $"slide {placed.Number}";
      output.WriteLine($"{pick.Id}  {where}{Offered(pick)}");
      WriteVariants(pick, output);
    }
    output.WriteLine($"{picks.Count} open pick{(picks.Count == 1 ? "" : "s")} in {picks[0].Origin.File}, each " +
      "waiting on the reviewer to keep a variant in the preview.");
    return 0;
  }

  public static int WriteResolved(string root, bool json, TextWriter output)
  {
    List<Pick> resolved = PickFile.LoadResolved(root, Directory.GetCurrentDirectory());
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(resolved, PayloadJson.Indented));
      return 0;
    }
    if (resolved.Count == 0)
    {
      output.WriteLine("no resolved picks");
      return 0;
    }
    foreach (Pick pick in resolved)
    {
      PickResolution resolution = pick.Resolved!;
      string by = resolution.By is null ? "" : resolution.Date is null ? $" by {resolution.By}" :
        $" by {resolution.By}, {resolution.Date}";
      string at = resolution.Rev is null ? "" : $" at {resolution.Rev}";
      string kept = resolution.Chosen == PickFile.None ? "kept none" : $"kept {resolution.Chosen}";
      output.WriteLine($"{pick.Id}  {kept}{by}{at}");
      WriteVariants(pick, output);
      if (resolution.Shown is not null)
      {
        output.WriteLine($"    shown: {string.Join(", ", resolution.Shown.Select((slide, index) =>
          $"{PickFile.Letter(index)} {slide}"))}");
      }
      if (resolution.Why is not null)
      {
        output.WriteLine($"    why: {resolution.Why}");
      }
      output.WriteLine();
    }
    int none = resolved.Count(pick => pick.Resolved!.Chosen == PickFile.None);
    output.WriteLine($"{resolved.Count} resolved pick{(resolved.Count == 1 ? "" : "s")}: " +
      $"{resolved.Count - none} kept a variant, {none} kept none.");
    return 0;
  }

  private static string Offered(Pick pick)
  {
    return pick.By is null ? "" : pick.Date is null ? $"  by {pick.By}" : $"  by {pick.By}, {pick.Date}";
  }

  private static void WriteVariants(Pick pick, TextWriter output)
  {
    int width = pick.Variants.Max(variant => variant.Slide.Length);
    foreach (Variant variant in pick.Variants)
    {
      string mark = pick.Resolved?.Chosen == variant.Slide ? "* " : "  ";
      output.WriteLine($"  {mark}{variant.Slide.PadRight(width)}  {variant.Intent}");
    }
    if (pick.Resolved is null)
    {
      output.WriteLine();
    }
  }


}