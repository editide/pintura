using System.Diagnostics;
using Shouldly;

namespace Pintura.Tests;

/// <summary>Shared repository path and git helpers for tests.</summary>
public static class Repository
{
  public static string Root { get; } = FindRoot();

  private static string FindRoot()
  {
    DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
    {
      directory = directory.Parent;
    }
    return directory?.FullName ?? throw new InvalidOperationException("no repository root above the tests");
  }
}

/// <summary>Git repository helpers shared across tests, outside any xUnit test class.</summary>
public static class GitHelper
{
  /// <summary>Initializes a repository if needed, stages <paramref name="paths"/> (or everything), and commits.
  /// </summary>
  public static void Commit(string folder, string message = "first", params string[] paths)
  {
    if (!Directory.Exists(Path.Combine(folder, ".git")))
    {
      Git(folder, "init", "-q");
    }
    Git(folder, paths.Length == 0 ? ["add", "."] : ["add", .. paths]);
    Git(folder, "commit", "-q", "-m", message);
  }

  /// <summary>A slide long enough that git still takes it for the same file once it's retitled and renamed.</summary>
  public static string Body(string title)
  {
    return $"""
      layout: content
      title: {title}
      elements:
        - id: note
          text: A note that stays as it was
          frame: [40, 400, 300, 40]
        - id: source
          text: A source that stays as it was
          frame: [40, 460, 300, 20]
      """;
  }

  /// <summary>Commits a slide's file moved to another name, as <see cref="Body"/> with a title when given one.
  /// </summary>
  public static void Rename(DeckFixture deck, string from, string to, string? title = null)
  {
    Git(deck.Folder.Root, "mv", $"deck/slides/{from}.yaml", $"deck/slides/{to}.yaml");
    string manifest = deck.Folder.PathTo("deck/presentation.yaml");
    File.WriteAllText(manifest, File.ReadAllText(manifest).Replace($"- {from}\n", $"- {to}\n"));
    if (title is not null)
    {
      deck.Write($"deck/slides/{to}.yaml", Body(title));
    }
    Git(deck.Folder.Root, "commit", "-q", "-am", $"renamed {from} to {to}");
  }

  public static void Git(string folder, params string[] arguments)
  {
    var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true };
    foreach (string argument in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com" }
      .Concat(arguments))
    {
      start.ArgumentList.Add(argument);
    }
    using Process process = Process.Start(start)!;
    // Read while git runs, or a full pipe of warnings, such as Windows' line ending ones, stalls both.
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    process.ExitCode.ShouldBe(0, error);
    // A machine's autocrlf would otherwise decide the line endings a revision comes back with.
    if (arguments[0] == "init")
    {
      Git(folder, "config", "core.autocrlf", "false");
    }
  }


}
