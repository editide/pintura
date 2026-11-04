using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class PickTests
{
  private static readonly DateTime Now = new DateTime(2026, 11, 2, 18, 40, 0);
  private static readonly Snapshot Seen = new Snapshot("8b21d07", false, new HashSet<string>());

  private const string Picks = """
    - id: p4kx
      by: Claude
      date: 2026-11-02 10:14
      rev: 3f9a1c2
      variants:
        - slide: two
          intent: Two charts, side by side
        - slide: stacked
          intent: One stacked chart
        - slide: waterfall
          intent: A waterfall
    """;

  private const string List = """
    name: Test deck
    theme: ../theme
    fields:
      footer: Confidential
    slides:
      - one
      - section: Results
      - pick: p4kx
    """;

  private static DeckFixture Offering()
  {
    var deck = new DeckFixture();
    deck.Write("deck/picks.yaml", Picks);
    deck.Write("deck/presentation.yaml", List);
    deck.Write("deck/slides/stacked.yaml", "layout: content\ntitle: Stacked");
    deck.Write("deck/slides/waterfall.yaml", "layout: content\ntitle: Waterfall");
    return deck;
  }

  [Fact]
  public void A_pick_draws_its_first_variant_in_its_place_and_keeps_the_others_aside()
  {
    // Arrange
    using DeckFixture deck = Offering();

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Slides.Select(slide => slide.Id).ShouldBe(["one", "two"]);
    presentation.Slides[1].Pick.ShouldBe("p4kx");
    presentation.Variants.Select(slide => (slide.Id, slide.Number, slide.Section, slide.Pick))
      .ShouldBe([("stacked", 2, "Results", "p4kx"), ("waterfall", 2, "Results", "p4kx")]);
    presentation.Offered.Select(slide => slide.Id).ShouldBe(["one", "two", "stacked", "waterfall"]);
    presentation.Sections[1].Slides.ShouldBe(["two"]);
  }

  [Fact]
  public void The_preview_serves_the_pictures_every_variant_places()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/media/stacked.png", "png");
    deck.Write("deck/slides/stacked.yaml",
      "layout: content\nelements:\n  - id: picture\n    picture: ../media/stacked.png\n    frame: [40, 100, 400, 300]");

    // Act
    HashSet<string> media = PresentationStore.Media(deck.Load());

    // Assert
    media.ShouldContain(deck.Folder.PathTo("deck/media/stacked.png"));
  }

  [Theory]
  [InlineData("  - pick: zzzz", "no open pick zzzz in picks.yaml")]
  [InlineData("  - pick: p4kx\n  - stacked", "slide stacked is both in slides and offered in pick p4kx")]
  [InlineData("  - stacked\n  - pick: p4kx", "slide stacked is both in slides and offered in pick p4kx")]
  [InlineData("  - pick: p4kx\n  - pick: p4kx", "pick p4kx is listed twice")]
  public void A_list_that_misplaces_a_pick_fails_to_load(string entries, string problem)
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/presentation.yaml",
      $"name: Test deck\ntheme: ../theme\nfields:\n  footer: x\nslides:\n  - one\n{entries}");

    // Act
    LoadException error = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    error.Problem.ShouldBe(problem);
  }

  [Fact]
  public void The_checks_warn_of_an_open_pick_and_reach_its_later_variants()
  {
    // Arrange
    using DeckFixture deck = Offering();
    File.Delete(deck.Folder.PathTo("deck/slides/waterfall.yaml"));
    deck.Write("deck/slides/stacked.yaml", "layout: content\ntitle: { text: Stacked, frame: [40, 30, 880, 56] }");

    // Act
    List<Finding> findings = Checks.All(deck.Load(), new Measurement([], []), new FontSetting(false, false), false);

    // Assert
    findings.Select(finding => (finding.Level, finding.Kind, finding.Where)).ShouldBe(
    [
      ("error", "structure", "waterfall"),
      ("warning", "consistency", "stacked/title"),
      ("warning", "picks", "two")
    ]);
    findings[2].ToString().ShouldBe("pick p4kx waits on the reviewer to keep one of two, stacked, waterfall; the " +
      "export draws two in deck/picks.yaml:1; see pintura --docs picks");
  }

  [Theory]
  [InlineData("two", "pick p4kx offers only two; offer two or three variants, or list the slide in slides")]
  [InlineData("two,stacked,waterfall,four",
    "pick p4kx offers 4 variants, two, stacked, waterfall, four; offer the three most different at most")]
  public void A_pick_of_one_variant_or_more_than_three_is_an_error(string slides, string problem)
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/slides/four.yaml", "layout: content");
    IEnumerable<string> variants = slides.Split(',').Select(slide => $"    - {{ slide: {slide}, intent: It }}");
    deck.Write("deck/picks.yaml", $"- id: p4kx\n  variants:\n{string.Join("\n", variants)}");

    // Act
    Finding finding = Checks.Picks(deck.Load()).ShouldHaveSingleItem();

    // Assert
    (finding.Level, finding.Message).ShouldBe(("error", problem));
  }

  [Fact]
  public void Picks_lists_each_open_pick_with_its_place_and_variants()
  {
    // Arrange
    using DeckFixture deck = Offering();
    var output = new StringWriter();

    // Act
    PickList.Write(deck.Load(), false, output);

    // Assert
    output.ToString().ReplaceLineEndings("\n").ShouldBe("""
      p4kx  slide 2  by Claude, 2026-11-02 10:14
          two        Two charts, side by side
          stacked    One stacked chart
          waterfall  A waterfall

      1 open pick in deck/picks.yaml, each waiting on the reviewer to keep a variant in the preview.

      """);
  }

  [Fact]
  public void The_export_names_each_open_pick_it_drew_the_first_variant_of()
  {
    // Arrange
    using DeckFixture deck = Offering();

    // Act
    List<string> warnings = PickFile.Warnings(deck.Load());

    // Assert
    warnings.ShouldBe(["slide 2 is two, the first variant of pick p4kx, which waits on the reviewer to keep one of " +
      "two, stacked, waterfall"]);
  }

  [Fact]
  public void A_comment_on_a_later_variant_pins_to_it_in_deck_order()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/comments.yaml", "- id: c1\n  slide: two\n  at: [0, 0]\n  text: Hmm");

    // Act
    CommentFile.Apply(deck.Load(), new CommentChange(Add: new Comment { Slide = "waterfall", At = [1, 2],
      Text = "Busy" }), "Daniel", Now);
    CommentFile.Apply(deck.Load(), new CommentChange(Add: new Comment { Slide = "one", At = [1, 2], Text = "Hi" }),
      "Daniel", Now);

    // Assert
    deck.Load().Comments.Select(comment => comment.Slide).ShouldBe(["one", "two", "waterfall"]);
    Checks.Comments(deck.Load()).ShouldBeEmpty();
  }

  [Fact]
  public void A_pick_moves_in_slides_as_a_slide_does()
  {
    // Arrange
    string list = List.Replace("pick: p4kx", "pick: p4kx\n  - three");
    var move = new SlideMove("two", new ListEntry(Section: "Results"), null, Pick: "p4kx");

    // Act
    (string moved, SlideMove? undo) = Patcher.Move(list, move);
    (string back, _) = Patcher.Move(moved, undo!);

    // Assert
    moved.ShouldContain("slides:\n  - pick: p4kx\n  - one\n");
    undo.ShouldBe(new SlideMove("two", null, new ListEntry(Section: "Results"), Pick: "p4kx"));
    back.ShouldBe(list);
  }

  [Fact]
  public async Task The_preview_keeps_a_variant_and_its_undo_reopens_the_pick()
  {
    // Arrange
    using DeckFixture deck = Offering();
    await using Served served = await Served.Start(deck);
    string list = File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml"));
    var delete = new { slide = "two", pick = "p4kx", from = new { section = "Results" }, delete = true };

    // Act
    HttpResponseMessage deleted = await served.Client.PostAsJsonAsync($"{served.Url}/api/move", delete);
    DeckPayload offered = served.Store.Deck;
    HttpResponseMessage kept = await served.Client.PostAsJsonAsync($"{served.Url}/api/picks",
      new { pick = "p4kx", keep = "waterfall" });
    string[] order = served.Store.Deck.Slides.Select(slide => slide.Id).ToArray();
    JsonElement undo = (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("undo");
    HttpResponseMessage reopened = await served.Client.PostAsJsonAsync($"{served.Url}/api/picks", undo);

    // Assert
    deleted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    offered.Slides.Select(slide => (slide.Id, slide.Pick)).ShouldBe([("one", null), ("two", "p4kx")]);
    offered.Variants.Select(slide => slide.Id).ShouldBe(["stacked", "waterfall"]);
    offered.Picks.ShouldHaveSingleItem().Variants.Count.ShouldBe(3);
    kept.StatusCode.ShouldBe(HttpStatusCode.OK);
    order.ShouldBe(["one", "waterfall"]);
    reopened.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(list);
    served.Store.Deck.Picks.ShouldHaveSingleItem();
  }

  [Fact]
  public void Keeping_a_variant_writes_it_in_the_picks_place_deletes_the_others_and_archives_the_pick()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/comments.yaml", "- id: c1\n  slide: waterfall\n  at: [0, 0]\n  text: Too busy");

    // Act
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", "One chart says it",
      Shown: ["waterfall", "two", "stacked"]), "Daniel", Now, Seen);
    Presentation kept = deck.Load();

    // Assert
    kept.Slides.Select(slide => slide.Id).ShouldBe(["one", "stacked"]);
    kept.Picks.ShouldBeEmpty();
    kept.Comments.ShouldBeEmpty();
    File.ReadAllText(deck.Folder.PathTo("deck/presentation.yaml")).ShouldBe(List.Replace("pick: p4kx", "stacked") +
      "\n");
    File.Exists(deck.Folder.PathTo("deck/picks.yaml")).ShouldBeFalse();
    File.Exists(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBeFalse();
    File.Exists(deck.Folder.PathTo("deck/slides/waterfall.yaml")).ShouldBeFalse();
    File.ReadAllText(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBe(Picks + "\n  resolved:\n" +
      "    by: Daniel\n    date: 2026-11-02 18:40\n    rev: 8b21d07\n" +
      "    shown: { A: waterfall, B: two, C: stacked }\n    chosen: stacked\n    why: One chart says it\n");
    PickFile.LoadResolved(deck.Folder.PathTo("deck"), deck.Folder.Root)[0].Resolved!.Chosen.ShouldBe("stacked");
    CommentFile.LoadResolved(deck.Folder.PathTo("deck"), deck.Folder.Root)[0].Resolved!.Outcome.ShouldBe("removed");
    undo.Reopen!.Comments.ShouldBe(["c1"]);
  }

  [Fact]
  public void Undoing_a_keep_puts_every_file_back_as_it_was()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/comments.yaml", "- id: c1\n  slide: waterfall\n  at: [0, 0]\n  text: Too busy");
    string[] files = ["presentation.yaml", "picks.yaml", "comments.yaml", "slides/two.yaml", "slides/waterfall.yaml"];
    string[] before = [.. files.Select(file => File.ReadAllText(deck.Folder.PathTo($"deck/{file}")))];
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", null,
      Shown: ["two", "waterfall", "stacked"]), "Daniel", Now, Seen);

    // Act
    (PickChange redo, _) = PickFile.Apply(deck.Load(), undo, "Daniel", Now, Seen);

    // Assert
    files.Select(file => File.ReadAllText(deck.Folder.PathTo($"deck/{file}"))).ShouldBe(before);
    File.Exists(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBeFalse();
    File.Exists(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldBeFalse();
    redo.Pick.ShouldBe("p4kx");
    redo.Keep.ShouldBe("stacked");
    redo.Shown.ShouldBe(["two", "waterfall", "stacked"]);
  }

  [Fact]
  public void An_order_shown_that_isnt_the_picks_variants_is_refused()
  {
    // Arrange
    using DeckFixture deck = Offering();

    // Act
    PatchException refused = Should.Throw<PatchException>(() => PickFile.Apply(deck.Load(),
      new PickChange("p4kx", "two", null, Shown: ["two", "stacked"]), "Daniel", Now, Seen));

    // Assert
    refused.Message.ShouldBe("pick p4kx's variants changed since they were shown");
    File.Exists(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBeFalse();
  }

  [Fact]
  public void Keeping_none_keeps_the_first_variant_in_place_and_needs_a_reason()
  {
    // Arrange
    using DeckFixture deck = Offering();

    // Act
    PatchException refused = Should.Throw<PatchException>(() =>
      PickFile.Apply(deck.Load(), new PickChange("p4kx", null, " "), "Daniel", Now, Seen));
    PickFile.Apply(deck.Load(), new PickChange("p4kx", null, "None of them shows the gap"), "Daniel", Now, Seen);

    // Assert
    refused.Message.ShouldBe("say why none of the variants will do");
    deck.Load().Slides.Select(slide => slide.Id).ShouldBe(["one", "two"]);
    PickFile.LoadResolved(deck.Folder.PathTo("deck"), deck.Folder.Root)[0].Resolved
      .ShouldBe(new PickResolution("Daniel", "2026-11-02 18:40", "8b21d07", null, "none", "None of them shows the gap"));
  }

  [Fact]
  public void Keeping_none_marks_the_slide_kept_rejected_until_its_redo_deletes_the_mark()
  {
    // Arrange
    using DeckFixture deck = Offering();
    string two = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));

    // Act
    PickFile.Apply(deck.Load(), new PickChange("p4kx", null, "None of them shows the gap"), "Daniel", Now, Seen);
    Presentation rejected = deck.Load();

    // Assert
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBe($"rejected: p4kx\n{two}");
    rejected.Slides[1].Rejected.ShouldBe(new Rejection("p4kx", "None of them shows the gap"));
    PayloadBuilder.Build(rejected, 1, (path, _) => path).Slides[1].Rejected
      .ShouldBe(new Rejection("p4kx", "None of them shows the gap"));
    Finding finding = Checks.Picks(rejected).ShouldHaveSingleItem();
    (finding.Level, finding.Where, finding.Message).ShouldBe(("warning", "two",
      "two was rejected in pick p4kx: None of them shows the gap; redo it, then delete rejected: p4kx"));
  }

  [Fact]
  public void Undoing_none_deletes_the_rejected_mark()
  {
    // Arrange
    using DeckFixture deck = Offering();
    string two = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", null, "No"), "Daniel", Now, Seen);

    // Act
    PickFile.Apply(deck.Load(), undo, "Daniel", Now, Seen);

    // Assert
    File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml")).ShouldBe(two);
  }

  [Fact]
  public void Choosing_again_reopens_a_pick_kept_none_of_from_the_commit_it_was_resolved_at()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/comments.yaml", "- id: c1\n  slide: waterfall\n  at: [0, 0]\n  text: Too busy");
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "offered");
    string root = deck.Folder.PathTo("deck");
    string[] files = ["presentation.yaml", "picks.yaml", "comments.yaml", "slides/two.yaml", "slides/stacked.yaml",
      "slides/waterfall.yaml"];
    string[] offered = [.. files.Select(file => File.ReadAllText(Path.Combine(root, file)))];
    var seen = new Snapshot(Revisions.Run(root, "rev-parse", "HEAD").Output.Trim(), false, new HashSet<string>());
    PickFile.Apply(deck.Load(), new PickChange("p4kx", null, "No"), "Daniel", Now, seen);

    // Act
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", Revisit: true), "Daniel", Now, seen);

    // Assert
    files.Select(file => File.ReadAllText(Path.Combine(root, file))).ShouldBe(offered);
    File.Exists(Path.Combine(root, "picks-resolved.yaml")).ShouldBeFalse();
    (undo.Pick, undo.Keep, undo.Why).ShouldBe(("p4kx", null, "No"));
  }

  [Fact]
  public void Choosing_again_is_refused_for_a_pick_that_kept_a_variant()
  {
    // Arrange
    using DeckFixture deck = Offering();
    PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", null), "Daniel", Now, Seen);

    // Act
    PatchException refused = Should.Throw<PatchException>(() =>
      PickFile.Apply(deck.Load(), new PickChange("p4kx", Revisit: true), "Daniel", Now, Seen));

    // Assert
    refused.Message.ShouldBe("pick p4kx kept stacked, so there's nothing to choose again");
  }

  [Theory]
  [InlineData("zzzz", "rejected names no pick resolved with none in picks-resolved.yaml")]
  [InlineData("p4kx", "rejected names pick p4kx, which kept stacked rather than none")]
  public void A_rejected_mark_naming_no_pick_resolved_with_none_is_an_error(string pick, string problem)
  {
    // Arrange
    using DeckFixture deck = Offering();
    PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", null), "Daniel", Now, Seen);
    deck.Write("deck/slides/stacked.yaml", $"rejected: {pick}\nlayout: content");

    // Act
    Slide slide = deck.Load().Slides[1];

    // Assert
    slide.Error!.Message.ShouldContain(problem);
  }

  [Fact]
  public void A_resolution_written_with_shown_as_a_list_fails_to_load()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/picks-resolved.yaml", Picks +
      "\n  resolved:\n    shown: [two, stacked, waterfall]\n    chosen: two");

    // Act
    LoadException error = Should.Throw<LoadException>(() =>
      PickFile.LoadResolved(deck.Folder.PathTo("deck"), deck.Folder.Root));

    // Assert
    error.Message.ShouldContain("write shown as each letter the reviewer saw with its variant, as A: two");
  }

  [Fact]
  public void An_indented_pick_archives_at_the_files_margin_and_reopens_at_its_own()
  {
    // Arrange
    using DeckFixture deck = Offering();
    string indented = string.Join("\n", Picks.Split('\n').Select(line => "  " + line));
    string other = "  - id: q9zz\n    variants:\n      - { slide: other, intent: Alone }";
    deck.Write("deck/picks.yaml", $"{indented}\n\n{other}");
    deck.Write("deck/slides/other.yaml", "layout: content");
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", "two", null), "Daniel", Now, Seen);

    // Act
    string archived = File.ReadAllText(deck.Folder.PathTo("deck/picks-resolved.yaml"));
    PickFile.Apply(deck.Load(), undo, "Daniel", Now, Seen);

    // Assert
    archived.ShouldStartWith("- id: p4kx\n  by: Claude");
    File.ReadAllText(deck.Folder.PathTo("deck/picks.yaml")).ShouldBe($"{indented}\n\n{other}\n");
  }

  [Fact]
  public void A_keep_that_fails_midway_writes_nothing()
  {
    // Arrange
    using DeckFixture deck = Offering();
    Presentation presentation = deck.Load();
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [one, two]");
    string picks = File.ReadAllText(deck.Folder.PathTo("deck/picks.yaml"));

    // Act
    Should.Throw<PatchException>(() =>
      PickFile.Apply(presentation, new PickChange("p4kx", "two", null), "Daniel", Now, Seen));

    // Assert
    File.ReadAllText(deck.Folder.PathTo("deck/picks.yaml")).ShouldBe(picks);
    File.Exists(deck.Folder.PathTo("deck/slides/stacked.yaml")).ShouldBeTrue();
    File.Exists(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBeFalse();
  }


}