using System.Reflection;

namespace Pintura;

/// <summary>A new presentation: the default theme, copied in as files to read and change, and one slide.</summary>
public static class Starter
{
  private const string Prefix = "Pintura.Starter.";

  private const string PresentationFile = """
    name: New presentation
    theme: theme

    fields:
      footer: Draft

    slides:
      - cover
    """;

  private const string Cover = """
    layout: title
    title: A title that says what the presentation shows

    placeholders:
      subtitle: Who it's for, and when
    """;

  /// <summary>Writes a presentation into <paramref name="folder"/>, which must be empty or not exist yet.</summary>
  public static void Create(string folder)
  {
    if (File.Exists(folder) || Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
    {
      throw new IOException($"{folder} isn't empty; pick an empty folder, or one that doesn't exist yet");
    }
    Assembly assembly = typeof(Starter).Assembly;
    foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix,
      StringComparison.Ordinal)))
    {
      // MSBuild names a resource in a subfolder with the build machine's separator.
      string relative = name[Prefix.Length..].Replace('\\', '/');
      string path = Path.Combine(folder, "theme", relative);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using Stream source = assembly.GetManifestResourceStream(name)!;
      using FileStream target = File.Create(path);
      source.CopyTo(target);
    }
    Directory.CreateDirectory(Path.Combine(folder, "slides"));
    File.WriteAllText(Path.Combine(folder, "presentation.yaml"), PresentationFile + "\n");
    File.WriteAllText(Path.Combine(folder, "slides", "cover.yaml"), Cover + "\n");
  }


}