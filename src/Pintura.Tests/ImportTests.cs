using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ImportTests
{
  [Theory]
  [InlineData("presentations/example")]
  [InlineData("presentations/board-update")]
  [InlineData("presentations/default-theme")]
  [InlineData("presentations/balance-sheet")]
  [InlineData("src/Pintura.Tests/fixtures/charts")]
  public void A_presentation_written_from_its_model_loads_unchanged(string relative)
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), relative);
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Fact]
  public void A_table_cells_empty_paragraph_with_formatting_of_its_own_imports()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example");
    JsonObject deck = Importer.Snapshot(PresentationLoader.Load(root));
    JsonNode table = deck["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .First(item => (string?)item!["kind"] == "table")!;
    JsonNode paragraph = table["properties"]!["cells"]![0]!["paragraphs"]![0]!;
    paragraph["runs"] = new JsonArray();
    paragraph["properties"]!["align"] = "center";
    paragraph["properties"]!["space_before"] = 7;
    using var output = new TempFolder();

    // Act
    ImportResult result = Importer.Import(deck.ToJsonString(), root, output.PathTo("imported"));

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    PresentationLoader.Load(output.PathTo("imported")).Slides.SelectMany(slide => slide.Elements)
      .ShouldContain(element => element.Table != null);
  }

  [Fact]
  public void An_element_the_format_cant_hold_is_left_out_with_a_finding_and_the_rest_imports()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    JsonObject chart = before["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .Select(item => item!.AsObject()).First(item => (string?)item["kind"] == "chart");
    chart["properties"]!["type"] = "percent_stacked_column";
    chart["properties"]!["data_labels.position"] = "outside";
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldContain(finding => finding.Contains("left out"));
    Presentation imported = PresentationLoader.Load(folder);
    imported.Slides.ShouldAllBe(slide => slide.Error == null);
    imported.Slides.Count.ShouldBe(before["slides"]!.AsArray().Count);
  }

  [Fact]
  public void Bullets_survive_import_from_a_deck_whose_theme_states_no_levels()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    before["theme"]!["levels"] = new JsonObject();
    int bulleted = Levels(before).Count(level => level > 0);
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    bulleted.ShouldBeGreaterThan(0);
    Levels(Importer.Snapshot(PresentationLoader.Load(folder))).Count(level => level > 0).ShouldBe(bulleted);
  }

  [Fact]
  public void A_theme_whose_levels_skip_one_keeps_each_it_states_and_fills_the_gap()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    JsonObject first = before["theme"]!["levels"]!["1"]!.AsObject();
    JsonObject third = first.DeepClone().AsObject();
    third["bullet"] = "–";
    third["indent"] = 40.0;
    before["theme"]!["levels"] = new JsonObject { ["1"] = first.DeepClone(), ["3"] = third };
    JsonObject paragraph = before["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .Select(node => node!.AsObject()).First(node => (int)node["level"]! == 1);
    paragraph["level"] = 3;
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    JsonObject levels = Importer.Snapshot(PresentationLoader.Load(folder))["theme"]!["levels"]!.AsObject();
    levels.Select(level => level.Key).ShouldBe(["1", "2", "3"]);
    ((string?)levels["3"]!["bullet"]).ShouldBe("–");
    ((double)levels["3"]!["indent"]!).ShouldBe(40);
  }

  [Fact]
  public void A_paragraph_without_a_bullet_that_starts_like_one_survives_import_as_written()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    List<JsonObject> plain = before["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .Where(item => (string?)item!["kind"] == "text" && item["role"] is null)
      .SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .Select(paragraph => paragraph!.AsObject())
      .Where(paragraph => (int)paragraph["level"]! == 0 && paragraph["runs"]!.AsArray().Count == 1).Take(2).ToList();
    plain[0]["runs"]![0]!["text"] = "1. Click the button";
    plain[1]["runs"]![0]!["text"] = "- or press Enter";
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldNotContain(finding => finding.Contains("left out"));
    List<JsonObject> paragraphs = Importer.Snapshot(PresentationLoader.Load(folder))["slides"]!.AsArray()
      .SelectMany(slide => slide!["items"]!.AsArray()).SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .Select(paragraph => paragraph!.AsObject()).ToList();
    foreach (string text in new[] { "1. Click the button", "- or press Enter" })
    {
      paragraphs.ShouldContain(paragraph => (int)paragraph["level"]! == 0 &&
        string.Concat(paragraph["runs"]!.AsArray().Select(run => (string?)run!["text"])) == text);
    }
  }

  [Fact]
  public void A_paragraph_that_reads_like_a_style_line_survives_import_as_written()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    JsonObject plain = before["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .Where(item => (string?)item!["kind"] == "text" && item["role"] is null)
      .SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .Select(paragraph => paragraph!.AsObject())
      .First(paragraph => (int)paragraph["level"]! == 0 && paragraph["runs"]!.AsArray().Count == 1);
    plain["runs"]![0]!["text"] = "{.note}";
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    Importer.Snapshot(PresentationLoader.Load(folder))["slides"]!.AsArray()
      .SelectMany(slide => slide!["items"]!.AsArray()).SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .ShouldContain(paragraph => string.Concat(paragraph!["runs"]!.AsArray().Select(run => (string?)run!["text"]))
        == "{.note}");
  }

  [Fact]
  public void Layouts_sharing_a_picture_survive_import()
  {
    // Arrange
    using var source = new TempFolder();
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), source.PathTo("themes/default"));
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme"),
      source.PathTo("presentations/default-theme"));
    Directory.CreateDirectory(source.PathTo("themes/default/media"));
    File.Copy(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "acme", "media", "logo.svg"),
      source.PathTo("themes/default/media/logo.svg"));
    foreach (string layout in new[] { "content", "section" })
    {
      File.AppendAllText(source.PathTo($"themes/default/layouts/{layout}.yaml"),
        "\nelements:\n  - { id: logo, picture: ../media/logo.svg, frame: [880, 500, 60, 20] }\n");
    }
    string root = source.PathTo("presentations/default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Fact]
  public void A_decorative_element_survives_import()
  {
    // Arrange
    using var source = new TempFolder();
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), source.PathTo("themes/default"));
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme"),
      source.PathTo("presentations/default-theme"));
    File.AppendAllText(source.PathTo("themes/default/layouts/content.yaml"),
      "\nelements:\n  - { id: band, shape: rectangle, frame: [0, 0, 960, 20], decorative: true }\n");
    string root = source.PathTo("presentations/default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    File.ReadAllText(Path.Combine(folder, "theme", "layouts", "content.yaml")).ShouldContain("decorative: true");
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Theory]
  [InlineData("shape: { fill: light2, stroke: { color: dark1 lighter 50, width: 1 } }",
    "shape: { fill: light2, stroke: { color: dark1 lighter 50, width: 1 }, shadow: { color: dark1, opacity: 40, " +
    "blur: 4, distance: 3, angle: 90 } }", true)]
  [InlineData("    shape: rounded_rectangle\n", "    shape: rounded_rectangle\n    shadow: { color: accent1, distance: 2 }\n",
    false)]
  public void A_shadow_survives_import_in_the_shape_base_only_when_every_shape_has_it(string from, string to,
    bool inBase)
  {
    // Arrange
    using var source = new TempFolder();
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), source.PathTo("themes/default"));
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme"),
      source.PathTo("presentations/default-theme"));
    string file = inBase ? "themes/default/styles.yaml" : "presentations/default-theme/slides/operating-income-bridge.yaml";
    string text = File.ReadAllText(source.PathTo(file));
    int first = text.IndexOf(from, StringComparison.Ordinal);
    first.ShouldBeGreaterThanOrEqualTo(0);
    File.WriteAllText(source.PathTo(file), text[..first] + to + text[(first + from.Length)..]);
    string root = source.PathTo("presentations/default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    File.ReadAllText(Path.Combine(folder, "theme", "styles.yaml")).Contains("shadow").ShouldBe(inBase);
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Fact]
  public void A_tables_left_and_right_borders_survive_import()
  {
    // Arrange
    using var source = new TempFolder();
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), source.PathTo("themes/default"));
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme"),
      source.PathTo("presentations/default-theme"));
    string table = source.PathTo("presentations/default-theme/tables/income-statement.yaml");
    File.WriteAllText(table, File.ReadAllText(table)
      .Replace("- { width: 400 }", "- { width: 400, border_right: { color: dark1, width: 0.75 } }")
      .Replace("cells:", "cells:\n  - { row: 10, column: 4, border_left: { color: accent1, width: 2 } }"));
    string root = source.PathTo("presentations/default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    File.ReadAllText(Path.Combine(folder, "tables", "income-statement.yaml"))
      .ShouldContain("border_right: { color: dark1, width: 0.75 }");
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Fact]
  public void A_cells_top_and_bottom_borders_survive_import_on_the_cell_and_a_whole_rows_on_the_row()
  {
    // Arrange
    using var source = new TempFolder();
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), source.PathTo("themes/default"));
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme"),
      source.PathTo("presentations/default-theme"));
    string table = source.PathTo("presentations/default-theme/tables/income-statement.yaml");
    string underline = "border_bottom: { color: dark1, width: 0.75 }";
    File.WriteAllText(table, File.ReadAllText(table)
      .Replace("\nrows:", "\nrows:\n  4: { border_top: { color: accent1, width: 1 } }")
      .Replace("cells:", $$"""
        cells:
          - { row: 2, column: 2, {{underline}} }
          - { row: 2, column: 3, {{underline}} }
          - { row: 2, column: 4, {{underline}} }
          - { row: 10, column: 1, border_top: { width: 3 } }
        """));
    string root = source.PathTo("presentations/default-theme");
    JsonObject before = Importer.Snapshot(PresentationLoader.Load(root));
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    ImportResult result = Importer.Import(before.ToJsonString(), root, folder);

    // Assert
    result.Findings.ShouldBeEmpty();
    string yaml = File.ReadAllText(Path.Combine(folder, "tables", "income-statement.yaml"));
    yaml.ShouldContain("4: { border_top: { color: accent1, width: 1 } }");
    yaml.ShouldContain($"- {{ row: 2, column: 2, {underline} }}");
    JsonObject after = Importer.Snapshot(PresentationLoader.Load(folder));
    Comparable(after).ShouldBe(Comparable(before));
  }

  [Fact]
  public void An_import_refuses_a_folder_holding_something_else()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    string json = Importer.Snapshot(PresentationLoader.Load(root)).ToJsonString();
    using var output = new TempFolder();
    output.Write("notes.md", "Mine");

    // Act
    Action import = () => Importer.Import(json, root, output.Root);

    // Assert
    import.ShouldThrow<ArgumentException>().Message.ShouldContain("pick an empty folder");
    File.Exists(output.PathTo("notes.md")).ShouldBeTrue();
  }

  [Fact]
  public void An_import_refuses_a_folder_holding_a_presentation_and_leaves_it_alone()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    string json = Importer.Snapshot(PresentationLoader.Load(root)).ToJsonString();
    using var output = new TempFolder();
    string folder = output.PathTo("imported");
    Importer.Import(json, root, folder);
    File.WriteAllText(Path.Combine(folder, "review-notes.txt"), "Mine");

    // Act
    Action import = () => Importer.Import(json, root, folder);

    // Assert
    import.ShouldThrow<ArgumentException>().Message.ShouldContain("pick an empty folder");
    File.Exists(Path.Combine(folder, "review-notes.txt")).ShouldBeTrue();
    File.Exists(Path.Combine(folder, "presentation.yaml")).ShouldBeTrue();
  }

  [Fact]
  public void A_failed_import_leaves_nothing_behind()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    JsonObject deck = Importer.Snapshot(PresentationLoader.Load(root));
    deck["slides"]![1]!["layout"] = "missing";
    using var output = new TempFolder();
    string folder = output.PathTo("imported");

    // Act
    Action import = () => Importer.Import(deck.ToJsonString(), root, folder);

    // Assert
    import.ShouldThrow<InvalidDataException>();
    Directory.GetFileSystemEntries(output.Root).ShouldBeEmpty();
  }

  [Fact]
  public void Importing_a_theme_refuses_a_folder_holding_one_and_leaves_nothing_behind()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    using var output = new TempFolder();
    output.Write("deck.json", Importer.Snapshot(PresentationLoader.Load(root)).ToJsonString());
    output.Write("theme/theme.yaml", "name: Mine");

    // Act
    Action import = () => ImportCommand.Run(output.PathTo("deck.json"), output.PathTo("theme"), true, TextWriter.Null);

    // Assert
    import.ShouldThrow<ArgumentException>().Message.ShouldContain("pick an empty folder");
    File.ReadAllText(output.PathTo("theme/theme.yaml")).ShouldBe("name: Mine\n");
    Directory.GetFileSystemEntries(output.Root).Select(Path.GetFileName).ShouldBe(["deck.json", "theme"],
      ignoreOrder: true);
  }

  [Fact]
  public void Importing_slides_writes_those_alone_with_the_whole_theme()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "default-theme");
    using var output = new TempFolder();
    output.Write("deck.json", Importer.Snapshot(PresentationLoader.Load(root)).ToJsonString());
    Presentation source = PresentationLoader.Load(root);

    // Act
    ImportCommand.Run(output.PathTo("deck.json"), output.PathTo("out"), false, TextWriter.Null,
      SlideSelection.Parse($"1,{source.Slides[^1].Id}"));

    // Assert
    Presentation imported = PresentationLoader.Load(output.PathTo("out"));
    imported.Slides.Select(slide => slide.Id).ShouldBe([source.Slides[0].Id, source.Slides[^1].Id]);
    imported.Theme.Layouts.Count.ShouldBe(source.Theme.Layouts.Count);
  }

  [Fact]
  public void Importing_with_theme_writes_the_theme_alone()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example");
    using var output = new TempFolder();
    output.Write("presentations/example/deck.json", Importer.Snapshot(PresentationLoader.Load(root)).ToJsonString());
    // The JSON's pictures are relative to its folder, so a copy of the theme sits where they point.
    CopyFolder(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "acme"), output.PathTo("themes/acme"));
    string theme = output.PathTo("imported");

    // Act
    int code = ImportCommand.Run(output.PathTo("presentations/example/deck.json"), theme, true, TextWriter.Null);

    // Assert
    code.ShouldBe(0);
    Directory.GetFileSystemEntries(theme).Select(Path.GetFileName).ShouldBe(
      ["layouts", "media", "styles.yaml", "theme.yaml"], ignoreOrder: true);
    File.ReadAllText(Path.Combine(theme, "theme.yaml")).ShouldContain("picture: media/logo.svg");
  }

  private static void CopyFolder(string from, string to)
  {
    Directory.CreateDirectory(to);
    foreach (string file in Directory.GetFiles(from))
    {
      File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    }
    foreach (string child in Directory.GetDirectories(from))
    {
      CopyFolder(child, Path.Combine(to, Path.GetFileName(child)));
    }
  }

  private static IEnumerable<int> Levels(JsonObject deck)
  {
    return deck["slides"]!.AsArray().SelectMany(slide => slide!["items"]!.AsArray())
      .SelectMany(item => item!["paragraphs"]?.AsArray() ?? [])
      .Select(paragraph => (int)paragraph!["level"]!);
  }

  /// <summary>The deck as indented JSON with its keys sorted, and each picture named by its file alone, since the
  /// import copies pictures into folders of its own.</summary>
  private static string Comparable(JsonNode deck)
  {
    return Sorted(deck)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
  }

  private static JsonNode? Sorted(JsonNode? node)
  {
    return node switch
    {
      JsonObject map => new JsonObject(map
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => KeyValuePair.Create(pair.Key, pair.Key == "picture" && pair.Value is JsonValue path
          ? JsonValue.Create(Path.GetFileName((string)path!))
          : Sorted(pair.Value)))),
      JsonArray list => new JsonArray(list.Select(Sorted).ToArray()),
      _ => node?.DeepClone()
    };
  }


}