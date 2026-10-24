using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ExportTests
{
  [Fact]
  public void Slides_carry_only_what_they_own_and_the_theme_and_layouts_are_defined_once()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ExportDeck export = ExportBuilder.Build(deck.Load());

    // Assert
    export.Layouts.Select(layout => layout.Id).ShouldBe(["content", "cover"], ignoreOrder: true);
    export.Layouts.Single(layout => layout.Id == "content").Elements.Select(item => item.Id).ShouldBe(["page"]);
    export.Slides.Single(slide => slide.Id == "one").Items!.Select(item => item.Id).ShouldBe(["title", "subtitle"]);
  }

  [Fact]
  public void Colors_and_fonts_stay_theme_references_with_named_colors_resolved_to_what_they_name()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ExportItem subtitle = ExportBuilder.Build(deck.Load()).Slides.Single(slide => slide.Id == "one").Items!
      .Single(item => item.Id == "subtitle");

    // Assert
    ExportRun run = subtitle.Paragraphs![0].Runs[0];
    run.Text.ShouldBe("Confidential");
    run.Properties["color"].ShouldBe(new ExportColor("dark1", null, 50, null, null));
    run.Properties["font"].ShouldBe(new ExportFont("body", null));
  }

  [Fact]
  public void A_slide_number_on_a_layout_keeps_its_field_with_no_text_of_its_own()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ExportRun run = ExportBuilder.Build(deck.Load()).Layouts.Single(layout => layout.Id == "content").Elements.Single()
      .Paragraphs![0].Runs.Single();

    // Assert
    run.Field.ShouldBe("slide");
    run.Text.ShouldBe("");
  }

  [Fact]
  public void A_layout_title_resolves_over_the_themes_and_a_layout_without_one_inherits_it()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ExportDeck export = ExportBuilder.Build(deck.Load());

    // Assert
    ExportItem title = export.Layouts.Single(layout => layout.Id == "cover").Title!;
    title.Properties["frame"].ShouldBe(new double[] { 60, 190, 840, 90 });
    title.Paragraphs![0].Runs.ShouldBeEmpty();
    title.Paragraphs[0].Properties["size"].ShouldBe(28.0);
    export.Layouts.Single(layout => layout.Id == "content").Title.ShouldBeNull();
    export.Theme.Title!.Properties["frame"].ShouldBe(new double[] { 40, 24, 880, 56 });
  }

  [Fact]
  public void A_layout_without_its_own_background_inherits_the_themes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/cover.yaml", "name: Cover\nbackground: dark2");

    // Act
    ExportDeck export = ExportBuilder.Build(deck.Load());

    // Assert
    export.Layouts.Single(layout => layout.Id == "cover").Background.ShouldBe(new ExportColor("dark2", null, null,
      null, null));
    export.Layouts.Single(layout => layout.Id == "content").Background.ShouldBeNull();
  }

  [Fact]
  public void Formats_carry_their_negative_style_as_the_color_it_names()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ExportFormat billions = ExportBuilder.Build(deck.Load()).Theme.Formats["billions"];

    // Assert
    billions.ShouldBe(new ExportFormat(0.001, 1, null, "$", "B", "parentheses",
      new ExportColor(null, "#C00000", null, null, null), null));
  }

  [Fact]
  public void A_charts_text_font_stays_a_theme_font_reference()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: stacked_column\ndata: revenue.csv\ntext: { font: heading }");

    // Act
    ExportItem chart = ExportBuilder.Build(deck.Load()).Slides.Single(slide => slide.Id == "chart").Items!
      .Single(item => item.Id == "chart");

    // Assert
    chart.Properties["text.font"].ShouldBe(new ExportFont("heading", null));
  }

  [Fact]
  public void A_slide_carries_only_a_background_of_its_own()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\nbackground: dark2");

    // Act
    ExportDeck export = ExportBuilder.Build(deck.Load());

    // Assert
    export.Slides.Single(slide => slide.Id == "two").Background.ShouldBe(new ExportColor("dark2", null, null, null,
      null));
    export.Slides.Single(slide => slide.Id == "one").Background.ShouldBeNull();
  }


}