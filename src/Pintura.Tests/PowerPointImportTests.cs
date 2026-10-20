using System.IO.Compression;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

/// <summary>The fixture presentations as an export wrote them to a <c>.pptx</c>, with editide's reads of each, so
/// the import is tested offline. After changing a fixture, export it again with
/// <c>pintura presentations/example --export out/example.pptx</c>, import that, and copy the deck and
/// its cached reads here.</summary>
public class PowerPointImportTests
{
  private static string Fixture(string name)
  {
    return Path.Combine(LoaderTests.RepositoryRoot(), "src", "Pintura.Tests", "fixtures", "pptx", name);
  }

  private static JsonObject Reads(string name = "example")
  {
    return JsonNode.Parse(File.ReadAllText(Fixture($"{name}.reads.json")))!.AsObject();
  }

  /// <summary>A copy of the example deck with one part's XML changed.</summary>
  private static string Changed(TempFolder output, string part, Func<string, string> change)
  {
    string deck = output.PathTo("example.pptx");
    File.Copy(Fixture("example.pptx"), deck);
    using ZipArchive zip = ZipFile.Open(deck, ZipArchiveMode.Update);
    ZipArchiveEntry entry = zip.GetEntry(part)!;
    string xml;
    using (var reader = new StreamReader(entry.Open()))
    {
      xml = reader.ReadToEnd();
    }
    string changed = change(xml);
    changed.ShouldNotBe(xml);
    entry.Delete();
    using (var writer = new StreamWriter(zip.CreateEntry(part).Open()))
    {
      writer.Write(changed);
    }
    return deck;
  }

  private static (Presentation Presentation, List<string> Findings) Import(TempFolder output,
    string name = "example")
  {
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture($"{name}.reads.json")))!.AsObject();
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture($"{name}.pptx"), output.PathTo("media"));
    ImportResult result = Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    return (PresentationLoader.Load(output.PathTo("imported")), [.. mapped.Findings, .. result.Findings]);
  }

  private static Presentation Imported(TempFolder output, JsonObject reads)
  {
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    return PresentationLoader.Load(output.PathTo("imported"));
  }

  [Theory]
  [InlineData("example")]
  [InlineData("default-theme")]
  [InlineData("balance-sheet")]
  public void Each_fixtures_stored_deck_imports_as_the_fixture(string name)
  {
    // Arrange
    using var output = new TempFolder();
    Presentation original = PresentationLoader.Load(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", name));

    // Act
    (Presentation imported, _) = Import(output, name);
    DeckDiff diff = Differ.Compare(original, imported, byPosition: true);

    // Assert
    var listing = new StringWriter();
    DiffCommand.Write(diff, $"{name} after export and import", listing);
    string[] exportGaps = DiffCommand.ExportGaps;
    diff.Slides
      .Where(slide => slide.Status != "changed" || slide.Changes.Any(change => !exportGaps.Contains(change.Key)) ||
        slide.Items.Any(item => item.Status != "changed" ||
          item.Changes.Any(change => !exportGaps.Contains(change.Key))))
      .Select(slide => slide.Id)
      .ShouldBeEmpty(listing.ToString());
    // Fails once sections export, as a reminder to drop "section" from the gaps above.
    diff.Slides.SelectMany(slide => slide.Changes).Select(change => change.Key).ShouldContain("section");
  }

  [Fact]
  public void A_deck_as_exported_differs_from_its_presentation_in_nothing()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(LoaderTests.RepositoryRoot(), "presentations",
      "example"));

    // Act
    (DeckDiff diff, _) = DiffCommand.Against(original, Reads(), Fixture("example.pptx"));

    // Assert
    var listing = new StringWriter();
    DiffCommand.Write(diff, "example against its deck", listing);
    diff.Slides.ShouldBeEmpty(listing.ToString());
  }

  [Fact]
  public void Text_reworded_in_powerpoint_shows_on_its_slide_by_the_presentations_ids()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(LoaderTests.RepositoryRoot(), "presentations",
      "example"));
    JsonObject reads = Reads();
    JsonNode read = reads["slides"]![1]!["read"]!["text_elements"]![0]!;
    read["text_component"]!["paragraph_list"]![0]!["runs"]![0]!["text"] = "Financial results";

    // Act
    (DeckDiff diff, _) = DiffCommand.Against(original, reads, Fixture("example.pptx"));

    // Assert
    SlideDiff slide = diff.Slides.ShouldHaveSingleItem();
    slide.Id.ShouldBe(original.Slides[1].Id);
    ItemDiff title = slide.Items.ShouldHaveSingleItem();
    title.Id.ShouldBe("title");
    title.Changes.Single(change => change.Key == "text").After.ShouldBe("Financial results");
  }

  [Fact]
  public void A_box_added_in_powerpoint_shows_what_it_draws()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(LoaderTests.RepositoryRoot(), "presentations",
      "example"));
    JsonObject reads = Reads();
    JsonArray texts = reads["slides"]![1]!["read"]!["text_elements"]!.AsArray();
    texts.Add(JsonNode.Parse("""
      {
        "id": 40, "is_placeholder": false, "position": { "left": 600, "top": 400, "z_index": 9 },
        "size": { "height": 30, "width": 200 }, "autofit": "none", "wrap_text": true,
        "text_component": { "paragraph_list": [{ "indent_level": 0, "runs": [{ "text": "Added in PowerPoint" }] }] }
      }
      """));

    // Act
    (DeckDiff diff, _) = DiffCommand.Against(original, reads, Fixture("example.pptx"));

    // Assert
    ItemDiff added = diff.Slides.ShouldHaveSingleItem().Items.ShouldHaveSingleItem();
    added.Status.ShouldBe("added");
    added.Changes.Single(change => change.Key == "text").After.ShouldBe("Added in PowerPoint");
    added.Changes.Single(change => change.Key == "frame").After.ShouldBe("[600, 400, 200, 30]");
  }

  [Fact]
  public void A_border_the_whole_table_sets_reaches_each_cell_a_row_doesnt_override()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    JsonNode table = reads["slides"]![4]!["read"]!["table_elements"]![0]!;
    table["default_borders"] = JsonNode.Parse("""
      { "left": { "color": { "scheme": "lt1" }, "weight": 3, "dash_type": "solid" } }
      """);

    // Act
    Presentation presentation = Imported(output, reads);

    // Assert
    SceneItem item = SceneBuilder.Build(presentation, presentation.Slides[4]).Items.Single(item => item.Kind == "table");
    item.Table!.Cells.ShouldAllBe(cell => cell.Properties.ContainsKey("border_left.color"));
  }

  [Fact]
  public void A_deck_imports_with_its_slides_and_layouts()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    (Presentation presentation, List<string> findings) = Import(output);

    // Assert
    presentation.Slides.Select(slide => slide.Error?.Message).ShouldAllBe(error => error == null);
    presentation.Slides.Count.ShouldBe(6);
    presentation.Theme.Layouts.Count.ShouldBe(4);
    findings.ShouldBe(["slide 6: a callout's tail may not point where PowerPoint draws it"]);
  }

  [Fact]
  public void Elements_in_groups_nested_or_not_import_as_they_would_ungrouped()
  {
    // Arrange
    using var plain = new TempFolder();
    using var grouped = new TempFolder();
    JsonObject reads = Reads();
    JsonObject read = reads["slides"]![5]!["read"]!.AsObject();
    JsonArray shapes = read["shape_elements"]!.AsArray();
    // A group's children carry their own style in full, since the tools fold none of it into the slide's defaults.
    JsonObject Ungrouped(int id)
    {
      JsonObject shape = shapes.Single(node => (int?)node!["id"] == id)!.AsObject();
      shapes.Remove(shape);
      JsonObject style = read["shape_element_shape_style"]!.DeepClone().AsObject();
      foreach ((string key, JsonNode? value) in shape["shape_style"]?.AsObject() ?? [])
      {
        style[key] = value?.DeepClone();
      }
      shape["shape_style"] = style;
      return shape;
    }
    JsonObject brace = Ungrouped(4);
    JsonObject arrow = Ungrouped(5);
    read["group_elements"] = new JsonArray(new JsonObject
    {
      ["id"] = 90,
      ["shape_elements"] = new JsonArray(brace),
      ["group_elements"] = new JsonArray(new JsonObject { ["id"] = 91, ["shape_elements"] = new JsonArray(arrow) })
    });

    // Act
    Presentation expected = Imported(plain, Reads());
    Presentation actual = Imported(grouped, reads);
    DeckDiff diff = Differ.Compare(expected, actual, byPosition: true);

    // Assert
    var listing = new StringWriter();
    DiffCommand.Write(diff, "grouped against ungrouped", listing);
    diff.Slides.ShouldBeEmpty(listing.ToString());
  }

  [Fact]
  public void What_most_layouts_show_moves_to_the_theme_with_its_picture_copied_once()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    (Presentation presentation, _) = Import(output);

    // Assert
    presentation.Theme.Elements.Select(element => element.Id).ShouldBe(["slide-number", "picture-1", "line-1",
      "text-1"]);
    // The layouts show one logo, and the cover a white one.
    Directory.GetFiles(output.PathTo("imported/theme/media")).Length.ShouldBe(2);
    presentation.Theme.Layouts["cover"].ThemeElements.ShouldBeFalse();
  }

  [Fact]
  public void A_placeholder_of_one_line_stays_a_placeholder_named_for_its_prompt()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    (Presentation presentation, _) = Import(output);

    // Assert
    presentation.Theme.Layouts["cover"].Placeholders.Keys.ShouldBe(["kicker", "subtitle"]);
    presentation.Theme.Layouts["comparison"].Placeholders["right_heading"].Style.ShouldBe("left_heading");
  }

  [Fact]
  public void Axis_bounds_come_back_only_where_they_differ_from_the_automatic_scale()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(LoaderTests.RepositoryRoot(), "presentations",
      "example"));
    Scale auto = AxisScale.Of(original.Charts["revenue-by-segment"], false, null, null, null);
    string Imported(double max)
    {
      using var output = new TempFolder();
      JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
      JsonObject axis = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["primary_value_axis"]!.AsObject();
      axis["min"] = auto.Min;
      axis["max"] = max;
      axis["units"] = auto.Step;
      foreach (string auto in new[] { "is_min_auto", "is_max_auto", "is_units_auto" })
      {
        axis[auto] = false;
      }
      PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
      Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
      return File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());
    }

    // Act
    string same = Imported(auto.Max);
    string higher = Imported(auto.Max + auto.Step * 2);

    // Assert
    same.ShouldNotContain("min:");
    same.ShouldNotContain("max:");
    same.ShouldNotContain("step:");
    higher.ShouldContain($"max: {auto.Max + auto.Step * 2}");
    higher.ShouldContain($"step: {auto.Step}");
    higher.ShouldNotContain("min:");
  }

  [Fact]
  public void A_category_axis_with_no_label_position_comes_back_without_labels()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    reads["slides"]![3]!["read"]!["chart_elements"]![0]!["primary_category_axis"]!["label_position"] = "none";

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    chart.ShouldContain("labels: false");
  }

  [Fact]
  public void A_value_axis_with_no_label_position_comes_back_hidden_keeping_its_scale()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonObject axis = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["primary_value_axis"]!.AsObject();
    axis["is_deleted"] = false;
    axis["label_position"] = "none";
    axis["max"] = 4200.0;
    axis["is_max_auto"] = false;

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    chart.ShouldContain("value_axis: { visible: false, max: 4200");
  }

  [Fact]
  public void A_lines_own_markers_come_back_as_its_points()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonNode ai = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["series_list"]![2]!;
    ai["chart_type"] = "line";
    ai["default_data_point"] = JsonNode.Parse("""{ "border": { "color": { "scheme": "accent3" } } }""");
    ai["default_marker"] = JsonNode.Parse("""{ "marker_type": "circle", "size": 5 }""");
    ai["marker_list"] = JsonNode.Parse("""
      [{ "index": 2, "marker_type": "square", "size": 9,
        "marker_shape_style": { "border_color": { "scheme": "accent2" }, "fill": { "scheme": "lt1" } } }]
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    chart.ShouldContain("2025: { color: accent2, markers: { shape: square, size: 9, fill: light1 } }");
  }

  [Fact]
  public void A_series_own_labels_come_back_as_its_points()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonNode ai = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["series_list"]![2]!;
    ai["data_label_list"] = JsonNode.Parse("""
      [{ "index": 0, "is_deleted": true, "value": "$2,961.0B" },
        { "index": 1, "value": "$2,620.0B" },
        { "index": 2, "dock": "inside_end", "value": "$3,201.0B", "text_component": { "paragraph_list": [{ "index": 0,
          "runs": [{ "index": 0, "text_style": { "font_color": { "scheme": "accent2" }, "is_bold": true } }] }] } }]
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    chart.ShouldContain("2023: { data_labels: { show: none } }");
    chart.ShouldContain("2025: { data_labels: { position: inside, color: accent2, bold: true } }");
    chart.ShouldNotContain("2024:");
  }

  [Fact]
  public void A_points_labels_come_back_under_a_category_with_dots_in_its_name()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonObject chartRead = reads["slides"]![3]!["read"]!["chart_elements"]![0]!.AsObject();
    JsonObject data = chartRead["chart_data"]!.AsObject();
    JsonNode values = data["2:2025"]!;
    data.Remove("2:2025");
    data["2:GPT-4.1-mini"] = values;
    chartRead["series_list"]![2]!["data_label_list"] = JsonNode.Parse("""
      [{ "index": 2, "dock": "inside_end", "value": "$3,201.0B" }]
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    ImportResult result = Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    chart.ShouldContain("GPT-4.1-mini: { data_labels: { position: inside } }");
  }

  [Fact]
  public void A_points_color_comes_back_under_a_category_named_like_a_points_key()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonObject chartRead = reads["slides"]![3]!["read"]!["chart_elements"]![0]!.AsObject();
    JsonObject data = chartRead["chart_data"]!.AsObject();
    JsonNode values = data["2:2025"]!;
    data.Remove("2:2025");
    data["2:v1.markers.x"] = values;
    chartRead["series_list"]![2]!["data_point_list"] = JsonNode.Parse("""
      [{ "index": 2, "fill": { "scheme": "accent1" } }]
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    ImportResult result = Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    chart.ShouldContain("v1.markers.x: { color: accent1 }");
  }

  [Fact]
  public void An_automatic_axis_is_fixed_where_PowerPoint_scales_it_otherwise_than_Pintura()
  {
    // Arrange
    JsonObject Properties(string type) => JsonNode.Parse($$"""
      { "type": "{{type}}", "value_axis.visible": false, "categories": ["Decision"], "series": [
        { "name": "A", "values": [830], "type": "bar" }, { "name": "B", "values": [24.5], "type": "bar" },
        { "name": "C", "values": [66.18], "type": "bar" }] }
      """)!.AsObject();
    JsonObject lifted = Properties("stacked_bar");
    JsonObject zero = Properties("clustered_bar");

    // Act
    PowerPointMapping.FixAxes(lifted, "stacked_bar");
    PowerPointMapping.FixAxes(zero, "clustered_bar");

    // Assert
    ((double?)lifted["value_axis.min"]).ShouldBe(780);
    ((double?)lifted["value_axis.max"]).ShouldBe(940);
    lifted.ContainsKey("value_axis.step").ShouldBeFalse();
    zero.ContainsKey("value_axis.min").ShouldBeFalse();
    zero.ContainsKey("value_axis.max").ShouldBeFalse();
    zero.ContainsKey("value_axis.step").ShouldBeFalse();
  }

  [Fact]
  public void A_table_reads_its_numbers_as_values_under_formats_that_spell_them_back()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    Import(output);

    // Assert
    string yaml = File.ReadAllText(output.PathTo("imported/tables/revenue-rose-54-in-h1.yaml"));
    yaml.ShouldContain("- { width: 160, align: right, format: percent }");
    yaml.ShouldContain("1: { bold: true, border_bottom: { color: dark1, width: 1 } }");
    yaml.ShouldNotContain("cells:");
    File.ReadAllText(output.PathTo("imported/tables/revenue-rose-54-in-h1.csv"))
      .ShouldContain("Revenue,Space,1611,1581,-0.02");
    File.ReadAllText(output.PathTo("imported/theme/styles.yaml"))
      .ShouldContain("number: { decimals: 0, separator: true, negative: parentheses, negative_style: negative }");
  }

  [Fact]
  public void A_rule_between_two_columns_reads_once_as_the_right_border_of_the_cell_left_of_it()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonNode row = reads["slides"]![4]!["read"]!["table_elements"]![0]!["rows"]![2]!;
    JsonNode Rule() => JsonNode.Parse("""{ "color": { "scheme": "dk1" }, "weight": 0.75, "dash_type": "solid" }""")!;
    row["cells"]![1]!["borders"] = new JsonObject { ["left"] = Rule(), ["right"] = Rule() };
    row["cells"]![2]!["borders"] = new JsonObject { ["left"] = Rule() };

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    mapped.Findings.ShouldNotContain(finding => finding.Contains("borders are left out"));
    JsonNode table = mapped.Deck["slides"]![4]!["items"]!.AsArray().First(item => (string?)item!["kind"] == "table")!;
    JsonObject Properties(int column) => table["properties"]!["cells"]!.AsArray()
      .First(cell => (int)cell!["row"]! == 3 && (int)cell["column"]! == column)!["properties"]!.AsObject();
    ((double?)Properties(2)["border_left.width"]).ShouldBe(0.75);
    ((double?)Properties(2)["border_right.width"]).ShouldBe(0.75);
    Properties(3).ContainsKey("border_left.width").ShouldBeFalse();
  }

  [Fact]
  public void A_table_row_taller_than_its_content_keeps_its_height()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    // Content height includes the cell's insets, so 1.6pt of slack is a height PowerPoint was given.
    reads["slides"]![4]!["read"]!["table_elements"]![0]!["rows"]![2]!["height"] = 28.0;

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    JsonNode table = mapped.Deck["slides"]![4]!["items"]!.AsArray().First(item => (string?)item!["kind"] == "table")!;
    table["properties"]!["heights"]!.AsArray().Select(height => (double?)height).ShouldBe(
      [null, null, 28, null, null, null, null, null, null]);
  }

  [Fact]
  public void Series_without_a_name_or_sharing_one_each_take_their_own()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonArray series = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["series_list"]!.AsArray();
    series[0]!["name"] = "";
    series[1]!["name"] = "";
    series[2]!["name"] = "Series 1";

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    JsonNode chart = mapped.Deck["slides"]![3]!["items"]!.AsArray().First(item => (string?)item!["kind"] == "chart")!;
    chart["properties"]!["series"]!.AsArray().Select(entry => (string)entry!["name"]!)
      .ShouldBe(["Series 1", "Series 2", "Series 1 2"]);
  }

  [Fact]
  public void A_legend_in_a_corner_moves_to_the_right_with_a_finding()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    reads["slides"]![3]!["read"]!["chart_elements"]![0]!["legend"]!["dock"] = "top_right";

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    JsonNode chart = mapped.Deck["slides"]![3]!["items"]!.AsArray().First(item => (string?)item!["kind"] == "chart")!;
    ((string?)chart["properties"]!["legend"]).ShouldBe("right");
    mapped.Findings.ShouldContain(finding => finding.Contains("a legend at top right moves to the right"));
  }

  [Fact]
  public void Labels_at_a_position_their_chart_cant_place_take_its_default_with_a_finding()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    foreach (JsonNode? series in reads["slides"]![3]!["read"]!["chart_elements"]![0]!["series_list"]!.AsArray())
    {
      series!["default_data_label"]!["dock"] = "outside_end";
    }

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    JsonNode chart = mapped.Deck["slides"]![3]!["items"]!.AsArray().First(item => (string?)item!["kind"] == "chart")!;
    chart.ToJsonString().ShouldNotContain("outside");
    mapped.Findings.ShouldContain(finding => finding.Contains("labels outside on a stacked column"));
  }

  [Fact]
  public void A_total_series_labelled_outside_its_stacked_column_takes_no_finding_about_its_position()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonObject chartRead = reads["slides"]![3]!["read"]!["chart_elements"]![0]!.AsObject();
    JsonArray series = chartRead["series_list"]!.AsArray();
    JsonObject total = series[2]!.DeepClone().AsObject();
    total["index"] = 3;
    total["order"] = 3;
    total["name"] = "Total";
    total["default_data_point"] = JsonNode.Parse("""{ "fill": "transparent" }""");
    total["default_data_label"]!["dock"] = "outside_end";
    total.Remove("data_label_list");
    total.Remove("data_point_list");
    series.Add(total);
    foreach ((string _, JsonNode? values) in chartRead["chart_data"]!.AsObject())
    {
      values!["3:Total"] = 1;
    }

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    mapped.Deck.ToJsonString().ShouldContain("\"total\"");
    mapped.Findings.ShouldNotContain(finding => finding.Contains("take its default position"));
  }

  [Fact]
  public void A_point_labelled_where_its_chart_cant_place_it_takes_the_charts_default_rather_than_its_series()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = JsonNode.Parse(File.ReadAllText(Fixture("example.reads.json")))!.AsObject();
    JsonObject series = reads["slides"]![3]!["read"]!["chart_elements"]![0]!["series_list"]![2]!.AsObject();
    series["default_data_label"]!["dock"] = "inside_end";
    series["data_label_list"] = JsonNode.Parse("""
      [{ "index": 2, "dock": "outside_end", "show_value": true }]
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    ImportResult result = Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));
    string chart = File.ReadAllText(Directory.GetFiles(output.PathTo("imported/charts"), "*.yaml").Single());

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    chart.ShouldContain("2025: { data_labels: { position: center } }");
  }

  [Theory]
  [InlineData("(21,162)", -21162, "{\"decimals\":0,\"separator\":true,\"negative\":\"parentheses\"}")]
  [InlineData("($21)", -21, "{\"decimals\":0,\"separator\":true,\"prefix\":\"$\",\"negative\":\"parentheses\"}")]
  [InlineData("45.2%", 0.452, "{\"scale\":100,\"decimals\":1,\"separator\":true,\"suffix\":\"%\",\"negative\":\"parentheses\"}")]
  [InlineData("-3.5x", -3.5, "{\"decimals\":1,\"separator\":true,\"suffix\":\"x\",\"negative\":\"minus\"}")]
  public void A_spelled_number_reads_as_its_value_and_the_format_that_spells_it(string text, double value,
    string format)
  {
    // Act
    (double Value, JsonObject Format)? read = PowerPointMapping.ReadNumber(text);

    // Assert
    read.ShouldNotBeNull();
    read.Value.Value.ShouldBe(value, 1e-9);
    read.Value.Format.ToJsonString().ShouldBe(format);
  }

  [Theory]
  [InlineData("(6.4)ppts")]
  [InlineData("N.M.")]
  [InlineData("Q2 2025")]
  [InlineData("1Password")]
  [InlineData("34-50")]
  [InlineData("")]
  public void Text_that_only_looks_like_a_number_stays_text(string text)
  {
    // Act
    (double Value, JsonObject Format)? read = PowerPointMapping.ReadNumber(text);

    // Assert
    read.ShouldBeNull();
  }

  [Theory]
  [InlineData("mmm\\-d\\-\\'yy", "MMM-D-'YY")]
  [InlineData("mmmm yyyy", "MMMM YYYY")]
  [InlineData("dd/mm/yyyy", "DD/MM/YYYY")]
  [InlineData("h:mm", null)]
  [InlineData("\"Day \"d", null)]
  [InlineData("General", null)]
  public void An_excel_date_code_reads_as_a_date_pattern_unless_it_keeps_a_time_or_a_literal_letter(string code,
    string? pattern)
  {
    // Act
    string? read = PowerPointMapping.DatePattern(code);

    // Assert
    read.ShouldBe(pattern);
  }

  [Fact]
  public void A_list_keeps_its_numbering_and_a_paragraph_its_own_bullet()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    Import(output);

    // Assert
    string slide = File.ReadAllText(output.PathTo("imported/slides/connectivity-funds-the-build-out.yaml"));
    slide.ShouldContain("2. Research and development of $5.1B, from $0.2B in 2023");
    File.ReadAllText(output.PathTo("imported/theme/styles.yaml"))
      .ShouldContain("levels: { 1: { bullet: –, indent: 36, hanging: 18 } }");
  }

  [Fact]
  public void A_line_break_inside_a_paragraph_imports_as_a_space()
  {
    // Arrange
    using var output = new TempFolder();
    string deck = Changed(output, "ppt/slides/slide2.xml", xml => xml.Replace(
      "<a:r><a:rPr /><a:t>Q2 2026 Board Update</a:t></a:r>",
      "<a:r><a:rPr /><a:t>Q2 2026</a:t></a:r><a:br><a:rPr /></a:br><a:r><a:rPr /><a:t>Board Update</a:t></a:r>"));
    JsonObject reads = Reads();
    // The read tools leave the break out, splitting the paragraph's runs around it.
    JsonArray runs = TitleRuns(reads);
    runs[0]!["text"] = "Q2 2026";
    runs.Add(new JsonObject { ["text"] = "Board Update" });

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, deck, output.PathTo("media"));

    // Assert
    JsonNode title = mapped.Deck["slides"]![0]!["items"]!.AsArray().First(item => (string?)item!["id"] == "title")!;
    title["paragraphs"]!.AsArray().SelectMany(paragraph => paragraph!["runs"]!.AsArray())
      .Select(run => (string?)run!["text"]).ShouldBe(["Q2 2026 Board Update"]);
    mapped.Findings.ShouldContain("slide 1: a line break inside a paragraph is kept as a space");
  }

  [Fact]
  public void A_carriage_return_or_line_separator_in_a_run_imports_as_a_space()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    TitleRuns(reads)[0]!["text"] = "Q2 2026\rBoard\u2028Update";

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck.ToJsonString(), mapped.Media, output.PathTo("imported"));

    // Assert
    File.ReadAllText(output.PathTo("imported/slides/q2-2026-board-update.yaml"))
      .ShouldContain("title: Q2 2026 Board Update\n");
  }

  private static JsonArray TitleRuns(JsonObject reads)
  {
    return reads["slides"]![0]!["read"]!["text_elements"]![0]!["text_component"]!["paragraph_list"]![0]!["runs"]!
      .AsArray();
  }


}