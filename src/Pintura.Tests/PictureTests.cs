using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class PictureTests
{
  private sealed class FewPictures : IToolHost
  {
    public string Version => "test";
    public int? MostPictures => 1;

    public IToolSession Open(string deck)
    {
      throw new InvalidOperationException("opened the deck");
    }


  }

  [Fact]
  public void An_export_with_more_pictures_than_the_tools_take_is_refused_before_it_starts()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/a.png", "a");
    deck.Write("deck/media/b.png", "b");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: a, picture: ../media/a.png, frame: [0, 0, 10, 10] }
        - { id: b, picture: ../media/b.png, frame: [20, 0, 10, 10] }
        - { id: again, picture: ../media/a.png, frame: [40, 0, 10, 10] }
      """);

    // Act
    InvalidOperationException refusal = Should.Throw<InvalidOperationException>(() =>
      Exporter.Write(deck.Load(), deck.Folder.PathTo("out/deck.pptx"), new FewPictures()));

    // Assert
    refusal.Message.ShouldBe("the presentation uses 2 different pictures, and an export takes at most 1");
  }

  [Fact]
  public void An_export_with_a_picture_larger_than_the_tools_take_is_refused_before_it_starts()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/a.png", "a");
    File.WriteAllBytes(deck.Folder.PathTo("deck/media/a.png"), new byte[10 * 1024 * 1024 + 1]);
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: a, picture: ../media/a.png, frame: [0, 0, 10, 10] }
      """);
    var tools = new BlandTools();

    // Act
    InvalidOperationException refusal = Should.Throw<InvalidOperationException>(() =>
      Exporter.Write(deck.Load(), deck.Folder.PathTo("out/deck.pptx"), tools));

    // Assert
    refusal.Message.ShouldBe("picture media/a.png is over 10 MB, the most an export takes");
    tools.Calls.ShouldBeEmpty();
  }

  [Fact]
  public void An_svg_in_two_fills_counts_as_two_pictures_against_the_tools_limit()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"/>""");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: a, picture: ../media/icon.svg, frame: [0, 0, 10, 10], fill: accent1 }
        - { id: b, picture: ../media/icon.svg, frame: [20, 0, 10, 10], fill: accent2 }
      """);

    // Act
    InvalidOperationException refusal = Should.Throw<InvalidOperationException>(() =>
      Exporter.Write(deck.Load(), deck.Folder.PathTo("out/deck.pptx"), new FewPictures()));

    // Assert
    refusal.Message.ShouldBe("the presentation uses 2 different pictures, and an export takes at most 1");
  }

  [Fact]
  public async Task An_svg_is_drawn_as_a_png_of_the_size_asked_keeping_its_transparency()
  {
    // Arrange
    byte[] svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 2 1"><rect width="1" height="1" fill="red"/></svg>"""u8
      .ToArray();

    // Act
    List<byte[]> pngs = await HeadlessBrowser.Rasterize([(svg, 200, 100)]);

    // Assert
    using var folder = new TempFolder();
    folder.Write("drawn.png", "");
    File.WriteAllBytes(folder.PathTo("drawn.png"), pngs.Single());
    Exporter.Intrinsic(folder.PathTo("drawn.png")).ShouldBe([200, 100]);
    // Color type 6 is RGBA.
    pngs.Single()[25].ShouldBe((byte)6);
  }

  private const string Stroked = """
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor">
    <path d="M4 4h16"/><circle r="2" fill="#123456" style="stroke: red; opacity: 0.5"/></svg>
    """;

  [Fact]
  public void Recoloring_an_svg_paints_each_fill_and_stroke_but_none_in_the_color()
  {
    // Act
    string painted = SvgPaint.Recolor(Stroked, "#4472C4");

    // Assert
    painted.ShouldContain("fill=\"none\"");
    painted.ShouldContain("stroke=\"#4472C4\"");
    painted.ShouldContain("fill=\"#4472C4\"");
    painted.ShouldContain("style=\"stroke: #4472C4; opacity: 0.5\"");
    painted.ShouldNotContain("currentColor");
    painted.ShouldNotContain("#123456");
    painted.ShouldNotContain("red");
  }

  [Fact]
  public void Recoloring_an_svg_paints_what_it_leaves_black_by_default_and_what_its_stylesheet_paints()
  {
    // Arrange
    string svg = """
      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><style>.a { fill: #FF0000; stroke: none }</style>
      <path d="M4 4h16"/><path class="a" d="M4 8h16"/>
      <linearGradient><stop stop-color="#00FF00"/></linearGradient></svg>
      """;

    // Act
    string painted = SvgPaint.Recolor(svg, "#4472C4");

    // Assert
    painted.ShouldContain("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"#4472C4\"");
    painted.ShouldContain(".a { fill: #4472C4; stroke: none }");
    painted.ShouldContain("stop-color=\"#4472C4\"");
  }

  [Fact]
  public void An_svg_counts_each_distinct_paint_but_none()
  {
    // Arrange
    string icon = """<svg xmlns="http://www.w3.org/2000/svg" fill="none" stroke="currentColor"><path/></svg>""";
    string plain = """<svg xmlns="http://www.w3.org/2000/svg"><path/></svg>""";

    // Act
    int[] paints = [SvgPaint.Paints(Stroked), SvgPaint.Paints(icon), SvgPaint.Paints(plain)];

    // Assert
    paints.ShouldBe([3, 1, 0]);
  }

  [Fact]
  public void A_fill_recolors_an_svg_picture_from_the_element_or_its_style()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"/>""");
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("styles:\n", "styles:\n  icon: { fill: accent2 }\n"));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: own, picture: ../media/icon.svg, frame: [0, 0, 24, 24], fill: accent1 lighter 40 }
        - { id: styled, picture: ../media/icon.svg, frame: [40, 0, 24, 24], style: icon }
        - { id: plain, picture: ../media/icon.svg, frame: [80, 0, 24, 24] }
      """);

    // Act
    List<string> pictures = PayloadBuilder.Build(deck.Load(), 1, (path, fill) => fill ?? "unfilled").Slides[1].Items!
      .Where(item => item.Kind == "picture")
      .Select(item => item.Picture!)
      .ToList();

    // Assert
    pictures.ShouldBe(["#8FAADC", "#ED7D31", "unfilled"]);
  }

  [Fact]
  public void A_fill_on_a_picture_that_isnt_an_svg_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/photo.png", "not read");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: photo, picture: ../media/photo.png, frame: [0, 0, 24, 24], fill: accent1 }
      """);

    // Act
    string error = deck.Load().Slides[1].Error!.Problem;

    // Assert
    error.ShouldBe("fill recolors an SVG picture, and ../media/photo.png isn't one");
  }

  [Fact]
  public void The_export_draws_a_filled_svg_in_its_color_resolved_against_the_theme()
  {
    // Arrange
    JsonNode deck = JsonNode.Parse("""{ "theme": { "colors": { "accent1": "#4472C4" } } }""")!;

    // Act
    string?[] fills = [
      Exporter.PictureFill(JsonNode.Parse("""{ "fill": { "theme": "accent1", "lighter": 40 } }"""), deck),
      Exporter.PictureFill(JsonNode.Parse("""{ "fill": { "hex": "#C00000" } }"""), deck),
      Exporter.PictureFill(JsonNode.Parse("""{ }"""), deck)
    ];

    // Assert
    fills.ShouldBe(["#8FAADC", "#C00000", null]);
  }


  [Fact]
  public void A_fill_flattening_an_svg_of_several_colors_is_a_warning()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/logo.svg", """
      <svg xmlns="http://www.w3.org/2000/svg"><path fill="#FF0000"/><path fill="#00FF00"/></svg>
      """);
    deck.Write("deck/media/icon.svg", """<svg xmlns="http://www.w3.org/2000/svg"><path fill="#FF0000"/></svg>""");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: logo, picture: ../media/logo.svg, frame: [0, 0, 24, 24], fill: accent1 }
        - { id: icon, picture: ../media/icon.svg, frame: [40, 0, 24, 24], fill: accent1 }
        - { id: plain, picture: ../media/logo.svg, frame: [80, 0, 24, 24] }
      """);

    // Act
    List<Finding> findings = Checks.Structure(deck.Load());

    // Assert
    findings.ShouldBe([
      new Finding("warning", "structure", "two/logo", "fill paints an SVG picture of 2 colors in one") { Slide = "two" }
    ]);
  }

  [Fact]
  public void A_picture_from_a_folder_beside_the_presentation_loads()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("shared/logo.png", "logo");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: logo, picture: ../../shared/logo.png, frame: [0, 0, 10, 10] }
      """);

    // Act
    Presentation presentation = deck.Load();

    // Assert
    presentation.Slides.Single(slide => slide.Id == "two").Elements.Single().PicturePath
      .ShouldBe(deck.Folder.PathTo("shared/logo.png"));
  }

  [Fact]
  public void A_picture_that_isnt_an_image_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/media/notes.txt", "notes");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: notes, picture: ../media/notes.txt, frame: [0, 0, 10, 10] }
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldStartWith("../media/notes.txt isn't a picture");
  }

  [Fact]
  public void A_link_named_like_a_picture_to_a_file_that_isnt_one_is_refused()
  {
    if (OperatingSystem.IsWindows())
    {
      // Making a link on Windows needs developer mode or an administrator.
      return;
    }

    // Arrange
    using var deck = new DeckFixture();
    deck.Write("elsewhere/key", "not a picture");
    Directory.CreateDirectory(deck.Folder.PathTo("deck/media"));
    File.CreateSymbolicLink(deck.Folder.PathTo("deck/media/logo.png"), deck.Folder.PathTo("elsewhere/key"));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: logo, picture: ../media/logo.png, frame: [0, 0, 10, 10] }
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldStartWith("../media/logo.png isn't a picture");
  }

  [Fact]
  public void A_chart_or_table_reading_a_file_that_isnt_a_csv_is_refused()
  {
    // Arrange
    using var charted = new DeckFixture();
    charted.Write("deck/charts/figures.txt", "segment,2025\nSpace,4086");
    charted.Chart("""
      type: clustered_column
      data: figures.txt
      """);
    using var tabled = new DeckFixture();
    tabled.Write("deck/tables/figures.txt", "segment,2025\nSpace,4086");
    tabled.Table("""
      data: figures.txt
      header_rows: 1
      """);

    // Act
    LoadException chart = charted.Error("chart");
    LoadException table = tabled.Error("table");

    // Assert
    chart.Message.ShouldStartWith("figures.txt isn't a CSV file");
    table.Message.ShouldStartWith("figures.txt isn't a CSV file");
  }

  [Fact]
  public void A_chart_or_table_named_by_a_path_rather_than_a_stem_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("outside/revenue.yaml", "type: clustered_column\ndata: revenue.csv");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: chart, chart: ../../outside/revenue, frame: left }
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Message.ShouldStartWith("name a chart by its file's stem in charts/");
  }


}