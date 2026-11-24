using System.Text.Json;

namespace Pintura;

/// <summary>Holds the page's payload and rebuilds it when the presentation or its theme changes on disk. A load
/// error keeps the last good slides, so the page shows the error without losing its place.</summary>
public class PresentationStore : IDisposable
{
  private const int ReloadDelayMilliseconds = 120;
  private static readonly string[] SourceExtensions =
    [".yaml", ".csv", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp"];

  private readonly string root;
  private readonly TextWriter log;
  private readonly IBookReader? books;
  private readonly TaskCompletionSource ready =
    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
  private readonly Dictionary<string, FileSystemWatcher> sourceWatchers =
    new Dictionary<string, FileSystemWatcher>(StringComparer.Ordinal);
  private readonly object gate = new object();
  private readonly object rebuildGate = new object();
  private readonly Timer reloadTimer;
  private Snapshot snapshot = new Snapshot(new DeckPayload(),
    JsonSerializer.Serialize(new DeckPayload(), PayloadJson.Options), new HashSet<string>(), null, null);
  private string? drawn;
  // Seeded from the clock, so a page that outlived a restart sees a new version and reloads.
  private long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  private long assetVersion;
  private long headVersion;
  private long changeVersion;
  private int dirty = 1;
  private bool disposed;

  /// <param name="background">Loads on another thread, so the preview can open while a large book is read, and
  /// <see cref="Ready"/> says when the first load ends.</param>
  public PresentationStore(string root, string assets, TextWriter log, IBookReader? books = null,
    bool background = false)
  {
    this.root = root;
    this.log = log;
    this.books = books;
    reloadTimer = new Timer(_ => Rebuild(), null, Timeout.Infinite, Timeout.Infinite);
    if (!background)
    {
      Rebuild();
    }
    watchers.Add(CreateWatcher(assets, OnAssetChanged));
    WatchGit();
    if (background)
    {
      Task.Run(Rebuild);
    }
  }

  public Task Ready => ready.Task;

  public DeckPayload Deck => Volatile.Read(ref snapshot).Deck;

  /// <summary>The current <see cref="Deck"/> already serialized for <c>/api/presentation</c>.</summary>
  public string DeckJson => Volatile.Read(ref snapshot).DeckJson;

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

  /// <summary>Bumped on every file change or reload, even when what the page draws doesn't change, so git status
  /// caches don't stay stale.</summary>
  public long ChangeVersion => Interlocked.Read(ref changeVersion);

  /// <summary>Reloads now rather than after the watcher's debounce. The version moves only if what the page draws
  /// changed, so an open tab doesn't redraw for nothing.</summary>
  public void Refresh()
  {
    Rebuild();
  }

  /// <summary>Reloads only when a watcher saw a change since the last build.</summary>
  public void RefreshIfDirty()
  {
    if (Volatile.Read(ref dirty) != 0)
    {
      Rebuild();
    }
  }

  public string Root => root;

  /// <summary>Only pictures the presentation places and fonts its theme ships may be requested.</summary>
  public bool TryGetMedia(string path, out string? contentType)
  {
    contentType = ServedType(path);
    return contentType is not null && Volatile.Read(ref snapshot).Media.Contains(path) && File.Exists(path);
  }

  /// <summary>A picture's type, or a font's, which the page loads but no slide may place.</summary>
  public static string? ServedType(string path)
  {
    return Pictures.ContentType(path) ?? ThemeFonts.ContentType(path);
  }

  /// <summary>Every picture the layouts, the slides and the variants of open picks place, and every font the theme
  /// ships.</summary>
  public static HashSet<string> Media(Presentation presentation)
  {
    return presentation.Theme.Layouts.Values.SelectMany(layout => layout.Elements)
      .Concat(presentation.Offered.SelectMany(slide => slide.Elements))
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
      HashSet<string> wanted = folders.Where(Directory.Exists).SelectMany(Linked).ToHashSet(StringComparer.Ordinal);
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
    Interlocked.Increment(ref changeVersion);
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
    Interlocked.Exchange(ref dirty, 1);
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
      try
      {
        Load();
      }
      finally
      {
        ready.TrySetResult();
      }
    }
  }

  private void Load()
  {
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
    }
    Interlocked.Exchange(ref dirty, 0);
    Interlocked.Increment(ref changeVersion);
    long next = Version + 1;
    Presentation presentation;
    DeckPayload deck;
    ThemeViewPayload theme;
    string deckJson;
    string json;
    try
    {
      presentation = PresentationLoader.Load(root, books: books);
      var scenes = new Dictionary<string, SlideScene>(StringComparer.Ordinal);
      deck = PayloadBuilder.Build(presentation, next, MediaUrl, scenes);
      theme = ThemeView.Build(presentation, MediaUrl, scenes);
      deckJson = JsonSerializer.Serialize(deck, PayloadJson.Options);
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
      DeckPayload errored = previous.Deck with { Version = next, Error = message };
      Volatile.Write(ref snapshot, previous with
      {
        Deck = errored,
        DeckJson = JsonSerializer.Serialize(errored, PayloadJson.Options)
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
    Volatile.Write(ref snapshot, new Snapshot(deck, deckJson, media, presentation, theme));
    Interlocked.Exchange(ref version, next);
    foreach (SlidePayload slide in deck.Slides.Where(slide => slide.Error is not null))
    {
      log.WriteLine($"  error     {DateTime.Now:HH:mm:ss}  {slide.Error}");
    }
    log.WriteLine($"  reloaded  {DateTime.Now:HH:mm:ss}  {deck.Slides.Count} slides");
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
    return MediaUrl(DiskFiles.Instance, path, fill);
  }

  /// <summary>A picture's or font's address, naming the revision it's read at when that isn't the working tree.
  /// </summary>
  public static string MediaUrl(IFiles files, string path, string? fill)
  {
    // The file's stamp, so a picture replaced in place changes the payload, which the page redraws, and the URL,
    // which the browser fetches again.
    string url = $"api/media?path={Uri.EscapeDataString(path)}&v={Uri.EscapeDataString(files.Stamp(path))}";
    if (files is GitTree tree)
    {
      url += $"&at={tree.Commit}";
    }
    return fill is null ? url : $"{url}&fill={Uri.EscapeDataString(fill)}";
  }

  private record Snapshot(DeckPayload Deck, string DeckJson, HashSet<string> Media, Presentation? Presentation,
    ThemeViewPayload? Theme);


}
