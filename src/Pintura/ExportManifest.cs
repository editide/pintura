using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pintura;

public record ExportedSlide(string Id, string Hash, IReadOnlyList<string> Gaps);

/// <summary>What an export wrote, kept beside the deck so the next export of it rewrites only what changed: the deck's
/// own hash, so an edit made since is noticed, a hash of the design, and each slide's hash and gaps in deck order.
/// </summary>
public record ExportManifest(string Deck, string Design, IReadOnlyList<string> DesignGaps,
  IReadOnlyList<ExportedSlide> Slides)
{
  private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
  {
    WriteIndented = true
  };

  public static string PathFor(string deck)
  {
    return $"{deck}.pintura.json";
  }

  /// <summary>The manifest of <paramref name="deck"/>, or null when there is none, or the deck changed since.
  /// </summary>
  public static ExportManifest? Read(string deck)
  {
    return Load(deck) is ExportManifest manifest && manifest.Deck == FileHash(deck) ? manifest : null;
  }

  /// <summary>Whether <paramref name="deck"/> was saved since the export its manifest describes, such as in
  /// PowerPoint.</summary>
  public static bool Edited(string deck)
  {
    return Load(deck) is ExportManifest manifest && manifest.Deck != FileHash(deck);
  }

  /// <summary>The manifest beside <paramref name="deck"/>, or null when either is missing or the manifest is
  /// unreadable or incomplete.</summary>
  private static ExportManifest? Load(string deck)
  {
    string path = PathFor(deck);
    if (!File.Exists(path) || !File.Exists(deck))
    {
      return null;
    }
    try
    {
      ExportManifest? manifest = JsonSerializer.Deserialize<ExportManifest>(File.ReadAllText(path), Options);
      // Deserializing leaves whatever the file lacks null, whatever the types say.
      bool whole = manifest is { Deck: not null, Design: not null, DesignGaps: not null, Slides: not null } &&
        manifest.Slides.All(slide => slide is { Id: not null, Hash: not null, Gaps: not null });
      return whole ? manifest : null;
    }
    catch (JsonException)
    {
      return null;
    }
  }

  public void Write(string deck)
  {
    File.WriteAllText(PathFor(deck), JsonSerializer.Serialize(this with { Deck = FileHash(deck) }, Options));
  }

  /// <summary>Bumped when the exporter writes the theme or layouts differently, so a deck exported before is rebuilt.
  /// Not the build version, as rebuilding bills the master and every layout again, which an upgrade shouldn't.
  /// </summary>
  public const string DesignMapping = "1";

  /// <summary>Everything but the slides, with <see cref="DesignMapping"/>.</summary>
  public static string DesignHash(JsonObject deck, string root)
  {
    JsonObject design = deck.DeepClone().AsObject();
    design.Remove("slides");
    return Hash(design, root, DesignMapping);
  }

  /// <summary>A slide as the model has it, but for its number, which PowerPoint fills in where it lands.</summary>
  public static string SlideHash(JsonNode slide, string root)
  {
    JsonObject hashed = slide.DeepClone().AsObject();
    hashed.Remove("number");
    return Hash(hashed, root, "");
  }

  /// <summary>Hashes the JSON with the bytes of each picture it names, since a picture can change under its name.
  /// </summary>
  private static string Hash(JsonNode node, string root, string salt)
  {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    hash.AppendData(Encoding.UTF8.GetBytes(salt));
    hash.AppendData(Encoding.UTF8.GetBytes(node.ToJsonString()));
    foreach (string picture in Pictures(node).Distinct(StringComparer.Ordinal))
    {
      string file = Path.GetFullPath(Path.Combine(root, picture));
      hash.AppendData(Encoding.UTF8.GetBytes(picture));
      hash.AppendData(File.Exists(file) ? SHA256.HashData(File.ReadAllBytes(file)) : []);
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
  }

  private static IEnumerable<string> Pictures(JsonNode? node)
  {
    if (node is JsonObject entries)
    {
      foreach ((string key, JsonNode? value) in entries)
      {
        if (key == "picture" && value is JsonValue && value.GetValueKind() == JsonValueKind.String)
        {
          yield return value.GetValue<string>();
        }
        foreach (string picture in Pictures(value))
        {
          yield return picture;
        }
      }
    }
    else if (node is JsonArray items)
    {
      foreach (string picture in items.SelectMany(Pictures))
      {
        yield return picture;
      }
    }
  }

  private static string FileHash(string file)
  {
    using FileStream stream = File.OpenRead(file);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }
}