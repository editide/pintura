using System.Text;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class CheckTests
{
  private static BoxMeasure Box(string fit, double overflow, double? scale = null)
  {
    return new BoxMeasure("revenue", "takeaway", "slide", fit, 9, [4, 5], overflow, scale, null);
  }

  [Fact]
  public void Text_running_past_its_frame_is_an_error_naming_the_slide_and_element()
  {
    // Arrange
    var measurement = new Measurement([], [Box("none", 14)]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("error");
    finding.Where.ShouldBe("revenue/takeaway");
    finding.Message.ShouldBe("text runs past its frame by 14pt, at 9 lines");
  }

  [Fact]
  public void An_element_the_theme_repeats_on_every_slide_is_reported_once()
  {
    // Arrange
    var measurement = new Measurement([],
    [
      new BoxMeasure("cover", "footer", "theme", "none", 1, [1], 3.8, null, null),
      new BoxMeasure("revenue", "footer", "theme", "none", 1, [1], 4.2, null, null),
      new BoxMeasure("capital", "footer", "theme", "none", 1, [1], 4.2, null, null)
    ]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("cover/footer");
    finding.Message.ShouldBe("text runs past its frame by up to 4.2pt, at 1 line, on 3 slides from the theme");
  }

  [Fact]
  public void Subpixel_overflow_is_rounding_rather_than_a_finding()
  {
    // Arrange
    var measurement = new Measurement([], [Box("none", 0.4)]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void Text_that_would_wrap_past_its_frame_set_a_hair_wider_is_a_warning()
  {
    // Arrange
    var measurement = new Measurement([], [Box("none", 0) with { Lines = 1, Paragraphs = [1], Tight = 5.4 }]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("warning");
    finding.Where.ShouldBe("revenue/takeaway");
    finding.Message.ShouldBe("text barely fits its frame's width: set 1% wider, as another browser or PowerPoint " +
      "may, it wraps 5.4pt past the frame");
  }

  [Fact]
  public void Text_with_room_to_spare_across_is_not_tight()
  {
    // Arrange
    var measurement = new Measurement([], [Box("none", 0) with { Tight = 0.3 }]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_paragraph_wrapping_onto_a_line_of_one_or_two_words_is_a_warning_quoting_it()
  {
    // Arrange
    var measurement = new Measurement([], [Box("none", 0) with { Runts = [new Runt(1, "by Europe")] }]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("warning");
    finding.Where.ShouldBe("revenue/takeaway");
    finding.Message.ShouldBe("paragraph 2 wraps onto a last line of two words, \"by Europe\"");
  }

  [Fact]
  public void A_box_of_one_paragraph_wrapping_onto_one_word_names_no_paragraph()
  {
    // Arrange
    var measurement = new Measurement([],
      [Box("none", 0) with { Paragraphs = [2], Runts = [new Runt(0, "Europe")] }]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    findings.ShouldHaveSingleItem().Message.ShouldBe("text wraps onto a last line of one word, \"Europe\"");
  }

  [Fact]
  public void Shrunk_text_that_still_overflows_names_its_scale()
  {
    // Arrange
    var measurement = new Measurement([], [Box("shrink", 3.5, 0.25)]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    findings.ShouldHaveSingleItem().Message.ShouldBe("text overflows its box by 3.5pt even shrunk to 25%");
  }

  [Fact]
  public void A_missing_font_on_wsl_names_the_fontconfig_fix()
  {
    // Arrange
    var measurement = new Measurement([new FontMeasure("Georgia", false), new FontMeasure("Arial", true)], []);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(true, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("warning");
    finding.Where.ShouldBe("font Georgia");
    finding.Message.ShouldContain("<dir>/mnt/c/Windows/Fonts</dir>");
    finding.Message.ShouldContain("/etc/fonts/local.conf");
  }

  [Fact]
  public void A_font_missing_even_from_the_windows_fonts_has_no_fix_to_name()
  {
    // Arrange
    var measurement = new Measurement([new FontMeasure("Brand Sans", false)], []);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(true, true));

    // Assert
    findings.ShouldHaveSingleItem().Message.ShouldNotContain("local.conf");
  }

  [Fact]
  public void Text_shrunk_below_the_themes_threshold_is_a_warning()
  {
    // Arrange
    var measurement = new Measurement([], [Box("shrink", 0, 0.55)]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false), 70);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("warning");
    finding.Message.ShouldBe("text shrunk to 55%, below the theme's 70%");
  }

  [Fact]
  public void Without_a_threshold_in_the_theme_shrinking_is_never_a_finding()
  {
    // Arrange
    var measurement = new Measurement([], [Box("shrink", 0, 0.2)]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void Each_seeded_fault_yields_its_finding_and_nothing_else()
  {
    // Arrange
    string repository = LoaderTests.RepositoryRoot();
    Presentation presentation = PresentationLoader.Load(Path.Combine(repository, "src", "Pintura.Tests", "fixtures",
      "faults"), repository);

    // Act
    List<Finding> findings = Checks.Structure(presentation);
    findings.AddRange(Checks.Geometry(presentation));
    findings.AddRange(Checks.Consistency(presentation));
    findings.AddRange(Checks.Export(presentation));

    // Assert
    findings.Select(finding => $"{finding.Level} {finding.Kind} {finding.Where}").ShouldBe([
      "error structure broken",
      "warning geometry off-slide/badge",
      "warning geometry overlap/second",
      "warning geometry near-miss/body",
      "error geometry wide-table/table",
      "warning consistency moved-title/title",
      "warning consistency small-body/body",
      "warning consistency off-slide/note",
      "warning export off-slide/pointer"
    ]);
    Finding broken = findings[0];
    (broken.File, broken.Line, broken.Topic).ShouldBe(("src/Pintura.Tests/fixtures/faults/slides/broken.yaml", 6,
      "styles"));
  }

  [Fact]
  public void The_same_fill_and_opacity_on_several_slides_want_a_style()
  {
    // Arrange
    using var deck = new DeckFixture();
    foreach (string slide in new[] { "a", "b", "c" })
    {
      deck.Write($"deck/slides/{slide}.yaml", """
        layout: content
        elements:
          - id: credit
            shape: rectangle
            frame: [620, 442, 300, 24]
            fill: dark1
            opacity: 55
            text: Credit
        """);
    }
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [a, b, c]");

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.Select(finding => finding.Message).ShouldContain(
      "the same one-off formatting (fill: dark1, opacity: 55) is on 3 slides, a, b, c; name a style for it in " +
      "styles.yaml");
  }

  [Fact]
  public void The_same_fill_on_boxes_of_different_styles_is_a_coincidence_rather_than_a_style()
  {
    // Arrange
    using var deck = new DeckFixture();
    foreach ((string slide, string style) in new[] { ("a", "lead"), ("b", "caption"), ("c", "callout") })
    {
      deck.Write($"deck/slides/{slide}.yaml", $"""
        layout: content
        elements:
          - id: box
            shape: rectangle
            frame: left
            style: {style}
            fill: dark1
        """);
    }
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [a, b, c]");

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.ShouldNotContain(finding => finding.Message.Contains("one-off formatting"));
  }

  [Fact]
  public void A_slide_size_of_its_own_exports_as_it_is()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      size: [1088, 612]
      slides: [one, two]
      """);

    // Act
    List<Finding> findings = Checks.Export(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void Every_presentation_comes_back_clean()
  {
    // Arrange
    string repository = LoaderTests.RepositoryRoot();
    string[] roots = [.. Directory.GetDirectories(Path.Combine(repository, "presentations")),
      Path.Combine(repository, "src", "Pintura.Tests", "fixtures", "charts")];

    foreach (string root in roots)
    {
      // Act
      Presentation presentation = PresentationLoader.Load(root);
      List<Finding> findings = Checks.Structure(presentation);
      findings.AddRange(Checks.Geometry(presentation));
      findings.AddRange(Checks.Consistency(presentation));

      // Assert
      findings.ShouldBeEmpty(Path.GetFileName(root));
    }
  }

  [Fact]
  public void A_title_in_a_style_of_its_own_is_not_measured_against_the_other_titles()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/cover.yaml", """
      name: Cover
      title: { frame: [60, 190, 840, 90], style: display }
      """);
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Hello");
    foreach (string id in new[] { "two", "three", "four" })
    {
      deck.Write($"deck/slides/{id}.yaml", "layout: content\ntitle: Results");
    }
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides: [one, two, three, four]
      """);
    string styles = DeckFixture.Styles.Replace("  title: { font: heading, size: 28 }",
      "  title: { font: heading, size: 28 }\n  display: { font: heading, size: 40 }");
    deck.Write("theme/styles.yaml", styles);

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_title_sized_by_its_layout_is_not_measured_against_the_other_titles()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/cover.yaml", """
      name: Cover
      title: { frame: [60, 190, 840, 90], size: 48 }
      """);
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Hello");
    foreach (string id in new[] { "two", "three", "four" })
    {
      deck.Write($"deck/slides/{id}.yaml", "layout: content\ntitle: Results");
    }
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides: [one, two, three, four]
      """);

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_box_inside_another_is_layering_rather_than_an_overlap()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: card
          shape: rectangle
          frame: [100, 200, 300, 200]
        - id: label
          text: Inside the card
          frame: [120, 220, 200, 40]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_line_running_through_a_box_between_the_two_it_joins_is_reported_on_the_line()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: first
          shape: rectangle
          frame: [100, 200, 100, 50]
        - id: middle
          shape: rectangle
          frame: [270, 210, 60, 50]
        - id: last
          shape: rectangle
          frame: [400, 200, 100, 50]
        - id: link
          line: { from: [200, 225], to: [400, 225] }
          end: triangle
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    Finding finding = findings.Where(finding => finding.Message.StartsWith("runs through")).ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/link");
    finding.Message.ShouldBe("runs through middle");
  }

  [Fact]
  public void An_arc_running_through_a_box_is_reported_but_not_one_ending_inside_a_box_it_points_into()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: start
          shape: rectangle
          frame: [70, 280, 60, 40]
        - id: crossed
          shape: rectangle
          frame: [220, 185, 60, 40]
        - id: loop
          arc: { from: 180, to: 359 }
          frame: [100, 200, 300, 200]
          end: triangle
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.Where(finding => finding.Message.StartsWith("runs through")).Select(finding => finding.Message)
      .ShouldBe(["runs through crossed"]);
  }

  [Fact]
  public void A_line_crossing_a_text_box_with_nothing_drawn_around_it_counts_only_where_its_text_is()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: note
          text: A short note
          frame: [200, 200, 300, 100]
        - id: under
          line: { from: [150, 280], to: [550, 280] }
        - id: through
          line: { from: [150, 210], to: [550, 210] }
      """);
    var measurement = new Measurement([],
      [new BoxMeasure("two", "note", "slide", "none", 1, [1], 0, null, null, Ink: [[200, 200, 120, 20]])]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.Where(finding => finding.Message.StartsWith("runs through")).ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/through");
    finding.Message.ShouldBe("runs through note's text");
  }

  [Fact]
  public void Facing_edges_a_few_points_apart_are_spacing_rather_than_a_near_miss()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: body
          text: Body
          frame: content
        - id: source
          text: Source
          frame: [40, 482, 600, 20]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void An_item_layered_inside_another_is_not_a_near_miss_for_items_outside_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: panel
          shape: rectangle
          frame: [40, 120, 500, 320]
        - id: highlight
          shape: rectangle
          frame: [480, 292, 40, 130]
        - id: side_head
          text: Beside the panel
          frame: [560, 290, 300, 18]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void Items_layered_inside_the_same_one_are_still_near_misses_for_each_other()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: panel
          shape: rectangle
          frame: [40, 120, 500, 320]
        - id: first
          text: First
          frame: [60, 140, 200, 40]
        - id: second
          text: Second
          frame: [280, 142, 200, 40]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.Select(finding => finding.Where).ShouldBe(["two/second"]);
  }

  [Fact]
  public void Items_sharing_a_centre_line_are_aligned_rather_than_near_misses()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: label
          text: Label
          frame: [60, 140, 100, 20]
        - id: icon
          shape: ellipse
          frame: [170, 142, 16, 16]
        - id: off_centre
          shape: ellipse
          frame: [200, 141, 16, 16]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.Select(finding => finding.Where).ShouldBe(["two/off_centre"]);
  }

  [Fact]
  public void Text_edges_its_alignment_leaves_ragged_are_not_near_misses()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: head
          text: A centred head
          align: center
          valign: middle
          frame: [50, 140, 240, 20]
        - id: badge
          shape: ellipse
          frame: [52, 170, 30, 30]
        - id: label
          text: Left-aligned label
          frame: [400, 140, 238, 40]
        - id: note
          text: Another left-aligned note
          frame: [400, 200, 240, 42]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void Text_edges_its_alignment_anchors_are_still_near_misses()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: badge
          shape: ellipse
          frame: [400, 100, 30, 30]
        - id: label
          text: Left-aligned label
          frame: [402, 140, 238, 40]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/label");
    finding.Message.ShouldBe("left edge at x 402 is 2pt from badge's left edge at x 400");
  }

  [Fact]
  public void Text_in_a_box_it_draws_keeps_every_edge()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: badge
          shape: ellipse
          frame: [52, 100, 30, 30]
        - id: band
          text: A filled band
          align: center
          fill: light2
          frame: [50, 140, 240, 20]
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.Select(finding => finding.Where).ShouldBe(["two/band"]);
  }

  [Fact]
  public void A_straight_lines_run_is_compared_only_with_other_lines()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: box
          shape: rectangle
          frame: [365, 256, 200, 60]
        - id: arrow
          line: { from: [300, 254], to: [365, 254] }
        - id: label
          shape: rectangle
          text: Beside the arrow
          frame: [600, 140, 120, 20]
        - id: other_arrow
          line: { from: [600, 162], to: [700, 162] }
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_straight_line_ending_short_of_a_box_or_off_another_line_is_a_near_miss()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: box
          shape: rectangle
          frame: [400, 300, 200, 60]
        - id: down
          line: { from: [500, 200], to: [500, 298] }
        - id: across
          line: { from: [100, 150], to: [300, 150] }
        - id: below
          line: { from: [320, 152], to: [380, 152] }
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    findings.Select(finding => $"{finding.Where}: {finding.Message}").ShouldBe([
      "two/down: end at y 298 is 2pt from box's top edge at y 300",
      "two/below: start at y 152 is 2pt from across's start at y 150; end at y 152 is 2pt from across's start at y 150"
    ]);
  }

  [Fact]
  public void A_contained_picture_is_placed_where_its_image_draws_rather_than_by_its_frame()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/logo.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 176 41"/>""");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: logo
          picture: ../media/logo.svg
          frame: [371.5, 224, 217, 41]
        - id: framed
          shape: rectangle
          frame: [373, 300, 140, 30]
        - id: drawn
          shape: rectangle
          frame: [390, 360, 100, 30]
      """);
    var measurement = new Measurement([], [], Pictures: [new PictureMeasure("two", "logo", "slide", [392, 224, 176, 41])]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/drawn");
    finding.Message.ShouldBe("left edge at x 390 is 2pt from logo's left edge at x 392");
  }

  [Fact]
  public void A_picture_whose_image_has_empty_margins_built_in_wants_trimming()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/logo.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 40"/>""");
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"/>""");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: logo
          picture: ../media/logo.svg
          frame: [40, 100, 100, 40]
        - id: icon
          picture: ../media/icon.svg
          fill: accent1
          frame: [40, 200, 24, 24]
      """);
    var measurement = new Measurement([], [], Pictures:
    [
      new PictureMeasure("two", "logo", "slide", [40, 100, 100, 40], [0.037, 0.193, 0.038, 0.253]),
      new PictureMeasure("two", "icon", "slide", [40, 200, 24, 24], [0.1, 0.1, 0.1, 0.1])
    ]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/logo");
    finding.Message.ShouldBe("../media/logo.svg has empty margins built in, 19% of its height at the top and 25% at " +
      "the bottom, so its frame doesn't meet what it draws; trim the file");
  }

  [Fact]
  public void Text_inside_a_charts_frame_is_reported_where_its_lines_cross_the_charts_labels()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      title: Results
      elements:
        - { id: chart, chart: revenue, frame: [40, 100, 600, 300] }
        - { id: note, text: A note, frame: [60, 360, 300, 40] }
        - { id: aside, text: Clear of the labels, frame: [400, 110, 200, 40] }
      """);
    var measurement = new Measurement([],
    [
      new BoxMeasure("chart", "note", "slide", "none", 1, [1], 0, null, null, Ink: [[62, 372, 80, 14]]),
      new BoxMeasure("chart", "aside", "slide", "none", 1, [1], 0, null, null, Ink: [[402, 112, 120, 14]])
    ],
    [
      new ChartMeasure("chart", "chart", [new ChartLabel("2024", [120, 380, 30, 12]),
        new ChartLabel("2025", [300, 380, 30, 12]), new ChartLabel("4,086", [430, 140, 36, 12])])
    ]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("chart/note");
    finding.Message.ShouldBe("its text crosses chart's label 2024");
  }

  [Fact]
  public void Data_labels_with_no_room_past_the_plots_edge_are_reported_with_how_to_make_room()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      title: Results
      elements:
        - { id: chart, chart: revenue, frame: [40, 100, 600, 300] }
      """);
    var measurement = new Measurement([], [],
    [
      new ChartMeasure("chart", "chart", [new ChartLabel("2024", [120, 380, 30, 12]),
        new ChartLabel("$8.5B", [430, 110, 36, 12], Moved: "top"),
        new ChartLabel("$8.0B", [500, 110, 36, 12], Moved: "top"),
        new ChartLabel("($1.2B)", [300, 360, 36, 12], Moved: "bottom")])
    ]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    findings.Select(finding => $"{finding.Where} {finding.Message}").ShouldBe(
    [
      "chart/chart its data labels $8.5B and $8.0B have no room past its top edge, so they're drawn over " +
        "their data; raise its value axis's max",
      "chart/chart its data label ($1.2B) has no room past its bottom edge, so it's drawn over its data; " +
        "lower its value axis's min"
    ], ignoreOrder: true);
  }

  [Fact]
  public void A_data_label_drawn_over_its_charts_category_labels_is_reported()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      title: Results
      elements:
        - { id: chart, chart: revenue, frame: [40, 100, 600, 300] }
      """);
    var measurement = new Measurement([], [],
    [
      new ChartMeasure("chart", "chart", [new ChartLabel("2024", [120, 380, 30, 12], Part: "category_axis"),
        new ChartLabel("2025", [300, 380, 30, 12], Part: "category_axis"),
        new ChartLabel("($49M)", [296, 374, 38, 12], Part: "label"),
        new ChartLabel("$2.6B", [120, 200, 36, 12], Part: "label")])
    ]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("chart/chart");
    finding.Message.ShouldBe("its data label ($49M) crosses its category label 2025; lower its value axis's min");
  }

  [Fact]
  public void An_axis_label_the_plot_narrowed_for_is_an_export_warning_since_PowerPoint_cuts_it_off()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: line\ndata: revenue.csv");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      title: Results
      elements:
        - { id: chart, chart: revenue, frame: [40, 100, 600, 300] }
      """);
    var measurement = new Measurement([], [],
    [
      new ChartMeasure("chart", "chart", [new ChartLabel("2024", [40, 380, 30, 12], Clipped: true),
        new ChartLabel("2025", [300, 380, 30, 12]), new ChartLabel("2026", [600, 380, 30, 12], Clipped: true)])
    ]);

    // Act
    List<Finding> findings = Checks.Export(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("chart/chart");
    finding.Message.ShouldBe(
      "PowerPoint can't narrow a chart's plot to fit its axis labels 2024 and 2026, so it cuts them off at its edge");
  }

  [Fact]
  public void A_charts_plot_insets_are_an_export_warning()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: line\ndata: revenue.csv\nplot: { insets: [0, 20, 0, 0] }");

    // Act
    List<Finding> findings = Checks.Export(deck.Load());

    // Assert
    findings.Select(finding => $"{finding.Where} {finding.Message}").ShouldContain(
      "chart/chart a chart's plot insets aren't exported, so PowerPoint places its plot itself");
  }

  [Fact]
  public void A_decorative_element_is_left_out_of_the_geometry_checks()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: watermark
          shape: rectangle
          frame: [600, 300, 500, 400]
          decorative: true
        - id: note
          text: Across the watermark
          frame: [500, 350, 200, 40]
        - id: aside
          text: Beside the watermark
          frame: [602, 100, 140, 30]
      """);
    Presentation presentation = deck.Load();

    // Act
    List<Finding> findings = Checks.Geometry(presentation);

    // Assert
    presentation.Slides.ShouldAllBe(slide => slide.Error == null);
    findings.ShouldBeEmpty();
  }

  [Fact]
  public void A_grown_box_runs_off_the_slide_by_its_measured_height()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: notes
          text: Notes
          fit: grow
          frame: [40, 400, 430, 40]
      """);
    var measurement = new Measurement([], [new BoxMeasure("two", "notes", "slide", "grow", 9, [9], 0, null, 180)]);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load(), measurement);

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("two/notes");
    finding.Message.ShouldBe("runs 40pt past the slide's bottom edge");
  }

  [Fact]
  public void A_layout_element_off_the_slide_is_reported_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", "layout: content\ntitle: Hello");
    string layout = File.ReadAllText(deck.Folder.PathTo("theme/layouts/content.yaml"));
    deck.Write("theme/layouts/content.yaml", layout.Replace("frame: [860, 510, 60, 16]", "frame: [900, 510, 80, 16]"));

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("one/page");
    finding.Message.ShouldBe("runs 20pt past the slide's right edge, on 2 slides from the layout");
  }

  [Fact]
  public void A_load_error_stopping_the_presentation_is_a_finding_with_its_file_line_and_topic()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("  size: 16\n", ""));

    // Act
    Finding finding = Checks.Structure(deck.Error());

    // Assert
    finding.Where.ShouldBe("presentation");
    finding.Message.ShouldBe("text needs size: every default is in the theme");
    (finding.File, finding.Topic).ShouldBe(("theme/styles.yaml", "styles"));
  }

  [Fact]
  public void Since_a_revision_a_finding_is_new_unless_the_same_kind_was_on_the_same_element_before()
  {
    // Arrange
    var overflow = new Finding("error", "text", "revenue/takeaway", "text runs past its frame by 2pt, at 3 lines");
    var worse = overflow with { Message = "text runs past its frame by 9pt, at 4 lines" };
    var elsewhere = overflow with { Where = "revenue/title" };
    var fixedOne = new Finding("warning", "geometry", "cover/logo", "logo sits 1pt off the grid");

    // Act
    (List<Finding> introduced, List<Finding> gone) = Checks.Since([overflow, fixedOne], [worse, elsewhere]);

    // Assert
    introduced.ShouldBe([elsewhere]);
    gone.ShouldBe([fixedOne]);
  }

  [Fact]
  public void Geometry_on_a_slide_of_hundreds_of_boxes_takes_moments()
  {
    // Arrange
    using var deck = new DeckFixture();
    var boxes = new StringBuilder("layout: content\nelements:\n");
    for (int index = 0; index < 400; index++)
    {
      boxes.Append($"  - {{ id: b{index}, shape: rectangle, frame: [{index % 20 * 45 + 3.3}, {index / 20 * 25 + 1.7}, 40, 20] }}\n");
    }
    deck.Write("deck/slides/two.yaml", boxes.ToString());
    Presentation presentation = deck.Load();
    var clock = System.Diagnostics.Stopwatch.StartNew();

    // Act
    Checks.Geometry(presentation);

    // Assert
    clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
  }


}