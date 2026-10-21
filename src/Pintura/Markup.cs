using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>What markup may refer to: the theme's styles, the presentation's fields and book references, the bullet
/// levels, and colors. <c>FieldProblem</c> answers null for a field or reference that resolves, and otherwise why it
/// doesn't.</summary>
public record MarkupContext(
  Func<string, bool> IsStyle,
  Func<string, string?> FieldProblem,
  Func<string, bool> IsNamedColor,
  int Levels)
{
  public static MarkupContext For(Presentation presentation)
  {
    Theme theme = presentation.Theme;
    return new MarkupContext(theme.Styles.Styles.ContainsKey, FieldProblemFor(presentation),
      theme.NamedColors.ContainsKey, theme.Styles.Levels.Count);
  }

  /// <summary>Fields and references as the loaded presentation resolved them.</summary>
  public static Func<string, string?> FieldProblemFor(Presentation presentation)
  {
    return name => PresentationLoader.BuiltInFields.Contains(name) || presentation.Fields.ContainsKey(name) ||
      presentation.References.ContainsKey(name)
        ? null
        : Reference.Looks(name) ? $"{name} isn't a reference the presentation resolved"
        : $"unknown field {name}; define it in presentation.yaml fields";
  }


}

/// <summary>Parses the Markdown subset into paragraphs of runs: one paragraph per line, bullets and numbering by
/// indentation, <c>**bold**</c>, <c>*italic*</c>, links, Pandoc spans, <c>{.style}</c> lines, and fields.</summary>
public static partial class Markup
{
  private const string EscapableCharacters = "\\*[]{}()$";

  public static List<Paragraph> Parse(Node node, MarkupContext context)
  {
    return Parse(node.AsString(), index => LineOrigin(node, index), context);
  }

  /// <summary>Markup from outside YAML, such as a CSV cell, with the origin of each of its lines.</summary>
  public static List<Paragraph> Parse(string source, Func<int, Origin> lineOrigin, MarkupContext context)
  {
    string text = source.Replace("\r\n", "\n");
    if (text.EndsWith('\n'))
    {
      text = text[..^1];
    }
    var paragraphs = new List<Paragraph>();
    string? pendingStyle = null;
    Origin? pendingOrigin = null;
    var numbering = new Dictionary<int, int>();
    string[] lines = text.Split('\n');
    for (int index = 0; index < lines.Length; index++)
    {
      string line = lines[index];
      Origin origin = lineOrigin(index);
      Match styleLine = StyleLinePattern().Match(line);
      if (styleLine.Success)
      {
        if (pendingStyle is not null)
        {
          throw Error($"{{.{pendingStyle}}} is followed by another style line rather than a paragraph", origin);
        }
        pendingStyle = styleLine.Groups["name"].Value;
        pendingOrigin = origin;
        if (!context.IsStyle(pendingStyle))
        {
          throw Error($"unknown style {pendingStyle}", origin, "styles");
        }
        continue;
      }

      int level = 0;
      int? number = null;
      string content = line;
      Match item = ItemPattern().Match(line);
      if (item.Success)
      {
        int indent = item.Groups["indent"].Length;
        if (indent % 2 != 0)
        {
          throw Error("bullets indent two spaces per level", origin);
        }
        level = indent / 2 + 1;
        if (level > context.Levels)
        {
          throw Error($"bullet level {level} is not among the levels in styles.yaml text.levels", origin, "styles");
        }
        content = item.Groups["content"].Value;
        if (item.Groups["number"].Success)
        {
          int written = int.Parse(item.Groups["number"].Value, CultureInfo.InvariantCulture);
          number = numbering.TryGetValue(level, out int previous) ? previous + 1 : written;
        }
      }
      // A deeper list ends at a shallower paragraph, and a numbered list at anything but its next item.
      foreach (int deeper in numbering.Keys.Where(key => key > level).ToList())
      {
        numbering.Remove(deeper);
      }
      if (number is null)
      {
        numbering.Remove(level);
      }
      else
      {
        numbering[level] = number.Value;
      }

      int start = item.Success ? item.Groups["content"].Index : 0;
      paragraphs.Add(new Paragraph
      {
        Level = level,
        Number = number,
        Style = pendingStyle,
        Runs = ParseInline(content, start, origin, context),
        Origin = pendingOrigin ?? origin,
        Line = index,
        Start = start
      });
      pendingStyle = null;
      pendingOrigin = null;
    }
    if (pendingStyle is not null)
    {
      throw Error($"{{.{pendingStyle}}} must be followed by the paragraph it styles", pendingOrigin!);
    }
    return paragraphs;
  }

  /// <summary>A paragraph's line as markup that keeps it a paragraph, escaping what would start a list item or make
  /// it a style line.</summary>
  public static string EscapeLineStart(string line)
  {
    if (StyleLinePattern().IsMatch(line))
    {
      int brace = line.IndexOf('{');
      return line[..brace] + "\\" + line[brace..];
    }
    Match item = ItemPattern().Match(line);
    if (!item.Success)
    {
      return line;
    }
    int marker = item.Groups["content"].Index - 2;
    return line[..marker] + "\\" + line[marker..];
  }

  /// <summary>Where the backslash of an escaped list marker at the start of <paramref name="text"/> sits, or -1, as
  /// only there do <c>\-</c> and <c>\.</c> escape.</summary>
  private static int EscapedMarker(string text)
  {
    Match marker = EscapedMarkerPattern().Match(text);
    return marker.Success ? marker.Length - 3 : -1;
  }

  /// <summary>Text for the title and placeholders: literal apart from fields, since the layout decides its look.
  /// </summary>
  public static List<Run> ParsePlain(Node node, Func<string, string?> fieldProblem)
  {
    return ParsePlain(node.AsString(), node.Origin, fieldProblem);
  }

  public static List<Run> ParsePlain(string text, Origin origin, Func<string, string?> fieldProblem)
  {
    var runs = new List<Run>();
    var literal = new Literal();
    for (int position = 0; position < text.Length; position++)
    {
      if (text[position] == '\\' && position + 2 < text.Length && text[position + 1] == '$' &&
        text[position + 2] == '{')
      {
        literal.Append('$', position, position + 2);
        literal.Append('{', position + 2, position + 3);
        position += 2;
        continue;
      }
      if (text[position] == '$' && position + 1 < text.Length && text[position + 1] == '{')
      {
        int start = position;
        string field = ReadField(text, ref position, origin, fieldProblem);
        Flush(literal, runs, origin);
        runs.Add(new Run { Field = field, Origin = origin, Sources = [(start, position + 1)] });
        continue;
      }
      literal.Append(text[position], position, position + 1);
    }
    Flush(literal, runs, origin);
    return runs;
  }

  private static void Flush(Literal literal, List<Run> runs, Origin origin)
  {
    if (literal.Length > 0)
    {
      runs.Add(new Run { Text = literal.ToString(), Origin = origin, Sources = literal.Sources() });
      literal.Clear();
    }
  }

  /// <summary>Literal text as it accumulates, with the span of source each character was read from, so an edit to the
  /// text can find the characters to replace.</summary>
  private class Literal
  {
    private readonly StringBuilder text = new StringBuilder();
    private readonly List<(int Start, int End)> sources = new List<(int Start, int End)>();

    public int Length => text.Length;

    public void Append(char character, int start, int end)
    {
      text.Append(character);
      sources.Add((start, end));
    }

    public (int Start, int End)[] Sources()
    {
      return sources.ToArray();
    }

    public void Clear()
    {
      text.Clear();
      sources.Clear();
    }

    public override string ToString()
    {
      return text.ToString();
    }


  }

  private static Origin LineOrigin(Node node, int index)
  {
    Origin origin = node.Origin;
    return node.IsBlock ? origin with { Line = origin.Line + 1 + index, Column = 1 } : origin;
  }

  private static LoadException Error(string problem, Origin origin, string topic = "text")
  {
    return new LoadException(problem, origin, topic);
  }

  /// <summary>A field's name, or a reference as <see cref="Reference.ToString"/> writes it.</summary>
  private static string ReadField(string text, ref int position, Origin origin, Func<string, string?> fieldProblem)
  {
    int close = text.IndexOf('}', position + 2);
    if (close < 0)
    {
      throw Error("a field opened with ${ is never closed; write \\${ for a literal ${", origin);
    }
    string name = text[(position + 2)..close].Trim();
    string topic = "presentations";
    if (Reference.Looks(name))
    {
      name = Reference.Parse(name)?.ToString() ?? throw Error($"${{{name}}} is not a reference; write " +
        "${book::line[\"column\"]}, with | and a format's name before the } to spell it", origin, "books");
      topic = "books";
    }
    else if (!FieldPattern().IsMatch(name))
    {
      throw Error($"${{{name}}} is not a field name", origin, topic);
    }
    if (fieldProblem(name) is string problem)
    {
      throw Error(problem, origin, topic);
    }
    position = close;
    return name;
  }

  private record InlineState(bool Bold, bool Italic, string? Link, List<string> Styles, TextProperties? Properties);

  /// <summary>Runs from <paramref name="text"/>, which starts <paramref name="offset"/> characters into its line.
  /// </summary>
  private static List<Run> ParseInline(string text, int offset, Origin origin, MarkupContext context)
  {
    var runs = new List<Run>();
    int position = 0;
    ParseSpan(text, offset, ref position, null, new InlineState(false, false, null, new List<string>(), null), runs,
      origin, context);
    return Merge(runs);
  }

  /// <summary>Reads runs until <paramref name="closer"/>, returning false when it never appears, so the caller can
  /// take the opening delimiter as literal text instead.</summary>
  private static bool ParseSpan(
    string text,
    int offset,
    ref int position,
    string? closer,
    InlineState state,
    List<Run> runs,
    Origin origin,
    MarkupContext context)
  {
    var literal = new Literal();
    void FlushLiteral()
    {
      if (literal.Length > 0)
      {
        runs.Add(MakeRun(literal.ToString(), state, origin, literal.Sources()));
        literal.Clear();
      }
    }

    while (position < text.Length)
    {
      // Two asterisks inside italic open bold, but three close the italic and then the bold around it.
      if (closer is not null && string.CompareOrdinal(text, position, closer, 0, closer.Length) == 0 &&
        !(closer == "*" && At(text, position, "**") && !At(text, position, "***")))
      {
        FlushLiteral();
        position += closer.Length;
        return true;
      }
      char current = text[position];
      if (current == '\\' && position + 1 < text.Length &&
        (EscapableCharacters.Contains(text[position + 1]) || (offset == 0 && position == EscapedMarker(text))))
      {
        literal.Append(text[position + 1], offset + position, offset + position + 2);
        position += 2;
        continue;
      }
      if (current == '$' && At(text, position, "${"))
      {
        FlushLiteral();
        int start = position;
        string field = ReadField(text, ref position, origin, context.FieldProblem);
        runs.Add(MakeRun("", state, origin, [(offset + start, offset + position + 1)], field));
        position++;
        continue;
      }
      if (current == '*')
      {
        string delimiter = At(text, position, "**") ? "**" : "*";
        FlushLiteral();
        int start = runs.Count;
        int inner = position + delimiter.Length;
        InlineState nested = delimiter == "**" ? state with { Bold = true } : state with { Italic = true };
        if (inner < text.Length && text[inner] != ' ' &&
          ParseSpan(text, offset, ref inner, delimiter, nested, runs, origin, context))
        {
          position = inner;
          continue;
        }
        runs.RemoveRange(start, runs.Count - start);
        foreach (char character in delimiter)
        {
          literal.Append(character, offset + position, offset + position + 1);
          position++;
        }
        continue;
      }
      if (current == '[')
      {
        int close = MatchingBracket(text, position);
        if (close > 0 && close + 1 < text.Length && text[close + 1] is '(' or '{')
        {
          char opener = text[close + 1];
          int end = opener == '(' ? text.IndexOf(')', close + 2) : AttributesEnd(text, close + 2);
          if (end > 0)
          {
            string inside = text[(close + 2)..end];
            // The page and the exported deck open a link with a click, so it can't be a script or a local file.
            if (opener == '(' && !WebLink(inside.Trim()))
            {
              throw Error($"a link is http, https, or mailto, not {inside.Trim()}", origin);
            }
            InlineState nested = opener == '('
              ? state with { Link = inside.Trim() }
              : Attributes(inside, state, origin, context);
            FlushLiteral();
            string content = text[(position + 1)..close];
            int inner = 0;
            ParseSpan(content, offset + position + 1, ref inner, null, nested, runs, origin, context);
            position = end + 1;
            continue;
          }
        }
      }
      literal.Append(current, offset + position, offset + position + 1);
      position++;
    }
    FlushLiteral();
    return closer is null;
  }

  private static bool At(string text, int position, string token)
  {
    return string.CompareOrdinal(text, position, token, 0, token.Length) == 0;
  }

  private static bool WebLink(string link)
  {
    return Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "mailto";
  }

  private static int MatchingBracket(string text, int open)
  {
    int depth = 0;
    for (int position = open; position < text.Length; position++)
    {
      if (text[position] == '\\')
      {
        position++;
        continue;
      }
      if (text[position] == '[')
      {
        depth++;
      }
      else if (text[position] == ']' && --depth == 0)
      {
        return position;
      }
    }
    return -1;
  }

  private static int AttributesEnd(string text, int start)
  {
    bool quoted = false;
    for (int position = start; position < text.Length; position++)
    {
      if (text[position] == '"')
      {
        quoted = !quoted;
      }
      else if (text[position] == '}' && !quoted)
      {
        return position;
      }
    }
    return -1;
  }

  private static InlineState Attributes(string attributes, InlineState state, Origin origin, MarkupContext context)
  {
    var styles = new List<string>(state.Styles);
    TextProperties properties = Copy(state.Properties);
    foreach (Match token in AttributePattern().Matches(attributes))
    {
      if (token.Groups["style"].Success)
      {
        string style = token.Groups["style"].Value;
        if (!context.IsStyle(style))
        {
          throw Error($"unknown style {style}", origin, "styles");
        }
        styles.Add(style);
        continue;
      }
      string key = token.Groups["key"].Value;
      string value = token.Groups["quoted"].Success ? token.Groups["quoted"].Value : token.Groups["value"].Value;
      switch (key)
      {
        case "font":
          properties.Font = value;
          break;
        case "size":
          properties.Size = PlainNumber.TryParse(value, out double size) && size > 0
            ? size
            : throw Error($"span size must be a number of points: {value}", origin);
          break;
        case "color":
          properties.Color = Color.Parse(value, context.IsNamedColor, problem => Error(problem, origin, "colors"));
          break;
        case "underline":
          properties.Underline = value switch
          {
            "true" => true,
            "false" => false,
            _ => throw Error("span underline must be true or false", origin)
          };
          break;
        case "bold" or "italic":
          throw Error($"{key} is not a span attribute; write **bold** or *italic* around the span", origin);
        default:
          throw Error($"unknown span attribute {key}; spans take font, size, color, and underline", origin);
      }
      properties.Origins[key] = origin;
    }
    string rest = AttributePattern().Replace(attributes, "").Trim();
    if (rest.Length > 0)
    {
      throw Error($"cannot read span attributes {{{attributes}}}", origin);
    }
    return state with { Styles = styles, Properties = properties.IsEmpty ? null : properties };
  }

  private static TextProperties Copy(TextProperties? source)
  {
    var copy = new TextProperties();
    if (source is null)
    {
      return copy;
    }
    copy.Font = source.Font;
    copy.Size = source.Size;
    copy.Color = source.Color;
    copy.Underline = source.Underline;
    foreach ((string key, Origin origin) in source.Origins)
    {
      copy.Origins[key] = origin;
    }
    return copy;
  }

  private static Run MakeRun(string text, InlineState state, Origin origin, (int Start, int End)[] sources,
    string? field = null)
  {
    return new Run
    {
      Text = text,
      Field = field,
      Sources = sources,
      Bold = state.Bold ? true : null,
      Italic = state.Italic ? true : null,
      Link = state.Link,
      Styles = state.Styles.Count == 0 ? null : state.Styles,
      Properties = state.Properties,
      Origin = origin
    };
  }

  private static List<Run> Merge(List<Run> runs)
  {
    var merged = new List<Run>();
    foreach (Run run in runs)
    {
      if (merged.Count > 0 && run.Field is null && merged[^1].Field is null && SameFormatting(merged[^1], run))
      {
        Run previous = merged[^1];
        merged[^1] = new Run
        {
          Text = previous.Text + run.Text,
          Bold = previous.Bold,
          Italic = previous.Italic,
          Link = previous.Link,
          Styles = previous.Styles,
          Properties = previous.Properties,
          Origin = previous.Origin,
          Sources = [.. previous.Sources, .. run.Sources]
        };
        continue;
      }
      merged.Add(run);
    }
    return merged;
  }

  private static bool SameFormatting(Run left, Run right)
  {
    return left.Bold == right.Bold && left.Italic == right.Italic && left.Link == right.Link &&
      ReferenceEquals(left.Properties, right.Properties) &&
      (left.Styles ?? []).SequenceEqual(right.Styles ?? []);
  }

  [GeneratedRegex(@"^\s*\{\.(?<name>[A-Za-z][A-Za-z0-9_-]*)\}\s*$")]
  private static partial Regex StyleLinePattern();

  [GeneratedRegex(@"^(?<indent> *)(?:- |(?<number>[0-9]{1,9})\. )(?<content>.*)$")]
  private static partial Regex ItemPattern();

  [GeneratedRegex(@"^ *(?:\\-|[0-9]{1,9}\\\.) ")]
  private static partial Regex EscapedMarkerPattern();

  [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
  private static partial Regex FieldPattern();

  [GeneratedRegex(@"\.(?<style>[A-Za-z][A-Za-z0-9_-]*)|(?<key>[a-z_]+)=(?:""(?<quoted>[^""]*)""|(?<value>[^\s""]+))")]
  private static partial Regex AttributePattern();


}