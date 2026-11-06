using System.Text.Json;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class OutlineTests
{
  private static string Outline(DeckFixture deck, bool json = false, SlideSelection? only = null)
  {
    var output = new StringWriter();
    HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Outline, null, null, json, output, only: only);
    return output.ToString().ReplaceLineEndings("\n");
  }

  [Fact]
  public void Outline_prints_each_slides_words_in_reading_order_without_the_layouts()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results in ${section}
      elements:
        - id: source
          text: "Source: Acme S-1/A"
          style: caption
          frame: [40, 484, 430, 14]
        - id: right
          text: Space grew 8%
          frame: [490, 100, 430, 60]
        - id: left
          text: |
            {.lead}
            2025 revenue by segment
            - Connectivity: [$11.4B]{.emphasis}
              - Up from $4.1B
          frame: [40, 110, 430, 300]
        - id: watermark
          text: Draft
          frame: [0, 0, 960, 540]
          decorative: true
        - id: rule
          line: { from: [40, 90], to: [920, 90] }
      """);

    // Act
    string outline = Outline(deck);

    // Assert
    outline.ShouldBe("""
       1 one  Hello
           subtitle: Confidential
         Results
       2 two  Results in Results
           left
             2025 revenue by segment
             - Connectivity: $11.4B
               - Up from $4.1B
           right: Space grew 8%
           source: Source: Acme S-1/A

      """);
  }

  [Fact]
  public void Outline_spells_a_charts_values_as_its_labels_do_and_a_tables_cells_by_row_and_column()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv\ntitle: Revenue\ndata_labels: { show: value, format: " +
      "billions }");
    deck.Folder.Write("deck/tables/results.yaml", "data: results.csv\nmerge:\n  - { row: 2, column: 1, rows: 2 }");
    deck.Folder.Write("deck/tables/results.csv", "measure,segment,2025\nRevenue,Space,4.1\n,AI,3.2");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      title: Space leads
      elements:
        - id: chart
          chart: revenue
          frame: left
        - id: results
          table: results
          frame: [490, 100, 430, 200]
      """);

    // Act
    string outline = Outline(deck);

    // Assert
    outline.ShouldBe("""
       1 chart  Space leads
           chart: chart revenue, clustered_column, titled Revenue
             categories: 2024 | 2025
             Space: $3.8B | $4.1B
             AI: $2.6B | ($3.2B)
           results: table results
             measure | segment | 2025
             Revenue | Space | 4.1
              | AI | 3.2

      """);
  }

  [Fact]
  public void Outline_spells_a_series_on_the_secondary_axis_in_its_own_format()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "src", "Pintura.Tests", "fixtures", "charts");
    var output = new StringWriter();

    // Act
    HeadlessRunner.Run(root, HeadlessCommand.Outline, null, null, false, output,
      only: SlideSelection.Parse("combinations"));

    // Assert
    output.ToString().ReplaceLineEndings("\n").ShouldContain("       Adjusted EBITDA margin: 37% | 38% | 35%\n");
  }

  [Fact]
  public void Outline_shows_a_picks_variants_with_their_intents_and_marks_a_rejected_slide()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/picks.yaml",
      "- id: p4kx\n  variants:\n    - { slide: two, intent: Two charts }\n    - { slide: stacked, intent: One }");
    deck.Write("deck/picks-resolved.yaml", """
      - id: q7rt
        variants:
          - { slide: one, intent: A }
          - { slide: gone, intent: B }
        resolved: { by: Daniel, chosen: none, why: Too busy }
      """);
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nfields:\n  footer: x\nslides:\n" +
      "  - one\n  - pick: p4kx");
    deck.Write("deck/slides/one.yaml", "rejected: q7rt\nlayout: cover\ntitle: Hello");
    deck.Write("deck/slides/stacked.yaml", "layout: content\ntitle: Stacked");

    // Act
    string outline = Outline(deck);

    // Assert
    outline.ShouldBe("""
       1 one  Hello
           rejected in pick q7rt: Too busy
         pick p4kx, waiting on the reviewer to keep one of:
       2 two  Results in
           intent: Two charts
       2 stacked  Stacked
           intent: One

      """);
  }

  [Fact]
  public void Outline_takes_the_slides_it_names_and_answers_in_json()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    string outline = Outline(deck, true, SlideSelection.Parse("one"));

    // Assert
    JsonElement slide = JsonDocument.Parse(outline).RootElement.EnumerateArray().ShouldHaveSingleItem();
    slide.GetProperty("id").GetString().ShouldBe("one");
    slide.GetProperty("title").GetString().ShouldBe("Hello");
    JsonElement subtitle = slide.GetProperty("elements").EnumerateArray().ShouldHaveSingleItem();
    (subtitle.GetProperty("id").GetString(), subtitle.GetProperty("kind").GetString()).ShouldBe(("subtitle",
      "placeholder"));
    subtitle.GetProperty("paragraphs")[0].GetProperty("text").GetString().ShouldBe("Confidential");
  }

  [Fact]
  public void Outline_exits_nonzero_and_says_why_when_a_slide_fails()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\ncolour: red");
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Outline, null, null, false, output);

    // Assert
    exit.ShouldBe(1);
    output.ToString().ShouldContain(" 2 two  error: unknown key colour in ");
  }

  [Theory]
  [InlineData(new[] { "deck", "--outline", "--slides", "2-3", "--json" }, null)]
  [InlineData(new[] { "deck", "--outline", "--list" }, "Use only one of")]
  [InlineData(new[] { "deck", "--outline", "--diff" }, "--diff combines only with --check and --screenshot.")]
  public void Outline_takes_slides_and_json(string[] arguments, string? problem)
  {
    // Act
    Func<CommandLineOptions> parse = () => CommandLineOptions.Parse(arguments);

    // Assert
    if (problem is null)
    {
      CommandLineOptions options = parse();
      (options.Command, options.Json, options.Slides is not null).ShouldBe((HeadlessCommand.Outline, true, true));
    }
    else
    {
      parse.ShouldThrow<ArgumentException>().Message.ShouldStartWith(problem);
    }
  }


}