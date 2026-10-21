using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>A mouse edit the patcher refused. A conflict means the file no longer holds what the page drew, so the
/// page redraws from it; otherwise the edit is one for an agent or a text editor.</summary>
public class PatchException : Exception
{
  public PatchException(string message, bool conflict = false)
    : base(message)
  {
    Conflict = conflict;
  }

  public bool Conflict { get; }
}

/// <summary>One value a mouse edit writes: its key path in the slide's file, or in <paramref name="File"/> where it
/// names the theme's or a layout's as origins name them, its source as the page drew it or null where the key was
/// absent, and either numbers to a hundredth of a point, for a frame or a point, each paragraph's text with
/// <see cref="Patcher.FieldMark"/> where a field sits, a scalar <paramref name="Value"/> such as a color or a width, or
/// the source to restore, as an undo writes back. A delete removes the element at the path; a restore without a
/// source puts one back after the element named by <paramref name="After"/>, or first. A format applies to the
/// paragraphs once reworded.</summary>
public record ValueEdit(string Path, string? Source, double[]? Numbers = null, List<string>? Paragraphs = null,
  string? Restore = null, bool Delete = false, string? After = null, TextFormat? Format = null, string? Value = null,
  string? File = null);

/// <summary>Bold, italic, or underline turned on or off over stretches of paragraphs, in characters of the text the
/// page reads back, with <see cref="Patcher.FieldMark"/> for a field.</summary>
public record TextFormat(string Mark, bool On, List<FormatRange> Ranges);

public record FormatRange(int Paragraph, int Start, int End);

/// <summary>A file's text after a patch, and the edits that write it back as it was, in the order to apply them.
/// </summary>
public record Patched(string Text, List<ValueEdit> Undo);

/// <summary>An entry in a presentation's <c>slides</c>: a slide by file stem, or a section by name.</summary>
public record ListEntry(string? Slide = null, string? Section = null);

/// <summary>A slide's line in <c>slides</c> moved from after <paramref name="From"/> to after <paramref name="To"/>,
/// either null for the top of the list. A delete removes the line after <paramref name="From"/>, with the slide's
/// file and its comments; a restore puts the line back after <paramref name="To"/>, with the file's text and the
/// comments it had.</summary>
public record SlideMove(string Slide, ListEntry? From, ListEntry? To, bool Delete = false, string? Restore = null,
  List<Comment>? Comments = null);

/// <summary>Writes mouse edits back as patches: each value's span of source is found again by its key path and
/// replaced alone, so comments, formatting, and an agent's edits elsewhere in the file survive.</summary>
public static partial class Patcher
{
  /// <summary>Stands for a field in a paragraph's text, which an edit keeps as written.</summary>
  public const char FieldMark = '￼';

  private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];
  private static readonly object FileGate = new object();

  public static List<ValueEdit> ApplyToFile(string path, IReadOnlyList<ValueEdit> edits, MarkupContext context)
  {
    return Rewrite(path, text =>
    {
      Patched patched = Apply(text, edits, context);
      return (patched.Text, patched.Undo);
    });
  }

  public static SlideMove? MoveInFile(string path, SlideMove move)
  {
    return Rewrite(path, text => Move(text, move));
  }

  private static T Rewrite<T>(string path, Func<string, (string Text, T Undo)> patch)
  {
    lock (FileGate)
    {
      byte[] bytes = File.ReadAllBytes(path);
      bool marked = bytes.AsSpan().StartsWith(ByteOrderMark);
      string text = Encoding.UTF8.GetString(marked ? bytes[3..] : bytes);
      (string patched, T undo) = patch(text);
      patched = KeepLineEndings(text, patched);
      if (patched == text)
      {
        return undo;
      }
      // An agent's save can land between the read and the write; refusing then loses nothing, where writing would
      // overwrite it.
      if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
      {
        throw new PatchException($"{Path.GetFileName(path)} changed while the edit was written", true);
      }
      byte[] body = Encoding.UTF8.GetBytes(patched);
      Files.Replace(path, marked ? [.. ByteOrderMark, .. body] : body);
      return undo;
    }
  }

  /// <summary>Writes <paramref name="patched"/> with CRLF wherever <paramref name="original"/> used only CRLF, as a
  /// Windows checkout does, since the edits insert plain line feeds.</summary>
  public static string KeepLineEndings(string original, string patched)
  {
    return original.Contains("\r\n") && !LoneLineFeed().IsMatch(original)
      ? LoneLineFeed().Replace(patched, "\r\n")
      : patched;
  }

  [GeneratedRegex("(?<!\r)\n")]
  private static partial Regex LoneLineFeed();

  /// <summary>Each edit's undo restores the smallest node it changed: the value itself, or the map or shorthand
  /// title a new key went into, so undoing leaves the file as it was around whatever an agent changed elsewhere.
  /// </summary>
  public static Patched Apply(string text, IReadOnlyList<ValueEdit> edits, MarkupContext context)
  {
    var undo = new List<ValueEdit>();
    foreach (ValueEdit edit in edits)
    {
      if (edit.Path.Length == 0)
      {
        if (edit.Source != text || edit.Restore is null)
        {
          throw Conflict(edit);
        }
        undo.Insert(0, new ValueEdit("", edit.Restore, Restore: text));
        text = edit.Restore;
        continue;
      }
      if (edit.Delete)
      {
        (text, ValueEdit restore) = Delete(text, edit);
        undo.Insert(0, restore);
        continue;
      }
      if (edit.Restore is not null && edit.Source is null &&
        edit.Path.StartsWith("elements.", StringComparison.Ordinal))
      {
        text = Insert(text, edit);
        undo.Insert(0, new ValueEdit(edit.Path, SourceAt(text, edit.Path), Delete: true));
        continue;
      }
      string anchor = Existing(Parse(text), edit.Path);
      string? before = SourceAt(text, anchor);
      text = edit.Numbers is not null ? Numbers(text, edit) : edit.Paragraphs is not null
        ? Text(text, edit, context)
        : edit.Value is not null
          ? Scalar(text, edit)
          : edit.Restore is not null
            ? Restore(text, edit)
            : throw new PatchException($"the edit to {edit.Path} carries no value");
      string? after = SourceAt(text, anchor);
      if (before is not null && after is not null && before != after)
      {
        undo.Insert(0, new ValueEdit(anchor, after, Restore: before));
      }
    }
    return new Patched(text, undo);
  }

  /// <summary>The edits that write the node at <paramref name="path"/>, or the whole file without one, as
  /// <paramref name="original"/> has it: restored in place, deleted where the original lacks it, or for an element
  /// only the original has, put back after the nearest element before it that the file still has. None when the two
  /// agree.</summary>
  public static List<ValueEdit> Revert(string text, string original, string? path)
  {
    if (path is null)
    {
      return text == original ? [] : [new ValueEdit("", text, Restore: original)];
    }
    YamlNode root = Parse(text);
    YamlNode earlier = Parse(original);
    YamlNode? now = Locate(root, path).Node;
    YamlNode? was = Locate(earlier, path).Node;
    string? source = now is null ? null : Node.Span(text, now);
    if (was is not null && now is not null)
    {
      string restore = Node.Span(original, was);
      return restore == source ? [] : [new ValueEdit(path, source, Restore: restore)];
    }
    if (was is null && now is null)
    {
      return [];
    }
    bool element = path.StartsWith("elements.", StringComparison.Ordinal) && (was ?? now) is YamlMappingNode;
    if (!element)
    {
      throw new PatchException($"{path} was {(was is null ? "added" : "removed")} since; revert it in the file");
    }
    if (now is not null)
    {
      return [new ValueEdit(path, source, Delete: true)];
    }
    (int start, int end) = EntryLines(original, was!);
    string lines = original[start..end];
    if (Value((YamlMappingNode)root, "elements") is YamlSequenceNode { Children.Count: 0 })
    {
      return [new ValueEdit(path, null, Restore: $"\n{lines.TrimEnd('\r', '\n')}")];
    }
    HashSet<string?> kept = Ids(root).ToHashSet();
    List<string?> before = Ids(earlier);
    string? after = before.Take(before.IndexOf(Id(was!))).LastOrDefault(kept.Contains);
    return [new ValueEdit(path, null, Restore: lines, After: after)];
  }

  /// <summary>The source at <paramref name="path"/>, or null where the file has no such key.</summary>
  public static string? SourceAt(string text, string path)
  {
    (YamlNode? node, _, _) = Locate(Parse(text), path);
    return node is null ? null : Node.Span(text, node);
  }

  #region Numbers

  private static string Numbers(string text, ValueEdit edit)
  {
    double[] numbers = edit.Numbers!.Select(Round).ToArray();
    string value = $"[{string.Join(", ", numbers.Select(number => number.ToString(CultureInfo.InvariantCulture)))}]";
    (YamlNode? node, YamlNode? parent, string key) = Locate(Parse(text), edit.Path);

    bool Written(string candidate)
    {
      (YamlNode? written, _, _) = Locate(Parse(candidate), edit.Path);
      return written is YamlSequenceNode sequence && sequence.Children
        .Select(child => (child as YamlScalarNode)?.Value)
        .SequenceEqual(numbers.Select(number => number.ToString(CultureInfo.InvariantCulture)));
    }

    if (node is not null)
    {
      Expect(text, node, edit);
      if (node is YamlSequenceNode { Style: SequenceStyle.Block })
      {
        throw new PatchException($"{edit.Path} is a block list; write it as {value} to move it with the mouse");
      }
      return First(edit, Written, Splice(text, node, value));
    }
    if (edit.Source is not null)
    {
      throw Conflict(edit);
    }
    string entry = $"{key}: {value}";
    switch (parent)
    {
      case YamlMappingNode { Style: MappingStyle.Flow } map:
        (int mapStart, int mapEnd) = Node.Extent(text, map);
        int at = mapEnd - 1;
        while (at > 0 && text[at - 1] == ' ')
        {
          at--;
        }
        return First(edit, Written, map.Children.Count == 0
          ? Splice(text, mapStart, mapEnd, $"{{ {entry} }}")
          : Splice(text, at, at, $", {entry}"));
      case YamlMappingNode map:
        YamlNode first = map.Children.First().Key;
        return First(edit, Written,
          Splice(text, (int)first.Start.Index, (int)first.Start.Index, $"{entry}\n{Indent(first)}"));
      case YamlScalarNode scalar when OneLine(scalar):
        // The shorthand title: Revenue becomes the map it stands for, text first as the docs write it.
        string source = Node.Span(text, scalar);
        return First(edit, candidate => Written(candidate) && TextAt(candidate, $"{Parent(edit.Path)}.text") ==
            scalar.Value,
          Splice(text, scalar, $"{{ text: {source}, {entry} }}"),
          Splice(text, scalar, $"{{ text: {DoubleQuoted(scalar.Value ?? "")}, {entry} }}"));
      case YamlScalarNode:
        throw new PatchException($"{Parent(edit.Path)} is written across lines; give it a {key} in the file");
      default:
        throw Conflict(edit);
    }
  }

  private static double Round(double value)
  {
    double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
    return rounded == 0 ? 0 : rounded;
  }

  private static string Indent(YamlNode node)
  {
    return new string(' ', (int)node.Start.Column - 1);
  }

  #endregion

  #region Scalars

  // The keys a new one goes after, in the order the docs write an element's keys.
  private static readonly string[] KeyOrder = ["id", "shape", "line", "direction", "adjust", "frame", "fill", "stroke", "shadow"];

  /// <summary>Writes one value, such as a color or a width. A key the file lacks is added with any maps it needs,
  /// replacing a <c>none</c> that stood for the map.</summary>
  private static string Scalar(string text, ValueEdit edit)
  {
    YamlNode root = Parse(text);
    string value = edit.Value!;

    bool Written(string candidate)
    {
      return Locate(Parse(candidate), edit.Path).Node is YamlScalarNode { Value: string written } && written == value;
    }

    if (Locate(root, edit.Path).Node is YamlNode node)
    {
      Expect(text, node, edit);
      if (!OneLine(node))
      {
        throw new PatchException($"{edit.Path} is written across lines; change it in the file");
      }
      return First(edit, Written, Splice(text, node, value), Splice(text, node, DoubleQuoted(value)));
    }
    if (edit.Source is not null)
    {
      throw Conflict(edit);
    }
    string holder = Existing(root, edit.Path);
    YamlNode parent = Locate(root, holder).Node!;
    string[] missing = edit.Path[(holder.Length + 1)..].Split('.');

    string Entry(string written)
    {
      string nested = written;
      for (int index = missing.Length - 1; index > 0; index--)
      {
        nested = $"{{ {missing[index]}: {nested} }}";
      }
      return $"{missing[0]}: {nested}";
    }

    string[] Candidates(Func<string, string> place)
    {
      return [place(Entry(value)), place(Entry(DoubleQuoted(value)))];
    }

    switch (parent)
    {
      case YamlScalarNode { Value: "none" } none when OneLine(none):
        return First(edit, Written, Candidates(entry => Splice(text, none, $"{{ {entry} }}")));
      case YamlMappingNode { Style: MappingStyle.Flow } map:
        (int mapStart, int mapEnd) = Node.Extent(text, map);
        int at = mapEnd - 1;
        while (at > 0 && text[at - 1] == ' ')
        {
          at--;
        }
        return First(edit, Written, Candidates(entry => map.Children.Count == 0
          ? Splice(text, mapStart, mapEnd, $"{{ {entry} }}")
          : Splice(text, at, at, $", {entry}")));
      case YamlMappingNode map:
        (YamlNode key, int end) = KeyBefore(text, map, missing[0]);
        return First(edit, Written, Candidates(entry => Splice(text, end, end, $"\n{Indent(key)}{entry}")));
      default:
        throw new PatchException($"{holder} isn't a map; give it a {missing[0]} in the file");
    }
  }

  /// <summary>The key of the one-line entry a new <paramref name="key"/> goes after, and where that entry's line
  /// ends: the last entry the docs write before it, or else the first.</summary>
  private static (YamlNode Key, int End) KeyBefore(string text, YamlMappingNode map, string key)
  {
    int rank = Array.IndexOf(KeyOrder, key);
    KeyValuePair<YamlNode, YamlNode>[] entries = map.Children
      .Where(pair => OneLine(pair.Value) && pair.Key.Start.Line == pair.Value.Start.Line).ToArray();
    KeyValuePair<YamlNode, YamlNode> after = entries.LastOrDefault(pair =>
      pair.Key is YamlScalarNode { Value: string name } && Array.IndexOf(KeyOrder, name) is int at && at >= 0 &&
      (rank < 0 || at < rank));
    if (after.Key is null)
    {
      after = entries.FirstOrDefault();
    }
    if (after.Key is null)
    {
      throw new PatchException($"there's no line to write {key} after; add it in the file");
    }
    int end = Node.Extent(text, after.Value).End;
    while (end < text.Length && text[end] is not ('\n' or '\r'))
    {
      end++;
    }
    return (after.Key, end);
  }

  #endregion

  private static string Restore(string text, ValueEdit edit)
  {
    (YamlNode? node, _, _) = Locate(Parse(text), edit.Path);
    if (node is null)
    {
      throw Conflict(edit);
    }
    Expect(text, node, edit);
    return First(edit, candidate => SourceAt(candidate, edit.Path) == edit.Restore,
      Splice(text, node, edit.Restore!));
  }

  #region Deleting elements

  /// <summary>Removes an element's lines with the blank lines before it, or after it when it comes first, so putting
  /// them back after the element before it leaves the file as it was. The last element leaves an empty list.
  /// </summary>
  private static (string Text, ValueEdit Undo) Delete(string text, ValueEdit edit)
  {
    YamlNode root = Parse(text);
    (YamlSequenceNode list, YamlScalarNode key) = Elements(root, edit);
    (YamlNode? node, _, _) = Locate(root, edit.Path);
    if (node is null)
    {
      throw Conflict(edit);
    }
    Expect(text, node, edit);
    int index = list.Children.IndexOf(node);
    if (index < 0)
    {
      throw new PatchException($"only a whole element can be deleted, not {edit.Path}");
    }
    if (list.Style == SequenceStyle.Flow)
    {
      throw new PatchException("elements is written as a flow list; delete the element in the file");
    }
    List<string?> ids = Ids(root);
    ids.RemoveAt(index);

    bool Deleted(string candidate)
    {
      YamlNode written = Parse(candidate);
      return Locate(written, edit.Path).Node is null && Ids(written).SequenceEqual(ids);
    }

    (int start, int end) = EntryLines(text, node);
    if (list.Children.Count == 1)
    {
      int colon = text.IndexOf(':', (int)key.End.Index) + 1;
      int stop = end;
      while (stop > start && text[stop - 1] is '\n' or '\r')
      {
        stop--;
      }
      string removed = text[colon..stop];
      return (First(edit, Deleted, Splice(text, colon, stop, " []")), new ValueEdit(edit.Path, null, Restore: removed));
    }
    if (index == 0)
    {
      while (end < text.Length && BlankLine(text, end) is int next)
      {
        end = next;
      }
    }
    else
    {
      while (start > 0 && LineStart(text, start - 2) is int previous && BlankLine(text, previous) == start)
      {
        start = previous;
      }
    }
    string? after = index == 0 ? null : Id(list.Children[index - 1]);
    return (First(edit, Deleted, Splice(text, start, end, "")),
      new ValueEdit(edit.Path, null, Restore: text[start..end], After: after));
  }

  /// <summary>Puts back the lines a delete removed: after the element they followed, before the first, or in place
  /// of the empty list the last one left.</summary>
  private static string Insert(string text, ValueEdit edit)
  {
    YamlNode root = Parse(text);
    (YamlSequenceNode list, YamlScalarNode key) = Elements(root, edit);
    if (Locate(root, edit.Path).Node is not null)
    {
      throw Conflict(edit);
    }
    List<string?> ids = Ids(root);
    int index = edit.After is null ? 0 : ids.IndexOf(edit.After) + 1;
    if (edit.After is not null && index == 0)
    {
      throw Conflict(edit);
    }
    ids.Insert(index, edit.Path["elements.".Length..]);

    bool Inserted(string candidate)
    {
      YamlNode written = Parse(candidate);
      return Locate(written, edit.Path).Node is YamlMappingNode && Ids(written).SequenceEqual(ids);
    }

    if (list.Children.Count == 0)
    {
      int colon = text.IndexOf(':', (int)key.End.Index) + 1;
      return First(edit, Inserted, Splice(text, colon, Node.Extent(text, list).End, edit.Restore!));
    }
    int at = index == 0
      ? EntryLines(text, list.Children[0]).Start
      : EntryLines(text, list.Children[index - 1]).End;
    return First(edit, Inserted, Splice(text, at, at, edit.Restore!));
  }

  private static (YamlSequenceNode List, YamlScalarNode Key) Elements(YamlNode root, ValueEdit edit)
  {
    if (root is YamlMappingNode map && map.Children.FirstOrDefault(pair =>
      pair.Key is YamlScalarNode { Value: "elements" }) is { Key: YamlScalarNode key, Value: YamlSequenceNode list })
    {
      return (list, key);
    }
    throw Conflict(edit);
  }

  private static List<string?> Ids(YamlNode root)
  {
    return root is YamlMappingNode map && Value(map, "elements") is YamlSequenceNode list
      ? list.Children.Select(Id).ToList()
      : [];
  }

  private static string? Id(YamlNode entry)
  {
    return entry is YamlMappingNode map && Value(map, "id") is YamlScalarNode { Value: string id } ? id : null;
  }

  /// <summary>From the start of the line holding an entry's dash to past the end of its last line.</summary>
  private static (int Start, int End) EntryLines(string text, YamlNode entry)
  {
    int dash = (int)entry.Start.Index - 1;
    while (dash >= 0 && char.IsWhiteSpace(text[dash]))
    {
      dash--;
    }
    int start = LineStart(text, dash);
    if (dash < 0 || text[dash] != '-' || text[start..dash].Trim().Length > 0)
    {
      throw new PatchException("the entry doesn't start its own line; change it in the file");
    }
    // A block scalar ends where the next line's indentation does.
    int end = Node.Extent(text, entry).End;
    while (end > start && text[end - 1] is ' ' or '\t')
    {
      end--;
    }
    if (end == 0 || text[end - 1] != '\n')
    {
      int newline = text.IndexOf('\n', end);
      end = newline < 0 ? text.Length : newline + 1;
    }
    return (start, end);
  }

  private static int LineStart(string text, int index)
  {
    return index < 0 ? 0 : text.LastIndexOf('\n', index) + 1;
  }

  /// <summary>Past the line starting at <paramref name="start"/> when it holds only whitespace and a line break.
  /// </summary>
  private static int? BlankLine(string text, int start)
  {
    int newline = text.IndexOf('\n', start);
    return newline >= 0 && text[start..newline].Trim().Length == 0 ? newline + 1 : null;
  }

  #endregion

  #region Reordering slides

  /// <summary>Moves a slide's line, with any comment on it, to after the entry it is dropped after, refusing when the
  /// slide no longer follows the entry the page drew it after. Moving it back is the undo, and a drop where the
  /// slide already is writes nothing and has none.</summary>
  public static (string Text, SlideMove? Undo) Move(string text, SlideMove move)
  {
    if (move.Delete)
    {
      return Remove(text, move);
    }
    if (move.Restore is not null)
    {
      return Insert(text, move);
    }
    YamlSequenceNode list = SlideList(Parse(text), move);
    List<ListEntry?> entries = list.Children.Select(Entry).ToList();
    var slide = new ListEntry(move.Slide);
    int index = entries.IndexOf(slide);
    if (index < 0 || (index == 0 ? null : entries[index - 1]) != move.From)
    {
      throw MoveConflict(move);
    }
    if (move.To == move.From || move.To == slide)
    {
      return (text, null);
    }
    if (list.Style == SequenceStyle.Flow)
    {
      throw new PatchException("slides is written as a flow list; reorder it in the file");
    }
    int anchor = -1;
    if (move.To is not null)
    {
      anchor = entries.IndexOf(move.To);
      if (anchor < 0)
      {
        throw MoveConflict(move);
      }
      if (entries.LastIndexOf(move.To) != anchor)
      {
        throw new PatchException($"two sections are named {move.To.Section}; move {move.Slide} in the file");
      }
    }
    List<ListEntry?> order = [.. entries];
    order.RemoveAt(index);
    order.Insert(anchor < index ? anchor + 1 : anchor, slide);

    // The last line may end the file without a break, and a line moved from there needs one.
    string newline = text.Contains("\r\n") ? "\r\n" : "\n";
    bool unbroken = !text.EndsWith('\n');
    string broken = unbroken ? text + newline : text;
    (int start, int end) = EntryLines(broken, list.Children[index]);
    int at = anchor < 0 ? EntryLines(broken, list.Children[0]).Start : EntryLines(broken, list.Children[anchor]).End;
    string line = broken[start..end];
    string moved = at < start
      ? string.Concat(broken[..at], line, broken[at..start], broken[end..])
      : string.Concat(broken[..start], broken[end..at], line, broken[at..]);
    if (unbroken)
    {
      moved = moved[..^newline.Length];
    }
    string written = First(new ValueEdit($"slides.{move.Slide}", null), candidate =>
      SlideList(Parse(candidate), move).Children.Select(Entry).SequenceEqual(order), moved);
    return (written, new SlideMove(move.Slide, move.To, move.From));
  }

  /// <summary>Removes a slide's line, refusing the last slide since a presentation needs one. The undo restores it
  /// after the same entry, with an empty <see cref="SlideMove.Restore"/> for the caller to fill with the file.
  /// </summary>
  private static (string Text, SlideMove? Undo) Remove(string text, SlideMove move)
  {
    YamlSequenceNode list = SlideList(Parse(text), move);
    List<ListEntry?> entries = list.Children.Select(Entry).ToList();
    var slide = new ListEntry(move.Slide);
    int index = entries.IndexOf(slide);
    if (index < 0 || (index == 0 ? null : entries[index - 1]) != move.From)
    {
      throw MoveConflict(move);
    }
    if (entries.Count(entry => entry?.Slide is not null) == 1)
    {
      throw new PatchException($"{move.Slide} is the only slide, and a presentation needs one");
    }
    if (list.Style == SequenceStyle.Flow)
    {
      throw new PatchException("slides is written as a flow list; delete the slide in the file");
    }
    (int start, int end) = EntryLines(text, list.Children[index]);
    string removed = string.Concat(text[..start], text[end..]);
    // A last line without a break leaves the break before it dangling.
    if (end == text.Length && !text.EndsWith('\n'))
    {
      removed = removed.TrimEnd('\r', '\n');
    }
    return (removed, new SlideMove(move.Slide, null, move.From, Restore: ""));
  }

  /// <summary>Writes a slide's line after the entry it was deleted after, indented as the list's first entry is.
  /// </summary>
  private static (string Text, SlideMove? Undo) Insert(string text, SlideMove move)
  {
    YamlSequenceNode list = SlideList(Parse(text), move);
    List<ListEntry?> entries = list.Children.Select(Entry).ToList();
    if (entries.Contains(new ListEntry(move.Slide)))
    {
      throw MoveConflict(move);
    }
    if (list.Style == SequenceStyle.Flow || list.Children.Count == 0)
    {
      throw new PatchException($"slides is written as a flow list; add {move.Slide} back in the file");
    }
    int anchor = -1;
    if (move.To is not null)
    {
      anchor = entries.IndexOf(move.To);
      if (anchor < 0)
      {
        throw MoveConflict(move);
      }
    }
    string newline = text.Contains("\r\n") ? "\r\n" : "\n";
    (int first, _) = EntryLines(text, list.Children[0]);
    string indent = text[first..text.IndexOf('-', first)];
    string line = $"{indent}- {YamlWriter.Text(move.Slide)}";
    string written;
    if (anchor < 0)
    {
      written = string.Concat(text[..first], line, newline, text[first..]);
    }
    else
    {
      (_, int end) = EntryLines(text, list.Children[anchor]);
      written = end == text.Length && !text.EndsWith('\n')
        ? string.Concat(text, newline, line)
        : string.Concat(text[..end], line, newline, text[end..]);
    }
    return (written, new SlideMove(move.Slide, move.To, null, Delete: true));
  }

  private static YamlSequenceNode SlideList(YamlNode root, SlideMove move)
  {
    return root is YamlMappingNode map && Value(map, "slides") is YamlSequenceNode list
      ? list
      : throw MoveConflict(move);
  }

  private static ListEntry? Entry(YamlNode entry)
  {
    return entry switch
    {
      YamlScalarNode { Value: string stem } => new ListEntry(stem),
      YamlMappingNode map when Value(map, "section") is YamlScalarNode { Value: string name } =>
        new ListEntry(Section: name),
      _ => null
    };
  }

  private static PatchException MoveConflict(SlideMove move)
  {
    return new PatchException($"slides changed in the file since the sidebar was drawn, so moving {move.Slide} was " +
      "dropped", true);
  }

  #endregion

  #region Text

  private static string Text(string text, ValueEdit edit, MarkupContext context)
  {
    (YamlNode? node, YamlNode? parent, _) = Locate(Parse(text), edit.Path);
    if (node is null)
    {
      throw Conflict(edit);
    }
    Expect(text, node, edit);
    if (node is not YamlScalarNode scalar)
    {
      throw new PatchException($"{edit.Path} isn't text");
    }
    // The title and placeholders take plain text, and everything else markup.
    bool plain = edit.Path == "title" || edit.Path.StartsWith("title.", StringComparison.Ordinal) ||
      edit.Path.StartsWith("placeholders.", StringComparison.Ordinal);
    string value = scalar.Value ?? "";
    string updated = Reword(value, edit.Paragraphs!, plain, context);
    if (edit.Format is TextFormat format)
    {
      updated = plain
        ? throw new PatchException("the title and placeholders take plain text, and the layout decides their look")
        : Format(updated, format, context);
    }
    if (updated == value)
    {
      return text;
    }

    bool Written(string candidate)
    {
      return TextAt(candidate, edit.Path) == updated;
    }

    switch (scalar.Style)
    {
      case ScalarStyle.Literal:
        return First(edit, Written, Lines(text, scalar, value, updated));
      case ScalarStyle.Folded:
      case not ScalarStyle.Literal when !OneLine(scalar):
        throw new PatchException($"{edit.Path} is written across lines; write it as a block (|) to edit it here");
      // Markup of more than one line reads best as a block, as an agent writes it.
      case not ScalarStyle.Literal when !plain && updated.Contains('\n'):
        return First(edit, Written, Block(text, scalar, parent, updated), Splice(text, scalar, DoubleQuoted(updated)));
      case ScalarStyle.SingleQuoted:
        return First(edit, Written, Splice(text, scalar, $"'{updated.Replace("'", "''")}'"),
          Splice(text, scalar, DoubleQuoted(updated)));
      case ScalarStyle.DoubleQuoted:
        return First(edit, Written, Splice(text, scalar, DoubleQuoted(updated)));
      default:
        return First(edit, Written, Splice(text, scalar, updated), Splice(text, scalar, DoubleQuoted(updated)));
    }
  }

  /// <summary>The value with each changed paragraph's changed stretch replaced in its line, so formatting and
  /// fields outside the stretch stay as written.</summary>
  private static string Reword(string value, List<string> paragraphs, bool plain, MarkupContext context)
  {
    bool trailing = !plain && value.EndsWith('\n');
    string body = trailing ? value[..^1] : value;
    string[] lines = plain ? [body] : body.Split('\n');
    List<Paragraph> parsed = Parse(body, plain, context);
    if (parsed.Count != paragraphs.Count)
    {
      return Regroup(body, parsed, paragraphs, context) + (trailing ? "\n" : "");
    }
    for (int index = 0; index < parsed.Count; index++)
    {
      Paragraph paragraph = parsed[index];
      List<(char Character, int Start, int End, string? Field)> old = Characters(paragraph);
      string before = new string(old.Select(character => character.Character).ToArray());
      string after = paragraphs[index];
      if (before == after)
      {
        continue;
      }
      string line = lines[paragraph.Line];
      // From the end back, so each stretch's offsets still hold when it is replaced.
      foreach ((int start, int end, string typed) in Hunks(before, after).AsEnumerable().Reverse())
      {
        List<(char Character, int Start, int End, string? Field)> removed = old[start..end];
        if (removed.FirstOrDefault(character => character.Field is not null).Field is string field)
        {
          throw new PatchException($"${{{field}}} is a field and stays as written; change it in the file");
        }
        if (typed.Contains(FieldMark))
        {
          throw new PatchException("a field can't be typed on the slide; write it in the file");
        }
        int from;
        int to;
        if (removed.Count > 0)
        {
          from = removed[0].Start;
          to = removed[^1].End;
          if (removed.Sum(character => character.End - character.Start) != to - from)
          {
            throw new PatchException("that edit crosses formatting, such as bold or a span; make it in the file");
          }
        }
        else
        {
          from = to = start > 0 ? old[start - 1].End : old.Count > 0 ? old[0].Start : paragraph.Start;
        }
        line = line[..from] + Escape(typed, plain) + line[to..];
      }
      lines[paragraph.Line] = plain || paragraph.Level > 0 ? line : Markup.EscapeLineStart(line);
    }
    string updated = string.Join('\n', lines) + (trailing ? "\n" : "");

    List<Paragraph> reread = Parse(plain ? updated : updated.TrimEnd('\n'), plain, context);
    bool same = reread.Count == parsed.Count && reread.Select((paragraph, index) =>
      Plain(paragraph) == paragraphs[index] && paragraph.Level == parsed[index].Level &&
      paragraph.Style == parsed[index].Style).All(match => match);
    return same ? updated : throw new PatchException("that text would read as markup; write it in the file");
  }

  /// <summary>The value with paragraphs split or joined: the paragraphs read as one text with a newline between
  /// each, and each changed stretch replaced in the value, so that a newline typed into a paragraph starts another at
  /// its level and in its style.</summary>
  private static string Regroup(string body, List<Paragraph> parsed, List<string> paragraphs, MarkupContext context)
  {
    string[] lines = body.Split('\n');
    var starts = new int[lines.Length];
    for (int index = 1; index < lines.Length; index++)
    {
      starts[index] = starts[index - 1] + lines[index - 1].Length + 1;
    }
    int End(Paragraph paragraph) => starts[paragraph.Line] + lines[paragraph.Line].Length;
    int Content(Paragraph paragraph) => starts[paragraph.Line] + paragraph.Start;

    // A paragraph break spans from the end of one paragraph's line to the start of the next one's text, taking in
    // its style line and list marker.
    var old = new List<(char Character, int Start, int End, string? Field)>();
    for (int index = 0; index < parsed.Count; index++)
    {
      Paragraph paragraph = parsed[index];
      if (index > 0)
      {
        old.Add(('\n', End(parsed[index - 1]), Content(paragraph), null));
      }
      int line = starts[paragraph.Line];
      old.AddRange(Characters(paragraph).Select(character =>
        (character.Character, line + character.Start, line + character.End, character.Field)));
    }
    string before = new string(old.Select(character => character.Character).ToArray());
    var text = new StringBuilder(body);
    foreach ((int start, int end, string typed) in Hunks(before, string.Join('\n', paragraphs)).AsEnumerable()
      .Reverse())
    {
      List<(char Character, int Start, int End, string? Field)> removed = old[start..end];
      if (removed.FirstOrDefault(character => character.Field is not null).Field is string field)
      {
        throw new PatchException($"${{{field}}} is a field and stays as written; change it in the file");
      }
      if (typed.Contains(FieldMark))
      {
        throw new PatchException("a field can't be typed on the slide; write it in the file");
      }
      int from;
      int to;
      if (removed.Count > 0)
      {
        from = removed[0].Start;
        to = removed[^1].End;
        if (removed.Sum(character => character.End - character.Start) != to - from)
        {
          throw new PatchException("that edit crosses formatting, such as bold or a span; make it in the file");
        }
      }
      else if (start > 0 && typed.StartsWith('\n') && (start == old.Count || old[start].Character == '\n'))
      {
        // A break typed at a paragraph's end goes after whatever closes its last run.
        from = to = old[start - 1].Character == '\n'
          ? old[start - 1].End
          : End(parsed.Last(paragraph => Content(paragraph) <= old[start - 1].Start));
      }
      else
      {
        from = to = start > 0 ? old[start - 1].End : old.Count > 0 ? old[0].Start : 0;
      }
      Paragraph home = parsed.LastOrDefault(paragraph => Content(paragraph) <= from) ?? parsed[0];
      string opening = (home.Style is null ? "" : $"{{.{home.Style}}}\n") + lines[home.Line][..home.Start];
      int origin = starts[home.Line];
      int next = (removed.Count == 0 && start < old.Count && old[start].Character != '\n' ? old[start].Start : to) -
        origin;
      List<Wrapper> around = typed.Contains('\n') && to <= End(home)
        ? Around(lines[home.Line], home.Start, from - origin)
        : [];
      // A break typed where markup ends goes after its closer, and one inside markup closes it on the first line and
      // opens it again on the next.
      List<Wrapper> ending = around.Where(markup => markup.ContentEnd < next).ToList();
      if (removed.Count == 0 && ending.Count > 0)
      {
        from = to = origin + ending.Max(markup => markup.ContentEnd + markup.Close.Length);
      }
      List<Wrapper> inside = around.Where(markup => markup.ContentEnd >= next).ToList();
      string closing = string.Concat(inside.Select(markup => markup.Close).Reverse());
      string reopening = string.Concat(inside.Select(markup => markup.Open));
      text.Remove(from, to - from);
      text.Insert(from, Escape(typed, false).Replace("\n", closing + "\n" + opening + reopening));
    }
    string updated = text.ToString();

    List<Paragraph> reread = Parse(updated, false, context);
    bool same = reread.Count == paragraphs.Count &&
      reread.Select((paragraph, index) => Plain(paragraph) == paragraphs[index]).All(match => match);
    return same ? updated : throw new PatchException("that text would read as markup; write it in the file");
  }

  /// <summary>Bold, italic, a link or a span: where its content starts and ends on its line, and the markup on either
  /// side.</summary>
  private record Wrapper(int ContentStart, int ContentEnd, string Open, string Close);

  /// <summary>The markup whose content takes in <paramref name="position"/> on a line, its ends included, outermost
  /// first, read as <see cref="Markup"/> reads it.</summary>
  private static List<Wrapper> Around(string line, int start, int position)
  {
    var wrapping = new List<Wrapper>();
    var open = new Stack<(int ContentStart, string Marker, int Close, string Closer)>();
    int at = start;
    while (at < line.Length)
    {
      char current = line[at];
      if (open.FirstOrDefault(markup => markup.Marker == "[") is { Marker: "[" } span && at == span.Close)
      {
        // Emphasis left unclosed inside a span is literal text, since the span's content reads on its own.
        while (open.Peek().Marker != "[")
        {
          open.Pop();
        }
        open.Pop();
        if (span.ContentStart <= position && position <= span.Close)
        {
          wrapping.Add(new Wrapper(span.ContentStart, span.Close, "[", span.Closer));
        }
        at = span.Close + span.Closer.Length;
        continue;
      }
      if (current == '\\')
      {
        at += 2;
        continue;
      }
      if (current == '$' && at + 1 < line.Length && line[at + 1] == '{')
      {
        int end = line.IndexOf('}', at);
        at = end < 0 ? line.Length : end + 1;
        continue;
      }
      if (current == '*')
      {
        string rest = line[at..];
        bool closesItalic = open.Count > 0 && open.Peek().Marker == "*" &&
          (!rest.StartsWith("**", StringComparison.Ordinal) || rest.StartsWith("***", StringComparison.Ordinal));
        string delimiter = !closesItalic && rest.StartsWith("**", StringComparison.Ordinal) ? "**" : "*";
        if (open.Count > 0 && open.Peek().Marker == delimiter)
        {
          int contentStart = open.Pop().ContentStart;
          if (contentStart <= position && position <= at)
          {
            wrapping.Add(new Wrapper(contentStart, at, delimiter, delimiter));
          }
        }
        else if (at + delimiter.Length < line.Length && line[at + delimiter.Length] != ' ')
        {
          open.Push((at + delimiter.Length, delimiter, -1, ""));
        }
        at += delimiter.Length;
        continue;
      }
      if (current == '[' && Bracketed(line, at) is (int close, string closer))
      {
        open.Push((at + 1, "[", close, closer));
      }
      at++;
    }
    return wrapping.OrderBy(markup => markup.ContentStart).ToList();
  }

  /// <summary>Where a link or span opened at <paramref name="open"/> closes, and the markup that closes it, such as
  /// <c>]{.positive}</c>.</summary>
  private static (int Close, string Closer)? Bracketed(string line, int open)
  {
    int depth = 0;
    for (int position = open; position < line.Length; position++)
    {
      if (line[position] == '\\')
      {
        position++;
      }
      else if (line[position] == '[')
      {
        depth++;
      }
      else if (line[position] == ']' && --depth == 0)
      {
        if (position + 1 >= line.Length || line[position + 1] is not ('(' or '{'))
        {
          return null;
        }
        int end = line.IndexOf(line[position + 1] == '(' ? ')' : '}', position + 2);
        return end < 0 ? null : (position, line[position..(end + 1)]);
      }
    }
    return null;
  }

  private record CharacterFormat(char Character, bool Bold, bool Italic, bool? Underline, string? Link, string Styles,
    string? Font, double? Size, string? Color);

  private static List<CharacterFormat> Formats(Paragraph paragraph)
  {
    return paragraph.Runs.SelectMany(run => (run.Field is null ? run.Text : FieldMark.ToString()).Select(character =>
      new CharacterFormat(character, run.Bold == true, run.Italic == true, run.Properties?.Underline, run.Link,
        string.Join(' ', run.Styles ?? []), run.Properties?.Font, run.Properties?.Size,
        run.Properties?.Color?.ToString()))).ToList();
  }

  private static CharacterFormat Toggled(CharacterFormat format, string mark, bool on)
  {
    return mark switch
    {
      "bold" => format with { Bold = on },
      "italic" => format with { Italic = on },
      "underline" => format with { Underline = on ? true : format.Underline == true ? null : false },
      _ => throw new PatchException($"there is no {mark} formatting; choose bold, italic, or underline")
    };
  }

  /// <summary>Turns a mark on or off over each range by trying the markup that could write it, keeping the first
  /// whose text reads back with that mark changed there and nothing else changed anywhere.</summary>
  private static string Format(string value, TextFormat format, MarkupContext context)
  {
    bool trailing = value.EndsWith('\n');
    string body = trailing ? value[..^1] : value;
    foreach (FormatRange range in format.Ranges.OrderByDescending(range => (range.Paragraph, range.Start)))
    {
      List<Paragraph> parsed = Parse(body, false, context);
      if (range.Paragraph < 0 || range.Paragraph >= parsed.Count)
      {
        throw new PatchException("the text changed in the file since it was selected");
      }
      Paragraph paragraph = parsed[range.Paragraph];
      List<(char Character, int Start, int End, string? Field)> characters = Characters(paragraph);
      int start = Math.Clamp(range.Start, 0, characters.Count);
      int end = Math.Clamp(range.End, start, characters.Count);
      // Markdown's markers can't sit against a space inside them.
      while (start < end && characters[start].Character == ' ')
      {
        start++;
      }
      while (end > start && characters[end - 1].Character == ' ')
      {
        end--;
      }
      if (start == end)
      {
        continue;
      }
      List<CharacterFormat> before = Formats(paragraph);
      List<CharacterFormat> wanted = before.Select((character, index) =>
        index >= start && index < end ? Toggled(character, format.Mark, format.On) : character).ToList();
      bool marked = before[start..end].All(character => Toggled(character, format.Mark, true) == character);
      if (!format.On && !marked && format.Mark != "underline")
      {
        throw new PatchException($"that text isn't {format.Mark} in its markup, so its style makes it so; change " +
          "the style in the file");
      }
      if (wanted.SequenceEqual(before))
      {
        continue;
      }
      string[] lines = body.Split('\n');
      string line = lines[paragraph.Line];
      int from = characters[start].Start;
      int to = characters[end - 1].End;
      IEnumerable<string> candidates = format.On || !marked
        ? Wrapped(line, from, to, format)
        : Unwrapped(line, from, to, format.Mark, characters.Select(character => character.Start).ToList());
      string? written = candidates.Select(candidate =>
      {
        lines[paragraph.Line] = candidate;
        return string.Join('\n', lines);
      }).FirstOrDefault(candidate => Reads(candidate, parsed, range.Paragraph, wanted, context));
      body = written ?? throw new PatchException($"that {format.Mark} crosses formatting, such as a span or a link; " +
        "make it in the file");
    }
    return body + (trailing ? "\n" : "");
  }

  private static bool Reads(string body, List<Paragraph> before, int index, List<CharacterFormat> wanted,
    MarkupContext context)
  {
    List<Paragraph> after;
    try
    {
      after = Parse(body, false, context);
    }
    catch (PatchException)
    {
      return false;
    }
    return after.Count == before.Count && after.Select((paragraph, at) =>
      paragraph.Level == before[at].Level && paragraph.Style == before[at].Style &&
      Formats(paragraph).Select(Seen).SequenceEqual((at == index ? wanted : Formats(before[at])).Select(Seen)))
      .All(same => same);
  }

  /// <summary>Bold and italic don't show on a space, and Markdown's markers leave spaces outside them.</summary>
  private static CharacterFormat Seen(CharacterFormat format)
  {
    return format.Character == ' ' ? format with { Bold = false, Italic = false } : format;
  }

  private static (string Open, string Close) Markers(string mark, bool on)
  {
    return mark switch
    {
      "bold" => ("**", "**"),
      "italic" => ("*", "*"),
      _ => ("[", on ? "]{underline=true}" : "]{underline=false}")
    };
  }

  private static IEnumerable<string> Wrapped(string line, int from, int to, TextFormat format)
  {
    (string open, string close) = Markers(format.Mark, format.On);
    yield return string.Concat(line[..from], open, line[from..to], close, line[to..]);
  }

  /// <summary>The line with the markers around the selection removed, the nearest pairs first, and the text they
  /// also held either side marked again on its own; markup with no text between a marker and the selection, such as
  /// a span's bracket, stays with the selection.</summary>
  private static IEnumerable<string> Unwrapped(string line, int from, int to, string mark, List<int> texts)
  {
    (string open, string close) = Markers(mark, true);
    List<int> opens = Occurrences(line, open).Where(at => at + open.Length <= from).Reverse().Take(4).ToList();
    List<int> closes = Occurrences(line, close).Where(at => at >= to).Take(4).ToList();
    foreach (int opened in opens)
    {
      foreach (int closed in closes)
      {
        int inner = opened + open.Length;
        int start = texts.Any(at => at >= inner && at < from) ? from : inner;
        int end = texts.Any(at => at >= to && at < closed) ? to : closed;
        string left = Remark(line[inner..start], open, close, mark != "underline");
        string right = Remark(line[end..closed], open, close, mark != "underline");
        yield return string.Concat(line[..opened], left, line[start..end], right, line[(closed + close.Length)..]);
      }
    }
  }

  private static IEnumerable<int> Occurrences(string line, string token)
  {
    for (int at = line.IndexOf(token, StringComparison.Ordinal); at >= 0;
      at = line.IndexOf(token, at + 1, StringComparison.Ordinal))
    {
      yield return at;
    }
  }

  /// <summary>Markup between markers, marked on its own, with any spaces at its ends left outside for Markdown's.
  /// </summary>
  private static string Remark(string text, string open, string close, bool outside)
  {
    if (!outside)
    {
      return text.Length == 0 ? text : string.Concat(open, text, close);
    }
    string core = text.Trim(' ');
    if (core.Length == 0)
    {
      return text;
    }
    int lead = text.Length - text.TrimStart(' ').Length;
    int tail = text.Length - text.TrimEnd(' ').Length;
    return string.Concat(text[..lead], open, core, close, text[^tail..]);
  }

  /// <summary>The stretches of <paramref name="before"/> an edit replaced, as its longest common subsequence with
  /// <paramref name="after"/> leaves them, so two edits either side of formatting stay two.</summary>
  private static List<(int Start, int End, string Typed)> Hunks(string before, string after)
  {
    int rows = before.Length;
    int columns = after.Length;
    var common = new int[rows + 1, columns + 1];
    for (int row = rows - 1; row >= 0; row--)
    {
      for (int column = columns - 1; column >= 0; column--)
      {
        common[row, column] = before[row] == after[column]
          ? common[row + 1, column + 1] + 1
          : Math.Max(common[row + 1, column], common[row, column + 1]);
      }
    }
    var hunks = new List<(int Start, int End, string Typed)>();
    int start = -1;
    var typed = new StringBuilder();
    int i = 0;
    int j = 0;
    void Flush()
    {
      if (start >= 0)
      {
        hunks.Add((start, i, typed.ToString()));
        start = -1;
        typed.Clear();
      }
    }
    while (i < rows || j < columns)
    {
      if (i < rows && j < columns && before[i] == after[j])
      {
        Flush();
        i++;
        j++;
        continue;
      }
      if (start < 0)
      {
        start = i;
      }
      if (j >= columns || (i < rows && common[i + 1, j] >= common[i, j + 1]))
      {
        i++;
      }
      else
      {
        typed.Append(after[j]);
        j++;
      }
    }
    Flush();
    return hunks;
  }

  private static List<Paragraph> Parse(string value, bool plain, MarkupContext context)
  {
    var origin = new Origin("", 0, 0);
    try
    {
      return plain
        ? [new Paragraph { Runs = Markup.ParsePlain(value, origin, context.FieldProblem) }]
        : Markup.Parse(value, _ => origin, context);
    }
    catch (LoadException exception)
    {
      throw new PatchException($"the text doesn't read: {exception.Problem}");
    }
  }

  private static List<(char Character, int Start, int End, string? Field)> Characters(Paragraph paragraph)
  {
    var characters = new List<(char Character, int Start, int End, string? Field)>();
    foreach (Run run in paragraph.Runs)
    {
      if (run.Field is not null)
      {
        characters.Add((FieldMark, run.Sources[0].Start, run.Sources[0].End, run.Field));
        continue;
      }
      characters.AddRange(run.Text.Select((character, index) =>
        (character, run.Sources[index].Start, run.Sources[index].End, (string?)null)));
    }
    return characters;
  }

  private static string Plain(Paragraph paragraph)
  {
    return string.Concat(paragraph.Runs.Select(run => run.Field is null ? run.Text : FieldMark.ToString()));
  }

  /// <summary>Typed text as markup that reads back as the same text.</summary>
  private static string Escape(string text, bool plain)
  {
    var escaped = new StringBuilder();
    for (int index = 0; index < text.Length; index++)
    {
      char character = text[index];
      bool field = character == '$' && index + 1 < text.Length && text[index + 1] == '{';
      if (field || (!plain && character is '\\' or '*' or '[' or ']'))
      {
        escaped.Append('\\');
      }
      escaped.Append(character);
    }
    return escaped.ToString();
  }

  /// <summary>A literal block with each changed line of its value replaced in place, at the block's indentation.
  /// </summary>
  private static string Lines(string text, YamlScalarNode scalar, string value, string updated)
  {
    string[] before = value.Split('\n');
    string[] after = updated.Split('\n');
    List<(int Start, int End)> lines = LineSpans(text);
    // The value's first line sits on the line after the block's indicator.
    int first = (int)scalar.Start.Line;
    int sample = Array.FindIndex(before, line => line.Trim().Length > 0);
    string sampleLine = text[lines[first + sample].Start..lines[first + sample].End];
    int indent = Leading(sampleLine) - Leading(before[sample]);
    var patched = new StringBuilder(text);
    if (before.Length != after.Length)
    {
      // A kept final newline splits off an empty last entry, which is the line after the block.
      int kept = value.EndsWith('\n') ? 1 : 0;
      int start = lines[first].Start;
      int end = lines[first + before.Length - 1 - kept].End;
      patched.Remove(start, end - start);
      patched.Insert(start, Indented(after[..^kept], indent));
      return patched.ToString();
    }
    for (int index = before.Length - 1; index >= 0; index--)
    {
      if (before[index] == after[index])
      {
        continue;
      }
      (int start, int end) = lines[first + index];
      patched.Remove(start, end - start);
      patched.Insert(start, after[index].Length == 0 ? "" : new string(' ', indent) + after[index]);
    }
    return patched.ToString();
  }

  private static string Indented(IEnumerable<string> lines, int indent)
  {
    return string.Join('\n', lines.Select(line => line.Length == 0 ? "" : new string(' ', indent) + line));
  }

  /// <summary>A value written as a literal block in place of <paramref name="scalar"/>, indented under its key.
  /// </summary>
  private static string Block(string text, YamlScalarNode scalar, YamlNode? parent, string updated)
  {
    YamlNode? key = (parent as YamlMappingNode)?.Children.FirstOrDefault(pair => pair.Value == scalar).Key;
    if (key is null)
    {
      return text;
    }
    bool kept = updated.EndsWith('\n');
    string[] lines = (kept ? updated[..^1] : updated).Split('\n');
    int indent = (int)key.Start.Column - 1 + 2;
    return Splice(text, scalar, $"{(kept ? "|" : "|-")}\n{Indented(lines, indent)}");
  }

  private static List<(int Start, int End)> LineSpans(string text)
  {
    var lines = new List<(int Start, int End)>();
    int start = 0;
    while (true)
    {
      int newline = text.IndexOf('\n', start);
      int end = newline < 0 ? text.Length : newline;
      lines.Add((start, end > start && text[end - 1] == '\r' ? end - 1 : end));
      if (newline < 0)
      {
        return lines;
      }
      start = newline + 1;
    }
  }

  private static int Leading(string line)
  {
    return line.Length - line.TrimStart(' ').Length;
  }

  private static string DoubleQuoted(string value)
  {
    var quoted = new StringBuilder("\"");
    foreach (char character in value)
    {
      quoted.Append(character switch
      {
        '"' => "\\\"",
        '\\' => "\\\\",
        '\n' => "\\n",
        '\t' => "\\t",
        _ => character.ToString()
      });
    }
    return quoted.Append('"').ToString();
  }

  private static string? TextAt(string text, string path)
  {
    return Locate(Parse(text), path).Node is YamlScalarNode scalar ? scalar.Value : null;
  }

  #endregion

  #region Finding values

  private static YamlNode Parse(string text)
  {
    var stream = new YamlStream();
    try
    {
      stream.Load(new StringReader(text));
    }
    catch (YamlException)
    {
      throw new PatchException("the file doesn't read as YAML just now", true);
    }
    return stream.Documents.Count == 0 ? new YamlMappingNode() : stream.Documents[0].RootNode;
  }

  /// <summary>The node at a key path such as <c>elements.chart.frame</c>, where a list entry is named by its id, with
  /// the node that holds or would hold it.</summary>
  private static (YamlNode? Node, YamlNode? Parent, string Key) Locate(YamlNode root, string path)
  {
    string[] parts = path.Split('.');
    YamlNode? current = root;
    YamlNode? parent = null;
    string key = "";
    for (int index = 0; index < parts.Length; index++)
    {
      if (current is null)
      {
        return (null, null, parts[^1]);
      }
      parent = current;
      if (current is YamlSequenceNode sequence)
      {
        // Ids may hold dots, so the id is as many parts as name an entry.
        current = null;
        for (int last = index; last < parts.Length && current is null; last++)
        {
          key = string.Join('.', parts[index..(last + 1)]);
          current = sequence.Children.FirstOrDefault(entry => entry is YamlMappingNode map &&
            Value(map, "id") is YamlScalarNode { Value: string id } && id == key);
          if (current is not null)
          {
            index = last;
          }
        }
        continue;
      }
      key = parts[index];
      current = current is YamlMappingNode mapping ? Value(mapping, key) : null;
    }
    return (current, parent, key);
  }

  private static YamlNode? Value(YamlMappingNode map, string key)
  {
    return map.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: string name } && name == key)
      .Value;
  }

  private static string Parent(string path)
  {
    return path[..path.LastIndexOf('.')];
  }

  /// <summary>The path itself if the file has it, or its nearest ancestor the file has.</summary>
  private static string Existing(YamlNode root, string path)
  {
    while (Locate(root, path).Node is null && path.Contains('.'))
    {
      path = Parent(path);
    }
    return path;
  }

  private static void Expect(string text, YamlNode node, ValueEdit edit)
  {
    if (edit.Source is null || Node.Span(text, node) != edit.Source)
    {
      throw Conflict(edit);
    }
  }

  private static PatchException Conflict(ValueEdit edit)
  {
    string changed = edit.Path.Length == 0 ? "the slide" : edit.Path;
    return new PatchException($"{changed} changed in the file since the slide was drawn, so the edit was dropped",
      true);
  }

  private static bool OneLine(YamlNode node)
  {
    return node.Start.Line == node.End.Line;
  }

  /// <summary>An empty value sits right after its key's colon, so a value written there needs the space YAML
  /// wants after it, and clearing one takes that space too rather than leaving it trailing.</summary>
  private static string Splice(string text, YamlNode node, string replacement)
  {
    (int start, int end) = Node.Extent(text, node);
    if (node is YamlScalarNode && start == end && start > 0 && text[start - 1] == ':')
    {
      while (end < text.Length && text[end] is ' ' or '\t')
      {
        end++;
      }
      return Splice(text, start, end, replacement.Length == 0 ? "" : $" {replacement}");
    }
    if (node is YamlScalarNode && replacement.Length == 0 && start >= 2 && text[start - 1] == ' ' &&
      text[start - 2] == ':' && (end == text.Length || text[end] is '\n' or '\r'))
    {
      start--;
    }
    return Splice(text, start, end, replacement);
  }

  private static string Splice(string text, int start, int end, string replacement)
  {
    return string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
  }

  /// <summary>The first candidate that reads back as the edit meant; YAML's rules on what needs quotes are easier
  /// to check than to restate.</summary>
  private static string First(ValueEdit edit, Func<string, bool> written, params string[] candidates)
  {
    foreach (string candidate in candidates)
    {
      try
      {
        if (written(candidate))
        {
          return candidate;
        }
      }
      catch (PatchException)
      {
      }
    }
    throw new PatchException($"{edit.Path} can't be written back as it was edited; change it in the file");
  }

  #endregion


}