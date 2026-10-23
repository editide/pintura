namespace Pintura.Tests;

/// <summary>A small theme and presentation in a temp folder, with each file replaceable per test. Paths in errors
/// are relative to the temp root, so a test can assert <c>deck/slides/one.yaml:4</c>.</summary>
public class DeckFixture : IDisposable
{
  public const string Theme = """
    name: Test

    colors:
      dark1: "#000000"
      light1: "#FFFFFF"
      dark2: "#44546A"
      light2: "#E7E6E6"
      accent1: "#4472C4"
      accent2: "#ED7D31"
      accent3: "#A5A5A5"
      accent4: "#FFC000"
      accent5: "#5B9BD5"
      accent6: "#70AD47"
      hyperlink: "#0563C1"
      followed_hyperlink: "#954F72"

    named_colors:
      muted: dark1 lighter 50

    fonts:
      heading: Georgia
      body: Arial

    title: { frame: [40, 24, 880, 56], style: title }

    areas:
      content: [40, 100, 880, 380]
      left: [40, 100, 430, 380]
    """;

  public const string Styles = """
    text:
      font: body
      size: 16
      color: dark1
      align: left
      valign: top
      insets: [7.2, 3.6, 7.2, 3.6]
      levels:
        1: { bullet: "•", indent: 18, space_before: 6 }
        2: { bullet: "–", size: 14, indent: 36 }

    shape_text: { align: center, valign: middle }

    picture: { fit: contain }

    shape: { fill: light2, stroke: { color: dark1, width: 1 } }

    line: { stroke: { color: dark1, width: 1 } }

    styles:
      title: { font: heading, size: 28 }
      caption: { size: 11, color: muted }
      lead: { size: 18, bold: true }
      callout: { size: 20, color: accent1 }
      emphasis: { bold: true, color: accent1 }
      negative: { color: "#C00000" }

    chart:
      series_colors: [accent1, accent2, accent3]
      text: { size: 11, color: dark1 }
      value_axis: { gridlines: true }
      legend: bottom

    charts:
      minimal:
        value_axis: { visible: false }
        data_labels: { show: value, position: center }

    formats:
      billions: { scale: 0.001, decimals: 1, prefix: "$", suffix: B, negative: parentheses, negative_style: negative }
      dollars: { negative: parentheses, negative_style: negative }

    tables:
      plain:
        header: { bold: true, fill: light2, border_bottom: { color: dark1, width: 1 } }
        total: { bold: true, border_top: { color: dark1, width: 1 }, border_bottom: { color: dark1, width: 2 } }
        banded: light2 lighter 50
        indent: 12
    """;

  public DeckFixture()
  {
    Folder.Write("theme/theme.yaml", Theme);
    Folder.Write("theme/styles.yaml", Styles);
    Folder.Write("theme/layouts/content.yaml", """
      name: Content
      elements:
        - id: page
          text: ${slide}
          style: caption
          frame: [860, 510, 60, 16]
      """);
    Folder.Write("theme/layouts/cover.yaml", """
      name: Cover
      title: { frame: [60, 190, 840, 90], color: light1 }
      placeholders:
        subtitle: { frame: [60, 290, 840, 40], style: caption }
      areas:
        left: [60, 340, 400, 100]
      """);
    Folder.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      fields:
        footer: Confidential
      slides:
        - one
        - section: Results
        - two
      """);
    Folder.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      placeholders:
        subtitle: ${footer}
      """);
    Folder.Write("deck/slides/two.yaml", """
      layout: content
      title: Results in ${section}
      """);
  }

  public TempFolder Folder { get; } = new TempFolder();

  public void Write(string relative, string content)
  {
    Folder.Write(relative, content);
  }

  /// <summary>A chart file and its CSV, and a slide <c>chart</c> placing it on the left.</summary>
  public void Chart(string yaml, string csv = "segment,2024,2025\nSpace,3796,4086\nAI,2620,-3201")
  {
    Folder.Write("deck/charts/revenue.yaml", yaml);
    Folder.Write("deck/charts/revenue.csv", csv);
    Folder.Write("deck/slides/chart.yaml", """
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    Folder.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - chart
      """);
  }

  /// <summary>A table file and its CSV, and a slide <c>table</c> placing it in the content area.</summary>
  public void Table(string yaml, string csv = "segment,2024,2025\nSpace,3796,4086\nAI,2620,-3201\nTotal,6416,885")
  {
    Folder.Write("deck/tables/results.yaml", yaml);
    Folder.Write("deck/tables/results.csv", csv);
    Folder.Write("deck/slides/table.yaml", """
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    Folder.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      slides:
        - table
      """);
  }

  /// <summary>Books a test's references read, which stand in for Libreta.</summary>
  public IBookReader? Books { get; set; }

  public Presentation Load()
  {
    return PresentationLoader.Load(Folder.PathTo("deck"), Folder.Root, Books);
  }

  /// <summary>Loads and returns the error a slide kept, or the one that stopped the whole presentation.</summary>
  public LoadException Error(string slide = "two")
  {
    try
    {
      return Load().Slides.Single(candidate => candidate.Id == slide).Error
        ?? throw new InvalidOperationException($"slide {slide} loaded without an error");
    }
    catch (LoadException exception)
    {
      return exception;
    }
  }

  public SceneItem Item(string slide, string id)
  {
    Presentation presentation = Load();
    return SceneBuilder.Build(presentation, presentation.Slides.Single(candidate => candidate.Id == slide)).Items
      .Single(item => item.Id == id);
  }

  public void Dispose()
  {
    Folder.Dispose();
  }


}