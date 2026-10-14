using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class AccountTests
{
  private sealed class Editide
  {
    public Exception? Failure { get; set; }

    public AccountPlan WhoAmI(string key)
    {
      return Failure is null ? new AccountPlan("api", "basic", 12, 30) : throw Failure;
    }


  }

  [Fact]
  public void Signing_out_forgets_the_key()
  {
    // Arrange
    using var folder = new TempFolder();
    var account = new Account(folder.PathTo("account.json"), null);
    account.Login("key-1", new Editide().WhoAmI);

    // Act
    account.Logout();

    // Assert
    account.Key.ShouldBeNull();
    File.Exists(folder.PathTo("account.json")).ShouldBeFalse();
  }

  [Fact]
  public void The_key_is_kept_where_only_its_owner_can_read_it()
  {
    // Arrange
    using var folder = new TempFolder();
    var account = new Account(folder.PathTo("pintura/account.json"), null);

    // Act
    account.Login("key-1", new Editide().WhoAmI);

    // Assert
    new Account(folder.PathTo("pintura/account.json"), null).Key.ShouldBe("key-1");
    if (!OperatingSystem.IsWindows())
    {
      File.GetUnixFileMode(folder.PathTo("pintura/account.json"))
        .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
  }

  [Fact]
  public void A_key_editide_refuses_is_not_kept()
  {
    // Arrange
    using var folder = new TempFolder();
    var account = new Account(folder.PathTo("account.json"), null);
    var editide = new Editide { Failure = new InvalidOperationException("editide didn't accept the API key") };

    // Act
    Should.Throw<InvalidOperationException>(() => account.Login("key-1", editide.WhoAmI));

    // Assert
    account.Key.ShouldBeNull();
    File.Exists(folder.PathTo("account.json")).ShouldBeFalse();
  }

  [Fact]
  public void A_key_in_the_environment_stands_in_for_the_one_signed_in_with()
  {
    // Arrange
    using var folder = new TempFolder();
    new Account(folder.PathTo("account.json"), null).Login("key-1", new Editide().WhoAmI);

    // Act
    var account = new Account(folder.PathTo("account.json"), "key-2");

    // Assert
    account.Key.ShouldBe("key-2");
    account.KeySource.ShouldBe(Account.KeyVariable);
  }

  [Fact]
  public void Signing_out_without_having_signed_in_succeeds()
  {
    // Arrange
    using var folder = new TempFolder();
    var account = new Account(folder.PathTo("pintura/account.json"), null);

    // Act
    account.Logout();

    // Assert
    account.Key.ShouldBeNull();
  }

  [Fact]
  public void On_windows_the_key_is_kept_encrypted_for_this_account()
  {
    if (!OperatingSystem.IsWindows())
    {
      // Elsewhere the file is readable by its owner alone, as the test above checks.
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    var account = new Account(folder.PathTo("pintura/account.json"), null);

    // Act
    account.Login("key-1", new Editide().WhoAmI);

    // Assert
    File.ReadAllText(folder.PathTo("pintura/account.json")).ShouldNotContain("key-1");
    new Account(folder.PathTo("pintura/account.json"), null).Key.ShouldBe("key-1");
  }


}