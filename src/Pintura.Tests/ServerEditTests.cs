using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ServerEditTests
{
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
    return new Restore(() => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, mode); });
  }

  [Fact]
  [Trait("Needs", "git")]
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
    GitHelper.Commit(deck.Folder.Root);
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
  [Trait("Needs", "git")]
  public async Task Reverting_a_slide_writes_its_file_as_the_revision_had_it_and_answers_with_the_undo()
  {
    // Arrange
    using var deck = new DeckFixture();
    string committed = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    GitHelper.Commit(deck.Folder.Root);
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
  [Trait("Needs", "git")]
  public async Task Reverting_an_element_a_layout_owns_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    await using Served served = await Served.Start(deck);
    var revert = new { @base = "HEAD", slide = "two", id = "page", owner = "layout" };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/revert", revert);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
  }

  [Fact]
  public async Task Moving_a_layout_s_area_writes_the_layout_file()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string file = served.Store.Theme!.Layouts.Single(layout => layout.Id == "cover").File;
    var edit = new
    {
      edits = new[]
      {
        new { file, path = "areas.left", source = "[60, 340, 400, 100]", numbers = new[] { 80.0, 340, 400, 120 } }
      }
    };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("theme/layouts/cover.yaml")).ShouldContain("left: [80, 340, 400, 120]");
  }

  [Fact]
  public async Task Edits_to_theme_yaml_and_a_layout_in_one_request_write_both_and_undo_as_one()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string layout = served.Store.Theme!.Layouts.Single(candidate => candidate.Id == "cover").File;
    string theme = layout.Replace("layouts/cover.yaml", "theme.yaml");
    string themeBefore = File.ReadAllText(deck.Folder.PathTo("theme/theme.yaml"));
    string layoutBefore = File.ReadAllText(deck.Folder.PathTo("theme/layouts/cover.yaml"));
    var edit = new
    {
      edits = new[]
      {
        new { file = theme, path = "areas.content", source = "[40, 100, 880, 380]",
          numbers = new[] { 50.0, 100, 880, 380 } },
        new { file = layout, path = "areas.left", source = "[60, 340, 400, 100]",
          numbers = new[] { 70.0, 340, 400, 100 } }
      }
    };

    // Act
    HttpResponseMessage moved = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    string themeMoved = File.ReadAllText(deck.Folder.PathTo("theme/theme.yaml"));
    string layoutMoved = File.ReadAllText(deck.Folder.PathTo("theme/layouts/cover.yaml"));
    System.Text.Json.JsonElement undo = (await moved.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { edits = undo });

    // Assert
    moved.StatusCode.ShouldBe(HttpStatusCode.OK);
    themeMoved.ShouldContain("content: [50, 100, 880, 380]");
    layoutMoved.ShouldContain("left: [70, 340, 400, 100]");
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("theme/theme.yaml")).ShouldBe(themeBefore);
    File.ReadAllText(deck.Folder.PathTo("theme/layouts/cover.yaml")).ShouldBe(layoutBefore);
  }

  [Fact]
  public async Task Deleting_an_element_on_a_layout_s_stage_removes_it_from_the_layout_and_undo_writes_it_back()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    ItemPayload page = served.Store.Theme!.Layouts.Single(candidate => candidate.Id == "content").Stage.Items!
      .Single(item => item.Id == "page");
    string before = File.ReadAllText(deck.Folder.PathTo("theme/layouts/content.yaml"));
    var edit = new { edits = new[] { new { file = page.Edit.File, path = page.Edit.Delete!.Path,
      source = page.Edit.Delete.Source, delete = true } } };

    // Act
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    string after = File.ReadAllText(deck.Folder.PathTo("theme/layouts/content.yaml"));
    System.Text.Json.JsonElement undo = (await deleted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { edits = undo });

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
    after.ShouldNotContain("page");
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("theme/layouts/content.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task An_edit_that_conflicts_in_one_file_leaves_the_request_s_other_files_unwritten()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string layout = served.Store.Theme!.Layouts.Single(candidate => candidate.Id == "cover").File;
    string theme = layout.Replace("layouts/cover.yaml", "theme.yaml");
    string themeBefore = File.ReadAllText(deck.Folder.PathTo("theme/theme.yaml"));
    var edit = new
    {
      edits = new[]
      {
        new { file = theme, path = "areas.content", source = "[40, 100, 880, 380]",
          numbers = new[] { 50.0, 100, 880, 380 } },
        new { file = layout, path = "areas.left", source = "[1, 2, 3, 4]", numbers = new[] { 70.0, 340, 400, 100 } }
      }
    };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    File.ReadAllText(deck.Folder.PathTo("theme/theme.yaml")).ShouldBe(themeBefore);
  }

  [Fact]
  public async Task Deleting_elements_together_answers_with_one_undo_that_writes_them_back_in_order()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      elements:
        - id: note
          text: A note
          frame: [40, 400, 300, 40]

        - id: box
          shape: rectangle
          frame: [400, 400, 100, 40]

        - id: last
          shape: rectangle
          frame: [600, 400, 100, 40]
      """);
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    var edits = served.Store.Deck.Slides[0].Items!.Where(item => item.Id is "note" or "box")
      .Select(item => new { path = item.Edit.Delete!.Path, source = item.Edit.Delete.Source, delete = true });
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit",
      new { slide = "one", edits });
    System.Text.Json.JsonElement undo = (await deleted.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");

    // Act
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "one", edits = undo });

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task A_fill_picked_for_a_slide_element_is_written_on_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      elements:
        - id: box
          shape: rectangle
          frame: [400, 400, 100, 40]
      """);
    await using Served served = await Served.Start(deck);
    var edit = new { slide = "one", edits = new[] { new { path = "elements.box.fill", source = (string?)null,
      value = "accent2 lighter 40" } } };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldContain("    fill: accent2 lighter 40");
  }

  [Fact]
  public async Task A_style_and_alignments_picked_for_a_text_box_are_written_on_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      elements:
        - id: note
          text: Hello
          frame: [400, 400, 100, 40]
      """);
    await using Served served = await Served.Start(deck);
    var edit = new { slide = "one", edits = new[] {
      new { path = "elements.note.style", source = (string?)null, value = "callout" },
      new { path = "elements.note.align", source = (string?)null, value = "center" },
      new { path = "elements.note.valign", source = (string?)null, value = "middle" } } };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"))
      .ShouldContain("    frame: [400, 400, 100, 40]\n    style: callout\n    align: center\n    valign: middle\n");
  }

  [Theory]
  [InlineData("elements.note.style", "headline")]
  [InlineData("elements.note.align", "middle")]
  [InlineData("elements.note.valign", "center")]
  [InlineData("title.style", "lead")]
  public async Task A_style_or_alignment_the_theme_and_format_don_t_name_is_refused(string path, string value)
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Folder.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      elements:
        - id: note
          text: Hello
          frame: [400, 400, 100, 40]
      """);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml"));
    await using Served served = await Served.Start(deck);
    var edit = new { slide = "one", edits = new[] { new { path, source = (string?)null, value } } };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/one.yaml")).ShouldBe(before);
  }

  [Fact]
  public async Task An_edit_to_a_file_outside_the_theme_s_layouts_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string file = served.Store.Theme!.Layouts.Single(layout => layout.Id == "cover").File
      .Replace("layouts/cover", "styles");
    var edit = new { edits = new[] { new { file, path = "shape.fill", source = "light2", value = "accent1" } } };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    File.ReadAllText(deck.Folder.PathTo("theme/styles.yaml")).ShouldContain("shape: { fill: light2");
  }

  [Fact]
  public async Task Typing_over_a_section_s_name_on_a_slide_renames_the_section_and_undo_names_it_back()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    FieldEditPayload section = served.Store.Deck.Slides.Single(slide => slide.Id == "two").Items!
      .Single(item => item.Id == "title").Text!.Paragraphs[0].Runs.Single(run => run.Field == "section").FieldEdit!;
    var edit = new
    {
      slide = "two",
      edits = new[] { new { file = section.File, path = section.Path, source = section.Source, value = "Outlook" } }
    };

    // Act
    HttpResponseMessage renamed = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);
    string after = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    string title = served.Store.Deck.Slides.Single(slide => slide.Id == "two").Title!;
    System.Text.Json.JsonElement undo = (await renamed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
      .GetProperty("undo");
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "two", edits = undo });

    // Assert
    renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
    after.ShouldBe(before.Replace("section: Results", "section: Outlook"));
    title.ShouldBe("Results in Outlook");
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(before);
  }

  [Theory]
  [InlineData("name", "Test deck")]
  [InlineData("theme", "../theme")]
  [InlineData("slides[1]", "one")]
  public async Task An_edit_to_presentation_yaml_beyond_its_fields_and_section_names_is_refused(string path,
    string source)
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    string before = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    string file = served.Store.Deck.Slides.Single(slide => slide.Id == "one").Items!
      .Single(item => item.Id == "subtitle").Text!.Paragraphs[0].Runs.Single().FieldEdit!.File;
    var edit = new { slide = "one", edits = new[] { new { file, path, source, value = "other" } } };

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", edit);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(before);
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


  private sealed class Restore(Action action) : IDisposable
  {
    public void Dispose()
    {
      action();
    }
  }


}
