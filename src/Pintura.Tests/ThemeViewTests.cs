using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ThemeViewTests
{
  private static ThemeViewPayload Build(DeckFixture deck)
  {
    return ThemeView.Build(deck.Load(), (path, _) => path);
  }

  [Fact]
  public void A_named_color_counts_its_uses_toward_the_theme_color_it_names()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ThemeViewPayload theme = Build(deck);

    // Assert
    NamedColorPayload muted = theme.Named.Single(color => color.Name == "muted");
    muted.Value.ShouldBe("dark1 lighter 50");
    muted.Origin.ShouldBe(new OriginPayload("theme/theme.yaml", 18));
    muted.Uses.Slides.ShouldBe(["one", "two"]);
    theme.Colors.Single(color => color.Name == "dark1").Uses.Elements.ShouldBeGreaterThan(muted.Uses.Elements);
    theme.Colors.Single(color => color.Name == "accent6").Uses.Elements.ShouldBe(0);
  }

  [Fact]
  public void A_theme_color_lists_the_shades_powerpoint_offers()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    ThemeColorPayload dark1 = Build(deck).Colors.Single(color => color.Name == "dark1");

    // Assert
    dark1.Hex.ShouldBe("#000000");
    dark1.Shades.Select(shade => shade.Label)
      .ShouldBe(["lighter 80", "lighter 60", "lighter 40", "darker 25", "darker 50"]);
    dark1.Shades[0].Hex.ShouldBe("#CCCCCC");
  }

  [Fact]
  public void A_hex_color_outside_the_palette_is_listed_with_the_slides_using_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: box
          shape: rectangle
          fill: "#123456"
          frame: content
      """);

    // Act
    List<StrayColorPayload> stray = Build(deck).Stray;

    // Assert
    stray.ShouldHaveSingleItem().ShouldBe(new StrayColorPayload("#123456", new UsesPayload(1, ["two"])),
      new StrayComparer());
  }

  [Fact]
  public void A_style_shows_its_text_over_the_base_with_the_keys_it_sets_and_its_uses()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    List<StylePayload> styles = Build(deck).Styles;

    // Assert
    StylePayload caption = styles.Single(style => style.Name == "caption");
    caption.Family.ShouldBe("Arial");
    caption.Size.ShouldBe(11);
    caption.Color.ShouldBe("muted");
    caption.Own.ShouldBe(["size", "color"]);
    caption.Sets.ShouldBe(new Dictionary<string, string> { ["size"] = "11", ["color"] = "muted" });
    caption.Uses.Slides.ShouldBe(["one", "two"]);
    styles.Single(style => style.Name == "title").Family.ShouldBe("Georgia");
    styles.Single(style => style.Name == "callout").Uses.Elements.ShouldBe(0);
  }

  [Fact]
  public void The_base_text_is_shown_with_the_line_it_starts_on()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    StylePayload text = Build(deck).Text;

    // Assert
    text.Family.ShouldBe("Arial");
    text.Size.ShouldBe(16);
    text.Color.ShouldBe("dark1");
    text.Origin.ShouldBe(new OriginPayload("theme/styles.yaml", 2));
  }

  [Fact]
  public void A_style_samples_the_longest_short_text_the_deck_draws_in_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results in ${section}
      elements:
        - id: note
          text: |
            Plain, then [revenue rose sharply]{.lead}
            {.lead}
            A lead paragraph far too long to stand for the style in a specimen line of the theme view
          frame: content
      """);

    // Act
    List<StylePayload> styles = Build(deck).Styles;

    // Assert
    styles.Single(style => style.Name == "caption").Sample.ShouldBe(new StyleSamplePayload("Confidential", "one"));
    styles.Single(style => style.Name == "title").Sample
      .ShouldBe(new StyleSamplePayload("Results in Results", "two"));
    styles.Single(style => style.Name == "lead").Sample.ShouldBe(new StyleSamplePayload("revenue rose sharply", "two"));
    styles.Single(style => style.Name == "callout").Sample.ShouldBeNull();
  }

  [Fact]
  public void A_font_lists_the_styles_drawn_in_it()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    List<FontPayload> fonts = Build(deck).Fonts;

    // Assert
    fonts.Select(font => $"{font.Role} {font.Family} {string.Join(",", font.Styles)}")
      .ShouldBe(["heading Georgia title", "body Arial caption,lead,callout,emphasis,negative"]);
  }

  [Fact]
  public void A_layout_is_drawn_with_its_title_and_placeholders_holding_their_names()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    LayoutPayload cover = Build(deck).Layouts.Single(layout => layout.Id == "cover");

    // Assert
    cover.Name.ShouldBe("Cover");
    cover.File.ShouldBe("theme/layouts/cover.yaml");
    cover.Slides.ShouldBe(["one"]);
    cover.Slide.Items!.Select(item => string.Concat(item.Text!.Paragraphs[0].Runs.Select(run => run.Text)))
      .ShouldBe(["Title", "Subtitle"]);
  }

  [Fact]
  public void A_format_spells_a_sample_number_and_its_negative()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    FormatSamplePayload billions = Build(deck).Formats.Single(format => format.Name == "billions");

    // Assert
    billions.Positive.ShouldBe("$1,234.6B");
    billions.Negative.ShouldBe("($1,234.6B)");
    billions.NegativeHex.ShouldBe("#C00000");
    billions.Origin!.File.ShouldBe("theme/styles.yaml");
  }

  [Fact]
  public void A_table_style_shows_its_header_a_body_row_and_its_other_row_styles()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    TableStylePayload plain = Build(deck).Tables.Single();

    // Assert
    plain.Rows.Select(row => row.Name).ShouldBe(["header", "body", "total"]);
    plain.Rows[0].Fill.ShouldBe("#E7E6E6");
    plain.Rows[0].Text.Bold.ShouldBe(true);
    plain.Rows[2].BorderBottom!.Width.ShouldBe(2);
    plain.Banded.ShouldNotBeNull();
  }

  [Fact]
  public void A_series_color_counts_only_the_charts_with_a_series_that_reaches_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");

    // Act
    List<NamedColorPayload> series = Build(deck).Charts.SeriesColors;

    // Assert
    series.Select(color => color.Uses.Elements).ShouldBe([1, 1, 0]);
  }

  private class StrayComparer : IEqualityComparer<StrayColorPayload>
  {
    public bool Equals(StrayColorPayload? x, StrayColorPayload? y)
    {
      return x!.Hex == y!.Hex && x.Uses.Elements == y.Uses.Elements && x.Uses.Slides.SequenceEqual(y.Uses.Slides);
    }

    public int GetHashCode(StrayColorPayload obj)
    {
      return obj.Hex.GetHashCode();
    }


  }


}