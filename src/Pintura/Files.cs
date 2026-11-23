using System.Text;

namespace Pintura;

/// <summary>Writes a file whole or not at all: the new contents go to a temporary file beside it, which a rename then
/// puts in its place, so a crash midway leaves the file as it was rather than cut short.</summary>
public static class Files
{
  private const int Attempts = 3;

  /// <summary>Serializes the server's and helpers' writes to a presentation's files so concurrent edits, moves,
  /// comments, and picks never interleave on the same file.</summary>
  public static readonly object Gate = new object();

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

/// <summary>One multi-file write that remembers each file's bytes before it touched them, refuses to overwrite a
/// file that changed on disk since it was read, and puts back only the files still holding what it wrote.</summary>
public sealed class FileTransaction
{
  private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];

  private readonly Dictionary<string, byte[]?> originals = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
  private readonly Dictionary<string, byte[]?> written = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

  /// <summary>Each touched file's text before the transaction, without any UTF-8 BOM, or null when it didn't exist.
  /// </summary>
  public Dictionary<string, string?> Before { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);

  /// <summary>Reads <paramref name="path"/> and remembers its original bytes the first time it is seen.</summary>
  public string? Read(string path)
  {
    byte[]? bytes = Bytes(path);
    string? text = Decode(bytes);
    originals.TryAdd(path, bytes);
    Before.TryAdd(path, text);
    return text;
  }

  /// <summary>Remembers <paramref name="path"/> as it stands now, so a later rollback puts it back.</summary>
  public void Track(string path)
  {
    if (!originals.ContainsKey(path))
    {
      Read(path);
    }
  }

  /// <summary>Writes <paramref name="text"/> to <paramref name="path"/>, or deletes it when null, preserving its BOM
  /// and CRLF line endings. Refuses if the file changed on disk since this transaction last read or wrote it.</summary>
  public void Write(string path, string? text, string conflict = "the edit")
  {
    Track(path);
    byte[]? expected = written.TryGetValue(path, out byte[]? last) ? last : originals[path];
    byte[]? current = Bytes(path);
    if (!Same(current, expected))
    {
      throw new PatchException($"{Path.GetFileName(path)} changed while {conflict} was written", true);
    }
    if (text is null)
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
      written[path] = null;
      return;
    }
    string bodyText = Before[path] is string before ? Patcher.KeepLineEndings(before, text) : text;
    byte[] body = Encoding.UTF8.GetBytes(bodyText);
    bool marked = originals[path] is byte[] original && original.AsSpan().StartsWith(ByteOrderMark);
    byte[] bytes = marked ? [.. ByteOrderMark, .. body] : body;
    if (!Same(current, bytes))
    {
      Files.Replace(path, bytes);
    }
    written[path] = bytes;
  }

  /// <summary>Puts every touched file back as it was before the transaction, skipping any file that changed on disk
  /// after this transaction wrote it so an agent's concurrent save is never erased.</summary>
  public void Restore()
  {
    foreach ((string path, byte[]? original) in originals)
    {
      try
      {
        byte[]? current = Bytes(path);
        if (Same(current, original))
        {
          continue;
        }
        if (written.TryGetValue(path, out byte[]? ours) && !Same(current, ours))
        {
          continue;
        }
        if (original is null)
        {
          File.Delete(path);
        }
        else
        {
          Files.Replace(path, original);
        }
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
      }
    }
  }

  private static byte[]? Bytes(string path)
  {
    return File.Exists(path) ? File.ReadAllBytes(path) : null;
  }

  private static string? Decode(byte[]? bytes)
  {
    if (bytes is null)
    {
      return null;
    }
    return Encoding.UTF8.GetString(bytes.AsSpan().StartsWith(ByteOrderMark) ? bytes[3..] : bytes);
  }

  private static bool Same(byte[]? first, byte[]? second)
  {
    return first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
  }


}