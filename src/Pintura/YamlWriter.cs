using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>YAML in the repository's own style: block maps at the top, small maps and lists inline, markup as literal
/// blocks, and a blank line around each top-level key holding a block. Maps are
/// <see cref="OrderedDictionary{TKey, TValue}"/>s, lists are <see cref="List{T}"/>s, and scalars are strings, numbers,
/// and booleans.</summary>
public static partial class YamlWriter
{
  private const int Width = 110;

  public static string Write(OrderedDictionary<string, object?> map)
  {
    return Map(map, "", true) + "\n";
  }

  /// <summary>A map as one entry of a top-level list, in block style whatever its length.</summary>
  public static string Item(OrderedDictionary<string, object?> map)
  {
    return "- " + Map(map, "  ")[2..];
  }

  public static string Number(double value)
  {
    return value.ToString(CultureInfo.InvariantCulture);
  }

  /// <summary>Text as a scalar in a block list or map: plain where it reads back as itself, double-quoted where it
  /// wouldn't, such as one starting with # or [.</summary>
  public static string Text(string text)
  {
    return Scalar(text, flow: false);
  }

  /// <summary>Text as a double-quoted scalar, which reads back as the same text anywhere.</summary>
  public static string Quoted(string text)
  {
    return JsonSerializer.Serialize(text, PayloadJson.Options).Replace("\u2028", "\\u2028")
      .Replace("\u2029", "\\u2029");
  }

  /// <summary>Whether a character needs a quoted scalar to survive: a control character other than a line feed or
  /// tab, or a line break YAML reads besides the line feed.</summary>
  private static bool Unprintable(char character)
  {
    return character is not ('\n' or '\t') && char.IsControl(character) || character is '\u2028' or '\u2029';
  }

  /// <summary>Whether text spans lines and can be written as a literal block.</summary>
  private static bool IsBlock(string text)
  {
    return text.Contains('\n') && !text.Any(Unprintable);
  }

  private static bool IsPlain(string text, bool flow)
  {
    if (text.Length == 0 || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n') ||
      text.Contains('\t') || text.Any(Unprintable))
    {
      return false;
    }
    if ("-?:,[]{}#&*!|>'\"%@`".Contains(text[0]) || text.Contains(": ") || text.Contains(" #") || text.EndsWith(':'))
    {
      return false;
    }
    if (flow && text.IndexOfAny([',', '[', ']', '{', '}']) >= 0)
    {
      return false;
    }
    return !Reserved().IsMatch(text) &&
      !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
  }

  private static string Scalar(object? value, bool flow)
  {
    return value switch
    {
      null => "null",
      bool flag => flag ? "true" : "false",
      double number => Number(number),
      int number => number.ToString(CultureInfo.InvariantCulture),
      string text => IsPlain(text, flow) ? text : Quoted(text),
      _ => throw new ArgumentException($"no YAML for {value.GetType().Name}")
    };
  }

  private static bool IsScalar(object? value)
  {
    return value is not (OrderedDictionary<string, object?> or List<object?>);
  }

  private static string Inline(object? value)
  {
    return value switch
    {
      List<object?> list => $"[{string.Join(", ", list.Select(Inline))}]",
      OrderedDictionary<string, object?> map => map.Count == 0
        ? "{}"
        : $"{{ {string.Join(", ", map.Select(pair => $"{pair.Key}: {Inline(pair.Value)}"))} }}",
      _ => Scalar(value, true)
    };
  }

  /// <summary>Whether a value fits on one line: scalars without line breaks, and maps and lists nested at most twice.
  /// </summary>
  private static bool Flat(object? value, int depth = 0)
  {
    if (IsScalar(value))
    {
      return value is not string text || !IsBlock(text);
    }
    if (depth > 1)
    {
      return false;
    }
    IEnumerable<object?> items = value is List<object?> list
      ? list
      : ((OrderedDictionary<string, object?>)value!).Values;
    return items.All(item => Flat(item, depth + 1));
  }

  private static string Block(string text, string pad)
  {
    string indicator = text.StartsWith(' ') ? "2" : "";
    string chomp = text.EndsWith('\n') ? "" : "-";
    string body = text.EndsWith('\n') ? text[..^1] : text;
    IEnumerable<string> lines = body.Split('\n').Select(line => line.Length == 0 ? "" : pad + line);
    return $"|{indicator}{chomp}\n{string.Join("\n", lines)}";
  }

  private static string Value(object? item, string pad, int room)
  {
    if (item is string text && IsBlock(text))
    {
      return " " + Block(text, pad);
    }
    if (IsScalar(item) || (Flat(item) && Inline(item).Length <= room))
    {
      return " " + Inline(item);
    }
    return item is List<object?> list
      ? "\n" + List(list, pad)
      : "\n" + Map((OrderedDictionary<string, object?>)item!, pad);
  }

  private static string Map(OrderedDictionary<string, object?> map, string pad, bool spaced = false)
  {
    var lines = new List<string>();
    foreach ((string key, object? item) in map)
    {
      string text = $"{pad}{Key(key)}:{Value(item, pad + "  ", Width - pad.Length - key.Length - 2)}";
      bool block = text.Contains('\n');
      if (spaced && block && lines.Count > 0 && lines[^1].Length > 0)
      {
        lines.Add("");
      }
      lines.Add(text);
      if (spaced && block)
      {
        lines.Add("");
      }
    }
    while (lines.Count > 0 && lines[^1].Length == 0)
    {
      lines.RemoveAt(lines.Count - 1);
    }
    return string.Join("\n", lines);
  }

  private static string Key(string key)
  {
    return IsPlain(key, true) || int.TryParse(key, out _) ? key : Quoted(key);
  }

  private static string List(List<object?> items, string pad)
  {
    var lines = new StringBuilder();
    foreach (object? item in items)
    {
      if (lines.Length > 0)
      {
        lines.Append('\n');
      }
      if (IsScalar(item) || (Flat(item) && Inline(item).Length <= Width - pad.Length - 2))
      {
        lines.Append($"{pad}- {Inline(item)}");
        continue;
      }
      string body = item is List<object?> nested
        ? List(nested, pad + "  ")
        : Map((OrderedDictionary<string, object?>)item!, pad + "  ");
      lines.Append($"{pad}- {body[(pad.Length + 2)..]}");
    }
    return lines.ToString();
  }

  [GeneratedRegex("^(true|false|yes|no|on|off|null|y|n|~)$", RegexOptions.IgnoreCase)]
  private static partial Regex Reserved();


}