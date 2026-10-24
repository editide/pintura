using Shouldly;
using Xunit;
using static Pintura.Tests.RevisionTests;

namespace Pintura.Tests;

public class SaveTests
{
  private static readonly string Restyled = DeckFixture.Styles.Replace("title: { font: heading, size: 28 }",
    "title: { font: heading, size: 30 }");

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
    Edit(deck, "theme/styles.yaml", DeckFixture.Styles + "\ncaption: { size: 9 }");

    // Act
    SaveStatus edited = Saves.Status(root);

    // Assert
    (clean.Unsaved, clean.Next, clean.Edited).ShouldBe((false, 2, false));
    (commented.Unsaved, commented.Edited).ShouldBe((false, false));
    (edited.Unsaved, edited.Next, edited.Shared, edited.Slides.Count).ShouldBe((true, 2, true, 0));
  }

  [Fact]
  public void Saving_commits_only_what_the_page_wrote_and_leaves_the_rest_to_whoever_wrote_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Changed");
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: An agent's");
    deck.Write("deck/slides/three.yaml", "layout: content\ntitle: New");
    deck.Write("theme/styles.yaml", Restyled);
    SaveStatus status = Saves.Status(root);
    string draft = Saves.Draft(root);

    // Act
    Saves.Save(root, null);

    // Assert
    (status.Unsaved, status.Shared).ShouldBe((true, false));
    status.Slides.ShouldBe(["two"]);
    draft.ShouldBe("Reworded slide 2");
    Revisions.Versions(root)[0].Subject.ShouldBe("Reworded slide 2");
    Status(deck.Folder.Root).ShouldBe([" M deck/slides/one.yaml", " M theme/styles.yaml", "?? deck/slides/three.yaml"]);
    (Saves.Status(root).Unsaved, Saves.Status(root).Edited).ShouldBe((true, false));
  }

  [Fact]
  public void Changes_the_page_did_not_write_leave_nothing_to_save()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: An agent's");

    // Act
    Action save = () => Saves.Save(root, "Mine");

    // Assert
    (Saves.Status(root).Unsaved, Saves.Status(root).Edited).ShouldBe((true, false));
    save.ShouldThrow<InvalidDataException>().Message.ShouldBe("there are no changes to save");
  }

  [Fact]
  public void An_edit_the_page_puts_back_is_forgotten_so_a_later_change_there_is_not_the_pages()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    string slide = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Edit(deck, "deck/slides/two.yaml", slide);
    Saves.Status(root);

    // Act
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: An agent's");

    // Assert
    Saves.Status(root).Edited.ShouldBeFalse();
  }

  [Fact]
  public void Saving_names_the_files_the_page_wrote_that_something_else_changed_too()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    Edit(deck, "deck/slides/one.yaml", "layout: cover\ntitle: Mine");
    string? alone = Saves.Mixed(root, deck.Load());
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Mine\nplaceholders: { subtitle: An agent's }");
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: An agent's");
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Mine over an agent's");
    Edit(deck, "theme/styles.yaml", Restyled);

    // Act
    string? mixed = Saves.Mixed(root, deck.Load());

    // Assert
    alone.ShouldBeNull();
    mixed.ShouldBe("slides 1 and 2");
  }

  [Fact]
  public void Reformatting_yaml_leaves_the_presentation_saved()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string root = deck.Folder.PathTo("deck");
    string slide = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    deck.Write("deck/slides/two.yaml", "# A note\n" + slide.TrimEnd() + "\n\n");
    SaveStatus reformatted = Saves.Status(root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");

    // Act
    SaveStatus edited = Saves.Status(root);

    // Assert
    reformatted.Unsaved.ShouldBeFalse();
    edited.Unsaved.ShouldBeTrue();
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
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Edit(deck, "theme/styles.yaml", DeckFixture.Styles + "\ncaption: { size: 9 }");
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
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Hi");

    // Act
    Saves.Save(root, "Said hi");

    // Assert
    Status(deck.Folder.Root).ShouldBeEmpty();
  }

  [Fact]
  public void The_first_save_leaves_new_files_out_unless_they_are_yaml_or_named_by_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/Test deck.pptx", "exported");
    deck.Write("deck/screenshot.png", "scratch");
    deck.Write("theme/notes.txt", "scratch");
    deck.Write("deck/assets/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>");
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - { id: logo, picture: ../assets/logo.svg, frame: [880, 500, 60, 20] }
      """);
    Git(deck.Folder.Root, "add", "deck/screenshot.png");

    // Act
    Saves.Save(root, null);

    // Assert
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
    Edit(deck, "deck/presentation.yaml", "name: Test deck\ntheme: ../theme\nslides: [one]");
    Edit(deck, "deck/slides/two.yaml", null);

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
    (before.Unsaved, before.Next, before.Edited).ShouldBe((true, 1, true));
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
    Edit(deck, "deck/presentation.yaml", """
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
    Edit(deck, "deck/slides/one.yaml", """
      layout: cover
      title: Hello again
      placeholders:
        subtitle: ${footer}
      """);
    Edit(deck, "deck/slides/two.yaml", """
      layout: content
      title: { text: "Results in ${section}", frame: [40, 30, 880, 56] }
      """);
    Edit(deck, "deck/slides/three.yaml", "layout: content\ntitle: New");

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
    Edit(deck, "deck/slides/one.yaml", "layout: cover\ntitle: Hi\nplaceholders:\n  subtitle: ${footer}");
    Edit(deck, "deck/slides/two.yaml", "layout: content\ntitle: Changed");
    Edit(deck, "theme/styles.yaml", DeckFixture.Styles.Replace("title: { font: heading, size: 28 }",
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

  /// <summary>Writes a file as the page does, so saving takes it; a null content removes it.</summary>
  private static void Edit(DeckFixture deck, string relative, string? content)
  {
    string path = deck.Folder.PathTo(relative);
    string? before = File.Exists(path) ? File.ReadAllText(path) : null;
    if (content is null)
    {
      File.Delete(path);
    }
    else
    {
      deck.Write(relative, content);
    }
    PageEdits.Wrote(deck.Folder.PathTo("deck"), path, before);
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