using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Pintura;

/// <summary>Where a value was written: the file as shown to the reader, its line and column, and the key path.
/// </summary>
public record Origin(string File, int Line, int Column, string Path = "")
{
  /// <summary>The value exactly as written, which a mouse edit expects to find again before replacing it; null for
  /// what YAML didn't write, such as a CSV cell.</summary>
  public string? Source { get; init; }

  public Origin At(string path)
  {
    return this with { Path = path };
  }

  public override string ToString()
  {
    return Path.Length == 0 ? $"{File}:{Line}" : $"{File}:{Line} {Path}";
  }
}

/// <summary>A problem in the source files, naming the file, the line, and the docs topic that explains the fix.
/// </summary>
public class LoadException : Exception
{
  public LoadException(string problem, Origin origin, string topic)
    : base($"{problem} in {origin.File}:{origin.Line}; see pintura --docs {topic}")
  {
    Problem = problem;
    Origin = origin;
    Topic = topic;
  }

  public string Problem { get; }
  public Origin Origin { get; }
  public string Topic { get; }
}

/// <summary>A number as the files write one: digits with a point and an exponent at most, and finite, since NaN and
/// infinity reach as far as the page's JSON before anything rejects them.</summary>
public static class PlainNumber
{
  public static bool TryParse(string? text, out double value)
  {
    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
      double.IsFinite(value);
  }
}

/// <summary>A source file: its full path, and the path errors and provenance show, relative to the working folder
/// unless the full path is shorter.</summary>
public record SourceFile(string FullPath, string Display)
{
  /// <summary>The file's text as last loaded.</summary>
  public string Text { get; init; } = "";

  public static SourceFile For(string fullPath, string displayBase)
  {
    string relative = Path.GetRelativePath(displayBase, fullPath);
    string shown = relative.Length <= fullPath.Length ? relative : fullPath;
    return new SourceFile(fullPath, shown.Replace('\\', '/'));
  }

  public string Folder => System.IO.Path.GetDirectoryName(FullPath)!;

  public Node Load(string topic)
  {
    var stream = new YamlStream();
    string text = ReadText(topic);
    try
    {
      using var reader = new StringReader(text);
      stream.Load(reader);
    }
    catch (YamlException exception)
    {
      // YamlDotNet's messages repeat the position, which the origin already carries.
      string problem = exception.InnerException?.Message ?? exception.Message;
      int split = problem.IndexOf("): ", StringComparison.Ordinal);
      problem = split >= 0 ? problem[(split + 3)..] : problem;
      int line = (int)exception.Start.Line;
      string hint = FlowValueNear(text, line) ? "; quote text starting with [ or {, or write it as a block " +
        "scalar (|)" : "";
      throw new LoadException($"invalid YAML ({problem.TrimEnd('.')}){hint}",
        new Origin(Display, line, (int)exception.Start.Column), topic);
    }
    YamlNode root = stream.Documents.Count == 0 ? new YamlMappingNode() : stream.Documents[0].RootNode;
    return new Node(root, this with { Text = text }, "", topic);
  }

  /// <summary>The file's text, read while another program holds it open, as Excel does a CSV, which Windows
  /// otherwise refuses.</summary>
  public string ReadText(string topic)
  {
    try
    {
      using var stream = new FileStream(FullPath, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
      using var reader = new StreamReader(stream);
      return reader.ReadToEnd();
    }
    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      throw new LoadException("file not found", new Origin(Display, 1, 1), topic);
    }
    catch (UnauthorizedAccessException)
    {
      throw new LoadException("can't read the file: permission denied", new Origin(Display, 1, 1), topic);
    }
    catch (IOException exception)
    {
      throw new LoadException($"can't read the file ({exception.Message.TrimEnd('.')})", new Origin(Display, 1, 1),
        topic);
    }
  }

  /// <summary>Markup such as <c>[$4.4B]{.positive} of income</c> reads as a list or map followed by stray text,
  /// which YamlDotNet reports on that line or the next.</summary>
  private static bool FlowValueNear(string text, int line)
  {
    string[] lines = text.Split('\n');
    return Enumerable.Range(line - 2, 2)
      .Where(index => index >= 0 && index < lines.Length)
      .Any(index => Regex.IsMatch(lines[index], @"^\s*(-\s+)?[A-Za-z_][\w-]*:\s+[\[{].*[\]}]\S"));
  }


}

/// <summary>A YAML node with its file, key path, and the docs topic an error about it should point to.</summary>
public class Node
{
  public Node(YamlNode yaml, SourceFile file, string path, string topic)
  {
    Yaml = yaml;
    File = file;
    Path = path;
    Topic = topic;
  }

  public YamlNode Yaml { get; }
  public SourceFile File { get; }
  public string Path { get; }
  public string Topic { get; }

  public Origin Origin => new Origin(File.Display, (int)Yaml.Start.Line, (int)Yaml.Start.Column, Path)
  {
    Source = Span(File.Text, Yaml)
  };

  /// <summary>The text <paramref name="node"/> was read from; empty for a node YAML made up, such as a missing
  /// document.</summary>
  public static string Span(string text, YamlNode node)
  {
    (int start, int end) = Extent(text, node);
    return start < end && end <= text.Length ? text[start..end] : "";
  }

  /// <summary>Where <paramref name="node"/> starts and ends in <paramref name="text"/>. YamlDotNet ends a list or map
  /// where it opens, so a flow one runs to its closing bracket and a block one to the end of its last value.
  /// </summary>
  public static (int Start, int End) Extent(string text, YamlNode node)
  {
    int start = (int)node.Start.Index;
    YamlNode? last = node switch
    {
      YamlSequenceNode sequence => sequence.Children.LastOrDefault(),
      YamlMappingNode mapping => mapping.Children.Count == 0 ? null : mapping.Children.Last().Value,
      _ => null
    };
    bool flow = node is YamlSequenceNode { Style: SequenceStyle.Flow } or YamlMappingNode { Style: MappingStyle.Flow };
    if (node is YamlScalarNode)
    {
      return (start, (int)node.End.Index);
    }
    int after = last is null ? start + 1 : Extent(text, last).End;
    if (!flow)
    {
      return (start, after);
    }
    int close = text.IndexOf(node is YamlSequenceNode ? ']' : '}', Math.Min(after, text.Length));
    return (start, close < 0 ? after : close + 1);
  }

  public bool IsScalar => Yaml is YamlScalarNode;
  public bool IsMap => Yaml is YamlMappingNode;
  public bool IsList => Yaml is YamlSequenceNode;
  public bool IsNone => Yaml is YamlScalarNode { Value: "none", Style: ScalarStyle.Plain };

  /// <summary>A literal or folded block scalar, whose text starts on the line after its key.</summary>
  public bool IsBlock => Yaml is YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded };

  public Node WithTopic(string topic)
  {
    return new Node(Yaml, File, Path, topic);
  }

  public Node WithPath(string path)
  {
    return new Node(Yaml, File, path, Topic);
  }

  public LoadException Error(string problem, string? topic = null)
  {
    return new LoadException(problem, Origin, topic ?? Topic);
  }

  public string Name => Path.Length == 0 ? System.IO.Path.GetFileName(File.FullPath) : Path;

  public MapNode AsMap()
  {
    if (Yaml is YamlMappingNode mapping)
    {
      return new MapNode(this, mapping);
    }
    if (Yaml is YamlScalarNode { Value: "" or null, Style: ScalarStyle.Plain })
    {
      return new MapNode(this, new YamlMappingNode());
    }
    throw Error($"{Name} must be a map of keys");
  }

  public string AsString()
  {
    if (Yaml is YamlScalarNode scalar)
    {
      return scalar.Value ?? "";
    }
    if (Yaml is YamlSequenceNode or YamlMappingNode)
    {
      throw Error($"{Name} must be text; YAML read it as a {(IsList ? "list" : "map")}, so quote text starting with " +
        "[ or {, or write it as a block scalar (|)");
    }
    throw Error($"{Name} must be text");
  }

  public double AsNumber()
  {
    if (Yaml is YamlScalarNode { Style: ScalarStyle.Plain } scalar && PlainNumber.TryParse(scalar.Value, out double value))
    {
      return value;
    }
    throw Error($"{Name} must be a number, in points where it is a length");
  }

  public int AsInteger(int min = int.MinValue, int max = int.MaxValue)
  {
    double value = AsNumber();
    if (value != Math.Floor(value) || value < min || value > max)
    {
      throw Error(min == int.MinValue ? $"{Name} must be a whole number"
        : $"{Name} must be a whole number from {min} to {max}");
    }
    return (int)value;
  }

  public bool AsBoolean()
  {
    return Yaml is YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } scalar
      ? scalar.Value == "true"
      : throw Error($"{Name} must be true or false");
  }

  public List<Node> AsList()
  {
    if (Yaml is not YamlSequenceNode sequence)
    {
      throw Error($"{Name} must be a list");
    }
    return sequence.Children
      .Select((child, index) => new Node(child, File, $"{Path}[{index + 1}]", Topic))
      .ToList();
  }

  public double[] AsNumbers(int count, string shape)
  {
    if (Yaml is not YamlSequenceNode sequence || sequence.Children.Count != count)
    {
      throw Error($"{Name} must be {shape}");
    }
    return AsList().Select(child => child.AsNumber()).ToArray();
  }

  public string AsChoice(params string[] choices)
  {
    string value = IsScalar ? AsString() : "";
    if (!choices.Contains(value))
    {
      throw Error($"{Name} must be one of {string.Join(", ", choices)}");
    }
    return value;
  }


}

/// <summary>A YAML map read key by key. Keys never taken are unknown, and <see cref="Done"/> rejects the first.
/// </summary>
public class MapNode
{
  private readonly Node node;
  private readonly List<(string Key, YamlNode KeyNode, YamlNode Value)> entries;
  private readonly HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);

  public MapNode(Node node, YamlMappingNode mapping)
  {
    this.node = node;
    entries = mapping.Children
      .Select(pair => (pair.Key is YamlScalarNode scalar ? scalar.Value ?? "" : pair.Key.ToString(), pair.Key,
        pair.Value))
      .ToList();
  }

  public Node Node => node;

  public Origin Origin => node.Origin;

  public bool Has(string key)
  {
    return entries.Any(entry => entry.Key == key);
  }

  public Node? Take(string key)
  {
    foreach ((string Key, YamlNode _, YamlNode Value) entry in entries)
    {
      if (entry.Key == key)
      {
        taken.Add(key);
        return Child(key, entry.Value);
      }
    }
    return null;
  }

  public Node Require(string key)
  {
    return Take(key) ?? throw node.Error($"{node.Name} needs {key}");
  }

  /// <summary>Every entry, for maps whose keys are names the author chooses, such as areas or styles.</summary>
  public IEnumerable<(string Key, Node Value)> Entries()
  {
    foreach ((string Key, YamlNode _, YamlNode Value) entry in entries)
    {
      taken.Add(entry.Key);
      yield return (entry.Key, Child(entry.Key, entry.Value));
    }
  }

  public void Done()
  {
    foreach ((string Key, YamlNode KeyNode, YamlNode _) entry in entries)
    {
      if (!taken.Contains(entry.Key))
      {
        var origin = new Origin(node.File.Display, (int)entry.KeyNode.Start.Line, (int)entry.KeyNode.Start.Column);
        throw new LoadException($"unknown key {entry.Key}", origin, node.Topic);
      }
    }
  }

  private Node Child(string key, YamlNode value)
  {
    return new Node(value, node.File, node.Path.Length == 0 ? key : $"{node.Path}.{key}", node.Topic);
  }


}