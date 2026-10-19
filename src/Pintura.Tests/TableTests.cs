using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class TableTests
{
  private static SceneCell Cell(SceneItem item, int row, int column)
  {
    return item.Table!.Cells.Single(cell => cell.Row == row && cell.Column == column);
  }

  private static string Text(SceneCell cell)
  {
    return string.Join("\n",
      cell.Paragraphs.Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Text))));
  }

  [Fact]
  public void A_table_reads_every_csv_row_as_a_row_header_included()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      header_rows: 1
      """, "segment,2024,2025\n\"Space, launch\",3796,4086\nAI,2620,-3201");

    // Act
    Table table = deck.Load().Tables["results"];

    // Assert
    table.Values.Count.ShouldBe(3);
    table.Values[0].ShouldBe(["segment", "2024", "2025"]);
    table.Values[1][0].ShouldBe("Space, launch");
  }

  [Fact]
  public void Columns_without_a_width_share_what_the_others_leave_of_the_frame()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      columns:
        - { width: 280 }
      """);

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    item.Table!.Widths.Select(width => width.Value).ShouldBe([280.0, 300.0, 300.0]);
    item.Table.Widths[0].Origin!.ToString().ShouldBe("deck/tables/results.yaml:3 columns[1].width");
    item.Table.Widths[1].Origin.ShouldBeNull();
  }

  [Fact]
  public void Formatting_layers_from_the_table_style_through_columns_rows_and_cells()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      columns:
        - { color: muted }
        - { align: right }
        - { align: right }
      rows:
        4: { style: total, italic: true }
      cells:
        - { row: 1, column: 2, align: center, style: emphasis }
      """);

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    SceneCell header = Cell(item, 1, 2);
    Cell(item, 1, 3).Properties["bold"].Origin!.ToString().ShouldBe("theme/styles.yaml:45 tables.plain.header.bold");
    header.Properties["color"].Value.ShouldBe(new Color { Theme = "accent1" });
    header.Properties["align"].Value.ShouldBe("center");
    header.Properties["fill"].Value.ShouldBe(new Color { Theme = "light2" });
    Cell(item, 1, 3).Properties["align"].Value.ShouldBe("right");
    Cell(item, 2, 1).Properties["color"].Value.ShouldBe(new Color { Named = "muted" });
    SceneCell total = Cell(item, 4, 1);
    total.Properties["row_style"].Value.ShouldBe("total");
    total.Properties["italic"].Origin!.File.ShouldBe("deck/tables/results.yaml");
    total.Properties["border_bottom.width"].Value.ShouldBe(2.0);
    total.Properties.ShouldNotContainKey("fill");
  }

  [Fact]
  public void A_row_style_outranks_a_column_so_a_header_keeps_its_look_over_a_column_styled_for_its_values()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      columns:
        - { bold: false }
        - { style: caption, align: right }
      rows:
        4: { style: total }
      """);

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    Cell(item, 1, 1).Properties["bold"].Value.ShouldBe(true);
    Cell(item, 1, 2).Properties["align"].Value.ShouldBe("right");
    Cell(item, 2, 1).Properties["bold"].Value.ShouldBe(false);
    Cell(item, 2, 2).Properties["color"].Value.ShouldBe(new Color { Named = "muted" });
    Cell(item, 4, 1).Properties["bold"].Value.ShouldBe(true);
  }

  [Fact]
  public void Banding_fills_every_second_body_row_without_a_row_style()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      rows:
        5: { style: total }
      """, "segment,2025\nSpace,1\nAI,2\nOther,3\nTotal,4\nMore,5");

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    string[] fills = Enumerable.Range(2, 5)
      .Select(row => Cell(item, row, 1).Properties.GetValueOrDefault("fill")?.Value.ToString() ?? "-")
      .ToArray();
    fills.ShouldBe(["-", "light2 lighter 50", "-", "-", "-"]);
  }

  [Fact]
  public void A_number_is_spelled_by_its_format_and_a_negative_takes_the_formats_style()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      header_rows: 1
      columns:
        - {}
        - { format: billions }
        - { format: billions }
      rows:
        4: { format: dollars }
      cells:
        - { row: 2, column: 3, format: dollars }
      """, "segment,2024,2025\nSpace,3796,4086\nAI,2620,-3201\nTotal,6416,-885\nNote,n/a,");

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    Text(Cell(item, 1, 2)).ShouldBe("2024");
    Text(Cell(item, 2, 2)).ShouldBe("$3.8B");
    Text(Cell(item, 2, 3)).ShouldBe("4,086");
    Text(Cell(item, 3, 3)).ShouldBe("($3.2B)");
    Cell(item, 3, 3).Properties["color"].Value.ShouldBe(new Color { Hex = "#C00000" });
    Cell(item, 3, 2).Properties["color"].Value.ShouldBe(new Color { Theme = "dark1" });
    Text(Cell(item, 4, 3)).ShouldBe("(885)");
    Text(Cell(item, 5, 2)).ShouldBe("n/a");
    Text(Cell(item, 5, 3)).ShouldBe("");
  }

  [Fact]
  public void Cells_take_the_text_markup()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      """, "segment,note\nSpace,\"**Launch** and [Heavy Lift]{.emphasis}\"");

    // Act
    SceneCell cell = Cell(deck.Item("table", "results"), 2, 2);

    // Assert
    cell.Paragraphs[0].Runs[0].Properties["bold"].Value.ShouldBe(true);
    cell.Paragraphs[0].Runs[2].Properties["color"].Value.ShouldBe(new Color { Theme = "accent1" });
  }

  [Fact]
  public void A_merged_cell_shows_its_first_cells_text_and_takes_the_last_rows_bottom_border()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      rows:
        3: { style: total }
      merge:
        - { row: 2, column: 1, rows: 2 }
      """, "segment,2025\nRevenue,1\n,2");

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    item.Table!.Cells.Where(cell => cell.Column == 1).Select(cell => cell.Row).ShouldBe([1, 2]);
    SceneCell merged = Cell(item, 2, 1);
    (merged.Rows, merged.Columns).ShouldBe((2, 1));
    Text(merged).ShouldBe("Revenue");
    merged.Properties.ShouldNotContainKey("bold");
    merged.Properties["border_bottom.width"].Value.ShouldBe(2.0);
  }

  [Fact]
  public void A_cells_left_and_right_borders_merge_key_by_key_over_its_columns()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      columns:
        - { border_right: { color: dark1, width: 0.75 } }
        - { border_left: { color: dark1, width: 1 }, border_right: { color: dark1, width: 1, dash: dot } }
      cells:
        - { row: 2, column: 1, border_right: { width: 2 } }
        - { row: 2, column: 2, border_left: none }
      """);

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    SceneCell plain = Cell(item, 1, 1);
    plain.Properties["border_right.width"].Value.ShouldBe(0.75);
    plain.Properties["border_right.width"].Origin!.ToString()
      .ShouldBe("deck/tables/results.yaml:3 columns[1].border_right.width");
    plain.Properties.ShouldNotContainKey("border_left.color");
    Cell(item, 1, 2).Properties["border_right.dash"].Value.ShouldBe("dot");
    SceneCell widened = Cell(item, 2, 1);
    widened.Properties["border_right.width"].Value.ShouldBe(2.0);
    widened.Properties["border_right.color"].Value.ShouldBe(new Color { Theme = "dark1" });
    SceneCell cleared = Cell(item, 2, 2);
    cleared.Properties["border_left"].Value.ShouldBe("none");
    cleared.Properties.ShouldNotContainKey("border_left.width");
    cleared.Properties["border_right.width"].Value.ShouldBe(1.0);
  }

  [Fact]
  public void A_merge_takes_its_first_columns_left_border_and_its_last_columns_right_border()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      columns:
        - { border_left: { color: dark1, width: 1 }, border_right: { color: dark1, width: 1 } }
        - { border_left: { color: dark1, width: 2 }, border_right: { color: dark1, width: 2 } }
      merge:
        - { row: 1, column: 1, columns: 2 }
      """, "segment,\nSpace,1");

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    SceneCell merged = Cell(item, 1, 1);
    merged.Properties["border_left.width"].Value.ShouldBe(1.0);
    merged.Properties["border_right.width"].Value.ShouldBe(2.0);
  }

  [Fact]
  public void A_cells_top_and_bottom_borders_merge_key_by_key_over_its_rows()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      rows:
        4: { style: total }
      cells:
        - { row: 1, column: 1, border_bottom: none }
        - { row: 2, column: 2, border_bottom: { color: dark1, width: 0.75 } }
        - { row: 4, column: 3, border_top: { width: 3 } }
      """);

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    Cell(item, 1, 1).Properties["border_bottom"].Value.ShouldBe("none");
    Cell(item, 1, 2).Properties["border_bottom.width"].Value.ShouldBe(1.0);
    Cell(item, 2, 2).Properties["border_bottom.width"].Value.ShouldBe(0.75);
    Cell(item, 2, 3).Properties.ShouldNotContainKey("border_bottom.width");
    SceneCell total = Cell(item, 4, 3);
    total.Properties["border_top.width"].Value.ShouldBe(3.0);
    total.Properties["border_top.color"].Value.ShouldBe(new Color { Theme = "dark1" });
    Cell(item, 4, 2).Properties["border_top.width"].Value.ShouldBe(1.0);
  }

  [Theory]
  [InlineData("columns:\n  - { border_top: { color: dark1, width: 1 } }", "border_top")]
  [InlineData("rows:\n  2: { border_left: { color: dark1, width: 1 } }", "border_left")]
  public void A_column_has_no_top_or_bottom_border_and_a_row_no_left_or_right(string yaml, string key)
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table($"data: results.csv\n{yaml}");

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Message.ShouldStartWith($"unknown key {key}");
  }

  [Fact]
  public void Text_left_in_a_merged_away_cell_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      merge:
        - { row: 2, column: 1, columns: 2 }
      """);

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Message.ShouldBe("the merge covers row 2, column 2, which holds 3796; a merge shows its first cell, so " +
      "leave the rest empty in the CSV in deck/tables/results.yaml:3; see pintura --docs tables");
  }

  [Fact]
  public void Overlapping_merges_are_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      merge:
        - { row: 1, column: 2, rows: 2 }
        - { row: 2, column: 2, columns: 2 }
      """, "a,b,c\n,,\n,,");

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Problem.ShouldBe("this merge overlaps the one from row 1, column 2");
  }

  [Fact]
  public void A_row_past_the_csv_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      rows:
        7: { bold: true }
      """);

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Problem.ShouldBe("row 7 is past the table's 4 rows; rows are numbered from 1, header included");
  }

  [Fact]
  public void A_row_style_the_table_style_lacks_lists_the_ones_it_has()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      rows:
        4: { style: grand_total }
      """);

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Problem.ShouldBe("unknown row style grand_total; table style plain has header and total");
  }

  [Fact]
  public void An_unknown_table_style_lists_the_themes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: financial
      """);

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Problem.ShouldBe("unknown table style financial; the theme's are plain");
  }

  [Fact]
  public void More_columns_than_the_csv_has_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      columns: [{}, {}, {}, { width: 100 }]
      """);

    // Act
    LoadException error = deck.Error("table");

    // Assert
    error.Problem.ShouldBe("columns lists 4 columns, and results.csv has 3");
  }

  [Fact]
  public void Columns_wider_than_the_frame_are_an_error_in_check()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      columns:
        - { width: 400 }
        - { width: 300 }
        - { width: 200 }
      """);

    // Act
    List<Finding> findings = Checks.Geometry(deck.Load());

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    (finding.Level, finding.Where).ShouldBe(("error", "table/results"));
    finding.Message.ShouldBe("its columns add up to 900pt, 20pt wider than its 880pt frame");
  }

  [Fact]
  public void Rows_growing_past_the_frame_are_an_error()
  {
    // Arrange
    var measurement = new Measurement([],
      [new BoxMeasure("results", "table", "slide", "none", 12, [1, 1], 42.5, null, 422.5, "table")]);

    // Act
    List<Finding> findings = Checks.Text(measurement, new FontSetting(false, false));

    // Assert
    Finding finding = findings.ShouldHaveSingleItem();
    finding.Level.ShouldBe("error");
    finding.Message.ShouldBe("its rows grow 42.5pt past the bottom of its frame");
  }

  [Fact]
  public void The_payload_carries_widths_and_cells_with_hex_colors()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      rows:
        2: { height: 40 }
      merge:
        - { row: 1, column: 2, columns: 2 }
      """, "segment,Year,\nSpace,3796,4086");

    // Act
    TablePayload table = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "results").Table!;

    // Assert
    table.Widths.ShouldBe([880.0 / 3, 880.0 / 3, 880.0 / 3]);
    table.Heights.ShouldBe([null, 40.0]);
    CellPayload header = table.Cells[1];
    (header.Row, header.Column, header.Columns).ShouldBe((1, 2, 2));
    header.Fill.ShouldBe("#E7E6E6");
    header.BorderBottom.ShouldBe(new StrokePayload("#000000", 1, "solid"));
    header.Text.Paragraphs[0].Runs[0].Bold.ShouldBe(true);
    table.Cells.Count.ShouldBe(5);
  }

  [Fact]
  public void The_inspector_lists_each_cell_with_what_differs_from_the_base_text()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      style: plain
      header_rows: 1
      """);
    Presentation presentation = deck.Load();
    Slide slide = presentation.Slides[0];

    // Act
    ElementDetail detail = HeadlessRunner.Detail(presentation.Theme, slide, deck.Item("table", "results"));

    // Assert
    detail.Properties.Select(property => property.Key).ShouldContain("size");
    CellEntry header = detail.Cells!.First();
    header.Text.ShouldBe("segment");
    header.Properties.Select(property => property.Key)
      .ShouldBe(["row_style", "fill", "border_bottom.color", "border_bottom.width", "bold"]);
    detail.Cells![3].Properties.ShouldBeEmpty();
  }

  [Fact]
  public void The_export_carries_each_cell_with_its_span_and_paragraphs()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Table("""
      data: results.csv
      header_rows: 1
      columns:
        - {}
        - { format: billions }
      """);

    // Act
    ExportItem item = ExportBuilder.Build(deck.Load()).Slides[0].Items!.Single(item => item.Id == "results");

    // Assert
    item.Properties["widths"].ShouldBe(new List<double> { 880.0 / 3, 880.0 / 3, 880.0 / 3 });
    var cells = (List<ExportCell>)item.Properties["cells"];
    cells.Count.ShouldBe(12);
    cells[4].Paragraphs[0].Runs[0].Text.ShouldBe("$3.8B");
  }

  [Fact]
  public void A_date_format_spells_a_cells_iso_date()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("formats:\n",
      "formats:\n  month: { date: MMMM YYYY }\n"));
    deck.Table("""
      data: results.csv
      header_rows: 1
      columns:
        - {}
        - { format: month }
      """, "company,renewal\nSnowflake,2025-06-30\nDatadog,Q2 2025");

    // Act
    SceneItem item = deck.Item("table", "results");

    // Assert
    Text(Cell(item, 2, 2)).ShouldBe("June 2025");
    Text(Cell(item, 3, 2)).ShouldBe("Q2 2025");
  }



}