using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;

namespace Pintura;

/// <summary>Where a presentation's files are read from: the disk, or a commit's tree. Paths are the working tree's
/// either way, so a revision names its files, and its errors, as the working tree does.</summary>
public interface IFiles
{
  bool Exists(string path);
  bool DirectoryExists(string path);
  byte[] ReadAllBytes(string path);

  string ReadAllText(string path)
  {
    using var reader = new StreamReader(new MemoryStream(ReadAllBytes(path)), Encoding.UTF8);
    return reader.ReadToEnd();
  }

  /// <summary>The files directly in a folder whose names match a pattern such as <c>*.yaml</c>.</summary>
  IEnumerable<string> Files(string folder, string pattern);

  /// <summary>Changes whenever the file does, so a URL carrying it is fetched again.</summary>
  string Stamp(string path);
}

public sealed class DiskFiles : IFiles
{
  public static readonly DiskFiles Instance = new DiskFiles();

  public bool Exists(string path)
  {
    return File.Exists(path);
  }

  public bool DirectoryExists(string path)
  {
    return Directory.Exists(path);
  }

  public byte[] ReadAllBytes(string path)
  {
    return File.ReadAllBytes(path);
  }

  public IEnumerable<string> Files(string folder, string pattern)
  {
    return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern) : [];
  }

  public string Stamp(string path)
  {
    var file = new FileInfo(path);
    return file.Exists ? $"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}" : "0";
  }


}

/// <summary>A commit's tree, listed once and read without checking anything out, so a revision costs no copy of its
/// files and reads even what the working tree has since moved or deleted.</summary>
public sealed class GitTree : IFiles
{
  private readonly Dictionary<string, string> blobs;
  private readonly Dictionary<string, string> trees;
  private readonly Dictionary<string, List<string>> files = new Dictionary<string, List<string>>(Comparer);

  private GitTree(string top, string commit, Dictionary<string, string> blobs, Dictionary<string, string> trees)
  {
    Top = top;
    Commit = commit;
    this.blobs = blobs;
    this.trees = trees;
    foreach (string path in blobs.Keys)
    {
      string folder = Path.GetDirectoryName(path)!;
      if (!files.TryGetValue(folder, out List<string>? list))
      {
        files[folder] = list = [];
      }
      list.Add(path);
    }
  }

  /// <summary>The repository's top as the working tree spells it, which may differ from git's through a symlink.
  /// </summary>
  public string Top { get; }
  public string Commit { get; }

  private static StringComparer Comparer => OperatingSystem.IsLinux()
    ? StringComparer.Ordinal
    : StringComparer.OrdinalIgnoreCase;

  /// <summary>The tree of <paramref name="commit"/> in the repository at <paramref name="top"/>.</summary>
  public static GitTree Open(string top, string commit)
  {
    (int exit, string listing, string error) = Revisions.Run(top, "ls-tree", "-r", "-t", "-z", "--full-tree", commit);
    if (exit != 0)
    {
      throw new InvalidDataException($"git ls-tree failed: {error.Trim()}");
    }
    var blobs = new Dictionary<string, string>(Comparer);
    var trees = new Dictionary<string, string>(Comparer);
    foreach (string entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
    {
      int tab = entry.IndexOf('\t');
      string[] fields = entry[..tab].Split(' ');
      string path = Path.Combine(top, entry[(tab + 1)..].Replace('/', Path.DirectorySeparatorChar));
      if (fields[1] == "blob")
      {
        blobs[path] = fields[2];
      }
      else if (fields[1] == "tree")
      {
        trees[path] = fields[2];
      }
    }
    return new GitTree(top, commit, blobs, trees);
  }

  public bool Exists(string path)
  {
    return blobs.ContainsKey(Path.GetFullPath(path));
  }

  public bool DirectoryExists(string path)
  {
    path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    return trees.ContainsKey(path) || Comparer.Equals(path, Top);
  }

  public byte[] ReadAllBytes(string path)
  {
    if (!blobs.TryGetValue(Path.GetFullPath(path), out string? blob))
    {
      throw new FileNotFoundException($"no {path} at {Commit[..7]}", path);
    }
    return GitObjects.For(Top).Read(blob);
  }

  public IEnumerable<string> Files(string folder, string pattern)
  {
    return files.GetValueOrDefault(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), [])
      .Where(path => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path)))
      .Order(StringComparer.Ordinal);
  }

  public string Stamp(string path)
  {
    return blobs.GetValueOrDefault(Path.GetFullPath(path), "0");
  }

  /// <summary>Names what Libreta reads of a book at this commit: the book's tree, and each <c>formats.yaml</c> above
  /// it as far as the repository's root, which Libreta merges into its formats. Null when the commit has no such
  /// folder.</summary>
  public string? BookKey(string root)
  {
    root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    if (!trees.TryGetValue(root, out string? tree))
    {
      return null;
    }
    var key = new StringBuilder(tree);
    for (string? folder = Path.GetDirectoryName(root); folder is not null && folder.Length >= Top.Length;
      folder = Path.GetDirectoryName(folder))
    {
      key.Append(' ').Append(blobs.GetValueOrDefault(Path.Combine(folder, "formats.yaml"), "-"));
    }
    return key.ToString();
  }


}

/// <summary>One <c>git cat-file --batch</c> per repository, kept for the life of the process: objects never change,
/// so every revision of the repository reads through it, and a step through history starts no process per file.
/// </summary>
public sealed class GitObjects
{
  private static readonly ConcurrentDictionary<string, GitObjects> Repositories =
    new ConcurrentDictionary<string, GitObjects>(StringComparer.Ordinal);
  private readonly string top;
  private readonly object gate = new object();
  private Process? process;

  private GitObjects(string top)
  {
    this.top = top;
  }

  public static GitObjects For(string top)
  {
    return Repositories.GetOrAdd(top, folder => new GitObjects(folder));
  }

  public byte[] Read(string blob)
  {
    lock (gate)
    {
      if (process is null || process.HasExited)
      {
        process?.Dispose();
        ProcessStartInfo start = Revisions.Start(top, ["cat-file", "--batch"]);
        start.RedirectStandardInput = true;
        start.RedirectStandardError = false;
        process = Process.Start(start)!;
      }
      process.StandardInput.Write($"{blob}\n");
      process.StandardInput.Flush();
      Stream output = process.StandardOutput.BaseStream;
      string[] header = Line(output).Split(' ');
      if (header is not [_, "blob", string size])
      {
        throw new InvalidDataException($"git couldn't read the object {blob}");
      }
      byte[] content = new byte[int.Parse(size, CultureInfo.InvariantCulture)];
      output.ReadExactly(content);
      output.ReadExactly(new byte[1]);
      return content;
    }
  }

  private static string Line(Stream stream)
  {
    var line = new List<byte>();
    for (int next = stream.ReadByte(); next != '\n'; next = stream.ReadByte())
    {
      if (next < 0)
      {
        throw new InvalidDataException("git cat-file stopped answering");
      }
      line.Add((byte)next);
    }
    return Encoding.UTF8.GetString(line.ToArray());
  }


}