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
  /// <summary>The commit the deck was at when it was written, absent outside a repository.</summary>
  public string? Rev { get; init; }
  /// <summary>Whether the slide had changed since <see cref="Rev"/>, so the reviewer saw more than it.</summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Uncommitted { get; init; }
  public string Text { get; init; } = "";
  /// <summary>What an agent changed for it, left for the reviewer to check and resolve.</summary>
  public string? Done { get; init; }
  /// <summary>Why an agent didn't act on it.</summary>
  public string? Skipped { get; init; }
  /// <summary>What the reviewer asked for after an agent marked the comment, each marked in turn.</summary>
  public List<Reply>? Replies { get; init; }
  /// <summary>How it left the queue; only a comment in <c>comments-resolved.yaml</c> has one.</summary>
  public Resolution? Resolved { get; init; }
  [JsonIgnore] public Origin Origin { get; init; } = new Origin("", 0, 0);

  /// <summary>The note on the last thing asked, the last reply or else the comment; with neither note, the comment
  /// waits on an agent.</summary>
  [JsonIgnore] public string? LastDone => Replies is [.., Reply last] ? last.Done : Done;
  [JsonIgnore] public string? LastSkipped => Replies is [.., Reply last] ? last.Skipped : Skipped;
}

/// <summary>A reviewer's answer to a comment an agent marked: more to do, or with <see cref="Revert"/>, undoing what
/// the agent did, where the words, if any, say why.</summary>
public record Reply
{
  public string? By { get; init; }
  public string? Date { get; init; }
  public string? Rev { get; init; }
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Uncommitted { get; init; }
  public string? Text { get; init; }
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Revert { get; init; }
  public string? Done { get; init; }
  public string? Skipped { get; init; }
}

/// <summary>How a comment left the queue: who resolved it, when, the commit the deck was then at, and how its thread
/// ended, one of <see cref="CommentFile.Outcomes"/>.</summary>
public record Resolution(string? By, string? Date, string? Rev,
  [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Uncommitted, string Outcome);

/// <summary>The commit a deck was at and what had changed since, as a reviewer saw it: the slides whose own files
/// changed, and whether anything they share did, such as the theme or the list.</summary>
public record Snapshot(string Rev, bool Shared, IReadOnlySet<string> Slides)
{
  /// <summary>Whether the slide looked other than the commit has it, so the reviewer saw more than it.</summary>
  public bool Uncommitted(string slide)
  {
    return Shared || Slides.Contains(slide);
  }
}

/// <summary>One change to <c>comments.yaml</c>: a comment added, before the one <paramref name="Before"/> names when
/// it is put back where it was; one removed; its last ask reworded to <paramref name="Text"/>, which reopens it; one
/// replied to with <paramref name="Text"/>, or reverted; one restored as it was; one resolved into
/// <c>comments-resolved.yaml</c>, or reopened from it before <paramref name="Before"/>; or <paramref name="Many"/> of
/// these, made together or not at all.</summary>
public record CommentChange(Comment? Add = null, string? Before = null, string? Remove = null, string? Id = null,
  string? Text = null, string? Reply = null, bool Revert = false, Comment? Restore = null,
  List<CommentChange>? Many = null, string? Resolve = null, string? Reopen = null)
{
  public int Kinds => (Add is null ? 0 : 1) + (Remove is null ? 0 : 1) + (Id is null ? 0 : 1) +
    (Reply is null ? 0 : 1) + (Restore is null ? 0 : 1) + (Many is null ? 0 : 1) + (Resolve is null ? 0 : 1) +
    (Reopen is null ? 0 : 1);
}

/// <summary>A comment as <c>--comments</c> lists it, with the file and line that write what it points at.</summary>
public record CommentEntry(string Id, string Slide, string? Element, double[] At, string? By, string? Date,
  string Text, string? Done, string? Skipped, List<Reply>? Replies, string? File, int? Line);

/// <summary>Reads and writes <c>comments.yaml</c>, the comments beside the slides rather than in them, so a reviewer
/// pinning one never touches a file an agent is editing, and <c>comments-resolved.yaml</c>, where a resolved comment
/// goes with its whole thread and how it ended. Each change rewrites only its own entries.</summary>
public static class CommentFile
{
  public const string Name = "comments.yaml";
  public const string ResolvedName = "comments-resolved.yaml";
  private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

  /// <summary>How a thread can end: the agent's last note accepted as it stood, accepted after more replies, undone
  /// at the reviewer's asking, passed on, dropped while waiting on an agent, or gone with its slide.</summary>
  public static readonly string[] Outcomes = ["accepted", "partly", "reverted", "skipped", "withdrawn", "removed"];

  private static readonly object Gate = new object();

  private record Entry(Comment Comment, int Start, int End);

  /// <summary>Both files as read, each with its entries.</summary>
  private record Texts(string Live, List<Entry> Entries, string Archive, List<Entry> Archived);

  public static List<Comment> Load(string root, string displayBase)
  {
    return Load(Path.Combine(root, Name), displayBase, false);
  }

  private static List<Comment> Load(string path, string displayBase, bool archive)
  {
    if (!File.Exists(path))
    {
      return [];
    }
    return Read(SourceFile.For(path, displayBase).Load("comments"), archive).Select(entry => entry.Comment).ToList();
  }

  /// <summary>The resolved comments, in the order they were resolved.</summary>
  public static List<Comment> LoadResolved(string root, string displayBase)
  {
    return Load(Path.Combine(root, ResolvedName), displayBase, true);
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
      List<Node> replies = map.Take("replies")?.AsList() ?? [];
      var comment = new Comment
      {
        Id = map.Require("id").AsString(),
        Slide = map.Require("slide").AsString(),
        Element = map.Take("element")?.AsString(),
        At = map.Require("at").AsNumbers(2, "[x, y] in points"),
        By = map.Take("by")?.AsString(),
        Date = map.Take("date")?.AsString(),
        Rev = map.Take("rev")?.AsString(),
        Uncommitted = map.Take("uncommitted")?.AsBoolean() ?? false,
        Text = map.Require("text").AsString(),
        Done = map.Take("done")?.AsString(),
        Skipped = map.Take("skipped")?.AsString(),
        Replies = replies.Count > 0 ? replies.Select(ReadReply).ToList() : null,
        Resolved = archive ? ReadResolution(map.Require("resolved")) : null,
        Origin = new Origin(document.File.Display, node.Origin.Line, node.Origin.Column)
      };
      map.Done();
      if (comment.Done is not null && comment.Skipped is not null)
      {
        throw node.Error($"comment {comment.Id} is either done or skipped, not both");
      }
      (string? done, string? skipped) = (comment.Done, comment.Skipped);
      for (int index = 0; index < replies.Count; index++)
      {
        Reply reply = comment.Replies![index];
        if (done is null && skipped is null)
        {
          throw replies[index].Error($"comment {comment.Id} has a reply before an agent marked what it answers");
        }
        if (reply.Revert && done is null)
        {
          throw replies[index].Error($"comment {comment.Id} reverts what an agent skipped rather than did");
        }
        if (reply.Done is not null && reply.Skipped is not null)
        {
          throw replies[index].Error($"comment {comment.Id} has a reply either done or skipped, not both");
        }
        (done, skipped) = (reply.Done, reply.Skipped);
      }
      // A resolved id can come back on a later comment written before ids avoided resolved ones.
      if (!ids.Add(comment.Id) && !archive)
      {
        throw node.Error($"comment {comment.Id} is written twice");
      }
      (int start, int end) = Node.Extent(text, node.Yaml);
      entries.Add(new Entry(comment, text.LastIndexOf('\n', Math.Max(start - 1, 0)) + 1, end));
    }
    return entries;
  }

  private static Reply ReadReply(Node node)
  {
    MapNode map = node.AsMap();
    bool revert = map.Take("revert")?.AsBoolean() ?? false;
    var reply = new Reply
    {
      By = map.Take("by")?.AsString(),
      Date = map.Take("date")?.AsString(),
      Rev = map.Take("rev")?.AsString(),
      Uncommitted = map.Take("uncommitted")?.AsBoolean() ?? false,
      Text = revert ? map.Take("text")?.AsString() : map.Require("text").AsString(),
      Revert = revert,
      Done = map.Take("done")?.AsString(),
      Skipped = map.Take("skipped")?.AsString()
    };
    map.Done();
    return reply;
  }

  private static Resolution ReadResolution(Node node)
  {
    MapNode map = node.AsMap();
    Node outcomeNode = map.Require("outcome");
    var resolution = new Resolution(map.Take("by")?.AsString(), map.Take("date")?.AsString(),
      map.Take("rev")?.AsString(), map.Take("uncommitted")?.AsBoolean() ?? false, outcomeNode.AsString());
    map.Done();
    if (!Outcomes.Contains(resolution.Outcome))
    {
      throw outcomeNode.Error($"outcome is one of {string.Join(", ", Outcomes)}");
    }
    return resolution;
  }

  /// <summary>Applies a change and answers with the change that undoes it. A comment someone already removed is a
  /// conflict. <paramref name="seen"/> is the version the reviewer is looking at, recorded on what they write and
  /// resolve; null outside a repository.</summary>
  public static CommentChange Apply(Presentation presentation, CommentChange change, string author, DateTime now,
    Snapshot? seen = null)
  {
    if (change.Many is List<CommentChange> many)
    {
      var undos = new List<CommentChange>();
      try
      {
        foreach (CommentChange each in many)
        {
          undos.Insert(0, Apply(presentation, each, author, now, seen));
        }
      }
      catch (PatchException)
      {
        foreach (CommentChange undo in undos)
        {
          Apply(presentation, undo, author, now, seen);
        }
        throw;
      }
      return new CommentChange(Many: undos);
    }
    if (change.Reopen is string reopen)
    {
      return Reopen(presentation, reopen, change.Before);
    }
    if (change.Resolve is string resolve)
    {
      Resolve(presentation, resolve, author, now, seen, out CommentChange undo);
      return undo;
    }
    return Rewrite(presentation, files =>
    {
      (string text, List<Entry> entries) = (files.Live, files.Entries);
      if (change.Add is Comment draft)
      {
        Comment comment = Draft(presentation, draft, files, author, now, seen);
        int index = change.Before is string before && entries.FindIndex(entry => entry.Comment.Id == before) is
          >= 0 and int found
          ? found
          : InDeckOrder(presentation, entries, comment.Slide);
        return (Insert(text, entries, index, comment), files.Archive, new CommentChange(Remove: comment.Id));
      }
      string id = change.Remove ?? change.Id ?? change.Reply ?? change.Restore!.Id;
      int at = Find(entries, id);
      Comment existing = entries[at].Comment;
      if (change.Remove is not null)
      {
        string? next = at + 1 < entries.Count ? entries[at + 1].Comment.Id : null;
        return (Without(text, entries, at), files.Archive, new CommentChange(Add: existing, Before: next));
      }
      Comment changed = change switch
      {
        { Restore: Comment restore } => existing with
        {
          Text = Words(restore.Text), Done = restore.Done, Skipped = restore.Skipped,
          Replies = restore.Replies is { Count: > 0 } replies ? replies : null
        },
        { Reply: not null } => existing with
        {
          Replies = [.. existing.Replies ?? [], Answer(existing, change, author, now, seen)]
        },
        _ => Reworded(existing, Words(change.Text))
      };
      return (Replace(text, entries, at, changed), files.Archive, new CommentChange(Restore: existing));
    });
  }

  /// <summary>Moves a comment to the end of <c>comments-resolved.yaml</c> with how its thread ended, or with
  /// <paramref name="outcome"/> when something other than the thread decided it.</summary>
  private static Comment Resolve(Presentation presentation, string id, string author, DateTime now, Snapshot? seen,
    out CommentChange undo, string? outcome = null)
  {
    CommentChange? reopen = null;
    Comment resolved = Rewrite(presentation, files =>
    {
      int at = Find(files.Entries, id);
      Comment existing = files.Entries[at].Comment;
      reopen = new CommentChange(Reopen: id,
        Before: at + 1 < files.Entries.Count ? files.Entries[at + 1].Comment.Id : null);
      Comment archived = existing with
      {
        Resolved = new Resolution(author, Stamp(now), seen?.Rev, seen?.Uncommitted(existing.Slide) ?? false,
          outcome ?? Outcome(existing))
      };
      return (Without(files.Live, files.Entries, at),
        Insert(files.Archive, files.Archived, files.Archived.Count, archived), archived);
    });
    undo = reopen!;
    return resolved;
  }

  /// <summary>Takes the latest resolution of a comment back out of <c>comments-resolved.yaml</c>, putting the
  /// comment back before <paramref name="before"/>, or in deck order.</summary>
  private static CommentChange Reopen(Presentation presentation, string id, string? before)
  {
    return Rewrite(presentation, files =>
    {
      int at = files.Archived.FindLastIndex(entry => entry.Comment.Id == id);
      if (at < 0)
      {
        throw new PatchException($"comment {id} isn't resolved; someone reopened it", true);
      }
      if (files.Entries.Any(entry => entry.Comment.Id == id))
      {
        throw new PatchException($"comment {id} is already there", true);
      }
      Comment comment = files.Archived[at].Comment with { Resolved = null };
      int index = before is not null &&
        files.Entries.FindIndex(entry => entry.Comment.Id == before) is >= 0 and int found
        ? found
        : InDeckOrder(presentation, files.Entries, comment.Slide);
      return (Insert(files.Live, files.Entries, index, comment), Without(files.Archive, files.Archived, at),
        new CommentChange(Resolve: id));
    });
  }

  /// <summary>How a thread ended: undone if the reviewer ever asked for that, dropped if it still waits on an agent,
  /// and otherwise as the agent last marked it, partly if it took more than one ask.</summary>
  public static string Outcome(Comment comment)
  {
    if (comment.Replies?.Any(reply => reply.Revert) == true)
    {
      return "reverted";
    }
    if (comment.LastDone is null && comment.LastSkipped is null)
    {
      return "withdrawn";
    }
    if (comment.Replies is { Count: > 0 })
    {
      return "partly";
    }
    return comment.Done is not null ? "accepted" : "skipped";
  }

  /// <summary>Resolves the comments on a slide being deleted, answering with them for an undo to reopen.</summary>
  public static List<Comment> RemoveSlide(Presentation presentation, string slide, string author, DateTime now,
    Snapshot? seen)
  {
    var removed = new List<Comment>();
    foreach (Comment comment in Load(presentation.Root, presentation.Root).Where(each => each.Slide == slide))
    {
      removed.Add(Resolve(presentation, comment.Id, author, now, seen, out _, "removed"));
    }
    return removed;
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

  private static int Find(List<Entry> entries, string id)
  {
    int at = entries.FindIndex(entry => entry.Comment.Id == id);
    return at >= 0 ? at : throw new PatchException($"comment {id} is gone; someone resolved it", true);
  }

  /// <summary>Reads both files, applies the change, and writes what it changed, null deleting a file. A change to
  /// both writes both or neither.</summary>
  private static T Rewrite<T>(Presentation presentation, Func<Texts, (string? Live, string? Archive, T Answer)> change)
  {
    string livePath = Path.Combine(presentation.Root, Name);
    string archivePath = Path.Combine(presentation.Root, ResolvedName);
    lock (Gate)
    {
      (string live, List<Entry> entries) = ReadFile(livePath, presentation.Root, false);
      (string archive, List<Entry> archived) = ReadFile(archivePath, presentation.Root, true);
      (string? writtenLive, string? writtenArchive, T answer) = change(new Texts(live, entries, archive, archived));
      bool liveChanged = writtenLive != live;
      bool archiveChanged = writtenArchive != archive;
      // An agent resolving a comment can save between the read and the write; refusing loses nothing.
      if (liveChanged && Current(livePath) != live || archiveChanged && Current(archivePath) != archive)
      {
        throw new PatchException($"{(liveChanged && Current(livePath) != live ? Name : ResolvedName)} changed " +
          "while the comment was written", true);
      }
      if (liveChanged)
      {
        Write(livePath, live, writtenLive);
      }
      if (archiveChanged)
      {
        try
        {
          Write(archivePath, archive, writtenArchive);
        }
        catch (Exception) when (liveChanged)
        {
          Write(livePath, writtenLive ?? "", live.Length > 0 ? live : null);
          throw;
        }
      }
      return answer;
    }
  }

  private static (string Text, List<Entry> Entries) ReadFile(string path, string root, bool archive)
  {
    if (!File.Exists(path))
    {
      return ("", []);
    }
    try
    {
      Node document = SourceFile.For(path, root).Load("comments");
      return (document.File.Text, Read(document, archive));
    }
    catch (LoadException exception)
    {
      throw new PatchException(exception.Message);
    }
  }

  private static string Current(string path)
  {
    return File.Exists(path) ? File.ReadAllText(path) : "";
  }

  private static void Write(string path, string before, string? written)
  {
    if (written is null)
    {
      File.Delete(path);
    }
    else
    {
      Files.Replace(path, Patcher.KeepLineEndings(before, written));
    }
  }

  private static Reply Answer(Comment comment, CommentChange change, string author, DateTime now, Snapshot? seen)
  {
    if (comment.LastDone is null && comment.LastSkipped is null)
    {
      throw new PatchException($"comment {comment.Id} still waits on an agent; reword it instead", true);
    }
    if (change.Revert && comment.LastDone is null)
    {
      throw new PatchException($"comment {comment.Id} was skipped, so there's nothing to revert", true);
    }
    return new Reply
    {
      By = author,
      Date = Stamp(now),
      Rev = seen?.Rev,
      Uncommitted = seen?.Uncommitted(comment.Slide) ?? false,
      Text = change.Revert && string.IsNullOrWhiteSpace(change.Text) ? null : Words(change.Text),
      Revert = change.Revert
    };
  }

  /// <summary>The comment with its last ask, the last reply or else its own text, reworded and waiting again.
  /// </summary>
  private static Comment Reworded(Comment comment, string text)
  {
    if (comment.Replies is [.., Reply last])
    {
      Reply reworded = last with { Text = text, Done = null, Skipped = null };
      return comment with { Replies = [.. comment.Replies[..^1], reworded] };
    }
    return comment with { Text = text, Done = null, Skipped = null };
  }

  private static string Stamp(DateTime now)
  {
    return now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
  }

  private static Comment Draft(Presentation presentation, Comment draft, Texts files, string author, DateTime now,
    Snapshot? seen)
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
    if (draft.Id.Length > 0 && files.Entries.Any(entry => entry.Comment.Id == draft.Id))
    {
      throw new PatchException($"comment {draft.Id} is already there", true);
    }
    bool fresh = draft.Id.Length == 0;
    // Ids stay unique across the resolved comments too, so a thread's id names it for good.
    var taken = files.Entries.Concat(files.Archived).Select(entry => entry.Comment.Id)
      .ToHashSet(StringComparer.Ordinal);
    return draft with
    {
      Id = fresh ? NewId(taken) : draft.Id,
      At = [Math.Round(x, 1), Math.Round(y, 1)],
      By = draft.By ?? author,
      Date = draft.Date ?? Stamp(now),
      Rev = fresh ? seen?.Rev : draft.Rev,
      Uncommitted = fresh ? seen?.Uncommitted(draft.Slide) ?? false : draft.Uncommitted,
      Text = Words(draft.Text),
      Resolved = null
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
    Seen(map, comment.Rev, comment.Uncommitted);
    map["text"] = comment.Text;
    if (comment.Done is not null)
    {
      map["done"] = comment.Done;
    }
    if (comment.Skipped is not null)
    {
      map["skipped"] = comment.Skipped;
    }
    string rendered = YamlWriter.Item(map);
    if (comment.Replies is { Count: > 0 } replies)
    {
      // Written in block style whatever their length, so a thread reads down the page.
      IEnumerable<string> lines = replies.SelectMany(reply => YamlWriter.Item(Render(reply)).Split('\n'));
      rendered += "\n  replies:\n" + string.Join("\n", lines.Select(line => line.Length == 0 ? "" : "    " + line));
    }
    if (comment.Resolved is Resolution resolved)
    {
      var resolution = new OrderedDictionary<string, object?>();
      if (resolved.By is not null)
      {
        resolution["by"] = resolved.By;
      }
      if (resolved.Date is not null)
      {
        resolution["date"] = resolved.Date;
      }
      Seen(resolution, resolved.Rev, resolved.Uncommitted);
      resolution["outcome"] = resolved.Outcome;
      // An entry of its own, re-indented as the comment's last key.
      rendered += "\n  " + YamlWriter.Item(new OrderedDictionary<string, object?> { ["resolved"] = resolution })[2..];
    }
    return rendered;
  }

  private static void Seen(OrderedDictionary<string, object?> map, string? rev, bool uncommitted)
  {
    if (rev is not null)
    {
      map["rev"] = rev;
    }
    if (uncommitted)
    {
      map["uncommitted"] = true;
    }
  }

  private static OrderedDictionary<string, object?> Render(Reply reply)
  {
    var map = new OrderedDictionary<string, object?>();
    if (reply.By is not null)
    {
      map["by"] = reply.By;
    }
    if (reply.Date is not null)
    {
      map["date"] = reply.Date;
    }
    Seen(map, reply.Rev, reply.Uncommitted);
    if (reply.Text is not null)
    {
      map["text"] = reply.Text;
    }
    if (reply.Revert)
    {
      map["revert"] = true;
    }
    if (reply.Done is not null)
    {
      map["done"] = reply.Done;
    }
    if (reply.Skipped is not null)
    {
      map["skipped"] = reply.Skipped;
    }
    return map;
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
      comment.Text, comment.Done, comment.Skipped, comment.Replies, target?.File, target?.Line);
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

  public static int Write(Presentation presentation, bool json, TextWriter output, SlideSelection? only = null)
  {
    List<CommentEntry> entries = Entries(presentation);
    string named = "";
    if (only is not null)
    {
      HashSet<string> picked = [.. only.Pick(presentation.Slides, slide => slide.Id).Select(slide => slide.Id)];
      entries = [.. entries.Where(entry => picked.Contains(entry.Slide))];
      named = " on the slides named";
    }
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(entries, PayloadJson.Indented));
      return 0;
    }
    if (entries.Count == 0)
    {
      output.WriteLine($"no comments{named}");
      return 0;
    }
    foreach (CommentEntry entry in entries)
    {
      string where = entry.Element is null
        ? $"{entry.Slide} at [{YamlWriter.Number(entry.At[0])}, {YamlWriter.Number(entry.At[1])}]"
        : $"{entry.Slide}/{entry.Element}";
      string target = entry.File is null ? "no longer in the presentation" : $"{entry.File}:{entry.Line}";
      Reply last = Last(entry);
      string mark = last.Done is not null ? "  done" : last.Skipped is not null ? "  skipped" : "";
      output.WriteLine($"{entry.Id}  {where}  {target}{mark}");
      WriteThread(new Reply { By = entry.By, Date = entry.Date, Text = entry.Text, Done = entry.Done,
        Skipped = entry.Skipped }, entry.Replies, output);
    }
    string file = presentation.Comments[0].Origin.File;
    int done = entries.Count(entry => Last(entry).Done is not null);
    int skipped = entries.Count(entry => Last(entry).Skipped is not null);
    output.WriteLine($"{entries.Count} comment{(entries.Count == 1 ? "" : "s")}{named} in {file}: " +
      $"{entries.Count - done - skipped} open, {done} done, {skipped} skipped.");
    output.WriteLine("Act on each open one, then mark it done: with what you changed, or skipped: with why, for the " +
      "reviewer to resolve; in a thread, mark its last reply.");
    return 0;
  }

  /// <summary><c>--comments --resolved</c>: the resolved comments in the order they were resolved, each with how
  /// its thread ended.</summary>
  public static int WriteResolved(string root, bool json, TextWriter output)
  {
    List<Comment> resolved = CommentFile.LoadResolved(root, Directory.GetCurrentDirectory());
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(resolved, PayloadJson.Indented));
      return 0;
    }
    if (resolved.Count == 0)
    {
      output.WriteLine("no resolved comments");
      return 0;
    }
    foreach (Comment comment in resolved)
    {
      string where = comment.Element is null
        ? $"{comment.Slide} at [{YamlWriter.Number(comment.At[0])}, {YamlWriter.Number(comment.At[1])}]"
        : $"{comment.Slide}/{comment.Element}";
      Resolution resolution = comment.Resolved!;
      string by = resolution.By is null ? "" : resolution.Date is null ? $" by {resolution.By}" :
        $" by {resolution.By}, {resolution.Date}";
      string at = resolution.Rev is null ? "" : $" at {resolution.Rev}{(resolution.Uncommitted ? " and later" : "")}";
      output.WriteLine($"{comment.Id}  {where}  {resolution.Outcome}{by}{at}");
      WriteThread(new Reply { By = comment.By, Date = comment.Date, Text = comment.Text, Done = comment.Done,
        Skipped = comment.Skipped }, comment.Replies, output);
    }
    IEnumerable<string> counts = CommentFile.Outcomes
      .Select(outcome => (outcome, count: resolved.Count(comment => comment.Resolved!.Outcome == outcome)))
      .Where(pair => pair.count > 0)
      .Select(pair => $"{pair.count} {pair.outcome}");
    output.WriteLine($"{resolved.Count} resolved comment{(resolved.Count == 1 ? "" : "s")}: " +
      $"{string.Join(", ", counts)}.");
    return 0;
  }

  /// <summary>A thread as a reader follows it: each ask with who wrote it and when, and the agent's note under it.
  /// </summary>
  private static void WriteThread(Reply first, List<Reply>? replies, TextWriter output)
  {
    foreach (Reply ask in replies is null ? [first] : replies.Prepend(first))
    {
      string by = ask.By is null ? "" : ask.Date is null ? $"{ask.By}: " : $"{ask.By}, {ask.Date}: ";
      string words = ask.Revert ? ask.Text is null ? "revert" : $"revert: {ask.Text}" : ask.Text ?? "";
      output.WriteLine($"    {by}{Indented(words)}");
      if (ask.Done is not null)
      {
        output.WriteLine($"    done: {Indented(ask.Done)}");
      }
      if (ask.Skipped is not null)
      {
        output.WriteLine($"    skipped: {Indented(ask.Skipped)}");
      }
    }
    output.WriteLine();
  }

  private static Reply Last(CommentEntry entry)
  {
    return entry.Replies is [.., Reply last] ? last : new Reply { Done = entry.Done, Skipped = entry.Skipped };
  }

  private static string Indented(string text)
  {
    return text.Replace("\n", "\n    ");
  }


}