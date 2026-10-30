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

  [Fact]
  public void A_pick_of_one_variant_is_an_error()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/picks.yaml", "- id: p4kx\n  variants:\n    - { slide: two, intent: Alone }");

    // Act
    Finding finding = Checks.Picks(deck.Load()).ShouldHaveSingleItem();

    // Assert
    (finding.Level, finding.Message).ShouldBe(("error",
      "pick p4kx offers only two; offer two to four variants, or list the slide in slides"));
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
  public void Keeping_a_variant_writes_it_in_the_picks_place_deletes_the_others_and_archives_the_pick()
  {
    // Arrange
    using DeckFixture deck = Offering();
    deck.Write("deck/comments.yaml", "- id: c1\n  slide: waterfall\n  at: [0, 0]\n  text: Too busy");

    // Act
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", "One chart says it"),
      "Daniel", Now, Seen);
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
    File.ReadAllText(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBe(Picks + "\n  resolved: { by: Daniel, " +
      "date: 2026-11-02 18:40, rev: 8b21d07, chosen: stacked, why: One chart says it }\n");
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
    (PickChange undo, _) = PickFile.Apply(deck.Load(), new PickChange("p4kx", "stacked", null), "Daniel", Now, Seen);

    // Act
    (PickChange redo, _) = PickFile.Apply(deck.Load(), undo, "Daniel", Now, Seen);

    // Assert
    files.Select(file => File.ReadAllText(deck.Folder.PathTo($"deck/{file}"))).ShouldBe(before);
    File.Exists(deck.Folder.PathTo("deck/picks-resolved.yaml")).ShouldBeFalse();
    File.Exists(deck.Folder.PathTo("deck/comments-resolved.yaml")).ShouldBeFalse();
    redo.ShouldBe(new PickChange("p4kx", "stacked", ""));
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
      .ShouldBe(new PickResolution("Daniel", "2026-11-02 18:40", "8b21d07", "none", "None of them shows the gap"));
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