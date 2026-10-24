using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class DiffTests
{
  [Fact]
  public void A_removed_paragraph_lists_as_one_line_and_a_run_that_only_rewords_shows_once_as_the_text()
  {
    // Arrange
    var item = new ItemDiff("cell", "slide", "text", "changed",
    [
      new ChangeEntry("text", "Revenue: $7.6B\nBacklog: ~$104B", "Revenue, LTM", null, null),
      new ChangeEntry("paragraph 1 run 1", "Revenue: {size=11 bold=false}", "Revenue, LTM {size=11 bold=false}", null,
        null),
      new ChangeEntry("paragraph 2.align", "left", null, null, null),
      new ChangeEntry("paragraph 2 run 1", "Backlog:  {size=11 bold=false}", null, null, null),
      new ChangeEntry("paragraph 2 run 2", "~$104B {size=11 bold=true}", null, null, null)
    ]);
    var diff = new DeckDiff([new SlideDiff { Id = "summary", Status = "changed", After = 2, Items = [item] }], []);
    var output = new StringWriter();

    // Act
    DiffCommand.Write(diff, "deck", output);

    // Assert
    string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => line.Trim()).ToArray();
    lines.ShouldContain("paragraph 2  removed: Backlog: ~$104B");
    lines.ShouldNotContain(line => line.StartsWith("paragraph 1 run 1"));
    lines.ShouldNotContain(line => line.StartsWith("paragraph 2.align"));
  }

  private const string Boxes = """
    layout: content
    title: Results in ${section}
    elements:
      - id: note
        text: Revenue grew
        style: caption
        frame: [40, 100, 200, 22]
      - id: box
        shape: rectangle
        frame: [40, 200, 100, 50]
    """;

  [Fact]
  public void An_unchanged_presentation_has_no_changes()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation before = deck.Load();

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    diff.Slides.ShouldBeEmpty();
    diff.Causes.ShouldBeEmpty();
  }

  [Fact]
  public void A_moved_element_names_its_frame_before_and_after_with_the_lines_that_set_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", Boxes);
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("[40, 200, 100, 50]", "[52, 200, 100, 50]"));

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    SlideDiff slide = diff.Slides.ShouldHaveSingleItem();
    slide.Id.ShouldBe("two");
    slide.Status.ShouldBe("changed");
    slide.Moved.ShouldBeFalse();
    ItemDiff box = slide.Items.ShouldHaveSingleItem();
    box.Id.ShouldBe("box");
    box.Status.ShouldBe("changed");
    ChangeEntry frame = box.Changes.ShouldHaveSingleItem();
    frame.ShouldBe(new ChangeEntry("frame", "[40, 200, 100, 50]", "[52, 200, 100, 50]",
      "deck/slides/two.yaml:10 elements.box.frame", "deck/slides/two.yaml:10 elements.box.frame"));
    diff.Causes.ShouldBeEmpty();
  }

  [Fact]
  public void Added_and_removed_elements_are_named_without_their_properties()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", Boxes);
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("id: box", "id: square"));

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    List<ItemDiff> items = diff.Slides.ShouldHaveSingleItem().Items;
    items.Select(item => (item.Id, item.Kind, item.Status)).ShouldBe([("square", "shape", "added"),
      ("box", "shape", "removed")]);
    items.ShouldAllBe(item => item.Changes.Count == 0);
  }

  [Fact]
  public void Reworded_text_shows_the_old_and_new_words()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", Boxes);
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("Revenue grew", "Revenue fell"));

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    ChangeEntry text = diff.Slides.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Changes.ShouldHaveSingleItem();
    text.Key.ShouldBe("text");
    text.Before.ShouldBe("Revenue grew");
    text.After.ShouldBe("Revenue fell");
  }

  [Fact]
  public void A_style_change_is_named_once_as_the_cause_of_every_element_it_restyles()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", Boxes);
    deck.Write("deck/slides/two.yaml", Boxes);
    Presentation before = deck.Load();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("caption: { size: 11,", "caption: { size: 12,"));

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    CauseDiff cause = diff.Causes.ShouldHaveSingleItem();
    cause.Origin.ShouldBe("theme/styles.yaml styles.caption.size");
    cause.Key.ShouldBe("size");
    (cause.Before, cause.After).ShouldBe(("11", "12"));
    cause.Items.ShouldBe(4);
    cause.Slides.ShouldBe(["one", "two"]);
    diff.Slides.SelectMany(slide => slide.Items).SelectMany(item => item.Changes)
      .ShouldAllBe(change => change.Cause == "theme/styles.yaml styles.caption.size");
  }

  [Fact]
  public void A_value_the_theme_newly_sets_is_the_themes_change()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation before = deck.Load();
    deck.Write("theme/styles.yaml",
      DeckFixture.Styles.Replace("caption: { size: 11,", "caption: { italic: true, size: 11,"));

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    CauseDiff cause = diff.Causes.ShouldHaveSingleItem();
    cause.Origin.ShouldBe("theme/styles.yaml styles.caption.italic");
    (cause.Before, cause.After).ShouldBe((null, "true"));
  }

  [Fact]
  public void An_override_dropped_from_a_slide_is_the_slides_change_rather_than_the_themes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("style: caption", "style: caption\n    size: 30"));
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes);

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    ChangeEntry size = diff.Slides.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Changes.ShouldHaveSingleItem();
    size.Key.ShouldBe("size");
    size.Cause.ShouldBeNull();
    diff.Causes.ShouldBeEmpty();
  }

  [Fact]
  public void Slides_added_and_removed_are_named_and_only_the_one_reordered_counts_as_moved()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/three.yaml", "layout: content\ntitle: Three");
    deck.Write("deck/slides/four.yaml", "layout: content\ntitle: Four");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - one
        - two
        - three
      """);
    Presentation before = deck.Load();
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - four
        - two
        - one
      """);

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    diff.Slides.Select(slide => (slide.Id, slide.Status, slide.Before, slide.After, slide.Moved)).ShouldBe([
      ("four", "added", null, 1, false),
      ("one", "moved", 1, 3, true),
      ("three", "removed", 3, null, false)
    ]);
  }

  [Fact]
  public void A_slide_number_that_shifts_with_its_slide_is_not_a_change()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation before = deck.Load();
    deck.Write("deck/slides/zero.yaml", "layout: cover\ntitle: Zero");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      fields:
        footer: Confidential
      slides:
        - zero
        - one
        - section: Results
        - two
      """);

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    diff.Slides.ShouldHaveSingleItem().Id.ShouldBe("zero");
  }

  [Fact]
  public void Renaming_a_section_changes_only_the_slides_that_show_its_name()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/three.yaml", "layout: content\ntitle: Detail");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - one
        - section: Results
        - two
        - three
      """);
    Presentation before = deck.Load();
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - one
        - section: Outlook
        - two
        - three
      """);

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    SlideDiff two = diff.Slides.ShouldHaveSingleItem();
    two.Id.ShouldBe("two");
    two.Changes.ShouldBeEmpty();
    two.Items.ShouldHaveSingleItem().Id.ShouldBe("title");
  }

  [Fact]
  public void Changed_chart_data_names_the_series_that_changed()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");
    Presentation before = deck.Load();
    deck.Write("deck/charts/revenue.csv", "segment,2024,2025\nSpace,3796,4200\nAI,2620,-3201");

    // Act
    DeckDiff diff = Differ.Compare(before, deck.Load());

    // Assert
    ChangeEntry series = diff.Slides.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Changes
      .ShouldHaveSingleItem();
    series.Key.ShouldBe("series Space");
    series.Before.ShouldBe("3796, 4086");
    series.After.ShouldBe("3796, 4200");
  }

  [Fact]
  public void The_listing_names_each_slides_own_changes_and_each_theme_change_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", Boxes);
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("[40, 200, 100, 50]", "[52, 200, 100, 50]"));
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("caption: { size: 11,", "caption: { size: 12,"));
    var output = new StringWriter();

    // Act
    DiffCommand.Write(Differ.Compare(before, deck.Load()), "HEAD → the working tree", output);

    // Assert
    output.ToString().ShouldBe("""
      HEAD → the working tree

       1 one  changed
          restyled by the theme: 1 element
       2 two  changed
          ~ box
              frame  [40, 200, 100, 50] → [52, 200, 100, 50]
          restyled by the theme: 2 elements

      theme/styles.yaml styles.caption.size: 11 → 12 on 3 elements across 2 slides

      """.ReplaceLineEndings(Environment.NewLine));
  }



  [Fact]
  public void The_listing_shows_a_long_text_by_the_words_that_changed()
  {
    // Arrange
    using var deck = new DeckFixture();
    const string Long = "Revenue grew from $3.9B in 2023 to $11.4B in 2025, led by connectivity subscriptions";
    deck.Write("deck/slides/two.yaml", Boxes.Replace("Revenue grew", Long));
    Presentation before = deck.Load();
    deck.Write("deck/slides/two.yaml", Boxes.Replace("Revenue grew", Long.Replace("$11.4B", "$11.5B")));
    var output = new StringWriter();

    // Act
    DiffCommand.Write(Differ.Compare(before, deck.Load()), "HEAD → the working tree", output);

    // Assert
    output.ToString().ShouldContain("text  …2023 to $11.4B in 2025,… → …2023 to $11.5B in 2025,…");
  }

}