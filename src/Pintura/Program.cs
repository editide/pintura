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
    return ExportCommand.Html(root, options.Output!, Console.Out);
  }

  if (options.Command == HeadlessCommand.ExportPptx)
  {
    return ExportCommand.Pptx(root, options.Output!, options.Slides, Console.Out, Console.Error,
      interactive: !Console.IsErrorRedirected);
  }

  if (options.Command is not null)
  {
    return HeadlessRunner.Run(root, options.Command.Value, options.Slide, options.Element, options.Json,
      Console.Out, options.Resolved, options.Ids, options.Slides);
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
  using var store = new PresentationStore(root, assets, Console.Out, background: true);
  int port = PreviewServer.ChoosePort(options.Port, options.PortSpecified);
  var instance = new ServerInstance(Environment.ProcessId, root, port, Build: ServerInstance.CurrentBuild);
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
  await using var headless = new HeadlessBrowser(instance.Url, () => store.AssetVersion);
  PreviewServer.Map(app, store, headless, watched ? null : viewers);

  Console.WriteLine($"  {root}");
  Console.WriteLine($"  interface {assets}");
  Console.WriteLine($"  watching for changes, serving http://localhost:{port}");
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
  bool expected = exception is not ArgumentNullException and (LoadException or BookException or ArgumentException or
    IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
    HttpRequestException or JsonException or TimeoutException or OperationCanceledException);
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