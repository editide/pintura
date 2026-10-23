using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class CommentArchiveTests
{
  private static readonly DateTime Now = new DateTime(2026, 10, 7, 17, 30, 0);
  private static readonly Snapshot Seen = new Snapshot("abc1234", false);

  private const string Done = """
    - id: a1
      slide: two
      element: title
      at: [12, 8]
      by: Daniel
      date: 2026-10-07 13:00
      text: Say what the results are
      done: Named the 18% growth
    """;

  private const string Open = """
    - id: b2
      slide: two
      at: [480, 300]
      by: Daniel
      date: 2026-10-07 13:05
      text: Too empty
    """;

  [Fact]
  public void Resolving_a_comment_moves_it_to_the_archive_and_its_undo_reopens_it_as_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = Done + "\n\n" + Open;
    deck.Write("deck/comments.yaml", written);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "a1"), "Ana", Now, Seen);
    string[] left = deck.Load().Comments.Select(comment => comment.Id).ToArray();
    string archived = File.ReadAllText(deck.Folder.PathTo("deck/comments-resolved.yaml"));
    CommentFile.Apply(deck.Load(), undo, "Ana", Now, Seen);

    // Assert
    left.ShouldBe(["b2"]);
    archived.ShouldBe(Done + "\n  resolved: { by: Ana, date: 2026-10-07 17:30, rev: abc1234, outcome: accepted }\n");
    undo.ShouldBe(new CommentChange(Reopen: "a1", Before: "b2"));
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
    File.Exists(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldBeFalse();
  }

  [Theory]
  [InlineData("done: Did it", "accepted")]
  [InlineData("done: Did it\nreplies:\n  - { by: Daniel, text: Shorter, done: Cut it }", "partly")]
  [InlineData("done: Did it\nreplies:\n  - { by: Daniel, revert: true, done: Put it back }", "reverted")]
  [InlineData(
    "done: Did it\nreplies:\n  - { by: Daniel, text: Shorter, done: Cut it }\n  - { by: Daniel, revert: true }",
    "reverted")]
  [InlineData("skipped: No data", "skipped")]
  [InlineData("skipped: No data\nreplies:\n  - { by: Daniel, text: It's in the book, done: Added it }", "partly")]
  [InlineData("", "withdrawn")]
  [InlineData("done: Did it\nreplies:\n  - { by: Daniel, text: Shorter }", "withdrawn")]
  public void Resolving_records_how_the_thread_ended(string thread, string outcome)
  {
    // Arrange
    using var deck = new DeckFixture();
    string marks = thread.Length == 0 ? "" : "\n" + string.Join("\n", thread.Split('\n').Select(line => "  " + line));
    deck.Write("deck/comments.yaml", $"- id: a1\n  slide: two\n  at: [0, 0]\n  text: Do it{marks}\n");

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "a1"), "Ana", Now, Seen);

    // Assert
    Resolved(deck)[0].Resolved!.Outcome.ShouldBe(outcome);
  }

  [Fact]
  public void Resolving_adds_after_what_was_resolved_before_and_notes_changes_not_yet_committed()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done + "\n\n" + Open);

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "b2"), "Ana", Now, Seen);
    CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "a1"), "Ana", Now.AddMinutes(5),
      new Snapshot("def5678", true));

    // Assert
    File.Exists(deck.Folder.PathTo("deck/comments.yaml")).ShouldBeFalse();
    List<Comment> archived = Resolved(deck);
    archived.Select(comment => comment.Id).ShouldBe(["b2", "a1"]);
    archived[1].Resolved.ShouldBe(new Resolution("Ana", "2026-10-07 17:35", "def5678", true, "accepted"));
    File.ReadAllText(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldContain(
      "resolved: { by: Ana, date: 2026-10-07 17:35, rev: def5678, uncommitted: true, outcome: accepted }");
  }

  [Fact]
  public void Resolving_several_at_once_is_undone_at_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    string written = Done + "\n\n" + Open;
    deck.Write("deck/comments.yaml", written);
    var both = new CommentChange(Many: [new CommentChange(Resolve: "a1"), new CommentChange(Resolve: "b2")]);

    // Act
    CommentChange undo = CommentFile.Apply(deck.Load(), both, "Ana", Now, Seen);
    int archived = Resolved(deck).Count;
    CommentFile.Apply(deck.Load(), undo, "Ana", Now, Seen);

    // Assert
    archived.ShouldBe(2);
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(written + "\n");
    File.Exists(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldBeFalse();
  }

  [Fact]
  public void Resolving_a_comment_someone_already_resolved_is_a_conflict()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Open);

    // Act
    PatchException error = Should.Throw<PatchException>(() =>
      CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "a1"), "Ana", Now, Seen));

    // Assert
    error.Conflict.ShouldBeTrue();
    error.Message.ShouldBe("comment a1 is gone; someone resolved it");
  }

  [Fact]
  public void A_new_comment_and_a_reply_record_the_version_the_reviewer_saw()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done);
    var add = new CommentChange(Add: new Comment { Slide = "two", At = [1, 2], Text = "Hi" });

    // Act
    CommentFile.Apply(deck.Load(), add, "Ana", Now, new Snapshot("abc1234", true));
    CommentFile.Apply(deck.Load(), new CommentChange(Reply: "a1", Text: "Shorter"), "Ana", Now, Seen);

    // Assert
    List<Comment> comments = deck.Load().Comments;
    comments[1].Rev.ShouldBe("abc1234");
    comments[1].Uncommitted.ShouldBeTrue();
    comments[0].Replies![0].Rev.ShouldBe("abc1234");
    comments[0].Replies![0].Uncommitted.ShouldBeFalse();
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldContain(
      "  date: 2026-10-07 17:30\n  rev: abc1234\n  uncommitted: true\n  text: Hi");
  }

  [Fact]
  public void A_new_comment_outside_a_repository_records_no_version()
  {
    // Arrange
    using var deck = new DeckFixture();
    var add = new CommentChange(Add: new Comment { Slide = "two", At = [1, 2], Text = "Hi" });

    // Act
    CommentFile.Apply(deck.Load(), add, "Ana", Now);

    // Assert
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldNotContain("rev:");
  }

  [Fact]
  public void A_new_comment_takes_an_id_no_resolved_comment_had()
  {
    // Arrange
    using var deck = new DeckFixture();
    string many = string.Join("\n\n", Enumerable.Range(0, 400).Select(index =>
      $"- id: x{index:000}\n  slide: two\n  at: [0, 0]\n  text: Old\n  resolved: {{ outcome: accepted }}"));
    deck.Write("deck/comments-resolved.yaml", many);
    var add = new CommentChange(Add: new Comment { Slide = "two", At = [1, 2], Text = "Hi" });

    // Act
    CommentFile.Apply(deck.Load(), add, "Ana", Now);

    // Assert
    string id = deck.Load().Comments[0].Id;
    Resolved(deck).ShouldNotContain(comment => comment.Id == id);
  }

  [Fact]
  public void A_live_comment_carrying_a_resolution_fails_to_load()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done + "\n  resolved: { outcome: accepted }");

    // Act
    LoadException error = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    error.Message.ShouldContain("resolved");
  }

  [Fact]
  public void Comments_resolved_lists_the_archive_in_the_order_it_was_resolved_with_each_outcome()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done + "\n\n" + Open);
    CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "b2"), "Ana", Now, Seen);
    CommentFile.Apply(deck.Load(), new CommentChange(Resolve: "a1"), "Ana", Now, Seen);
    var output = new StringWriter();

    // Act
    int exit = CommentList.WriteResolved(deck.Folder.PathTo("deck"), false, output);

    // Assert
    exit.ShouldBe(0);
    string text = output.ToString().ReplaceLineEndings("\n");
    text.ShouldStartWith("b2  two at [480, 300]  withdrawn by Ana, 2026-10-07 17:30 at abc1234\n");
    text.ShouldContain("a1  two/title  accepted by Ana, 2026-10-07 17:30 at abc1234\n" +
      "    Daniel, 2026-10-07 13:00: Say what the results are\n    done: Named the 18% growth\n");
    text.ShouldEndWith("2 resolved comments: 1 accepted, 1 withdrawn.\n");
  }

  [Fact]
  public void Comments_resolved_without_an_archive_says_so()
  {
    // Arrange
    using var deck = new DeckFixture();
    var output = new StringWriter();

    // Act
    CommentList.WriteResolved(deck.Folder.PathTo("deck"), false, output);

    // Assert
    output.ToString().Trim().ShouldBe("no resolved comments");
  }

  [Fact]
  public void The_check_warns_when_comments_go_uncommitted_in_a_repository()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Open);
    deck.Write("deck/comments-resolved.yaml", Done + "\n  resolved: { outcome: accepted }");
    string root = deck.Folder.PathTo("");
    RevisionTests.Git(root, "init", "-q");
    RevisionTests.Git(root, "add", "deck/presentation.yaml", "deck/slides", "theme");
    RevisionTests.Git(root, "commit", "-q", "-m", "First");

    // Act
    List<Finding> untracked = Checks.Comments(deck.Load());
    RevisionTests.Git(root, "add", "deck/comments.yaml", "deck/comments-resolved.yaml");
    List<Finding> tracked = Checks.Comments(deck.Load());

    // Assert
    untracked.Select(finding => finding.Message).ShouldBe([
      "comments.yaml isn't in git, so a thread resolved from it leaves no history; commit it with the slides",
      "comments-resolved.yaml isn't in git, so the resolved threads in it have no history; commit it with the slides"
    ]);
    tracked.ShouldBeEmpty();
  }

  [Fact]
  public void The_check_says_nothing_of_comments_outside_a_repository()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Open);

    // Act
    List<Finding> findings = Checks.Comments(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_snapshot_is_the_commit_and_whether_the_deck_changed_since_leaving_comments_aside()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("");
    RevisionTests.Git(root, "init", "-q");
    RevisionTests.Git(root, "add", "-A");
    RevisionTests.Git(root, "commit", "-q", "-m", "First");
    string head = Revisions.Run(root, "rev-parse", "--short", "HEAD").Output.Trim();

    // Act
    Snapshot? clean = Saves.Snapshot(deck.Folder.PathTo("deck"));
    deck.Write("deck/comments.yaml", Open);
    deck.Write("deck/comments-resolved.yaml", Done + "\n  resolved: { outcome: accepted }");
    Snapshot? commented = Saves.Snapshot(deck.Folder.PathTo("deck"));
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Snapshot? edited = Saves.Snapshot(deck.Folder.PathTo("deck"));

    // Assert
    clean.ShouldBe(new Snapshot(head, false));
    commented.ShouldBe(new Snapshot(head, false));
    edited.ShouldBe(new Snapshot(head, true));
    Saves.Status(deck.Folder.PathTo("deck")).Unsaved.ShouldBeTrue();
  }

  [Fact]
  public void Changing_only_the_archive_leaves_nothing_to_save()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("");
    RevisionTests.Git(root, "init", "-q");
    RevisionTests.Git(root, "add", "-A");
    RevisionTests.Git(root, "commit", "-q", "-m", "First");

    // Act
    deck.Write("deck/comments-resolved.yaml", Done + "\n  resolved: { outcome: accepted }");

    // Assert
    Saves.Status(deck.Folder.PathTo("deck")).Unsaved.ShouldBeFalse();
    Saves.Snapshot(deck.Folder.PathTo("deck")).ShouldNotBeNull();
  }

  [Fact]
  public void Outside_a_repository_there_is_no_snapshot()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    Snapshot? snapshot = Saves.Snapshot(deck.Folder.PathTo("deck"));

    // Assert
    snapshot.ShouldBeNull();
  }

  [Fact]
  public async Task The_page_resolves_a_comment_into_the_archive_and_its_undo_reopens_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done);
    await using Served served = await Served.Start(deck);

    // Act
    HttpResponseMessage resolved = await served.Client.PostAsJsonAsync($"{served.Url}/api/comments",
      new { resolve = "a1" });
    JsonElement undo = (await resolved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("undo");
    bool archived = Resolved(deck).Any(comment => comment.Id == "a1");
    HttpResponseMessage reopened = await served.Client.PostAsJsonAsync($"{served.Url}/api/comments", undo);

    // Assert
    resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
    archived.ShouldBeTrue();
    undo.GetProperty("reopen").GetString().ShouldBe("a1");
    reopened.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/comments.yaml")).ShouldBe(Done + "\n");
    served.Store.Deck.Comments.Select(comment => comment.Id).ShouldBe(["a1"]);
  }

  [Fact]
  public async Task Deleting_a_slide_archives_its_comments_and_the_undo_reopens_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done + "\n\n" + Open + "\n\n- { id: c3, slide: one, at: [0, 0], text: Keep }");
    await using Served served = await Served.Start(deck);
    var delete = new { slide = "two", from = new { section = "Results" }, to = (object?)null, delete = true };

    // Act
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", delete);
    JsonElement undo = (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("undo");
    string[] archived = Resolved(deck)
      .Select(comment => $"{comment.Id} {comment.Resolved!.Outcome}").ToArray();
    HttpResponseMessage undone = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", undo);

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
    archived.ShouldBe(["a1 removed", "b2 removed"]);
    undone.StatusCode.ShouldBe(HttpStatusCode.OK);
    deck.Load().Comments.Select(comment => comment.Id).ShouldBe(["a1", "b2", "c3"], ignoreOrder: true);
    File.Exists(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldBeFalse();
  }


  [Fact]
  public void Resolve_resolves_the_comments_named_and_says_how_each_ended()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done + "\n\n" + Open);
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Resolve, null, null, false, output,
      ids: ["a1"]);

    // Assert
    exit.ShouldBe(0);
    output.ToString().Trim().ShouldBe("resolved a1 (accepted) into comments-resolved.yaml; 1 comment left");
    deck.Load().Comments.Select(comment => comment.Id).ShouldBe(["b2"]);
  }

  [Fact]
  public void Resolve_with_an_id_that_isnt_there_resolves_none()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/comments.yaml", Done);

    // Act
    InvalidDataException error = Should.Throw<InvalidDataException>(() => HeadlessRunner.Run(
      deck.Folder.PathTo("deck"), HeadlessCommand.Resolve, null, null, false, new StringWriter(), ids: ["a1", "zz"]));

    // Assert
    error.Message.ShouldBe("comment zz is gone; someone resolved it");
    deck.Load().Comments.Select(comment => comment.Id).ShouldBe(["a1"]);
  }

  [Fact]
  public void The_command_line_resolves_by_id_and_lists_what_was_resolved()
  {
    // Act
    CommandLineOptions resolve = CommandLineOptions.Parse(["deck", "--resolve", "a1,b2"]);
    CommandLineOptions list = CommandLineOptions.Parse(["deck", "--comments", "--resolved", "--json"]);

    // Assert
    resolve.Command.ShouldBe(HeadlessCommand.Resolve);
    resolve.Ids.ShouldBe(["a1", "b2"]);
    list.Command.ShouldBe(HeadlessCommand.Comments);
    list.Resolved.ShouldBeTrue();
    list.Json.ShouldBeTrue();
    Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(["deck", "--check", "--resolved"]))
      .Message.ShouldBe("--resolved requires --comments.");
  }


  private static List<Comment> Resolved(DeckFixture deck)
  {
    return CommentFile.LoadResolved(deck.Folder.PathTo("deck"), Directory.GetCurrentDirectory());
  }


}