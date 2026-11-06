using System.Runtime.CompilerServices;

namespace Pintura.Tests;

/// <summary>Points Pintura's cache, and the headless servers' the tests start, at a folder of the run's own, so the
/// commits each run makes in new repositories leave nothing in the user's cache.</summary>
internal static class TestCache
{
  [ModuleInitializer]
  internal static void Use()
  {
    string folder = Path.Combine(Path.GetTempPath(), "pintura-tests", $"cache-{Environment.ProcessId}");
    Environment.SetEnvironmentVariable("PINTURA_CACHE", folder);
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
      try
      {
        Directory.Delete(folder, true);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
      }
    };
  }


}