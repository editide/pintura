using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class PayloadTests
{
  private static SlidePayload Slide(DeckFixture deck, string id)
  {
    return PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides.Single(slide => slide.Id == id);
  }

  [Fact]
  public void Guides_show_the_layouts_areas_over_the_themes_and_where_the_title_and_placeholders_sit()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    List<string> guides = Slide(deck, "one").Guides!
      .Select(guide => $"{guide.Kind} {guide.Name} {guide.Owner} [{string.Join(", ", guide.Rect)}] {guide.Used}")
      .ToList();

    // Assert
    guides.ShouldBe([
      "area content theme [40, 100, 880, 380] False",
      "area left layout [60, 340, 400, 100] False",
      "placeholder title layout [60, 190, 840, 90] True",
      "placeholder subtitle layout [60, 290, 840, 40] True"
    ]);
  }

  [Fact]
  public void An_area_is_used_when_an_element_takes_it_as_its_frame_and_a_placeholder_when_the_slide_writes_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", """
      layout: cover
      elements:
        - id: note
          text: Hi
          frame: left
      """);

    // Act
    List<GuidePayload> guides = Slide(deck, "one").Guides!;

    // Assert
    guides.Where(guide => guide.Used).Select(guide => guide.Name).ShouldBe(["left"]);
  }

  [Fact]
  public void A_layout_without_a_title_has_no_title_guide()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/content.yaml", """
      name: Content
      title: none
      """);
    deck.Write("deck/slides/two.yaml", "layout: content");

    // Act
    List<GuidePayload> guides = Slide(deck, "two").Guides!;

    // Assert
    guides.ShouldNotContain(guide => guide.Kind == "placeholder");
  }

  [Fact]
  public void The_inspector_finds_an_item_by_its_place_in_the_drawing_order()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation presentation = deck.Load();

    // Act
    ElementDetail? page = HeadlessRunner.Detail(presentation, "two", 0);
    ElementDetail? missing = HeadlessRunner.Detail(presentation, "two", 9);

    // Assert
    page!.Id.ShouldBe("page");
    page.Owner.ShouldBe("theme");
    page.Properties.Single(property => property.Key == "size").Origin
      .ShouldBe("theme/styles.yaml:22 styles.caption.size");
    missing.ShouldBeNull();
  }

  [Fact]
  public void A_theme_element_refuses_the_mouse_and_names_the_file_that_owns_it()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    EditPayload edit = Slide(deck, "two").Items!.Single(item => item.Id == "page").Edit;

    // Assert
    edit.Locked.ShouldBe("page is on the theme; change it in theme/theme.yaml");
    edit.Frame.ShouldBeNull();
  }

  [Fact]
  public void A_slide_element_carries_the_source_of_each_value_the_mouse_writes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          text: Hi **there**
          frame: left
        - id: arrow
          line: { from: [10, 20], to: [30, 40] }
      """);

    // Act
    List<ItemPayload> items = Slide(deck, "two").Items!;

    // Assert
    EditPayload note = items.Single(item => item.Id == "note").Edit;
    note.Locked.ShouldBeNull();
    note.Frame.ShouldBe(new SourcePayload("elements.note.frame", "left"));
    note.Text.ShouldBe(new SourcePayload("elements.note.text", "Hi **there**"));
    EditPayload arrow = items.Single(item => item.Id == "arrow").Edit;
    arrow.From.ShouldBe(new SourcePayload("elements.arrow.line.from", "[10, 20]"));
    arrow.To.ShouldBe(new SourcePayload("elements.arrow.line.to", "[30, 40]"));
  }

  [Fact]
  public void A_title_the_layout_places_writes_its_frame_into_the_slide()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    EditPayload edit = Slide(deck, "one").Items!.Single(item => item.Id == "title").Edit;

    // Assert
    edit.Frame.ShouldBe(new SourcePayload("title.frame", null));
    edit.Text.ShouldBe(new SourcePayload("title", "Hello"));
  }

  [Fact]
  public void Table_text_is_not_edited_on_the_slide_and_says_nothing_of_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("data: results.csv");

    // Act
    EditPayload edit = Slide(deck, "table").Items!.Single(item => item.Id == "results").Edit;

    // Assert
    edit.Frame.ShouldBe(new SourcePayload("elements.results.frame", "content"));
    edit.Text.ShouldBeNull();
    edit.TextLocked.ShouldBeNull();
  }


}