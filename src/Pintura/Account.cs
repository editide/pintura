using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pintura;

/// <summary>An editide account's plan, as <c>whoami</c> answers for a key.</summary>
public sealed record AccountPlan(string Product, string Tier, int SlidesUsed, int SlidesIncluded);

/// <summary>The API key export and import sign in with, kept in one file only its owner can read: on Windows in the
/// folder that stays on this machine, encrypted for this Windows account, and elsewhere in the user's config folder,
/// readable by its owner alone. A key in <c>EDITIDE_API_KEY</c> stands in for the one signed in with.</summary>
public sealed class Account
{
  public const string KeyVariable = "EDITIDE_API_KEY";
  public const string Dashboard = "https://app.editide.com/dashboard/keys";

  private readonly string file;
  private readonly string? environmentKey;

  public Account(string file, string? environmentKey)
  {
    this.file = file;
    this.environmentKey = string.IsNullOrEmpty(environmentKey) ? null : environmentKey;
  }

  // Windows copies the roaming folder between the machines of a network account, so the key stays in the local one.
  public static Account Default => OperatingSystem.IsWindows()
    ? new Account(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "account.json"),
      Environment.GetEnvironmentVariable(KeyVariable))
    : new Account(Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "account.json"),
      Environment.GetEnvironmentVariable(KeyVariable));

  /// <summary>The key in <c>EDITIDE_API_KEY</c>, or else the one signed in with.</summary>
  public string? Key => environmentKey ?? Stored();

  /// <summary>Where <see cref="Key"/> comes from, in words.</summary>
  public string KeySource => environmentKey is not null ? KeyVariable : "pintura --login";

  /// <summary>Asks editide for <paramref name="key"/>'s plan and keeps the key, refusing one editide doesn't accept.
  /// </summary>
  public AccountPlan Login(string key, Func<string, AccountPlan> whoAmI)
  {
    AccountPlan plan = whoAmI(key);
    Write(key);
    return plan;
  }

  public void Logout()
  {
    if (File.Exists(file))
    {
      File.Delete(file);
    }
  }

  private static string Folder(Environment.SpecialFolder folder)
  {
    return Path.Combine(Environment.GetFolderPath(folder), "pintura");
  }

  private string? Stored()
  {
    JsonObject? json = Read(file);
    if ((string?)json?["protected_key"] is string sealedKey)
    {
      return OperatingSystem.IsWindows() ? Unseal(sealedKey) : null;
    }
    return (string?)json?["key"];
  }

  private static JsonObject? Read(string path)
  {
    try
    {
      return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
    {
      return null;
    }
  }

  [SupportedOSPlatform("windows")]
  private static string Seal(string key)
  {
    return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null,
      DataProtectionScope.CurrentUser));
  }

  /// <summary>The key, or null when this Windows account can't decrypt it, such as on another machine.</summary>
  [SupportedOSPlatform("windows")]
  private static string? Unseal(string sealedKey)
  {
    try
    {
      return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedKey), null,
        DataProtectionScope.CurrentUser));
    }
    catch (Exception exception) when (exception is CryptographicException or FormatException)
    {
      return null;
    }
  }

  private void Write(string key)
  {
    JsonObject json = OperatingSystem.IsWindows()
      ? new JsonObject { ["protected_key"] = Seal(key) }
      : new JsonObject { ["key"] = key };
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
    if (!OperatingSystem.IsWindows())
    {
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }
    using (var writer = new StreamWriter(file, new UTF8Encoding(false), options))
    {
      writer.Write(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    if (!OperatingSystem.IsWindows())
    {
      File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
  }


}

/// <summary><c>--login</c>, <c>--logout</c>, and <c>--whoami</c>, against editide at <see cref="ToolHosts.Url"/>.
/// </summary>
public static class AccountCommands
{
  public static AccountPlan WhoAmI(string key)
  {
    return EditideTools.Connect(ToolHosts.Url, key).Plan();
  }

  /// <summary>Takes the key from standard input, unechoed when it's a terminal, and keeps it once editide accepts it.
  /// </summary>
  public static int Login(Account account, TextWriter output)
  {
    string key = ReadKey();
    if (key.Length == 0)
    {
      Console.Error.WriteLine($"no key given; make one at {Account.Dashboard}");
      return 1;
    }
    AccountPlan plan;
    try
    {
      plan = account.Login(key, WhoAmI);
    }
    catch (InvalidOperationException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
    output.WriteLine($"Signed in on editide's {plan.Product} {plan.Tier} plan.");
    if (account.Key != key)
    {
      output.WriteLine($"{Account.KeyVariable} is set, though, and Pintura uses that key instead while it is.");
    }
    return 0;
  }

  public static int Logout(Account account, TextWriter output)
  {
    account.Logout();
    output.WriteLine(account.Key is null
      ? "Signed out."
      : $"Signed out, though Pintura still uses the key in {Account.KeyVariable}.");
    return 0;
  }

  public static int Show(Account account, TextWriter output)
  {
    string? key = account.Key;
    if (key is null)
    {
      Console.Error.WriteLine($"Not signed in: sign in with `pintura --login`, or set {Account.KeyVariable}.");
      return 1;
    }
    try
    {
      AccountPlan plan = WhoAmI(key);
      output.WriteLine($"{plan.Product} {plan.Tier}, with the key from {account.KeySource}");
      output.WriteLine($"{plan.SlidesUsed} of {plan.SlidesIncluded} slides exported this billing period");
      return 0;
    }
    catch (InvalidOperationException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static string ReadKey()
  {
    if (Console.IsInputRedirected)
    {
      return Console.In.ReadLine()?.Trim() ?? "";
    }
    Console.Error.Write($"API key, from {Account.Dashboard} (hidden as you type or paste): ");
    var key = new StringBuilder();
    while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } pressed)
    {
      if (pressed.Key == ConsoleKey.Backspace)
      {
        key.Length = Math.Max(0, key.Length - 1);
      }
      else if (!char.IsControl(pressed.KeyChar))
      {
        key.Append(pressed.KeyChar);
      }
    }
    Console.Error.WriteLine();
    return key.ToString().Trim();
  }


}