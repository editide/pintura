using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class RevisionTests
{
  [Fact]
  public void A_revision_loads_as_committed_with_origins_named_as_in_the_working_tree()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");

    // Act
    Presentation committed = Revisions.Load(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root);

    // Assert
    Slide two = committed.Slides.Single(slide => slide.Id == "two");
    PayloadBuilder.Title(committed, two).ShouldBe("Results in Results");
    two.Title!.Origin.File.ShouldBe("deck/slides/two.yaml");
    committed.Theme.Display.ShouldBe("theme");
  }

  [Fact]
  public void A_revision_loads_pictures_from_outside_the_presentation_and_its_theme()
  {
    // Arrange
    using var deck = new DeckFixture();
    const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>";
    deck.Write("assets/crest.svg", svg);
    deck.Write("assets/logo.svg", svg);
    deck.Write("theme/layouts/cover.yaml", """
      name: Cover
      elements:
        - { id: crest, picture: ../../assets/crest.svg, frame: [880, 480, 40, 40] }
      """);
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - { id: logo, picture: ../../assets/logo.svg, frame: [880, 500, 60, 20] }
      """);
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");

    // Act
    Presentation committed = Revisions.Load(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root);

    // Assert
    committed.Slides.Count.ShouldBe(2);
  }

  [Fact]
  public void A_repository_reached_through_a_symlink_loads_its_revision()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    // As macOS reaches its temporary folder through /var, a symlink to /private/var.
    string link = $"{deck.Folder.Root}-link";
    Directory.CreateSymbolicLink(link, deck.Folder.Root);

    try
    {
      // Act
      Presentation committed = Revisions.Load(Path.Combine(link, "deck"), "HEAD", link);

      // Assert
      committed.Slides.Count.ShouldBe(2);
      committed.Theme.Display.ShouldBe("theme");
    }
    finally
    {
      Directory.Delete(link);
    }
  }

  [Fact]
  public void Outside_a_repository_a_revision_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    InvalidDataException error = Should.Throw<InvalidDataException>(() =>
      Revisions.Load(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root));

    // Assert
    error.Message.ShouldContain("not in a git repository");
  }

  [Fact]
  public void An_unknown_revision_is_named()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");

    // Act
    InvalidDataException error = Should.Throw<InvalidDataException>(() =>
      Revisions.Load(deck.Folder.PathTo("deck"), "nowhere", deck.Folder.Root));

    // Assert
    error.Message.ShouldBe("no revision 'nowhere'");
  }

  [Fact]
  public void Versions_are_the_commits_that_changed_the_presentation_or_its_theme_newest_first()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "config", "user.email", "test@example.com");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("notes.txt", "elsewhere");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "unrelated");
    deck.Write("theme/styles.yaml", DeckFixture.Styles + "\ncaption: { size: 9 }");
    Git(deck.Folder.Root, "commit", "-q", "-am", "smaller captions");
    Git(deck.Folder.Root, "tag", "sent");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");

    // Act
    List<DeckVersion> versions = Revisions.Versions(deck.Folder.PathTo("deck"));

    // Assert
    versions.Select(version => version.Subject).ShouldBe(["retitled", "smaller captions", "first"]);
    versions[1].Tags.ShouldBe(["sent"]);
    versions[0].Author.ShouldBe("Test");
    versions[0].Mine.ShouldBeTrue();
    versions[0].Commit.ShouldBe(Revisions.Commit(deck.Folder.Root, "HEAD"));
  }

  [Fact]
  public void A_commit_changing_only_comments_is_no_version()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/comments.yaml", "- { id: a1, slide: two, at: [0, 0], text: Hi }");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "reviewed");

    // Act
    List<DeckVersion> versions = Revisions.Versions(deck.Folder.PathTo("deck"));

    // Assert
    versions.Select(version => version.Subject).ShouldBe(["first"]);
  }

  [Fact]
  public void Outside_a_repository_there_are_no_versions()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    List<DeckVersion> versions = Revisions.Versions(deck.Folder.PathTo("deck"));

    // Assert
    versions.ShouldBeEmpty();
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