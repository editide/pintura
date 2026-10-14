namespace Pintura.Tests;

/// <summary>A fresh folder under the system temp directory, deleted on dispose.</summary>
public class TempFolder : IDisposable
{
  public TempFolder()
  {
    Root = Path.Combine(Path.GetTempPath(), "pintura-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Root);
  }

  public string Root { get; }

  public string PathTo(string relative)
  {
    return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
  }

  public void Write(string relative, string content)
  {
    string path = PathTo(relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.EndsWith('\n') ? content : $"{content}\n");
  }

  public void Dispose()
  {
    // Git leaves its objects read-only, which Windows refuses to delete.
    foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
    {
      File.SetAttributes(file, FileAttributes.Normal);
    }
    // A process a test left running, such as a fake libreta serving after it answered, may hold a file a moment longer.
    for (int attempt = 1; ; attempt++)
    {
      try
      {
        Directory.Delete(Root, true);
        return;
      }
      catch (IOException) when (attempt < 20)
      {
        Thread.Sleep(100);
      }
    }
  }


}