using System.Net;
using System.Net.Http.Json;
using System.Text;
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
  public async Task Compare_answers_with_the_revision_as_drawn_and_what_changed_since()
  {
    // Arrange
    using var deck = new DeckFixture();
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement answer =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare?base=HEAD");

    // Assert
    answer.GetProperty("before").GetProperty("slides")[1].GetProperty("title").GetString()
      .ShouldBe("Results in Results");
    System.Text.Json.JsonElement slide = answer.GetProperty("diff").GetProperty("slides").EnumerateArray().Single();
    slide.GetProperty("id").GetString().ShouldBe("two");
    slide.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe("title");
  }

  [Fact]
  public async Task Versions_list_the_presentation_s_commits_and_a_comparison_describes_the_one_it_is_against()
  {
    // Arrange
    using var deck = new DeckFixture();
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement versions =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/versions");
    string first = versions[1].GetProperty("commit").GetString()!;
    System.Text.Json.JsonElement changes =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/versions/changes?commit={first}");
    System.Text.Json.JsonElement compare =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare?base={first}");

    // Assert
    versions.GetArrayLength().ShouldBe(2);
    versions[0].GetProperty("subject").GetString().ShouldBe("retitled");
    changes.GetProperty("first").GetBoolean().ShouldBeTrue();
    compare.GetProperty("version").GetProperty("subject").GetString().ShouldBe("first");
    compare.GetProperty("diff").GetProperty("slides").GetArrayLength().ShouldBe(1);
  }

  [Fact]
  public async Task An_edit_patches_the_slide_file_once_and_a_second_from_the_same_drawing_is_a_conflict()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    var edit = new { slide = "one", edits = new[] { new { path = "title.frame", source = (string?)null,
      numbers = new[] { 60.0, 180, 840, 90 } } } };

    // Act
    HttpResponseMessage first = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    HttpResponseMessage second = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    first.StatusCode.ShouldBe(HttpStatusCode.OK);
    second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"))
      .ShouldContain("title: { text: Hello, frame: [60, 180, 840, 90] }");
    served.Store.Deck.Slides[0].Items!.Single(item => item.Id == "title").Frame.ShouldBe([60, 180, 840, 90]);
  }

  [Fact]
  public async Task An_edit_answers_with_its_undo_which_writes_the_file_back_as_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    var edit = new { slide = "one", edits = new[] { new { path = "title.frame", source = (string?)null,
      numbers = new[] { 60.0, 180, 840, 90 } } } };
    HttpResponseMessage moved = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    System.Text.Json.JsonElement undo = (await moved.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");

    // Act
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "one", edits = undo });

    // Assert
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task Deleting_an_element_answers_with_the_undo_that_writes_it_back()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      elements:
        - id: note
          text: A note
          frame: [40, 400, 300, 40]
        - id: box
          shape: rectangle
          frame: [400, 400, 100, 40]
      """);
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    ItemPayload item = served.Store.Deck.Slides[0].Items!.Single(candidate => candidate.Id == "note");
    var edit = new { slide = "one", edits = new[] { new { path = item.Edit.Delete!.Path,
      source = item.Edit.Delete.Source, delete = true } } };

    // Act
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    System.Text.Json.JsonElement undo = (await deleted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    bool gone = served.Store.Deck.Slides[0].Items!.All(candidate => candidate.Id != item.Id);
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "one", edits = undo });

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
    gone.ShouldBeTrue();
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task Moving_a_slide_reorders_the_deck_and_answers_with_the_move_that_undoes_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    var move = new { slide = "two", from = new { section = "Results" }, to = (object?)null };

    // Act
    HttpResponseMessage moved = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", move);
    System.Text.Json.JsonElement undo = (await moved.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    string[] order = served.Store.Deck.Slides.Select(slide => slide.Id).ToArray();
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", undo);

    // Assert
    moved.StatusCode.ShouldBe(HttpStatusCode.OK);
    order.ShouldBe(["two", "one"]);
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task Deleting_a_slide_removes_its_file_line_and_comments_and_the_undo_restores_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", CommentTests.TwoComments + "\n\n- { id: c3, slide: one, at: [0, 0], text: Keep }");
    await using Served served = await Served.Start(deck);
    string list = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    string slide = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    var delete = new { slide = "two", from = new { section = "Results" }, to = (object?)null, delete = true };

    // Act
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", delete);
    System.Text.Json.JsonElement undo = (await deleted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    string[] order = served.Store.Deck.Slides.Select(shown => shown.Id).ToArray();
    bool gone = !File.Exists(deck.Folder.PathTo("deck/slides/two.yaml"));
    string[] left = deck.Load().Comments.Select(comment => comment.Id).ToArray();
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", undo);

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
    order.ShouldBe(["one"]);
    gone.ShouldBeTrue();
    left.ShouldBe(["c3"]);
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(list);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBe(slide);
    deck.Load().Comments.Select(comment => $"{comment.Id} {comment.Text}").ShouldBe([
      "a1 Say what the results are", "b2 Too empty.\nAdd the chart from the book.", "c3 Keep"
    ], ignoreOrder: true);
  }

  [Fact]
  public async Task A_delete_that_fails_partway_leaves_the_slide_its_line_and_its_comments_as_they_were()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", CommentTests.TwoComments);
    await using Served served = await Served.Start(deck);
    string list = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    string comments = File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml"));
    var delete = new { slide = "two", from = new { section = "Results" }, to = (object?)null, delete = true };
    HttpResponseMessage response;

    // Act
    using (Undeletable(deck.Folder.PathTo("deck/slides/two.yaml")))
    {
      response = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", delete);
    }

    // Assert
    response.IsSuccessStatusCode.ShouldBeFalse();
    File.Exists(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBeTrue();
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(list);
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(comments);
  }

  /// <summary>Keeps a file from being deleted: held open on Windows, its folder made read-only elsewhere.</summary>
  private static IDisposable Undeletable(string path)
  {
    if (OperatingSystem.IsWindows())
    {
      return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    string folder = Path.GetDirectoryName(path)!;
    UnixFileMode mode = File.GetUnixFileMode(folder);
    File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    return new Restore(() => File.SetUnixFileMode(folder, mode));
  }

  [Fact]
  public async Task Reverting_an_element_writes_it_as_the_revision_had_it_and_answers_with_the_undo()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      elements:
        - id: note
          text: A note
          frame: [40, 400, 300, 40]
      """);
    string committed = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string edited = committed.Replace("text: A note", "text: A longer note").Replace("title: Hello", "title: Hi");
    deck.Folder.Write("deck/slides/one.yaml", edited);
    await using Served served = await Served.Start(deck);
    var revert = new { @base = "HEAD", slide = "one", id = "note", owner = "slide" };

    // Act
    HttpResponseMessage reverted = await served.Client.PostAsJsonAsync($"{served.Url}/api/revert", revert);
    string written = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    System.Text.Json.JsonElement undo = (await reverted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "one", edits = undo });

    // Assert
    reverted.StatusCode.ShouldBe(HttpStatusCode.OK);
    written.ShouldBe(committed.Replace("title: Hello", "title: Hi"));
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldBe(edited);
  }

  [Fact]
  public async Task Reverting_a_slide_writes_its_file_as_the_revision_had_it_and_answers_with_the_undo()
  {
    // Arrange
    using var deck = new DeckFixture();
    string committed = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    const string edited = "layout: cover\ntitle: Changed\n";
    deck.Folder.Write("deck/slides/two.yaml", edited);
    await using Served served = await Served.Start(deck);
    var revert = new { @base = "HEAD", slide = "two" };

    // Act
    HttpResponseMessage reverted = await served.Client.PostAsJsonAsync($"{served.Url}/api/revert", revert);
    string written = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    System.Text.Json.JsonElement undo = (await reverted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "two", edits = undo });

    // Assert
    reverted.StatusCode.ShouldBe(HttpStatusCode.OK);
    written.ShouldBe(committed);
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBe(edited);
  }

  [Fact]
  public async Task Reverting_an_element_the_theme_owns_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    await using Served served = await Served.Start(deck);
    var revert = new { @base = "HEAD", slide = "one", id = "page", owner = "theme" };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/revert", revert);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
  }

  [Fact]
  public async Task An_edit_to_a_key_the_mouse_never_writes_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    var edit = new
    {
      slide = "one",
      edits = new[] { new { path = "layout", source = "cover", numbers = new[] { 1.0 } } }
    };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldContain("layout: cover");
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
    for (int attempt = 0; attempt < 150 && store.Version == before; attempt++)
    {
      await Task.Delay(100);
    }

    // Assert
    store.Version.ShouldBeGreaterThan(before);
  }

  [Fact]
  public async Task A_commit_moves_the_head_version_so_the_page_compares_again_though_no_slide_changed()
  {
    // Arrange
    using var deck = new DeckFixture();
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    using var store = new PresentationStore(deck.Folder.PathTo("deck"), deck.Folder.PathTo("deck"), TextWriter.Null);
    long before = store.HeadVersion;

    // Act
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-a", "-m", "second");
    for (int attempt = 0; attempt < 150 && store.HeadVersion == before; attempt++)
    {
      await Task.Delay(100);
    }

    // Assert
    store.HeadVersion.ShouldBeGreaterThan(before);
  }

  [Theory]
  [InlineData("../../outside", false)]
  [InlineData("..\\..\\outside", false)]
  [InlineData("../presentation", true)]
  [InlineData("/tmp/outside", false)]
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
    File.Exists("/tmp/outside.yaml").ShouldBeFalse();
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
  public async Task A_powerpoint_export_refuses_a_slide_that_doesnt_load_rather_than_leave_it_out()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - { id: box, frame: [0, 0] }");
    await using Served served = await Served.Start(deck);

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/pptx", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    (await response.Content.ReadAsStringAsync()).ShouldContain("two doesn't load");
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
    clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
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
  public static IDisposable Unreadable(string path)
  {
    if (OperatingSystem.IsWindows())
    {
      return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    UnixFileMode mode = File.GetUnixFileMode(path);
    File.SetUnixFileMode(path, UnixFileMode.None);
    return new Restore(() => File.SetUnixFileMode(path, mode));
  }

  private sealed class Restore(Action action) : IDisposable
  {
    public void Dispose()
    {
      action();
    }
  }

  [Fact]
  public async Task Exporting_to_html_downloads_the_presentation_as_one_page_named_for_its_folder()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck, Served.Interface());

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/html", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Content.Headers.ContentDisposition.ShouldNotBeNull().FileName.ShouldBe("deck.html");
    string html = await response.Content.ReadAsStringAsync();
    html.ShouldContain("<title>Test deck</title>");
    html.ShouldNotContain("<script src=");
  }

  [Fact]
  public async Task Exporting_to_pdf_prints_a_page_per_slide_at_its_size_in_points_with_its_text_as_text()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck, Served.Interface());

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/pdf", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Content.Headers.ContentDisposition.ShouldNotBeNull().FileName.ShouldBe("deck.pdf");
    string pdf = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
    pdf.ShouldStartWith("%PDF-");
    pdf.ShouldContain("/Count 2");
    Regex.Matches(pdf, @"/MediaBox \[0 0 960 540\]").Count.ShouldBe(2);
    pdf.ShouldContain("/BaseFont");
    pdf.ShouldNotContain("/Subtype /Image");
  }

  [Fact]
  public async Task An_export_that_cannot_be_built_answers_with_why_for_the_page_to_show()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);

    // Act
    HttpResponseMessage unknown = await served.Client.PostAsync($"{served.Url}/api/export/docx", null);
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../missing\nslides: [one]");
    HttpResponseMessage broken = await served.Client.PostAsync($"{served.Url}/api/export/html", null);

    // Assert
    unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    (await unknown.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString()
      .ShouldBe("there is no docx export; choose html, pptx, or pdf");
    broken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    (await broken.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString()
      .ShouldBe("the presentation doesn't load; fix its error first");
  }

  [Fact]
  public void A_headless_server_starts_holding_none_of_the_commands_standard_streams()
  {
    // Arrange
    using var folder = new TempFolder();

    // Act
    System.Diagnostics.ProcessStartInfo start = HeadlessServer.StartInfo(folder.PathTo("deck"), folder.Root);

    // Assert
    start.RedirectStandardInput.ShouldBeFalse();
    start.RedirectStandardOutput.ShouldBeFalse();
    start.RedirectStandardError.ShouldBeFalse();
    if (OperatingSystem.IsWindows())
    {
      start.UseShellExecute.ShouldBeTrue();
    }
    else
    {
      start.FileName.ShouldBe("/bin/sh");
      start.ArgumentList[1].ShouldContain("</dev/null");
    }
  }

  [Fact]
  public void A_server_is_found_by_its_process_in_the_registry()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    registry.Register(new ServerInstance(Environment.ProcessId, folder.Root, port, true, ServerInstance.CurrentBuild));

    // Act
    ServerInstance? found = registry.Find(Environment.ProcessId);
    ServerInstance? other = registry.Find(Environment.ProcessId + 1);
    listener.Stop();

    // Assert
    found.ShouldNotBeNull().Url.ShouldBe($"http://localhost:{port}");
    other.ShouldBeNull();
  }

  [Fact]
  public async Task A_headless_server_that_stops_before_registering_names_its_log()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
      new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--version")
      {
        RedirectStandardOutput = true
      })!;

    // Act
    InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
      HeadlessServer.WaitForRegistration(process, registry, folder.PathTo("server.log")));

    // Assert
    error.Message.ShouldContain(folder.PathTo("server.log"));
  }


}