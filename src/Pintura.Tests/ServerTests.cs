using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ServerTests
{
  [Fact]
  public async Task Page_files_are_revalidated_on_every_load_so_an_edit_is_never_served_stale()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("assets/charts.js", "// charts");
    string assets = deck.Folder.PathTo("assets");
    await using Served served = await Served.Start(deck, assets);

    // Act
    HttpResponseMessage response = await served.Client.GetAsync($"{served.Url}/charts.js");

    // Assert
    response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    response.Headers.CacheControl.ShouldNotBeNull().NoCache.ShouldBeTrue();
  }

  [Fact]
  public async Task An_edit_inside_a_linked_folder_reloads_the_presentation()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      """);
    Directory.Move(deck.Folder.PathTo("deck/charts"), deck.Folder.PathTo("shared"));
    Directory.CreateSymbolicLink(deck.Folder.PathTo("deck/charts"), deck.Folder.PathTo("shared"));
    using var store = new PresentationStore(deck.Folder.PathTo("deck"), deck.Folder.PathTo("deck"), TextWriter.Null);
    long before = store.Version;

    // Act
    deck.Folder.Write("shared/revenue.csv", "segment,2024,2025\nSpace,3796,4086\nAI,2620,1");
    for (int attempt = 0; attempt < 400 && store.Version == before; attempt++)
    {
      await Task.Delay(25);
    }

    // Assert
    store.Version.ShouldBeGreaterThan(before);
  }

  [Fact]
  [Trait("Needs", "git")]
  public async Task A_commit_moves_the_head_version_so_the_page_compares_again_though_no_slide_changed()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    using var store = new PresentationStore(deck.Folder.PathTo("deck"), deck.Folder.PathTo("deck"), TextWriter.Null);
    long before = store.HeadVersion;

    // Act
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-a", "-m", "second");
    for (int attempt = 0; attempt < 400 && store.HeadVersion == before; attempt++)
    {
      await Task.Delay(25);
    }

    // Assert
    store.HeadVersion.ShouldBeGreaterThan(before);
  }

  [Theory]
  [InlineData("../../outside", false)]
  [InlineData("..\\..\\outside", false)]
  [InlineData("../presentation", true)]
  [InlineData("outside\ntheme: ../elsewhere", false)]
  public async Task A_slide_id_reaching_outside_the_slides_folder_is_refused_before_anything_is_written(string id,
    bool delete)
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string list = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    object move = delete
      ? new { slide = id, from = new { section = "Results" }, to = (object?)null, delete = true }
      : new { slide = id, from = (object?)null, to = new { section = "Results" }, restore = "layout: content\n" };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", move);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(list);
    Directory.GetFiles(deck.Folder.Root, "outside.yaml", SearchOption.AllDirectories).ShouldBeEmpty();
  }

  [Fact]
  public async Task An_absolute_slide_id_is_refused_before_anything_is_written()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string list = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    string target = deck.Folder.PathTo("outside-absolute");
    object move = new { slide = target, from = (object?)null, to = new { section = "Results" }, restore = "layout: content\n" };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", move);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(list);
    File.Exists($"{target}.yaml").ShouldBeFalse();
  }

  [Fact]
  public void A_slide_listed_by_a_path_is_an_error_on_any_system()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [one, ..\\theme\\theme]");

    // Act
    LoadException error = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    error.Problem.ShouldBe("list slides by file stem: write theme");
  }

  [Fact]
  public async Task Only_this_machines_own_pages_reach_the_server()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("index.html", "<!doctype html>");
    await using Served served = await Served.Start(deck);
    var port = new Uri(served.Url).Port;
    var edit = new { slide = "one", edits = new[] { new { path = "title.frame", source = (string?)null,
      numbers = new[] { 60.0, 180, 840, 90 } } } };
    HttpRequestMessage Request(HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
      var request = new HttpRequestMessage(method, $"{served.Url}{path}");
      if (method == HttpMethod.Post)
      {
        request.Content = JsonContent.Create(edit);
      }
      foreach ((string name, string value) in headers)
      {
        request.Headers.TryAddWithoutValidation(name, value);
      }
      return request;
    }

    // Act
    HttpResponseMessage foreignHost = await served.Client.SendAsync(Request(HttpMethod.Get, "/api/presentation",
      ("Host", "unrelated.example")));
    HttpResponseMessage foreignOrigin = await served.Client.SendAsync(Request(HttpMethod.Post, "/api/edit",
      ("Origin", "https://unrelated.example")));
    HttpResponseMessage crossSite = await served.Client.SendAsync(Request(HttpMethod.Post, "/api/edit",
      ("Sec-Fetch-Site", "cross-site")));
    HttpResponseMessage crossSiteRead = await served.Client.SendAsync(Request(HttpMethod.Get, "/api/screenshot?slide=one",
      ("Sec-Fetch-Site", "cross-site")));
    HttpResponseMessage crossSitePage = await served.Client.SendAsync(Request(HttpMethod.Get, "/",
      ("Sec-Fetch-Site", "cross-site")));
    HttpResponseMessage exportByGet = await served.Client.SendAsync(Request(HttpMethod.Get, "/api/export/html"));
    HttpResponseMessage local = await served.Client.SendAsync(Request(HttpMethod.Get, "/api/presentation",
      ("Host", $"localhost:{port}")));
    HttpResponseMessage own = await served.Client.SendAsync(Request(HttpMethod.Post, "/api/edit",
      ("Origin", $"http://localhost:{port}"), ("Sec-Fetch-Site", "same-origin")));

    // Assert
    foreignHost.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    foreignOrigin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    crossSite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    crossSiteRead.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    crossSitePage.StatusCode.ShouldBe(HttpStatusCode.OK);
    exportByGet.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    local.StatusCode.ShouldBe(HttpStatusCode.OK);
    own.StatusCode.ShouldBe(HttpStatusCode.OK);
  }

  [Fact]
  public async Task A_picture_is_served_sandboxed_so_an_svg_runs_no_script()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""");
    deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n" +
      "  - { id: icon, picture: ../media/icon.svg, frame: [0, 0, 24, 24] }");
    await using Served served = await Served.Start(deck);
    string url = served.Store.Deck.Slides[1].Items!.Single(item => item.Id == "icon").Picture!;

    // Act
    HttpResponseMessage response = await served.Client.GetAsync($"{served.Url}/{url}");

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Headers.GetValues("Content-Security-Policy").Single().ShouldStartWith("sandbox");
    response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
  }

  [Fact]
  public void Replacing_a_pictures_file_changes_the_version_and_its_address()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="red"/></svg>""");
    deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n" +
      "  - { id: icon, picture: ../media/icon.svg, frame: [0, 0, 24, 24] }");
    using var store = new PresentationStore(deck.Folder.PathTo("deck"), deck.Folder.PathTo("deck"), TextWriter.Null);
    long before = store.Version;
    string url = store.Deck.Slides[1].Items!.Single(item => item.Id == "icon").Picture!;

    // Act
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><rect fill="blue"/></svg>""");
    File.SetLastWriteTimeUtc(deck.Folder.PathTo("deck/media/icon.svg"), DateTime.UtcNow.AddSeconds(5));
    store.Refresh();

    // Assert
    store.Version.ShouldBeGreaterThan(before);
    store.Deck.Slides[1].Items!.Single(item => item.Id == "icon").Picture.ShouldNotBe(url);
  }

  [Fact]
  public async Task Edits_and_comments_keep_a_windows_checkouts_line_endings()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\r\ntitle:\r\n  text: Results\r\n");
    deck.Write("deck/comments.yaml", (CommentTests.TwoComments + "\n").ReplaceLineEndings("\r\n"));
    await using Served served = await Served.Start(deck);
    var edit = new { slide = "two", edits = new[] { new { path = "title.frame", source = (string?)null,
      numbers = new[] { 60.0, 180, 840, 90 } } } };
    var add = new { add = new { slide = "two", element = "title", at = new[] { 10, 5 }, text = "Hi" } };

    // Act
    HttpResponseMessage edited = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    HttpResponseMessage commented = await served.Client.PostAsJsonAsync($"{served.Url}/api/comments", add);

    // Assert
    edited.StatusCode.ShouldBe(HttpStatusCode.OK);
    commented.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldContain("frame:");
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).Replace("\r\n", "").ShouldNotContain("\n");
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).Replace("\r\n", "").ShouldNotContain("\n");
  }

  [Fact]
  public async Task The_preview_opens_before_its_first_load_ends_and_answers_once_it_does()
  {
    // Arrange
    using var deck = new DeckFixture();
    using var books = new HeldBooks();
    deck.Books = books;
    deck.Write("book/book.yaml", "name: Acme");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      books:
        - id: acme
          path: ../book
      slides:
        - two
      """);
    await using Served served = await Served.Start(deck, background: true);

    // Act
    books.Asked.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    Task<string> presentation = served.Client.GetStringAsync($"{served.Url}/api/presentation");
    await Task.Delay(300);
    bool answeredWhileWaiting = presentation.IsCompleted;
    books.Release();
    string answered = await presentation.WaitAsync(TimeSpan.FromSeconds(10));

    // Assert
    answeredWhileWaiting.ShouldBeFalse();
    answered.ShouldContain("\"two\"");
  }

  /// <summary>Books that answer only once released, as Libreta keeps a large book's first load waiting.</summary>
  private sealed class HeldBooks : IBookReader, IDisposable
  {
    private readonly ManualResetEventSlim released = new ManualResetEventSlim();

    public ManualResetEventSlim Asked { get; } = new ManualResetEventSlim();

    public void Release()
    {
      released.Set();
    }

    public IReadOnlyDictionary<string, BookLine> Lines(string root)
    {
      Asked.Set();
      released.Wait(TimeSpan.FromSeconds(10));
      return new Dictionary<string, BookLine>();
    }

    public BookView View(string root, string id)
    {
      return new BookView(id, [], []);
    }

    public string Page(string root)
    {
      return "<html></html>";
    }

    public void Dispose()
    {
      released.Set();
      released.Dispose();
      Asked.Dispose();
    }


  }

  [Fact]
  public async Task A_page_holding_the_event_stream_open_doesnt_hold_up_stopping_the_server()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    using HttpResponseMessage events = await served.Client.GetAsync($"{served.Url}/api/events",
      HttpCompletionOption.ResponseHeadersRead);
    using var reader = new StreamReader(await events.Content.ReadAsStreamAsync());
    await reader.ReadLineAsync();
    var clock = System.Diagnostics.Stopwatch.StartNew();

    // Act
    await served.Stop();

    // Assert
    clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
  }

  [Fact]
  public void A_file_that_cant_be_read_is_its_slides_error_and_the_preview_keeps_serving()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");
    using var store = new PresentationStore(deck.Folder.PathTo("deck"), deck.Folder.PathTo("deck"), TextWriter.Null);
    using IDisposable held = Unreadable(deck.Folder.PathTo("deck/charts/revenue.csv"));

    // Act
    store.Refresh();

    // Assert
    store.Deck.Error.ShouldBeNull();
    store.Deck.Slides.Single().Error.ShouldNotBeNull().ShouldContain("can't read");
  }

  /// <summary>Holds a file as a program with it open for writing does on Windows, or takes away its permissions
  /// elsewhere, where holding it doesn't stop a reader.</summary>
  private static IDisposable Unreadable(string path)
  {
    if (OperatingSystem.IsWindows())
    {
      return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    UnixFileMode mode = File.GetUnixFileMode(path);
    File.SetUnixFileMode(path, UnixFileMode.None);
    return new Restore(() => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); });
  }

  private sealed class Restore(Action action) : IDisposable
  {
    public void Dispose()
    {
      action();
    }
  }



}
