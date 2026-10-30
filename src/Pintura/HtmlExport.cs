using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Pintura;

/// <summary>One HTML file that draws the presentation with no server: the page's renderer, its stylesheet, the deck,
/// and every picture inlined, for sending to someone who has only a browser.</summary>
public static class HtmlExport
{
  private static readonly string[] Scripts = ["shapes.js", "render.js", "charts.js", "book.js", "navigation.js", "export.js"];
  private static readonly string[] Stylesheets = ["app.css", "export.css"];
  // Fonts every Windows, Mac, and iPhone has, though Android and Linux may draw a lookalike.
  private static readonly string[] Everywhere =
    ["Arial", "Courier New", "Georgia", "Times New Roman", "Trebuchet MS", "Verdana"];

  private const string SidebarIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<rect x=\"2.5\" y=\"3\" width=\"15\" height=\"14\" rx=\"1.5\"></rect><path d=\"M7.5 3v14\"></path></svg>";

  private const string BackIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<path d=\"M16 10H4.5M9 5l-5 5 5 5\"></path></svg>";

  private const string PreviousIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<path d=\"M12.5 4.5 7 10l5.5 5.5\"></path></svg>";

  private const string NextIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<path d=\"M7.5 4.5 13 10l-5.5 5.5\"></path></svg>";

  private const string CloseIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<path d=\"M5 5l10 10M15 5 5 15\"></path></svg>";

  /// <summary>The page, reporting what it is doing to <paramref name="progress"/> with how far it is, when it can
  /// tell.</summary>
  public static string Build(Presentation presentation, string assets, IBookReader? books = null,
    PictureCache? pictures = null, Action<string, double?>? progress = null)
  {
    // A book's page, exported by Libreta, needs nothing of the slides, so it's exported while the pictures encode.
    IBookReader reader = books ?? LibretaReader.Shared;
    Task<Dictionary<string, string>> exporting = Task.Run(() => presentation.Books.Values
      .ToDictionary(book => book.Id, book => reader.Page(book.Root)));
    pictures ??= PictureCache.User;
    // Encoding the pictures is the slow part of the export, so each is encoded once, all at the same time, however
    // many slides draw it.
    List<string> encoded = PresentationStore.Media(presentation)
      .Where(path => PresentationStore.ServedType(path) is "image/png" or "image/jpeg")
      .ToList();
    int done = 0;
    progress?.Invoke("Exporting pictures", 0);
    ConcurrentDictionary<(string, string?), string> uris = new(encoded.AsParallel().Select(path =>
    {
      string uri = DataUri(path, null, pictures);
      // One at a time, so the share reported never goes back.
      lock (encoded)
      {
        progress?.Invoke("Exporting pictures", (double)++done / encoded.Count);
      }
      return KeyValuePair.Create<(string, string?), string>((path, null), uri);
    }));
    DeckPayload deck = PayloadBuilder.Build(presentation, 0,
      (path, fill) => uris.GetOrAdd((path, fill), key => DataUri(key.Item1, key.Item2, pictures)));
    // What the preview needs to write edits back names the files on this machine, and a sent file has no use for it;
    // the reviewers' comments are for the authors, not whoever the file is sent to.
    deck = deck with
    {
      Comments = [],
      Slides = deck.Slides.Select(slide => slide with
      {
        Items = slide.Items?.Select(item => item with { Edit = new EditPayload() }).ToList()
      }).ToList()
    };
    string json = ScriptJson(deck);
    if (presentation.Books.Count > 0)
    {
      progress?.Invoke(presentation.Books.Count == 1 ? "Exporting the book" : "Exporting the books", null);
    }
    // A number traced to a book opens it beside the slide, so the reader can follow it without Libreta.
    Dictionary<string, string> pages = exporting.GetAwaiter().GetResult();
    Dictionary<string, Dictionary<string, TracedLine>> cells = Cells(presentation, deck, reader);

    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("<head>");
    html.AppendLine("<meta charset=\"utf-8\">");
    html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">");
    html.AppendLine($"<title>{WebUtility.HtmlEncode(presentation.Name)}</title>");
    foreach (string stylesheet in Stylesheets)
    {
      html.AppendLine($"<style>\n{InlineFonts(File.ReadAllText(Path.Combine(assets, stylesheet)), assets)}\n</style>");
    }
    html.AppendLine("</head>");
    html.AppendLine("<body class=\"exported\">");
    html.AppendLine("<div class=\"layout nav-hidden\">");
    html.AppendLine("<nav class=\"sidebar\" aria-label=\"Slides\"><div class=\"sidebar-head\">"
      + "<div class=\"deck-name\" id=\"deck-name\"></div>"
      + "<button type=\"button\" class=\"icon-button\" id=\"nav-hide\" title=\"Hide slides (\\)\" "
      + $"aria-label=\"Hide slides\">{SidebarIcon}</button>"
      + "</div><div class=\"sidebar-scroll\"><div id=\"thumbnails\"></div></div></nav>");
    html.AppendLine("<main id=\"view\"><div class=\"export-bar\">"
      + "<button type=\"button\" class=\"icon-button\" id=\"nav-show\" title=\"Show slides (\\)\" "
      + $"aria-label=\"Show slides\">{SidebarIcon}</button>"
      + "<button type=\"button\" id=\"present-button\" title=\"Present (P)\">Present</button></div>"
      + "<div class=\"export-slide\" id=\"slide\"></div>"
      + "<div class=\"export-turn\"><button type=\"button\" class=\"icon-button\" id=\"previous\" "
      + $"title=\"Previous slide (\u2190)\" aria-label=\"Previous slide\">{PreviousIcon}</button>"
      + "<span id=\"counter\" aria-live=\"polite\"></span>"
      + "<button type=\"button\" class=\"icon-button\" id=\"next\" title=\"Next slide (\u2192)\" "
      + $"aria-label=\"Next slide\">{NextIcon}</button></div></main>");
    html.AppendLine("</div>");
    html.AppendLine("<div class=\"present\" id=\"present\" hidden>"
      + "<button type=\"button\" class=\"icon-button\" id=\"present-exit\" title=\"Stop presenting (Esc)\" "
      + $"aria-label=\"Stop presenting\">{CloseIcon}</button>"
      + "<p class=\"present-hint\">Turn your phone sideways for a larger slide</p></div>");
    if (pages.Count > 0)
    {
      html.AppendLine("<div class=\"book\" id=\"book\"><div class=\"book-bar\">"
        + $"<button type=\"button\" id=\"book-back\">{BackIcon}<span id=\"book-back-label\"></span></button></div>"
        + "<iframe id=\"book-frame\" title=\"Book\"></iframe></div>");
      html.AppendLine("<div class=\"cell-sheet\" id=\"cell-sheet\" hidden><div class=\"cell-card\" role=\"dialog\" "
        + "aria-labelledby=\"cell-label\"><div class=\"cell-label\" id=\"cell-label\"></div>"
        + "<div class=\"cell-value\" id=\"cell-value\"></div>"
        + "<div class=\"cell-source\" id=\"cell-source\"></div>"
        + "<button type=\"button\" id=\"cell-open\">Open in the book</button></div></div>");
    }
    html.AppendLine($"<script type=\"application/json\" id=\"deck\">{json}</script>");
    if (pages.Count > 0)
    {
      html.AppendLine($"<script type=\"application/json\" id=\"books\">{ScriptJson(pages)}</script>");
      html.AppendLine($"<script type=\"application/json\" id=\"cells\">{ScriptJson(cells)}</script>");
    }
    foreach (string script in Scripts)
    {
      html.AppendLine($"<script>\n{File.ReadAllText(Path.Combine(assets, script))}\n</script>");
    }
    html.AppendLine("</body>");
    html.AppendLine("</html>");
    return html.ToString();
  }

  /// <summary>Each line a slide traces to, by book, with the values of only the cells traced: a phone shows a
  /// number's cell on its own, since the book's page is too wide for it.</summary>
  private static Dictionary<string, Dictionary<string, TracedLine>> Cells(Presentation presentation, DeckPayload deck,
    IBookReader reader)
  {
    IEnumerable<TracePayload> traces = deck.Slides.SelectMany(slide => slide.Items ?? [])
      .SelectMany(item => new[] { item.Text }.Concat(item.Table?.Cells.Select(cell => cell.Text) ?? [])
          .SelectMany(text => text?.Paragraphs ?? [])
          .SelectMany(paragraph => paragraph.Runs)
          .Select(run => run.Trace)
        .Concat(item.Table?.Cells.Select(cell => cell.Trace) ?? [])
        .Concat(item.Chart?.Series.SelectMany(series => series.Traces ?? []) ?? []))
      .OfType<TracePayload>();
    var cells = new Dictionary<string, Dictionary<string, TracedLine>>(StringComparer.Ordinal);
    foreach (TracePayload trace in traces)
    {
      if (presentation.Books.GetValueOrDefault(trace.Book) is not Book book
        || reader.Lines(book.Root).GetValueOrDefault(trace.Line) is not BookLine line)
      {
        continue;
      }
      int column = line.Columns.IndexOf(trace.Column);
      if (column < 0)
      {
        continue;
      }
      if (!cells.TryGetValue(trace.Book, out Dictionary<string, TracedLine>? lines))
      {
        lines = cells[trace.Book] = new Dictionary<string, TracedLine>(StringComparer.Ordinal);
      }
      if (!lines.TryGetValue(trace.Line, out TracedLine? traced))
      {
        traced = lines[trace.Line] = new TracedLine(line.Label, line.Units, new Dictionary<string, string>());
      }
      traced.Values[trace.Column] = line.Display[column];
    }
    return cells;
  }

  /// <summary>Each font the slides draw in that the theme's <c>fonts/</c> doesn't carry into the page, so the reader
  /// sees it only if they have it installed. Fonts every Windows, Mac, and iPhone has go unmentioned.</summary>
  public static List<string> Warnings(Presentation presentation)
  {
    DeckPayload deck = PayloadBuilder.Build(presentation, 0, (path, _) => path);
    HashSet<string> shipped = presentation.Theme.Faces.Select(face => face.Family)
      .Concat(Everywhere).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return deck.Slides.SelectMany(slide => slide.Items ?? [])
      .SelectMany(item => (item.Chart is null ? [] : new[] { item.Chart.Text.Font })
        .Concat(new[] { item.Text }.Concat(item.Table?.Cells.Select(cell => cell.Text) ?? [])
          .SelectMany(text => text?.Paragraphs ?? [])
          .SelectMany(paragraph => paragraph.Runs.Prepend(paragraph.Mark))
          .Select(run => run.Font)))
      .OfType<string>()
      .Where(family => !shipped.Contains(family))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Order(StringComparer.Ordinal)
      .Select(family => $"{family} isn't in the theme's fonts/, so readers without it installed see the text in it " +
        "in another font")
      .ToList();
  }

  // "</" would end the script element early, and "<!--<script" would keep the real end from ending it; JSON has "<"
  // only inside strings, where its escape reads the same.
  private static string ScriptJson<T>(T value)
  {
    return JsonSerializer.Serialize(value, PayloadJson.Options).Replace("<", "\\u003c");
  }

  private record TracedLine(string Label, string? Units, Dictionary<string, string> Values);

  private static string InlineFonts(string css, string assets)
  {
    return Regex.Replace(css, @"url\(""(fonts/[^""]+\.woff2)""\)", match =>
    {
      byte[] font = File.ReadAllBytes(Path.Combine(assets, match.Groups[1].Value));
      return $"url(\"data:font/woff2;base64,{Convert.ToBase64String(font)}\")";
    });
  }

  private static string DataUri(string path, string? fill, PictureCache pictures)
  {
    string contentType = PresentationStore.ServedType(path) ?? "application/octet-stream";
    if (PresentationLoader.Svg(path))
    {
      byte[] svg = fill is null
        ? File.ReadAllBytes(path)
        : Encoding.UTF8.GetBytes(SvgPaint.Recolor(File.ReadAllText(path), fill));
      return $"data:{contentType};base64,{Convert.ToBase64String(svg)}";
    }
    byte[] original = File.ReadAllBytes(path);
    byte[]? webp = contentType is "image/png" or "image/jpeg"
      ? pictures.Get(original, "webp", () => WebP(original))
      : null;
    return webp is not null && webp.Length < original.Length
      ? $"data:image/webp;base64,{Convert.ToBase64String(webp)}"
      : $"data:{contentType};base64,{Convert.ToBase64String(original)}";
  }

  /// <summary>Lossless when that costs at most twice the lossy size, as flat colors and hard edges such as a chart's
  /// do; lossy otherwise, as for a photograph or a page of small text. Skia encodes lossless at libwebp's fastest
  /// method, which a detailed picture outgrows.</summary>
  private static byte[]? WebP(byte[] picture)
  {
    using SKCodec? codec = SKCodec.Create(new SKMemoryStream(picture));
    // Browsers turn a photo by its EXIF orientation and draw a wide-gamut one in its own colors, and WebP would keep
    // neither.
    if (codec is null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft || codec.Info.ColorSpace is { IsSrgb: false })
    {
      return null;
    }
    // Unpremultiplied, so lossless keeps a translucent pixel's color exactly.
    using SKBitmap? bitmap = SKBitmap.Decode(codec, codec.Info.WithAlphaType(
      codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul));
    if (bitmap is null)
    {
      return null;
    }
    using SKPixmap pixmap = bitmap.PeekPixels();
    using SKData lossless = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 25));
    using SKData lossy = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80));
    return (lossless.Size <= 2 * lossy.Size ? lossless : lossy).ToArray();
  }


}