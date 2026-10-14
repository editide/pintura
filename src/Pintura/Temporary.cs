using System.Runtime.Versioning;

namespace Pintura;

/// <summary>Where Pintura keeps what it can make again: reads of decks, drawn pictures, exports, and revisions.</summary>
public static class Temporary
{
  /// <summary>A folder only this user reaches, since what it holds comes from their decks. Linux shares /tmp between
  /// users, so there it is the user's cache folder; elsewhere the temporary folder is the user's own.</summary>
  public static string Folder(params string[] parts)
  {
    string root = OperatingSystem.IsLinux()
      ? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } cache
        ? cache
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "pintura")
      : Path.Combine(Path.GetTempPath(), "pintura");
    if (OperatingSystem.IsLinux())
    {
      Restrict(root);
    }
    return Path.Combine([root, .. parts]);
  }

  /// <summary>Makes <paramref name="root"/> for its user alone, as a cache folder can be readable by others.</summary>
  [UnsupportedOSPlatform("windows")]
  public static void Restrict(string root)
  {
    const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    if (!Directory.Exists(root))
    {
      Directory.CreateDirectory(root, owner);
    }
    else if (File.GetUnixFileMode(root) != owner)
    {
      File.SetUnixFileMode(root, owner);
    }
  }


}
