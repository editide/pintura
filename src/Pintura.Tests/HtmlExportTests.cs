using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Pintura.Tests;

public class HtmlExportTests
{
  private static readonly string Assets = Path.Combine(LoaderTests.RepositoryRoot(), "src", "Pintura", "wwwroot");

  /// <summary>A book's page as Libreta exports it, holding the script elements a real one does.</summary>
  private sealed class PagedBooks : IBookReader
  {
    public const string Page = "<html><script type=\"application/json\">{}</script><script>refresh();</script></html>";

    public List<string> Exported { get; } = [];

    public IReadOnlyDictionary<string, BookLine> Lines(string root)
    {
      return new Dictionary<string, BookLine>();
    }

    public BookView View(string root, string id)
    {
      throw new BookException("no views");
    }

    string IBookReader.Page(string root)
    {
      Exported.Add(root);
      return Page;
    }


  }

  [Fact]
  public void The_page_carries_the_deck_and_its_pictures_and_asks_no_server_for_anything()
  {
    // Arrange
    Presentation presentation = PresentationLoader.Load(
      Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example"));

    // Act
    string html = HtmlExport.Build(presentation, Assets);

    // Assert
    html.ShouldNotContain("api/");
    html.ShouldNotContain(LoaderTests.RepositoryRoot());
    html.ShouldNotContain("<script src=");
    html.ShouldNotContain("<link ");
    JsonElement deck = Deck(html);
    deck.GetProperty("slides").GetArrayLength().ShouldBe(presentation.Slides.Count);
    string[] pictures = Pictures(deck).ToArray();
    pictures.ShouldNotBeEmpty();
    pictures.ShouldAllBe(picture => picture.StartsWith("data:image/svg+xml;base64,"));
  }

  [Fact]
  public void The_page_names_no_file_on_this_machine()
  {
    // Arrange
    Presentation presentation = PresentationLoader.Load(
      Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example"));

    // Act
    string html = HtmlExport.Build(presentation, Assets);

    // Assert
    Properties(Deck(html), "fieldEdit").ShouldAllBe(edit => edit.ValueKind == JsonValueKind.Null);
    html.ShouldNotContain(".yaml\"");
  }

  [Fact]
  public void The_page_carries_the_interfaces_fonts()
  {
    // Arrange
    using var fixture = new DeckFixture();

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    html.ShouldNotContain("url(\"fonts/");
    Regex.Matches(html, "url\\(\"data:font/woff2;base64,").Count.ShouldBe(2);
  }

  [Fact]
  public void Text_that_closes_a_script_stays_inside_the_deck()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/two.yaml", """
      layout: content
      title: "</script><b>loose</b>"
      """);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    html.ShouldNotContain("<b>loose</b>");
    Deck(html).GetProperty("slides")[1].GetRawText().ShouldContain("loose");
  }

  [Fact]
  public void Text_opening_a_markup_comment_and_a_script_leaves_the_deck_whole()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/two.yaml", """
      layout: content
      title: "Q2 <!--<script> update"
      """);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    html.ShouldNotContain("<!--<script>");
    Deck(html).GetProperty("slides")[1].GetProperty("title").GetRawText().ShouldContain("update");
  }

  [Fact]
  public void The_page_carries_no_reviewers_comments()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/comments.yaml", """
      - { id: a1, slide: one, at: [0, 0], text: PRIVATE_NOTE, done: PRIVATE_RESOLUTION }
      - { id: b2, slide: two, at: [0, 0], text: PRIVATE_OTHER, skipped: PRIVATE_REASON }
      """);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    html.ShouldNotContain("PRIVATE_");
  }

  [Fact]
  public void The_page_draws_an_open_picks_first_variant_and_carries_none_of_the_others_or_their_intents()
  {
    // Arrange
    using DeckFixture fixture = PickTests.Offering();

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    JsonElement deck = Deck(html);
    deck.GetProperty("slides").EnumerateArray().Select(slide => slide.GetProperty("id").GetString())
      .ShouldBe(["one", "two"]);
    deck.GetRawText().ShouldNotContain("\"Waterfall\"", Case.Sensitive);
    html.ShouldNotContain("Two charts, side by side");
    html.ShouldNotContain("One stacked chart");
    html.ShouldNotContain("p4kx");
  }

  [Theory]
  [InlineData("stacked")]
  [InlineData(null)]
  public void The_page_carries_no_resolved_pick_nor_why_the_reviewer_chose_it(string? keep)
  {
    // Arrange
    using DeckFixture fixture = PickTests.Offering();
    PickFile.Apply(fixture.Load(), new PickChange("p4kx", keep, "PRIVATE_WHY"), "Daniel",
      new DateTime(2026, 11, 2, 18, 40, 0), new Snapshot("8b21d07", false, new HashSet<string>()));

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    html.ShouldNotContain("PRIVATE_");
    html.ShouldNotContain("p4kx");
  }

  [Fact]
  public void The_page_carries_each_books_own_page_whole()
  {
    // Arrange
    using var fixture = new DeckFixture { Books = new PagedBooks() };
    fixture.Write("book/book.yaml", "name: Acme");
    fixture.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      books:
        - id: acme
          path: ../book
      slides:
        - one
      """);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets, fixture.Books);

    // Assert
    Books(html)!.Value.GetProperty("acme").GetString().ShouldBe(PagedBooks.Page);
    ((PagedBooks)fixture.Books).Exported.ShouldBe([fixture.Folder.PathTo("book")]);
  }

  [Fact]
  public void The_export_reports_the_pictures_as_it_encodes_them_then_the_books_it_waits_on()
  {
    // Arrange
    using var fixture = new DeckFixture { Books = new PagedBooks() };
    using var cache = new TempFolder();
    using SKBitmap flat = Flat();
    Place(fixture, "filing.png", Encode(flat, SKEncodedImageFormat.Png, 100));
    fixture.Write("book/book.yaml", "name: Acme");
    fixture.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      books:
        - id: acme
          path: ../book
      slides:
        - one
        - two
      """);
    var reports = new ConcurrentQueue<(string Stage, double? Fraction)>();

    // Act
    HtmlExport.Build(fixture.Load(), Assets, fixture.Books, new PictureCache(cache.Root),
      (stage, fraction) => reports.Enqueue((stage, fraction)));

    // Assert
    reports.ShouldContain(("Exporting pictures", 1.0));
    reports.Select(report => report.Stage).Distinct().ShouldBe(["Exporting pictures", "Exporting the book"]);
  }

  [Fact]
  public void A_picture_encoded_for_one_export_is_read_from_the_cache_by_the_next()
  {
    // Arrange
    using var fixture = new DeckFixture();
    using var cache = new TempFolder();
    using SKBitmap photograph = Photograph();
    Place(fixture, "photo.png", Encode(photograph, SKEncodedImageFormat.Png, 100));
    string first = HtmlExport.Build(fixture.Load(), Assets, pictures: new PictureCache(cache.PathTo("pictures")));
    string entry = Directory.GetFiles(cache.PathTo("pictures")).ShouldHaveSingleItem();
    File.WriteAllBytes(entry, Carried(first, "image/webp")[..^1]);

    // Act
    string again = HtmlExport.Build(fixture.Load(), Assets, pictures: new PictureCache(cache.PathTo("pictures")));

    // Assert
    Carried(again, "image/webp").ShouldBe(Carried(first, "image/webp")[..^1]);
  }

  [Fact]
  public void A_presentation_reading_no_book_carries_none()
  {
    // Arrange
    using var fixture = new DeckFixture();
    var books = new PagedBooks();

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets, books);

    // Assert
    Books(html).ShouldBeNull();
    books.Exported.ShouldBeEmpty();
  }

  [Fact]
  public void A_photograph_is_carried_as_lossy_webp()
  {
    // Arrange
    using var fixture = new DeckFixture();
    using SKBitmap photograph = Photograph();
    byte[] photo = Encode(photograph, SKEncodedImageFormat.Png, 100);
    Place(fixture, "photo.png", photo);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    byte[] carried = Carried(html, "image/webp");
    carried.Length.ShouldBeLessThan(photo.Length / 4);
    using SKBitmap decoded = SKBitmap.Decode(carried);
    (decoded.Width, decoded.Height).ShouldBe((400, 300));
  }

  [Fact]
  public void A_flat_picture_is_carried_as_webp_with_every_pixel_kept()
  {
    // Arrange
    using var fixture = new DeckFixture();
    using SKBitmap flat = Flat();
    byte[] png = Encode(flat, SKEncodedImageFormat.Png, 100);
    Place(fixture, "filing.png", png);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    byte[] carried = Carried(html, "image/webp");
    carried.Length.ShouldBeLessThan(png.Length);
    using SKBitmap decoded = SKBitmap.Decode(carried);
    decoded.Pixels.ShouldBe(flat.Pixels);
  }

  [Fact]
  public void A_picture_smaller_than_its_webp_is_carried_as_it_is()
  {
    // Arrange
    using var fixture = new DeckFixture();
    using SKBitmap photograph = Photograph();
    byte[] jpeg = Encode(photograph, SKEncodedImageFormat.Jpeg, 10);
    Place(fixture, "photo.jpg", jpeg);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    Carried(html, "image/jpeg").ShouldBe(jpeg);
  }

  [Fact]
  public void The_page_reads_as_text_before_any_script_runs()
  {
    // Arrange
    Presentation presentation = PresentationLoader.Load(
      Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example"));

    // Act
    string html = HtmlExport.Build(presentation, Assets);

    // Assert
    string words = Words(html);
    words.ShouldContain("<h1>Acme Q2 2026 Board Update</h1>");
    words.ShouldContain("<h2>Connectivity funds the build-out in AI</h2>");
    words.ShouldContain("$4.4B operating income in 2025");
    words.ShouldContain("<a href=\"https://example.com/acme/s-1a\">Acme S-1/A</a>");
    words.ShouldContain("<td>H1 2026</td>");
    words.ShouldContain("<th scope=\"row\">2023</th><td>$3.6B</td>");
    html.IndexOf("deck-text", StringComparison.Ordinal)
      .ShouldBeLessThan(html.IndexOf("<script", StringComparison.Ordinal));
  }

  [Fact]
  public void The_pages_text_leaves_out_what_the_layout_repeats_on_every_slide()
  {
    // Arrange
    Presentation presentation = PresentationLoader.Load(
      Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example"));

    // Act
    string html = HtmlExport.Build(presentation, Assets);

    // Assert
    Words(html).ShouldNotContain("Confidential. Prepared for board discussion.");
  }

  [Fact]
  public void The_pages_text_escapes_what_the_slides_say()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/one.yaml", """
      layout: cover
      title: "<b>R&D</b>"
      """);

    // Act
    string html = HtmlExport.Build(fixture.Load(), Assets);

    // Assert
    Words(html).ShouldContain("<h2>&lt;b&gt;R&amp;D&lt;/b&gt;</h2>");
  }

  private static string Words(string html)
  {
    Match match = Regex.Match(html, "<article class=\"deck-text\".*?</article>", RegexOptions.Singleline);
    match.Success.ShouldBeTrue();
    return match.Value;
  }

  private static void Place(DeckFixture fixture, string name, byte[] picture)
  {
    fixture.Write($"deck/media/{name}", "");
    File.WriteAllBytes(fixture.Folder.PathTo($"deck/media/{name}"), picture);
    fixture.Write("deck/slides/two.yaml",
      $"layout: content\nelements:\n  - id: picture\n    picture: ../media/{name}\n    frame: [40, 100, 400, 300]");
  }

  private static IEnumerable<JsonElement> Properties(JsonElement element, string name)
  {
    return element.ValueKind switch
    {
      JsonValueKind.Object => element.EnumerateObject()
        .SelectMany(property => (property.Name == name ? [property.Value] : Enumerable.Empty<JsonElement>())
          .Concat(Properties(property.Value, name))),
      JsonValueKind.Array => element.EnumerateArray().SelectMany(item => Properties(item, name)),
      _ => []
    };
  }

  private static byte[] Carried(string html, string type)
  {
    string picture = Pictures(Deck(html)).Single();
    picture.ShouldStartWith($"data:{type};base64,");
    return Convert.FromBase64String(picture[picture.IndexOf(',')..][1..]);
  }

  /// <summary>Smooth color with grain, which a lossy encoding shrinks far more than a lossless one.</summary>
  private static SKBitmap Photograph()
  {
    var bitmap = new SKBitmap(400, 300);
    var random = new Random(7);
    for (int y = 0; y < bitmap.Height; y++)
    {
      for (int x = 0; x < bitmap.Width; x++)
      {
        int grain = random.Next(-12, 13);
        bitmap.SetPixel(x, y, new SKColor(Channel(x * 255 / 400 + grain), Channel(y * 255 / 300 + grain),
          Channel(160 + grain)));
      }
    }
    return bitmap;
  }

  /// <summary>Flat colors and hard edges, which a lossless encoding keeps exactly at less than the lossy size.</summary>
  private static SKBitmap Flat()
  {
    var bitmap = new SKBitmap(400, 300);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var ink = new SKPaint { Color = new SKColor(0x22, 0x22, 0x22) };
    var random = new Random(7);
    for (int line = 0; line < 20; line++)
    {
      for (int x = 10; x < 390; x += random.Next(3, 9))
      {
        canvas.DrawRect(x, 10 + line * 14, random.Next(1, 3), random.Next(4, 9), ink);
      }
    }
    return bitmap;
  }

  private static byte Channel(int value)
  {
    return (byte)Math.Clamp(value, 0, 255);
  }

  private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
  {
    using SKData data = bitmap.Encode(format, quality);
    return data.ToArray();
  }

  private static JsonElement? Books(string html)
  {
    Match match = Regex.Match(html, "<script type=\"application/json\" id=\"books\">(.*?)</script>",
      RegexOptions.Singleline);
    return match.Success ? JsonDocument.Parse(match.Groups[1].Value).RootElement : null;
  }

  private static JsonElement Deck(string html)
  {
    Match match = Regex.Match(html, "<script type=\"application/json\" id=\"deck\">(.*?)</script>",
      RegexOptions.Singleline);
    match.Success.ShouldBeTrue();
    return JsonDocument.Parse(match.Groups[1].Value).RootElement;
  }

  private static IEnumerable<string> Pictures(JsonElement deck)
  {
    return deck.GetProperty("slides").EnumerateArray()
      .SelectMany(slide => slide.GetProperty("items").EnumerateArray())
      .Where(item => item.TryGetProperty("picture", out _))
      .Select(item => item.GetProperty("picture").GetString()!);
  }


}