using System.Text.Json.Nodes;

namespace Pintura.Tests;

/// <summary>editide's tools answering every call blandly, as a deck with Office's one layout and one slide, and saving
/// the deck each session was opened on as it came. Counts every call.</summary>
public sealed class BlandTools : IToolHost
{
  public List<string> Calls { get; } = new List<string>();

  /// <summary>Each call's input, in the order the calls came.</summary>
  public List<(string Tool, JsonObject Input)> Inputs { get; } = new List<(string Tool, JsonObject Input)>();

  /// <summary>Runs before each save writes, such as to fail it.</summary>
  public Action<string>? Saving { get; init; }

  /// <summary>A tool these tools fail, as an older editide does one it lacks.</summary>
  public string? Missing { get; init; }

  /// <summary>What read_slide answers; a slide holding nothing when null.</summary>
  public JsonNode? SlideRead { get; init; }

  public string Version => "bland";

  public int? MostPictures { get; init; } = 25;

  /// <summary>The pictures each session attached, a list a session, in the order they were opened.</summary>
  public List<List<string>> Attached { get; } = new List<List<string>>();

  public IToolSession Open(string deck)
  {
    Attached.Add(new List<string>());
    return new Session(this, File.ReadAllBytes(deck));
  }

  private sealed class Session : IToolSession
  {
    private readonly BlandTools tools;
    private readonly byte[] deck;

    public Session(BlandTools tools, byte[] deck)
    {
      this.tools = tools;
      this.deck = deck;
    }

    public JsonNode? Call(string tool, JsonObject input)
    {
      tools.Calls.Add(tool);
      tools.Inputs.Add((tool, input));
      if (tool == tools.Missing)
      {
        throw new InvalidOperationException($"{tool} failed: Unknown tool: '{tool}'");
      }
      return tool switch
      {
        "list_slide_layouts" => JsonNode.Parse("""
          [{ "theme_name": "Office Theme", "layouts": [{ "name": "Title Slide", "slides": [1] }] }]
          """),
        "list_slides" => JsonNode.Parse("[{}]"),
        "read_slide" => tools.SlideRead?.DeepClone() ?? new JsonObject { ["text_elements"] = new JsonArray() },
        _ => JsonValue.Create("ok")
      };
    }

    public string Attach(string picture)
    {
      tools.Calls.Add("attach");
      tools.Attached[^1].Add(Path.GetFileName(picture));
      return Path.GetFileName(picture);
    }

    public void Save(string output)
    {
      tools.Saving?.Invoke(output);
      File.WriteAllBytes(output, deck);
    }

    public void Dispose()
    {
    }


  }


}