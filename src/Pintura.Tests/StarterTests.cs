using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class StarterTests
{
  [Fact]
  public void A_new_presentation_carries_the_default_theme_and_checks_clean()
  {
    // Arrange
    using var folder = new TempFolder();
    string created = folder.PathTo("deck");

    // Act
    Starter.Create(created);

    // Assert
    Presentation presentation = PresentationLoader.Load(created);
    presentation.Slides.ShouldAllBe(slide => slide.Error == null);
    Checks.Structure(presentation).ShouldBeEmpty();
    foreach (string file in Directory.GetFiles(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), "*",
      SearchOption.AllDirectories))
    {
      string relative = Path.GetRelativePath(Path.Combine(LoaderTests.RepositoryRoot(), "themes", "default"), file);
      File.ReadAllText(Path.Combine(created, "theme", relative)).ShouldBe(File.ReadAllText(file));
    }
  }

  [Fact]
  public void A_new_presentation_refuses_a_folder_holding_something()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("deck/notes.md", "mine");

    // Act
    Action create = () => Starter.Create(folder.PathTo("deck"));

    // Assert
    create.ShouldThrow<IOException>();
    File.ReadAllText(folder.PathTo("deck/notes.md")).ShouldBe("mine\n");
  }


}