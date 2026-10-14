using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class DeckReadsTests
{
  /// <summary>Tools answering <c>list_slide_layouts</c> with the masters given, and every read with an empty one.
  /// </summary>
  private sealed class Masters(string designs) : IToolHost, IToolSession
  {
    public int Opened { get; private set; }

    public string Version => "1";

    public int? MostPictures => null;

    public IToolSession Open(string deck)
    {
      Opened++;
      return this;
    }

    public JsonNode? Call(string tool, JsonObject input)
    {
      return tool == "list_slide_layouts"
        ? JsonNode.Parse(designs)
        : new JsonObject { ["read"] = input.DeepClone() };
    }

    public string Attach(string picture)
    {
      throw new NotSupportedException();
    }

    public void Save(string output)
    {
      throw new NotSupportedException();
    }

    public void Dispose()
    {
    }
  }

  private static string Design(string name, params int[] slides)
  {
    return $$"""
      { "theme_name": "{{name}}", "slides_using": {{slides.Length}},
        "layouts": [{ "id": {{name.Length}}, "name": "{{name}} content", "slides": [{{string.Join(", ", slides)}}] }] }
      """;
  }

  [Fact]
  public void A_master_no_slide_uses_is_left_out()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("deck.pptx", "deck");
    var tools = new Masters($"[{Design("Unused")}, {Design("Used", 1, 2)}, {Design("Old")}]");

    // Act
    JsonObject reads = DeckReads.Read(folder.PathTo("deck.pptx"), tools, folder.PathTo("cache"));

    // Assert
    ((string?)reads["theme_name"]).ShouldBe("Used");
    reads["slides"]!.AsArray().Count.ShouldBe(2);
    reads["findings"]!.AsArray().Select(finding => (string?)finding).ShouldBe([
      "the slide master Unused is left out, as no slide uses it",
      "the slide master Old is left out, as no slide uses it"]);
  }

  [Fact]
  public void A_deck_whose_slides_use_two_masters_is_refused_saying_how_many_use_each()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("deck.pptx", "deck");
    var tools = new Masters($"[{Design("Light", 1, 3)}, {Design("Dark", 2)}, {Design("Old")}]");

    // Act
    Action read = () => DeckReads.Read(folder.PathTo("deck.pptx"), tools, folder.PathTo("cache"));

    // Assert
    read.ShouldThrow<ArgumentException>().Message.ShouldEndWith(
      "deck.pptx's slides use 2 slide masters (Light holds slides 1, 3; Dark holds slide 2), and the import reads one; " +
      "move every slide onto one master's layouts in PowerPoint first");
  }

  [Fact]
  public void A_deck_replaced_under_the_same_time_is_read_again_and_a_broken_cache_entry_is_a_miss()
  {
    // Arrange
    using var folder = new TempFolder();
    string deck = folder.PathTo("deck.pptx");
    string cache = folder.PathTo("cache");
    folder.Write("deck.pptx", "first");
    DateTime stamp = File.GetLastWriteTimeUtc(deck);
    var tools = new Masters($"[{Design("Used", 1)}]");
    DeckReads.Read(deck, tools, cache);

    // Act
    folder.Write("deck.pptx", "second");
    File.SetLastWriteTimeUtc(deck, stamp);
    DeckReads.Read(deck, tools, cache);
    foreach (string entry in Directory.GetFiles(cache))
    {
      File.WriteAllText(entry, "{ cut short");
    }
    JsonObject reads = DeckReads.Read(deck, tools, cache);

    // Assert
    tools.Opened.ShouldBe(3);
    ((string?)reads["theme_name"]).ShouldBe("Used");
  }


}