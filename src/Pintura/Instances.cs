using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pintura;

/// <summary>A running server. A headless one serves only <c>--screenshot</c> and <c>--check</c>. Either is borrowed
/// to draw only by the build that started it, since it loads slides with that build's code.</summary>
public record ServerInstance(int ProcessId, string Root, int Port, bool Headless = false, string? Build = null)
{
  [JsonIgnore]
  public string Url => $"http://localhost:{Port}";

  public static string CurrentBuild => typeof(ServerInstance).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}

/// <summary>One file per running server, so a second launch of the same presentation reuses the first rather than
/// starting another on the next port. A server killed without cleanup leaves its file for the next lookup to prune.
/// </summary>
public class InstanceRegistry
{
  private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
  private readonly string folder;

  public InstanceRegistry(string folder)
  {
    this.folder = folder;
  }

  public static InstanceRegistry Default { get; } = new InstanceRegistry(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
    "pintura", "servers"));

  public void Register(ServerInstance instance)
  {
    Directory.CreateDirectory(folder);
    string path = RecordPath(instance);
    string pending = $"{path}.tmp";
    File.WriteAllText(pending, JsonSerializer.Serialize(instance));
    File.Move(pending, path, true);
  }

  public void Unregister(ServerInstance instance)
  {
    File.Delete(RecordPath(instance));
  }

  public ServerInstance? Find(string root, bool headless = false)
  {
    if (!Directory.Exists(folder))
    {
      return null;
    }
    string wanted = Normalize(root);
    foreach (string path in Directory.GetFiles(folder, "*.json"))
    {
      ServerInstance? instance = Read(path);
      if (instance is null || !Answers(instance))
      {
        TryDelete(path);
        continue;
      }
      if (instance.Headless == headless && string.Equals(Normalize(instance.Root), wanted, PathComparison))
      {
        return instance;
      }
    }
    return null;
  }

  /// <summary>The server the process registered, once it answers.</summary>
  public ServerInstance? Find(int processId)
  {
    string path = Path.Combine(folder, $"{processId}.json");
    if (!File.Exists(path))
    {
      return null;
    }
    ServerInstance? instance = Read(path);
    return instance is not null && Answers(instance) ? instance : null;
  }

  private static StringComparison PathComparison =>
    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

  private string RecordPath(ServerInstance instance)
  {
    return Path.Combine(folder, $"{instance.ProcessId}.json");
  }

  private static string Normalize(string root)
  {
    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
  }

  private static ServerInstance? Read(string path)
  {
    try
    {
      return JsonSerializer.Deserialize<ServerInstance>(File.ReadAllText(path));
    }
    catch (Exception exception) when (exception is JsonException or IOException)
    {
      return null;
    }
  }

  // Both, because a dead server's process id or port can be reused by an unrelated program.
  private static bool Answers(ServerInstance instance)
  {
    try
    {
      using Process process = Process.GetProcessById(instance.ProcessId);
      if (process.HasExited)
      {
        return false;
      }
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
      return false;
    }

    using var client = new TcpClient();
    try
    {
      return client.ConnectAsync(IPAddress.Loopback, instance.Port).Wait(ConnectTimeout);
    }
    catch (AggregateException)
    {
      return false;
    }
  }

  private static void TryDelete(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
    }
  }


}

/// <summary>Counts the pages holding the event stream open and stops the server once the last has been gone for the
/// grace period, which a page reload reconnects well within. Nothing counts down until a first page connects.</summary>
public class Viewers : IDisposable
{
  private readonly object gate = new object();
  private readonly TimeSpan grace;
  private readonly Timer timer;
  private int count;
  private bool disposed;

  public Viewers(TimeSpan grace, Action stop)
  {
    this.grace = grace;
    timer = new Timer(_ => Expire(stop), null, Timeout.Infinite, Timeout.Infinite);
  }

  /// <summary>Counts down now rather than from a first page, for a server whose caller opens the page itself and
  /// may never do so.</summary>
  public void Expect()
  {
    lock (gate)
    {
      if (count == 0 && !disposed)
      {
        timer.Change(grace, Timeout.InfiniteTimeSpan);
      }
    }
  }

  public IDisposable Connect()
  {
    lock (gate)
    {
      count++;
      if (!disposed)
      {
        timer.Change(Timeout.Infinite, Timeout.Infinite);
      }
    }
    return new Connection(this);
  }

  public void Dispose()
  {
    lock (gate)
    {
      disposed = true;
      timer.Dispose();
    }
    GC.SuppressFinalize(this);
  }

  private void Disconnect()
  {
    lock (gate)
    {
      count--;
      if (count == 0 && !disposed)
      {
        timer.Change(grace, Timeout.InfiniteTimeSpan);
      }
    }
  }

  private void Expire(Action stop)
  {
    lock (gate)
    {
      if (count > 0)
      {
        return;
      }
    }
    stop();
  }

  private class Connection(Viewers viewers) : IDisposable
  {
    private int disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref disposed, 1) == 0)
      {
        viewers.Disconnect();
      }
    }


  }


}

public static class Browser
{
  public static void Open(string url)
  {
    foreach (string opener in Openers())
    {
      var start = new ProcessStartInfo(opener, url)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      try
      {
        Process.Start(start)?.Dispose();
        return;
      }
      catch (Win32Exception)
      {
      }
    }
    Console.WriteLine($"  could not open a browser; visit {url}");
  }

  // WSL's own xdg-open usually has no browser to hand the page to, so the Windows side opens it.
  private static IEnumerable<string> Openers()
  {
    if (OperatingSystem.IsWindows())
    {
      return ["explorer.exe"];
    }
    if (OperatingSystem.IsMacOS())
    {
      return ["open"];
    }
    if (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null)
    {
      return ["wslview", "explorer.exe"];
    }
    return ["xdg-open"];
  }


}