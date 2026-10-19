using Shouldly;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Pintura.Tests;

public class MarkupTests
{
  private static readonly MarkupContext Context = new MarkupContext(
    style => style is "lead" or "emphasis" or "positive",
    field => field is "slide" or "footer" ? null : $"unknown field {field}; define it in presentation.yaml fields",
    color => color == "positive",
    3);

  private static List<Paragraph> Parse(string text)
  {
    var node = new YamlScalarNode(text) { Style = ScalarStyle.Literal };
    return Markup.Parse(new Node(node, new SourceFile("", "slide.yaml"), "text", "text"), Context);
  }

  private static LoadException Error(string text)
  {
    return Should.Throw<LoadException>(() => Parse(text));
  }

  [Fact]
  public void Each_line_is_a_paragraph_and_bullets_indent_two_spaces_per_level()
  {
    // Arrange
    string text = "Lead\n- one\n  - nested\n- two\n";

    // Act
    List<Paragraph> paragraphs = Parse(text);

    // Assert
    paragraphs.Select(paragraph => paragraph.Level).ShouldBe([0, 1, 2, 1]);
    paragraphs[2].Runs.Single().Text.ShouldBe("nested");
  }

  [Fact]
  public void Numbering_counts_on_from_the_first_number_written_and_survives_deeper_bullets()
  {
    // Arrange
    string text = "1. one\n1. two\n  - aside\n1. three\nbreak\n1. again";

    // Act
    List<Paragraph> paragraphs = Parse(text);

    // Assert
    paragraphs.Select(paragraph => paragraph.Number).ShouldBe([1, 2, null, 3, null, 1]);
  }

  [Fact]
  public void An_escaped_list_marker_keeps_a_line_without_a_bullet()
  {
    // Arrange
    string text = "1\\. Click\n\\- or press Enter";

    // Act
    List<Paragraph> paragraphs = Parse(text);

    // Assert
    paragraphs.Select(paragraph => paragraph.Level).ShouldBe([0, 0]);
    paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text)))
      .ShouldBe(["1. Click", "- or press Enter"]);
  }

  [Fact]
  public void A_backslash_before_a_dash_or_dot_past_the_line_start_stays_literal()
  {
    // Arrange
    string text = "C:\\Users\\.config and a\\-b";

    // Act
    List<Paragraph> paragraphs = Parse(text);

    // Assert
    string.Concat(paragraphs.Single().Runs.Select(run => run.Text)).ShouldBe("C:\\Users\\.config and a\\-b");
  }

  [Fact]
  public void A_style_line_styles_the_next_paragraph()
  {
    // Arrange
    string text = "{.lead}\nHeading\nBody";

    // Act
    List<Paragraph> paragraphs = Parse(text);

    // Assert
    paragraphs.Count.ShouldBe(2);
    paragraphs[0].Style.ShouldBe("lead");
    paragraphs[1].Style.ShouldBeNull();
  }

  [Fact]
  public void Bold_italic_links_and_spans_become_runs()
  {
    // Arrange
    string text = "Revenue of **$18.7B**, *up* [33%]{.positive size=24} per [the filing](https://sec.gov)";

    // Act
    List<Run> runs = Parse(text)[0].Runs;

    // Assert
    runs.Select(run => run.Text).ShouldBe(["Revenue of ", "$18.7B", ", ", "up", " ", "33%", " per ", "the filing"]);
    runs[1].Bold.ShouldBe(true);
    runs[3].Italic.ShouldBe(true);
    runs[5].Styles.ShouldBe(["positive"]);
    runs[5].Properties!.Size.ShouldBe(24);
    runs[7].Link.ShouldBe("https://sec.gov");
  }

  [Fact]
  public void A_span_inside_bold_is_bold_with_its_own_properties()
  {
    // Arrange
    string text = "**[$28.5B]{size=40 font=heading}**";

    // Act
    Run run = Parse(text)[0].Runs.Single();

    // Assert
    run.Bold.ShouldBe(true);
    run.Properties!.Size.ShouldBe(40);
    run.Properties.Font.ShouldBe("heading");
  }

  [Fact]
  public void Several_styles_on_a_span_keep_their_order()
  {
    // Arrange
    string text = "[x]{.emphasis .positive color=\"positive\"}";

    // Act
    Run run = Parse(text)[0].Runs.Single();

    // Assert
    run.Styles.ShouldBe(["emphasis", "positive"]);
    run.Properties!.Color!.Named.ShouldBe("positive");
  }

  [Fact]
  public void Dollars_asterisks_and_brackets_without_their_partners_are_literal()
  {
    // Arrange
    string text = "$4.7B at 3 * 4 [draft] and \\${slide}";

    // Act
    Run run = Parse(text)[0].Runs.Single();

    // Assert
    run.Text.ShouldBe("$4.7B at 3 * 4 [draft] and ${slide}");
  }

  [Fact]
  public void Fields_are_runs_of_their_own()
  {
    // Arrange
    string text = "Page ${slide} of the deck";

    // Act
    List<Run> runs = Parse(text)[0].Runs;

    // Assert
    runs.Select(run => run.Field).ShouldBe([null, "slide", null]);
  }

  [Fact]
  public void Bold_as_a_span_attribute_points_to_asterisks()
  {
    // Act
    LoadException error = Error("[x]{bold=true}");

    // Assert
    error.Problem.ShouldBe("bold is not a span attribute; write **bold** or *italic* around the span");
  }

  [Fact]
  public void An_odd_indent_is_an_error()
  {
    // Act
    LoadException error = Error("- one\n   - three spaces");

    // Assert
    error.Problem.ShouldBe("bullets indent two spaces per level");
  }

  [Fact]
  public void A_level_beyond_the_themes_levels_is_an_error()
  {
    // Act
    LoadException error = Error("- 1\n  - 2\n    - 3\n      - 4");

    // Assert
    error.Problem.ShouldStartWith("bullet level 4");
    error.Topic.ShouldBe("styles");
  }

  [Fact]
  public void Plain_text_keeps_markup_literal_and_fills_fields()
  {
    // Arrange
    var node = new YamlScalarNode("**Q3** ${footer}");

    // Act
    List<Run> runs = Markup.ParsePlain(new Node(node, new SourceFile("", "slide.yaml"), "title", "slides"),
      Context.FieldProblem);

    // Assert
    runs.Select(run => run.Text).ShouldBe(["**Q3** ", ""]);
    runs[1].Field.ShouldBe("footer");
  }


}