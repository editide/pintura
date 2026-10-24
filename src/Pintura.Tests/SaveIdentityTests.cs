using Shouldly;
using Xunit;
using static Pintura.Tests.RevisionTests;

namespace Pintura.Tests;

/// <summary>Hides git's own settings from every process this one starts, so it runs alone.</summary>
[CollectionDefinition(nameof(SaveIdentityTests), DisableParallelization = true)]
public class SaveIdentityCollection;

[Collection(nameof(SaveIdentityTests))]
public class SaveIdentityTests
{
  [Fact]
  public void Saving_without_a_git_identity_commits_as_the_account()
  {
    // Arrange
    using var deck = new DeckFixture();
    Git(deck.Folder.Root, "init", "-q");
    Git(deck.Folder.Root, "add", ".");
    Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    string slide = deck.Folder.PathTo("deck/slides/two.yaml");
    string before = File.ReadAllText(slide);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    PageEdits.Wrote(deck.Folder.PathTo("deck"), slide, before);
    string? global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
    string? system = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
    Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", deck.Folder.PathTo("no-gitconfig"));
    Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
    DeckVersion saved;
    try
    {
      // Act
      saved = Saves.Save(deck.Folder.PathTo("deck"), "Retitled");
    }
    finally
    {
      Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
      Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", system);
    }

    // Assert
    saved.Author.ShouldBe(Environment.UserName);
    saved.Subject.ShouldBe("Retitled");
  }


}