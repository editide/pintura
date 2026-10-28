using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ChartTests
{
  [Fact]
  public void A_chart_reads_one_series_per_row_and_one_category_per_column()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      """);

    // Act
    Chart chart = deck.Load().Charts["revenue"];

    // Assert
    chart.Categories.ShouldBe(["2024", "2025"]);
    chart.Series.Select(series => series.Name).ShouldBe(["Space", "AI"]);
    chart.Series[1].Values.ShouldBe([2620, -3201]);
  }

  [Fact]
  public void Series_in_columns_reads_the_header_as_series_and_the_first_column_as_categories()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_bar
      data: revenue.csv
      series_in: columns
      """);

    // Act
    Chart chart = deck.Load().Charts["revenue"];

    // Assert
    chart.Categories.ShouldBe(["Space", "AI"]);
    chart.Series.Select(series => series.Name).ShouldBe(["2024", "2025"]);
    chart.Series[1].Values.ShouldBe([4086, -3201]);
  }

  [Fact]
  public void Quoted_fields_keep_their_commas_and_an_empty_cell_is_a_gap()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: line
      data: revenue.csv
      """, "segment,2024,2025\n\"Space, launch\",3796,\nAI,2620,3201");

    // Act
    Chart chart = deck.Load().Charts["revenue"];

    // Assert
    chart.Series[0].Name.ShouldBe("Space, launch");
    chart.Series[0].Values.ShouldBe([3796, null]);
  }

  [Fact]
  public void A_spelled_out_number_in_the_csv_names_the_line_and_asks_for_a_format()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      """, "segment,2024,2025\nSpace,$3.8B,4086");

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Message.ShouldBe("$3.8B, for Space in 2024, isn't a plain number: write it without separators or " +
      "symbols, and let a format spell it in deck/charts/revenue.csv:2; see pintura --docs charts");
  }

  [Fact]
  public void A_row_with_the_wrong_number_of_cells_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      """, "segment,2024,2025\nSpace,3796");

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("the row has 2 cells and the header 3");
    error.Origin.Line.ShouldBe(2);
  }

  [Fact]
  public void An_unknown_chart_style_lists_the_themes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      style: sparse
      data: revenue.csv
      """);

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("unknown chart style sparse; the theme's are minimal");
    error.Origin.File.ShouldBe("deck/charts/revenue.yaml");
    error.Origin.Line.ShouldBe(2);
  }

  [Fact]
  public void An_unknown_format_is_an_error_where_it_is_named()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      value_axis: { format: millions }
      """);

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("unknown format millions; the theme's formats are billions, dollars");
    error.Topic.ShouldBe("charts");
  }

  [Fact]
  public void A_label_position_the_type_lacks_is_an_error_at_the_level_that_set_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: line
      style: minimal
      data: revenue.csv
      data_labels: { position: outside }
      """);

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("a line chart places labels above, below, left, right, or center, not outside");
    error.Origin.Line.ShouldBe(4);
  }

  [Fact]
  public void Percent_labels_are_for_pies_and_doughnuts()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: stacked_column
      data: revenue.csv
      data_labels: { show: percent }
      """);

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("show: percent is for pie and doughnut charts; a stacked_column shows value or none");
  }

  [Fact]
  public void A_pie_takes_one_series()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: pie
      data: revenue.csv
      """);

    // Act
    LoadException error = deck.Error("chart");

    // Assert
    error.Problem.ShouldBe("a pie draws one series, and revenue.csv has 2; use series_in to turn it, or a " +
      "doughnut for a ring per series");
  }

  [Fact]
  public void Keys_layer_from_the_base_through_the_style_to_the_chart_and_maps_merge_key_by_key()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: stacked_column
      style: minimal
      data: revenue.csv
      data_labels: { format: billions }
      """);

    // Act
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    item.Properties["data_labels.show"].Value.ShouldBe("value");
    item.Properties["data_labels.show"].Origin!.ToString()
      .ShouldBe("theme/styles.yaml:37 charts.minimal.data_labels.show");
    item.Properties["data_labels.format"].Origin!.File.ShouldBe("deck/charts/revenue.yaml");
    item.Properties["value_axis.visible"].Value.ShouldBe(false);
    item.Properties["value_axis.gridlines"].Origin!.Line.ShouldBe(31);
    item.Properties["legend"].Value.ShouldBe("bottom");
  }

  [Fact]
  public void Chart_text_falls_back_to_the_base_text_and_what_nothing_sets_is_powerpoints_default()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      data_labels: { show: value }
      """);

    // Act
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    item.Properties["text.font"].Value.ShouldBe("body");
    item.Properties["text.font"].Origin!.File.ShouldBe("theme/styles.yaml");
    item.Properties["text.size"].Value.ShouldBe(11.0);
    item.Properties["data_labels.position"].ShouldBe(new Resolved("outside", null));
    item.Properties["gap_width"].ShouldBe(new Resolved(150.0, null));
    item.Properties["overlap"].ShouldBe(new Resolved(0.0, null));
  }

  [Fact]
  public void A_stacked_chart_overlaps_its_series_fully_and_a_pie_has_no_axes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: stacked_bar
      data: revenue.csv
      overlap: 20
      """);
    deck.Write("deck/charts/share.yaml", """
      type: pie
      data: share.csv
      series_in: columns
      """);
    deck.Write("deck/charts/share.csv", "segment,2025\nSpace,4086\nAI,3201");
    deck.Write("deck/slides/chart.yaml", """
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
        - id: share
          chart: share
          frame: [490, 100, 430, 380]
      """);

    // Act
    SceneItem bar = deck.Item("chart", "chart");
    SceneItem pie = deck.Item("chart", "share");

    // Assert
    bar.Properties["overlap"].ShouldBe(new Resolved(100.0, null));
    pie.Properties.Keys.ShouldNotContain(key => key.Contains("axis") || key == "gap_width" || key == "overlap");
  }

  [Fact]
  public void The_payload_carries_hex_colors_the_format_and_the_color_a_negative_takes()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      data_labels: { show: value, format: billions, color: light1 }
      series_colors: [accent2 lighter 50, "#123456"]
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    chart.Colors.ShouldBe(["#F6BE98", "#123456"]);
    LabelsPayload labels = chart.Series[0].Labels!;
    labels.Format!.Scale.ShouldBe(0.001);
    labels.Format.NegativeColor.ShouldBe("#C00000");
    labels.Color.ShouldBe("#FFFFFF");
    chart.Text.Font.ShouldBe("Arial");
    chart.ValueAxis!.Gridlines.ShouldBeTrue();
  }

  [Fact]
  public void A_date_format_on_the_category_axis_makes_a_time_line_of_iso_dates()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("formats:\n",
      "formats:\n  day: { date: \"MMM-D-'YY\" }\n"));
    deck.Chart("""
      type: line
      data: revenue.csv
      category_axis: { format: day, step: 7 days }
      value_axis: { step: 500 }
      data_labels: { show: value, bold: true }
      """, "index,2026-01-02,2026-01-05\nS&P,100,101.5");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Dates.ShouldBe(new DateAxisPayload("MMM-D-'YY", 7, "days"));
    chart.ValueAxis!.Step.ShouldBe(500);
    chart.Series[0].Labels!.Bold.ShouldBeTrue();
    item.Properties["category_axis.step"].Value.ToString().ShouldBe("7 days");
  }

  [Fact]
  public void A_category_axis_can_hide_its_labels_and_keep_its_line()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv\ncategory_axis: { labels: false }");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.CategoryAxis.ShouldBe(true);
    chart.CategoryLabels.ShouldBe(false);
    item.Properties["category_axis.labels"].Value.ShouldBe(false);
  }

  [Fact]
  public void A_plot_takes_insets_from_the_chart_and_a_wrong_count_is_an_error()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: line\ndata: revenue.csv\nplot: { insets: [0, 20, 0, 0] }");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    deck.Chart("type: line\ndata: revenue.csv\nplot: { insets: [0, 20] }");
    Slide broken = deck.Load().Slides.Single(slide => slide.Id == "chart");

    // Assert
    chart.PlotInsets.ShouldBe([0, 20, 0, 0]);
    broken.Error!.Message.ShouldContain("insets must be [left, top, right, bottom] in points");
  }

  [Fact]
  public void Category_labels_show_by_default_as_in_PowerPoint()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv");

    // Act
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    item.Properties["category_axis.labels"].Value.ShouldBe(true);
    item.Properties["category_axis.labels"].Origin.ShouldBeNull();
  }

  [Fact]
  public void A_date_axis_needs_dates_and_a_date_format_spells_only_dates()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("formats:\n",
      "formats:\n  day: { date: \"MMM-D-'YY\" }\n"));

    // Act
    deck.Chart("type: line\ndata: revenue.csv\ncategory_axis: { format: day }");
    LoadException notDates = deck.Error("chart");
    deck.Chart("type: line\ndata: revenue.csv\nvalue_axis: { format: day }");
    LoadException onValues = deck.Error("chart");
    deck.Chart("type: line\ndata: revenue.csv\ncategory_axis: { step: 1 month }");
    LoadException stepAlone = deck.Error("chart");

    // Assert
    notDates.Problem.ShouldBe("day spells dates, and category 2024 in revenue.csv isn't one written as YYYY-MM-DD");
    onValues.Problem.ShouldBe("day spells dates; only a category axis takes a date format");
    stepAlone.Problem.ShouldBe("a step is for a date axis; give category_axis a date format");
  }

  [Fact]
  public void A_series_draws_as_a_line_on_the_secondary_axis_or_as_a_total_with_labels_of_its_own()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: stacked_column
      data: revenue.csv
      data_labels: { show: value, position: center, color: light1, bold: true }
      secondary_axis: { max: 1 }
      series:
        Total: { type: total }
        Margin: { type: line, axis: secondary, data_labels: { format: billions } }
      """, "segment,2024,2025\nSpace,3796,4086\nAI,2620,3201\nTotal,6416,7287\nMargin,0.2,0.25");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series.Select(series => (series.Type, series.Secondary))
      .ShouldBe([("column", false), ("column", false), ("total", false), ("line", true)]);
    chart.Series[0].Labels.ShouldBe(chart.Series[1].Labels);
    chart.Series[0].Labels!.Position.ShouldBe("center");
    chart.Series[2].Labels.ShouldBe(new LabelsPayload("value", "outside", null, null, true));
    chart.Series[3].Labels!.Position.ShouldBe("above");
    chart.Series[3].Labels!.Color.ShouldBe("#FFFFFF");
    chart.Series[3].Labels!.Format!.Scale.ShouldBe(0.001);
    chart.SecondaryAxis!.Max.ShouldBe(1);
    item.Properties["series.Total.data_labels.show"].Origin.ShouldBeNull();
    item.Properties["series.Margin.data_labels.color"].Origin!.Line.ShouldBe(3);
    item.Properties["series.Margin.data_labels.format"].Origin!.Line.ShouldBe(7);
  }

  [Fact]
  public void The_secondary_axis_stays_off_until_a_line_is_on_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: clustered_column\ndata: revenue.csv\nsecondary_axis: { max: 1 }");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    chart.SecondaryAxis.ShouldBeNull();
    deck.Item("chart", "chart").Properties.Keys.ShouldNotContain("secondary_axis.max");
  }

  [Fact]
  public void A_series_takes_only_what_its_chart_can_draw()
  {
    // Arrange
    using var deck = new DeckFixture();
    LoadException Load(string yaml)
    {
      deck.Chart(yaml);
      return deck.Error("chart");
    }

    // Act
    LoadException unknown = Load("type: stacked_column\ndata: revenue.csv\nseries: { Energy: { type: total } }");
    LoadException lineOnBars = Load("type: clustered_bar\ndata: revenue.csv\nseries: { AI: { type: line } }");
    LoadException clusteredTotal = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { type: total } }");
    LoadException secondaryColumn = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { axis: " +
      "secondary } }");
    LoadException allSecondary = Load("type: line\ndata: revenue.csv\nseries: { Space: { axis: secondary }, " +
      "AI: { axis: secondary } }");
    LoadException totalPosition = Load("type: stacked_column\ndata: revenue.csv\nseries: { AI: { type: total, " +
      "data_labels: { position: center } } }");
    LoadException pie = Load("type: doughnut\ndata: revenue.csv\nseries: { AI: { data_labels: { show: value } } }");

    // Assert
    unknown.Problem.ShouldBe("no series Energy in revenue.csv; its series are Space, AI");
    lineOnBars.Problem.ShouldBe("a line runs along the categories, which run down a bar chart; make it a column chart");
    clusteredTotal.Problem.ShouldBe("a total sits on a stack of columns or bars; make the chart a stacked_column or " +
      "stacked_bar");
    secondaryColumn.Problem.ShouldBe("only a line takes the secondary axis; give AI type: line");
    allSecondary.Problem.ShouldBe("every series is on the secondary axis; leave one on the value axis");
    totalPosition.Problem.ShouldBe("a total's labels sit past the end of its stack, so it takes no position");
    pie.Problem.ShouldBe("a pie's series take nothing of their own; series_colors colors its slices");
  }

  [Fact]
  public void A_column_or_bar_series_colors_single_points_by_category()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      series:
        AI:
          points:
            "2025": { color: "#C00000" }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series[0].Colors.ShouldBeNull();
    chart.Series[1].Colors.ShouldBe([null, "#C00000"]);
    item.Properties["series.AI.points.2025.color"].Origin!.Line.ShouldBe(6);
  }

  [Fact]
  public void A_point_color_names_a_category_of_a_series_drawn_as_columns_or_bars()
  {
    // Arrange
    using var deck = new DeckFixture();
    LoadException Load(string yaml)
    {
      deck.Chart(yaml);
      return deck.Error("chart");
    }

    // Act
    LoadException unknown = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { points: { \"2030\": " +
      "{ color: accent1 } } } }");
    LoadException column = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { points: { \"2025\": { " +
      "markers: { shape: circle } } } } }");
    LoadException empty = Load("type: line\ndata: revenue.csv\nseries: { AI: { points: { \"2025\": {} } } }");
    LoadException total = Load("type: stacked_column\ndata: revenue.csv\nseries: { AI: { type: total, points: { " +
      "\"2025\": { color: accent1 } } } }");

    // Assert
    unknown.Problem.ShouldBe("no category 2030 in revenue.csv; its categories are 2024, 2025");
    column.Problem.ShouldBe("a point's markers sit on a line, and AI is drawn as a column");
    empty.Problem.ShouldBe("a point takes a color, markers, data_labels, or several");
    total.Problem.ShouldBe("a total's points take only data_labels, and AI is drawn as a total");
  }

  [Fact]
  public void A_line_takes_the_charts_markers_and_a_series_its_own_over_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      markers: { shape: circle, size: 7 }
      series:
        AI: { type: line, markers: { fill: light1 } }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series[0].Markers.ShouldBeNull();
    chart.Series[1].Markers.ShouldBe(new MarkersPayload("circle", 7, "#FFFFFF"));
    item.Properties["series.AI.markers.shape"].Origin!.Line.ShouldBe(3);
    item.Properties["series.AI.markers.fill"].Origin!.Line.ShouldBe(5);
  }

  [Fact]
  public void A_point_takes_data_labels_of_its_own_over_its_series_key_by_key()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      data_labels: { show: value, position: inside, color: light1 }
      series:
        Space:
          points:
            "2024": { data_labels: { show: none } }
        AI:
          data_labels: { show: none }
          points:
            "2025": { data_labels: { show: value, bold: true } }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series[0].PointLabels.ShouldBe([LabelsPayload.Hidden, null]);
    chart.Series[1].Labels.ShouldBeNull();
    chart.Series[1].PointLabels.ShouldBe([null, new LabelsPayload("value", "inside", null, "#FFFFFF", true)]);
    item.Properties["series.AI.points.2025.data_labels.bold"].Origin!.Line.ShouldBe(11);
    item.Properties["series.AI.points.2025.data_labels.position"].Origin!.Line.ShouldBe(3);
  }

  [Fact]
  public void A_point_showing_the_only_label_takes_its_series_default_position()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: line
      data: revenue.csv
      series:
        AI: { points: { "2025": { data_labels: { show: value } } } }
      """);

    // Act
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    item.Properties["series.AI.points.2025.data_labels.position"].Value.ShouldBe("above");
    item.Properties["series.AI.points.2025.data_labels.position"].Origin.ShouldBeNull();
  }

  [Fact]
  public void A_points_data_labels_place_as_its_series_does_and_a_totals_take_no_position()
  {
    // Arrange
    using var deck = new DeckFixture();
    LoadException Load(string yaml)
    {
      deck.Chart(yaml);
      return deck.Error("chart");
    }

    // Act
    LoadException line = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { type: line, points: { " +
      "\"2025\": { data_labels: { show: value, position: inside } } } } }");
    LoadException percent = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { points: { \"2025\": { " +
      "data_labels: { show: percent } } } } }");
    LoadException total = Load("type: stacked_column\ndata: revenue.csv\nseries: { AI: { type: total, points: { " +
      "\"2025\": { data_labels: { position: center } } } } }");

    // Assert
    line.Problem.ShouldBe("a line places labels above, below, left, right, or center, not inside");
    percent.Problem.ShouldBe("show: percent is for pie and doughnut charts; a point shows value or none");
    total.Problem.ShouldBe("a total's labels sit past the end of its stack, so it takes no position");
  }

  [Fact]
  public void A_totals_point_takes_data_labels_alone()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: stacked_column
      data: revenue.csv
      series:
        AI: { type: total, points: { "2024": { data_labels: { show: none } } } }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    chart.Series[1].PointLabels.ShouldBe([LabelsPayload.Hidden, null]);
  }

  [Fact]
  public void A_lines_point_takes_markers_of_its_own_over_the_series_and_its_color_outlines_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      series:
        Space: { type: line, points: { "2024": { markers: { shape: diamond } } } }
        AI:
          type: line
          markers: { shape: circle, size: 7 }
          points:
            "2024": { color: "#C00000" }
            "2025": { color: "#C00000", markers: { shape: square, size: 9, fill: light1 } }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series[0].Markers.ShouldBeNull();
    chart.Series[0].PointMarkers.ShouldBe([new MarkersPayload("diamond", 5, null), null]);
    chart.Series[1].Colors.ShouldBe(["#C00000", "#C00000"]);
    chart.Series[1].PointMarkers.ShouldBe([null, new MarkersPayload("square", 9, "#FFFFFF")]);
    item.Properties["series.AI.points.2025.markers.shape"].Origin!.Line.ShouldBe(10);
  }

  [Fact]
  public void A_value_axis_can_run_top_down()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("""
      type: clustered_column
      data: revenue.csv
      value_axis: { reverse: true }
      secondary_axis: { reverse: true }
      series:
        AI: { type: line, axis: secondary }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.ValueAxis!.Reverse.ShouldBeTrue();
    chart.SecondaryAxis!.Reverse.ShouldBeTrue();
    item.Properties["secondary_axis.reverse"].Origin!.Line.ShouldBe(4);
  }

  [Fact]
  public void A_line_draws_no_markers_until_something_gives_them_a_shape()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Chart("type: line\ndata: revenue.csv");

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;
    SceneItem item = deck.Item("chart", "chart");

    // Assert
    chart.Series.ShouldAllBe(series => series.Markers == null);
    item.Properties["markers.shape"].Value.ShouldBe("none");
    item.Properties["markers.shape"].Origin.ShouldBeNull();
    item.Properties.Keys.ShouldNotContain("markers.size");
  }

  [Fact]
  public void Markers_sit_only_on_a_lines_points()
  {
    // Arrange
    using var deck = new DeckFixture();
    LoadException Load(string yaml)
    {
      deck.Chart(yaml);
      return deck.Error("chart");
    }

    // Act
    LoadException columns = Load("type: clustered_column\ndata: revenue.csv\nmarkers: { shape: circle }");
    LoadException column = Load("type: clustered_column\ndata: revenue.csv\nseries: { AI: { markers: { shape: " +
      "circle } } }");
    LoadException total = Load("type: stacked_column\ndata: revenue.csv\nseries: { AI: { type: total, markers: { " +
      "shape: circle } } }");
    LoadException size = Load("type: line\ndata: revenue.csv\nmarkers: { shape: circle, size: 1 }");

    // Assert
    columns.Problem.ShouldBe("markers sit on a line's points, and a clustered_column draws no line; give a series " +
      "type: line");
    column.Problem.ShouldBe("markers sit on a line's points, and AI is drawn as columns; give it type: line");
    total.Problem.ShouldBe("markers sit on a line's points, and AI is a total; a total draws only its labels");
    size.Problem.ShouldBe("a marker's size is a whole number of points, from 2 to 72, as in PowerPoint");
  }

  [Fact]
  public void A_total_that_isnt_its_stacks_sum_is_a_warning_to_within_the_digit_its_labels_show()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("formats:\n",
      "formats:\n  tenths: { decimals: 1 }\n"));
    deck.Chart("""
      type: stacked_column
      data: revenue.csv
      data_labels: { show: value, format: tenths }
      series: { Total: { type: total } }
      """, "segment,2023,2024,2025\nSpace,6.04,6,6\nAI,2.4,2.4,2.4\nTotal,8.4,8.5,8.7");

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.Select(finding => finding.Message)
      .ShouldBe(["Total is 8.5 in 2024, where its stack adds up to 8.4, and 1 more like it, in revenue.csv"]);
  }

  [Fact]
  public void A_totals_point_is_checked_to_its_own_labels_digit_and_not_at_all_where_it_shows_none()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("formats:\n",
      "formats:\n  tenths: { decimals: 1 }\n  whole: { decimals: 0 }\n"));
    deck.Chart("""
      type: stacked_column
      data: revenue.csv
      data_labels: { show: value, format: tenths }
      series:
        Total:
          type: total
          points:
            "2024": { data_labels: { format: whole } }
            "2025": { data_labels: { show: none } }
      """, "segment,2023,2024,2025\nSpace,6,6,6\nAI,2.4,2.4,2.4\nTotal,8.5,8.6,9");

    // Act
    List<Finding> findings = Checks.Consistency(deck.Load());

    // Assert
    findings.Select(finding => finding.Message)
      .ShouldBe(["Total is 8.5 in 2023, where its stack adds up to 8.4, in revenue.csv"]);
  }


}