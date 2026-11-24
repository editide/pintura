using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
  // A field's edit names its file on this machine wherever its run sits, so the deck is sent without it.
  private static readonly JsonSerializerOptions Sent = new JsonSerializerOptions(PayloadJson.Options)
  {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(type =>
    {
      if (type.Type == typeof(RunPayload))
      {
        type.Properties.Remove(type.Properties.Single(property => property.Name == "fieldEdit"));
      }
    })
  };

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
    // the reviewers' comments and picks, with the variants they turned down and why, are for the authors, not whoever
    // the file is sent to.
    deck = deck with
    {
      Comments = [],
      Variants = [],
      Picks = [],
      Resolved = [],
      Slides = deck.Slides.Select(slide => slide with
      {
        Pick = null,
        Rejected = null,
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
    html.AppendLine("<noscript><style>body.exported { overflow: auto; } .layout { display: none; } "
      + ".deck-text { display: block; }</style></noscript>");
    html.AppendLine("</head>");
    html.AppendLine("<body class=\"exported\">");
    html.Append(Words(deck));
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
      + $"aria-label=\"Next slide\">{NextIcon}</button></div>"
      + "<div class=\"trace-hint\" id=\"trace-hint\" hidden><span id=\"trace-hint-text\"></span>"
      + "<button type=\"button\" id=\"trace-hint-close\" aria-label=\"Close hint\">"
      + $"{CloseIcon}</button></div></main>");
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
    }
    html.AppendLine($"<script type=\"application/json\" id=\"deck\">{json}</script>");
    if (pages.Count > 0)
    {
      html.AppendLine($"<script type=\"application/json\" id=\"books\">{ScriptJson(pages)}</script>");
    }
    foreach (string script in Scripts)
    {
      html.AppendLine($"<script>\n{File.ReadAllText(Path.Combine(assets, script))}\n</script>");
    }
    html.AppendLine("</body>");
    html.AppendLine("</html>");
    return html.ToString();
  }

  /// <summary>Each font the slides draw in that the theme's <c>fonts/</c> doesn't carry into the page, so the reader
  /// sees it only if they have it installed. Fonts every Windows, Mac, and iPhone has go unmentioned.</summary>
  public static List<string> Warnings(Presentation presentation)
  {
    DeckPayload deck = PayloadBuilder.Build(presentation, 0, (path, _) => path);
    HashSet<string> shipped = presentation.Theme.Faces.Select(face => face.Family)
      .Concat(Everywhere).ToHashSet(StringComparer.OrdinalIgnoreCase);
    return PickFile.Warnings(presentation).Concat(deck.Slides.SelectMany(slide => slide.Items ?? [])
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
        "in another font"))
      .ToList();
  }

  /// <summary>The slides' words, tables, and chart data as a plain document at the top of the page, for whoever reads
  /// it without running its scripts: a crawler, a reader view, or a browser with scripts off. The stylesheet hides it
  /// wherever the scripts draw the slides. What the layout draws, such as a footer or page number, is left out.
  /// </summary>
  private static string Words(DeckPayload deck)
  {
    var html = new StringBuilder();
    html.AppendLine("<article class=\"deck-text\" id=\"deck-text\">");
    html.AppendLine($"<h1>{Encode(deck.Name)}</h1>");
    foreach (SlidePayload slide in deck.Slides)
    {
      List<ItemPayload> items = (slide.Items ?? []).Where(item => item.Owner == "slide").ToList();
      ItemPayload? title = items.FirstOrDefault(item => item.Role == "title");
      string heading = title?.Text is { } text
        ? string.Join(" ", text.Paragraphs.Select(Runs).Where(line => line.Length > 0))
        : Encode(slide.Title ?? "");
      html.AppendLine($"<section id=\"slide-{Encode(slide.Id)}\">");
      if (heading.Length > 0)
      {
        html.AppendLine($"<h2>{heading}</h2>");
      }
      foreach (ItemPayload item in items.Where(item => item != title))
      {
        if (item.Table is { } table)
        {
          Table(html, table);
        }
        else if (item.Chart is { } chart)
        {
          Chart(html, chart);
        }
        else if (item.Text is { } words)
        {
          Paragraphs(html, words.Paragraphs);
        }
      }
      html.AppendLine("</section>");
    }
    html.AppendLine("</article>");
    return html.ToString();
  }

  private static void Paragraphs(StringBuilder html, List<ParagraphPayload> paragraphs)
  {
    bool listing = false;
    foreach (ParagraphPayload paragraph in paragraphs)
    {
      string line = Runs(paragraph);
      if (line.Length == 0)
      {
        continue;
      }
      bool bulleted = paragraph.Bullet is not null;
      if (bulleted != listing)
      {
        html.AppendLine(bulleted ? "<ul>" : "</ul>");
        listing = bulleted;
      }
      html.AppendLine(bulleted ? $"<li>{line}</li>" : $"<p>{line}</p>");
    }
    if (listing)
    {
      html.AppendLine("</ul>");
    }
  }

  private static string Runs(ParagraphPayload paragraph)
  {
    if (string.IsNullOrWhiteSpace(string.Concat(paragraph.Runs.Select(run => run.Text))))
    {
      return "";
    }
    return string.Concat(paragraph.Runs.Select(run =>
    {
      string text = Encode(run.Text).Replace("\n", "<br>");
      return run.Link is null ? text : $"<a href=\"{Encode(run.Link)}\">{text}</a>";
    })).Trim();
  }

  private static void Table(StringBuilder html, TablePayload table)
  {
    html.AppendLine("<table>");
    foreach (IGrouping<int, CellPayload> row in table.Cells.GroupBy(cell => cell.Row).OrderBy(row => row.Key))
    {
      html.Append("<tr>");
      foreach (CellPayload cell in row.OrderBy(cell => cell.Column))
      {
        string spans = (cell.Rows > 1 ? $" rowspan=\"{cell.Rows}\"" : "")
          + (cell.Columns > 1 ? $" colspan=\"{cell.Columns}\"" : "");
        string lines = string.Join("<br>", cell.Text.Paragraphs.Select(Runs).Where(line => line.Length > 0));
        html.Append($"<td{spans}>{lines}</td>");
      }
      html.AppendLine("</tr>");
    }
    html.AppendLine("</table>");
  }

  /// <summary>A row for each category and a column for each series, each value formatted as its label is, or else
  /// as its axis is.</summary>
  private static void Chart(StringBuilder html, ChartPayload chart)
  {
    html.AppendLine("<table>");
    if (chart.Title is { Length: > 0 } title)
    {
      html.AppendLine($"<caption>{Encode(title)}</caption>");
    }
    html.AppendLine("<tr><td></td>" + string.Concat(chart.Series.Select(series =>
      $"<th scope=\"col\">{Encode(series.Name)}</th>")) + "</tr>");
    for (int index = 0; index < chart.Categories.Count; index++)
    {
      html.Append($"<tr><th scope=\"row\">{Encode(chart.Categories[index])}</th>");
      foreach (SeriesPayload series in chart.Series)
      {
        double? value = index < series.Values.Count ? series.Values[index] : null;
        FormatPayload? format = series.Labels?.Format
          ?? (series.Secondary ? chart.SecondaryAxis : chart.ValueAxis)?.Format;
        html.Append($"<td>{(value is { } number ? Encode(Number(number, format)) : "")}</td>");
      }
      html.AppendLine("</tr>");
    }
    html.AppendLine("</table>");
  }

  /// <summary>A value as charts.js's <c>formatNumber</c> labels it, so the text says what the chart shows.</summary>
  private static string Number(double value, FormatPayload? format)
  {
    if (format is null)
    {
      return double.Parse(value.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        .ToString(CultureInfo.InvariantCulture);
    }
    double scaled = value * format.Scale;
    string digits = Math.Abs(scaled).ToString(
      (format.Separator ? "#,0" : "0") + (format.Decimals > 0 ? "." + new string('0', format.Decimals) : ""),
      CultureInfo.InvariantCulture);
    string text = format.Prefix + digits + format.Suffix;
    if (scaled >= 0)
    {
      return text;
    }
    return format.Negative == "parentheses" ? $"({text})" : $"-{text}";
  }

  private static string Encode(string text)
  {
    return WebUtility.HtmlEncode(text);
  }

  // "</" would end the script element early, and "<!--<script" would keep the real end from ending it; JSON has "<"
  // only inside strings, where its escape reads the same.
  private static string ScriptJson<T>(T value)
  {
    return JsonSerializer.Serialize(value, Sent).Replace("<", "\\u003c");
  }

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
    using SKData? lossless = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 25));
    using SKData? lossy = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80));
    if (lossless is null || lossy is null)
    {
      return (lossless ?? lossy)?.ToArray();
    }
    return (lossless.Size <= 2 * lossy.Size ? lossless : lossy).ToArray();
  }


}