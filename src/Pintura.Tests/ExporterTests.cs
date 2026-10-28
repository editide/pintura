using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ExporterTests
{
  private static JsonNode Json(string json)
  {
    return JsonNode.Parse(json)!;
  }

  private static void ShouldMatch(JsonNode? actual, string expected)
  {
    JsonNode written = JsonNode.Parse(actual?.ToJsonString() ?? "null")!;
    JsonNode.DeepEquals(written, Json(expected)).ShouldBeTrue($"{written.ToJsonString()}\nis not\n{expected}");
  }

  private static Exporter NewExporter()
  {
    return new Exporter(new NoTools(), (relative, _) => (relative, relative));
  }

  private sealed class NoTools : ITools
  {
    public JsonNode? Call(string tool, JsonObject input)
    {
      throw new InvalidOperationException($"no tools in a unit test, asked for {tool}");
    }


  }

  [Fact]
  public void A_lighter_theme_color_is_luminance_modulated_and_offset_and_stays_a_scheme_color()
  {
    // Act
    JsonNode result = Exporter.Color(Json("""{ "theme": "accent1", "lighter": 80 }"""));

    // Assert
    ShouldMatch(result, """{ "scheme": "accent1", "luminance_mod": 20, "luminance_off": 80 }""");
  }

  [Fact]
  public void Opacity_becomes_the_fill_colors_alpha_and_none_is_transparent()
  {
    // Act
    JsonNode faded = Exporter.Color(Json("""{ "theme": "light1" }"""), 15);
    JsonNode none = Exporter.Color(Json("""{ "none": true }"""));

    // Assert
    ShouldMatch(faded, """{ "scheme": "lt1", "alpha": 15 }""");
    ShouldMatch(none, "\"transparent\"");
  }

  [Fact]
  public void Theme_fonts_become_links_to_the_theme_and_other_families_stay_literal()
  {
    // Act
    JsonNode?[] fonts =
    [
      Exporter.Font(Json("""{ "theme": "heading" }""")),
      Exporter.Font(Json("""{ "theme": "body" }""")),
      Exporter.Font(Json("""{ "family": "Futura" }"""))
    ];

    // Assert
    ShouldMatch(new JsonArray(fonts), """["+mj-lt", "+mn-lt", "Futura"]""");
  }

  [Fact]
  public void Against_a_base_only_what_differs_is_written_and_a_flag_the_base_sets_is_turned_off()
  {
    // Arrange
    JsonNode baseline = Json("""{ "font": { "theme": "heading" }, "size": 40, "bold": true }""");
    JsonNode own = Json("""{ "font": { "theme": "heading" }, "size": 30 }""");

    // Act
    JsonObject style = Exporter.TextStyle(own, baseline);

    // Assert
    ShouldMatch(style, """{ "font_size": 30, "is_bold": false }""");
  }

  [Fact]
  public void A_bullet_keeps_its_own_color_and_size_as_a_percent_of_the_text()
  {
    // Act
    JsonObject result = Exporter.Bullet(Json("""
      { "bullet": "+", "bulletColor": { "theme": "accent6" }, "bulletSize": 1.5 }
      """));

    // Assert
    ShouldMatch(result, """
      { "type": "character", "character": "+", "color": { "scheme": "accent6" }, "font_size": 150,
        "font_size_is_percentage": true }
      """);
  }

  [Fact]
  public void A_level_hangs_by_its_step_in_from_the_one_before_unless_it_sets_its_own_hanging()
  {
    // Arrange
    JsonNode theme = Json("""
      {
        "text": {}, "title": { "paragraphs": [{ "properties": {} }] },
        "levels": {
          "1": { "bullet": "•", "indent": 20, "properties": {} },
          "2": { "bullet": "none", "indent": 50, "hanging": 22, "properties": {} }
        }
      }
      """);

    // Act
    JsonArray body = Exporter.MasterStyles(theme)["body"]!.AsArray();

    // Assert
    ShouldMatch(body[1]!["paragraph_style"]!["indent"], "-20");
    ShouldMatch(body[2]!["paragraph_style"]!["indent"], "-22");
    ShouldMatch(body[2]!["bullet"], """{ "type": "none" }""");
  }

  [Fact]
  public void A_corner_radius_in_points_becomes_a_proportion_of_the_largest_radius_the_box_allows()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [0, 0, 200, 72], "shape": "rounded_rectangle", "adjust.corner": 6 } }
      """);

    // Act
    Exporter.Geometry result = NewExporter().Preset(item);

    // Assert
    result.Type.ShouldBe("round_rectangle");
    ((double)result.Adjustments["corner_radius"]!).ShouldBe(0.167, 0.001);
  }

  [Fact]
  public void A_downward_chevron_is_the_right_pointing_preset_turned_a_quarter_measured_along_its_height()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [0, 0, 40, 100], "shape": "chevron", "direction": "down", "adjust.point": 25 } }
      """);

    // Act
    Exporter.Geometry result = NewExporter().Preset(item);

    // Assert
    result.Rotation.ShouldBe(90);
    ShouldMatch(result.Adjustments["point_depth"], "0.25");
  }

  [Fact]
  public void A_line_drawn_up_and_to_the_left_keeps_its_box_between_the_endpoints_and_flips_both_ways()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "from": [100, 80], "to": [40, 20], "end": "triangle", "stroke.color": { "theme": "dark1" },
        "stroke.width": 1 } }
      """);

    // Act
    JsonObject result = Exporter.Line(item);

    // Assert
    ShouldMatch(result["position"], """{ "left": 40, "top": 20, "flip_horizontal": true, "flip_vertical": true }""");
    ShouldMatch(result["size"], """{ "width": 60, "height": 60 }""");
    ShouldMatch(result["shape_style"]!["tail_arrow"], "\"triangle\"");
  }

  [Fact]
  public void An_arc_exports_as_powerpoints_arc_in_its_frame_with_its_angles_as_turns_and_its_arrowheads()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [100, 100, 300, 300], "arc.from": 270, "arc.to": 45, "end": "triangle",
        "stroke.color": { "theme": "dark1" }, "stroke.width": 1.5 } }
      """);

    // Act
    JsonObject result = Exporter.Arc(item);

    // Assert
    ShouldMatch(result["shape_type"], "\"arc\"");
    ShouldMatch(result["position"], """{ "left": 100, "top": 100 }""");
    ShouldMatch(result["size"], """{ "width": 300, "height": 300 }""");
    ShouldMatch(result["shape_adjustments"], """{ "start_angle": 0.75, "end_angle": 0.125 }""");
    ShouldMatch(result["shape_style"]!["tail_arrow"], "\"triangle\"");
    ShouldMatch(result["shape_style"]!["border_weight"], "1.5");
  }

  [Fact]
  public void A_contained_picture_is_centered_in_its_frame_at_its_own_aspect_ratio()
  {
    // Act
    double[] frame = Exporter.Contain([0, 0, 100, 100], [200, 100]);

    // Assert
    frame.ShouldBe([0, 25, 100, 50]);
  }

  [Fact]
  public void A_contained_jpeg_is_exported_at_its_own_aspect_ratio()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("photo.jpg", "");
    File.WriteAllBytes(folder.PathTo("photo.jpg"), Images.Jpeg(200, 100));
    Exporter exporter = NewExporter();
    JsonNode item = Json($$"""
      { "kind": "picture", "properties": { "picture": {{JsonValue.Create(folder.PathTo("photo.jpg")).ToJsonString()}},
        "frame": [0, 0, 100, 100], "fit": "contain" } }
      """);

    // Act
    JsonObject picture = exporter.Picture(item);

    // Assert
    ShouldMatch(picture["position"], """{ "left": 0, "top": 25 }""");
    ShouldMatch(picture["size"], """{ "width": 100, "height": 50 }""");
    picture["lock_aspect_ratio"]!.GetValue<bool>().ShouldBeTrue();
  }

  [Fact]
  public void A_pictures_stroke_goes_as_its_border()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("shot.png", "not read");
    Exporter exporter = NewExporter();
    JsonNode item = Json($$"""
      { "kind": "picture", "properties": { "picture": {{JsonValue.Create(folder.PathTo("shot.png")).ToJsonString()}},
        "frame": [0, 0, 100, 50], "fit": "stretch", "stroke.color": { "theme": "accent2" }, "stroke.width": 0.5,
        "stroke.dash": "dot" } }
      """);

    // Act
    JsonObject picture = exporter.Picture(item);

    // Assert
    ShouldMatch(picture["shape_style"],
      """{ "border_color": { "scheme": "accent2" }, "border_weight": 0.5, "border_dash_type": "dot" }""");
    exporter.Gaps.ShouldBeEmpty();
  }

  [Fact]
  public void A_shadow_goes_as_the_shape_styles_effects_its_opacity_as_the_colors_alpha()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("shot.png", "not read");
    Exporter exporter = NewExporter();
    JsonNode item = Json($$"""
      { "kind": "picture", "properties": { "picture": {{JsonValue.Create(folder.PathTo("shot.png")).ToJsonString()}},
        "frame": [0, 0, 100, 50], "fit": "stretch", "shadow.color": { "theme": "dark1" }, "shadow.opacity": 40,
        "shadow.distance": 3 } }
      """);

    // Act
    JsonObject picture = exporter.Picture(item);

    // Assert
    ShouldMatch(picture["shape_style"], """
      { "effects": { "shadow": { "color": { "scheme": "dk1", "alpha": 40 }, "blur": 0, "distance": 3, "angle": 0 } } }
      """);
  }

  [Theory]
  [InlineData(342, 35, 101.3, 10.37, true)]
  [InlineData(342, 35, 110, 10.37, false)]
  public void A_picture_locks_its_aspect_ratio_only_when_its_frame_keeps_it(double width, double height,
    double frameWidth, double frameHeight, bool locked)
  {
    // Act
    bool locks = Exporter.LocksAspect([0, 0, frameWidth, frameHeight], [width, height]);

    // Assert
    locks.ShouldBe(locked);
  }

  [Fact]
  public void A_format_scaled_by_a_thousandth_becomes_a_trailing_comma_with_its_prefix_and_suffix_quoted()
  {
    // Act
    string code = NewExporter().NumberFormat(Json("""
      { "scale": 0.001, "decimals": 1, "prefix": "$", "suffix": "B", "negative": "parentheses" }
      """));

    // Assert
    code.ShouldBe("\"$\"#,##0.0,\"B\";(\"$\"#,##0.0,\"B\")");
  }

  [Fact]
  public void A_percent_format_lets_Excels_own_percent_do_the_scaling()
  {
    // Act
    string code = NewExporter().NumberFormat(Json("""{ "scale": 100, "decimals": 0, "suffix": "%", "negative": "minus" }"""));

    // Assert
    code.ShouldBe("#,##0%");
  }

  [Fact]
  public void A_stacked_chart_becomes_one_series_per_row_with_its_color_and_the_unset_legend_is_removed()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "stacked_column", "categories": ["2024", "2025"],
        "series": [{ "name": "Space", "values": [3796, null] }], "series_colors": [{ "theme": "accent1" }],
        "gap_width": 80, "value_axis.visible": false } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    ShouldMatch(result["series_list"], """
      [{ "index": 0, "name": "Space", "chart_type": "stacked_column",
        "default_data_point": { "fill": { "scheme": "accent1" } }, "delete_all_data_labels": true }]
      """);
    ShouldMatch(result["chart_data"], """{ "0:2024": { "0:Space": 3796 }, "1:2025": {} }""");
    ShouldMatch(result["primary_value_axis"]!["is_deleted"], "true");
    ShouldMatch(result["remove_chart_legend"], "true");
    ShouldMatch(result["gap_width"], "80");
  }

  [Fact]
  public void A_stacked_bar_chart_sends_its_categories_last_first_so_PowerPoint_draws_the_first_on_top()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "stacked_bar", "categories": ["2024", "2025"],
        "series": [{ "name": "Space", "values": [3796, 4086] }, { "name": "AI", "values": [2620, 3201] }] } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    result["series_list"]!.AsArray().Select(entry => (string)entry!["name"]!).ShouldBe(["Space", "AI"]);
    ShouldMatch(result["chart_data"], """
      { "0:2025": { "0:Space": 4086, "1:AI": 3201 }, "1:2024": { "0:Space": 3796, "1:AI": 2620 } }
      """);
  }

  [Fact]
  public void A_clustered_bar_chart_also_sends_its_series_last_first_keeping_each_series_color()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "clustered_bar", "categories": ["Operating"],
        "series": [{ "name": "2024", "values": [5776] }, { "name": "2025", "values": [6785] }],
        "series_colors": [{ "theme": "accent1" }, { "theme": "accent2" }] } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    ShouldMatch(new JsonArray(result["series_list"]!.AsArray().Select(entry => (JsonNode?)new JsonArray(
      entry!["index"]!.DeepClone(), entry["name"]!.DeepClone(), entry["default_data_point"]!["fill"]!.DeepClone()))
      .ToArray()), """[[0, "2025", { "scheme": "accent2" }], [1, "2024", { "scheme": "accent1" }]]""");
    ShouldMatch(result["chart_data"], """{ "0:Operating": { "0:2025": 6785, "1:2024": 5776 } }""");
  }

  [Fact]
  public void A_points_own_color_goes_as_a_data_point_at_its_category_as_sent()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "clustered_bar", "categories": ["2024", "2025", "2026"],
        "series": [{ "name": "Space", "values": [3796, 4086, 4400],
          "points": { "points.2024.color": { "theme": "accent3" } } }],
        "series_colors": [{ "theme": "accent1" }] } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    JsonNode series = result["series_list"]![0]!;
    ShouldMatch(series["default_data_point"], """{ "fill": { "scheme": "accent1" } }""");
    ShouldMatch(series["data_points"], """[{ "index": 2, "fill": { "scheme": "accent3" } }]""");
  }

  [Fact]
  public void A_date_axis_goes_as_a_timeline_with_its_pattern_as_its_number_format_and_a_fixed_step_as_its_units()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "line", "categories": ["2026-01-02", "2026-01-05"],
        "series": [{ "name": "S&P", "values": [100, 101.5] }], "category_axis.format": "day",
        "category_axis.step": "7 days", "value_axis.step": 5, "data_labels.show": "value",
        "data_labels.bold": true } }
      """);
    Exporter exporter = NewExporter();
    exporter.Formats = Json("""{ "day": { "date": "MMM-D-'YY" } }""").AsObject();

    // Act
    JsonObject result = exporter.Chart(item);

    // Assert
    ShouldMatch(result["category_axis_type"], "\"timeline\"");
    ShouldMatch(result["primary_category_axis"], """
      { "labels_text_style": {}, "label_position": "low", "number_format": "mmm\\-d\\-\\'yy", "units": 7,
        "major_time_unit": "days" }
      """);
    ShouldMatch(result["primary_value_axis"]!["units"], "5");
    result["primary_value_axis"]!.AsObject().ContainsKey("is_units_auto").ShouldBeFalse();
    ShouldMatch(result["series_list"]![0]!["default_data_label"]!["default_text_style_for_all_runs"]!["is_bold"],
      "true");
  }

  [Fact]
  public void A_lines_points_go_as_markers_of_their_own_over_the_series_and_leave_its_line_alone()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "line", "categories": ["2024", "2025"],
        "series": [{ "name": "S", "values": [1, 4], "markers": { "markers.shape": "circle", "markers.size": 5 },
          "points": { "points.2024.color": { "theme": "accent3" }, "points.2025.markers.shape": "square",
            "points.2025.markers.fill": { "theme": "light1" } } }],
        "series_colors": [{ "theme": "accent1" }] } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    JsonNode series = result["series_list"]![0]!;
    series["data_points"].ShouldBeNull();
    ShouldMatch(series["marker_list"], """
      [{ "index": 0, "marker_type": "circle", "size": 5, "marker_shape_style": { "border_weight": 2.25,
          "border_color": { "scheme": "accent3" }, "fill": { "scheme": "accent3" } } },
        { "index": 1, "marker_type": "square", "size": 5, "marker_shape_style": { "border_weight": 2.25,
          "border_color": { "scheme": "accent1" }, "fill": { "scheme": "lt1" } } }]
      """);
  }

  [Fact]
  public void A_points_own_labels_go_as_data_labels_by_index_and_a_hidden_one_shows_nothing()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "clustered_column", "categories": ["2023", "2024", "2025"],
        "series": [{ "name": "S", "values": [1, 2, 4],
          "labels": { "data_labels.show": "value", "data_labels.position": "center",
            "data_labels.color": { "theme": "light1" } },
          "points": { "points.2023.data_labels.show": "none", "points.2025.data_labels.show": "value",
            "points.2025.data_labels.position": "outside", "points.2025.data_labels.color": { "theme": "accent3" },
            "points.2025.data_labels.bold": true } }],
        "text.color": { "theme": "dark1" } } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    JsonNode series = result["series_list"]![0]!;
    ShouldMatch(series["data_labels"], """
      [{ "index": 0, "show_value": false },
        { "index": 2, "show_value": true, "dock": "outside_end", "text_component": { "paragraph_list": [{ "index": 0,
          "runs": [{ "index": 0, "text_style": { "font_color": { "scheme": "accent3" }, "is_bold": true } }] }] } }]
      """);
  }

  [Fact]
  public void A_points_label_on_a_series_without_labels_shows_alone_in_the_charts_text()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "line", "categories": ["2024", "2025"],
        "series": [{ "name": "S", "values": [1, 4], "labels": { "data_labels.show": "none" },
          "points": { "points.2025.data_labels.show": "value", "points.2025.data_labels.position": "above" } }],
        "text.font": { "theme": "body" }, "text.size": 10, "text.color": { "theme": "dark1" } } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    JsonNode series = result["series_list"]![0]!;
    ShouldMatch(series["delete_all_data_labels"], "true");
    ShouldMatch(series["data_labels"], """
      [{ "index": 1, "show_value": true, "dock": "above", "text_component": { "paragraph_list": [{ "index": 0,
          "runs": [{ "index": 0, "text_style": { "font_family": "+mn-lt", "font_size": 10,
            "font_color": { "scheme": "dk1" }, "is_bold": false } }] }] } }]
      """);
  }

  [Fact]
  public void A_reversed_value_axis_is_a_gap()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "clustered_column", "categories": ["2024"],
        "series": [{ "name": "S", "values": [4] }], "value_axis.reverse": true } }
      """);
    Exporter exporter = NewExporter();

    // Act
    exporter.Chart(item);

    // Assert
    exporter.Gaps.ShouldContain("a reversed value axis isn't exported, so it runs bottom up in PowerPoint");
  }

  [Fact]
  public void A_category_axis_without_labels_keeps_its_line_and_drops_its_labels()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "clustered_column", "categories": ["2024"],
        "series": [{ "name": "S", "values": [4] }], "category_axis.labels": false } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    ShouldMatch(result["primary_category_axis"], """{ "labels_text_style": {}, "label_position": "none" }""");
  }

  [Fact]
  public void A_series_drawn_in_no_color_loses_its_legend_entry()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "stacked_column", "categories": ["2024"],
        "series": [{ "name": "Americas", "values": [4] }, { "name": "Total", "values": [4] }], "legend": "bottom",
        "series_colors": [{ "theme": "accent1" }, { "none": true }] } }
      """);

    // Act
    JsonObject result = NewExporter().Chart(item);

    // Assert
    ShouldMatch(result["chart_legend"]!["legend_entries"], """[{ "index": 1, "delete": true }]""");
  }

  [Fact]
  public void A_lines_markers_are_outlined_in_its_color_and_filled_with_it_unless_they_name_a_fill()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "line", "categories": ["2024"],
        "series": [
          { "name": "Space", "values": [4], "type": "line", "secondary": false },
          { "name": "AI", "values": [3], "type": "line", "secondary": false, "labels": {},
            "markers": { "markers.shape": "circle", "markers.size": 7, "markers.fill": { "theme": "light1" } } }
        ],
        "series_colors": [{ "theme": "accent1" }, { "theme": "accent2" }], "markers.shape": "square",
        "markers.size": 5 } }
      """);

    // Act
    JsonArray series = NewExporter().Chart(item)["series_list"]!.AsArray();

    // Assert
    ShouldMatch(series[0]!["default_marker"], """
      { "marker_type": "square", "size": 5, "marker_shape_style": { "border_weight": 2.25,
        "border_color": { "scheme": "accent1" }, "fill": { "scheme": "accent1" } } }
      """);
    ShouldMatch(series[1]!["default_marker"], """
      { "marker_type": "circle", "size": 7, "marker_shape_style": { "border_weight": 2.25,
        "border_color": { "scheme": "accent2" }, "fill": { "scheme": "lt1" } } }
      """);
  }

  [Theory]
  [InlineData("left")]
  [InlineData("right")]
  public void A_lines_labels_left_or_right_of_its_points_dock_there(string position)
  {
    // Arrange
    JsonNode item = Json($$"""
      { "properties": { "frame": [40, 100, 430, 380], "type": "line", "categories": ["2024"],
        "series": [{ "name": "Close", "values": [4], "type": "line", "secondary": false,
          "labels": { "data_labels.show": "value", "data_labels.position": "{{position}}" } }],
        "series_colors": [{ "theme": "accent1" }] } }
      """);

    // Act
    JsonArray series = NewExporter().Chart(item)["series_list"]!.AsArray();

    // Assert
    ShouldMatch(series[0]!["default_data_label"]!["dock"], $"\"{position}\"");
  }

  [Fact]
  public void A_line_over_columns_goes_on_the_secondary_axis_and_a_total_as_a_line_in_no_color_labelled_above()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "stacked_column", "categories": ["2024"],
        "series": [
          { "name": "Americas", "values": [4], "type": "column", "secondary": false },
          { "name": "Total", "values": [4], "type": "total", "secondary": false,
            "labels": { "data_labels.show": "value", "data_labels.position": "outside", "data_labels.bold": true } },
          { "name": "Margin", "values": [0.2], "type": "line", "secondary": true,
            "labels": { "data_labels.show": "none" } }
        ],
        "legend": "bottom",
        "series_colors": [{ "theme": "accent1" }, { "theme": "accent2" }, { "theme": "accent3" }],
        "data_labels.show": "value", "data_labels.color": { "theme": "light1" }, "secondary_axis.visible": true,
        "secondary_axis.max": 1, "secondary_axis.format": "percent" } }
      """);
    Exporter exporter = NewExporter();
    exporter.Formats = Json("""{ "percent": { "scale": 100, "suffix": "%" } }""").AsObject();

    // Act
    JsonObject result = exporter.Chart(item);

    // Assert
    JsonArray series = result["series_list"]!.AsArray();
    ShouldMatch(series[0]!["chart_type"], "\"stacked_column\"");
    ShouldMatch(series[0]!["default_data_label"]!["default_text_style_for_all_runs"],
      """{ "font_color": { "scheme": "lt1" } }""");
    ShouldMatch(series[1]!["chart_type"], "\"line\"");
    ShouldMatch(series[1]!["default_data_point"], """{ "border": { "color": "transparent" } }""");
    ShouldMatch(series[1]!["default_marker"], """{ "marker_type": "none" }""");
    ShouldMatch(series[1]!["default_data_label"]!["dock"], "\"above\"");
    ShouldMatch(series[1]!["default_data_label"]!["default_text_style_for_all_runs"], """{ "is_bold": true }""");
    ShouldMatch(series[2]!["chart_type"], "\"line\"");
    ShouldMatch(series[2]!["uses_secondary_axis"], "true");
    ShouldMatch(series[2]!["delete_all_data_labels"], "true");
    ShouldMatch(series[2]!["default_data_point"], """{ "border": { "color": { "scheme": "accent3" }, "weight": 2.25 } }""");
    ShouldMatch(result["secondary_value_axis"]!["max"], "1");
    ShouldMatch(result["secondary_value_axis"]!["number_format"], "\"#,##0%;(#,##0%)\"");
    ShouldMatch(result["secondary_category_axis"], """{ "is_deleted": true }""");
    ShouldMatch(result["chart_legend"]!["legend_entries"], """[{ "index": 1, "delete": true }]""");
    exporter.Gaps.ShouldBeEmpty();
  }

  [Fact]
  public void A_waterfall_goes_as_one_series_with_its_totals_as_subtotals_and_each_bar_colored_by_its_kind()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "waterfall",
        "categories": ["Revenue", "COGS", "Gross Profit", "Rebate"], "totals": ["Revenue", "Gross Profit"],
        "series": [{ "name": "Value", "values": [500, -200, 300, 20], "type": "column", "secondary": false,
          "points": { "points.Rebate.color": { "theme": "accent6" } } }],
        "series_colors": [{ "theme": "accent1" }, { "theme": "accent2" }, { "theme": "accent3" }],
        "data_labels.show": "value", "data_labels.position": "outside", "legend": "bottom", "gap_width": 50,
        "connector_lines": false, "category_axis.visible": true, "category_axis.labels": true } }
      """);
    Exporter exporter = NewExporter();

    // Act
    JsonObject result = exporter.Chart(item);

    // Assert
    ShouldMatch(result["series_list"], """
      [{ "index": 0, "name": "Value", "chart_type": "waterfall", "subtotal_indices": [0, 2],
        "data_points": [{ "index": 0, "fill": { "scheme": "accent3" } },
          { "index": 1, "fill": { "scheme": "accent2" } }, { "index": 2, "fill": { "scheme": "accent3" } },
          { "index": 3, "fill": { "scheme": "accent6" } }],
        "default_data_label": { "show_value": true, "dock": "outside_end", "default_text_style_for_all_runs": {} } }]
      """);
    ShouldMatch(result["connector_lines"], "false");
    ShouldMatch(result["gap_width"], "50");
    ShouldMatch(result["primary_category_axis"]!["label_position"], "\"next_to\"");
    ShouldMatch(result["chart_legend"], """{ "dock": "bottom", "overlay": false, "text_style": {} }""");
    exporter.Gaps.ShouldBeEmpty();
  }

  [Fact]
  public void A_waterfall_points_own_label_goes_by_index_without_its_style_which_is_a_gap()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 430, 380], "type": "waterfall", "categories": ["Start", "End"],
        "totals": ["Start", "End"],
        "series": [{ "name": "Value", "values": [500, 500], "type": "column", "secondary": false,
          "labels": { "data_labels.show": "value", "data_labels.position": "inside" },
          "points": { "points.End.data_labels.show": "value", "points.End.data_labels.position": "outside",
            "points.End.data_labels.bold": true } }],
        "data_labels.show": "value", "data_labels.position": "inside" } }
      """);
    Exporter exporter = NewExporter();

    // Act
    JsonObject result = exporter.Chart(item);

    // Assert
    ShouldMatch(result["series_list"]![0]!["data_labels"], """
      [{ "index": 1, "show_value": true, "dock": "outside_end" }]
      """);
    exporter.Gaps.ShouldBe(["a waterfall point's label in a color or boldness of its own exports in its series', " +
      "as editide can't yet style one waterfall label"]);
  }

  [Fact]
  public void An_empty_cell_keeps_its_paragraphs_size_in_an_empty_run_so_its_row_is_as_tall_as_the_preview_draws()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 400, 300], "size": 14, "widths": [200], "heights": [null],
        "cells": [{ "row": 1, "column": 1, "rows": 1, "columns": 1, "properties": {},
          "paragraphs": [{ "level": 0, "properties": { "size": 13, "bold": true }, "runs": [] }] }] } }
      """);

    // Act
    (JsonObject element, _) = NewExporter().Table(item);

    // Assert
    JsonNode? runs = element["rows"]![0]!["cells"]![0]!["text_component"]!["paragraph_list"]![0]!["runs"];
    ShouldMatch(runs![0]!["text"], "\"\"");
    ShouldMatch(runs[0]!["text_style"]!["font_size"], "13");
    ShouldMatch(runs[0]!["text_style"]!["is_bold"], "true");
  }

  [Fact]
  public void A_tables_row_sizes_and_merges_wait_for_the_second_edit_and_its_cells_carry_their_own_borders()
  {
    // Arrange
    static string Cell(int row, int column, int rows, string properties = "{}") => $$"""
      { "row": {{row}}, "column": {{column}}, "rows": {{rows}}, "columns": 1, "properties": {{properties}},
        "paragraphs": [{ "level": 0, "properties": {}, "runs": [{ "text": "{{row}}.{{column}}", "properties": {} }] }] }
      """;
    JsonNode item = Json($$"""
      { "properties": { "frame": [40, 100, 400, 300], "header_rows": 1, "widths": [200, 200],
        "heights": [null, null, 30], "cells": [{{Cell(1, 1, 1)}}, {{Cell(1, 2, 1)}},
          {{Cell(2, 1, 2, """{ "border_bottom.color": { "theme": "dark1" }, "border_bottom.width": 2 }""")}},
          {{Cell(2, 2, 1)}}, {{Cell(3, 2, 1)}}] } }
      """);

    // Act
    (JsonObject element, JsonArray fix) = NewExporter().Table(item);

    // Assert
    element["rows"]!.AsArray().Count.ShouldBe(3);
    ShouldMatch(element["rows"]![1]!["cells"]![0]!["style"]!["borders"]!["bottom"],
      """{ "color": { "scheme": "dk1" }, "weight": 2 }""");
    ShouldMatch(element["rows"]![2]!["cells"]![0]!["text"], "\"\"");
    ShouldMatch(fix, """
      [{ "index": 0, "minimize_row_whitespace": true },
        { "index": 1, "minimize_row_whitespace": true, "cells": [{ "index": 0, "row_span": 2 }] },
        { "index": 2, "height": 30 }]
      """);
  }

  [Fact]
  public void A_cells_left_and_right_borders_go_on_its_cell()
  {
    // Arrange
    JsonNode item = Json("""
      { "properties": { "frame": [40, 100, 400, 300], "widths": [200], "heights": [null],
        "cells": [{ "row": 1, "column": 1, "rows": 1, "columns": 1,
          "properties": { "border_left.color": { "theme": "dark1" }, "border_left.width": 1,
            "border_right.color": { "theme": "dark1" }, "border_right.width": 0.75, "border_right.dash": "dot" },
          "paragraphs": [] }] } }
      """);

    // Act
    (JsonObject element, _) = NewExporter().Table(item);

    // Assert
    ShouldMatch(element["rows"]![0]!["cells"]![0]!["style"]!["borders"], """
      { "left": { "color": { "scheme": "dk1" }, "weight": 1 },
        "right": { "color": { "scheme": "dk1" }, "weight": 0.75, "dash_type": "dot" } }
      """);
  }

  [Fact]
  public void A_placeholder_a_slide_moves_off_its_layout_frame_is_moved_there()
  {
    // Arrange
    JsonNode layout = Json("""{ "properties": { "frame": [74.18, 392, 760, 100] }, "paragraphs": [{}] }""");
    JsonNode slide = Json("""{ "properties": { "frame": [81, 392, 760, 90] }, "paragraphs": [] }""");

    // Act
    JsonObject fill = Exporter.PlaceholderFill(Json("2"), slide, layout);

    // Assert
    ShouldMatch(fill, """
      { "id": 2, "text_component": { "paragraph_list": [] },
        "position": { "left": 81, "top": 392 }, "size": { "width": 760, "height": 90 } }
      """);
  }

  [Fact]
  public void A_placeholder_on_its_layout_frame_is_left_where_the_layout_puts_it()
  {
    // Arrange
    JsonNode layout = Json("""{ "properties": { "frame": [74.18, 392, 760, 100] }, "paragraphs": [{}] }""");
    JsonNode slide = Json("""{ "properties": { "frame": [74.18, 392, 760, 100] }, "paragraphs": [] }""");

    // Act
    JsonObject fill = Exporter.PlaceholderFill(Json("2"), slide, layout);

    // Assert
    ShouldMatch(fill, """{ "id": 2, "text_component": { "paragraph_list": [] } }""");
  }

  [Fact]
  public void A_deck_takes_the_presentation_size_before_the_tools_open_it()
  {
    // Arrange
    using var folder = new TempFolder();
    string deck = folder.PathTo("deck.pptx");
    using (ZipArchive zip = ZipFile.Open(deck, ZipArchiveMode.Create))
    {
      using var writer = new StreamWriter(zip.CreateEntry("ppt/presentation.xml").Open());
      writer.Write("""<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">""" +
        """<p:sldSz cx="12192000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""");
    }

    // Act
    Exporter.Resize(deck, [1088, 612]);

    // Assert
    using ZipArchive read = ZipFile.OpenRead(deck);
    string xml = new StreamReader(read.GetEntry("ppt/presentation.xml")!.Open()).ReadToEnd();
    xml.ShouldContain("""<p:sldSz cx="13817600" cy="7772400" />""");
    xml.ShouldContain("""<p:notesSz cx="6858000" cy="9144000" />""");
  }

  [Fact]
  public void A_title_drawn_over_an_element_moves_up_the_slides_order_with_the_elements_around_it()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello
      placeholders:
        subtitle: ${footer}
      elements:
        - id: band
          shape: rectangle
          frame: [0, 0, 960, 540]
        - title
        - id: note
          text: On top
          frame: [60, 400, 400, 40]
      """);
    var tools = new BlandTools
    {
      SlideRead = Json("""
        { "text_elements": [
          { "id": 2, "is_placeholder": true, "placeholder": { "role": "title" },
            "position": { "left": 60, "top": 190 } },
          { "id": 3, "is_placeholder": true, "placeholder": { "role": "body" },
            "position": { "left": 60, "top": 290 } }] }
        """)
    };

    // Act
    Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    JsonNode edit = tools.Inputs.First(call => call.Tool == "edit_slide" &&
      call.Input["slide"]?["shape_elements_to_add"] is not null).Input["slide"]!;
    ShouldMatch(edit["shape_elements_to_add"]![0]!["position"]!["z_index"], "1");
    ShouldMatch(edit["text_elements_to_add"]![0]!["position"]!["z_index"], "3");
    JsonArray fills = edit["text_elements"]!.AsArray();
    ShouldMatch(fills.Single(fill => (int)fill!["id"]! == 2)!["position"]!["z_index"], "2");
    fills.Single(fill => (int)fill!["id"]! == 3)!["position"].ShouldBeNull();
  }

  [Fact]
  public void An_exported_deck_is_titled_by_the_presentation_and_names_no_author_or_add_in()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    DateTime before = DateTime.UtcNow.AddSeconds(-1);

    // Act
    Exporter.Write(fixture.Load(), output, new BlandTools());

    // Assert
    using ZipArchive deck = ZipFile.OpenRead(output);
    XDocument core = XDocument.Load(deck.GetEntry("docProps/core.xml")!.Open());
    XNamespace dc = "http://purl.org/dc/elements/1.1/";
    XNamespace terms = "http://purl.org/dc/terms/";
    core.Root!.Element(dc + "title")!.Value.ShouldBe("Test deck");
    core.Root.Element(dc + "creator").ShouldBeNull();
    core.ToString().ShouldNotContain("lastModifiedBy");
    DateTime.Parse(core.Root.Element(terms + "created")!.Value).ToUniversalTime().ShouldBeGreaterThan(before);
    DateTime.Parse(core.Root.Element(terms + "modified")!.Value).ToUniversalTime().ShouldBeGreaterThan(before);
    deck.Entries.ShouldNotContain(entry => entry.FullName.Contains("webextension"));
  }

  [Fact]
  public void An_svg_is_drawn_at_its_own_proportions_at_four_pixels_a_point_of_its_largest_use()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("media/logo.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 24"></svg>""");
    folder.Write("media/photo.png", "not drawn");
    JsonObject deck = Json("""
      {
        "layouts": [{ "items": [{ "kind": "picture", "properties": { "picture": "media/logo.svg",
          "frame": [0, 0, 64, 24] } }] }],
        "slides": [{ "items": [
          { "kind": "picture", "properties": { "picture": "media/logo.svg", "frame": [0, 0, 128, 48] } },
          { "kind": "picture", "properties": { "picture": "media/photo.png", "frame": [0, 0, 100, 100] } }
        ] }]
      }
      """).AsObject();

    // Act
    Dictionary<string, (int Width, int Height)> sizes = Exporter.SvgSizes(deck, folder.Root);

    // Assert
    sizes.ShouldBe(new Dictionary<string, (int, int)> { [folder.PathTo("media/logo.svg")] = (512, 192) });
  }

  [Fact]
  public void An_svg_stretched_into_a_frame_is_drawn_to_cover_it_and_never_beyond_4096_pixels()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("logo.svg", """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="24"></svg>""");
    JsonObject stretched = Json("""
      { "slides": [{ "items": [{ "kind": "picture", "properties": { "picture": "logo.svg", "frame": [0, 0, 64, 64] } }]
      }] }
      """).AsObject();
    JsonObject huge = Json("""
      { "slides": [{ "items": [{ "kind": "picture", "properties": { "picture": "logo.svg", "frame": [0, 0, 6400, 2400] }
      }] }] }
      """).AsObject();

    // Act
    (int Width, int Height) cover = Exporter.SvgSizes(stretched, folder.Root).Single().Value;
    (int Width, int Height) capped = Exporter.SvgSizes(huge, folder.Root).Single().Value;

    // Assert
    cover.ShouldBe((683, 256));
    capped.ShouldBe((4096, 1536));
  }

  [Fact]
  public void A_slides_own_background_is_set_on_its_slide()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/two.yaml", "layout: content\nbackground: \"#091024\"");
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    List<JsonObject> edits = tools.Inputs.Where(call => call.Tool == "edit_slide" && call.Input["slide_index"] is not null)
      .Select(call => call.Input).ToList();
    edits.Count.ShouldBe(2);
    edits[0]["slide"]!["background"].ShouldBeNull();
    ShouldMatch(edits[1]["slide"]!["background"], """{ "color": { "rgb": "#091024" } }""");
  }


}