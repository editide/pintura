using System.Diagnostics;
using System.Globalization;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>A presentation as a commit left it, extracted with its theme, its books, and the pictures and CSVs they
/// name into a folder of its own.</summary>
public record Revision(string Name, string Commit, string Folder, string Root, string DisplayBase);

/// <summary>A commit that changed a presentation, as the page lists it to compare against.</summary>
public record DeckVersion(string Commit, DateTimeOffset Date, string Subject, string Author, bool Mine,
  List<string> Tags);

public record VersionChanges(int Slides, bool First);

/// <summary>Presentations at a git revision. Each commit is extracted once, under the temporary folder, and loaded by
/// the same loader as the working tree, with origins named as the working tree names them.</summary>
public static class Revisions
{
  private const string ExtractionVersion = "2";

  public static Presentation Load(string root, string revision, string? displayBase = null)
  {
    return Load(Checkout(root, revision, displayBase ?? Directory.GetCurrentDirectory()));
  }

  public static Presentation Load(Revision checkout, IBookReader? books = null)
  {
    return PresentationLoader.Load(checkout.Root, checkout.DisplayBase, books);
  }

  public static Revision Checkout(string root, string revision, string displayBase)
  {
    root = Path.GetFullPath(root);
    (string top, string spelled, string presentation) = Locate(root);
    string commit = Commit(top, revision);
    string manifest = presentation == "." ? "presentation.yaml" : $"{presentation}/presentation.yaml";
    (int exit, string text, _) = Run(top, "show", $"{commit}:{manifest}");
    if (exit != 0)
    {
      throw new InvalidDataException($"{revision} has no {manifest}");
    }
    var paths = new List<string> { presentation };
    foreach ((string? book, string path) in Linked(root, spelled, text, revision))
    {
      paths.Add(path);
      if (book is not null)
      {
        paths.AddRange(Formats(top, commit, path));
      }
    }

    // The version names what an extraction holds, so one made before pictures and CSVs outside these paths came
    // along is made again.
    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
      string.Join("\n", paths.Prepend(ExtractionVersion)))))[..8];
    string folder = Temporary.Folder("revisions", $"{commit}-{key}".ToLowerInvariant());
    if (!Directory.Exists(folder))
    {
      Extract(top, commit, paths, folder);
    }
    string shown = Relative(spelled, Path.GetFullPath(displayBase));
    return new Revision(revision, commit, folder, Path.Combine(folder, presentation),
      shown.StartsWith("..", StringComparison.Ordinal) ? folder : Path.GetFullPath(Path.Combine(folder, shown)));
  }

  /// <summary>The repository's top, as git answers it and as the root spells it, and the presentation's folder in it.
  /// </summary>
  private static (string Top, string Spelled, string Presentation) Locate(string root)
  {
    string top = TopLevel(root);
    string presentation = Run(root, "rev-parse", "--show-prefix").Output.Trim().TrimEnd('/');
    // The top as root spells it: git answers with symlinks resolved, such as macOS's /private/var for /var.
    string spelled = root;
    foreach (string _ in presentation.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
      spelled = Path.GetDirectoryName(spelled)!;
    }
    return (top, spelled, presentation.Length == 0 ? "." : presentation);
  }

  /// <summary>The repository paths of the theme and the books a manifest names, with each book's id; null for the
  /// theme.</summary>
  private static IEnumerable<(string? Book, string Path)> Linked(string root, string spelled, string manifest,
    string revision)
  {
    if (Theme(manifest) is string theme)
    {
      string themePath = Relative(spelled, Path.GetFullPath(Path.Combine(root, theme)));
      if (themePath.StartsWith("..", StringComparison.Ordinal))
      {
        throw new InvalidDataException($"the theme at {theme} is outside the repository, so {revision} can't " +
          "be compared");
      }
      yield return (null, themePath);
    }
    foreach ((string id, string book) in Books(manifest))
    {
      string bookPath = Relative(spelled, Path.GetFullPath(Path.Combine(root, book)));
      if (bookPath.StartsWith("..", StringComparison.Ordinal))
      {
        throw new InvalidDataException($"the book {id} at {book} is outside the repository, so {revision} can't " +
          "be compared");
      }
      yield return (id, bookPath);
    }
  }

  /// <summary>The commits on the current branch that changed the presentation, its theme, or its books, newest first;
  /// empty outside a repository or before the presentation's first commit.</summary>
  public static List<DeckVersion> Versions(string root, int limit = 50)
  {
    root = Path.GetFullPath(root);
    try
    {
      (string top, string spelled, string presentation) = Locate(root);
      string manifest = File.ReadAllText(Path.Combine(root, "presentation.yaml"));
      List<string> paths = Linked(root, spelled, manifest, "HEAD").Select(linked => linked.Path)
        .Prepend(presentation).ToList();
      // Comments are about a version rather than part of one.
      string comments = $":(exclude){(presentation == "." ? "" : presentation + "/")}{CommentFile.Name}";
      (int exit, string output, _) = Run(top, ["log", "--first-parent", $"-n{limit}", $"--format={Format}", "HEAD",
        "--", .. paths, comments]);
      if (exit != 0)
      {
        return [];
      }
      string email = Run(top, "config", "user.email").Output.Trim();
      return output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(record => Version(record, email))
        .ToList();
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
      return [];
    }
  }

  /// <summary>One commit as a version, named by its full hash.</summary>
  public static DeckVersion Describe(string root, string commit)
  {
    string top = TopLevel(root);
    (int exit, string output, _) = Run(top, "log", "-1", $"--format={Format}", commit, "--");
    if (exit != 0)
    {
      throw new InvalidDataException($"no revision '{commit}'");
    }
    return Version(output.Trim().TrimEnd('\x1e'), Run(top, "config", "user.email").Output.Trim());
  }

  /// <summary>How many slides a commit changed from its first parent, or every slide it has when it's the
  /// presentation's first.</summary>
  public static VersionChanges Changes(string root, string revision)
  {
    Presentation after = Load(root, revision);
    Presentation before;
    try
    {
      before = Load(root, Commit(root, $"{Commit(root, revision)}^"));
    }
    catch (InvalidDataException)
    {
      return new VersionChanges(after.Slides.Count, true);
    }
    return new VersionChanges(Differ.Compare(before, after).Slides.Count, false);
  }

  // Unit and record separators, which no subject or name holds.
  private const string Format = "%H%x1f%aI%x1f%an%x1f%ae%x1f%D%x1f%s%x1e";

  private static DeckVersion Version(string record, string email)
  {
    string[] fields = record.Split('\x1f');
    List<string> tags = fields[4].Split(", ", StringSplitOptions.RemoveEmptyEntries)
      .Where(reference => reference.StartsWith("tag: ", StringComparison.Ordinal))
      .Select(reference => reference["tag: ".Length..])
      .ToList();
    return new DeckVersion(fields[0], DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture), fields[5],
      fields[2], email.Length > 0 && string.Equals(fields[3], email, StringComparison.OrdinalIgnoreCase), tags);
  }

  /// <summary>The full hash a revision names, so a moving name such as <c>HEAD</c> finds a new extraction once it
  /// moves.</summary>
  public static string Commit(string folder, string revision)
  {
    (int exit, string output, _) = Run(TopLevel(folder), "rev-parse", "--verify", "--quiet", "--end-of-options",
      $"{revision}^{{commit}}");
    if (exit != 0)
    {
      throw new InvalidDataException($"no revision '{revision}'");
    }
    return output.Trim();
  }

  /// <summary>The folders whose files say which commit each revision names: the repository's git folder and, for a
  /// worktree, the common one its branches live in. Empty outside a repository or without git.</summary>
  public static IReadOnlyList<string> GitFolders(string folder)
  {
    try
    {
      (int exit, string output, _) = Run(folder, "rev-parse", "--absolute-git-dir", "--git-common-dir");
      if (exit != 0)
      {
        return [];
      }
      return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.GetFullPath(path, folder))
        .Distinct(StringComparer.Ordinal)
        .ToList();
    }
    catch (InvalidDataException)
    {
      return [];
    }
  }

  private static string TopLevel(string folder)
  {
    (int exit, string output, _) = Run(folder, "rev-parse", "--show-toplevel");
    if (exit != 0)
    {
      throw new InvalidDataException($"{folder} is not in a git repository");
    }
    return Path.GetFullPath(output.Trim());
  }

  private static string? Theme(string manifest)
  {
    return Child(Manifest(manifest), "theme") is YamlScalarNode scalar
      ? scalar.Value
      : null;
  }

  /// <summary>Each book's id and path, as far as the manifest names them; the loader reports what's malformed.
  /// </summary>
  private static List<(string Id, string Path)> Books(string manifest)
  {
    if (Child(Manifest(manifest), "books") is not YamlSequenceNode books)
    {
      return [];
    }
    return books.Children.OfType<YamlMappingNode>()
      .Select(book => (Id: Child(book, "id") as YamlScalarNode, Path: Child(book, "path") as YamlScalarNode))
      .Where(book => book.Path?.Value is { Length: > 0 })
      .Select(book => (book.Id?.Value ?? book.Path!.Value!, book.Path!.Value!))
      .ToList();
  }

  private static YamlNode? Child(YamlMappingNode? map, string key)
  {
    return map is not null && map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? child) ? child : null;
  }

  private static YamlMappingNode? Manifest(string manifest)
  {
    try
    {
      var stream = new YamlStream();
      stream.Load(new StringReader(manifest));
      return stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
    }
    catch (YamlDotNet.Core.YamlException)
    {
      return null;
    }
  }

  /// <summary>The <c>formats.yaml</c> the commit has above a book, up to the repository's root, which Libreta merges
  /// into the book's formats.</summary>
  private static IEnumerable<string> Formats(string top, string commit, string book)
  {
    var candidates = new List<string>();
    for (string? folder = Path.GetDirectoryName(book); folder is not null; folder = Path.GetDirectoryName(folder))
    {
      candidates.Add(folder.Length == 0 ? "formats.yaml" : $"{folder.Replace('\\', '/')}/formats.yaml");
    }
    (int exit, string output, _) = Run(top, ["ls-tree", "--name-only", commit, "--", .. candidates]);
    return exit == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
  }

  /// <summary>Extracts beside the destination and renames, so a folder that exists is always whole.</summary>
  private static void Extract(string top, string commit, List<string> paths, string folder)
  {
    string partial = $"{folder}.{Guid.NewGuid():N}";
    Directory.CreateDirectory(partial);
    try
    {
      Archive(top, commit, paths, partial);
      List<string> referenced = Referenced(partial).ToList();
      if (referenced.Count > 0 && Committed(top, commit, referenced) is { Count: > 0 } committed)
      {
        Archive(top, commit, committed, partial);
      }
      // Libreta merges formats as far as a worktree's root, which it finds by a .git marker.
      File.WriteAllText(Path.Combine(partial, ".git"), "");
      try
      {
        Directory.Move(partial, folder);
      }
      catch (IOException) when (Directory.Exists(folder))
      {
      }
    }
    finally
    {
      if (Directory.Exists(partial))
      {
        Directory.Delete(partial, true);
      }
    }
  }

  private static void Archive(string top, string commit, IEnumerable<string> paths, string folder)
  {
    var start = Start(top, ["archive", "--format=tar", commit, "--", .. paths]);
    using Process process = Process.Start(start)!;
    Task<string> error = process.StandardError.ReadToEndAsync();
    TarFile.ExtractToDirectory(process.StandardOutput.BaseStream, folder, true);
    // The reader stops at the archive's end, before the padding git writes after it, which can be more than
    // Windows' pipe holds, so git would wait to write it while this waits for git.
    process.StandardOutput.BaseStream.CopyTo(Stream.Null);
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
      throw new InvalidDataException($"git archive failed: {error.Result.Trim()}");
    }
  }

  /// <summary>The pictures and CSVs the extracted YAML names outside the extracted paths, as repository paths;
  /// those outside the repository are left for the loader to report.</summary>
  private static IEnumerable<string> Referenced(string folder)
  {
    var found = new SortedSet<string>(StringComparer.Ordinal);
    foreach (string file in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories))
    {
      var stream = new YamlStream();
      try
      {
        stream.Load(new StringReader(File.ReadAllText(file)));
      }
      catch (YamlDotNet.Core.YamlException)
      {
        continue;
      }
      foreach (string value in stream.Documents.SelectMany(document => FileValues(document.RootNode)))
      {
        string path = Relative(folder, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, value)));
        if (!path.StartsWith("..", StringComparison.Ordinal) && !File.Exists(Path.Combine(folder, path)))
        {
          found.Add(path);
        }
      }
    }
    return found;
  }

  /// <summary>The paths the commit has, so one it lacks is the loader's to report rather than git archive's.
  /// </summary>
  private static List<string> Committed(string top, string commit, List<string> paths)
  {
    (int exit, string output, _) = Run(top, ["ls-tree", "--name-only", commit, "--", .. paths]);
    return exit == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : [];
  }

  private static IEnumerable<string> FileValues(YamlNode node)
  {
    if (node is YamlSequenceNode sequence)
    {
      return sequence.Children.SelectMany(FileValues);
    }
    if (node is not YamlMappingNode map)
    {
      return [];
    }
    return map.Children.SelectMany(pair =>
      pair.Key is YamlScalarNode { Value: "picture" or "data" } && pair.Value is YamlScalarNode { Value.Length: > 0 }
        ? [((YamlScalarNode)pair.Value).Value!]
        : FileValues(pair.Value));
  }

  private static string Relative(string from, string to)
  {
    return Path.GetRelativePath(from, to).Replace('\\', '/');
  }

  private static (int Exit, string Output, string Error) Run(string folder, params string[] arguments)
  {
    ProcessStartInfo start = Start(folder, arguments);
    Process process;
    try
    {
      process = Process.Start(start)!;
    }
    catch (System.ComponentModel.Win32Exception)
    {
      throw new InvalidDataException("comparing revisions needs git on the PATH");
    }
    using (process)
    {
      Task<string> error = process.StandardError.ReadToEndAsync();
      string output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return (process.ExitCode, output, error.Result);
    }
  }

  private static ProcessStartInfo Start(string folder, IEnumerable<string> arguments)
  {
    var start = new ProcessStartInfo("git")
    {
      WorkingDirectory = folder,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    foreach (string argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }
    return start;
  }


}