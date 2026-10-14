using System.Text.Json.Nodes;

namespace Pintura.Tests;

/// <summary>editide's tools answering every call blandly, as a deck with Office's one layout and one slide, and saving
/// the deck each session was opened on as it came. Counts every call.</summary>
public sealed class BlandTools : IToolHost
{
  public List<string> Calls { get; } = new List<string>();

  /// <summary>Runs before each save writes, such as to fail it.</summary>
  public Action<string>? Saving { get; init; }

  public string Version => "bland";

  public int? MostPictures => 25;

  public IToolSession Open(string deck)
  {
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
      return tool switch
      {
        "list_slide_layouts" => JsonNode.Parse("""
          [{ "theme_name": "Office Theme", "layouts": [{ "name": "Title Slide", "slides": [1] }] }]
          """),
        "list_slides" => JsonNode.Parse("[{}]"),
        "read_slide" => new JsonObject { ["text_elements"] = new JsonArray() },
        _ => JsonValue.Create("ok")
      };
    }

    public string Attach(string picture)
    {
      tools.Calls.Add("attach");
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