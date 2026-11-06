using System.Diagnostics;
using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>A commit that changed a presentation, as the page lists it to compare against, numbered from its first;
/// <see cref="ByHand"/> when the page's Save made it rather than an agent or git.</summary>
public record DeckVersion(int Number, string Commit, DateTimeOffset Date, string Subject, string Author, bool Mine,
  List<string> Tags, bool ByHand = false);

/// <summary>The repository's top, the presentation's folder in it, and the paths its versions cover: the folder and
/// theme it saves, and the books it reads, which Libreta saves.</summary>
public record VersionScope(string Top, string Presentation, List<string> Saved, List<string> Books)
{
  public string CommentFile => InPresentation(Pintura.CommentFile.Name);
  public string ResolvedFile => InPresentation(Pintura.CommentFile.ResolvedName);
  public string PickFile => InPresentation(Pintura.PickFile.Name);
  public string ResolvedPickFile => InPresentation(Pintura.PickFile.ResolvedName);
  public string Manifest => InPresentation("presentation.yaml");

  /// <summary>Comments are about a version rather than part of one.</summary>
  public string[] Comments => [$":(exclude){CommentFile}", $":(exclude){ResolvedFile}"];

  /// <summary>The slide a repository path is the file of, or null when it's another of the deck's files.</summary>
  public string? Slide(string path)
  {
    string folder = InPresentation("slides/");
    return path.StartsWith(folder, StringComparison.Ordinal) && path.EndsWith(".yaml", StringComparison.Ordinal) &&
      !path[folder.Length..].Contains('/')
      ? path[folder.Length..^".yaml".Length]
      : null;
  }

  private string InPresentation(string name)
  {
    return $"{(Presentation == "." ? "" : Presentation + "/")}{name}";
  }
}

/// <summary>Presentations at a git revision, read from git by the same loader as the working tree, with origins named
/// as the working tree names them.</summary>
public static class Revisions
{
  /// <summary>The presentation as a revision left it, with its theme, books and pictures, read from git.</summary>
  public static Presentation Load(string root, string revision, string? displayBase = null, IBookReader? books = null)
  {
    root = Path.GetFullPath(root);
    GitTree tree = Tree(root, revision);
    return PresentationLoader.Load(root, displayBase, (books ?? LibretaReader.Shared).At(tree), tree);
  }

  /// <summary>The tree of the commit a revision names, in the repository holding the presentation.</summary>
  public static GitTree Tree(string root, string revision)
  {
    (_, string spelled, _) = Locate(Path.GetFullPath(root));
    return GitTree.Open(spelled, Commit(root, revision));
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

  /// <summary>The paths a presentation's versions cover, as the working tree's manifest names them.</summary>
  public static VersionScope Scope(string root)
  {
    root = Path.GetFullPath(root);
    (string top, string spelled, string presentation) = Locate(root);
    string manifest = File.ReadAllText(Path.Combine(root, "presentation.yaml"));
    List<(string? Book, string Path)> linked = Linked(root, spelled, manifest, "HEAD").ToList();
    return new VersionScope(top, presentation,
      [presentation, .. linked.Where(path => path.Book is null).Select(path => path.Path)],
      [.. linked.Where(path => path.Book is not null).Select(path => path.Path)]);
  }

  /// <summary>The commits on the current branch that changed the presentation, its theme, or its books, newest first;
  /// empty outside a repository or before the presentation's first commit.</summary>
  public static List<DeckVersion> Versions(string root, int limit = 50)
  {
    try
    {
      VersionScope scope = Scope(root);
      (int exit, string output, _) = Run(scope.Top, ["log", "--first-parent", $"-n{limit}", $"--format={Format}",
        "HEAD", "--", .. scope.Saved, .. scope.Books, .. scope.Comments]);
      if (exit != 0)
      {
        return [];
      }
      int count = Count(scope, "HEAD");
      string email = Run(scope.Top, "config", "user.email").Output.Trim();
      return output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select((record, index) => Version(count - index, record, email))
        .ToList();
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException)
    {
      return [];
    }
  }

  /// <summary>How many versions the presentation had as of a commit, which numbers the newest of them.</summary>
  public static int Count(VersionScope scope, string commit)
  {
    (int exit, string output, _) = Run(scope.Top, ["rev-list", "--count", "--first-parent", commit, "--",
      .. scope.Saved, .. scope.Books, .. scope.Comments]);
    return exit == 0 ? int.Parse(output.Trim(), CultureInfo.InvariantCulture) : 0;
  }

  /// <summary>The repository paths of the presentation's files that differ between two commits.</summary>
  public static List<string> ChangedFiles(VersionScope scope, string from, string to)
  {
    (int exit, string output, string error) = Run(scope.Top, ["diff", "--name-only", "--no-renames", from, to, "--",
      .. scope.Saved, .. scope.Books, .. scope.Comments]);
    if (exit != 0)
    {
      throw new InvalidDataException(error.Trim());
    }
    return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
  }

  /// <summary>One commit as a version, named by its full hash.</summary>
  public static DeckVersion Describe(string root, string commit)
  {
    VersionScope scope = Scope(root);
    (int exit, string output, _) = Run(scope.Top, "log", "-1", $"--format={Format}", commit, "--");
    if (exit != 0)
    {
      throw new InvalidDataException($"no revision '{commit}'");
    }
    return Version(Count(scope, commit), output.Trim().TrimEnd('\x1e'),
      Run(scope.Top, "config", "user.email").Output.Trim());
  }

  /// <summary>The trailer the page's Save writes, since every version is authored as whoever runs git, agents too.
  /// </summary>
  public const string SavedFrom = "Saved-from";

  // Unit and record separators, which no subject or name holds.
  private const string Format = $"%H%x1f%aI%x1f%an%x1f%ae%x1f%D%x1f%s%x1f%(trailers:key={SavedFrom},valueonly)%x1e";

  private static DeckVersion Version(int number, string record, string email)
  {
    string[] fields = record.Split('\x1f');
    List<string> tags = fields[4].Split(", ", StringSplitOptions.RemoveEmptyEntries)
      .Where(reference => reference.StartsWith("tag: ", StringComparison.Ordinal))
      .Select(reference => reference["tag: ".Length..])
      .ToList();
    return new DeckVersion(number, fields[0], DateTimeOffset.Parse(fields[1], CultureInfo.InvariantCulture), fields[5],
      fields[2], email.Length > 0 && string.Equals(fields[3], email, StringComparison.OrdinalIgnoreCase), tags,
      fields[6].Trim().Length > 0);
  }

  /// <summary>The full hash a revision names, so a moving name such as <c>HEAD</c> is read anew once it moves.
  /// </summary>
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

  /// <summary>The paths a YAML node names as pictures or CSVs, as written.</summary>
  public static IEnumerable<string> FileValues(YamlNode node)
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

  public static (int Exit, string Output, string Error) Run(string folder, params string[] arguments)
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

  internal static ProcessStartInfo Start(string folder, IEnumerable<string> arguments)
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