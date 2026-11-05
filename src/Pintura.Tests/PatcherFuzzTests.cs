using System.Text;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Pintura.Tests;

/// <summary>Random edits of the kinds the page sends, over random markup: each must apply and read back as the text
/// the page asked for, so a refusal or a wrong write shows up here before it does on a slide.</summary>
public class PatcherFuzzTests
{
  private const int Cases = 3000;
  private const char Mark = Patcher.FieldMark;

  private static readonly MarkupContext Context = new MarkupContext(style => style is "lead" or "positive",
    field => field is "date" or "slide" ? null : $"unknown field {field}", _ => false, 2);

  private readonly ITestOutputHelper output;

  public PatcherFuzzTests(ITestOutputHelper output)
  {
    this.output = output;
  }

  [Fact]
  public void Random_edits_to_markup_apply_and_read_back_as_typed()
  {
    // Arrange
    List<string> failures = Run(seed => Markup(new Random(seed)), "elements.note.text", false);

    // Assert
    Report(failures).ShouldBeEmpty();
  }

  [Fact]
  public void Random_edits_to_a_title_apply_and_read_back_as_typed()
  {
    // Arrange
    List<string> failures = Run(seed => Title(new Random(seed)), "title", true);

    // Assert
    Report(failures).ShouldBeEmpty();
  }

  private List<string> Run(Func<int, string> file, string path, bool plain)
  {
    var failures = new List<string>();
    for (int seed = 0; seed < Cases; seed++)
    {
      string text = file(seed);
      var random = new Random(seed * 7919 + 1);
      string value = ValueAt(text, path)!;
      List<string> before = Paragraphs(value, plain);
      List<string> after = Edit(random, before, plain);
      // Plain text has no escape for a backslash, so one typed before a field would escape it instead.
      if (after.SequenceEqual(before) || (plain && after[0].Contains("\\" + Mark)))
      {
        continue;
      }
      // The page drops a trailing empty paragraph before sending, as the patcher does.
      List<string> expected = after.ToList();
      while (expected.Count > 1 && expected[^1].Length == 0)
      {
        expected.RemoveAt(expected.Count - 1);
      }
      string outcome;
      try
      {
        string patched = Patcher.Apply(text, [new ValueEdit(path, Patcher.SourceAt(text, path), Paragraphs: after)],
          Context).Text;
        string written = ValueAt(patched, path)!;
        List<string> read = Paragraphs(written, plain);
        if (!read.SequenceEqual(expected))
        {
          outcome = $"reads back as {Show(read)}\n  wrote {Show([patched])}";
        }
        else if (Reformatted(Formats(value, plain), Formats(written, plain),
          Aligned(before, expected)) is string moved)
        {
          outcome = $"reformatted: {moved}\n  wrote {Show([patched])}";
        }
        else
        {
          continue;
        }
      }
      catch (PatchException exception)
      {
        outcome = $"refused: {exception.Message}";
      }
      catch (Exception exception)
      {
        outcome = $"threw {exception.GetType().Name}: {exception.Message}";
      }
      failures.Add($"seed {seed}: {Show(before)} -> {Show(after)}\n  {outcome}\n  file {Show([text])}");
    }
    return failures;
  }

  private List<string> Report(List<string> failures)
  {
    foreach (IGrouping<string, string> group in failures.GroupBy(Kind).OrderByDescending(group => group.Count()))
    {
      output.WriteLine($"{group.Count()} x {group.Key}");
      foreach (string failure in group.OrderBy(failure => failure.Length).Take(14))
      {
        output.WriteLine(failure);
      }
      output.WriteLine("");
    }
    return failures;
  }

  private static string Kind(string failure)
  {
    string line = failure.Split('\n')[1].Trim().Split(" DEBUG")[0];
    return line.StartsWith("reads back", StringComparison.Ordinal) ? "reads back wrong"
      : line.StartsWith("reformatted", StringComparison.Ordinal) ? "reformatted" : line;
  }

  private static string Show(List<string> paragraphs)
  {
    return "[" + string.Join(" | ", paragraphs.Select(paragraph =>
      paragraph.Replace("\n", "\\n").Replace(Mark.ToString(), "§"))) + "]";
  }

  #region Edits

  /// <summary>One to three edits, as a user makes before the text is committed: a stretch deleted, typed over, or
  /// typed into, which may run across paragraphs and fields but never types a field or a line break.</summary>
  private static List<string> Edit(Random random, List<string> paragraphs, bool plain)
  {
    string text = string.Join('\n', paragraphs);
    int edits = random.Next(1, 4);
    for (int edit = 0; edit < edits; edit++)
    {
      int start = random.Next(text.Length + 1);
      int end = random.Next(4) switch
      {
        0 => start,
        1 => Math.Min(text.Length, start + 1),
        2 => Math.Min(text.Length, start + random.Next(1, 8)),
        _ => random.Next(start, text.Length + 1)
      };
      string typed = random.Next(3) == 0 && end > start ? "" : Typed(random);
      if (start == end && typed.Length == 0)
      {
        typed = "x";
      }
      text = text[..start] + typed + text[end..];
    }
    return plain ? [text] : text.Split('\n').ToList();
  }

  private static string Typed(Random random)
  {
    const string letters = "abcdefg  ";
    const string awkward = "*[]{}()$\\-1.#_`!:";
    int length = random.Next(1, 7);
    var typed = new StringBuilder();
    for (int index = 0; index < length; index++)
    {
      typed.Append(random.Next(4) == 0 ? awkward[random.Next(awkward.Length)] : letters[random.Next(letters.Length)]);
    }
    // A word typed after a space, as a user types one.
    string word = typed.ToString();
    return random.Next(5) == 0 ? "1. " + word : random.Next(5) == 0 ? "- " + word : word;
  }

  #endregion

  #region Files

  private static string Markup(Random random)
  {
    var lines = new List<string>();
    int paragraphs = random.Next(1, 5);
    for (int index = 0; index < paragraphs; index++)
    {
      if (random.Next(5) == 0)
      {
        lines.Add("{.lead}");
      }
      string prefix = random.Next(5) switch
      {
        0 => "- ",
        1 => "  - ",
        2 => $"{index + 1}. ",
        _ => ""
      };
      lines.Add(prefix + Line(random, false));
    }
    if (lines[0].StartsWith("  ", StringComparison.Ordinal))
    {
      lines[0] = lines[0].TrimStart();
    }
    return "elements:\n  - id: note\n    text: |\n" + string.Concat(lines.Select(line => $"      {line}\n"));
  }

  private static string Title(Random random)
  {
    string line = Line(random, true);
    return $"title: \"{line.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\n";
  }

  private static string Line(Random random, bool plain)
  {
    var line = new StringBuilder();
    int pieces = random.Next(1, 6);
    for (int index = 0; index < pieces; index++)
    {
      string piece = plain ? PlainPiece(random) : Piece(random);
      // Emphasis run into emphasis, as `*a**b*`, reads as other markers than were meant, and no one writes it.
      bool glued = line.Length > 0 && line[^1] == '*' && piece.StartsWith('*');
      if (index > 0 && (glued || random.Next(3) > 0))
      {
        line.Append(' ');
      }
      line.Append(piece);
    }
    return line.ToString().Trim() is { Length: > 0 } trimmed ? trimmed : "word";
  }

  private static string Piece(Random random)
  {
    return random.Next(10) switch
    {
      0 => $"**{Words(random)}**",
      1 => $"*{Words(random)}*",
      2 => $"[{Words(random)}](https://example.com)",
      3 => $"[{Words(random)}]{{.positive}}",
      4 => "${date}",
      5 => $"**[{Words(random)}]{{.positive}}**",
      6 => $"**{Words(random)} ${{slide}} {Words(random)}**",
      7 => @"\*" + Words(random),
      _ => Words(random)
    };
  }

  private static string PlainPiece(Random random)
  {
    return random.Next(5) switch
    {
      0 => "${date}",
      1 => "${slide}",
      2 => "$4.7B *",
      _ => Words(random)
    };
  }

  private static string Words(Random random)
  {
    string[] words = ["sales", "grew", "rent", "is", "of", "spend", "a", "board", "$11.4B", "33%"];
    return string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ => words[random.Next(words.Length)]));
  }

  #endregion

  #region Reading

  private static string? ValueAt(string text, string path)
  {
    var yaml = new YamlStream();
    yaml.Load(new StringReader(text));
    YamlNode node = yaml.Documents[0].RootNode;
    foreach (string key in path.Split('.'))
    {
      node = node switch
      {
        YamlMappingNode map => map.Children[new YamlScalarNode(key)],
        YamlSequenceNode list => list.Children.Single(child =>
          child is YamlMappingNode item && item.Children.TryGetValue(new YamlScalarNode("id"), out YamlNode? id) &&
          id is YamlScalarNode { Value: string name } && name == key),
        _ => throw new InvalidOperationException(path)
      };
    }
    return (node as YamlScalarNode)?.Value;
  }

  private static List<string> Paragraphs(string value, bool plain)
  {
    List<Paragraph> paragraphs = plain
      ? [new Paragraph { Runs = Pintura.Markup.ParsePlain(value, new Origin("", 0, 0), Context.FieldProblem) }]
      : Pintura.Markup.Parse(value, _ => new Origin("", 0, 0), Context);
    return paragraphs.Select(paragraph =>
      string.Concat(paragraph.Runs.Select(run => run.Field is null ? run.Text : Mark.ToString()))).ToList();
  }

  /// <summary>Each character of the text, paragraphs joined by a line break, with its bold, italic, link and styles.
  /// </summary>
  private static List<(char Character, string Format)> Formats(string value, bool plain)
  {
    List<Paragraph> paragraphs = plain
      ? [new Paragraph { Runs = Pintura.Markup.ParsePlain(value, new Origin("", 0, 0), Context.FieldProblem) }]
      : Pintura.Markup.Parse(value, _ => new Origin("", 0, 0), Context);
    return paragraphs.SelectMany((paragraph, index) => (index == 0 ? [] : new[] { ('\n', "") }).Concat(paragraph.Runs
      .SelectMany(run => (run.Field is null ? run.Text : Mark.ToString()).Select(character => (character,
        $"{(run.Bold == true ? "bold " : "")}{(run.Italic == true ? "italic " : "")}{run.Link} " +
        string.Join(' ', run.Styles ?? [])))))).ToList();
  }

  /// <summary>Where each character after an edit was before it, or -1 for one typed, as the patcher's diff aligns
  /// them: the page sends only text, so which of two like stretches a user edited is the diff's call.</summary>
  private static List<int> Aligned(List<string> before, List<string> after)
  {
    // As many paragraphs after as before are diffed one by one, and otherwise as one text.
    if (before.Count != after.Count)
    {
      return Aligned(string.Join('\n', before), string.Join('\n', after), 0);
    }
    var origins = new List<int>();
    int offset = 0;
    for (int index = 0; index < before.Count; index++)
    {
      if (index > 0)
      {
        origins.Add(offset - 1);
      }
      origins.AddRange(Aligned(before[index], after[index], offset));
      offset += before[index].Length + 1;
    }
    return origins;
  }

  private static List<int> Aligned(string before, string after, int offset)
  {
    var origins = new List<int>();
    int position = 0;
    foreach ((int start, int end, string typed) in Patcher.Hunks(before, after))
    {
      origins.AddRange(Enumerable.Range(offset + position, start - position));
      origins.AddRange(Enumerable.Repeat(-1, typed.Length));
      position = end;
    }
    origins.AddRange(Enumerable.Range(offset + position, before.Length - position));
    return origins;
  }

  /// <summary>The first character the edit left alone whose formatting changed, or null. A space may leave
  /// emphasis, as markup can't open onto one.</summary>
  private static string? Reformatted(List<(char Character, string Format)> before,
    List<(char Character, string Format)> after, List<int> origins)
  {
    for (int index = 0; index < after.Count; index++)
    {
      int origin = origins[index];
      if (origin >= 0 && after[index].Character != ' ' && before[origin].Format != after[index].Format)
      {
        return $"'{after[index].Character}' at {index} was '{before[origin].Format}', is '{after[index].Format}'";
      }
    }
    return null;
  }

  #endregion
}