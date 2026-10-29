using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class PatcherTests
{
  private const string Slide = """
    layout: content
    # The chart sits in the left area.
    title: Connectivity revenue nearly tripled
    placeholders:
      kicker: "Board update, ${date}"

    elements:
      - id: chart
        chart: revenue
        frame: left   # moved with the area
      - id: arrow
        line: { from: [100, 200], to: [300, 200] }
        end: arrow
      - id: takeaway
        frame: [490, 100, 430, 170]
        text: |
          {.lead}
          Revenue of [$11.4B]{.positive} in ${date}
          - Up **33%** on 2024
    """;

  private static readonly MarkupContext Context = new MarkupContext(style => style is "lead" or "positive",
    field => field is "date" or "slide" ? null : $"unknown field {field}", _ => false, 2);

  private static string Apply(string text, params ValueEdit[] edits)
  {
    return Patcher.Apply(text, edits, Context).Text;
  }

  private static string Undo(string text, params ValueEdit[] edits)
  {
    Patched patched = Patcher.Apply(text, edits, Context);
    return Patcher.Apply(patched.Text, patched.Undo, Context).Text;
  }

  private static string[] Changed(string before, string after)
  {
    string[] old = before.Split('\n');
    string[] lines = after.Split('\n');
    lines.Length.ShouldBe(old.Length);
    return lines.Where((line, index) => line != old[index]).ToArray();
  }

  [Fact]
  public void An_element_moved_out_of_its_area_trades_the_area_name_for_numbers_on_the_same_line()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300]));

    // Assert
    Changed(Slide, patched).ShouldBe(["    frame: [52, 110, 400, 300]   # moved with the area"]);
  }

  [Fact]
  public void Values_round_to_hundredths_of_a_point()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.frame", "[490, 100, 430, 170]",
      [674.18000000000006, 12.5, 429.605, -0.001]));

    // Assert
    Changed(Slide, patched).ShouldBe(["    frame: [674.18, 12.5, 429.61, 0]"]);
  }

  [Fact]
  public void A_value_changed_since_the_page_drew_it_is_a_conflict()
  {
    // Arrange
    string edited = Slide.Replace("frame: left", "frame: [40, 100, 430, 380]");

    // Act
    PatchException exception = Should.Throw<PatchException>(() =>
      Apply(edited, new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300])));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void An_edit_elsewhere_in_the_file_leaves_the_patch_to_find_its_value()
  {
    // Arrange
    string edited = Slide.Replace("elements:\n",
      "elements:\n  - id: note\n    text: Added by an agent\n    frame: [0, 0, 10, 10]\n");

    // Act
    string patched = Apply(edited, new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300]));

    // Assert
    Changed(edited, patched).ShouldBe(["    frame: [52, 110, 400, 300]   # moved with the area"]);
  }

  [Fact]
  public void A_line_moves_both_endpoints_on_its_one_line()
  {
    // Act
    string patched = Apply(Slide,
      new ValueEdit("elements.arrow.line.from", "[100, 200]", [110, 210]),
      new ValueEdit("elements.arrow.line.to", "[300, 200]", [310, 210]));

    // Assert
    Changed(Slide, patched).ShouldBe(["    line: { from: [110, 210], to: [310, 210] }"]);
  }

  [Fact]
  public void Moving_the_title_writes_its_frame_beside_its_text()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("title.frame", null, [40, 30, 800, 44]));

    // Assert
    Changed(Slide, patched).ShouldBe([
      "title: { text: Connectivity revenue nearly tripled, frame: [40, 30, 800, 44] }"
    ]);
  }

  [Fact]
  public void A_title_that_only_reads_as_text_outside_braces_is_quoted_inside_them()
  {
    // Arrange
    string slide = "layout: content\ntitle: Revenue, up 33%\n";

    // Act
    string patched = Apply(slide, new ValueEdit("title.frame", null, [40, 30, 800, 44]));

    // Assert
    patched.ShouldBe("layout: content\ntitle: { text: \"Revenue, up 33%\", frame: [40, 30, 800, 44] }\n");
  }

  [Fact]
  public void A_title_written_as_a_map_gains_a_frame_key()
  {
    // Arrange
    string slide = "layout: content\ntitle:\n  text: Revenue\n  size: 30\n";

    // Act
    string patched = Apply(slide, new ValueEdit("title.frame", null, [40, 30, 800, 44]));

    // Assert
    patched.ShouldBe("layout: content\ntitle:\n  frame: [40, 30, 800, 44]\n  text: Revenue\n  size: 30\n");
  }

  [Fact]
  public void A_frame_the_page_thought_absent_but_an_agent_wrote_is_a_conflict()
  {
    // Arrange
    string slide = "layout: content\ntitle: { text: Revenue, frame: [0, 0, 10, 10] }\n";

    // Act
    PatchException exception = Should.Throw<PatchException>(() =>
      Apply(slide, new ValueEdit("title.frame", null, [40, 30, 800, 44])));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Rewording_a_paragraph_changes_its_line_alone_and_keeps_its_formatting_and_fields()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Sales of $11.4B in ￼", "Up 33% on 2024 alone"]));

    // Assert
    Changed(Slide, patched).ShouldBe([
      "      Sales of [$11.4B]{.positive} in ${date}",
      "      - Up **33%** on 2024 alone"
    ]);
  }

  [Fact]
  public void Rewording_a_paragraph_to_start_like_a_list_item_or_a_style_line_escapes_it()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string note = "elements:\n  - id: note\n    text: |\n      Plain words\n";
    string numbered = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["1. Revenue of $11.4B in \uFFFC", "Up 33% on 2024"]));
    string styled = Apply(note, new ValueEdit("elements.note.text", Patcher.SourceAt(note, "elements.note.text")!,
      Paragraphs: ["{.lead}"]));

    // Assert
    Changed(Slide, numbered).ShouldBe(["      1\\. Revenue of [$11.4B]{.positive} in ${date}"]);
    Changed(note, styled).ShouldBe(["      \\{.lead}"]);
  }

  [Fact]
  public void Edits_on_either_side_of_formatting_patch_each_stretch_on_its_own()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.4B in \uFFFC", "Now up 33% on 2023"]));

    // Assert
    Changed(Slide, patched).ShouldBe(["      - Now up **33%** on 2023"]);
  }

  [Fact]
  public void Typed_markup_characters_are_escaped_so_they_stay_text()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.4B in ￼", "Up 33% on 2024 [*] ${slide}"]));

    // Assert
    Changed(Slide, patched).ShouldBe([@"      - Up **33%** on 2024 \[\*\] \${slide}"]);
  }

  [Fact]
  public void Typing_over_a_field_writes_the_text_in_its_place()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;
    string kicker = Patcher.SourceAt(Slide, "placeholders.kicker")!;

    // Act
    string reworded = Apply(Slide,
      new ValueEdit("elements.takeaway.text", source, Paragraphs: ["Revenue of $11.4B in 2026", "Up 33% on 2024"]));
    string joined = Apply(Slide,
      new ValueEdit("elements.takeaway.text", source, Paragraphs: ["Revenue of $11.4B in 2026, up 33% on 2024"]));
    string plain = Apply(Slide, new ValueEdit("placeholders.kicker", kicker, Paragraphs: ["Board update, October"]));

    // Assert
    Changed(Slide, reworded).ShouldBe(["      Revenue of [$11.4B]{.positive} in 2026"]);
    joined.ShouldEndWith("\n      Revenue of [$11.4B]{.positive} in 2026, up **33%** on 2024");
    Changed(Slide, plain).ShouldBe(["  kicker: \"Board update, October\""]);
  }

  [Fact]
  public void An_edit_across_formatting_is_refused_rather_than_breaking_it()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    Should.Throw<PatchException>(() => Apply(Slide,
      new ValueEdit("elements.takeaway.text", source, Paragraphs: ["Revenue of $11.4B in ￼", "Up 3X2024"])));
  }

  [Fact]
  public void A_quoted_placeholder_keeps_its_quotes_and_its_field()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("placeholders.kicker", "\"Board update, ${date}\"",
      Paragraphs: ["Board meeting, ￼"]));

    // Assert
    Changed(Slide, patched).ShouldBe(["  kicker: \"Board meeting, ${date}\""]);
  }

  [Fact]
  public void Undoing_a_placeholder_reworded_to_nothing_writes_its_words_back()
  {
    // Arrange
    const string cover = "layout: cover\ntitle: Results\nplaceholders:\n  subtitle: How the year went\n";

    var cleared = new ValueEdit("placeholders.subtitle", "How the year went", Paragraphs: [""]);

    // Act
    string patched = Apply(cover, cleared);
    string undone = Undo(cover, cleared);

    // Assert
    Changed(cover, patched).ShouldBe(["  subtitle:"]);
    undone.ShouldBe(cover);
  }

  [Fact]
  public void A_line_break_in_the_title_is_written_as_an_escaped_newline_on_its_line()
  {
    // Arrange
    var broken = new ValueEdit("title", "Connectivity revenue nearly tripled",
      Paragraphs: ["Connectivity revenue\nnearly tripled"]);

    // Act
    string patched = Apply(Slide, broken);
    string undone = Undo(Slide, broken);

    // Assert
    Changed(Slide, patched).ShouldBe(["title: \"Connectivity revenue\\nnearly tripled\""]);
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Deleting_the_last_lines_words_drops_the_empty_paragraph_they_leave()
  {
    // Arrange
    const string bubble = "elements:\n  - id: bubble\n    text: |-\n      +${date}\n      YoY\n    style: lead\n";
    var cleared = new ValueEdit("elements.bubble.text", Patcher.SourceAt(bubble, "elements.bubble.text")!,
      Paragraphs: ["+￼", ""]);

    // Act
    string patched = Apply(bubble, cleared);
    string undone = Undo(bubble, cleared);

    // Assert
    patched.ShouldBe("elements:\n  - id: bubble\n    text: |-\n      +${date}\n    style: lead\n");
    undone.ShouldBe(bubble);
  }

  [Fact]
  public void Splitting_a_bullet_writes_a_bullet_at_its_level_after_it()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;
    var split = new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.4B in \uFFFC", "Up 33%", "on 2024"]);

    // Act
    string patched = Apply(Slide, split);
    string undone = Undo(Slide, split);

    // Assert
    patched.ShouldEndWith("      - Up **33%**\n      - on 2024");
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Splitting_a_styled_paragraph_styles_both_halves()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of ", "$11.4B in \uFFFC", "Up 33% on 2024"]));

    // Assert
    patched.ShouldEndWith(
      "      {.lead}\n      Revenue of \n      {.lead}\n      [$11.4B]{.positive} in ${date}\n      - Up **33%** on 2024");
  }

  [Fact]
  public void Splitting_inside_a_span_closes_it_before_the_break_and_opens_it_again_after()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;
    var split = new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.", "4B in ￼", "Up 33% on 2024"]);

    // Act
    string patched = Apply(Slide, split);
    string undone = Undo(Slide, split);

    // Assert
    patched.ShouldEndWith(
      "      {.lead}\n      Revenue of [$11.]{.positive}\n      {.lead}\n      [4B]{.positive} in ${date}\n" +
      "      - Up **33%** on 2024");
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Splitting_inside_bold_closes_it_before_the_break_and_opens_it_again_after()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.4B in ￼", "Up 3", "3% on 2024"]));

    // Assert
    patched.ShouldEndWith("      - Up **3**\n      - **3%** on 2024");
  }

  [Theory]
  [InlineData("**Bold**words", "Bold", "words", "**Bold**", "words")]
  [InlineData("[Lead]{.lead}words", "Lead", "words", "[Lead]{.lead}", "words")]
  [InlineData("***Both***words", "Both", "words", "***Both***", "words")]
  [InlineData("**Bold *both***words", "Bold both", "words", "**Bold *both***", "words")]
  [InlineData("[**Nested**]{.lead}words", "Nested", "words", "[**Nested**]{.lead}", "words")]
  [InlineData("[**Nested**, span]{.lead}", "Nested", ", span", "[**Nested**]{.lead}", "[, span]{.lead}")]
  public void Splitting_where_markup_ends_breaks_after_its_closer(string written, string before, string after,
    string first, string second)
  {
    // Arrange
    string note = $"elements:\n  - id: note\n    text: |\n      {written}\n    frame: [0, 0, 100, 50]\n";
    var split = new ValueEdit("elements.note.text", Patcher.SourceAt(note, "elements.note.text")!,
      Paragraphs: [before, after]);

    // Act
    string patched = Apply(note, split);

    // Assert
    patched.ShouldContain($"      {first}\n      {second}\n");
  }

  [Fact]
  public void Joining_two_paragraphs_writes_them_on_one_line()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Revenue of $11.4B in \uFFFC, up 33% on 2024"]));

    // Assert
    patched.ShouldEndWith("      {.lead}\n      Revenue of [$11.4B]{.positive} in ${date}, up **33%** on 2024");
  }

  [Fact]
  public void Splitting_text_written_on_one_line_writes_it_as_a_block()
  {
    // Arrange
    const string note = "elements:\n  - id: note\n    text: Plain words\n    frame: [0, 0, 100, 50]\n";

    // Act
    string patched = Apply(note, new ValueEdit("elements.note.text", "Plain words", Paragraphs: ["Plain", "words"]));

    // Assert
    patched.ShouldBe(
      "elements:\n  - id: note\n    text: |-\n      Plain\n      words\n    frame: [0, 0, 100, 50]\n");
  }

  [Fact]
  public void A_plain_title_that_needs_quotes_after_an_edit_gets_them()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("title", "Connectivity revenue nearly tripled",
      Paragraphs: ["Connectivity: revenue nearly tripled"]));

    // Assert
    Changed(Slide, patched).ShouldBe(["title: \"Connectivity: revenue nearly tripled\""]);
  }

  [Fact]
  public void Undoing_a_move_out_of_an_area_writes_the_area_name_back()
  {
    // Act
    string undone = Undo(Slide, new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300]));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Undoing_a_title_move_turns_it_back_into_the_shorthand()
  {
    // Act
    string undone = Undo(Slide, new ValueEdit("title.frame", null, [40, 30, 800, 44]));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Undoing_a_frame_added_to_a_title_map_removes_it()
  {
    // Arrange
    string slide = "layout: content\ntitle:\n  text: Revenue\n  size: 30\n";

    // Act
    string undone = Undo(slide, new ValueEdit("title.frame", null, [40, 30, 800, 44]));

    // Assert
    undone.ShouldBe(slide);
  }

  [Fact]
  public void Undoing_a_line_move_puts_both_endpoints_back()
  {
    // Act
    string undone = Undo(Slide,
      new ValueEdit("elements.arrow.line.from", "[100, 200]", [110, 210]),
      new ValueEdit("elements.arrow.line.to", "[300, 200]", [310, 210]));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Undoing_a_reword_restores_the_markup_as_written()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.takeaway.text")!;

    // Act
    string undone = Undo(Slide, new ValueEdit("elements.takeaway.text", source,
      Paragraphs: ["Sales of $11.4B in ￼", "Up 33% on 2024 alone"]));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Undoing_an_undo_writes_the_edit_again()
  {
    // Arrange
    Patched moved = Patcher.Apply(Slide, [new ValueEdit("title.frame", null, [40, 30, 800, 44])], Context);
    Patched undone = Patcher.Apply(moved.Text, moved.Undo, Context);

    // Act
    Patched redone = Patcher.Apply(undone.Text, undone.Undo, Context);

    // Assert
    redone.Text.ShouldBe(moved.Text);
    Patcher.Apply(redone.Text, redone.Undo, Context).Text.ShouldBe(Slide);
  }

  [Fact]
  public void An_undo_after_an_agent_changed_the_value_is_a_conflict()
  {
    // Arrange
    Patched moved = Patcher.Apply(Slide, [new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300])],
      Context);
    string edited = moved.Text.Replace("[52, 110, 400, 300]", "right");

    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Apply(edited, moved.Undo, Context));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void An_undo_keeps_an_agents_edit_elsewhere_in_the_file()
  {
    // Arrange
    Patched moved = Patcher.Apply(Slide, [new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300])],
      Context);
    string edited = moved.Text.Replace("end: arrow", "end: none");

    // Act
    string undone = Patcher.Apply(edited, moved.Undo, Context).Text;

    // Assert
    undone.ShouldBe(Slide.Replace("end: arrow", "end: none"));
  }

  [Fact]
  public void Deleting_an_element_removes_its_lines_alone()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.arrow")!;

    // Act
    string patched = Apply(Slide, new ValueEdit("elements.arrow", source, Delete: true));

    // Assert
    patched.ShouldBe(Slide.Replace("""
        - id: arrow
          line: { from: [100, 200], to: [300, 200] }
          end: arrow

      """, ""));
  }

  [Theory]
  [InlineData("chart")]
  [InlineData("arrow")]
  [InlineData("takeaway")]
  public void Undoing_a_delete_writes_the_element_back_where_it_was(string id)
  {
    // Arrange
    string path = $"elements.{id}";

    // Act
    string undone = Undo(Slide, new ValueEdit(path, Patcher.SourceAt(Slide, path), Delete: true));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Theory]
  [InlineData("one")]
  [InlineData("two")]
  [InlineData("three")]
  public void Undoing_a_delete_between_blank_lines_and_text_blocks_keeps_them_as_they_were(string id)
  {
    // Arrange
    const string spaced = """
      elements:
        - id: one
          text: One

        - id: two
          text: |
            Two

        - id: three
          text: Three
      """;
    string path = $"elements.{id}";

    // Act
    Patched deleted = Patcher.Apply(spaced, [new ValueEdit(path, Patcher.SourceAt(spaced, path), Delete: true)],
      Context);
    string undone = Patcher.Apply(deleted.Text, deleted.Undo, Context).Text;

    // Assert
    deleted.Text.ShouldNotContain("\n\n\n");
    undone.ShouldBe(spaced);
  }

  [Fact]
  public void Undoing_a_delete_puts_the_element_back_after_the_title_it_followed()
  {
    // Arrange
    const string stacked = """
      title: Hall
      elements:
        - id: photo
          picture: hall.png
          frame: [0, 0, 960, 540]
        - title
        - id: note
          text: On top
      """;
    ValueEdit delete = new ValueEdit("elements.note", Patcher.SourceAt(stacked, "elements.note"), Delete: true);

    // Act
    Patched deleted = Patcher.Apply(stacked, [delete], Context);
    string undone = Patcher.Apply(deleted.Text, deleted.Undo, Context).Text;

    // Assert
    undone.ShouldBe(stacked);
  }

  [Fact]
  public void Deleting_the_only_element_leaves_an_empty_list_and_undoing_it_restores_the_element()
  {
    // Arrange
    const string single = """
      title: Alone
      elements:
        - id: note
          text: The only one
      """;
    ValueEdit delete = new ValueEdit("elements.note", Patcher.SourceAt(single, "elements.note"), Delete: true);

    // Act
    Patched deleted = Patcher.Apply(single, [delete], Context);
    string undone = Patcher.Apply(deleted.Text, deleted.Undo, Context).Text;

    // Assert
    deleted.Text.ShouldBe("title: Alone\nelements: []");
    undone.ShouldBe(single);
  }

  [Fact]
  public void Deleting_an_element_an_agent_changed_since_is_a_conflict()
  {
    // Arrange
    string source = Patcher.SourceAt(Slide, "elements.arrow")!;
    string edited = Slide.Replace("end: arrow", "end: none");

    // Act
    PatchException exception = Should.Throw<PatchException>(() =>
      Apply(edited, new ValueEdit("elements.arrow", source, Delete: true)));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Undoing_a_delete_keeps_an_agents_edit_to_the_element_beside_it()
  {
    // Arrange
    Patched deleted = Patcher.Apply(Slide,
      [new ValueEdit("elements.arrow", Patcher.SourceAt(Slide, "elements.arrow"), Delete: true)], Context);
    string edited = deleted.Text.Replace("frame: left", "frame: right");

    // Act
    string undone = Patcher.Apply(edited, deleted.Undo, Context).Text;

    // Assert
    undone.ShouldBe(Slide.Replace("frame: left", "frame: right"));
  }

  [Fact]
  public void Undoing_a_delete_after_an_agent_removed_the_element_before_it_is_a_conflict()
  {
    // Arrange
    Patched deleted = Patcher.Apply(Slide,
      [new ValueEdit("elements.arrow", Patcher.SourceAt(Slide, "elements.arrow"), Delete: true)], Context);
    string edited = Patcher.Apply(deleted.Text,
      [new ValueEdit("elements.chart", Patcher.SourceAt(deleted.Text, "elements.chart"), Delete: true)], Context).Text;

    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Apply(edited, deleted.Undo, Context));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Redoing_a_delete_removes_the_element_again()
  {
    // Arrange
    Patched deleted = Patcher.Apply(Slide,
      [new ValueEdit("elements.takeaway", Patcher.SourceAt(Slide, "elements.takeaway"), Delete: true)], Context);
    Patched undone = Patcher.Apply(deleted.Text, deleted.Undo, Context);

    // Act
    string redone = Patcher.Apply(undone.Text, undone.Undo, Context).Text;

    // Assert
    redone.ShouldBe(deleted.Text);
  }

  #region Formatting text

  private static ValueEdit Format(string mark, bool on, int paragraph, int start, int end, string slide = Slide,
    List<string>? paragraphs = null)
  {
    return new ValueEdit("elements.takeaway.text", Patcher.SourceAt(slide, "elements.takeaway.text"),
      Paragraphs: paragraphs ?? ["Revenue of $11.4B in ￼", "Up 33% on 2024"],
      Format: new TextFormat(mark, on, [new FormatRange(paragraph, start, end)]));
  }

  [Fact]
  public void Bolding_a_word_wraps_it_in_its_line_alone()
  {
    // Act
    string patched = Apply(Slide, Format("bold", true, 0, 0, 7));

    // Assert
    Changed(Slide, patched).ShouldBe(["      **Revenue** of [$11.4B]{.positive} in ${date}"]);
  }

  [Fact]
  public void Spaces_at_the_ends_of_a_selection_stay_outside_the_formatting()
  {
    // Act
    string patched = Apply(Slide, Format("italic", true, 0, 7, 11));

    // Assert
    Changed(Slide, patched).ShouldBe(["      Revenue *of* [$11.4B]{.positive} in ${date}"]);
  }

  [Fact]
  public void Underlining_writes_a_span_and_formatting_inside_a_span_nests_in_it()
  {
    // Act
    string underlined = Apply(Slide, Format("underline", true, 0, 18, 20));
    string bolded = Apply(Slide, Format("bold", true, 0, 11, 17));

    // Assert
    Changed(Slide, underlined).ShouldBe(["      Revenue of [$11.4B]{.positive} [in]{underline=true} ${date}"]);
    Changed(Slide, bolded).ShouldBe(["      Revenue of [**$11.4B**]{.positive} in ${date}"]);
  }

  [Fact]
  public void Unbolding_all_of_a_bold_run_drops_its_markers()
  {
    // Act
    string patched = Apply(Slide, Format("bold", false, 1, 3, 6));

    // Assert
    Changed(Slide, patched).ShouldBe(["      - Up 33% on 2024"]);
  }

  [Fact]
  public void Unbolding_the_middle_of_a_bold_run_splits_it_around_the_selection()
  {
    // Arrange
    string slide = Slide.Replace("- Up **33%** on 2024", "- **Up 33% on 2024**");

    // Act
    string patched = Apply(slide, Format("bold", false, 1, 3, 6, slide));

    // Assert
    Changed(slide, patched).ShouldBe(["      - **Up** 33% **on 2024**"]);
  }

  [Fact]
  public void Unmarking_text_inside_a_span_keeps_the_span_whole()
  {
    // Arrange
    string slide = Slide.Replace("- Up **33%** on 2024", "- Up *[33%]{underline=true}* on 2024");

    // Act
    string patched = Apply(slide, Format("italic", false, 1, 3, 6, slide));

    // Assert
    Changed(slide, patched).ShouldBe(["      - Up [33%]{underline=true} on 2024"]);
  }

  [Fact]
  public void Text_typed_before_formatting_is_written_with_it()
  {
    // Act
    string patched = Apply(Slide, Format("bold", true, 0, 0, 5,
      paragraphs: ["Sales of $11.4B in ￼", "Up 33% on 2024"]));

    // Assert
    Changed(Slide, patched).ShouldBe(["      **Sales** of [$11.4B]{.positive} in ${date}"]);
  }

  [Fact]
  public void Undoing_formatting_restores_the_markup_as_written()
  {
    // Act
    string undone = Undo(Slide, Format("bold", true, 0, 0, 7));

    // Assert
    undone.ShouldBe(Slide);
  }

  [Fact]
  public void Unbolding_text_its_style_makes_bold_is_refused_with_a_style_that_would_turn_it_off()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Apply(Slide, Format("bold", false, 0, 0, 7)));

    // Assert
    exception.Message.ShouldContain("regular: { bold: false }");
  }

  [Fact]
  public void The_title_takes_no_formatting()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Apply(Slide, new ValueEdit("title",
      "Connectivity revenue nearly tripled", Paragraphs: ["Connectivity revenue nearly tripled"],
      Format: new TextFormat("bold", true, [new FormatRange(0, 0, 12)]))));

    // Assert
    exception.Message.ShouldContain("layout");
  }

  #endregion

  #region Reordering slides

  private const string Deck = """
    name: Board update
    theme: ../../themes/acme

    slides:
      - cover
      - section: Financial performance
      - revenue   # the chart slide
      - segments
      - section: Capital allocation
      - capital
    """;

  [Fact]
  public void Moving_a_slide_moves_its_line_and_nothing_else()
  {
    // Act
    (string moved, _) = Patcher.Move(Deck, new SlideMove("segments", new ListEntry("revenue"),
      new ListEntry(Section: "Financial performance")));

    // Assert
    moved.ShouldBe(Deck.Replace("  - revenue   # the chart slide\n  - segments\n",
      "  - segments\n  - revenue   # the chart slide\n"));
  }

  [Fact]
  public void A_slide_moved_after_a_section_joins_it_and_one_moved_first_leads_the_list()
  {
    // Act
    (string joined, _) = Patcher.Move(Deck, new SlideMove("cover", null, new ListEntry(Section: "Capital allocation")));
    (string first, _) = Patcher.Move(Deck, new SlideMove("capital", new ListEntry(Section: "Capital allocation"),
      null));

    // Assert
    joined.ShouldBe(Deck.Replace("  - cover\n", "").Replace("allocation\n", "allocation\n  - cover\n"));
    first.ShouldBe(Deck.Replace("\n  - capital", "").Replace("slides:\n", "slides:\n  - capital\n"));
  }

  [Fact]
  public void Undoing_a_move_puts_the_line_back_where_it_was()
  {
    // Arrange
    (string moved, SlideMove? undo) = Patcher.Move(Deck, new SlideMove("revenue",
      new ListEntry(Section: "Financial performance"), new ListEntry("capital")));

    // Act
    (string undone, SlideMove? redo) = Patcher.Move(moved, undo.ShouldNotBeNull());

    // Assert
    undone.ShouldBe(Deck);
    Patcher.Move(undone, redo.ShouldNotBeNull()).Text.ShouldBe(moved);
  }

  [Fact]
  public void The_last_line_moves_even_without_a_line_break_after_it()
  {
    // Arrange
    string deck = Deck.TrimEnd('\n').Replace("\n", "\r\n");

    // Act
    (string moved, _) = Patcher.Move(deck, new SlideMove("capital", new ListEntry(Section: "Capital allocation"),
      new ListEntry("cover")));

    // Assert
    moved.ShouldBe(deck.Replace("\r\n  - capital", "").Replace("  - cover\r\n", "  - cover\r\n  - capital\r\n"));
  }

  [Fact]
  public void Dropping_a_slide_where_it_already_is_writes_nothing()
  {
    // Act
    (string moved, SlideMove? undo) = Patcher.Move(Deck, new SlideMove("segments", new ListEntry("revenue"),
      new ListEntry("revenue")));

    // Assert
    moved.ShouldBe(Deck);
    undo.ShouldBeNull();
  }

  [Fact]
  public void Moving_a_slide_an_agent_moved_since_is_a_conflict()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(Deck,
      new SlideMove("segments", new ListEntry("cover"), null)));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Moving_a_slide_after_one_an_agent_removed_is_a_conflict()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(Deck,
      new SlideMove("cover", null, new ListEntry("margins"))));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Moving_a_slide_after_a_section_whose_name_two_share_is_refused()
  {
    // Arrange
    string deck = Deck.Replace("Capital allocation", "Financial performance");

    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(deck,
      new SlideMove("cover", null, new ListEntry(Section: "Financial performance"))));

    // Assert
    exception.Conflict.ShouldBeFalse();
  }

  [Fact]
  public void Slides_written_as_a_flow_list_are_refused()
  {
    // Arrange
    string deck = "name: Board update\nslides: [cover, revenue]\n";

    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(deck,
      new SlideMove("revenue", new ListEntry("cover"), null)));

    // Assert
    exception.Conflict.ShouldBeFalse();
  }

  [Fact]
  public void Deleting_a_slide_removes_its_line_and_answers_with_a_restore_after_the_entry_before_it()
  {
    // Act
    (string deleted, SlideMove? undo) = Patcher.Move(Deck, new SlideMove("revenue",
      new ListEntry(Section: "Financial performance"), null, Delete: true));

    // Assert
    deleted.ShouldBe(Deck.Replace("  - revenue   # the chart slide\n", ""));
    undo.ShouldBe(new SlideMove("revenue", null, new ListEntry(Section: "Financial performance"), Restore: ""));
  }

  [Fact]
  public void Restoring_a_slide_puts_its_line_back_and_answers_with_the_delete()
  {
    // Arrange
    string deleted = Deck.Replace("  - capital", "  - capital\n").Replace("  - segments\n", "");

    // Act
    (string restored, SlideMove? undo) = Patcher.Move(deleted, new SlideMove("segments", null,
      new ListEntry("revenue"), Restore: "title: Segments\n"));

    // Assert
    restored.ShouldBe(Deck.Replace("  - capital", "  - capital\n"));
    undo.ShouldBe(new SlideMove("segments", new ListEntry("revenue"), null, Delete: true));
  }

  [Fact]
  public void Restoring_a_slide_whose_name_yaml_would_misread_writes_it_quoted()
  {
    // Arrange
    string deleted = Deck.Replace("  - segments\n", "");

    // Act
    (string restored, _) = Patcher.Move(deleted, new SlideMove("#draft", null, new ListEntry("revenue"),
      Restore: "title: Draft\n"));

    // Assert
    restored.ShouldContain("  - \"#draft\"\n");
    Patcher.Move(restored, new SlideMove("#draft", new ListEntry("revenue"), null, Delete: true)).Text
      .ShouldBe(deleted);
  }

  [Fact]
  public void Restoring_the_last_slide_in_a_file_without_a_final_break_leads_or_ends_the_list()
  {
    // Arrange
    string deck = Deck.TrimEnd('\n').Replace("\n  - capital", "");

    // Act
    (string last, _) = Patcher.Move(deck, new SlideMove("capital", null,
      new ListEntry(Section: "Capital allocation"), Restore: ""));
    (string first, _) = Patcher.Move(deck, new SlideMove("capital", null, null, Restore: ""));

    // Assert
    last.ShouldBe(deck + "\n  - capital");
    first.ShouldBe(deck.Replace("slides:\n", "slides:\n  - capital\n"));
  }

  [Fact]
  public void Deleting_the_only_slide_is_refused()
  {
    // Arrange
    string deck = "name: Board update\nslides:\n  - section: Opening\n  - cover\n";

    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(deck,
      new SlideMove("cover", new ListEntry(Section: "Opening"), null, Delete: true)));

    // Assert
    exception.Conflict.ShouldBeFalse();
  }

  [Fact]
  public void Deleting_a_slide_an_agent_moved_since_is_a_conflict()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(Deck,
      new SlideMove("segments", new ListEntry("cover"), null, Delete: true)));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  [Fact]
  public void Restoring_a_slide_that_is_listed_already_is_a_conflict()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() => Patcher.Move(Deck,
      new SlideMove("segments", null, new ListEntry("cover"), Restore: "")));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  #endregion

  [Fact]
  public void Line_endings_and_a_byte_order_mark_survive_a_patch()
  {
    // Arrange
    using var folder = new TempFolder();
    string path = folder.PathTo("slide.yaml");
    File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Slide.Replace("\n", "\r\n"))]);

    // Act
    Patcher.ApplyToFile(path, [new ValueEdit("elements.chart.frame", "left", [52, 110, 400, 300])], Context);

    // Assert
    byte[] bytes = File.ReadAllBytes(path);
    bytes[..3].ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
    string text = System.Text.Encoding.UTF8.GetString(bytes[3..]);
    text.ShouldBe(Slide.Replace("frame: left", "frame: [52, 110, 400, 300]").Replace("\n", "\r\n"));
  }


  #region Reverting

  private static string Revert(string text, string original, string? path)
  {
    return Apply(text, [.. Patcher.Revert(text, original, path)]);
  }

  [Fact]
  public void Reverting_a_changed_element_writes_it_as_the_revision_did_and_keeps_changes_elsewhere()
  {
    // Arrange
    string edited = Slide.Replace("frame: [490, 100, 430, 170]", "frame: [500, 120, 400, 170]")
      .Replace("- Up **33%** on 2024", "- Up **40%** on 2024")
      .Replace("frame: left", "frame: [40, 100, 430, 380]");

    // Act
    string reverted = Revert(edited, Slide, "elements.takeaway");

    // Assert
    reverted.ShouldBe(Slide.Replace("frame: left", "frame: [40, 100, 430, 380]"));
  }

  [Fact]
  public void Reverting_an_element_added_since_deletes_it()
  {
    // Arrange
    string original = $"{Slide}\n";
    string edited = $"{original}  - id: note\n    text: A note\n    frame: [40, 400, 300, 40]\n";

    // Act
    string reverted = Revert(edited, original, "elements.note");

    // Assert
    reverted.ShouldBe(original);
  }

  [Fact]
  public void Reverting_an_element_removed_since_puts_it_back_after_the_one_it_followed()
  {
    // Arrange
    string edited = Apply(Slide, new ValueEdit("elements.arrow", Patcher.SourceAt(Slide, "elements.arrow"),
      Delete: true));

    // Act
    string reverted = Revert(edited, Slide, "elements.arrow");

    // Assert
    reverted.ShouldBe(Slide);
  }

  [Fact]
  public void Reverting_an_element_removed_from_a_list_now_empty_fills_it_again()
  {
    // Arrange
    const string original = "layout: content\nelements:\n  - id: note\n    text: A note\n";
    const string edited = "layout: content\nelements: []\n";

    // Act
    string reverted = Revert(edited, original, "elements.note");

    // Assert
    reverted.ShouldBe(original);
  }

  [Fact]
  public void Reverting_the_title_restores_its_source()
  {
    // Arrange
    string edited = Slide.Replace("title: Connectivity revenue nearly tripled", "title: Revenue tripled");

    // Act
    string reverted = Revert(edited, Slide, "title");

    // Assert
    reverted.ShouldBe(Slide);
  }

  [Fact]
  public void An_element_unchanged_since_has_nothing_to_revert()
  {
    // Arrange
    string edited = Slide.Replace("frame: left", "frame: [40, 100, 430, 380]");

    // Act
    List<ValueEdit> edits = Patcher.Revert(edited, Slide, "elements.takeaway");

    // Assert
    edits.ShouldBeEmpty();
  }

  [Fact]
  public void Reverting_a_whole_slide_writes_the_revision_and_its_undo_writes_the_edits_back()
  {
    // Arrange
    string edited = Slide.Replace("layout: content", "layout: two-column").Replace("frame: left", "frame: right");

    // Act
    Patched reverted = Patcher.Apply(edited, Patcher.Revert(edited, Slide, null), Context);
    string undone = Patcher.Apply(reverted.Text, reverted.Undo, Context).Text;

    // Assert
    reverted.Text.ShouldBe(Slide);
    undone.ShouldBe(edited);
  }

  [Fact]
  public void A_whole_slide_changed_since_the_revert_was_worked_out_is_a_conflict()
  {
    // Arrange
    string edited = Slide.Replace("layout: content", "layout: two-column");
    List<ValueEdit> edits = Patcher.Revert(edited, Slide, null);

    // Act
    PatchException exception = Should.Throw<PatchException>(() =>
      Patcher.Apply(edited.Replace("frame: left", "frame: right"), edits, Context));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  #endregion

  #region Fill and outline

  private const string Shapes = """
    elements:
      - id: step
        shape: chevron
        frame: [40, 200, 180, 60]
        fill: accent1
        stroke: none
        text: Launch
      - id: note
        shape: rectangle
        frame: [320, 120, 200, 56]
        stroke: { color: accent3, width: 1.5 }
        text: |
          Twelve launches
          in the quarter
      - id: link
        line: { from: [230, 230], to: [300, 230] }
    """;

  [Fact]
  public void A_new_fill_replaces_the_old_on_its_line()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.step.fill", "accent1", Value: "accent2 lighter 40"));

    // Assert
    Changed(Shapes, patched).ShouldBe(["    fill: accent2 lighter 40"]);
  }

  [Fact]
  public void A_hex_fill_is_quoted_so_it_isnt_read_as_a_comment()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.step.fill", "accent1", Value: "#C0392B"));

    // Assert
    Changed(Shapes, patched).ShouldBe(["    fill: \"#C0392B\""]);
  }

  [Fact]
  public void A_fill_the_element_lacks_goes_on_the_line_after_its_frame()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.note.fill", null, Value: "light2"));

    // Assert
    patched.ShouldBe(Shapes.Replace("[320, 120, 200, 56]\n", "[320, 120, 200, 56]\n    fill: light2\n"));
  }

  [Fact]
  public void A_width_goes_into_the_stroke_map_beside_its_color()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.note.stroke.width", "1.5", Value: "3"));

    // Assert
    Changed(Shapes, patched).ShouldBe(["    stroke: { color: accent3, width: 3 }"]);
  }

  [Fact]
  public void An_outline_on_a_shape_with_none_replaces_the_none()
  {
    // Act
    string patched = Apply(Shapes,
      new ValueEdit("elements.step.stroke.color", null, Value: "dark1"),
      new ValueEdit("elements.step.stroke.width", null, Value: "1"));

    // Assert
    Changed(Shapes, patched).ShouldBe(["    stroke: { color: dark1, width: 1 }"]);
  }

  [Fact]
  public void An_outline_on_an_element_without_a_stroke_adds_one()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.link.stroke.width", null, Value: "2.25"));

    // Assert
    patched.ShouldBe(Shapes + "\n    stroke: { width: 2.25 }");
  }

  [Fact]
  public void No_outline_replaces_the_whole_stroke()
  {
    // Act
    string patched = Apply(Shapes, new ValueEdit("elements.note.stroke", "{ color: accent3, width: 1.5 }",
      Value: "none"));

    // Assert
    Changed(Shapes, patched).ShouldBe(["    stroke: none"]);
  }

  [Fact]
  public void Undoing_an_added_outline_takes_it_out_again()
  {
    // Act
    string undone = Undo(Shapes,
      new ValueEdit("elements.step.stroke.color", null, Value: "dark1"),
      new ValueEdit("elements.step.stroke.width", null, Value: "1"),
      new ValueEdit("elements.note.fill", null, Value: "light2"));

    // Assert
    undone.ShouldBe(Shapes);
  }

  [Fact]
  public void A_fill_an_agent_changed_since_is_a_conflict()
  {
    // Act
    PatchException exception = Should.Throw<PatchException>(() =>
      Apply(Shapes, new ValueEdit("elements.step.fill", "accent4", Value: "accent2")));

    // Assert
    exception.Conflict.ShouldBeTrue();
  }

  #endregion

  #region Alignment and style

  [Fact]
  public void An_alignment_the_box_lacks_goes_on_the_line_after_its_frame()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.align", null, Value: "center"));

    // Assert
    patched.ShouldBe(Slide.Replace("[490, 100, 430, 170]\n", "[490, 100, 430, 170]\n    align: center\n"));
  }

  [Fact]
  public void A_style_the_box_lacks_goes_on_the_line_after_its_frame()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("elements.takeaway.style", null, Value: "lead"));

    // Assert
    patched.ShouldBe(Slide.Replace("[490, 100, 430, 170]\n", "[490, 100, 430, 170]\n    style: lead\n"));
  }

  [Fact]
  public void An_alignment_on_a_one_line_title_makes_it_the_map_it_stands_for()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("title.align", null, Value: "center"));

    // Assert
    Changed(Slide, patched).ShouldBe(["title: { text: Connectivity revenue nearly tripled, align: center }"]);
  }

  [Fact]
  public void An_alignment_on_a_quoted_placeholder_keeps_its_quotes()
  {
    // Act
    string patched = Apply(Slide, new ValueEdit("placeholders.kicker.align", null, Value: "right"));

    // Assert
    Changed(Slide, patched).ShouldBe(["  kicker: { text: \"Board update, ${date}\", align: right }"]);
  }

  [Fact]
  public void Undoing_an_alignment_on_a_title_puts_the_shorthand_back()
  {
    // Act
    string undone = Undo(Slide, new ValueEdit("title.align", null, Value: "center"));

    // Assert
    undone.ShouldBe(Slide);
  }

  #endregion

}