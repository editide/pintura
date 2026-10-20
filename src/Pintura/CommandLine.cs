namespace Pintura;

public enum HeadlessCommand { List, Slide, Element, Screenshot, Check, Url, Export, ExportHtml, ExportPptx, Diff, Comments }

public enum AccountCommand { Login, Logout, WhoAmI }

public class CommandLineOptions
{
  /// <summary>Null when no root was given, leaving the presentation to be discovered below the working folder.
  /// </summary>
  public string? Root { get; private init; }
  public int Port { get; private init; } = 5180;
  /// <summary>False means the port is a default the host may step past when it is already taken.</summary>
  public bool PortSpecified { get; private init; }
  /// <summary>Leaves opening the page to the caller, as a tool linking to the presentation does.</summary>
  public bool NoOpen { get; private init; }
  public HeadlessCommand? Command { get; private init; }
  public string? Slide { get; private init; }
  public string? Element { get; private init; }
  /// <summary>Where <c>--screenshot</c> writes, or null for the temporary folder, and the file <c>--export-html</c>
  /// or <c>--export</c> writes.</summary>
  public string? Output { get; private init; }
  public bool Json { get; private init; }
  /// <summary>What <c>--diff</c> compares: a revision against the working tree, or <c>a..b</c> for two revisions.
  /// Alone it lists the changes; with <c>--check</c> or <c>--screenshot</c> it narrows or pairs what they show.
  /// </summary>
  public string? Revision { get; private init; }
  /// <summary>Adds the export's gaps to <c>--check</c>.</summary>
  public bool Export { get; private init; }
  public bool Help { get; private init; }
  /// <summary>Empty for the index, otherwise a topic name.</summary>
  public string? Documentation { get; private init; }
  /// <summary>The model's JSON <c>--import</c> reads, and the folder it writes.</summary>
  public string? Import { get; private init; }
  public string? ImportFolder { get; private init; }
  /// <summary>Makes <c>--import</c> write only the theme.</summary>
  public bool ThemeOnly { get; private init; }
  /// <summary>The slides <c>--export</c> or <c>--import</c> takes, or null for all of them.</summary>
  public SlideSelection? Slides { get; private init; }
  public AccountCommand? Account { get; private init; }
  /// <summary>The folder <c>--new</c> writes a presentation into.</summary>
  public string? New { get; private init; }
  public bool Version { get; private init; }

  public static CommandLineOptions Parse(string[] arguments)
  {
    string? root = null;
    int first = 0;
    if (arguments.Length > 0 && !arguments[0].StartsWith('-'))
    {
      root = arguments[0];
      first = 1;
    }

    int port = 5180;
    bool portSpecified = false;
    bool noOpen = false;
    HeadlessCommand? command = null;
    string? slide = null;
    string? element = null;
    string? output = null;
    string? revision = null;
    bool json = false;
    bool export = false;
    string? documentation = null;
    string? import = null;
    string? importFolder = null;
    bool themeOnly = false;
    SlideSelection? slides = null;
    for (int index = first; index < arguments.Length; index++)
    {
      string argument = arguments[index];
      switch (argument)
      {
        case "--port":
          Require(arguments, index, 1);
          portSpecified = true;
          if (!int.TryParse(arguments[++index], out port) || port is < 1 or > 65535)
          {
            throw new ArgumentException("--port requires a number from 1 through 65535.");
          }
          break;
        case "--no-open":
          noOpen = true;
          break;
        case "--list":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.List;
          break;
        case "--slide":
          RequireNoCommand(command, argument);
          Require(arguments, index, 1);
          command = HeadlessCommand.Slide;
          slide = arguments[++index];
          break;
        case "--element":
          RequireNoCommand(command, argument);
          Require(arguments, index, 2);
          command = HeadlessCommand.Element;
          slide = arguments[++index];
          element = arguments[++index];
          break;
        case "--screenshot":
          RequireNoCommand(command, argument);
          Require(arguments, index, 1);
          command = HeadlessCommand.Screenshot;
          slide = arguments[++index];
          break;
        case "--output":
          Require(arguments, index, 1);
          output = arguments[++index];
          break;
        case "--check":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Check;
          break;
        case "--comments":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Comments;
          break;
        case "--url":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Url;
          break;
        case "--export-json":
          RequireNoCommand(command, argument);
          command = HeadlessCommand.Export;
          break;
        case "--export-html":
          RequireNoCommand(command, argument);
          Require(arguments, index, 1);
          command = HeadlessCommand.ExportHtml;
          output = arguments[++index];
          break;
        case "--diff":
          if (revision is not null)
          {
            throw new ArgumentException("Use --diff only once.");
          }
          revision = index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-')
            ? arguments[++index]
            : "HEAD";
          break;
        case "--json":
          json = true;
          break;
        case "--export":
          // A file makes it the command; alone it adds the export's gaps to --check.
          if (index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-'))
          {
            RequireNoCommand(command, argument);
            command = HeadlessCommand.ExportPptx;
            output = arguments[++index];
          }
          else
          {
            export = true;
          }
          break;
        case "--docs":
          if (documentation is not null)
          {
            throw new ArgumentException("Use --docs only once.");
          }
          documentation = "";
          if (index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-'))
          {
            documentation = arguments[++index];
            if (!Pintura.Documentation.Topics.Contains(documentation))
            {
              throw new ArgumentException($"Unknown documentation topic '{documentation}'. Available: " +
                $"{string.Join(", ", Pintura.Documentation.Topics)}.");
            }
          }
          break;
        case "--import":
          Require(arguments, index, 2);
          import = arguments[++index];
          importFolder = arguments[++index];
          break;
        case "--theme":
          themeOnly = true;
          break;
        case "--slides":
          Require(arguments, index, 1);
          slides = SlideSelection.Parse(arguments[++index]);
          break;
        case "--login":
        case "--logout":
        case "--whoami":
          if (arguments.Length != 1)
          {
            throw new ArgumentException($"{argument} cannot be combined with a presentation or another option.");
          }
          return new CommandLineOptions
          {
            Account = argument switch
            {
              "--login" => AccountCommand.Login,
              "--logout" => AccountCommand.Logout,
              _ => AccountCommand.WhoAmI
            }
          };
        case "--new":
          if (arguments.Length != 2 || index != 0)
          {
            throw new ArgumentException("--new takes the folder to create and nothing else.");
          }
          return new CommandLineOptions { New = arguments[1] };
        case "--version":
          if (arguments.Length != 1)
          {
            throw new ArgumentException("--version cannot be combined with a presentation or another option.");
          }
          return new CommandLineOptions { Version = true };
        case "--help":
        case "-h":
          return new CommandLineOptions { Help = true };
        default:
          throw new ArgumentException($"Unknown option '{argument}'.");
      }
    }

    if (revision is not null)
    {
      if (command is null)
      {
        command = HeadlessCommand.Diff;
      }
      else if (command is not (HeadlessCommand.Check or HeadlessCommand.Screenshot))
      {
        throw new ArgumentException("--diff combines only with --check and --screenshot.");
      }
    }
    if (command is not null && portSpecified)
    {
      throw new ArgumentException("--port cannot be combined with --list, --slide, --element, --screenshot, " +
        "--check, --comments, --url, --export, --export-json, --export-html or --diff.");
    }
    if (noOpen && (command is not null || documentation is not null || import is not null))
    {
      throw new ArgumentException("--no-open leaves the server's page to the caller, so it cannot be combined with a " +
        "command that starts no server.");
    }
    if ((command is null or HeadlessCommand.Screenshot or HeadlessCommand.Url or HeadlessCommand.Export or
      HeadlessCommand.ExportHtml or HeadlessCommand.ExportPptx) && json)
    {
      throw new ArgumentException("--json requires --list, --slide, --element, --check, --comments or --diff.");
    }
    if (command is not HeadlessCommand.Check && export)
    {
      throw new ArgumentException("--export requires --check.");
    }
    if (command is not (HeadlessCommand.Screenshot or HeadlessCommand.ExportHtml or HeadlessCommand.ExportPptx) &&
      output is not null)
    {
      throw new ArgumentException("--output requires --screenshot.");
    }
    if (documentation is not null && (root is not null || command is not null || portSpecified || json ||
      export || output is not null))
    {
      throw new ArgumentException("--docs cannot be combined with a presentation or another option.");
    }
    if (import is not null && (root is not null || command is not null || revision is not null || portSpecified ||
      json || export || output is not null || documentation is not null))
    {
      throw new ArgumentException("--import cannot be combined with a presentation or another option but --theme " +
        "or --slides.");
    }
    if (themeOnly && import is null)
    {
      throw new ArgumentException("--theme requires --import.");
    }
    if (slides is not null && (themeOnly || import is null && command is not HeadlessCommand.ExportPptx))
    {
      throw new ArgumentException("--slides requires --export <deck.pptx> or --import without --theme.");
    }
    return new CommandLineOptions
    {
      Root = root,
      Port = port,
      PortSpecified = portSpecified,
      NoOpen = noOpen,
      Command = command,
      Slide = slide,
      Element = element,
      Output = output,
      Revision = revision,
      Json = json,
      Export = export,
      Documentation = documentation,
      Import = import,
      ImportFolder = importFolder,
      ThemeOnly = themeOnly,
      Slides = slides
    };
  }

  public static void PrintUsage(TextWriter writer)
  {
    writer.WriteLine("usage:");
    writer.WriteLine("  pintura --new <folder>   (a presentation with the default theme copied in, to change as it " +
      "needs)");
    writer.WriteLine("  pintura [<presentation>] [--port <n>] [--no-open]   (without --port, the first free port from " +
      "5180; --no-open leaves opening the page to the caller)");
    writer.WriteLine("  pintura [<presentation>] --list [--json]   (slides with their layouts and titles)");
    writer.WriteLine("  pintura [<presentation>] --slide <slide> [--json]   (its elements and their frames)");
    writer.WriteLine("  pintura [<presentation>] --element <slide> <id> [--json]   (effective properties, " +
      "each with the file and line that set it)");
    writer.WriteLine("  pintura [<presentation>] --screenshot <slide>|all|theme [--diff [<rev>]] [--output <png>]   " +
      "(the slide as a PNG, a contact sheet of every slide, or the preview's theme view; --diff draws a slide before " +
      "and after, with the changes marked)");
    writer.WriteLine("  pintura [<presentation>] --check [--export] [--diff [<rev>|<a>..<b>]] [--json]   (structure, " +
      "text, geometry, and consistency findings; --export adds what the PowerPoint export can't reproduce, and " +
      "--diff keeps only those the change introduced)");
    writer.WriteLine("  pintura [<presentation>] --comments [--json]   (reviewers' comments in slide order, each " +
      "beside the file and line of what it points at)");
    writer.WriteLine("  pintura [<presentation>] --url   (the address of the server already running it)");
    writer.WriteLine("  pintura [<presentation>] --export <deck.pptx> [--slides <list>]   (a PowerPoint deck written " +
      "on editide, with what it can't reproduce; --slides takes ids, numbers, and ranges such as cover,3-5)");
    writer.WriteLine("  pintura [<presentation>] --export-json   (the model the exporter reads: styles resolved, " +
      "theme colors and fonts kept as references)");
    writer.WriteLine("  pintura [<presentation>] --export-html <file>   (one HTML file that shows the slides with no " +
      "server, for sending)");
    writer.WriteLine("  pintura [<presentation>] --diff [<rev>|<a>..<b>] [--json]   (what changed since a git " +
      "revision, HEAD by default, or between two)");
    writer.WriteLine("  pintura --import <deck.pptx>|<deck.json> <folder> [--theme|--slides <list>]   (a " +
      "presentation folder with its theme inside, from a PowerPoint deck or the model's JSON as --export-json writes " +
      "it; --theme writes the theme alone, and --slides only the slides it names)");
    writer.WriteLine("  pintura --docs [<topic>]   (the format, one topic at a time)");
    writer.WriteLine("  pintura --version");
    writer.WriteLine("  pintura --login   (signs in with an API key from editide's dashboard, read from standard " +
      "input; EDITIDE_API_KEY stands in for it)");
    writer.WriteLine("  pintura --logout");
    writer.WriteLine("  pintura --whoami   (the plan the key is on, and the slides exported this billing period)");
    writer.WriteLine("  pintura --install-browser   (fetches the headless Chromium a machine without Edge or Chrome " +
      "would fetch on its first --screenshot or --check, through HTTPS_PROXY if set)");
    writer.WriteLine();
    writer.WriteLine("--export and --import run on editide's API with an API key; everything else needs no account");
    writer.WriteLine("without a presentation, the one presentation up to three folders below the working folder");
    writer.WriteLine("the server opens a browser tab, reuses one already serving the presentation, and stops with " +
      "no tab open");
    writer.WriteLine("--screenshot and --check borrow its headless browser, or start a headless server that stops " +
      "after five idle minutes");
    writer.WriteLine("screenshot through --screenshot rather than a browser of your own, which may lack the fonts and " +
      "settings Pintura gives its own, and draw differently from --check and the export");
  }

  private static void Require(string[] arguments, int optionIndex, int valueCount)
  {
    if (optionIndex + valueCount >= arguments.Length ||
      arguments.Skip(optionIndex + 1).Take(valueCount).Any(argument => argument.StartsWith('-')))
    {
      throw new ArgumentException($"{arguments[optionIndex]} requires {valueCount} value(s).");
    }
  }

  private static void RequireNoCommand(HeadlessCommand? command, string argument)
  {
    if (command is not null)
    {
      throw new ArgumentException(
        "Use only one of --list, --slide, --element, --screenshot, --check, --comments, --url, --export, " +
        $"--export-json or --export-html; found '{argument}' after another.");
    }
  }


}

/// <summary>The one presentation at or below the working folder. Descent stops at a presentation, and the depth cap
/// keeps a run from the wrong folder off the rest of the disk.</summary>
public static class PresentationDiscovery
{
  private const int MaximumDepth = 3;

  public static List<string> Find(string start)
  {
    var roots = new List<string>();
    Descend(new DirectoryInfo(start), 0, roots);
    roots.Sort(StringComparer.Ordinal);
    return roots;
  }

  private static void Descend(DirectoryInfo folder, int depth, List<string> roots)
  {
    if (File.Exists(Path.Combine(folder.FullName, "presentation.yaml")))
    {
      roots.Add(folder.FullName);
      return;
    }
    if (depth == MaximumDepth)
    {
      return;
    }
    DirectoryInfo[] children;
    try
    {
      children = folder.GetDirectories();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      return;
    }
    foreach (DirectoryInfo child in children.Where(Searchable))
    {
      Descend(child, depth + 1, roots);
    }
  }

  private static bool Searchable(DirectoryInfo folder)
  {
    return !folder.Name.StartsWith('.') &&
      folder.Name is not ("bin" or "obj" or "node_modules") &&
      (folder.Attributes & FileAttributes.ReparsePoint) == 0;
  }


}