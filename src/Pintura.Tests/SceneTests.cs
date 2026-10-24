using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class SceneTests
{
  [Fact]
  public void A_layout_title_overrides_the_themes_key_by_key_and_says_where()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    SceneItem title = deck.Item("one", "title");

    // Assert
    ((Frame)title.Properties["frame"].Value).Rect.ShouldBe([60, 190, 840, 90]);
    title.Properties["frame"].Origin!.ToString().ShouldBe("theme/layouts/cover.yaml:2 title.frame");
    title.Properties["size"].Origin!.ToString().ShouldBe("theme/styles.yaml:21 styles.title.size");
    title.Properties["color"].Value.ShouldBe(new Color { Theme = "light1" });
  }

  [Fact]
  public void Theme_elements_draw_first_unless_the_layout_drops_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    Presentation presentation = deck.Load();

    // Act
    List<string> cover = SceneBuilder.Build(presentation, presentation.Slides[0]).Items.Select(item => item.Id)
      .ToList();
    List<string> content = SceneBuilder.Build(presentation, presentation.Slides[1]).Items.Select(item => item.Id)
      .ToList();

    // Assert
    cover.ShouldBe(["title", "subtitle"]);
    content.ShouldBe(["page", "title"]);
  }

  [Fact]
  public void An_elements_entry_naming_the_title_draws_it_there_and_unnamed_placeholders_draw_first()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      placeholders:
        subtitle: ${footer}
      elements:
        - id: photo
          shape: rectangle
          frame: [0, 0, 960, 540]
        - title
        - id: note
          text: On top
          frame: [60, 400, 400, 40]
      """);
    Presentation presentation = deck.Load();

    // Act
    List<string> drawn = SceneBuilder.Build(presentation, presentation.Slides[0]).Items.Select(item => item.Id)
      .ToList();

    // Assert
    drawn.ShouldBe(["subtitle", "photo", "title", "note"]);
  }

  [Fact]
  public void Fields_fill_in_per_slide()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    SceneItem page = deck.Item("two", "page");
    SceneItem title = deck.Item("two", "title");
    SceneItem subtitle = deck.Item("one", "subtitle");

    // Assert
    page.Paragraphs![0].Runs.Single().Text.ShouldBe("2");
    string.Concat(title.Paragraphs![0].Runs.Select(run => run.Text)).ShouldBe("Results in Results");
    subtitle.Paragraphs![0].Runs.Single().Origin.ToString().ShouldBe("deck/presentation.yaml:4 fields.footer");
  }

  [Fact]
  public void A_bullet_level_sets_a_default_that_the_boxs_style_overrides()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: plain
          text: |
            - one
              - two
          frame: content
        - id: styled
          text: |
            - one
              - two
          style: callout
          frame: content
      """);

    // Act
    SceneItem plain = deck.Item("two", "plain");
    SceneItem styled = deck.Item("two", "styled");

    // Assert
    plain.Paragraphs![1].Properties["size"].Value.ShouldBe(14.0);
    plain.Paragraphs[1].Hanging!.Value.ShouldBe(18.0);
    plain.Paragraphs[1].Bullet!.Value.ShouldBe("–");
    styled.Paragraphs![1].Properties["size"].Value.ShouldBe(20.0);
  }

  [Fact]
  public void A_boxs_style_chooses_its_bullets_and_a_paragraphs_style_chooses_over_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("  negative:", """
        pros: { levels: { 1: { bullet: "+", bullet_color: accent6, bullet_size: 1.5 } } }
        cons: { levels: { 1: { bullet: "–" } } }
        negative:
      """));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: box
          text: |
            - pro
              - detail
            {.cons}
            - con
          style: pros
          frame: content
      """);

    // Act
    List<SceneParagraph> paragraphs = deck.Item("two", "box").Paragraphs!;

    // Assert
    paragraphs[0].Bullet!.Value.ShouldBe("+");
    paragraphs[0].Bullet!.Origin!.ToString().ShouldBe("theme/styles.yaml:26 styles.pros.levels.1.bullet");
    paragraphs[0].BulletColor!.Value.ShouldBe(new Color { Theme = "accent6" });
    paragraphs[0].BulletSize!.Value.ShouldBe(1.5);
    paragraphs[1].Bullet!.Value.ShouldBe("–");
    paragraphs[1].BulletColor.ShouldBeNull();
    paragraphs[2].Bullet!.Value.ShouldBe("–");
    paragraphs[2].BulletColor!.Value.ShouldBe(new Color { Theme = "accent6" });
  }

  [Fact]
  public void A_level_hangs_by_the_gap_to_the_level_above_unless_it_says_how_far()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles
      .Replace("indent: 36 }", "indent: 36, hanging: 12 }")
      .Replace("  negative:", """
        wide: { levels: { 1: { indent: 30 }, 2: { indent: 60 } } }
        negative:
      """));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: plain
          text: |
            - one
              - two
          frame: content
        - id: wide
          text: |
            - one
              - two
          style: wide
          frame: content
      """);

    // Act
    SceneItem plain = deck.Item("two", "plain");
    SceneItem wide = deck.Item("two", "wide");

    // Assert
    plain.Paragraphs![0].Hanging!.Value.ShouldBe(18.0);
    plain.Paragraphs[1].Hanging!.Value.ShouldBe(12.0);
    plain.Paragraphs[1].Hanging!.Origin!.Path.ShouldBe("text.levels.2.hanging");
    wide.Paragraphs![0].Hanging!.Value.ShouldBe(30.0);
    wide.Paragraphs[1].Indent!.Value.ShouldBe(60.0);
    wide.Paragraphs[1].Hanging!.Value.ShouldBe(12.0);
  }

  [Fact]
  public void Runs_layer_the_paragraph_their_styles_their_properties_and_bold()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          text: |
            {.lead}
            **[$4.4B]{.emphasis size=30}** operating income
          frame: content
      """);

    // Act
    SceneParagraph paragraph = deck.Item("two", "note").Paragraphs!.Single();

    // Assert
    paragraph.Properties["size"].Origin!.Path.ShouldBe("styles.lead.size");
    SceneRun figure = paragraph.Runs[0];
    figure.Properties["size"].Value.ShouldBe(30.0);
    figure.Properties["color"].Origin!.Path.ShouldBe("styles.emphasis.color");
    figure.Properties["bold"].Origin!.Line.ShouldBe(6);
    paragraph.Runs[1].Properties["size"].Value.ShouldBe(18.0);
  }

  [Fact]
  public void A_shape_starts_from_the_shape_base_and_its_stroke_merges_key_by_key()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: box
          shape: chevron
          frame: content
          stroke: { color: accent1 }
          text: Launch
      """);

    // Act
    SceneItem box = deck.Item("two", "box");

    // Assert
    box.Properties["fill"].Origin!.ToString().ShouldBe("theme/styles.yaml:16 shape.fill");
    box.Properties["stroke.color"].Value.ShouldBe(new Color { Theme = "accent1" });
    box.Properties["stroke.width"].Origin!.Path.ShouldBe("shape.stroke.width");
    box.Properties["align"].Origin!.Path.ShouldBe("shape_text.align");
    box.Properties["direction"].ShouldBe(new Resolved("right", null));
  }

  [Fact]
  public void A_named_style_sets_fill_stroke_opacity_and_insets_between_the_shape_base_and_the_element()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("""
        negative: { color: "#C00000" }
      """, """
        negative: { color: "#C00000" }
        badge: { size: 11, fill: accent1, stroke: { width: 2 }, opacity: 60, insets: [4, 2, 4, 2] }
      """));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: shape
          shape: rectangle
          frame: content
          style: badge
          stroke: { color: accent2 }
          text: Shape
        - id: own
          shape: rectangle
          frame: left
          style: badge
          fill: accent3
          opacity: 80
          text: Own
        - id: box
          text: Box
          frame: left
          style: badge
      """);

    // Act
    SceneItem shape = deck.Item("two", "shape");
    SceneItem own = deck.Item("two", "own");
    SceneItem box = deck.Item("two", "box");

    // Assert
    shape.Properties["fill"].Value.ShouldBe(new Color { Theme = "accent1" });
    shape.Properties["fill"].Origin!.Path.ShouldBe("styles.badge.fill");
    shape.Properties["stroke.width"].Origin!.Path.ShouldBe("styles.badge.stroke.width");
    shape.Properties["stroke.color"].Value.ShouldBe(new Color { Theme = "accent2" });
    shape.Properties["opacity"].Origin!.Path.ShouldBe("styles.badge.opacity");
    shape.Properties["insets"].Origin!.Path.ShouldBe("styles.badge.insets");
    own.Properties["fill"].Value.ShouldBe(new Color { Theme = "accent3" });
    own.Properties["opacity"].Value.ShouldBe(80.0);
    box.Properties["fill"].Origin!.Path.ShouldBe("styles.badge.fill");
    box.Properties.ContainsKey("stroke.color").ShouldBeFalse();
  }

  [Fact]
  public void A_shadow_merges_key_by_key_from_the_shape_base_through_the_style_to_the_element()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles
      .Replace("shape: {", "shape: { shadow: { color: dark1, opacity: 40, blur: 4, distance: 3, angle: 90 },")
      .Replace("styles:\n", "styles:\n  lifted: { shadow: { distance: 8 } }\n"));
    deck.Write("deck/media/photo.png", "not read");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: card, shape: rectangle, frame: content, style: lifted, shadow: { color: accent1 } }
        - { id: flat, shape: rectangle, frame: left, shadow: none }
        - { id: note, text: Note, frame: left, style: lifted }
        - { id: photo, picture: ../media/photo.png, frame: [0, 0, 24, 24], shadow: { color: dark1, blur: 2 } }
      """);

    // Act
    SceneItem card = deck.Item("two", "card");
    SceneItem flat = deck.Item("two", "flat");
    SceneItem note = deck.Item("two", "note");
    SceneItem photo = deck.Item("two", "photo");

    // Assert
    card.Properties["shadow.color"].Value.ShouldBe(new Color { Theme = "accent1" });
    card.Properties["shadow.distance"].Origin!.Path.ShouldBe("styles.lifted.shadow.distance");
    card.Properties["shadow.blur"].Origin!.Path.ShouldBe("shape.shadow.blur");
    flat.Properties["shadow"].Value.ShouldBe("none");
    flat.Properties.Keys.ShouldNotContain(key => key.StartsWith("shadow."));
    note.Properties["shadow.distance"].Value.ShouldBe(8.0);
    note.Properties.ContainsKey("shadow.color").ShouldBeFalse();
    photo.Properties["shadow.blur"].Value.ShouldBe(2.0);
  }

  [Fact]
  public void A_lines_endpoints_name_their_lines()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: link
          line:
            from: [10, 20]
            to: [30, 40]
      """);

    // Act
    SceneItem link = deck.Item("two", "link");

    // Assert
    link.Properties["from"].Origin!.ToString().ShouldBe("deck/slides/two.yaml:5 elements.link.line.from");
    link.Properties["to"].Origin!.ToString().ShouldBe("deck/slides/two.yaml:6 elements.link.line.to");
  }

  [Fact]
  public void A_slide_hides_a_layout_element_by_its_id()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\nhide: [page]");
    Presentation presentation = deck.Load();

    // Act
    List<string> ids = SceneBuilder.Build(presentation, presentation.Slides[1]).Items.Select(item => item.Id)
      .ToList();
    deck.Write("deck/slides/two.yaml", "layout: content\nhide: [footer]");
    LoadException error = deck.Error();

    // Assert
    ids.ShouldNotContain("page");
    error.Problem.ShouldBe("unknown element footer; layout content draws page");
  }

  [Fact]
  public void A_slides_background_replaces_its_layouts()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/cover.yaml", "name: Cover\nbackground: dark2");
    deck.Write("deck/slides/two.yaml", "layout: cover\nbackground: \"#091024\"");
    Presentation presentation = deck.Load();

    // Act
    SlideScene scene = SceneBuilder.Build(presentation, presentation.Slides[1]);

    // Assert
    scene.Background.Value.ShouldBeOfType<Color>().Hex.ShouldBe("#091024");
    scene.Background.Origin!.ToString().ShouldBe("deck/slides/two.yaml:2 background");
  }


}