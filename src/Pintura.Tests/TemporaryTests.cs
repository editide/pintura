using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class TemporaryTests
{
  [Fact]
  public void A_folder_others_could_read_is_made_for_its_user_alone()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    string root = folder.PathTo("pintura");
    Directory.CreateDirectory(root);
    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
      UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    // Act
    Temporary.Restrict(root);

    // Assert
    File.GetUnixFileMode(root).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
  }

  [Fact]
  public void A_folder_that_does_not_exist_yet_is_made_for_its_user_alone()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    string root = folder.PathTo("cache/pintura");

    // Act
    Temporary.Restrict(root);

    // Assert
    File.GetUnixFileMode(root).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
  }


}