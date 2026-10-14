using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pintura;

/// <summary>editide's tools on one deck, as the exporter and the import call them.</summary>
public interface ITools
{
  JsonNode? Call(string tool, JsonObject input);
}

/// <summary>A deck open in editide's tools.</summary>
public interface IToolSession : ITools, IDisposable
{
  /// <summary>Puts a picture where the deck's tools find it, answering the uri a picture element names it by.
  /// </summary>
  string Attach(string picture);

  /// <summary>Writes the deck, as the tools left it, to <paramref name="output"/>.</summary>
  void Save(string output);
}

/// <summary>Where editide's tools run.</summary>
public interface IToolHost
{
  /// <summary>Names the tools' build, so reads cached under it go stale when it changes.</summary>
  string Version { get; }

  /// <summary>How many different pictures one export may attach, or null for any number.</summary>
  int? MostPictures { get; }

  /// <summary>Opens a copy of <paramref name="deck"/>, leaving the file itself alone.</summary>
  IToolSession Open(string deck);
}

public static class ToolHosts
{
  public const string Production = "https://cloud.editide.com";

  /// <summary>editide's API at <c>EDITIDE_URL</c>, or at editide's own when unset.</summary>
  public static IToolHost Default()
  {
    return EditideTools.Connect(Url);
  }

  /// <summary>editide's API at <c>EDITIDE_URL</c>, or at editide's own when unset.</summary>
  public static string Url =>
    Environment.GetEnvironmentVariable("EDITIDE_URL") is { Length: > 0 } url ? url : Production;


}

/// <summary>A deck as the read tools see it: the master its slides use, its layouts, and every slide, each read in
/// full, as <c>{ theme_name, master, layouts: [{ id, name, slides, read }], slides: [{ index, layout, read }],
/// findings }</c>.</summary>
public static class DeckReads
{
  /// <summary>Reads <paramref name="deck"/>, or takes the reads cached for this content and this build of the tools.
  /// </summary>
  public static JsonObject Read(string deck, IToolHost tools)
  {
    return Read(deck, tools, Temporary.Folder("reads"));
  }

  public static JsonObject Read(string deck, IToolHost tools, string folder)
  {
    byte[] content = SHA256.HashData(File.ReadAllBytes(deck));
    byte[] version = Encoding.UTF8.GetBytes(tools.Version);
    string key = Convert.ToHexString(SHA256.HashData([.. content, .. version]))[..16].ToLowerInvariant();
    string cache = Path.Combine(folder, $"{key}.json");
    if (File.Exists(cache))
    {
      try
      {
        if (JsonNode.Parse(File.ReadAllText(cache)) is JsonObject cached)
        {
          return cached;
        }
      }
      // A write cut short leaves a file that isn't JSON, which reading the deck again replaces.
      catch (JsonException)
      {
      }
    }
    JsonObject reads = ReadThroughTools(deck, tools);
    Directory.CreateDirectory(folder);
    string partial = $"{cache}.{Guid.NewGuid():N}";
    File.WriteAllText(partial, reads.ToJsonString());
    File.Move(partial, cache, true);
    return reads;
  }

  private static JsonObject ReadThroughTools(string deck, IToolHost tools)
  {
    using (IToolSession host = tools.Open(deck))
    {
      List<JsonObject> designs = host.Call("list_slide_layouts", new JsonObject())!.AsArray()
        .Select(node => node!.AsObject()).ToList();
      List<JsonObject> used = designs.Where(design => (int)design["slides_using"]! > 0).ToList();
      if (used.Count > 1)
      {
        // By the slides each holds, since two masters can share a name.
        string uses = string.Join("; ", used.Select(design =>
        {
          List<int> numbers = design["layouts"]!.AsArray().SelectMany(layout => layout!["slides"]!.AsArray())
            .Select(number => (int)number!).Order().ToList();
          return $"{design["theme_name"]} holds slide{(numbers.Count == 1 ? "" : "s")} {string.Join(", ", numbers)}";
        }));
        throw new ArgumentException($"{deck}'s slides use {used.Count} slide masters ({uses}), and the import reads " +
          "one; move every slide onto one master's layouts in PowerPoint first");
      }
      // A template without slides takes its first master.
      JsonObject design = used.FirstOrDefault() ?? designs[0];
      var findings = new JsonArray(designs.Where(other => other != design)
        .Select(other => (JsonNode)$"the slide master {other["theme_name"]} is left out, as no slide uses it")
        .ToArray());
      string themeName = (string)design["theme_name"]!;
      var layouts = new JsonArray();
      foreach (JsonObject summary in design["layouts"]!.AsArray().Select(node => node!.AsObject()))
      {
        JsonObject layout = summary.DeepClone().AsObject();
        layout["read"] = host.Call("read_slide", new JsonObject { ["layout_id"] = summary["id"]!.DeepClone(),
          ["mode"] = "full" });
        layouts.Add(layout);
      }
      var slides = new JsonArray();
      int count = (int)design["slides_using"]!;
      for (int index = 1; index <= count; index++)
      {
        JsonObject? layout = layouts.Select(node => node!.AsObject()).FirstOrDefault(layout =>
          layout["slides"]!.AsArray().Any(number => (int)number! == index));
        slides.Add(new JsonObject
        {
          ["index"] = index,
          ["layout"] = layout?["id"]?.DeepClone(),
          ["read"] = host.Call("read_slide", new JsonObject { ["slide_index"] = index, ["mode"] = "full" })
        });
      }
      return new JsonObject
      {
        ["theme_name"] = themeName,
        ["master"] = host.Call("read_slide", new JsonObject { ["theme_name"] = themeName, ["mode"] = "full" }),
        ["layouts"] = layouts,
        ["slides"] = slides,
        ["findings"] = findings
      };
    }
  }


}