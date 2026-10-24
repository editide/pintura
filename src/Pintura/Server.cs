using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Pintura;

public static partial class PreviewServer
{
  private const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

  public static WebApplication Create(string assets, string url)
  {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
      ContentRootPath = AppContext.BaseDirectory,
      WebRootPath = assets
    });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(url);
    return builder.Build();
  }

  /// <summary>The page, the presentation, and the headless browser. Without viewers, no page keeps the server alive.
  /// </summary>
  public static void Map(WebApplication app, PresentationStore store, HeadlessBrowser headless, Viewers? viewers)
  {
    // Any web page can send this server requests, and a name rebound to this machine can read its answers too.
    app.Use(async (context, next) =>
    {
      if (!FromThisMachine(context.Request))
      {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
      }
      await next(context);
    });
    app.UseDefaultFiles();
    // Without a Cache-Control, a browser reuses a script it judges fresh, and a long-lived headless page draws with
    // an edited file's old version.
    app.UseStaticFiles(new StaticFileOptions
    {
      OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache"
    });

    app.MapGet("/api/presentation", () => Results.Text(
      JsonSerializer.Serialize(store.Deck, PayloadJson.Options), "application/json"));
    app.MapGet("/api/theme", () => store.Theme is ThemeViewPayload theme
      ? Results.Json(theme, PayloadJson.Options)
      : Results.NotFound());
    app.MapGet("/api/element", (string? slide, string? layout, int index) =>
      store.Presentation is Presentation presentation && (layout is null
        ? slide is null ? null : HeadlessRunner.Detail(presentation, slide, index)
        : HeadlessRunner.LayoutDetail(presentation, layout, index)) is { } detail
        ? Results.Json(detail, PayloadJson.Options)
        : Results.NotFound());
    app.MapPost("/api/edit", (EditRequest request) => Edit(store, request));
    app.MapPost("/api/move", (SlideMove move) => Move(store, move));
    app.MapPost("/api/comments", (CommentChange change) => Comment(store, change));
    app.MapPost("/api/trace", (TracePayload trace) => Trace(store, trace));
    var comparison = new Comparison(store);
    app.MapGet("/api/compare", (string? @base) => comparison.Answer(@base));
    app.MapPost("/api/revert", (RevertRequest request) => comparison.Revert(request));
    app.MapGet("/api/versions", () => Results.Json(Revisions.Versions(store.Root), PayloadJson.Options));
    app.MapGet("/api/save", () => Saving(() => Saves.Status(store.Root)));
    app.MapGet("/api/save/draft", () => Saving(() =>
    {
      store.Refresh();
      return new { Message = store.Deck.Error is null ? Saves.Draft(store.Root, store.Presentation) : "" };
    }));
    app.MapPost("/api/save", (SaveRequest request) => Saving(() =>
    {
      // A nudge just made may still be in the watcher's debounce, and the draft must describe it.
      store.Refresh();
      return Saves.Save(store.Root, request.Message, store.Deck.Error is null ? store.Presentation : null);
    }));
    app.MapGet("/api/media", (HttpContext context, string path, string? fill) =>
    {
      if (!store.TryGetMedia(path, out string? contentType) && !comparison.TryGetMedia(path, out contentType))
      {
        return Results.NotFound();
      }
      // An SVG opened on its own would otherwise run its script as this page, with every endpoint at hand.
      context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
      context.Response.Headers.XContentTypeOptions = "nosniff";
      return fill is null || !PresentationLoader.Svg(path)
        ? Results.File(path, contentType)
        : SvgPaint.IsHex(fill)
          ? Results.Text(SvgPaint.Recolor(File.ReadAllText(path), fill), contentType)
          : Results.BadRequest();
    });

    // The page holds this open and re-fetches whenever the presentation changes on disk or git's HEAD moves.
    app.MapGet("/api/events", async (HttpContext context, CancellationToken aborted) =>
    {
      // A stream left open would otherwise hold the server's shutdown for its whole timeout.
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, app.Lifetime.ApplicationStopping);
      CancellationToken cancellation = linked.Token;
      context.Response.Headers.ContentType = "text/event-stream";
      context.Response.Headers.CacheControl = "no-cache";
      using IDisposable? viewer = viewers?.Connect();
      long seen = store.Version;
      long seenAssets = store.AssetVersion;
      long seenHead = store.HeadVersion;
      await context.Response.WriteAsync($"data: {seen}\n\n", cancellation);
      await context.Response.Body.FlushAsync(cancellation);
      while (!cancellation.IsCancellationRequested)
      {
        if (store.AssetVersion != seenAssets)
        {
          seenAssets = store.AssetVersion;
          await context.Response.WriteAsync($"event: assets\ndata: {seenAssets}\n\n", cancellation);
          await context.Response.Body.FlushAsync(cancellation);
        }
        if (store.HeadVersion != seenHead)
        {
          seenHead = store.HeadVersion;
          await context.Response.WriteAsync($"event: head\ndata: {seenHead}\n\n", cancellation);
          await context.Response.Body.FlushAsync(cancellation);
        }
        if (store.Version != seen)
        {
          seen = store.Version;
          await context.Response.WriteAsync($"data: {seen}\n\n", cancellation);
          await context.Response.Body.FlushAsync(cancellation);
        }
        try
        {
          await Task.Delay(200, cancellation);
        }
        catch (OperationCanceledException)
        {
          return;
        }
      }
    });

    // Both reload first: a command run straight after a save must not see the slides from before it, which the
    // watcher's debounce would otherwise still be serving.
    app.MapGet("/api/screenshot", async (string slide, string? @base) =>
    {
      store.Refresh();
      if (@base is not null && slide == "theme")
      {
        return Results.Text("--diff compares slides, not the theme view", statusCode: StatusCodes.Status400BadRequest);
      }
      if (@base is not null)
      {
        return await Headless(async () =>
        {
          (byte[]? png, string? problem) = await headless.Compare(slide == "all" ? null : slide, @base);
          return png is null
            ? Results.Text(problem!, statusCode: StatusCodes.Status404NotFound)
            : Results.File(png, "image/png");
        });
      }
      if (slide == "theme")
      {
        return await Headless(async () => Results.File(await headless.ThemeScreenshot(), "image/png"));
      }
      if (slide != "all" && store.Deck.Slides.All(candidate => candidate.Id != slide))
      {
        return Results.Text($"no slide '{slide}'", statusCode: StatusCodes.Status404NotFound);
      }
      return await Headless(async () => Results.File(await headless.Screenshot(slide == "all" ? null : slide),
        "image/png"));
    });
    var progress = new ExportProgress();
    app.MapGet("/api/export/progress", () => Results.Json(new { fraction = progress.Fraction }));
    // A POST, since a .pptx export is billed, and any page can make a browser GET.
    app.MapPost("/api/export/{format}", (HttpContext context, string format) =>
      Export(context, store, headless, app.Environment.WebRootPath, format, progress));
    app.MapGet("/api/measure", async (string? slides) =>
    {
      store.Refresh();
      string[]? wanted = slides?.Split(',', StringSplitOptions.RemoveEmptyEntries);
      return await Headless(async () => Results.Json(await headless.Measure(wanted), PayloadJson.Options));
    });
    app.MapGet("/api/findings", async (string slide) =>
    {
      store.Refresh();
      if (store.Presentation is not Presentation presentation)
      {
        return Results.Json(Array.Empty<Finding>(), PayloadJson.Options);
      }
      return await Headless(async () =>
      {
        Measurement measurement = await headless.Measure([slide]);
        List<Finding> findings = Checks.All(presentation, measurement, FontSetting.Current(FontFolders.Files()), false);
        return Results.Json(findings.Where(finding => finding.Slide == slide), PayloadJson.Options);
      });
    });
  }

  /// <summary>The presentation as a download, named for its folder and built from the files as they are now, as the
  /// command line's exports build it. A failure answers with its message for the page to show.</summary>
  private static async Task<IResult> Export(HttpContext context, PresentationStore store, HeadlessBrowser headless,
    string assets, string format, ExportProgress progress)
  {
    if (format is not ("html" or "pptx" or "pdf"))
    {
      return Refused($"there is no {format} export; choose html, pptx, or pdf", StatusCodes.Status404NotFound);
    }
    store.Refresh();
    if (store.Presentation is not Presentation presentation || store.Deck.Error is not null)
    {
      return Refused("the presentation doesn't load; fix its error first", StatusCodes.Status409Conflict);
    }
    string name = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(presentation.Root))}.{format}";
    try
    {
      if (format == "html")
      {
        string html = await Task.Run(() => HtmlExport.Build(presentation, assets));
        return Results.File(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", name);
      }
      if (format == "pptx")
      {
        // The deck would leave a slide that doesn't load out, with nothing in it to say so.
        if (presentation.Slides.Where(slide => slide.Error is not null).Select(slide => slide.Id).ToList() is
          [_, ..] broken)
        {
          return Refused($"{string.Join(", ", broken)} doesn't load; fix it first", StatusCodes.Status409Conflict);
        }
        (byte[] bytes, ExportReport report) = await Task.Run(() => Pptx(presentation, name, progress));
        if (report.Gaps.Count > 0)
        {
          context.Response.Headers["Pintura-Findings"] = Uri.EscapeDataString(JsonSerializer.Serialize(report.Gaps));
        }
        return Results.File(bytes, PptxType, name);
      }
      return Results.File(await headless.Print(store.Deck.Size), "application/pdf", name);
    }
    catch (PlaywrightException exception)
    {
      return Refused($"headless Chromium failed: {exception.Message}", StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or
      UnauthorizedAccessException or ArgumentException or InvalidOperationException or BookException)
    {
      return Refused(exception.Message, StatusCodes.Status500InternalServerError);
    }
  }

  /// <summary>Exports over the presentation's last export, kept in the temporary folder, so pressing Export again
  /// rewrites only the slides that changed. One export runs at a time.</summary>
  private static (byte[] Bytes, ExportReport Report) Pptx(Presentation presentation, string name,
    ExportProgress progress)
  {
    string root = Path.GetFullPath(presentation.Root);
    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12].ToLowerInvariant();
    string deck = Temporary.Folder("exports", key, name);
    lock (progress)
    {
      progress.Fraction = 0;
      try
      {
        ExportReport report = Exporter.Write(presentation, deck, ToolHosts.Default(),
          fraction => progress.Fraction = fraction);
        return (File.ReadAllBytes(report.File), report);
      }
      finally
      {
        progress.Fraction = null;
      }
    }
  }

  /// <summary>Patches the slide's file and reloads, so the page's next fetch draws the edit. A conflict leaves the
  /// file alone for the page to redraw from.</summary>
  private static IResult Edit(PresentationStore store, EditRequest request)
  {
    if (store.Presentation is not Presentation presentation || store.Deck.Error is not null)
    {
      return Refused("the presentation doesn't load; fix its error first", StatusCodes.Status409Conflict);
    }
    if (request.Edits.Count == 0)
    {
      return Refused("the edit carries no values", StatusCodes.Status400BadRequest);
    }
    var files = new List<(string Path, string? Name, List<ValueEdit> Edits)>();
    foreach (IGrouping<string?, ValueEdit> group in request.Edits.GroupBy(edit => edit.File))
    {
      bool listing = group.Key == presentation.Origin.File;
      string? path = listing ? Path.Combine(presentation.Root, "presentation.yaml")
        : group.Key is not null ? ThemeFile(presentation.Theme, group.Key)
        : presentation.Slides.Any(slide => slide.Id == request.Slide) ? SlideFile(presentation, request.Slide!)
        : null;
      if (path is null)
      {
        return Refused(group.Key is null ? $"no slide {request.Slide}"
          : $"the mouse writes only the theme's theme.yaml and layouts, not {group.Key}",
          StatusCodes.Status404NotFound);
      }
      if (listing && !group.All(Renamed))
      {
        return Refused("the mouse writes only presentation.yaml's fields and section names",
          StatusCodes.Status400BadRequest);
      }
      if (!listing && !group.All(edit => Writable(edit, group.Key is not null)))
      {
        return Refused(group.Key is null
          ? "the mouse writes frames, line ends, text, fills, outlines, styles, and alignments, adds text boxes, and " +
            "deletes elements"
          : "the mouse writes a layout's frames, areas, fills, and outlines, and deletes its elements",
          StatusCodes.Status400BadRequest);
      }
      if (group.Select(edit => Unnamed(edit, presentation.Theme)).FirstOrDefault(problem => problem is not null) is
        string unnamed)
      {
        return Refused(unnamed, StatusCodes.Status400BadRequest);
      }
      files.Add((path, group.Key, group.Select(edit => edit with { File = null }).ToList()));
    }
    return Patch(store, presentation, files);
  }

  /// <summary>The theme's <c>theme.yaml</c> or a layout's file, by the name origins give it.</summary>
  private static string? ThemeFile(Theme theme, string file)
  {
    if (file == $"{theme.Display}/theme.yaml")
    {
      return Path.Combine(theme.Root, "theme.yaml");
    }
    return theme.Layouts.Keys.FirstOrDefault(id => file == $"{theme.Display}/layouts/{id}.yaml") is string layout
      ? Path.Combine(theme.Root, "layouts", $"{layout}.yaml")
      : null;
  }

  /// <summary>Applies edits to a slide's file and reloads, answering with the edits that write it back.</summary>
  public static IResult Patch(PresentationStore store, Presentation presentation, Slide slide,
    IReadOnlyList<ValueEdit> edits)
  {
    return Patch(store, presentation, [(SlideFile(presentation, slide.Id), null, edits.ToList())]);
  }

  /// <summary>Applies each file's edits and reloads, answering with the edits that write them all back, each named
  /// with its file as the request named it. A file failing puts back the ones written before it.</summary>
  private static IResult Patch(PresentationStore store, Presentation presentation,
    IReadOnlyList<(string Path, string? Name, List<ValueEdit> Edits)> files)
  {
    var undo = new List<ValueEdit>();
    var written = new List<(string Path, string Text)>();
    try
    {
      foreach ((string path, string? name, List<ValueEdit> edits) in files)
      {
        string before = File.ReadAllText(path);
        List<ValueEdit> back = Patcher.ApplyToFile(path, edits, MarkupContext.For(presentation));
        written.Add((path, before));
        undo.InsertRange(0, back.Select(edit => edit with { File = name }));
      }
    }
    catch (Exception exception) when (exception is PatchException or IOException or UnauthorizedAccessException)
    {
      foreach ((string path, string text) in written)
      {
        Put(path, text);
      }
      return Unwritten(store, exception);
    }
    store.Refresh();
    return Results.Json(new { store.Version, Undo = undo }, PayloadJson.Options);
  }

  /// <summary>Why a write was refused or failed, after a reload, so the page redraws what the files now hold.</summary>
  private static IResult Unwritten(PresentationStore store, Exception exception)
  {
    store.Refresh();
    return exception is PatchException patch
      ? Refused(patch.Message, patch.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity)
      : Refused($"couldn't write the file: {exception.Message}", StatusCodes.Status500InternalServerError);
  }

  public static string SlideFile(Presentation presentation, string slide)
  {
    return Path.Combine(presentation.Root, "slides", $"{slide}.yaml");
  }

  /// <summary>Moves, deletes, or restores a slide's line in presentation.yaml, with its file for the last two, and
  /// reloads, answering with the move that puts it back.</summary>
  private static IResult Move(PresentationStore store, SlideMove move)
  {
    if (store.Presentation is not Presentation presentation || store.Deck.Error is not null)
    {
      return Refused("the presentation doesn't load; fix its error first", StatusCodes.Status409Conflict);
    }
    if (!PresentationLoader.IsSlideStem(move.Slide))
    {
      return Refused($"{move.Slide} isn't a slide's file stem", StatusCodes.Status400BadRequest);
    }
    SlideMove? undo;
    string list = Path.Combine(presentation.Root, "presentation.yaml");
    string commentsPath = Path.Combine(presentation.Root, CommentFile.Name);
    string resolvedPath = Path.Combine(presentation.Root, CommentFile.ResolvedName);
    string file = SlideFile(presentation, move.Slide);
    // A delete or restore touches the slide, the list, and the comments; one step failing puts back the others, so
    // the page never loses a slide without the undo that brings it back.
    string? listBefore = File.Exists(list) ? File.ReadAllText(list) : null;
    string? commentsBefore = File.Exists(commentsPath) ? File.ReadAllText(commentsPath) : null;
    string? resolvedBefore = File.Exists(resolvedPath) ? File.ReadAllText(resolvedPath) : null;
    string? slideBefore = null;
    bool written = false;
    try
    {
      if (move.Delete)
      {
        slideBefore = File.ReadAllText(file);
        List<Comment> comments = CommentFile.RemoveSlide(presentation, move.Slide,
          CommentFile.Author(presentation.Root), DateTime.Now, Saves.Snapshot(presentation.Root));
        undo = Patcher.MoveInFile(list, move)! with
        {
          Restore = slideBefore,
          Comments = comments.Count > 0 ? comments : null
        };
        File.Delete(file);
      }
      else if (move.Restore is string text)
      {
        if (File.Exists(file))
        {
          throw new PatchException($"slides/{move.Slide}.yaml exists again, so restoring it was dropped", true);
        }
        Files.Replace(file, text);
        written = true;
        undo = Patcher.MoveInFile(list, move);
        if (move.Comments is { Count: > 0 } comments)
        {
          store.Refresh();
          foreach (Comment comment in comments)
          {
            CommentFile.Apply(store.Presentation!, new CommentChange(Reopen: comment.Id), "", DateTime.Now);
          }
        }
      }
      else
      {
        undo = Patcher.MoveInFile(list, move);
      }
    }
    catch (Exception exception) when (exception is PatchException or IOException or UnauthorizedAccessException)
    {
      Put(list, listBefore);
      Put(commentsPath, commentsBefore);
      Put(resolvedPath, resolvedBefore);
      if (written)
      {
        Put(file, null);
      }
      else if (slideBefore is not null && !File.Exists(file))
      {
        Put(file, slideBefore);
      }
      return Unwritten(store, exception);
    }
    store.Refresh();
    return Results.Json(new { store.Version, Undo = undo }, PayloadJson.Options);
  }

  /// <summary>Puts a file back as it was, absent when <paramref name="text"/> is null, unless it already is.</summary>
  private static void Put(string path, string? text)
  {
    try
    {
      if (text is null)
      {
        File.Delete(path);
      }
      else if (!File.Exists(path) || File.ReadAllText(path) != text)
      {
        Files.Replace(path, text);
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
    }
  }

  /// <summary>Adds, removes, rewords, replies to, restores, resolves, or reopens comments and reloads, answering with
  /// the change that undoes it.</summary>
  private static IResult Comment(PresentationStore store, CommentChange change)
  {
    if (store.Presentation is not Presentation presentation || store.Deck.Error is not null)
    {
      return Refused("the presentation doesn't load; fix its error first", StatusCodes.Status409Conflict);
    }
    if (change.Kinds != 1 || change.Many?.Any(each => each.Kinds != 1 || each.Many is not null) == true)
    {
      return Refused("a change adds, removes, rewords, replies to, restores, resolves, or reopens one comment, or " +
        "several at once",
        StatusCodes.Status400BadRequest);
    }
    CommentChange undo;
    try
    {
      undo = CommentFile.Apply(presentation, change, CommentFile.Author(presentation.Root), DateTime.Now,
        Saves.Snapshot(presentation.Root));
    }
    catch (Exception exception) when (exception is PatchException or IOException or UnauthorizedAccessException)
    {
      return Unwritten(store, exception);
    }
    store.Refresh();
    return Results.Json(new { store.Version, Undo = undo }, PayloadJson.Options);
  }

  /// <summary>Opens Libreta at the cell a linked number reads, in a book the presentation includes.</summary>
  private static IResult Trace(PresentationStore store, TracePayload trace)
  {
    if (store.Presentation?.Books.GetValueOrDefault(trace.Book) is not Book book)
    {
      return Refused($"the presentation includes no book {trace.Book}", StatusCodes.Status404NotFound);
    }
    try
    {
      return Results.Json(LibretaReader.Shared.Open(book.Root, trace.Line, trace.Column));
    }
    catch (BookException exception)
    {
      return Refused(exception.Message, StatusCodes.Status422UnprocessableEntity);
    }
  }

  /// <summary>Only what a mouse edit changes: a frame, a line's ends, and the text of an element, the title, or a
  /// placeholder, the source an undo restores in one of them, a whole element deleted or put back, and a whole file
  /// as a revert or its undo restores it.</summary>
  /// <summary>Whether the mouse may write the edit: on a slide, frames, line ends, text, fills, outlines, an
  /// element's style, the alignments of its text, the title's, or a placeholder's, adding a text box, and deleting
  /// elements; on the theme or a layout, the frames of its title, placeholders, areas, and elements, its elements'
  /// fills and outlines, and deleting its elements.</summary>
  private static bool Writable(ValueEdit edit, bool theme)
  {
    if (edit.Path.Length == 0)
    {
      return !theme && edit.Source is not null && edit.Restore is not null && edit.Numbers is null &&
        edit.Paragraphs is null && !edit.Delete && edit.Value is null && !edit.Add;
    }
    string[] parts = edit.Path.Split('.');
    if (edit.Add)
    {
      return !theme && parts[0] == "elements" && parts.Length == 2 && edit.Source is null &&
        edit.Numbers is { Length: 4 } && edit.Paragraphs is null && edit.Restore is null && !edit.Delete &&
        edit.Value is null;
    }
    if (edit.Delete)
    {
      return parts[0] == "elements" && parts.Length > 1 && edit.Numbers is null &&
        edit.Paragraphs is null && edit.Restore is null && edit.Value is null;
    }
    bool owned = parts[0] is "title" or "elements" || (parts[0] == "placeholders" && parts.Length > 1) ||
      (theme && parts[0] == "areas" && parts.Length == 2);
    string last = parts[^1];
    bool painted = parts[0] == "elements" && (last is "fill" or "stroke" || edit.Path.EndsWith(".stroke.color") ||
      edit.Path.EndsWith(".stroke.width"));
    bool worded = !theme && parts.Length == (parts[0] == "title" ? 2 : 3) &&
      (last is "align" or "valign" || (last == "style" && parts[0] == "elements"));
    return owned && (edit.Restore is not null ? edit.Path != "elements" : edit.Numbers is not null
      ? last is "frame" or "from" or "to" || parts[0] == "areas"
      : edit.Value is not null
        ? painted || worded
        : !theme && edit.Paragraphs is not null && (last == "text" || edit.Path == "title" ||
          (parts[0] == "placeholders" && parts.Length == 2)));
  }

  /// <summary>Whether an edit to presentation.yaml rewrites, or undoes rewriting, a field <c>fields</c> defines or a
  /// section's name, which a slide shows where it writes the field.</summary>
  private static bool Renamed(ValueEdit edit)
  {
    return SectionOrField().IsMatch(edit.Path) && edit.Source is not null && (edit.Value is not null ^
      edit.Restore is not null) && edit.Numbers is null && edit.Paragraphs is null && !edit.Delete && !edit.Add &&
      edit.Format is null;
  }

  [GeneratedRegex(@"^(fields\.[^.]+|slides\[[0-9]+\]\.section)$")]
  private static partial Regex SectionOrField();

  /// <summary>Why the mouse can't write the edit's style or alignment, which must be one the theme or the format
  /// names, or null.</summary>
  private static string? Unnamed(ValueEdit edit, Theme theme)
  {
    string[] alignments = ["left", "center", "right", "justify"];
    string[] vertical = ["top", "middle", "bottom"];
    return edit.Value is not string value ? null
      : edit.Path.EndsWith(".style") && !theme.Styles.Styles.ContainsKey(value)
        ? $"the theme names no style {value}"
        : edit.Path.EndsWith(".align") && !alignments.Contains(value)
          ? $"text aligns {string.Join(", ", alignments)}, not {value}"
          : edit.Path.EndsWith(".valign") && !vertical.Contains(value)
            ? $"text sits at the {string.Join(", ", vertical)}, not {value}"
            : null;
  }

  private static IResult Refused(string message, int status)
  {
    return Results.Json(new { Error = message }, PayloadJson.Options, statusCode: status);
  }

  private static IResult Saving<T>(Func<T> answer)
  {
    try
    {
      return Results.Json(answer(), PayloadJson.Options);
    }
    catch (Exception exception) when (exception is InvalidDataException or LoadException or IOException)
    {
      return Refused(exception.Message, StatusCodes.Status409Conflict);
    }
  }

  /// <summary>Whether a request names this machine as its host and, if it asks the API for anything or changes
  /// anything, comes from no other site.</summary>
  public static bool FromThisMachine(HttpRequest request)
  {
    if (!Loopback(request.Host.Host))
    {
      return false;
    }
    bool reads = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
    // Another site's page may link to the preview, but may not ask its API for anything, since even a read can set
    // it screenshotting or copying out a revision.
    if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site" &&
      (!reads || request.Path.StartsWithSegments("/api")))
    {
      return false;
    }
    if (reads)
    {
      return true;
    }
    string origin = request.Headers.Origin.ToString();
    return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && Loopback(uri.Host));
  }

  private static bool Loopback(string host)
  {
    return host.Trim('[', ']') is "localhost" or "127.0.0.1" or "::1";
  }

  /// <summary>Serves wwwroot from the source folder the build recorded, so page edits need no rebuild, falling back to
  /// the published copy.</summary>
  public static string InterfaceFolder()
  {
    string? sourceRoot = Assembly.GetEntryAssembly()?
      .GetCustomAttributes<AssemblyMetadataAttribute>()
      .FirstOrDefault(attribute => attribute.Key == "SourceRoot")?.Value;

    if (sourceRoot is not null && Directory.Exists(Path.Combine(sourceRoot, "wwwroot")))
    {
      return Path.Combine(sourceRoot, "wwwroot");
    }
    return Path.Combine(AppContext.BaseDirectory, "wwwroot");
  }

  /// <summary>An explicit port fails if taken; a default steps upward. Releasing the probe before Kestrel binds is a
  /// race, acceptable for one local user.</summary>
  public static int ChoosePort(int requested, bool specified)
  {
    for (int candidate = requested; candidate <= 65535; candidate++)
    {
      try
      {
        // Kestrel binds "localhost" on both loopbacks, so a port half-taken is still unusable.
        using var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        v4.Bind(new IPEndPoint(IPAddress.Loopback, candidate));
        if (Socket.OSSupportsIPv6)
        {
          using var v6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
          try
          {
            v6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, candidate));
          }
          // A machine with IPv6 but no loopback for it, as in many containers, serves on IPv4 alone.
          catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressNotAvailable
            or SocketError.AddressFamilyNotSupported)
          {
          }
        }
        return candidate;
      }
      catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse
        or SocketError.AccessDenied)
      {
        if (specified)
        {
          throw new IOException($"port {candidate} is already in use; another instance may be running.");
        }
      }
    }
    throw new IOException($"no free port at or above {requested}.");
  }

  /// <summary>A port the system has free on the IPv4 loopback, for a server nobody visits by address.</summary>
  public static int FreePort()
  {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
  }

  private static async Task<IResult> Headless(Func<Task<IResult>> action)
  {
    try
    {
      return await action();
    }
    catch (PlaywrightException exception)
    {
      return Results.Text($"headless Chromium failed: {exception.Message}",
        statusCode: StatusCodes.Status503ServiceUnavailable);
    }
  }


}

/// <summary>Mouse edits to a slide's file and the theme's and layouts' files, applied together or not at all.</summary>
public record EditRequest(string? Slide, List<ValueEdit> Edits);

/// <summary>The page's before side: a revision drawn as the page draws the working tree, and what changed since.
/// </summary>
/// <summary>An element, the title, or a placeholder of a slide, or with no id the whole slide, to write back as the
/// revision <paramref name="Base"/> has it.</summary>
public record RevertRequest(string Base, string Slide, string? Id = null, string? Owner = null);

/// <summary>The working tree against a revision; with <see cref="LastSave"/>, against the version before that save,
/// since nothing drawn differs from the save itself.</summary>
public record ComparePayload(string Base, string Commit, DeckVersion Version, DeckPayload Before, DeckDiff Diff,
  DeckVersion? LastSave = null);

/// <summary>Compares the working tree against a revision for the page. The last two revisions are loaded once per
/// commit, and the diff again only when the commit or the working tree's model moves, which a line moving without the
/// drawing changing still does.</summary>
public class Comparison
{
  private readonly PresentationStore store;
  private readonly object gate = new object();
  private Side[] sides = [];
  private (string? Base, string Commit, Presentation After, string Json)? answer;

  public Comparison(PresentationStore store)
  {
    this.store = store;
  }

  /// <summary>Without a revision, the changes since the last save, or the changes that save made when there are none,
  /// so a page left open shows each round as it's saved.</summary>
  public IResult Answer(string? revision)
  {
    try
    {
      string commit = Revisions.Commit(store.Root, revision ?? "HEAD");
      lock (gate)
      {
        if (store.Presentation is not Presentation current || store.Deck.Error is not null)
        {
          return Refused("the presentation doesn't load; fix its error first");
        }
        if (answer is { } last && last.Base == revision && last.Commit == commit && last.After == current)
        {
          return Results.Text(last.Json, "application/json");
        }
        ComparePayload payload = Compare(revision ?? "HEAD", commit, current);
        if (revision is null && payload.Diff.Slides.Count == 0 &&
          Revisions.Versions(store.Root, 2) is [DeckVersion saved, DeckVersion previous])
        {
          payload = Compare(previous.Commit, previous.Commit, current) with { LastSave = saved };
        }
        string json = JsonSerializer.Serialize(payload, PayloadJson.Options);
        answer = (revision, commit, current, json);
        return Results.Text(json, "application/json");
      }
    }
    catch (Exception exception) when (exception is InvalidDataException or LoadException)
    {
      return Refused(exception.Message);
    }
  }

  /// <summary>Writes a slide-owned item, or the whole slide's file, back as the revision has it, answering with the
  /// edits that undo it.</summary>
  public IResult Revert(RevertRequest request)
  {
    Presentation current;
    Side side;
    try
    {
      string commit = Revisions.Commit(store.Root, request.Base);
      lock (gate)
      {
        if (store.Presentation is not Presentation presentation || store.Deck.Error is not null)
        {
          return Refused("the presentation doesn't load; fix its error first");
        }
        current = presentation;
        side = Before(request.Base, commit);
      }
    }
    catch (Exception exception) when (exception is InvalidDataException or LoadException)
    {
      return Refused(exception.Message);
    }
    Slide? now = current.Slides.FirstOrDefault(slide => slide.Id == request.Slide);
    Slide? was = side.Presentation.Slides.FirstOrDefault(slide => slide.Id == request.Slide);
    if (now is null || was is null)
    {
      return Refused($"{request.Slide} was {(now is null ? "removed" : "added")} since {request.Base}; revert it in " +
        "presentation.yaml", StatusCodes.Status422UnprocessableEntity);
    }
    string? path = null;
    if (request.Id is not null)
    {
      if (request.Owner != "slide")
      {
        return Refused($"{request.Id} is on the {request.Owner}; revert it there",
          StatusCodes.Status422UnprocessableEntity);
      }
      path = ItemPath(current, now, request.Id) ?? ItemPath(side.Presentation, was, request.Id);
      if (path is null)
      {
        return Refused($"no {request.Id} on {request.Slide}", StatusCodes.Status404NotFound);
      }
    }
    List<ValueEdit> edits;
    try
    {
      edits = Patcher.Revert(File.ReadAllText(PreviewServer.SlideFile(current, now.Id)),
        File.ReadAllText(PreviewServer.SlideFile(side.Presentation, was.Id)), path);
    }
    catch (PatchException exception)
    {
      return Refused(exception.Message, StatusCodes.Status422UnprocessableEntity);
    }
    if (edits.Count == 0)
    {
      return Refused($"{request.Id ?? request.Slide} reads in its file as {request.Base} has it; what changed came " +
        "from the theme, the layout, or the files they read", StatusCodes.Status422UnprocessableEntity);
    }
    return PreviewServer.Patch(store, current, now, edits);
  }

  private static string? ItemPath(Presentation presentation, Slide slide, string id)
  {
    if (slide.Error is not null)
    {
      return null;
    }
    SceneItem? item = SceneBuilder.Build(presentation, slide).Items
      .FirstOrDefault(candidate => candidate.Id == id && candidate.Owner == "slide");
    if (item is null)
    {
      return null;
    }
    if (item.Element is Element element)
    {
      return element.Origin.Path;
    }
    return (item.Role == "title" ? slide.Title! : slide.Placeholders[item.Id]).Origin.Path;
  }

  /// <summary>The revision as loaded and drawn, loaded again only once the commit changes. Called under the gate.
  /// </summary>
  private ComparePayload Compare(string revision, string commit, Presentation current)
  {
    Side side = Before(revision, commit);
    return new ComparePayload(revision, commit, side.Version, side.Deck, Differ.Compare(side.Presentation, current));
  }

  // Two, since with nothing unsaved the page compares against the last save and then the version before it.
  private Side Before(string revision, string commit)
  {
    if (sides.FirstOrDefault(side => side.Commit == commit) is Side kept)
    {
      return kept;
    }
    Revision checkout = Revisions.Checkout(store.Root, revision, Directory.GetCurrentDirectory());
    Presentation presentation = Revisions.Load(checkout);
    var loaded = new Side(commit, Revisions.Describe(store.Root, commit), presentation,
      PayloadBuilder.Build(presentation, 0, PresentationStore.MediaUrl), PresentationStore.Media(presentation));
    Volatile.Write(ref sides, [.. sides.TakeLast(1), loaded]);
    return loaded;
  }

  public bool TryGetMedia(string path, out string? contentType)
  {
    contentType = PresentationStore.ServedType(path);
    Side[] loaded = Volatile.Read(ref sides);
    return contentType is not null && loaded.Any(side => side.Media.Contains(path)) && File.Exists(path);
  }

  private static IResult Refused(string message, int status = StatusCodes.Status409Conflict)
  {
    return Results.Json(new { Error = message }, PayloadJson.Options, statusCode: status);
  }

  private record Side(string Commit, DeckVersion Version, Presentation Presentation, DeckPayload Deck,
    HashSet<string> Media);


}

/// <summary>How far the running <c>.pptx</c> export is, for the page to show while it waits; null when none runs.
/// </summary>
public sealed class ExportProgress
{
  public double? Fraction { get; set; }
}