using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
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