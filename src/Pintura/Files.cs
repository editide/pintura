using System.Text;

namespace Pintura;

/// <summary>Writes a file whole or not at all: the new contents go to a temporary file beside it, which a rename then
/// puts in its place, so a crash midway leaves the file as it was rather than cut short.</summary>
public static class Files
{
  private const int Attempts = 3;

  /// <summary>Replaces <paramref name="path"/>, or creates it, keeping its permissions. A linked file stays a link,
  /// and the file it links to is the one replaced. A read-only file is refused, as writing to it would be.</summary>
  public static void Replace(string path, byte[] contents)
  {
    Replace(path, stream => stream.Write(contents));
  }

  /// <summary>Replaces <paramref name="path"/> with <paramref name="text"/> in UTF-8, with no byte order mark.</summary>
  public static void Replace(string path, string text)
  {
    Replace(path, new UTF8Encoding(false).GetBytes(text));
  }

  /// <summary>Replaces <paramref name="path"/> with what <paramref name="write"/> writes to the stream it's given.
  /// </summary>
  public static void Replace(string path, Action<Stream> write)
  {
    var file = new FileInfo(path);
    string target = file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is { } linked
      ? linked.FullName
      : file.FullName;
    if (ReadOnly(target))
    {
      throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
    }
    // A file that can't be swapped, as on Windows while another program holds it open, or in a folder that takes no
    // new files, is written in place, which a crash can cut short but nothing else refuses. A failure writing the new
    // contents, such as a full disk, would fail in place too, after cutting the file short, so it is thrown instead.
    if (!Swap(target, write))
    {
      using (var stream = new FileStream(target, FileMode.Create, FileAccess.Write))
      {
        write(stream);
      }
    }
  }

  private static bool Swap(string target, Action<Stream> write)
  {
    string temporary = Path.Combine(Path.GetDirectoryName(target)!,
      $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
    FileStream stream;
    try
    {
      stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
    try
    {
      using (stream)
      {
        write(stream);
        stream.Flush(flushToDisk: true);
      }
      if (!OperatingSystem.IsWindows() && File.Exists(target))
      {
        File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
      }
      return Move(temporary, target);
    }
    finally
    {
      try
      {
        File.Delete(temporary);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
      }
    }
  }

  // A rename replaces any file in a folder the user can write to, on Linux and macOS, so whether they could write the
  // file itself is asked of the system, which weighs its owner, group, and mode alike, by opening it without writing.
  private static bool ReadOnly(string target)
  {
    if (!File.Exists(target))
    {
      return false;
    }
    if (OperatingSystem.IsWindows())
    {
      return File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly);
    }
    try
    {
      using (new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
      {
        return false;
      }
    }
    catch (UnauthorizedAccessException)
    {
      return true;
    }
  }

  // Windows refuses to replace a file another program holds open, as a virus scanner or a sync client briefly does,
  // so the rename is tried again for a moment before the file is written in place instead.
  private static bool Move(string from, string to)
  {
    for (int attempt = 1; ; attempt++)
    {
      try
      {
        File.Move(from, to, overwrite: true);
        return true;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        if (attempt == Attempts)
        {
          return false;
        }
        Thread.Sleep(25 * attempt);
      }
    }
  }


}