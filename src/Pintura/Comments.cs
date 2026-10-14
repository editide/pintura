using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>A reviewer's note pinned to a slide. <see cref="At"/> is in points from the top-left of the element it
/// names, which it follows as the element moves, or of the slide when it names none.</summary>
public record Comment
{
  public string Id { get; init; } = "";
  public string Slide { get; init; } = "";
  public string? Element { get; init; }
  public double[] At { get; init; } = [0, 0];
  public string? By { get; init; }
  public string? Date { get; init; }
  public string Text { get; init; } = "";
  /// <summary>What an agent changed for it, left for the reviewer to check and resolve.</summary>
  public string? Done { get; init; }
  /// <summary>Why an agent didn't act on it.</summary>
  public string? Skipped { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);
}

/// <summary>One change to <c>comments.yaml</c>: a comment added, before the one <paramref name="Before"/> names when
/// it is put back where it was; one removed; one reworded to <paramref name="Text"/>, which reopens it; one reopened;
/// one restored as it was; or <paramref name="Many"/> of these, made together or not at all.</summary>
public record CommentChange(Comment? Add = null, string? Before = null, string? Remove = null, string? Id = null,
  string? Text = null, string? Reopen = null, Comment? Restore = null, List<CommentChange>? Many = null)
{
  public int Kinds => (Add is null ? 0 : 1) + (Remove is null ? 0 : 1) + (Id is null ? 0 : 1) +
    (Reopen is null ? 0 : 1) + (Restore is null ? 0 : 1) + (Many is null ? 0 : 1);
}

/// <summary>A comment as <c>--comments</c> lists it, with the file and line that write what it points at.</summary>
public record CommentEntry(string Id, string Slide, string? Element, double[] At, string? By, string? Date,
  string Text, string? Done, string? Skipped, string? File, int? Line);

/// <summary>Reads and writes <c>comments.yaml</c>, the comments beside the slides rather than in them, so a reviewer
/// pinning one never touches a file an agent is editing. Each change rewrites only its own entry.</summary>
public static class CommentFile
{
  public const string Name = "comments.yaml";
  private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

  private static readonly object Gate = new object();

  private record Entry(Comment Comment, int Start, int End);

  public static List<Comment> Load(string root, string displayBase)
  {
    string path = Path.Combine(root, Name);
    if (!File.Exists(path))
    {
      return [];
    }
    return Read(SourceFile.For(path, displayBase).Load("comments")).Select(entry => entry.Comment).ToList();
  }

  private static List<Entry> Read(Node document)
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
      var comment = new Comment
      {
        Id = map.Require("id").AsString(),
        Slide = map.Require("slide").AsString(),
        Element = map.Take("element")?.AsString(),
        At = map.Require("at").AsNumbers(2, "[x, y] in points"),
        By = map.Take("by")?.AsString(),
        Date = map.Take("date")?.AsString(),
        Text = map.Require("text").AsString(),
        Done = map.Take("done")?.AsString(),
        Skipped = map.Take("skipped")?.AsString(),
        Origin = new Origin(document.File.Display, node.Origin.Line, node.Origin.Column)
      };
      map.Done();
      if (comment.Done is not null && comment.Skipped is not null)
      {
        throw node.Error($"comment {comment.Id} is either done or skipped, not both");
      }
      if (!ids.Add(comment.Id))
      {
        throw node.Error($"comment {comment.Id} is written twice");
      }
      (int start, int end) = Node.Extent(text, node.Yaml);
      entries.Add(new Entry(comment, text.LastIndexOf('\n', Math.Max(start - 1, 0)) + 1, end));
    }
    return entries;
  }

  /// <summary>Applies a change and answers with the change that undoes it. A comment someone already removed is a
  /// conflict.</summary>
  public static CommentChange Apply(Presentation presentation, CommentChange change, string author, DateTime now)
  {
    if (change.Many is List<CommentChange> many)
    {
      var undos = new List<CommentChange>();
      try
      {
        foreach (CommentChange each in many)
        {
          undos.Insert(0, Apply(presentation, each, author, now));
        }
      }
      catch (PatchException)
      {
        foreach (CommentChange undo in undos)
        {
          Apply(presentation, undo, author, now);
        }
        throw;
      }
      return new CommentChange(Many: undos);
    }
    return Rewrite(presentation, (text, entries) =>
    {
      if (change.Add is Comment draft)
      {
        Comment comment = Draft(presentation, draft, entries, author, now);
        int index = change.Before is string before && entries.FindIndex(entry => entry.Comment.Id == before) is
          >= 0 and int found
          ? found
          : InDeckOrder(presentation, entries, comment.Slide);
        return (Insert(text, entries, index, comment), new CommentChange(Remove: comment.Id));
      }
      string id = change.Remove ?? change.Id ?? change.Reopen ?? change.Restore!.Id;
      int at = entries.FindIndex(entry => entry.Comment.Id == id);
      if (at < 0)
      {
        throw new PatchException($"comment {id} is gone; someone resolved it", true);
      }
      Comment existing = entries[at].Comment;
      if (change.Remove is not null)
      {
        string? next = at + 1 < entries.Count ? entries[at + 1].Comment.Id : null;
        return (Without(text, entries, at), new CommentChange(Add: existing, Before: next));
      }
      Comment changed = change.Restore is Comment restore
        ? existing with { Text = Words(restore.Text), Done = restore.Done, Skipped = restore.Skipped }
        : existing with { Text = change.Text is null ? existing.Text : Words(change.Text), Done = null, Skipped = null };
      return (Replace(text, entries, at, changed), new CommentChange(Restore: existing));
    });
  }

  /// <summary>Removes the comments on a slide, answering with them for an undo to put back.</summary>
  public static List<Comment> RemoveSlide(Presentation presentation, string slide)
  {
    var removed = new List<Comment>();
    while (true)
    {
      Comment? next = Rewrite(presentation, (text, entries) =>
      {
        int at = entries.FindIndex(entry => entry.Comment.Slide == slide);
        return at < 0 ? (text, null) : (Without(text, entries, at), entries[at].Comment);
      });
      if (next is null)
      {
        return removed;
      }
      removed.Add(next);
    }
  }

  /// <summary>Whoever git says is writing in the presentation's folder, or failing that the account.</summary>
  public static string Author(string root)
  {
    try
    {
      var start = new ProcessStartInfo("git", ["config", "user.name"])
      {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
      };
      using Process process = Process.Start(start)!;
      string name = process.StandardOutput.ReadToEnd().Trim();
      process.WaitForExit();
      if (process.ExitCode == 0 && name.Length > 0)
      {
        return name;
      }
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
    }
    return Environment.UserName;
  }

  private static T Rewrite<T>(Presentation presentation, Func<string, List<Entry>, (string? Text, T Answer)> change)
  {
    string path = Path.Combine(presentation.Root, Name);
    lock (Gate)
    {
      bool exists = File.Exists(path);
      string text = "";
      List<Entry> entries = [];
      if (exists)
      {
        try
        {
          Node document = SourceFile.For(path, presentation.Root).Load("comments");
          text = document.File.Text;
          entries = Read(document);
        }
        catch (LoadException exception)
        {
          throw new PatchException(exception.Message);
        }
      }
      (string? written, T answer) = change(text, entries);
      if (written == text)
      {
        return answer;
      }
      // An agent resolving a comment can save between the read and the write; refusing loses nothing.
      if ((File.Exists(path) ? File.ReadAllText(path) : "") != text || File.Exists(path) != exists)
      {
        throw new PatchException($"{Name} changed while the comment was written", true);
      }
      if (written is null)
      {
        File.Delete(path);
      }
      else
      {
        Files.Replace(path, Patcher.KeepLineEndings(text, written));
      }
      return answer;
    }
  }

  private static Comment Draft(Presentation presentation, Comment draft, List<Entry> entries, string author,
    DateTime now)
  {
    Slide slide = presentation.Slides.FirstOrDefault(candidate => candidate.Id == draft.Slide)
      ?? throw new PatchException($"no slide {draft.Slide}");
    // Putting back a comment whose element has since gone keeps it, for the check to report.
    if (draft.Id.Length == 0 && draft.Element is string element && slide.Error is null &&
      SceneBuilder.Build(presentation, slide).Items.All(item => item.Id != element))
    {
      throw new PatchException($"no element {element} on {slide.Id}");
    }
    if (draft.At is not [double x, double y] || !double.IsFinite(x) || !double.IsFinite(y))
    {
      throw new PatchException("a comment's at is [x, y] in points");
    }
    var taken = entries.Select(entry => entry.Comment.Id).ToHashSet(StringComparer.Ordinal);
    if (draft.Id.Length > 0 && taken.Contains(draft.Id))
    {
      throw new PatchException($"comment {draft.Id} is already there", true);
    }
    return draft with
    {
      Id = draft.Id.Length > 0 ? draft.Id : NewId(taken),
      At = [Math.Round(x, 1), Math.Round(y, 1)],
      By = draft.By ?? author,
      Date = draft.Date ?? now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
      Text = Words(draft.Text)
    };
  }

  private static string Words(string? text)
  {
    string words = (text ?? "").Trim().ReplaceLineEndings("\n");
    return words.Length > 0 ? words : throw new PatchException("a comment needs words");
  }

  private static string NewId(HashSet<string> taken)
  {
    while (true)
    {
      string id = string.Concat(Enumerable.Range(0, 4).Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]));
      // An id starting with a digit could read as a number.
      if (char.IsLetter(id[0]) && !taken.Contains(id))
      {
        return id;
      }
    }
  }

  /// <summary>Before the first comment on a later slide, so the file reads in deck order while it was written so.
  /// </summary>
  private static int InDeckOrder(Presentation presentation, List<Entry> entries, string slide)
  {
    List<string> order = presentation.Slides.Select(candidate => candidate.Id).ToList();
    int place = order.IndexOf(slide);
    int later = entries.FindIndex(entry => order.IndexOf(entry.Comment.Slide) > place);
    return later < 0 ? entries.Count : later;
  }

  private static string Render(Comment comment)
  {
    var map = new OrderedDictionary<string, object?> { ["id"] = comment.Id, ["slide"] = comment.Slide };
    if (comment.Element is not null)
    {
      map["element"] = comment.Element;
    }
    map["at"] = new List<object?> { comment.At[0], comment.At[1] };
    if (comment.By is not null)
    {
      map["by"] = comment.By;
    }
    if (comment.Date is not null)
    {
      map["date"] = comment.Date;
    }
    map["text"] = comment.Text;
    if (comment.Done is not null)
    {
      map["done"] = comment.Done;
    }
    if (comment.Skipped is not null)
    {
      map["skipped"] = comment.Skipped;
    }
    return YamlWriter.Item(map);
  }

  private static string Insert(string text, List<Entry> entries, int index, Comment comment)
  {
    if (entries.Count == 0)
    {
      return Render(comment) + "\n";
    }
    if (index < entries.Count)
    {
      return text.Insert(entries[index].Start, Render(comment) + "\n\n");
    }
    return $"{text.TrimEnd()}\n\n{Render(comment)}\n";
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

  private static string Replace(string text, List<Entry> entries, int index, Comment comment)
  {
    int start = entries[index].Start;
    int end = index + 1 < entries.Count ? entries[index + 1].Start : text.Length;
    while (end > start && char.IsWhiteSpace(text[end - 1]))
    {
      end--;
    }
    return text[..start] + Render(comment) + text[end..];
  }


}

/// <summary><c>--comments</c>: each comment in deck order, beside the file and line to change for it.</summary>
public static class CommentList
{
  public static List<CommentEntry> Entries(Presentation presentation)
  {
    List<string> order = presentation.Slides.Select(slide => slide.Id).ToList();
    return presentation.Comments
      .OrderBy(comment => order.IndexOf(comment.Slide) is int place and >= 0 ? place : int.MaxValue)
      .Select(comment => Entry(presentation, comment))
      .ToList();
  }

  private static CommentEntry Entry(Presentation presentation, Comment comment)
  {
    Origin? target = Target(presentation, comment);
    return new CommentEntry(comment.Id, comment.Slide, comment.Element, comment.At, comment.By, comment.Date,
      comment.Text, comment.Done, comment.Skipped, target?.File, target?.Line);
  }

  /// <summary>The line that writes the element a comment points at, or the slide's file when it points at none.
  /// </summary>
  public static Origin? Target(Presentation presentation, Comment comment)
  {
    Slide? slide = presentation.Slides.FirstOrDefault(candidate => candidate.Id == comment.Slide);
    if (slide is null)
    {
      return null;
    }
    if (comment.Element is null || slide.Error is not null)
    {
      return slide.Origin;
    }
    return SceneBuilder.Build(presentation, slide).Items.FirstOrDefault(item => item.Id == comment.Element)?.Origin;
  }

  public static int Write(Presentation presentation, bool json, TextWriter output)
  {
    List<CommentEntry> entries = Entries(presentation);
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(entries, PayloadJson.Indented));
      return 0;
    }
    if (entries.Count == 0)
    {
      output.WriteLine("no comments");
      return 0;
    }
    foreach (CommentEntry entry in entries)
    {
      string where = entry.Element is null
        ? $"{entry.Slide} at [{YamlWriter.Number(entry.At[0])}, {YamlWriter.Number(entry.At[1])}]"
        : $"{entry.Slide}/{entry.Element}";
      string target = entry.File is null ? "no longer in the presentation" : $"{entry.File}:{entry.Line}";
      string mark = entry.Done is not null ? "  done" : entry.Skipped is not null ? "  skipped" : "";
      output.WriteLine($"{entry.Id}  {where}  {target}{mark}");
      string by = entry.By is null ? "" : entry.Date is null ? $"{entry.By}: " : $"{entry.By}, {entry.Date}: ";
      output.WriteLine($"    {by}{Indented(entry.Text)}");
      if (entry.Done is not null)
      {
        output.WriteLine($"    done: {Indented(entry.Done)}");
      }
      if (entry.Skipped is not null)
      {
        output.WriteLine($"    skipped: {Indented(entry.Skipped)}");
      }
      output.WriteLine();
    }
    string file = presentation.Comments[0].Origin.File;
    int done = entries.Count(entry => entry.Done is not null);
    int skipped = entries.Count(entry => entry.Skipped is not null);
    output.WriteLine($"{entries.Count} comment{(entries.Count == 1 ? "" : "s")} in {file}: " +
      $"{entries.Count - done - skipped} open, {done} done, {skipped} skipped.");
    output.WriteLine("Act on each open one, then mark it done: with what you changed, or skipped: with why, for the " +
      "reviewer to resolve.");
    return 0;
  }

  private static string Indented(string text)
  {
    return text.Replace("\n", "\n    ");
  }


}