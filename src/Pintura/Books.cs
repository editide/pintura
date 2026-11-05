using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>A line as <c>libreta --lines</c> reads it: every period its folder has, in natural direction.</summary>
public record BookLine(string Name, string Label, string? Units, List<string> Columns, List<string?> Exact,
  List<string> Display);

public record BookColumn(string Name, string Label);

/// <summary>A row of a Libreta view: a line with its values, or a <c>header</c> or <c>spacer</c> with none.
/// <c>Indent</c> counts the steps its label sits in from the view's edge, and <c>Exact</c> takes the view's sign, as
/// <c>Display</c> does. <c>Cells</c> names the book cell behind each value when it isn't the row's line in its
/// column's period, as in a comparison, whose rows are books.</summary>
public record BookRow(string? Name, string? Label, string Kind, string? Style, int Indent, List<string?>? Exact,
  List<string>? Display, List<BookCell>? Cells = null);

public record BookCell(string Line, string Column);

public record BookView(string Id, List<BookColumn> Columns, List<BookRow> Rows);

/// <summary>A book cell a number on a slide reads, which clicking it opens in Libreta.</summary>
public record TracePayload(string Book, string Line, string Column);

/// <summary>The book's page at a traced cell, and how many of its open pages selected the cell.</summary>
public record LibretaPage(string Url, int Pages);

/// <summary>A book that can't be read: Libreta missing, or failing on the book.</summary>
public class BookException(string message) : Exception(message);

/// <summary>Reads Libreta books for the loader, which asks for a book's lines once per load and a view once per
/// table.</summary>
public interface IBookReader
{
  IReadOnlyDictionary<string, BookLine> Lines(string root);
  BookView View(string root, string id);

  /// <summary>The whole book as one page that needs no server, as <c>libreta --export</c> writes it.</summary>
  string Page(string root);

  /// <summary>A reader for one load, which may take each book as it first finds it rather than checking it again.
  /// </summary>
  IBookReader Session()
  {
    return this;
  }
}

/// <summary>Runs the Libreta CLI, <c>PINTURA_LIBRETA</c> or <c>libreta</c> on the path, and keeps each answer until a
/// file in the book changes, so a reload for a slide's edit costs no Libreta start.</summary>
public class LibretaReader : IBookReader
{
  public static readonly LibretaReader Shared = new LibretaReader();

  /// <summary>Reads the copies of books extracted for revisions, each in a folder of its own, so that the versions
  /// sharing a book share its answers.</summary>
  public static readonly LibretaReader Extracted = new LibretaReader(byContent: true);

  private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
  private readonly Dictionary<string, (string Stamp, object Answer)> answers =
    new Dictionary<string, (string, object)>(StringComparer.Ordinal);
  private readonly object gate = new object();
  private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
  private readonly string executable;
  private readonly TimeSpan runTimeout;
  private readonly bool byContent;

  /// <param name="byContent">Whether to know a book by what its files hold rather than where they are and when they
  /// were written, which costs reading them but answers every copy of a book from one Libreta run.</param>
  public LibretaReader(string? executable = null, TimeSpan? runTimeout = null, bool byContent = false)
  {
    this.executable = executable ?? Environment.GetEnvironmentVariable("PINTURA_LIBRETA") ?? "libreta";
    this.runTimeout = runTimeout ?? TimeSpan.FromMinutes(2);
    this.byContent = byContent;
  }

  /// <summary>Checks whether a book changed once per book, rather than per question: a load asks one book dozens of
  /// times, and each check walks the book.</summary>
  public IBookReader Session()
  {
    return new BookSession(this);
  }

  private sealed class BookSession(LibretaReader reader) : IBookReader
  {
    private readonly ConcurrentDictionary<string, string> stamps = new ConcurrentDictionary<string, string>();

    public IReadOnlyDictionary<string, BookLine> Lines(string root)
    {
      return reader.Lines(root, stamps.GetOrAdd(root, reader.Stamp));
    }

    public BookView View(string root, string id)
    {
      return reader.View(root, id, stamps.GetOrAdd(root, reader.Stamp));
    }

    public string Page(string root)
    {
      return reader.Page(root, stamps.GetOrAdd(root, reader.Stamp));
    }


  }

  public IReadOnlyDictionary<string, BookLine> Lines(string root)
  {
    return Lines(root, Stamp(root));
  }

  public BookView View(string root, string id)
  {
    return View(root, id, Stamp(root));
  }

  public string Page(string root)
  {
    return Page(root, Stamp(root));
  }

  private string Page(string root, string stamp)
  {
    return Cached(root, stamp, "page", () =>
    {
      string file = Temporary.Folder("books", $"{Guid.NewGuid():N}.html");
      Directory.CreateDirectory(Path.GetDirectoryName(file)!);
      try
      {
        Run(root, "--export", file);
        return File.ReadAllText(file);
      }
      finally
      {
        File.Delete(file);
      }
    });
  }

  private IReadOnlyDictionary<string, BookLine> Lines(string root, string stamp)
  {
    return Cached(root, stamp, "lines", () =>
    {
      List<LineAnswer> lines = Answer<List<LineAnswer>>(Run(root, "--lines", "--json"));
      return lines
        .Where(line => line.Error is null)
        .ToDictionary(line => line.Name, line => new BookLine(line.Name, line.Label ?? line.Name, line.Units,
          line.Columns ?? [], line.Exact ?? [], line.Display ?? []), StringComparer.Ordinal);
    });
  }

  private BookView View(string root, string id, string stamp)
  {
    if (!Argument(id))
    {
      throw new BookException($"{id} isn't a view Libreta can show");
    }
    return Cached(root, stamp, $"view {id}", () =>
    {
      ViewAnswer view = Answer<ViewAnswer>(Run(root, "--view", id, "--json"));
      return new BookView(view.Id, view.Columns.Select(column => new BookColumn(column.Name, column.Label)).ToList(),
        view.Rows.Select(row => new BookRow(row.Name, row.Label, row.Kind, row.Style, row.Indent ?? 0,
          Signed(row.Exact, row.Contra), row.Display, row.Cells))
          .ToList());
    });
  }

  /// <summary>Has the book's open pages select a cell, and answers the page's address at it, in the server already
  /// running the book or a new one, which outlives this process. Libreta opens no tab, since only the page tracing the
  /// cell can bring one forward; it prints to this process's terminal, and fails fast when it can't find the cell.
  /// </summary>
  public LibretaPage Open(string root, string line, string column)
  {
    if (!Argument(line) || !Argument(column))
    {
      throw new BookException($"{line} in {column} isn't a cell Libreta can open");
    }
    var start = new ProcessStartInfo(executable)
    {
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    foreach (string argument in new[] { root, "--cell", line, column, "--no-open", "--json" })
    {
      start.ArgumentList.Add(argument);
    }
    Process process = Start(start);
    var answer = new TaskCompletionSource<LibretaPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
    process.OutputDataReceived += (_, received) =>
    {
      if (received.Data is not string text)
      {
        answer.TrySetResult(null);
      }
      else if (!answer.Task.IsCompleted && text.StartsWith('{'))
      {
        answer.TrySetResult(JsonSerializer.Deserialize<LibretaPage>(text, Json));
      }
      else
      {
        Console.WriteLine(text);
      }
    };
    var reason = new StringBuilder();
    process.ErrorDataReceived += (_, received) =>
    {
      if (received.Data is string text)
      {
        Console.Error.WriteLine(text);
        lock (reason)
        {
          reason.AppendLine(text);
        }
      }
    };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    // A server Libreta starts answers once it listens, and keeps the process, whose log passes through this one's.
    if (!answer.Task.Wait(OpenTimeout))
    {
      throw new BookException($"Libreta didn't answer for {line} in {column} within {OpenTimeout.TotalSeconds} s");
    }
    if (answer.Task.Result is LibretaPage page)
    {
      return page;
    }
    // Waiting without a timeout drains the last lines of standard error.
    process.WaitForExit();
    int exitCode = process.ExitCode;
    process.Dispose();
    string problem;
    lock (reason)
    {
      problem = reason.ToString().Trim();
    }
    if (problem.StartsWith("Unknown option '--no-open'", StringComparison.Ordinal))
    {
      throw new BookException("this Libreta is too old to open a cell in a tab Pintura holds; update it");
    }
    throw new BookException($"Libreta couldn't open {line} in {column}" +
      (problem.Length > 0 ? $": {problem}" : $"; it exited {exitCode}"));
  }

  private static T Answer<T>(string output) where T : class
  {
    try
    {
      return JsonSerializer.Deserialize<T>(output, Json) ?? throw new JsonException("null");
    }
    catch (JsonException exception)
    {
      throw new BookException($"Libreta answered with what Pintura can't read ({exception.Message}); update one " +
        "or the other");
    }
  }

  // Libreta would read a name starting with a dash as one of its options rather than a line, column, or view.
  private static bool Argument(string name)
  {
    return name.Length > 0 && !name.StartsWith('-') && !name.Any(char.IsControl);
  }

  private T Cached<T>(string root, string stamp, string key, Func<T> read) where T : class
  {
    // Known by content, a book's answers hold no path, so they serve any folder holding the same files.
    string entry = $"{(byContent ? stamp : root)}\n{key}";
    lock (gate)
    {
      if (answers.TryGetValue(entry, out (string Stamp, object Answer) cached) && cached.Stamp == stamp)
      {
        return (T)cached.Answer;
      }
    }
    T answer = read();
    lock (gate)
    {
      answers[entry] = (stamp, answer);
    }
    return answer;
  }

  /// <summary>The path, size, and write time of every file Libreta reads of a book, which move whenever a fact or
  /// formula does; known by content, each file's path from the book and a hash of what it holds.</summary>
  private string Stamp(string root)
  {
    var text = new StringBuilder();
    foreach (string path in BookFiles(root))
    {
      if (byContent)
      {
        text.Append(Path.GetRelativePath(root, path)).Append('\t')
          .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
      }
      else
      {
        var file = new FileInfo(path);
        text.Append(path).Append('\t').Append(file.Length).Append('\t').Append(file.LastWriteTimeUtc.Ticks);
      }
      text.Append('\n');
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
  }

  /// <summary>The files Libreta reads of a book: its own, but for a presentation kept inside it, and the
  /// <c>formats.yaml</c> above it as far as the Git worktree's root.</summary>
  private static IEnumerable<string> BookFiles(string root)
  {
    var paths = new List<string>();
    Read(new DirectoryInfo(root));
    for (DirectoryInfo? folder = new DirectoryInfo(root).Parent; folder is not null; folder = folder.Parent)
    {
      string formats = Path.Combine(folder.FullName, "formats.yaml");
      if (File.Exists(formats))
      {
        paths.Add(formats);
      }
      string marker = Path.Combine(folder.FullName, ".git");
      if (Directory.Exists(marker) || File.Exists(marker))
      {
        break;
      }
    }

    void Read(DirectoryInfo folder)
    {
      paths.AddRange(folder.EnumerateFiles().Select(file => file.FullName));
      foreach (DirectoryInfo child in folder.EnumerateDirectories())
      {
        if (child.Name != ".git" && !File.Exists(Path.Combine(child.FullName, "presentation.yaml")))
        {
          Read(child);
        }
      }
    }

    return paths.Order(StringComparer.Ordinal);
  }

  private string Run(string root, params string[] arguments)
  {
    var start = new ProcessStartInfo(executable)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false
    };
    start.ArgumentList.Add(root);
    foreach (string argument in arguments)
    {
      start.ArgumentList.Add(argument);
    }
    using (Process process = Start(start))
    {
      Task<string> error = process.StandardError.ReadToEndAsync();
      Task<string> reading = process.StandardOutput.ReadToEndAsync();
      // A load waits on the answer, and the preview's reloads wait on the load.
      if (!process.WaitForExit(runTimeout))
      {
        process.Kill(entireProcessTree: true);
        throw new BookException($"Libreta didn't answer within {runTimeout.TotalSeconds:0} s");
      }
      string output = reading.Result;
      // --lines exits 1 over a line that fails to evaluate, and still prints every line.
      if (output.TrimStart().StartsWith('[') || (process.ExitCode == 0 && output.Length > 0))
      {
        return output;
      }
      string problem = error.Result.Trim();
      throw new BookException(problem.Length > 0 ? $"Libreta: {problem}" : $"Libreta exited {process.ExitCode}");
    }
  }

  private Process Start(ProcessStartInfo start)
  {
    try
    {
      return Process.Start(start)!;
    }
    catch (System.ComponentModel.Win32Exception)
    {
      string install = OperatingSystem.IsWindows()
        ? "irm https://raw.githubusercontent.com/editide/libreta/main/install.ps1 | iex"
        : "curl -fsSL https://raw.githubusercontent.com/editide/libreta/main/install.sh | sh";
      throw new BookException($"reading a book needs Libreta, and there is no {executable} on the path; install it " +
        $"with `{install}`, or point PINTURA_LIBRETA at it");
    }
  }

  private record LineAnswer(string Name, string? Error, string? Label, string? Units, List<string>? Columns,
    List<string?>? Exact, List<string>? Display);

  private record ViewAnswer(string Id, List<ColumnAnswer> Columns, List<RowAnswer> Rows);

  private record ColumnAnswer(string Name, string Label);

  private record RowAnswer(string? Name, string? Label, string Kind, string? Style, int? Indent, List<string?>? Exact,
    List<string>? Display, List<bool>? Contra, List<BookCell>? Cells);

  /// <summary>Exact values as the view shows them, negating each cell Libreta marks contra.</summary>
  private static List<string?>? Signed(List<string?>? exact, List<bool>? contra)
  {
    if (exact is null || contra is null)
    {
      return exact;
    }
    return exact.Select((value, column) => value is null || !contra[column] || value == "0"
      ? value
      : value.StartsWith('-') ? value[1..] : $"-{value}").ToList();
  }


}

/// <summary><c>book::line["column"]</c>, Libreta's reference to one cell after the book's id, with an optional
/// <c>| format</c> from <c>styles.yaml</c>. <see cref="ToString"/> is the field name a run keeps.</summary>
public partial record Reference(string Book, string Line, string Column, string? Format)
{
  public static bool Looks(string text)
  {
    return text.Contains("::", StringComparison.Ordinal);
  }

  /// <summary>The cell a run's field reads, when the field is a reference.</summary>
  public static TracePayload? Trace(string? field)
  {
    return field is not null && Looks(field) && Parse(field) is Reference reference
      ? new TracePayload(reference.Book, reference.Line, reference.Column)
      : null;
  }

  public static Reference? Parse(string text)
  {
    Match match = Pattern().Match(text);
    return match.Success
      ? new Reference(match.Groups["book"].Value, match.Groups["line"].Value, match.Groups["column"].Value,
        match.Groups["format"].Success ? match.Groups["format"].Value : null)
      : null;
  }

  /// <summary>The number the reference shows: its format's spelling of the exact value, or Libreta's own.</summary>
  public static string? Spell(BookLine line, int column, NumberFormat? format)
  {
    if (format is null)
    {
      return line.Display[column];
    }
    return PlainNumber.TryParse(line.Exact[column], out double value)
      ? format.Spell(value)
      : null;
  }

  public override string ToString()
  {
    return $"{Book}::{Line}[\"{Column}\"]{(Format is null ? "" : $" | {Format}")}";
  }

  [GeneratedRegex(@"^\s*(?<book>[A-Za-z][A-Za-z0-9_-]*)::(?<line>[A-Za-z_][A-Za-z0-9_.:]*)" +
    @"\s*\[\s*""(?<column>[^""]+)""\s*\]\s*(?:\|\s*(?<format>[A-Za-z_][A-Za-z0-9_]*)\s*)?$")]
  private static partial Regex Pattern();


}