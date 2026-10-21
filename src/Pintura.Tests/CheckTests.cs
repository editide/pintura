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
      "warning export presentation",
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
  public void A_theme_element_off_the_slide_is_reported_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", "layout: content\ntitle: Hello");
    string theme = DeckFixture.Theme.Replace("frame: [860, 510, 60, 16]", "frame: [900, 510, 80, 16]");
    deck.Write("theme/theme.yaml", theme);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Where.ShouldBe("one/page");
    finding.Message.ShouldBe("runs 20pt past the slide's right edge, on 2 slides from the theme");
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