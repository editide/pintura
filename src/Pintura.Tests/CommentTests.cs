using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class CommentTests
{
  private static readonly DateTime Now = new DateTime(2026, 10, 7, 14, 3, 0);

  public const string TwoComments = """
    - id: a1
      slide: two
      element: title
      at: [12, 8]
      by: Daniel
      date: 2026-10-07 13:00
      text: Say what the results are

    - id: b2
      slide: two
      at: [480, 300]
      by: Daniel
      date: 2026-10-07 13:05
      text: |-
        Too empty.
        Add the chart from the book.
    """;

  private const string Thread = """
    - id: a1
      slide: two
      element: title
      at: [12, 8]
      by: Daniel
      date: 2026-10-07 13:00
      text: Say what the results are
      done: Named the 18% growth
      replies:
        - by: Daniel
          date: 2026-10-07 15:00
          text: Shorter
          done: Cut it to six words
        - by: Daniel
          date: 2026-10-07 16:00
          revert: true
    """;

  [Fact]
  public void Comments_load_in_file_order_with_the_line_each_starts_on()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);

    // Act
    List<Comment> comments = deck.Load().Comments;

    // Assert
    comments.Select(comment => comment.Id).ShouldBe(["a1", "b2"]);
    comments[0].Slide.ShouldBe("two");
    comments[0].Element.ShouldBe("title");
    comments[0].At.ShouldBe([12, 8]);
    comments[0].By.ShouldBe("Daniel");
    comments[0].Date.ShouldBe("2026-10-07 13:00");
    comments[1].Element.ShouldBeNull();
    comments[1].Text.ShouldBe("Too empty.\nAdd the chart from the book.");
    comments[1].Origin.ToString().ShouldBe("deck/comments.yaml:9");
  }

  [Fact]
  public void A_comment_with_an_unknown_key_fails_to_load_at_its_line()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- id: a1\n  slide: two\n  at: [0, 0]\n  text: Hi\n  colour: red");

    // Act
    LoadException error = Should.Throw<LoadException>(deck.Load);

    // Assert
    error.Problem.ShouldBe("unknown key colour");
    error.Origin.ToString().ShouldBe("deck/comments.yaml:5");
    error.Topic.ShouldBe("comments");
  }

  [Fact]
  public void Two_comments_with_one_id_fail_to_load()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- { id: a1, slide: one, at: [0, 0], text: Hi }\n" +
      "- { id: a1, slide: two, at: [0, 0], text: Hi }");

    // Act
    LoadException error = Should.Throw<LoadException>(deck.Load);

    // Assert
    error.Problem.ShouldBe("comment a1 is written twice");
    error.Origin.Line.ShouldBe(2);
  }

  [Fact]
  public void A_comment_an_agent_finished_or_passed_on_carries_its_note()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments + "\n  done: Named the 18% growth\n\n" +
      "- { id: c3, slide: one, at: [0, 0], text: Add a logo, skipped: The theme has none }");

    // Act
    List<Comment> comments = deck.Load().Comments;

    // Assert
    comments.Select(comment => (comment.Done, comment.Skipped)).ShouldBe([
      (null, null), ("Named the 18% growth", null), (null, "The theme has none")
    ]);
  }

  [Fact]
  public void A_comment_both_done_and_skipped_fails_to_load()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- { id: a1, slide: one, at: [0, 0], text: Hi, done: Yes, skipped: No }");

    // Act
    LoadException error = Should.Throw<LoadException>(deck.Load);

    // Assert
    error.Problem.ShouldBe("comment a1 is either done or skipped, not both");
  }

  [Fact]
  public void Adding_a_comment_writes_it_after_those_on_its_slide_and_earlier_ones_and_answers_with_its_removal()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);
    var draft = new Comment { Slide = "one", Element = "subtitle", At = [4.5, 2], Text = "Date it" };

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Add: draft), "Ana", Now);

    // Assert
    string written = File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml"));
    written.ShouldBe($"""
      - id: {undo.Remove}
        slide: one
        element: subtitle
        at: [4.5, 2]
        by: Ana
        date: 2026-10-07 14:03
        text: Date it

      {TwoComments}

      """);
    undo.Remove!.Length.ShouldBe(4);
    deck.Load().Comments.Select(comment => comment.Slide).ShouldBe(["one", "two", "two"]);
  }

  [Fact]
  public void Adding_a_comment_to_a_presentation_without_any_creates_the_file()
  {
    // Arrange
    using var deck = new DeckFixture();
    var draft = new Comment { Slide = "two", At = [100, 200], Text = "Needs: a chart\nand a source" };

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Add: draft), "Ana", Now);

    // Assert
    Comment comment = deck.Load().Comments.ShouldHaveSingleItem();
    comment.Text.ShouldBe("Needs: a chart\nand a source");
    comment.Element.ShouldBeNull();
  }

  [Fact]
  public void Removing_a_comment_leaves_the_others_as_written_and_answers_with_the_comment_to_add_back()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Remove: "a1"), "Ana", Now);
    string removed = File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml"));
    CommentFile.Apply(deck.Load(), undo, "Ana", Now);

    // Assert
    removed.ShouldBe(TwoComments[TwoComments.IndexOf("- id: b2", StringComparison.Ordinal)..] + "\n");
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(TwoComments + "\n");
  }

  [Fact]
  public void Removing_the_last_comment_deletes_the_file()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- { id: a1, slide: one, at: [0, 0], text: Hi }");

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Remove: "a1"), "Ana", Now);

    // Assert
    File.Exists(deck.Folder.PathTo("deck/comments.yaml")).ShouldBeFalse();
  }

  [Fact]
  public void Rewording_a_comment_replaces_its_text_and_answers_with_the_comment_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Id: "b2", Text: "Fine now"), "Ana", Now);

    // Assert
    deck.Load().Comments.Select(comment => comment.Text).ShouldBe(["Say what the results are", "Fine now"]);
    undo.Restore!.Id.ShouldBe("b2");
    undo.Restore.Text.ShouldBe("Too empty.\nAdd the chart from the book.");
  }

  [Fact]
  public void Rewording_a_done_comment_reopens_it_and_its_undo_marks_it_done_again()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = TwoComments + "\n  done: Added the margin chart";
    deck.Write("deck/comments.yaml", written);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Id: "b2", Text: "Now cite it"), "Ana",
      Now);
    Comment reopened = deck.Load().Comments[1];
    CommentFile.Apply(deck.Load(), undo, "Ana", Now);

    // Assert
    reopened.Text.ShouldBe("Now cite it");
    reopened.Done.ShouldBeNull();
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
  }

  [Fact]
  public void A_thread_loads_each_reply_with_its_note_and_waits_on_whatever_its_last_reply_does()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread);

    // Act
    Comment comment = deck.Load().Comments.ShouldHaveSingleItem();

    // Assert
    comment.Done.ShouldBe("Named the 18% growth");
    comment.Replies!.Select(reply => (reply.By, reply.Date, reply.Text, reply.Revert, reply.Done)).ShouldBe([
      ("Daniel", "2026-10-07 15:00", "Shorter", false, "Cut it to six words"),
      ("Daniel", "2026-10-07 16:00", null, true, null)
    ]);
    (comment.LastDone, comment.LastSkipped).ShouldBe((null, null));
  }

  [Fact]
  public void A_reply_to_what_no_agent_has_marked_fails_to_load_at_its_line()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- id: a1\n  slide: two\n  at: [0, 0]\n  text: Hi\n  replies:\n" +
      "    - text: Also this");

    // Act
    LoadException error = Should.Throw<LoadException>(deck.Load);

    // Assert
    error.Problem.ShouldBe("comment a1 has a reply before an agent marked what it answers");
    error.Origin.Line.ShouldBe(6);
  }

  [Fact]
  public void A_revert_of_what_an_agent_skipped_fails_to_load()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- id: a1\n  slide: two\n  at: [0, 0]\n  text: Hi\n  skipped: No\n" +
      "  replies:\n    - revert: true");

    // Act
    LoadException error = Should.Throw<LoadException>(deck.Load);

    // Assert
    error.Problem.ShouldBe("comment a1 reverts what an agent skipped rather than did");
  }

  [Fact]
  public void Replying_to_a_done_comment_adds_the_reviewers_reply_and_its_undo_takes_it_back()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = TwoComments + "\n  done: Added the margin chart";
    deck.Write("deck/comments.yaml", written);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Reply: "b2", Text: "Now cite it"), "Ana",
      Now);
    string replied = File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml"));
    CommentFile.Apply(deck.Load(), undo, "Ana", Now);

    // Assert
    replied.ShouldBe(written + """

        replies:
          - by: Ana
            date: 2026-10-07 14:03
            text: Now cite it

      """);
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
  }

  [Fact]
  public void Reverting_a_done_reply_adds_a_revert_that_needs_no_words()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread[..Thread.LastIndexOf("    - by:", StringComparison.Ordinal)].TrimEnd());

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Reply: "a1", Revert: true), "Ana", Now);

    // Assert
    Reply revert = deck.Load().Comments[0].Replies![^1];
    (revert.By, revert.Text, revert.Revert).ShouldBe(("Ana", null, true));
  }

  [Fact]
  public void Replying_to_a_comment_still_waiting_on_an_agent_is_a_conflict()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread);

    // Act
    PatchException error = Should.Throw<PatchException>(() =>
      CommentFile.Apply(deck.Load(), new CommentChange(Reply: "a1", Text: "And this"), "Ana", Now));

    // Assert
    error.Conflict.ShouldBeTrue();
    error.Message.ShouldBe("comment a1 still waits on an agent; reword it instead");
  }

  [Fact]
  public void Rewording_a_thread_rewords_its_last_reply()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread);

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Id: "a1", Text: "Put the old title back"), "Ana", Now);

    // Assert
    Comment comment = deck.Load().Comments[0];
    comment.Text.ShouldBe("Say what the results are");
    comment.Replies!.Select(reply => reply.Text).ShouldBe(["Shorter", "Put the old title back"]);
  }

  [Fact]
  public void Resolving_a_thread_and_undoing_it_writes_it_back_as_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = Thread + "\n\n" + TwoComments[TwoComments.IndexOf("- id: b2", StringComparison.Ordinal)..];
    deck.Write("deck/comments.yaml", written);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Remove: "a1"), "Ana", Now);
    CommentFile.Apply(deck.Load(), undo, "Ana", Now);

    // Assert
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
  }

  [Fact]
  public void Resolving_several_comments_at_once_is_undone_at_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = TwoComments + "\n\n- { id: c3, slide: two, at: [0, 0], text: Keep }";
    deck.Write("deck/comments.yaml", written);
    var both = new CommentChange(Many: [new CommentChange(Remove: "a1"), new CommentChange(Remove: "b2")]);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), both, "Ana", Now);
    string[] left = deck.Load().Comments.Select(comment => comment.Id).ToArray();
    CommentFile.Apply(deck.Load(), undo, "Ana", Now);

    // Assert
    left.ShouldBe(["c3"]);
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
  }

  [Fact]
  public void Resolving_several_comments_when_one_is_gone_resolves_none()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);
    var both = new CommentChange(Many: [new CommentChange(Remove: "a1"), new CommentChange(Remove: "zz")]);

    // Act
    PatchException error = Should.Throw<PatchException>(() => CommentFile.Apply(deck.Load(), both, "Ana", Now));

    // Assert
    error.Conflict.ShouldBeTrue();
    deck.Load().Comments.Select(comment => comment.Id).ShouldBe(["a1", "b2"]);
  }

  [Fact]
  public void Changing_a_comment_someone_already_removed_is_a_conflict()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments);

    // Act
    PatchException error = Should.Throw<PatchException>(() =>
      CommentFile.Apply(deck.Load(), new CommentChange(Id: "zz", Text: "Hi"), "Ana", Now));

    // Assert
    error.Conflict.ShouldBeTrue();
    error.Message.ShouldBe("comment zz is gone; someone resolved it");
  }

  [Fact]
  public void A_comment_must_be_on_a_slide_of_the_presentation_and_an_element_of_that_slide()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation presentation = deck.Load();

    // Act
    PatchException slide = Should.Throw<PatchException>(() => CommentFile.Apply(presentation,
      new CommentChange(Add: new Comment { Slide = "three", At = [0, 0], Text = "Hi" }), "Ana", Now));
    PatchException element = Should.Throw<PatchException>(() => CommentFile.Apply(presentation,
      new CommentChange(Add: new Comment { Slide = "two", Element = "chart", At = [0, 0], Text = "Hi" }), "Ana",
      Now));
    PatchException empty = Should.Throw<PatchException>(() => CommentFile.Apply(presentation,
      new CommentChange(Add: new Comment { Slide = "two", At = [0, 0], Text = "  " }), "Ana", Now));

    // Assert
    slide.Message.ShouldBe("no slide three");
    element.Message.ShouldBe("no element chart on two");
    empty.Message.ShouldBe("a comment needs words");
  }

  [Fact]
  public void Comments_lists_them_in_deck_order_with_the_line_that_writes_what_each_points_at()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments + "\n\n- { id: c3, slide: one, element: subtitle, at: [0, 0], " +
      "text: Date it, done: Dated it March 2026 }\n" +
      "- { id: d4, slide: two, element: page, at: [0, 0], text: Drop the number, skipped: The layout sets it }");
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Comments, null, null, false, output);

    // Assert
    exit.ShouldBe(0);
    string root = Shown(deck.Folder.PathTo("deck"));
    string theme = Shown(deck.Folder.PathTo("theme"));
    output.ToString().ReplaceLineEndings("\n").ShouldBe($"""
      c3  one/subtitle  {root}/slides/one.yaml:4  done
          Date it
          done: Dated it March 2026

      a1  two/title  {root}/slides/two.yaml:2
          Daniel, 2026-10-07 13:00: Say what the results are

      b2  two at [480, 300]  {root}/slides/two.yaml:1
          Daniel, 2026-10-07 13:05: Too empty.
          Add the chart from the book.

      d4  two/page  {theme}/layouts/content.yaml:3  skipped
          Drop the number
          skipped: The layout sets it

      4 comments in {root}/comments.yaml: 2 open, 1 done, 1 skipped.
      Act on each open one, then mark it done: with what you changed, or skipped: with why, for the reviewer to resolve; in a thread, mark its last reply.

      """);
  }

  [Fact]
  public void Comments_lists_a_thread_in_the_order_it_was_written()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread);
    var output = new StringWriter();

    // Act
    HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Comments, null, null, false, output);

    // Assert
    string root = Shown(deck.Folder.PathTo("deck"));
    output.ToString().ReplaceLineEndings("\n").ShouldStartWith($"""
      a1  two/title  {root}/slides/two.yaml:2
          Daniel, 2026-10-07 13:00: Say what the results are
          done: Named the 18% growth
          Daniel, 2026-10-07 15:00: Shorter
          done: Cut it to six words
          Daniel, 2026-10-07 16:00: revert

      1 comment in {root}/comments.yaml: 1 open, 0 done, 0 skipped.
      """);
  }

  [Fact]
  public void Comments_as_json_carry_a_threads_replies()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Thread);
    var output = new StringWriter();

    // Act
    HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Comments, null, null, true, output);

    // Assert
    JsonElement replies = JsonDocument.Parse(output.ToString()).RootElement[0].GetProperty("replies");
    replies[0].GetProperty("done").GetString().ShouldBe("Cut it to six words");
    replies[0].TryGetProperty("revert", out _).ShouldBeFalse();
    replies[1].GetProperty("revert").GetBoolean().ShouldBeTrue();
  }

  [Fact]
  public void Comments_as_json_carry_the_file_and_line_of_what_each_points_at()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", TwoComments.Replace("text: Say what", "done: Said it\n  text: Say what"));
    var output = new StringWriter();

    // Act
    HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Comments, null, null, true, output);

    // Assert
    JsonElement first = JsonDocument.Parse(output.ToString()).RootElement[0];
    first.GetProperty("id").GetString().ShouldBe("a1");
    first.GetProperty("text").GetString().ShouldBe("Say what the results are");
    first.GetProperty("file").GetString()!.ShouldEndWith("slides/two.yaml");
    first.GetProperty("line").GetInt32().ShouldBe(2);
    first.GetProperty("done").GetString().ShouldBe("Said it");
  }

  [Fact]
  public void A_presentation_without_comments_says_so()
  {
    // Arrange
    using var deck = new DeckFixture();
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Comments, null, null, false, output);

    // Assert
    exit.ShouldBe(0);
    output.ToString().ReplaceLineEndings("\n").ShouldBe("no comments\n");
  }

  [Fact]
  public void The_check_warns_of_a_comment_on_a_slide_or_element_that_no_longer_exists()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", "- { id: a1, slide: gone, at: [0, 0], text: Hi }\n" +
      "- { id: b2, slide: two, element: chart, at: [0, 0], text: Hi }\n" +
      "- { id: c3, slide: two, element: title, at: [0, 0], text: Hi }");

    // Act
    List<Finding> findings = Checks.Comments(deck.Load());

    // Assert
    findings.Select(finding => $"{finding.Level} {finding.Kind} {finding.Where}: {finding}").ShouldBe([
      "warning comments gone: comment a1 is on slide gone, which no longer exists; move it to a slide or resolve it " +
        "in deck/comments.yaml:1; see pintura --docs comments",
      "warning comments two/chart: comment b2 is on element chart, which no longer exists; move it to an element " +
        "or resolve it in deck/comments.yaml:2; see pintura --docs comments"
    ]);
  }

  [Fact]
  public async Task The_page_adds_a_comment_and_its_undo_removes_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);
    var add = new { add = new { slide = "two", element = "title", at = new[] { 10, 5 }, text = "Hi" } };

    // Act
    HttpResponseMessage added = await served.Client.PostAsJsonAsync($"{served.Url}/api/comments", add);
    JsonElement undo = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("undo");
    JsonElement shown = (await served.Client.GetFromJsonAsync<JsonElement>($"{served.Url}/api/presentation"))
      .GetProperty("comments")[0];
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/comments", undo);

    // Assert
    added.StatusCode.ShouldBe(HttpStatusCode.OK);
    shown.GetProperty("text").GetString().ShouldBe("Hi");
    shown.GetProperty("id").GetString().ShouldBe(undo.GetProperty("remove").GetString());
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.Exists(deck.Folder.PathTo("deck/comments.yaml")).ShouldBeFalse();
    served.Store.Deck.Comments.ShouldBeEmpty();
  }

  private static string Shown(string folder)
  {
    return SourceFile.For(folder, Directory.GetCurrentDirectory()).Display;
  }


}