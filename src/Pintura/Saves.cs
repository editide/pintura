using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Pintura;

public record SaveStatus(bool Unsaved, int Next);

public record SaveRequest(string? Message);

/// <summary>Saving is committing the presentation and its theme, and only those, so the page's readers can keep
/// versions without knowing git. Books are Libreta's to save.</summary>
public static class Saves
{
  public const string First = "First version";

  /// <summary>Whether the presentation or its theme differs from its last version, and the number the next one takes.
  /// Outside a repository everything is unsaved.</summary>
  public static SaveStatus Status(string root)
  {
    if (!Inside(root))
    {
      return new SaveStatus(true, 1);
    }
    VersionScope scope = Revisions.Scope(root);
    return new SaveStatus(Unsaved(scope), Revisions.Count(scope, "HEAD") + 1);
  }

  /// <summary>The commit the presentation is at and whether its slides or theme changed since, which a comment
  /// records as what its reviewer saw; null outside a repository or before its first commit.</summary>
  public static Snapshot? Snapshot(string root)
  {
    try
    {
      if (!Inside(root))
      {
        return null;
      }
      (int exit, string head, _) = Revisions.Run(root, "rev-parse", "--short", "HEAD");
      return exit == 0 ? new Snapshot(head.Trim(), Unsaved(Revisions.Scope(root))) : null;
    }
    catch (InvalidDataException)
    {
      return null;
    }
  }

  /// <summary>Comments alone wait for the next save, as does YAML only reformatted, which the next save takes along.
  /// </summary>
  private static bool Unsaved(VersionScope scope)
  {
    return Changes(scope).Any(path => path != scope.CommentFile && path != scope.ResolvedFile
      && !Reformatted(scope, path));
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

  /// <summary>Commits the presentation and its theme, starting a repository in the presentation when there's none,
  /// under the message or, without one, the draft.</summary>
  public static DeckVersion Save(string root, string? message, Presentation? current = null)
  {
    root = Path.GetFullPath(root);
    if (!Inside(root))
    {
      Check(Revisions.Run(root, "init", "-q"));
    }
    if (!Status(root).Unsaved)
    {
      throw new InvalidDataException("there are no changes to save");
    }
    string subject = string.IsNullOrWhiteSpace(message) ? Draft(root, current) : message.Trim();
    VersionScope scope = Revisions.Scope(root);
    List<string> paths = [.. Changes(scope).Select(path => $":(literal){path}")];
    Check(Revisions.Run(scope.Top, ["add", "-A", "--", .. paths]));
    // Naming the paths commits them alone, leaving whatever else is staged staged.
    Check(Revisions.Run(scope.Top, [.. Identity(scope.Top), "commit", "-q", "--only", "-m", subject, "--", .. paths]));
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
    bool born = Revisions.Run(scope.Top, "rev-parse", "--verify", "--quiet", "HEAD").Exit == 0;
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

  /// <summary>What changed since the last version, as a reader would say it.</summary>
  public static string Draft(string root, Presentation? current = null)
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
    return Draft(Differ.Compare(before, after));
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