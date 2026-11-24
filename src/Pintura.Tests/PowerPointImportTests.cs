using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

internal static class PowerPointFixtures
{
  private static readonly ConcurrentDictionary<string, JsonObject> CachedReads =
    new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);

  public static string Fixture(string name)
  {
    return Path.Combine(Repository.Root, "src", "Pintura.Tests", "fixtures", "pptx", name);
  }

  public static JsonObject Reads(string name = "example")
  {
    JsonObject parsed = CachedReads.GetOrAdd(name, key =>
      JsonNode.Parse(File.ReadAllText(Fixture($"{key}.reads.json")))!.AsObject());
    return parsed.DeepClone().AsObject();
  }

  /// <summary>A copy of the example deck with one part's XML changed.</summary>
  public static string Changed(TempFolder output, string part, Func<string, string> change)
  {
    return Changed(output, (part, change));
  }

  /// <summary>A copy of the example deck with each part's XML changed.</summary>
  public static string Changed(TempFolder output, params (string Part, Func<string, string> Change)[] changes)
  {
    string deck = output.PathTo("example.pptx");
    File.Copy(Fixture("example.pptx"), deck);
    using ZipArchive zip = ZipFile.Open(deck, ZipArchiveMode.Update);
    foreach ((string part, Func<string, string> change) in changes)
    {
      ZipArchiveEntry entry = zip.GetEntry(part)!;
      string xml;
      using (var reader = new StreamReader(entry.Open()))
      {
        xml = reader.ReadToEnd();
      }
      string changed = change(xml);
      changed.ShouldNotBe(xml);
      entry.Delete();
      using var writer = new StreamWriter(zip.CreateEntry(part).Open());
      writer.Write(changed);
    }
    return deck;
  }

  public static (Presentation Presentation, List<string> Findings) Import(TempFolder output, string name = "example")
  {
    JsonObject reads = Reads(name);
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture($"{name}.pptx"), output.PathTo("media"));
    ImportResult result = Importer.Import(mapped.Deck, mapped.Media, output.PathTo("imported"));
    return (PresentationLoader.Load(output.PathTo("imported")), [.. mapped.Findings, .. result.Findings]);
  }

  public static Presentation Imported(TempFolder output, JsonObject reads)
  {
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck, mapped.Media, output.PathTo("imported"));
    return PresentationLoader.Load(output.PathTo("imported"));
  }


}

/// <summary>The fixture presentations as an export wrote them to a <c>.pptx</c>, with editide's reads of each, so
/// the import is tested offline. After changing a fixture, export it again with
/// <c>pintura presentations/example --export out/example.pptx</c>, import that, and copy the deck and
/// its cached reads here.</summary>
public class PowerPointImportTests
{
  private static string Fixture(string name) => PowerPointFixtures.Fixture(name);
  private static JsonObject Reads(string name = "example") => PowerPointFixtures.Reads(name);
  private static string Changed(TempFolder output, string part, Func<string, string> change) =>
    PowerPointFixtures.Changed(output, part, change);
  private static string Changed(TempFolder output, params (string Part, Func<string, string> Change)[] changes) =>
    PowerPointFixtures.Changed(output, changes);
  private static (Presentation Presentation, List<string> Findings) Import(TempFolder output, string name = "example") =>
    PowerPointFixtures.Import(output, name);
  private static Presentation Imported(TempFolder output, JsonObject reads) =>
    PowerPointFixtures.Imported(output, reads);

  [Theory]
  [InlineData("example")]
  [InlineData("default-theme")]
  [InlineData("balance-sheet")]
  public void Each_fixtures_stored_deck_imports_as_the_fixture(string name)
  {
    // Arrange
    using var output = new TempFolder();
    Presentation original = PresentationLoader.Load(Path.Combine(Repository.Root, "presentations", name));

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
  }

  [Fact]
  public void A_deck_as_exported_differs_from_its_presentation_in_nothing()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(Repository.Root, "presentations",
      "example"));

    // Act
    (DeckDiff diff, _) = DiffCommand.Against(original, Reads(), Fixture("example.pptx"));

    // Assert
    var listing = new StringWriter();
    DiffCommand.Write(diff, "example against its deck", listing);
    diff.Slides.ShouldBeEmpty(listing.ToString());
  }

  [Fact]
  public void A_leading_default_section_reads_as_the_slides_before_the_first_section()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(Reads(), Fixture("example.pptx"), output.PathTo("media"));

    // Assert
    mapped.Deck["slides"]!.AsArray().Select(slide => (string?)slide!["section"]).ShouldBe([null,
      "Financial performance", "Financial performance", "Financial performance", "Financial performance",
      "Capital allocation"]);
  }

  [Fact]
  public void Text_reworded_in_powerpoint_shows_on_its_slide_by_the_presentations_ids()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(Repository.Root, "presentations",
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
    Presentation original = PresentationLoader.Load(Path.Combine(Repository.Root, "presentations",
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
    SceneItem item = SceneBuilder.Build(presentation, presentation.Slides[4]).Items
      .Single(item => item.Kind == "table");
    item.Table!.Cells.ShouldAllBe(cell => cell.Properties.ContainsKey("border_left.color"));
  }

  [Fact]
  public void The_overview_counts_each_slides_changes_and_slides_narrow_the_detail()
  {
    // Arrange
    Presentation original = PresentationLoader.Load(Path.Combine(Repository.Root, "presentations",
      "example"));
    JsonObject reads = Reads();
    JsonNode read = reads["slides"]![1]!["read"]!["text_elements"]![0]!;
    read["text_component"]!["paragraph_list"]![0]!["runs"]![0]!["text"] = "Financial results";
    read["position"]!["left"] = 80.0;
    (DeckDiff diff, _) = DiffCommand.Against(original, reads, Fixture("example.pptx"));
    var overview = new StringWriter();
    var detail = new StringWriter();

    // Act
    DiffCommand.Overview(diff, "example", overview);
    DiffCommand.Write(DiffCommand.Only(diff, original, SlideSelection.Parse("3")), "example", detail);

    // Assert
    overview.ToString().ShouldContain($" 2 {original.Slides[1].Id}  1 reworded, 1 moved");
    detail.ToString().ShouldContain("no changes");
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
  public void An_arc_imports_with_its_frame_its_angles_in_degrees_and_its_arrowheads()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    JsonObject read = reads["slides"]![5]!["read"]!.AsObject();
    read["shape_elements"]!.AsArray().Add(JsonNode.Parse("""
      { "id": 77, "position": { "left": 100, "top": 120, "z_index": 40 }, "size": { "width": 300, "height": 300 },
        "shape_type": "arc", "shape_adjustments": { "start_angle": 0.75, "end_angle": 0.125 },
        "shape_style": { "border_color": { "scheme": "accent1" }, "border_weight": 2, "fill": "transparent",
          "head_arrow": "none", "tail_arrow": "triangle" } }
      """));

    // Act
    Presentation presentation = Imported(output, reads);

    // Assert
    Element arc = presentation.Slides.SelectMany(slide => slide.Elements).Single(element => element.Kind == "arc");
    arc.Frame!.Rect.ShouldBe([100, 120, 300, 300]);
    arc.Arc.ShouldBe([270, 45]);
    arc.Start.ShouldBeNull();
    arc.End.ShouldBe("triangle");
    arc.Stroke!.Width.ShouldBe(2);
  }

  [Fact]
  public void A_slant_imports_in_points_and_the_default_slant_states_none()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    JsonArray shapes = reads["slides"]![5]!["read"]!["shape_elements"]!.AsArray();
    foreach ((int id, string type, double slant) in new[]
      { (77, "parallelogram", 0.25), (78, "trapezoid", 0.5), (79, "parallelogram", 0.125) })
    {
      shapes.Add(JsonNode.Parse($$"""
        { "id": {{id}}, "position": { "left": 100, "top": {{id}}, "z_index": {{id}} },
          "size": { "width": 200, "height": 100 }, "shape_type": "{{type}}", "shape_adjustments": { "slant": {{slant}} },
          "shape_style": { "fill": { "scheme": "accent1" } } }
        """));
    }

    // Act
    Presentation presentation = Imported(output, reads);

    // Assert
    Element[] slanted = presentation.Slides.SelectMany(slide => slide.Elements)
      .Where(element => element.Shape is "parallelogram" or "trapezoid").ToArray();
    slanted.Select(element => element.Adjust?["slant"]).ShouldBe([50, 50, null]);
  }

  [Fact]
  public void Each_layout_keeps_the_shapes_it_shows_with_a_picture_copied_once()
  {
    // Arrange
    using var output = new TempFolder();

    // Act
    (Presentation presentation, _) = Import(output);

    // Assert
    presentation.Theme.Layouts["content"].Elements.Select(element => element.Id).ShouldBe(["slide-number",
      "picture-1", "line-1", "text-1"]);
    // The layouts show one logo, and the cover a white one.
    Directory.GetFiles(output.PathTo("imported/theme/media")).Length.ShouldBe(2);
  }

  [Fact]
  public void A_deck_takes_the_layouts_its_slides_use_and_a_theme_alone_takes_them_all()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    JsonObject unused = reads["layouts"]![1]!.DeepClone().AsObject();
    (unused["id"], unused["name"], unused["slides"]) = (99, "Unused", new JsonArray());
    reads["layouts"]!.AsArray().Add(unused);
    static List<string> Layouts(PowerPointDeck mapped) =>
      mapped.Deck["layouts"]!.AsArray().Select(layout => (string)layout!["id"]!).ToList();

    // Act
    PowerPointDeck deck = PowerPointMapping.Map(reads.DeepClone().AsObject(), Fixture("example.pptx"),
      output.PathTo("deck"));
    PowerPointDeck theme = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("theme"),
      wholeTheme: true);

    // Assert
    Layouts(deck).ShouldBe(["comparison", "content", "cover", "section"]);
    Layouts(theme).ShouldBe(["comparison", "content", "cover", "section", "unused"]);
  }

  [Fact]
  public void The_masters_shapes_go_on_every_layout_behind_its_own()
  {
    // Arrange
    using var output = new TempFolder();
    string deck = Changed(output,
      ("ppt/slideMasters/slideMaster1.xml", xml => xml.Replace("</p:spTree>", """
        <p:pic><p:nvPicPr><p:cNvPr id="901" name="Master logo" /><p:cNvPicPr /><p:nvPr /></p:nvPicPr><p:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="RMasterLogo" /><a:stretch><a:fillRect /></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x="0" y="0" /><a:ext cx="812800" cy="304800" /></a:xfrm><a:prstGeom prst="rect" /></p:spPr></p:pic></p:spTree>
        """)),
      ("ppt/slideMasters/_rels/slideMaster1.xml.rels", xml => xml.Replace("</Relationships>",
        "<Relationship Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" " +
        "Target=\"/ppt/media/image2.png\" Id=\"RMasterLogo\" /></Relationships>")));
    JsonObject reads = Reads();
    JsonObject master = reads["master"]!.AsObject();
    master["shape_elements"] = JsonNode.Parse("""
      [{ "id": 900, "name": "Master band", "is_placeholder": false, "position": { "left": 0, "top": 0, "z_index": 5 },
         "size": { "height": 20, "width": 960 },
         "shape_style": { "border_color": "transparent", "border_weight": 0, "fill": { "scheme": "accent2" } } }]
      """);
    master["picture_elements"] = JsonNode.Parse("""
      [{ "id": 901, "position": { "left": 0, "top": 0, "z_index": 6 }, "size": { "height": 24, "width": 64 } }]
      """);
    foreach (JsonNode? layout in reads["layouts"]!.AsArray())
    {
      JsonObject read = layout!["read"]!.AsObject();
      JsonArray shapes = read["shape_elements"] as JsonArray ?? (JsonArray)(read["shape_elements"] = new JsonArray());
      shapes.Insert(0, JsonNode.Parse("""
        { "name": "Master band", "lock_source": "master", "is_placeholder": false,
          "position": { "left": 0, "top": 0, "z_index": -1 }, "size": { "height": 20, "width": 960 } }
        """));
    }

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, deck, output.PathTo("media"));
    Importer.Import(mapped.Deck, mapped.Media, output.PathTo("imported"));
    Presentation presentation = PresentationLoader.Load(output.PathTo("imported"));

    // Assert
    foreach (Layout layout in presentation.Theme.Layouts.Values)
    {
      List<Element> drawn = layout.Elements.Take(2).ToList();
      drawn.Select(element => element.Kind).ShouldBe(["shape", "picture"], layout.Id);
      drawn[0].Frame!.Rect.ShouldBe([0, 0, 960, 20]);
      layout.Elements.Count(element => element.Kind == "shape" && element.Frame!.Rect[3] == 20).ShouldBe(1);
    }
    mapped.Findings.ShouldNotContain(finding => finding.Contains("picture is left out"));
  }

  [Fact]
  public void A_pictures_border_and_shadow_come_back_and_an_inner_shadow_or_a_glow_with_a_finding()
  {
    // Arrange
    using var output = new TempFolder();
    JsonObject reads = Reads();
    foreach (JsonNode? layout in reads["layouts"]!.AsArray().Where(layout => (string?)layout!["name"] != "Cover"))
    {
      foreach (JsonNode? picture in layout!["read"]!["picture_elements"]?.AsArray() ?? [])
      {
        picture!["shape_style"] = JsonNode.Parse("""
          {
            "border_color": { "scheme": "accent2" }, "border_weight": 2, "border_dash_type": "dot",
            "effects": { "shadow": { "color": { "rgb": "#000000", "alpha": 50 }, "blur": 8, "distance": 5, "angle": 135 } }
          }
          """);
      }
    }
    JsonObject shape = reads["slides"]![5]!["read"]!["shape_elements"]!.AsArray()
      .Single(element => (int?)element!["id"] == 4)!.AsObject();
    shape["shape_style"] = JsonNode.Parse("""
      {
        "effects": {
          "shadow": { "color": { "scheme": "dk1" }, "inner": true }, "glow": { "color": { "scheme": "accent1" }, "radius": 4 }
        }
      }
      """);

    // Act
    PowerPointDeck mapped = PowerPointMapping.Map(reads, Fixture("example.pptx"), output.PathTo("media"));
    Importer.Import(mapped.Deck, mapped.Media, output.PathTo("imported"));
    Element logo = PresentationLoader.Load(output.PathTo("imported")).Theme.Layouts["content"].Elements
      .Single(element => element.Kind == "picture");

    // Assert
    logo.Stroke.ShouldNotBeNull();
    (logo.Stroke.Color!.Theme, logo.Stroke.Width, logo.Stroke.Dash).ShouldBe(("accent2", 2.0, "dot"));
    logo.Shadow.ShouldNotBeNull();
    (logo.Shadow.Color!.Hex, logo.Shadow.Opacity, logo.Shadow.Blur, logo.Shadow.Distance, logo.Shadow.Angle)
      .ShouldBe(("#000000", 50.0, 8.0, 5.0, 135.0));
    mapped.Findings.ShouldContain(finding => finding.EndsWith("an inner shadow has no match in the format and is left out"));
    mapped.Findings.ShouldContain(finding => finding.EndsWith("a glow has no match in the format and is left out"));
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


}
