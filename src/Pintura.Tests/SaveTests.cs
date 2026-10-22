using Shouldly;
using Xunit;
using static Pintura.Tests.RevisionTests;

namespace Pintura.Tests;

public class SaveTests
{
  [Fact]
  public void Versions_are_numbered_from_the_first_and_skip_commits_that_left_the_presentation_alone()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("notes.txt", "elsewhere");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "unrelated");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");
    string first = Revisions.Versions(deck.Folder.PathTo("deck"))[1].Commit;

    // Act
    List<DeckVersion> versions = Revisions.Versions(deck.Folder.PathTo("deck"));
    DeckVersion described = Revisions.Describe(deck.Folder.PathTo("deck"), first);

    // Assert
    versions.Select(version => version.Number).ShouldBe([2, 1]);
    described.Number.ShouldBe(1);
  }

  [Fact]
  public void An_edit_leaves_the_presentation_unsaved_and_a_comment_does_not()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    SaveStatus clean = Saves.Status(root);
    deck.Write("deck/comments.yaml", "- { id: a1, slide: two, at: [0, 0], text: Hi }");
    SaveStatus commented = Saves.Status(root);
    deck.Write("theme/styles.yaml", DeckFixture.Styles + "\ncaption: { size: 9 }");

    // Act
    SaveStatus edited = Saves.Status(root);

    // Assert
    clean.ShouldBe(new SaveStatus(false, 2));
    commented.Unsaved.ShouldBeFalse();
    edited.ShouldBe(new SaveStatus(true, 2));
  }

  [Fact]
  public void Saving_commits_the_presentation_and_its_theme_and_nothing_else_staged_or_not()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    deck.Write("deck/slides/three.yaml", "layout: content\ntitle: New");
    deck.Write("theme/styles.yaml", DeckFixture.Styles + "\ncaption: { size: 9 }");
    deck.Write("notes.txt", "staged elsewhere");
    deck.Write("other.txt", "untracked elsewhere");
    Git(deck.Folder.Root, "add", "notes.txt");

    // Act
    DeckVersion saved = Saves.Save(root, "Retitled slide 2");

    // Assert
    saved.Number.ShouldBe(2);
    saved.Subject.ShouldBe("Retitled slide 2");
    Saves.Status(root).Unsaved.ShouldBeFalse();
    Revisions.Versions(root)[0].Commit.ShouldBe(saved.Commit);
    Status(deck.Folder.Root).ShouldBe(["A  notes.txt", "?? other.txt"]);
  }

  [Fact]
  public void Saving_commits_the_comments_with_the_presentation()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/comments.yaml", "- { id: a1, slide: two, at: [0, 0], text: Hi, done: Said hi }");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Hi");

    // Act
    Saves.Save(root, "Said hi");

    // Assert
    Status(deck.Folder.Root).ShouldBeEmpty();
  }

  [Fact]
  public void Saving_leaves_new_files_out_unless_they_are_yaml_or_named_by_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/Test deck.pptx", "exported");
    deck.Write("deck/screenshot.png", "scratch");
    deck.Write("theme/notes.txt", "scratch");
    SaveStatus stray = Saves.Status(root);
    deck.Write("deck/assets/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - { id: logo, picture: ../assets/logo.svg, frame: [880, 500, 60, 20] }
      """);
    Git(deck.Folder.Root, "add", "deck/screenshot.png");

    // Act
    Saves.Save(root, "Added the logo");

    // Assert
    stray.Unsaved.ShouldBeFalse();
    Status(deck.Folder.Root).ShouldBe(["A  deck/screenshot.png", "?? \"deck/Test deck.pptx\"",
      "?? theme/notes.txt"]);
  }

  [Fact]
  public void Saving_removes_a_deleted_slide()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [one]");
    File.Delete(deck.Folder.PathTo("deck/slides/two.yaml"));

    // Act
    Saves.Save(root, "Removed slide 2");

    // Assert
    Status(deck.Folder.Root).ShouldBeEmpty();
  }

  [Fact]
  public void Saving_outside_a_repository_starts_one_in_the_presentation()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: theme\nslides: [one]");
    deck.Write("deck/slides/one.yaml", "layout: content\ntitle: Hello");
    foreach (string file in Directory.GetFiles(deck.Folder.PathTo("theme"), "*", SearchOption.AllDirectories))
    {
      deck.Write(Path.Combine("deck", Path.GetRelativePath(deck.Folder.Root, file)),
        File.ReadAllText(file));
    }
    string root = deck.Folder.PathTo("deck");
    SaveStatus before = Saves.Status(root);

    // Act
    DeckVersion saved = Saves.Save(root, null);

    // Assert
    before.ShouldBe(new SaveStatus(true, 1));
    saved.Number.ShouldBe(1);
    saved.Subject.ShouldBe("First version");
    Directory.Exists(Path.Combine(root, ".git")).ShouldBeTrue();
  }

  [Fact]
  public void Nothing_to_save_is_refused()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");

    // Act
    Action save = () => Saves.Save(deck.Folder.PathTo("deck"), "Again");

    // Assert
    save.ShouldThrow<InvalidDataException>().Message.ShouldBe("there are no changes to save");
  }

  [Fact]
  public void A_draft_says_what_happened_to_which_slides()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      fields:
        footer: Confidential
      slides:
        - one
        - section: Results
        - two
        - three
      """);
    deck.Write("deck/slides/one.yaml", """
      layout: cover
      title: Hello again
      placeholders:
        subtitle: ${footer}
      """);
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: { text: "Results in ${section}", frame: [40, 30, 880, 56] }
      """);
    deck.Write("deck/slides/three.yaml", "layout: content\ntitle: New");

    // Act
    string draft = Saves.Draft(deck.Folder.PathTo("deck"));

    // Assert
    draft.ShouldBe("Added slide 3, reworded slide 1, and rearranged slide 2");
  }

  [Fact]
  public void A_draft_names_slides_sharing_a_change_together_and_a_theme_change_once()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Hi\nplaceholders:\n  subtitle: ${footer}");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("title: { font: heading, size: 28 }",
      "title: { font: heading, size: 30 }"));

    // Act
    string draft = Saves.Draft(deck.Folder.PathTo("deck"));

    // Assert
    draft.ShouldBe("Reworded slides 1 and 2, and changed the theme");
  }

  [Fact]
  public void The_first_draft_is_the_first_version()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    string draft = Saves.Draft(deck.Folder.PathTo("deck"));

    // Assert
    draft.ShouldBe("First version");
  }

  private static List<string> Status(string folder)
  {
    var start = new System.Diagnostics.ProcessStartInfo("git", ["status", "--porcelain"])
    {
      WorkingDirectory = folder,
      RedirectStandardOutput = true
    };
    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
  }


}