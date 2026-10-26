using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class LoaderTests
{
  [Fact]
  public void An_unknown_key_names_the_file_the_line_and_the_topic()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: note
          text: Hello
          frame: [40, 100, 200, 40]
          colour: accent1
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldBe("unknown key colour in deck/slides/two.yaml:7; see pintura --docs slides");
  }

  [Fact]
  public void An_unknown_key_on_a_shape_points_to_the_shapes_topic()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: box
          shape: rectangle
          frame: [40, 100, 200, 40]
          radius: 4
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Topic.ShouldBe("shapes");
    error.Origin.Line.ShouldBe(6);
  }

  [Fact]
  public void An_unknown_key_in_the_theme_stops_the_whole_presentation()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles + "\nlegend_position: bottom");

    // Act
    LoadException error = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    error.Message.ShouldStartWith("unknown key legend_position in theme/styles.yaml:");
    error.Topic.ShouldBe("styles");
  }

  [Fact]
  public void Elements_are_drawn_by_layouts_never_by_the_theme()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    deck.Write("theme/theme.yaml", DeckFixture.Theme + "\nelements:\n  - id: rule\n    line: { from: [40, 90], to: [920, 90] }");
    LoadException themed = Should.Throw<LoadException>(() => deck.Load());
    deck.Write("theme/theme.yaml", DeckFixture.Theme);
    deck.Write("theme/layouts/cover.yaml", "name: Cover\ntheme_elements: false");
    LoadException switched = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    themed.Message.ShouldStartWith("unknown key elements in theme/theme.yaml:");
    switched.Message.ShouldStartWith("unknown key theme_elements in theme/layouts/cover.yaml:2");
  }

  [Fact]
  public void A_styles_levels_take_only_bullets_and_indents_and_only_the_themes_levels()
  {
    // Arrange
    using var deck = new DeckFixture();
    string Styles(string style) => DeckFixture.Styles.Replace("  negative:", $"  {style}\n  negative:");

    // Act
    deck.Write("theme/styles.yaml", Styles("pros: { levels: { 1: { bullet: \"+\", size: 12 } } }"));
    LoadException size = Should.Throw<LoadException>(() => deck.Load());
    deck.Write("theme/styles.yaml", Styles("pros: { levels: { 3: { bullet: \"+\" } } }"));
    LoadException deeper = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    size.Message.ShouldStartWith("unknown key size in theme/styles.yaml:26");
    deeper.Message.ShouldStartWith("the theme's text has levels 1 to 2 in theme/styles.yaml:26");
  }

  [Fact]
  public void Unquoted_markup_starting_with_a_bracket_asks_for_quotes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          text: [$4.4B]{.emphasis} of income
          frame: [40, 100, 200, 40]
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldEndWith("quote text starting with [ or {, or write it as a block scalar (|)");
  }

  [Fact]
  public void Text_YAML_read_as_a_list_asks_for_quotes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          text: [Draft]
          frame: [40, 100, 200, 40]
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldContain("quote text starting with [ or {");
    error.Origin.Line.ShouldBe(4);
  }

  [Fact]
  public void A_markup_error_names_its_line_within_a_block()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          frame: content
          text: |
            - one
               - three spaces
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldBe("bullets indent two spaces per level in deck/slides/two.yaml:7; see pintura --docs text");
  }

  [Fact]
  public void A_broken_slide_keeps_its_error_and_the_others_load()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: missing");

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Slides[0].Error.ShouldBeNull();
    presentation.Slides[1].Error!.Message.ShouldBe(
      "unknown layout missing; the theme's layouts are content, cover in deck/slides/two.yaml:1; " +
      "see pintura --docs layouts");
  }

  [Fact]
  public void A_frame_naming_an_area_takes_the_layouts_rectangle_over_the_themes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", """
      layout: cover
      elements:
        - id: note
          text: Hello
          frame: left
      """);

    // Act
    Element note = deck.Load().Slides[0].Elements[0];

    // Assert
    note.Frame!.Area.ShouldBe("left");
    note.Frame.Rect.ShouldBe([60, 340, 400, 100]);
  }

  [Fact]
  public void An_unknown_area_lists_the_ones_there_are()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: note
          text: Hello
          frame: right
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("unknown area right; areas here are content, left");
  }

  [Fact]
  public void An_undefined_field_is_an_error_naming_presentation_fields()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Board update, ${date}
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("unknown field date; define it in presentation.yaml fields");
    error.Topic.ShouldBe("presentations");
  }

  [Fact]
  public void The_themes_shrink_threshold_is_a_percent()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/theme.yaml", $"{DeckFixture.Theme}\n\nchecks: {{ shrink: 0.7 }}");
    Presentation valid = deck.Load();
    deck.Write("theme/theme.yaml", $"{DeckFixture.Theme}\n\nchecks: {{ shrink: 170 }}");

    // Act
    LoadException error = deck.Error();

    // Assert
    valid.Theme.Checks.Shrink.ShouldBe(0.7);
    error.Problem.ShouldBe("shrink is a percent, more than 0 and at most 100");
    error.Topic.ShouldBe("checks");
  }

  [Fact]
  public void A_placeholder_the_layout_lacks_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      placeholders:
        subtitle: Hello
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("unknown placeholder subtitle; layout content has none");
  }

  [Theory]
  [InlineData("subtitle", "subtitle isn't the title or a placeholder this slide writes: title")]
  [InlineData("missing", "missing isn't the title or a placeholder this slide writes: title")]
  public void An_elements_entry_naming_what_the_slide_doesnt_write_is_an_error(string name, string problem)
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", $"""
      layout: cover
      title: Hello
      elements:
        - {name}
      """);

    // Act
    LoadException error = deck.Error("one");

    // Assert
    error.Problem.ShouldBe(problem);
    error.Origin.ToString().ShouldBe("deck/slides/one.yaml:4 elements[1]");
  }

  [Fact]
  public void An_elements_entry_naming_the_title_twice_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      elements:
        - title
        - title
      """);

    // Act
    LoadException error = deck.Error("one");

    // Assert
    error.Problem.ShouldBe("title is placed twice");
  }

  [Fact]
  public void A_layouts_elements_take_no_entry_naming_the_title()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/layouts/cover.yaml", """
      name: Cover
      title: { frame: [60, 190, 840, 90], color: light1 }
      elements:
        - title
      """);

    // Act
    LoadException error = deck.Error("one");

    // Assert
    error.Problem.ShouldBe("elements[1] must be a map of keys");
  }

  [Fact]
  public void A_slide_element_reusing_a_layout_elements_id_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: page
          text: Hello
          frame: content
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("duplicate id page, already used by layout content");
  }

  [Fact]
  public void A_line_with_a_frame_is_told_to_use_endpoints()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: rule
          line: { from: [40, 88], to: [920, 88] }
          frame: [40, 88, 880, 0]
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldStartWith("a line has no frame");
    error.Topic.ShouldBe("shapes");
  }

  [Fact]
  public void A_direction_on_a_shape_without_one_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - id: box
          shape: rectangle
          direction: right
          frame: content
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("a rectangle has no direction");
  }

  [Fact]
  public void A_missing_theme_color_is_named()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/theme.yaml", DeckFixture.Theme.Replace("  accent6: \"#70AD47\"\n", ""));

    // Act
    LoadException error = Should.Throw<LoadException>(() => deck.Load());

    // Assert
    error.Problem.ShouldBe("colors needs all twelve theme colors, and accent6 is missing");
    error.Topic.ShouldBe("colors");
  }

  [Fact]
  public void Invalid_YAML_names_the_line()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: [unclosed");

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldStartWith("invalid YAML");
    error.Origin.File.ShouldBe("deck/slides/two.yaml");
  }

  [Fact]
  public void Sections_group_the_slides_after_them()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Sections.Select(section => section.Name).ShouldBe([null, "Results"]);
    presentation.Slides[1].Section.ShouldBe("Results");
    presentation.Slides[1].Number.ShouldBe(2);
  }

  private static string Note(string text, string extra = "")
  {
    return $"layout: content\nelements:\n  - id: note\n    text: \"{text}\"\n    frame: [40, 100, 200, 40]{extra}";
  }

  public static TheoryData<string, Action<DeckFixture>, string?> Malformed => new()
  {
    { "a frame of NaN", deck => deck.Write("deck/slides/two.yaml", Note("Hi").Replace("[40,", "[NaN,")),
      "must be a number" },
    { "a frame past a double", deck => deck.Write("deck/slides/two.yaml", Note("Hi").Replace("[40,", "[1e400,")),
      "must be a number" },
    { "nan in a chart's CSV", deck => deck.Chart("type: clustered_column\ndata: revenue.csv",
      "segment,2024,2025\nSpace,nan,4086\nAI,2620,1"), "isn't a plain number" },
    { "NaN in a formatted table cell", deck => deck.Table("data: results.csv\nstyle: plain\ncolumns:\n  - {}\n" +
      "  - { format: billions }", "segment,2024\nSpace,NaN\nAI,1e22"), null },
    { "negative decimals", deck => deck.Write("theme/styles.yaml",
      DeckFixture.Styles.Replace("decimals: 1", "decimals: -1")), "decimals must be a whole number from 0 to 15" },
    { "a list numbered past an int", deck => deck.Write("deck/slides/two.yaml", Note("99999999999. Hi")), null },
    { "a color step past an int", deck => deck.Write("deck/slides/two.yaml",
      Note("Hi", "\n    color: accent1 lighter 99999999999")), "not a color" },
    { "a date step past an int", deck => deck.Chart("type: line\ndata: revenue.csv\n" +
      "category_axis: { step: 99999999999 days }", "date,value\n2025-01-01,1\n2025-02-01,2"), "step is a time" },
    { "a span size of NaN", deck => deck.Write("deck/slides/two.yaml", Note("[Hi]{size=NaN}")), "span size" },
    { "a negative stroke", deck => deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - id: box\n" +
      "    shape: rectangle\n    frame: [40, 100, 200, 40]\n    stroke: { width: -1 }"), "width must be 0 or more" },
    { "a malformed SVG to recolor", deck =>
      {
        deck.Write("deck/pictures/icon.svg", "<svg><path></svg>");
        deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - id: icon\n    picture: ../pictures/icon.svg\n" +
          "    fill: accent1\n    frame: [40, 100, 40, 40]");
      }, "isn't well-formed SVG" },
    { "a picture outside the presentation's folder and repository", deck =>
      {
        string outside = Elsewhere();
        deck.Write("deck/slides/two.yaml", $"layout: content\nelements:\n  - id: leak\n    picture: {outside}\n" +
          "    frame: [40, 100, 40, 40]");
      }, "outside the presentation" },
    { "a picture linked to a file outside", deck =>
      {
        Directory.CreateDirectory(deck.Folder.PathTo("deck/pictures"));
        File.CreateSymbolicLink(deck.Folder.PathTo("deck/pictures/leak.png"), Elsewhere());
        deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - id: leak\n    picture: ../pictures/leak.png\n" +
          "    frame: [40, 100, 40, 40]");
      }, "outside the presentation" },
    { "a link that runs script", deck => deck.Write("deck/slides/two.yaml", Note("[Hi](javascript:alert(1))")),
      "a link is http" },
    { "a picture that isn't one", deck =>
      {
        deck.Write("deck/notes.txt", "secret");
        deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - id: leak\n    picture: ../notes.txt\n" +
          "    frame: [40, 100, 40, 40]");
      }, "isn't a picture" },
    { "a crop wider than its PNG", deck => Cropped(deck, "png", Images.Png(600, 400), "[300, 0, 400, 0]"),
      "crop trims 700 of photo.png's 600 pixels across" },
    { "a crop taller than its JPEG", deck => Cropped(deck, "jpg", Images.Jpeg(600, 400), "[0, 100, 0, 300]"),
      "crop trims 400 of photo.jpg's 400 pixels down" },
    { "a crop wider than its GIF", deck => Cropped(deck, "gif", Images.Gif(600, 400), "[600, 0, 0, 0]"),
      "of photo.gif's 600 pixels across" },
    { "a crop wider than its WebP", deck => Cropped(deck, "webp", Images.WebP(600, 400), "[0, 0, 601, 0]"),
      "of photo.webp's 600 pixels across" },
    { "a crop taller than its SVG", deck => Cropped(deck, "svg",
      "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 60 40\"/>"u8.ToArray(), "[0, 20, 0, 20]"),
      "of photo.svg's 40 pixels down" },
    { "a negative crop", deck => Cropped(deck, "png", Images.Png(600, 400), "[-10, 0, 0, 0]"),
      "crop trims [left, top, right, bottom] pixels from the image's edges, each 0 or more" }
  };

  [Fact]
  public void A_crop_trimming_part_of_each_edge_loads()
  {
    // Arrange
    using var deck = new DeckFixture();
    Cropped(deck, "png", Images.Png(600, 400), "[100, 50, 100, 50]");

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Slides.ShouldAllBe(slide => slide.Error == null);
  }

  private static void Cropped(DeckFixture deck, string extension, byte[] image, string crop)
  {
    deck.Write($"deck/media/photo.{extension}", "");
    File.WriteAllBytes(deck.Folder.PathTo($"deck/media/photo.{extension}"), image);
    deck.Write("deck/slides/two.yaml", $"layout: content\nelements:\n  - id: photo\n    picture: ../media/photo.{extension}" +
      $"\n    crop: {crop}\n    frame: [40, 100, 300, 200]");
  }

  [Fact]
  public void A_picture_can_come_from_a_folder_shared_beside_the_presentation()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("assets/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>");
    deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - id: logo\n    picture: ../../assets/logo.svg\n" +
      "    frame: [40, 100, 40, 40]");

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Slides.ShouldAllBe(slide => slide.Error == null);
  }

  /// <summary>A picture in a folder of its own, beside no presentation.</summary>
  private static string Elsewhere()
  {
    string folder = Directory.CreateTempSubdirectory("pintura-elsewhere-").FullName;
    string path = Path.Combine(folder, "private.png");
    File.WriteAllText(path, "secret");
    return path;
  }

  [Theory]
  [MemberData(nameof(Malformed))]
  public void A_value_the_engine_cant_use_is_an_error_naming_it_never_a_crash(string _, Action<DeckFixture> write,
    string? problem)
  {
    // Arrange
    using var deck = new DeckFixture();
    write(deck);
    List<string> errors = [];

    // Act
    Exception? thrown = Record.Exception(() =>
    {
      Presentation presentation = deck.Load();
      errors.AddRange(presentation.Slides.Select(slide => slide.Error?.Message).OfType<string>());
      System.Text.Json.JsonSerializer.Serialize(PayloadBuilder.Build(presentation, 1, (path, _) => path),
        PayloadJson.Options);
    });

    // Assert
    if (thrown is not null)
    {
      thrown.ShouldBeOfType<LoadException>().Message.ShouldContain(problem!);
    }
    else if (problem is null)
    {
      errors.ShouldBeEmpty();
    }
    else
    {
      errors.ShouldHaveSingleItem().ShouldContain(problem);
    }
  }

  [Fact]
  public void A_format_groups_any_number_of_digits()
  {
    // Act
    string spelled = new NumberFormat().Spell(1e19);

    // Assert
    spelled.ShouldBe("10,000,000,000,000,000,000");
  }

  public static string RepositoryRoot()
  {
    DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
    {
      directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("no repository root above the tests");
  }


}