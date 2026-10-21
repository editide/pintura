using System.Reflection;
using System.Text;
using System.Text.Json;
using Pintura;

// Redirected output on Windows is encoded in the console's legacy code page, mangling symbols such as ← and → for
// whatever reads it. The console's own code page stays untouched, since changing it outlives the process.
if (OperatingSystem.IsWindows())
{
  if (Console.IsOutputRedirected)
  {
    Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
  }
  if (Console.IsErrorRedirected)
  {
    Console.SetError(Utf8Writer(Console.OpenStandardError()));
  }
}

// Also how a command fetches Chromium, in a child process, so its progress bars can be kept off the command's output.
if (args is [HeadlessBrowser.InstallArgument])
{
  return Microsoft.Playwright.Program.Main(["install", "chromium", "--only-shell"]);
}

if (args is [HeadlessServer.ServeArgument, string served])
{
  return await HeadlessServer.Serve(served, InstanceRegistry.Default);
}

try
{
  CommandLineOptions options;
  try
  {
    options = CommandLineOptions.Parse(args);
  }
  catch (ArgumentException exception)
  {
    Console.Error.WriteLine(exception.Message);
    CommandLineOptions.PrintUsage(Console.Error);
    return 1;
  }

  if (options.Help)
  {
    CommandLineOptions.PrintUsage(Console.Out);
    return 0;
  }

  if (options.Version)
  {
    Console.WriteLine(typeof(Starter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
      ?.InformationalVersion ?? "unknown");
    return 0;
  }

  if (options.New is string created)
  {
    try
    {
      Starter.Create(created);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
    Console.WriteLine($"created {created}; preview it with: pintura {created}");
    return 0;
  }

  if (options.Documentation is not null)
  {
    Documentation.Write(options.Documentation, Console.Out);
    return 0;
  }

  if (options.Account is AccountCommand command)
  {
    return command switch
    {
      AccountCommand.Login => AccountCommands.Login(Account.Default, Console.Out),
      AccountCommand.Logout => AccountCommands.Logout(Account.Default, Console.Out),
      _ => AccountCommands.Show(Account.Default, Console.Out)
    };
  }

  if (options.Import is not null)
  {
    return ImportCommand.Run(options.Import, options.ImportFolder!, options.ThemeOnly, Console.Out, options.Slides);
  }

  string root;
  if (options.Root is not null)
  {
    root = Path.GetFullPath(options.Root);
    if (!File.Exists(Path.Combine(root, "presentation.yaml")))
    {
      Console.Error.WriteLine($"no presentation.yaml in {root}");
      return 1;
    }
  }
  else
  {
    string start = Directory.GetCurrentDirectory();
    List<string> found = PresentationDiscovery.Find(start);
    if (found.Count == 0)
    {
      Console.Error.WriteLine($"no presentation found under {start}; pass a presentation folder.");
      CommandLineOptions.PrintUsage(Console.Error);
      return 1;
    }
    if (found.Count > 1)
    {
      Console.Error.WriteLine($"several presentations under {start}; pass the one you want:");
      foreach (string candidate in found)
      {
        Console.Error.WriteLine($"  {Path.GetRelativePath(start, candidate)}");
      }
      return 1;
    }
    root = found[0];
  }

  InstanceRegistry registry = InstanceRegistry.Default;
  if (options.Command == HeadlessCommand.Url)
  {
    ServerInstance? running = registry.Find(root);
    if (running is null)
    {
      Console.Error.WriteLine($"no server is running {root}.");
      return 1;
    }
    Console.WriteLine(running.Url);
    return 0;
  }

  if (options.Command is HeadlessCommand.Screenshot or HeadlessCommand.Check)
  {
    return await BrowserCommands.Run(root, options, registry, Console.Out);
  }

  if (options.Command == HeadlessCommand.Diff)
  {
    return DiffCommand.Run(root, options.Revision!, options.Json, Console.Out, options.Slides);
  }

  if (options.Command == HeadlessCommand.ExportHtml)
  {
    string file = Path.GetFullPath(options.Output!);
    File.WriteAllText(file, HtmlExport.Build(PresentationLoader.Load(root), PreviewServer.InterfaceFolder()));
    Console.WriteLine(file);
    return 0;
  }

  if (options.Command == HeadlessCommand.ExportPptx)
  {
    Presentation presentation = PresentationLoader.Load(root);
    Action<double>? progress = Console.IsErrorRedirected
      ? null
      : fraction => Console.Error.Write($"\rexporting {fraction:0%}");
    ExportReport report;
    try
    {
      report = Exporter.Write(presentation, options.Output!, ToolHosts.Default(), progress, only: options.Slides);
    }
    finally
    {
      if (progress is not null)
      {
        Console.Error.Write("\r\u001b[K");
      }
    }
    string reused = report.Reused > 0 ? $" ({report.Reused} unchanged since the last export)" : "";
    Console.WriteLine($"{report.File}: {report.Slides} slides{reused}, {report.Calls} operations, " +
      $"{report.Elapsed.TotalSeconds:0.0}s");
    if (report.Billed.Slides + report.Billed.Layouts + report.Billed.Masters > 0)
    {
      ExportBilling billed = report.Billed;
      string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
      string design = billed.Masters > 0
        ? $", {Count(billed.Layouts, "layout")}, {Count(billed.Masters, "master")}"
        : "";
      Console.WriteLine($"billed: {Count(billed.Slides, "slide")}{design}");
      if (billed.Masters > 0)
      {
        Console.WriteLine("the master and layouts bill when the deck is built from scratch, as on a first export or " +
          "after a theme or layout change");
      }
    }
    if (report.Edited is string aside)
    {
      Console.WriteLine($"the deck had been saved since the last export, so it was moved to {aside} and the " +
        "presentation exported afresh");
    }
    void List(string heading, IReadOnlyCollection<string> lines)
    {
      if (lines.Count > 0)
      {
        Console.WriteLine();
        Console.WriteLine(heading);
        foreach (string line in lines)
        {
          Console.WriteLine($"- {line}");
        }
      }
    }
    List("what the deck can't reproduce:", report.Gaps);
#if DEBUG
    List("workarounds:", report.Workarounds);
#endif
    return presentation.Slides.Any(slide => slide.Error is not null) ? 1 : 0;
  }

  if (options.Command is not null)
  {
    return HeadlessRunner.Run(root, options.Command.Value, options.Slide, options.Element, options.Json,
      Console.Out);
  }

  // dotnet watch restarts the process on edits it cannot apply in place, so only its first run opens a tab, and neither
  // reuse nor idle shutdown may end the development server it is supervising.
  bool watched = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";
  bool firstRun = Environment.GetEnvironmentVariable("DOTNET_WATCH_ITERATION") is null or "1";

  if (!watched && registry.Find(root) is ServerInstance existing)
  {
    if (options.PortSpecified && existing.Port != options.Port)
    {
      Console.Error.WriteLine($"{root} is already served at {existing.Url}.");
      return 1;
    }
    Console.WriteLine($"  already serving {root} at {existing.Url}");
    if (!options.NoOpen)
    {
      Browser.Open(existing.Url);
    }
    return 0;
  }

  string assets = PreviewServer.InterfaceFolder();
  using var store = new PresentationStore(root, assets, Console.Out);
  int port = PreviewServer.ChoosePort(options.Port, options.PortSpecified);
  var instance = new ServerInstance(Environment.ProcessId, root, port);
  WebApplication app = PreviewServer.Create(assets, instance.Url);
  using var viewers = new Viewers(TimeSpan.FromSeconds(10), () =>
  {
    Console.WriteLine($"  stopped   {DateTime.Now:HH:mm:ss}  no page open");
    app.Lifetime.StopApplication();
  });
  if (options.NoOpen && !watched)
  {
    viewers.Expect();
  }
  await using var headless = new HeadlessBrowser(instance.Url);
  PreviewServer.Map(app, store, headless, watched ? null : viewers);

  Console.WriteLine($"  {root}");
  Console.WriteLine($"  interface {assets}");
  Console.WriteLine($"  {store.Deck.Slides.Count} slides, watching for changes, serving http://localhost:{port}");
  await app.StartAsync();
  registry.Register(instance);
  try
  {
    if (firstRun && !options.NoOpen)
    {
      Browser.Open(instance.Url);
    }
    await app.WaitForShutdownAsync();
  }
  finally
  {
    registry.Unregister(instance);
  }
  return 0;
}
// One line rather than a stack trace, whatever a command meets; a debug build adds the trace for a bug.
catch (Exception exception)
{
  bool expected = exception is LoadException or BookException or ArgumentException or IOException or
    UnauthorizedAccessException or InvalidDataException or InvalidOperationException or HttpRequestException or
    JsonException or TimeoutException or OperationCanceledException;
  Console.Error.WriteLine(expected ? exception.Message : $"{exception.GetType().Name}: {exception.Message}");
#if DEBUG
  if (!expected)
  {
    Console.Error.WriteLine(exception.StackTrace);
  }
#endif
  return 1;
}

static TextWriter Utf8Writer(Stream stream)
{
  return TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
}

/// <summary>Holds the page's payload and rebuilds it when the presentation or its theme changes on disk. A load
/// error keeps the last good slides, so the page shows the error without losing its place.</summary>
public class PresentationStore : IDisposable
{
  private const int ReloadDelayMilliseconds = 120;
  private static readonly string[] SourceExtensions =
    [".yaml", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"];

  private readonly string root;
  private readonly TextWriter log;
  private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
  private readonly Dictionary<string, FileSystemWatcher> sourceWatchers =
    new Dictionary<string, FileSystemWatcher>(StringComparer.Ordinal);
  private readonly object gate = new object();
  private readonly object rebuildGate = new object();
  private readonly Timer reloadTimer;
  private Snapshot snapshot = new Snapshot(new DeckPayload(), new HashSet<string>(), null, null);
  private string? drawn;
  // Seeded from the clock, so a page that outlived a restart sees a new version and reloads.
  private long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  private long assetVersion;
  private long headVersion;
  private bool disposed;

  public PresentationStore(string root, string assets, TextWriter log)
  {
    this.root = root;
    this.log = log;
    reloadTimer = new Timer(_ => Rebuild(), null, Timeout.Infinite, Timeout.Infinite);
    Rebuild();
    watchers.Add(CreateWatcher(assets, OnAssetChanged));
    WatchGit();
  }

  public DeckPayload Deck => Volatile.Read(ref snapshot).Deck;

  public long Version => Interlocked.Read(ref version);

  /// <summary>The model behind <see cref="Deck"/>, for the inspector; null until a load succeeds.</summary>
  public Presentation? Presentation => Volatile.Read(ref snapshot).Presentation;

  /// <summary>What the theme view draws; null until a load succeeds.</summary>
  public ThemeViewPayload? Theme => Volatile.Read(ref snapshot).Theme;

  /// <summary>Bumped when the page's own files change; the browser reloads itself in response.</summary>
  public long AssetVersion => Interlocked.Read(ref assetVersion);

  /// <summary>Bumped when a branch or HEAD moves, by a commit or a checkout, which changes what the page compares
  /// against though no slide changed.</summary>
  public long HeadVersion => Interlocked.Read(ref headVersion);

  /// <summary>Reloads now rather than after the watcher's debounce. The version moves only if what the page draws
  /// changed, so an open tab doesn't redraw for nothing.</summary>
  public void Refresh()
  {
    Rebuild();
  }

  public string Root => root;

  /// <summary>Only pictures the presentation places and fonts its theme ships may be requested.</summary>
  public bool TryGetMedia(string path, out string? contentType)
  {
    contentType = ServedType(path);
    return contentType is not null && Volatile.Read(ref snapshot).Media.Contains(path) && File.Exists(path);
  }

  public static string? ContentType(string path)
  {
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".gif" => "image/gif",
      ".svg" => "image/svg+xml",
      ".webp" => "image/webp",
      _ => null
    };
  }

  /// <summary>A picture's type, or a font's, which the page loads but no slide may place.</summary>
  public static string? ServedType(string path)
  {
    return ContentType(path) ?? ThemeFonts.ContentType(path);
  }

  /// <summary>Every picture the theme, its layouts, and the slides place, and every font the theme ships.</summary>
  public static HashSet<string> Media(Presentation presentation)
  {
    return presentation.Theme.Elements
      .Concat(presentation.Theme.Layouts.Values.SelectMany(layout => layout.Elements))
      .Concat(presentation.Slides.SelectMany(slide => slide.Elements))
      .Select(element => element.PicturePath)
      .OfType<string>()
      .Concat(presentation.Theme.Faces.Select(face => face.Path))
      .ToHashSet(StringComparer.Ordinal);
  }

  public void Dispose()
  {
    lock (gate)
    {
      disposed = true;
      foreach (FileSystemWatcher watcher in watchers.Concat(sourceWatchers.Values))
      {
        watcher.Dispose();
      }
      watchers.Clear();
      sourceWatchers.Clear();
      reloadTimer.Dispose();
    }
    // Dispose can race a callback that already left the timer queue.
    lock (rebuildGate)
    {
      GC.SuppressFinalize(this);
    }
  }

  private FileSystemWatcher CreateWatcher(string path, FileSystemEventHandler handler, bool subdirectories = true)
  {
    var watcher = new FileSystemWatcher(path, "*")
    {
      IncludeSubdirectories = subdirectories,
      NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName |
        NotifyFilters.Size,
      EnableRaisingEvents = true
    };
    watcher.Changed += handler;
    watcher.Created += handler;
    watcher.Deleted += handler;
    watcher.Renamed += (sender, eventArgs) => handler(sender, eventArgs);
    watcher.Error += (_, _) => ScheduleReload();
    return watcher;
  }

  /// <summary>Watches the presentation and whichever theme and books it names, which can change with any save.
  /// </summary>
  private void RefreshSourceWatchers(IEnumerable<string> folders)
  {
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
      var wanted = folders.Where(Directory.Exists).SelectMany(Linked).ToHashSet(StringComparer.Ordinal);
      foreach (string folder in sourceWatchers.Keys.Where(folder => !wanted.Contains(folder)).ToList())
      {
        sourceWatchers[folder].Dispose();
        sourceWatchers.Remove(folder);
      }
      foreach (string folder in wanted.Where(folder => !sourceWatchers.ContainsKey(folder)))
      {
        try
        {
          sourceWatchers[folder] = CreateWatcher(folder, OnChanged);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
        }
      }
    }
  }

  /// <summary>The folder and the target of every linked folder inside it, which a watcher doesn't follow though
  /// the loader and Libreta do.</summary>
  private static List<string> Linked(string folder)
  {
    var folders = new List<string> { folder };
    try
    {
      foreach (string path in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
      {
        if (new DirectoryInfo(path).LinkTarget is not null &&
          Directory.ResolveLinkTarget(path, returnFinalTarget: true) is DirectoryInfo { Exists: true } target)
        {
          folders.Add(target.FullName);
        }
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
    }
    return folders;
  }

  /// <summary>Watches HEAD, packed-refs, and the refs folder rather than all of git's folder, whose objects churn.
  /// </summary>
  private void WatchGit()
  {
    foreach (string folder in Revisions.GitFolders(root))
    {
      try
      {
        watchers.Add(CreateWatcher(folder, OnGitChanged, subdirectories: false));
        string refs = Path.Combine(folder, "refs");
        if (Directory.Exists(refs))
        {
          watchers.Add(CreateWatcher(refs, (_, _) => Interlocked.Increment(ref headVersion)));
        }
      }
      catch (Exception exception) when (exception is ArgumentException or IOException)
      {
      }
    }
  }

  private void OnGitChanged(object sender, FileSystemEventArgs eventArgs)
  {
    if (eventArgs.Name is "HEAD" or "packed-refs")
    {
      Interlocked.Increment(ref headVersion);
    }
  }

  private void OnAssetChanged(object sender, FileSystemEventArgs eventArgs)
  {
    Interlocked.Increment(ref assetVersion);
  }

  private void OnChanged(object sender, FileSystemEventArgs eventArgs)
  {
    bool source = SourceExtensions.Contains(Path.GetExtension(eventArgs.FullPath).ToLowerInvariant()) ||
      Directory.Exists(eventArgs.FullPath) ||
      eventArgs.ChangeType is WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed;
    if (source)
    {
      ScheduleReload();
    }
  }

  private void ScheduleReload()
  {
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
      reloadTimer.Change(ReloadDelayMilliseconds, Timeout.Infinite);
    }
  }

  private void Rebuild()
  {
    lock (rebuildGate)
    {
      lock (gate)
      {
        if (disposed)
        {
          return;
        }
      }
      long next = Version + 1;
      Presentation presentation;
      DeckPayload deck;
      ThemeViewPayload theme;
      string json;
      try
      {
        presentation = PresentationLoader.Load(root);
        deck = PayloadBuilder.Build(presentation, next, MediaUrl);
        theme = ThemeView.Build(presentation, MediaUrl);
        // The theme view draws styles and formats no slide uses, so they count as drawn too.
        json = JsonSerializer.Serialize(deck with { Version = 0 }, PayloadJson.Options) +
          JsonSerializer.Serialize(theme, PayloadJson.Options);
      }
      // Anything at all, since a reload runs on a timer, where an exception would stop the server.
      catch (Exception exception)
      {
        string message = exception is LoadException
          ? exception.Message
          : $"couldn't load the presentation ({exception.Message.TrimEnd('.')})";
        Snapshot previous = Volatile.Read(ref snapshot);
        RefreshSourceWatchers([root, .. ThemeFolders()]);
        if (previous.Deck.Error == message)
        {
          return;
        }
        Volatile.Write(ref snapshot, previous with
        {
          Deck = previous.Deck with { Version = next, Error = message }
        });
        Interlocked.Exchange(ref version, next);
        log.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  {message}");
        return;
      }
      RefreshSourceWatchers([root, presentation.Theme.Root, .. presentation.Books.Values.Select(book => book.Root)]);
      HashSet<string> media = Media(presentation);
      if (json == drawn && Deck.Error is null)
      {
        // Lines can move without the drawing changing, and the inspector reports them.
        Volatile.Write(ref snapshot, Volatile.Read(ref snapshot) with { Presentation = presentation, Theme = theme });
        return;
      }
      drawn = json;
      Volatile.Write(ref snapshot, new Snapshot(deck, media, presentation, theme));
      Interlocked.Exchange(ref version, next);
      foreach (SlidePayload slide in deck.Slides.Where(slide => slide.Error is not null))
      {
        log.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  {slide.Error}");
      }
      log.WriteLine($"  reloaded  {DateTime.Now:HH:mm:ss}  {deck.Slides.Count} slides");
    }
  }

  // A presentation that fails to load still names its theme and books often enough to keep watching them.
  private List<string> ThemeFolders()
  {
    lock (gate)
    {
      return sourceWatchers.Keys.Where(folder => folder != root).ToList();
    }
  }

  public static string MediaUrl(string path, string? fill)
  {
    // The file's stamp, so a picture replaced in place changes the payload, which the page redraws, and the URL,
    // which the browser fetches again.
    var file = new FileInfo(path);
    string stamp = file.Exists ? $"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}" : "0";
    string url = $"api/media?path={Uri.EscapeDataString(path)}&v={stamp}";
    return fill is null ? url : $"{url}&fill={Uri.EscapeDataString(fill)}";
  }

  private record Snapshot(DeckPayload Deck, HashSet<string> Media, Presentation? Presentation,
    ThemeViewPayload? Theme);


}