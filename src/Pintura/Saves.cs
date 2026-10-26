using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>Whether the deck differs from its last version, the number the next one takes, and of the changes, the
/// ones the page made, which Save commits: the slides they're in, and whether they reach a file the slides share.
/// </summary>
public record SaveStatus(bool Unsaved, int Next, List<string> Slides, bool Shared)
{
  public bool Edited => Shared || Slides.Count > 0;
}

/// <summary>A save, of the page's edits in the files named, or all of them when none are.</summary>
public record SaveRequest(string? Message, List<string>? Files);

/// <summary>Edits a save can take without the rest, as the save box lists them, and the files they're in.</summary>
public record SavePart(string Label, List<string> Files);

/// <summary>Saving is committing what the page changed in the presentation and its theme, and only that, so the page's
/// readers can keep versions without knowing git while agents commit their own work. Books are Libreta's to save.
/// </summary>
public static class Saves
{
  public const string First = "First version";

  /// <summary>Outside a repository everything is unsaved, and the page's.</summary>
  public static SaveStatus Status(string root)
  {
    if (!Inside(root))
    {
      return new SaveStatus(true, 1, [], true);
    }
    VersionScope scope = Revisions.Scope(root);
    Pending pending = Pend(scope, root);
    Dictionary<string, bool> edited = pending.Edited;
    return new SaveStatus(pending.Changed.Count > 0, Revisions.Count(scope, "HEAD") + 1,
      [.. edited.Keys.Select(scope.Slide).OfType<string>().Order(StringComparer.Ordinal)],
      edited.Keys.Any(path => scope.Slide(path) is null));
  }

  /// <summary>The commit the presentation is at and what changed since, whoever changed it, which a comment records
  /// as what its reviewer saw; null outside a repository or before its first commit.</summary>
  public static Snapshot? Snapshot(string root)
  {
    try
    {
      if (!Inside(root))
      {
        return null;
      }
      (int exit, string head, _) = Revisions.Run(root, "rev-parse", "--short", "HEAD");
      if (exit != 0)
      {
        return null;
      }
      VersionScope scope = Revisions.Scope(root);
      List<string> changed = Substantive(scope, Changes(scope));
      return new Snapshot(head.Trim(), changed.Any(path => scope.Slide(path) is null),
        changed.Select(scope.Slide).OfType<string>().ToHashSet(StringComparer.Ordinal));
    }
    catch (InvalidDataException)
    {
      return null;
    }
  }

  /// <summary>The deck's files that differ from its last version, those of them that are more than comments or
  /// reformatting, and the ones the page wrote, each with whether something besides the page changed it too. Before
  /// the first version, every file is the page's.</summary>
  private record Pending(List<string> All, List<string> Changed, Dictionary<string, bool> Wrote)
  {
    public Dictionary<string, bool> Edited => Wrote.Where(pair => Changed.Contains(pair.Key))
      .ToDictionary(pair => pair.Key, pair => pair.Value);
  }

  /// <summary>Comments alone wait for the next save, as does YAML only reformatted, which the next save takes along
  /// when the page wrote it. Given files, the page's edits are only the parts they're in.</summary>
  private static Pending Pend(VersionScope scope, string root, IReadOnlyCollection<string>? only = null)
  {
    List<string> all = Changes(scope);
    List<string> changed = Substantive(scope, all);
    Dictionary<string, bool> wrote = Born(scope)
      ? PageEdits.Read(scope, root, all.ToHashSet(StringComparer.Ordinal))
      : all.ToDictionary(path => path, _ => false);
    if (only is not null)
    {
      HashSet<string> chosen = [.. Groups(scope, wrote.Keys.Where(changed.Contains))
        .Where(files => files.Any(only.Contains)).SelectMany(files => files)];
      wrote = wrote.Where(pair => chosen.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }
    return new Pending(all, changed, wrote);
  }

  /// <summary>The parts of the page's edits a save can take without the rest, in slide order; none outside a
  /// repository or before its first version, which takes everything.</summary>
  public static List<SavePart> Parts(string root, Presentation current)
  {
    if (!Inside(root))
    {
      return [];
    }
    VersionScope scope = Revisions.Scope(root);
    if (!Born(scope))
    {
      return [];
    }
    int Number(string path) => current.Slides.FindIndex(slide => slide.Id == scope.Slide(path)) + 1;
    return [.. Groups(scope, Pend(scope, root).Edited.Keys)
      .OrderBy(files => files is [string file] && scope.Slide(file) is not null ? Number(file) : int.MaxValue)
      .Select(files => new SavePart(Label(scope, files, Number), files))];
  }

  /// <summary>Each slide's file is a part, as are presentation.yaml and the theme. A slide deleted or restored goes
  /// with presentation.yaml, since a version listing a slide it lacks, or lacking one it lists, doesn't load.
  /// </summary>
  private static List<List<string>> Groups(VersionScope scope, IEnumerable<string> edited)
  {
    List<List<string>> slides = [];
    List<string> listed = [];
    List<string> themed = [];
    foreach (string path in edited.Order(StringComparer.Ordinal))
    {
      if (path == scope.Manifest || scope.Slide(path) is not null && CameOrWent(scope, path))
      {
        listed.Add(path);
      }
      else if (scope.Slide(path) is not null)
      {
        slides.Add([path]);
      }
      else
      {
        themed.Add(path);
      }
    }
    return [.. slides, .. new[] { listed, themed }.Where(files => files.Count > 0)];
  }

  private static bool CameOrWent(VersionScope scope, string path)
  {
    bool saved = Revisions.Run(scope.Top, "rev-parse", "--verify", "--quiet", $"HEAD:{path}").Exit == 0;
    return saved != File.Exists(Path.Combine(scope.Top, path));
  }

  /// <summary>A part as <c>Slide 4 and presentation.yaml</c> or <c>presentation.yaml, removing slide intro</c>.
  /// </summary>
  private static string Label(VersionScope scope, List<string> files, Func<string, int> number)
  {
    List<string> slides = [.. files.Where(path => scope.Slide(path) is not null)];
    List<int> numbers = [.. slides.Select(number).Where(found => found > 0)];
    List<string> removed = [.. slides.Where(path => number(path) == 0).Select(path => scope.Slide(path)!)];
    List<string> parts = numbers.Count > 0 ? [Numbers(numbers)] : [];
    if (files.Contains(scope.Manifest))
    {
      parts.Add("presentation.yaml");
    }
    if (files.Any(path => scope.Slide(path) is null && path != scope.Manifest))
    {
      parts.Add("the theme");
    }
    string label = Joined(parts) ?? "";
    if (removed.Count > 0)
    {
      string gone = $"removing {(removed.Count == 1 ? "slide" : "slides")} {Joined(removed)}";
      label = label.Length > 0 ? $"{label}, {gone}" : gone;
    }
    return label.StartsWith("presentation.yaml", StringComparison.Ordinal)
      ? label
      : char.ToUpperInvariant(label[0]) + label[1..];
  }

  private static bool Born(VersionScope scope)
  {
    return Revisions.Run(scope.Top, "rev-parse", "--verify", "--quiet", "HEAD").Exit == 0;
  }

  private static List<string> Substantive(VersionScope scope, List<string> changes)
  {
    return [.. changes.Where(path => path != scope.CommentFile && path != scope.ResolvedFile &&
      !Reformatted(scope, path))];
  }

  /// <summary>Whether a YAML file reads as it did in the last version, differing only in comments, blank lines,
  /// indentation or the order of a mapping's keys.</summary>
  private static bool Reformatted(VersionScope scope, string path)
  {
    string file = Path.Combine(scope.Top, path);
    if (!path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
    {
      return false;
    }
    (int exit, string before, _) = Revisions.Run(scope.Top, "show", $"HEAD:{path}");
    if (exit != 0)
    {
      return false;
    }
    try
    {
      var was = new YamlStream();
      was.Load(new StringReader(before));
      var now = new YamlStream();
      now.Load(new StringReader(File.ReadAllText(file)));
      return was.Documents.Count == now.Documents.Count
        && was.Documents.Zip(now.Documents).All(pair => Same(pair.First.RootNode, pair.Second.RootNode));
    }
    catch (YamlException)
    {
      return false;
    }
  }

  /// <summary>Quoting a plain scalar counts as a change, since it can turn a number or a boolean into a string.
  /// </summary>
  private static bool Same(YamlNode was, YamlNode now)
  {
    switch (was, now)
    {
      case (YamlScalarNode a, YamlScalarNode b):
        return a.Value == b.Value && (a.Style == ScalarStyle.Plain) == (b.Style == ScalarStyle.Plain);
      case (YamlSequenceNode a, YamlSequenceNode b):
        return a.Children.Count == b.Children.Count
          && a.Children.Zip(b.Children).All(pair => Same(pair.First, pair.Second));
      case (YamlMappingNode a, YamlMappingNode b):
        Dictionary<string, YamlNode> keyed = [];
        foreach ((YamlNode key, YamlNode value) in b.Children)
        {
          if (key is not YamlScalarNode { Value: string name })
          {
            return false;
          }
          keyed[name] = value;
        }
        return a.Children.Count == keyed.Count && a.Children.All(entry =>
          entry.Key is YamlScalarNode { Value: string name } && keyed.TryGetValue(name, out YamlNode? value)
          && Same(entry.Value, value));
      default:
        return false;
    }
  }

  /// <summary>Commits what the page changed in the presentation and its theme, or in the parts of it the files named
  /// are in, with the comments, starting a repository in the presentation when there's none, under the message or,
  /// without one, the draft.</summary>
  public static DeckVersion Save(string root, string? message, Presentation? current = null,
    IReadOnlyCollection<string>? only = null)
  {
    root = Path.GetFullPath(root);
    if (!Inside(root))
    {
      Check(Revisions.Run(root, "init", "-q"));
    }
    VersionScope scope = Revisions.Scope(root);
    Pending pending = Pend(scope, root, only);
    if (pending.Edited.Count == 0)
    {
      throw new InvalidDataException("there are no changes to save");
    }
    string subject = string.IsNullOrWhiteSpace(message) ? Draft(root, current, only) : message.Trim();
    List<string> files = [.. pending.All.Where(path => path == scope.CommentFile || path == scope.ResolvedFile ||
      pending.Wrote.ContainsKey(path))];
    List<string> paths = [.. files.Select(path => $":(literal){path}")];
    Check(Revisions.Run(scope.Top, ["add", "-A", "--", .. paths]));
    // Naming the paths commits them alone, leaving whatever else is staged staged.
    Check(Revisions.Run(scope.Top, [.. Identity(scope.Top), "commit", "-q", "--only", "-m", subject,
      "--trailer", $"{Revisions.SavedFrom}: Pintura", "--", .. paths]));
    PageEdits.Forget(scope, root, files);
    return Revisions.Versions(root, 1)[0];
  }

  /// <summary>The deck's files that differ from its last version, as repository paths: every file it had that changed
  /// or went, and of the new ones only YAML and the pictures and CSVs YAML names, so an export or a screenshot left
  /// in the folder isn't saved with it.</summary>
  private static List<string> Changes(VersionScope scope)
  {
    string[] Listed(params string[] arguments)
    {
      (int exit, string output, _) = Revisions.Run(scope.Top, [.. arguments, "--", .. scope.Saved]);
      return exit == 0 ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }
    bool born = Born(scope);
    var changed = new SortedSet<string>(StringComparer.Ordinal);
    var added = new List<string>(Listed("ls-files", "-z", "--others", "--exclude-standard"));
    if (born)
    {
      string[] entries = Listed("diff", "-z", "--name-status", "--no-renames", "HEAD");
      for (int index = 0; index + 1 < entries.Length; index += 2)
      {
        if (entries[index] == "A")
        {
          added.Add(entries[index + 1]);
        }
        else
        {
          changed.Add(entries[index + 1]);
        }
      }
    }
    else
    {
      added.AddRange(Listed("ls-files", "-z", "--cached"));
    }
    HashSet<string> named = Named(scope);
    foreach (string path in added)
    {
      if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || named.Contains(path))
      {
        changed.Add(path);
      }
    }
    return [.. changed];
  }

  /// <summary>The pictures and CSVs the deck's YAML names, as repository paths.</summary>
  private static HashSet<string> Named(VersionScope scope)
  {
    var named = new HashSet<string>(StringComparer.Ordinal);
    foreach (string saved in scope.Saved)
    {
      string folder = Path.GetFullPath(Path.Combine(scope.Top, saved));
      if (!Directory.Exists(folder))
      {
        continue;
      }
      foreach (string file in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories))
      {
        var stream = new YamlStream();
        try
        {
          stream.Load(new StringReader(File.ReadAllText(file)));
        }
        catch (YamlException)
        {
          continue;
        }
        foreach (string value in stream.Documents.SelectMany(document => Revisions.FileValues(document.RootNode)))
        {
          string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, value));
          named.Add(Path.GetRelativePath(scope.Top, path).Replace('\\', '/'));
        }
      }
    }
    return named;
  }

  /// <summary>What the page changed since the last version, or in the parts the files named are in, as a reader
  /// would say it.</summary>
  public static string Draft(string root, Presentation? current = null, IReadOnlyCollection<string>? only = null)
  {
    Presentation after = current ?? PresentationLoader.Load(root);
    Presentation before;
    try
    {
      before = Revisions.Load(root, "HEAD");
    }
    catch (InvalidDataException)
    {
      return First;
    }
    DeckDiff diff = Differ.Compare(before, after);
    VersionScope scope = Revisions.Scope(root);
    Pending pending = Pend(scope, root, only);
    HashSet<string> edited = [.. pending.Edited.Keys];
    HashSet<string> mine = [.. edited.Select(scope.Slide).OfType<string>()];
    HashSet<string> others = [.. pending.Changed.Where(path => !edited.Contains(path)).Select(scope.Slide)
      .OfType<string>()];
    bool listed = edited.Contains(scope.Manifest);
    bool themed = edited.Any(path => scope.Slide(path) is null && path != scope.Manifest);
    // A slide's changes that came from the list or the theme are the page's when it wrote them, unless the slide's
    // own file holds someone else's too.
    List<SlideDiff> slides = [.. diff.Slides
      .Where(slide => mine.Contains(slide.Id) || (listed || themed) && !others.Contains(slide.Id))
      .Where(slide => listed || slide.Status != "moved")
      .Select(slide => listed ? slide : slide with { Moved = false })];
    return Draft(new DeckDiff(slides, themed ? diff.Causes : []));
  }

  /// <summary>What saving takes along that the page didn't write: the slides and shared files it wrote, of the parts
  /// the files named are in when there are any, that something else changed too, as <c>slide 5 and the theme</c>;
  /// null when there are none.</summary>
  public static string? Mixed(string root, Presentation current, IReadOnlyCollection<string>? only = null)
  {
    if (!Inside(root))
    {
      return null;
    }
    VersionScope scope = Revisions.Scope(root);
    List<string> mixed = [.. Pend(scope, root, only).Edited.Where(pair => pair.Value).Select(pair => pair.Key)];
    List<int> numbers = [.. mixed.Select(scope.Slide).OfType<string>()
      .Select(id => current.Slides.FindIndex(slide => slide.Id == id) + 1).Where(number => number > 0)];
    List<string> parts = numbers.Count > 0 ? [Numbers(numbers)] : [];
    if (mixed.Contains(scope.Manifest))
    {
      parts.Add("presentation.yaml");
    }
    if (mixed.Any(path => scope.Slide(path) is null && path != scope.Manifest))
    {
      parts.Add("the theme");
    }
    return Joined(parts);
  }

  private static string? Joined(List<string> parts)
  {
    return parts switch
    {
      [] => null,
      [string one] => one,
      _ => string.Join(", ", parts[..^1]) + (parts.Count > 2 ? ", and " : " and ") + parts[^1]
    };
  }

  public static string Draft(DeckDiff diff)
  {
    var slides = new OrderedDictionary<string, List<int>>
    {
      ["added"] = [], ["removed"] = [], ["reworded"] = [], ["rearranged"] = [], ["restyled"] = [], ["edited"] = []
    };
    bool reordered = false;
    foreach (SlideDiff slide in diff.Slides)
    {
      reordered |= slide.Moved || slide.Status == "moved";
      if (slide.Status is "added" or "removed")
      {
        slides[slide.Status].Add((slide.After ?? slide.Before)!.Value);
        continue;
      }
      if (slide.Status != "changed")
      {
        continue;
      }
      var kinds = new HashSet<string>(StringComparer.Ordinal);
      foreach (ItemDiff item in slide.Items)
      {
        if (item.Status != "changed")
        {
          kinds.Add("edited");
          continue;
        }
        // A change the theme made is the theme's, said once for every slide.
        (bool reworded, bool moved, bool restyled) = DiffCommand.Kinds(
          item.Changes.Where(change => change.Cause is null).ToList());
        if (reworded)
        {
          kinds.Add("reworded");
        }
        if (moved)
        {
          kinds.Add("rearranged");
        }
        if (restyled)
        {
          kinds.Add("restyled");
        }
      }
      if (slide.Changes.Count > 0)
      {
        kinds.Add("restyled");
      }
      foreach (string kind in kinds)
      {
        slides[kind].Add(slide.After!.Value);
      }
    }
    List<string> parts = [.. slides.Where(pair => pair.Value.Count > 0)
      .Select(pair => $"{pair.Key} {Numbers(pair.Value)}")];
    if (reordered)
    {
      parts.Add("reordered the slides");
    }
    if (diff.Causes.Count > 0)
    {
      parts.Add("changed the theme");
    }
    if (parts.Count == 0)
    {
      return "Edited the presentation's files";
    }
    string sentence = parts.Count == 1
      ? parts[0]
      : string.Join(", ", parts[..^1]) +
        (parts.Count > 2 || parts.Any(part => part.Contains(" and ", StringComparison.Ordinal)) ? ", and " : " and ") +
        parts[^1];
    return char.ToUpperInvariant(sentence[0]) + sentence[1..];
  }

  /// <summary>Slides by number, three or more in a row as a range: <c>slides 2–5 and 8</c>.</summary>
  private static string Numbers(List<int> numbers)
  {
    numbers.Sort();
    var spans = new List<string>();
    for (int start = 0; start < numbers.Count;)
    {
      int end = start;
      while (end + 1 < numbers.Count && numbers[end + 1] == numbers[end] + 1)
      {
        end++;
      }
      if (end - start >= 2)
      {
        spans.Add(string.Create(CultureInfo.InvariantCulture, $"{numbers[start]}–{numbers[end]}"));
        start = end + 1;
      }
      else
      {
        spans.Add(numbers[start].ToString(CultureInfo.InvariantCulture));
        start++;
      }
    }
    string listed = spans.Count == 1 ? spans[0] : $"{string.Join(", ", spans[..^1])} and {spans[^1]}";
    return numbers.Count == 1 ? $"slide {listed}" : $"slides {listed}";
  }

  /// <summary>The name and email git lacks to commit with, as settings for one command: whoever comments are by, and
  /// that name at this machine, so a reader who never set git up can still save.</summary>
  private static List<string> Identity(string top)
  {
    var settings = new List<string>();
    string name = Revisions.Run(top, "config", "user.name").Output.Trim();
    if (name.Length == 0)
    {
      settings.AddRange(["-c", $"user.name={CommentFile.Author(top)}"]);
    }
    if (Revisions.Run(top, "config", "user.email").Output.Trim().Length == 0)
    {
      settings.AddRange(["-c", $"user.email={Environment.UserName}@{Environment.MachineName}"]);
    }
    return settings;
  }

  private static bool Inside(string root)
  {
    return Revisions.Run(root, "rev-parse", "--is-inside-work-tree").Exit == 0;
  }

  private static void Check((int Exit, string Output, string Error) result)
  {
    if (result.Exit != 0)
    {
      throw new InvalidDataException($"git couldn't save: {result.Error.Trim()}");
    }
  }


}