using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Pintura;

/// <summary><c>--screenshot</c> and <c>--check</c>, which need the slides drawn. A server already running the
/// presentation lends its headless browser; otherwise a headless server is started, and kept for the next command.
/// </summary>
public static class BrowserCommands
{
  // Long enough for the first run to fetch Chromium.
  public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

  public static async Task<int> Run(string root, CommandLineOptions options, InstanceRegistry registry,
    TextWriter output)
  {
    if (options.Command == HeadlessCommand.Check && options.Revision is string revision)
    {
      return await CheckSince(root, revision, options, registry, output);
    }
    Presentation presentation;
    try
    {
      presentation = PresentationLoader.Load(root);
    }
    catch (LoadException exception) when (options.Command == HeadlessCommand.Check)
    {
      return Report([Checks.Structure(exception)], options.Json, output);
    }
    if (options.Revision?.Contains("..", StringComparison.Ordinal) == true)
    {
      throw new InvalidDataException("--screenshot --diff draws a revision beside the working tree, so it takes one " +
        "revision rather than two");
    }
    // A slide the change removed is only in the revision, which the page answers for.
    if (options.Command == HeadlessCommand.Screenshot && options.Slide is not ("all" or "theme") &&
      options.Revision is null)
    {
      HeadlessRunner.FindSlide(presentation, options.Slide!);
    }
    string url = await HeadlessServer.Find(root, registry);
    using var client = new HttpClient { BaseAddress = new Uri($"{url}/"), Timeout = Timeout };
    return options.Command == HeadlessCommand.Screenshot
      ? await Screenshot(client, presentation, root, options.Slide!, options.Revision, options.Output, output)
      : await Check(client, presentation, options, output);
  }

  private static async Task<int> Screenshot(HttpClient client, Presentation presentation, string root, string slide,
    string? revision, string? path, TextWriter output)
  {
    string query = $"slide={Uri.EscapeDataString(slide)}" +
      (revision is null ? "" : $"&base={Uri.EscapeDataString(revision)}");
    HttpResponseMessage response = await Get(client, $"api/screenshot?{query}");
    byte[] png = await response.Content.ReadAsByteArrayAsync();
    path = Path.GetFullPath(path ?? Path.Combine(Temporary.Folder("screenshots", Path.GetFileName(root)),
      revision is null ? $"{slide}.png" : $"{slide}.diff.png"));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllBytesAsync(path, png);
    output.WriteLine(path);

    if (slide == "theme" || (slide != "all" && presentation.Slides.All(candidate => candidate.Id != slide)))
    {
      return 0;
    }
    string slides = slide == "all" ? "" : $"?slides={Uri.EscapeDataString(slide)}";
    List<Finding> findings = Checks.All(presentation, await Measure(client, slides),
      FontSetting.Current(FontFolders.Files()), false);
    Write(findings.Where(finding => slide == "all" || finding.Slide is null || finding.Slide == slide), output);
    return 0;
  }

  private static async Task<int> Check(HttpClient client, Presentation presentation, CommandLineOptions options,
    TextWriter output)
  {
    List<Finding> findings = Checks.All(presentation, await Measure(client, ""),
      FontSetting.Current(FontFolders.Files()), options.Export);
    return Report(findings, options.Json, output);
  }

  /// <summary>The findings the change introduced, each side checked as <c>--check</c> checks it: a revision is drawn
  /// by a headless server of its own, over the folder it was extracted to. Exits 1 only on an error it introduced.
  /// </summary>
  private static async Task<int> CheckSince(string root, string revision, CommandLineOptions options,
    InstanceRegistry registry, TextWriter output)
  {
    string[] ends = revision.Split("..", 2);
    string first = ends[0].Length == 0 ? "HEAD" : ends[0];
    async Task<List<Finding>> Findings(string? name)
    {
      string drawn = root;
      Presentation presentation;
      try
      {
        if (name is null)
        {
          presentation = PresentationLoader.Load(root);
        }
        else
        {
          Revision checkout = Revisions.Checkout(root, name, Directory.GetCurrentDirectory());
          drawn = checkout.Root;
          presentation = Revisions.Load(checkout);
        }
      }
      catch (LoadException exception)
      {
        return [Checks.Structure(exception)];
      }
      string url = await HeadlessServer.Find(drawn, registry);
      using var client = new HttpClient { BaseAddress = new Uri($"{url}/"), Timeout = Timeout };
      return Checks.All(presentation, await Measure(client, ""), FontSetting.Current(FontFolders.Files()),
        options.Export);
    }
    List<Finding> before = await Findings(first);
    List<Finding> after = await Findings(ends.Length == 1 ? null : ends[1]);
    (List<Finding> introduced, List<Finding> gone) = Checks.Since(before, after);

    string since = $"{first} ({Revisions.Commit(root, first)[..7]})";
    int errors = introduced.Count(finding => finding.Level == "error");
    int warnings = introduced.Count - errors;
    if (options.Json)
    {
      output.WriteLine(JsonSerializer.Serialize(new { since, errors, warnings, findings = introduced, gone },
        PayloadJson.Indented));
    }
    else
    {
      Write(introduced, output);
      string cleared = gone.Count == 0 ? "" : $"; {gone.Count} {(gone.Count == 1 ? "finding" : "findings")} gone";
      output.WriteLine(introduced.Count == 0
        ? $"no findings introduced since {since}{cleared}"
        : $"{errors} {(errors == 1 ? "error" : "errors")}, {warnings} {(warnings == 1 ? "warning" : "warnings")} " +
          $"introduced since {since}{cleared}");
    }
    return errors > 0 ? 1 : 0;
  }

  /// <summary>Exits 1 on errors, as Libreta's <c>--check</c> does; warnings alone exit 0.</summary>
  private static int Report(List<Finding> findings, bool json, TextWriter output)
  {
    int errors = findings.Count(finding => finding.Level == "error");
    int warnings = findings.Count - errors;
    if (json)
    {
      output.WriteLine(JsonSerializer.Serialize(new { errors, warnings, findings }, PayloadJson.Indented));
    }
    else
    {
      Write(findings, output);
      output.WriteLine(findings.Count == 0
        ? "no findings"
        : $"{errors} {(errors == 1 ? "error" : "errors")}, {warnings} {(warnings == 1 ? "warning" : "warnings")}");
    }
    return errors > 0 ? 1 : 0;
  }

  private static void Write(IEnumerable<Finding> findings, TextWriter output)
  {
    List<Finding> list = findings.ToList();
    int width = list.Count == 0 ? 0 : list.Max(finding => finding.Where.Length);
    foreach (Finding finding in list)
    {
      output.WriteLine($"{finding.Level,-8} {finding.Where.PadRight(width)}  {finding}");
    }
  }

  public static async Task<Measurement> Measure(HttpClient client, string query)
  {
    HttpResponseMessage response = await Get(client, $"api/measure{query}");
    return (await response.Content.ReadFromJsonAsync<Measurement>(PayloadJson.Options))!;
  }

  /// <summary>A failed request carries the server's reason, which becomes the command's error.</summary>
  private static async Task<HttpResponseMessage> Get(HttpClient client, string path)
  {
    HttpResponseMessage response = await client.GetAsync(path);
    if (!response.IsSuccessStatusCode)
    {
      string reason = await response.Content.ReadAsStringAsync();
      throw new InvalidDataException(reason.Length > 0
        ? reason
        : $"{client.BaseAddress} answered {(int)response.StatusCode} to {path}; restart it if it predates this " +
          "command.");
    }
    return response;
  }


}

/// <summary>A server with no tab, started in the background by the first <c>--screenshot</c> or <c>--check</c> and
/// kept until it has been idle a few minutes, so the next command skips the browser's start.</summary>
public static class HeadlessServer
{
  public const string ServeArgument = "--headless-server";
  private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

  public static string LogFolder => Temporary.Folder();

  /// <summary>The address of a server that can draw the presentation, starting one when none runs.</summary>
  public static async Task<string> Find(string root, InstanceRegistry registry)
  {
    if (registry.Find(root) is ServerInstance preview)
    {
      return preview.Url;
    }
    if (registry.Find(root, headless: true) is ServerInstance headless)
    {
      if (headless.Build == ServerInstance.CurrentBuild)
      {
        return headless.Url;
      }
      Stop(headless);
    }
    return await Start(root, registry);
  }

  /// <summary>Runs in the child process: serves until idle, registered for the command that started it to find, and
  /// writing to its log.</summary>
  public static async Task<int> Serve(string root, InstanceRegistry registry)
  {
    string log = LogPath(LogFolder, Environment.ProcessId);
    TextWriter? writer = null;
    // On Windows the server has a hidden console of its own, so it writes its log itself.
    if (!Console.IsOutputRedirected)
    {
      Directory.CreateDirectory(LogFolder);
      var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
      writer = TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
      Console.SetOut(writer);
      Console.SetError(writer);
    }
    try
    {
      await Run(root, registry);
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception);
      writer?.Dispose();
      return 1;
    }
    writer?.Dispose();
    File.Delete(log);
    return 0;
  }

  private static async Task Run(string root, InstanceRegistry registry)
  {
    string assets = PreviewServer.InterfaceFolder();
    using var store = new PresentationStore(root, assets, TextWriter.Null);
    var instance = new ServerInstance(Environment.ProcessId, root, PreviewServer.FreePort(), true,
      ServerInstance.CurrentBuild);
    string url = $"http://127.0.0.1:{instance.Port}";
    WebApplication app = PreviewServer.Create(assets, url);
    using var requests = new Viewers(Idle, app.Lifetime.StopApplication);
    // Arms the countdown, in case the command that started this server never makes its request.
    requests.Connect().Dispose();
    app.Use(async (context, next) =>
    {
      using IDisposable request = requests.Connect();
      await next(context);
    });
    await using var headless = new HeadlessBrowser(url);
    PreviewServer.Map(app, store, headless, null);
    await app.StartAsync();
    registry.Register(instance);
    try
    {
      await app.WaitForShutdownAsync();
    }
    finally
    {
      registry.Unregister(instance);
    }
  }

  private static async Task<string> Start(string root, InstanceRegistry registry)
  {
    Directory.CreateDirectory(LogFolder);
    using Process process = Process.Start(StartInfo(root, LogFolder))!;
    string log = LogPath(LogFolder, process.Id);
    Relay(log);
    return await WaitForRegistration(process, registry, log);
  }

  /// <summary>Starts the server holding none of the command's handles, since a shell waits until everything holding
  /// the command's output has closed it. Windows' CreateProcess hands the child every inheritable handle, whatever
  /// its standard streams are, so the server starts through ShellExecuteEx, which hands it none. Elsewhere only the
  /// standard streams are inherited, and a shell swaps them for /dev/null and the log before becoming the server.
  /// </summary>
  public static ProcessStartInfo StartInfo(string root, string logFolder)
  {
    string executable = Environment.ProcessPath!;
    ProcessStartInfo start;
    if (OperatingSystem.IsWindows())
    {
      start = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
    }
    else
    {
      start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
      start.ArgumentList.Add("-c");
      // exec keeps the process id, so $$ names the file LogPath does.
      start.ArgumentList.Add("exec \"$@\" </dev/null >>\"$0/headless-$$.log\" 2>&1");
      start.ArgumentList.Add(logFolder);
      start.ArgumentList.Add(executable);
    }
    if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
    {
      start.ArgumentList.Add(typeof(HeadlessServer).Assembly.Location);
    }
    start.ArgumentList.Add(ServeArgument);
    start.ArgumentList.Add(root);
    return start;
  }

  private static string LogPath(string folder, int processId)
  {
    return Path.Combine(folder, $"headless-{processId}.log");
  }

  /// <summary>The address the started server registers, once it answers there.</summary>
  public static async Task<string> WaitForRegistration(Process process, InstanceRegistry registry, string log)
  {
    Stopwatch waited = Stopwatch.StartNew();
    while (waited.Elapsed < StartTimeout)
    {
      if (registry.Find(process.Id) is ServerInstance instance)
      {
        return instance.Url;
      }
      if (process.HasExited)
      {
        throw new InvalidDataException($"the headless server stopped before it started; see {log}.");
      }
      await Task.Delay(Poll);
    }
    process.Kill();
    throw new InvalidDataException(
      $"the headless server didn't start within {StartTimeout.TotalSeconds:0}s; see {log}.");
  }

  // For as long as the command runs, which shows Chromium's first fetch.
  private static void Relay(string log)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        while (!File.Exists(log))
        {
          await Task.Delay(Poll);
        }
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (true)
        {
          string text = await reader.ReadToEndAsync();
          if (text.Length > 0)
          {
            Console.Error.Write(text);
          }
          await Task.Delay(Poll);
        }
      }
      catch (IOException)
      {
      }
    });
  }

  private static void Stop(ServerInstance instance)
  {
    try
    {
      using Process process = Process.GetProcessById(instance.ProcessId);
      process.Kill();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
    }
  }


}