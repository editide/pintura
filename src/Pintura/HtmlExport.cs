using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>One HTML file that draws the presentation with no server: the page's renderer, its stylesheet, the deck,
/// and every picture inlined, for sending to someone who has only a browser.</summary>
public static class HtmlExport
{
  private static readonly string[] Scripts = ["shapes.js", "render.js", "charts.js", "book.js", "export.js"];
  private static readonly string[] Stylesheets = ["app.css", "export.css"];

  private const string SidebarIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<rect x=\"2.5\" y=\"3\" width=\"15\" height=\"14\" rx=\"1.5\"></rect><path d=\"M7.5 3v14\"></path></svg>";

  private const string BackIcon = "<svg class=\"icon\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">"
    + "<path d=\"M16 10H4.5M9 5l-5 5 5 5\"></path></svg>";

  public static string Build(Presentation presentation, string assets, IBookReader? books = null)
  {
    DeckPayload deck = PayloadBuilder.Build(presentation, 0, DataUri);
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
    // A number traced to a book opens it beside the slide, so the reader can follow it without Libreta.
    Dictionary<string, string> pages = presentation.Books.Values
      .ToDictionary(book => book.Id, book => (books ?? LibretaReader.Shared).Page(book.Root));

    var html = new StringBuilder();
    html.AppendLine("<!doctype html>");
    html.AppendLine("<html lang=\"en\">");
    html.AppendLine("<head>");
    html.AppendLine("<meta charset=\"utf-8\">");
    html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
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
      + "<div class=\"export-slide\" id=\"slide\"></div></main>");
    html.AppendLine("</div>");
    html.AppendLine("<div class=\"present\" id=\"present\" hidden></div>");
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

  // "</" would end the script element early, and "<!--<script" would keep the real end from ending it; JSON has "<"
  // only inside strings, where its escape reads the same.
  private static string ScriptJson<T>(T value)
  {
    return JsonSerializer.Serialize(value, PayloadJson.Options).Replace("<", "\\u003c");
  }

  private static string InlineFonts(string css, string assets)
  {
    return Regex.Replace(css, @"url\(""(fonts/[^""]+\.woff2)""\)", match =>
    {
      byte[] font = File.ReadAllBytes(Path.Combine(assets, match.Groups[1].Value));
      return $"url(\"data:font/woff2;base64,{Convert.ToBase64String(font)}\")";
    });
  }

  private static string DataUri(string path, string? fill)
  {
    string contentType = PresentationStore.ServedType(path) ?? "application/octet-stream";
    byte[] bytes = fill is null || !PresentationLoader.Svg(path)
      ? File.ReadAllBytes(path)
      : Encoding.UTF8.GetBytes(SvgPaint.Recolor(File.ReadAllText(path), fill));
    return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
  }


}