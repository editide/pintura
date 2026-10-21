using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class IncrementalExportTests
{
  /// <summary>Answers every call blandly, keeping the slide-level ones in order.</summary>
  private sealed class RecordingTools : ITools
  {
    public List<string> Calls { get; } = new List<string>();

    public JsonNode? Call(string tool, JsonObject input)
    {
      if (tool is "add_slide" or "move_slide" or "delete_slides")
      {
        Calls.Add($"{tool} {input.ToJsonString()}");
      }
      return tool == "read_slide" ? new JsonObject { ["text_elements"] = new JsonArray() } : JsonValue.Create("ok");
    }


  }

  private static JsonObject Deck(params string[] slides)
  {
    return new JsonObject
    {
      ["theme"] = new JsonObject(),
      ["layouts"] = new JsonArray(new JsonObject
      {
        ["id"] = "content", ["name"] = "Content", ["themeElements"] = true
      }),
      ["slides"] = new JsonArray(slides.Select((id, index) => (JsonNode?)new JsonObject
      {
        ["id"] = id.TrimEnd('*'),
        ["number"] = index + 1,
        ["layout"] = "content",
        // A star marks a slide changed since the last export.
        ["items"] = new JsonArray(),
        ["hide"] = new JsonArray(),
        ["note"] = id.EndsWith('*') ? "changed" : null
      }).ToArray())
    };
  }

  private static List<ExportedSlide> Exported(params string[] ids)
  {
    JsonObject deck = Deck(ids);
    return deck["slides"]!.AsArray()
      .Select(slide => new ExportedSlide((string)slide!["id"]!, ExportManifest.SlideHash(slide, ""), ["a gap"]))
      .ToList();
  }

  private static (Exporter Exporter, RecordingTools Tools) Updated(JsonObject deck, List<ExportedSlide> previous)
  {
    var tools = new RecordingTools();
    var exporter = new Exporter(tools, (relative, _) => (relative, relative));
    exporter.Update(deck, previous, ["a design gap"]);
    return (exporter, tools);
  }

  [Fact]
  public void An_unchanged_deck_makes_no_slide_calls_and_keeps_every_gap()
  {
    // Act
    (Exporter exporter, RecordingTools tools) = Updated(Deck("a", "b", "c"), Exported("a", "b", "c"));

    // Assert
    tools.Calls.ShouldBeEmpty();
    exporter.Reused.ShouldBe(3);
    exporter.Gaps.ShouldBe(["a design gap", "a gap"]);
  }

  [Fact]
  public void An_update_bills_only_the_slides_it_writes()
  {
    // Act
    (Exporter exporter, _) = Updated(Deck("a", "b*", "c", "d"), Exported("a", "b", "c"));

    // Assert
    exporter.Billed.ShouldBe(new ExportBilling(2, 0, 0));
  }

  [Fact]
  public void A_changed_slide_is_deleted_and_written_again_where_it_was()
  {
    // Act
    (Exporter exporter, RecordingTools tools) = Updated(Deck("a", "b*", "c"), Exported("a", "b", "c"));

    // Assert
    tools.Calls.ShouldBe([
      """delete_slides {"slide_indices":[2]}""",
      """add_slide {"layout_name":"Content","slide_index":2}"""
    ]);
    exporter.Reused.ShouldBe(2);
  }

  [Fact]
  public void A_slide_number_alone_changes_nothing()
  {
    // Arrange
    JsonObject deck = Deck("a", "b");
    deck["slides"]![0]!["number"] = 7;

    // Act
    (_, RecordingTools tools) = Updated(deck, Exported("a", "b"));

    // Assert
    tools.Calls.ShouldBeEmpty();
  }

  [Fact]
  public void Reordered_slides_are_moved_rather_than_written()
  {
    // Act
    (Exporter exporter, RecordingTools tools) = Updated(Deck("c", "a", "b"), Exported("a", "b", "c"));

    // Assert
    tools.Calls.ShouldBe(["""move_slide {"from_index":3,"to_index":1}"""]);
    exporter.Reused.ShouldBe(3);
  }

  [Fact]
  public void A_removed_slide_is_deleted_and_a_new_one_appended()
  {
    // Act
    (_, RecordingTools tools) = Updated(Deck("a", "c", "d"), Exported("a", "b", "c"));

    // Assert
    tools.Calls.ShouldBe([
      """delete_slides {"slide_indices":[2]}""",
      """add_slide {"layout_name":"Content"}"""
    ]);
  }

  [Fact]
  public void When_every_slide_changed_the_new_ones_go_in_before_the_old_are_deleted()
  {
    // Act
    (_, RecordingTools tools) = Updated(Deck("a*", "b*"), Exported("a", "b"));

    // Assert
    tools.Calls.ShouldBe([
      """add_slide {"layout_name":"Content","slide_index":1}""",
      """add_slide {"layout_name":"Content","slide_index":2}""",
      """delete_slides {"slide_indices":[3,4]}"""
    ]);
  }

  [Fact]
  public void A_manifest_is_dropped_once_its_deck_is_edited()
  {
    // Arrange
    using var folder = new TempFolder();
    string deck = folder.PathTo("deck.pptx");
    File.WriteAllText(deck, "as exported");
    new ExportManifest("", "design", [], Exported("a")).Write(deck);
    ExportManifest? before = ExportManifest.Read(deck);

    // Act
    File.WriteAllText(deck, "edited in PowerPoint");
    ExportManifest? after = ExportManifest.Read(deck);

    // Assert
    before.ShouldNotBeNull().Slides.Single().Id.ShouldBe("a");
    after.ShouldBeNull();
  }

  [Fact]
  public void A_manifest_missing_what_it_records_reads_as_none()
  {
    // Arrange
    using var folder = new TempFolder();
    string deck = folder.PathTo("deck.pptx");
    File.WriteAllText(deck, "as exported");
    new ExportManifest("", "design", [], Exported("a")).Write(deck);
    string manifest = ExportManifest.PathFor(deck);
    JsonObject written = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
    written.Remove("slides");
    File.WriteAllText(manifest, written.ToJsonString());

    // Act
    ExportManifest? read = ExportManifest.Read(deck);

    // Assert
    read.ShouldBeNull();
  }

  [Fact]
  public void A_layout_no_slide_uses_is_left_out()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("theme/layouts/unused.yaml", "name: Unused");

    // Act
    JsonObject deck = Exporter.Model(fixture.Load());

    // Assert
    deck["layouts"]!.AsArray().Select(layout => (string)layout!["id"]!).ShouldBe(["content", "cover"],
      ignoreOrder: true);
  }

  [Fact]
  public void A_presentation_with_no_slides_is_refused_before_any_call()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/presentation.yaml", "name: Empty\ntheme: ../theme\nslides: []");
    var tools = new BlandTools();

    // Act
    InvalidOperationException refused = Should.Throw<InvalidOperationException>(() =>
      Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools));

    // Assert
    refused.Message.ShouldContain("no slides");
    tools.Calls.ShouldBeEmpty();
  }

  [Fact]
  public void A_first_export_bills_the_master_each_layout_and_each_slide()
  {
    // Arrange
    using var fixture = new DeckFixture();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), new BlandTools());

    // Assert
    report.Billed.ShouldBe(new ExportBilling(2, 2, 1));
  }

  [Fact]
  public void A_second_export_of_an_unchanged_presentation_makes_no_calls()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    var tools = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools);

    // Assert
    tools.Calls.ShouldBeEmpty();
    report.Reused.ShouldBe(2);
    report.Billed.ShouldBe(new ExportBilling(0, 0, 0));
  }

  [Fact]
  public void A_theme_change_exports_the_whole_deck_again()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    fixture.Write("theme/theme.yaml", DeckFixture.Theme.Replace("#4472C4", "#112233"));
    var tools = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools);

    // Assert
    tools.Calls.ShouldContain("edit_theme");
    tools.Calls.Count(call => call == "add_slide").ShouldBe(2);
    report.Reused.ShouldBe(0);
  }

  [Fact]
  public void The_master_takes_the_themes_name_rather_than_the_blank_decks()
  {
    // Arrange
    using var fixture = new DeckFixture();
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    JsonObject master = tools.Inputs
      .Single(call => call.Tool == "edit_slide" && (string?)call.Input["target"] == "master").Input;
    ((string?)master["slide"]!["name"]).ShouldBe("Test");
    ((string?)tools.Inputs.Last(call => call.Tool == "delete_layout").Input["theme_name"]).ShouldBe("Test");
  }

  [Fact]
  public void Slides_export_alone_and_a_later_whole_export_adds_the_rest()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    var tools = new BlandTools();
    var whole = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools, only: SlideSelection.Parse("two"));
    ExportReport after = Exporter.Write(fixture.Load(), output, whole);

    // Assert
    report.Slides.ShouldBe(1);
    tools.Inputs.Where(call => call.Tool == "add_slide").Select(call => (string?)call.Input["layout_name"])
      .ShouldBe(["Content"]);
    after.Reused.ShouldBe(1);
    whole.Calls.Count(call => call == "add_slide").ShouldBe(1);
  }

  [Fact]
  public void Slides_exported_into_a_deck_rewrite_only_those_and_leave_the_rest_as_they_were()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    fixture.Write("deck/slides/one.yaml", "layout: cover\ntitle: Changed, but not named");
    fixture.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed and named");
    var tools = new BlandTools();
    var whole = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools, only: SlideSelection.Parse("two"));
    Exporter.Write(fixture.Load(), output, whole);

    // Assert
    report.Slides.ShouldBe(2);
    report.Reused.ShouldBe(1);
    tools.Inputs.Where(call => call.Tool is "add_slide" or "delete_slides").Select(call => call.Input.ToJsonString())
      .ShouldBe(["""{"slide_indices":[2]}""", """{"layout_name":"Content"}"""]);
    whole.Inputs.Where(call => call.Tool == "add_slide").Select(call => (string?)call.Input["layout_name"])
      .ShouldBe(["Cover"]);
  }

  [Theory]
  [InlineData("edited", "edited in PowerPoint")]
  [InlineData("theme", "theme or layouts changed")]
  [InlineData("foreign", "Pintura didn't export")]
  public void Slides_refuse_a_deck_they_would_replace_rather_than_update(string change, string message)
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    if (change == "edited")
    {
      File.AppendAllText(output, "edited in PowerPoint");
    }
    else if (change == "theme")
    {
      fixture.Write("theme/theme.yaml", DeckFixture.Theme.Replace("#4472C4", "#112233"));
    }
    else
    {
      File.Delete(ExportManifest.PathFor(output));
    }
    byte[] before = File.ReadAllBytes(output);
    var tools = new BlandTools();

    // Act
    Action export = () => Exporter.Write(fixture.Load(), output, tools, only: SlideSelection.Parse("two"));

    // Assert
    export.ShouldThrow<InvalidOperationException>().Message.ShouldContain(message);
    tools.Calls.ShouldBeEmpty();
    File.ReadAllBytes(output).ShouldBe(before);
  }

  [Fact]
  public void A_failed_save_leaves_the_previous_deck_and_manifest_alone()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    byte[] deck = File.ReadAllBytes(output);
    string manifest = File.ReadAllText(ExportManifest.PathFor(output));
    fixture.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    var failing = new BlandTools
    {
      Saving = path =>
      {
        File.WriteAllText(path, "half a deck");
        throw new InvalidOperationException("downloading the deck failed");
      }
    };

    // Act
    Should.Throw<InvalidOperationException>(() => Exporter.Write(fixture.Load(), output, failing));

    // Assert
    File.ReadAllBytes(output).ShouldBe(deck);
    File.ReadAllText(ExportManifest.PathFor(output)).ShouldBe(manifest);
  }

  [Fact]
  public void A_deck_open_in_another_program_is_refused_before_any_call()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    fixture.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    var tools = new BlandTools();
    InvalidOperationException refusal;

    // Act
    using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
      refusal = Should.Throw<InvalidOperationException>(() => Exporter.Write(fixture.Load(), output, tools));
    }

    // Assert
    refusal.Message.ShouldBe($"can't write {output}, as another program, such as PowerPoint, has it open; close " +
      "it and export again");
    tools.Calls.ShouldBeEmpty();
  }

  [Fact]
  public void A_slide_that_fails_to_load_is_refused_before_any_call()
  {
    // Arrange
    using var fixture = new DeckFixture();
    fixture.Write("deck/slides/two.yaml", "layout: nowhere");
    var tools = new BlandTools();

    // Act
    InvalidOperationException refusal = Should.Throw<InvalidOperationException>(() =>
      Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools));

    // Assert
    refusal.Message.ShouldStartWith("slide two failed to load: ");
    refusal.Message.ShouldEndWith("; fix it and export again");
    tools.Calls.ShouldBeEmpty();
  }

  [Fact]
  public void A_picture_replaced_during_an_export_is_exported_again_next_time()
  {
    // Arrange
    using var fixture = new DeckFixture();
    File.WriteAllBytes(fixture.Folder.PathTo("deck/logo.png"), Png);
    fixture.Write("deck/slides/two.yaml", """
      layout: content
      elements:
        - { id: logo, picture: ../logo.png, frame: [0, 0, 10, 10] }
      """);
    string output = fixture.Folder.PathTo("out/deck.pptx");
    var replacing = new BlandTools
    {
      Saving = _ => File.WriteAllBytes(fixture.Folder.PathTo("deck/logo.png"), [.. Png, 0])
    };
    Exporter.Write(fixture.Load(), output, replacing);
    var tools = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools);

    // Assert
    report.Reused.ShouldBe(1);
    tools.Calls.Count(call => call == "add_slide").ShouldBe(1);
  }

  [Fact]
  public void A_deck_saved_in_PowerPoint_since_is_set_aside_and_the_presentation_exported_afresh()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    fixture.Write("out/deck (edited in PowerPoint).pptx", "an earlier one");
    File.AppendAllText(output, "edited in PowerPoint");
    byte[] edited = File.ReadAllBytes(output);
    var tools = new BlandTools();

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools);

    // Assert
    string aside = fixture.Folder.PathTo("out/deck (edited in PowerPoint 2).pptx");
    report.Edited.ShouldBe(aside);
    File.ReadAllBytes(aside).ShouldBe(edited);
    File.ReadAllText(fixture.Folder.PathTo("out/deck (edited in PowerPoint).pptx")).ShouldBe("an earlier one\n");
    tools.Calls.ShouldContain("edit_theme");
    ExportManifest.Read(output).ShouldNotBeNull();
  }

  /// <summary>A one-pixel PNG.</summary>
  private static readonly byte[] Png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

  [Fact]
  public void A_slides_hash_follows_the_bytes_of_its_pictures()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("media/logo.png", "first");
    JsonNode slide = JsonNode.Parse("""
      { "id": "a", "items": [{ "kind": "picture", "properties": { "picture": "media/logo.png" } }] }
      """)!;
    string before = ExportManifest.SlideHash(slide, folder.Root);

    // Act
    folder.Write("media/logo.png", "second");
    string after = ExportManifest.SlideHash(slide, folder.Root);

    // Assert
    after.ShouldNotBe(before);
  }

  /// <summary>The fixture's presentation with <paramref name="slides"/> as its list.</summary>
  private static void Slides(DeckFixture fixture, params string[] slides)
  {
    fixture.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nfields:\n  footer: Confidential\n" +
      "slides:\n" + string.Concat(slides.Select(slide => $"  - {slide}\n")));
  }

  private static JsonNode? Sections(BlandTools tools)
  {
    return tools.Inputs.LastOrDefault(call => call.Tool == "edit_sections").Input?["sections"];
  }

  [Fact]
  public void Sections_export_after_the_slides_with_the_run_before_the_first_as_powerpoints_default()
  {
    // Arrange
    using var fixture = new DeckFixture();
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    tools.Calls.Last(call => call != "attach").ShouldBe("edit_sections");
    Sections(tools)!.ToJsonString().ShouldBe(
      """[{"name":"Default Section","first_slide":1},{"name":"Results","first_slide":2}]""");
  }

  [Fact]
  public void A_first_export_of_a_presentation_without_sections_writes_none()
  {
    // Arrange
    using var fixture = new DeckFixture();
    Slides(fixture, "one", "two");
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    tools.Calls.ShouldNotContain("edit_sections");
  }

  [Fact]
  public void A_renamed_section_is_written_again_on_a_second_export()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    Slides(fixture, "one", "section: Outlook", "two");
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), output, tools);

    // Assert
    Sections(tools)!.ToJsonString().ShouldBe(
      """[{"name":"Default Section","first_slide":1},{"name":"Outlook","first_slide":2}]""");
  }

  [Fact]
  public void An_update_that_drops_the_sections_clears_them_from_the_deck()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    Slides(fixture, "one", "two");
    var tools = new BlandTools();

    // Act
    Exporter.Write(fixture.Load(), output, tools);

    // Assert
    Sections(tools)!.ToJsonString().ShouldBe("[]");
  }

  [Fact]
  public void Tools_without_edit_sections_leave_the_deck_whole_and_report_the_sections_as_a_gap()
  {
    // Arrange
    using var fixture = new DeckFixture();
    var tools = new BlandTools { Missing = "edit_sections" };

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), fixture.Folder.PathTo("out/deck.pptx"), tools);

    // Assert
    report.Gaps.ShouldContain(gap => gap.StartsWith("sections aren't exported"));
    File.Exists(fixture.Folder.PathTo("out/deck.pptx")).ShouldBeTrue();
  }

  [Fact]
  public void Tools_without_edit_sections_raise_no_gap_for_a_presentation_without_sections()
  {
    // Arrange
    using var fixture = new DeckFixture();
    string output = fixture.Folder.PathTo("out/deck.pptx");
    Exporter.Write(fixture.Load(), output, new BlandTools());
    Slides(fixture, "one", "two");
    var tools = new BlandTools { Missing = "edit_sections" };

    // Act
    ExportReport report = Exporter.Write(fixture.Load(), output, tools);

    // Assert
    report.Gaps.ShouldNotContain(gap => gap.StartsWith("sections"));
  }


}