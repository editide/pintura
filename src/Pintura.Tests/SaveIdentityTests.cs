using Shouldly;
using Xunit;

namespace Pintura.Tests;

[Trait("Needs", "git")]
public class SaveIdentityTests
{
  [Fact]
  public void Saving_without_a_git_identity_commits_as_the_account()
  {
    // Arrange: TestCache isolates the test process from global and system git configuration.
    using var deck = new DeckFixture();
    deck.Folder.Commit();
    string slide = deck.Folder.PathTo("deck/slides/two.yaml");
    string before = File.ReadAllText(slide);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    PageEdits.Wrote(deck.Folder.PathTo("deck"), slide, before);

    // Act
    DeckVersion saved = Saves.Save(deck.Folder.PathTo("deck"), "Retitled");

    // Assert
    saved.Author.ShouldBe(Environment.UserName);
    saved.Subject.ShouldBe("Retitled");
  }


}