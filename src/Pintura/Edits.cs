using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pintura;

/// <summary>The deck's files the page wrote since their last version, which Save commits and nothing else, so an agent
/// working in the same folder commits its own. They're kept in git's folder, so a restarted server still knows them.
/// </summary>
public static class PageEdits
{
  private static readonly object gate = new();

  /// <summary>A file the page wrote: whether only the page changed it since it matched its last version, and its
  /// contents as the page left them, as git hashes them, empty when the page removed it.</summary>
  private record Entry(bool Alone, string Left);

  /// <summary>Records that the page wrote <paramref name="path"/>, which read <paramref name="before"/> until then,
  /// null when it didn't exist. Outside a repository there's nothing to record.</summary>
  public static void Wrote(string root, string path, string? before)
  {
    try
    {
      if (Revisions.Run(root, "rev-parse", "--is-inside-work-tree").Exit != 0)
      {
        return;
      }
      VersionScope scope = Revisions.Scope(root);
      string file = RepositoryPath(scope, root, path);
      lock (gate)
      {
        Dictionary<string, Entry> entries = Load(scope, root);
        bool alone = entries.TryGetValue(file, out Entry? entry)
          ? entry.Alone && entry.Left == Blob(before)
          : Blob(before) == Saved(scope, file);
        entries[file] = new Entry(alone, Blob(File.Exists(path) ? File.ReadAllText(path) : null));
        Store(scope, root, entries);
      }
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or
      UnauthorizedAccessException)
    {
    }
  }

  /// <summary>The files the page wrote that are still among <paramref name="changed"/>, each with whether something
  /// besides the page changed it too; the rest were saved or put back since, and are forgotten.</summary>
  public static Dictionary<string, bool> Read(VersionScope scope, string root, IReadOnlySet<string> changed)
  {
    lock (gate)
    {
      Dictionary<string, Entry> entries = Load(scope, root);
      if (entries.Keys.Where(file => !changed.Contains(file)).ToList() is [_, ..] gone)
      {
        foreach (string file in gone)
        {
          entries.Remove(file);
        }
        Store(scope, root, entries);
      }
      return entries.ToDictionary(pair => pair.Key, pair =>
      {
        string path = Path.Combine(scope.Top, pair.Key);
        return !pair.Value.Alone || pair.Value.Left != Blob(File.Exists(path) ? File.ReadAllText(path) : null);
      });
    }
  }

  /// <summary>Forgets the files a save committed.</summary>
  public static void Forget(VersionScope scope, string root, IEnumerable<string> files)
  {
    lock (gate)
    {
      Dictionary<string, Entry> entries = Load(scope, root);
      foreach (string file in files)
      {
        entries.Remove(file);
      }
      Store(scope, root, entries);
    }
  }

  /// <summary>A path as the repository names it. The root is the presentation's folder as the server spells it, which
  /// may differ from git's top, where symlinks are resolved.</summary>
  private static string RepositoryPath(VersionScope scope, string root, string path)
  {
    string anchor = Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
    string inside = Path.GetFullPath(Path.Combine(anchor, scope.Presentation, Path.GetRelativePath(root, path)));
    return Path.GetRelativePath(anchor, inside).Replace('\\', '/');
  }

  /// <summary>The file's blob in the last version, empty when it had none.</summary>
  private static string Saved(VersionScope scope, string file)
  {
    (int exit, string output, _) = Revisions.Run(scope.Top, "rev-parse", "--verify", "--quiet", $"HEAD:{file}");
    return exit == 0 ? output.Trim() : "";
  }

  /// <summary>Text hashed as git hashes a file, so it compares with the last version's blob.</summary>
  private static string Blob(string? text)
  {
    if (text is null)
    {
      return "";
    }
    byte[] body = new UTF8Encoding(false).GetBytes(text);
    byte[] header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
    return Convert.ToHexString(SHA1.HashData([.. header, .. body])).ToLowerInvariant();
  }

  private static Dictionary<string, Entry> Load(VersionScope scope, string root)
  {
    string file = Record(scope, root);
    try
    {
      return File.Exists(file)
        ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? []
        : [];
    }
    catch (JsonException)
    {
      return [];
    }
  }

  private static void Store(VersionScope scope, string root, Dictionary<string, Entry> entries)
  {
    string file = Record(scope, root);
    if (entries.Count == 0)
    {
      if (File.Exists(file))
      {
        File.Delete(file);
      }
      return;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    Files.Replace(file, JsonSerializer.Serialize(entries));
  }

  /// <summary>One record a presentation, since a repository may hold several.</summary>
  private static string Record(VersionScope scope, string root)
  {
    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.Presentation)))[..12];
    string path = Revisions.Run(root, "rev-parse", "--git-path", $"pintura/edits/{key.ToLowerInvariant()}.json")
      .Output.Trim();
    return Path.GetFullPath(Path.Combine(root, path));
  }


}