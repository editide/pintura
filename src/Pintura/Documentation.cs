using System.Reflection;

namespace Pintura;

/// <summary>The format's topics, packaged from docs/topics so the binary prints the docs it was built with.</summary>
public static class Documentation
{
  public static readonly string[] Topics =
  [
    "presentations", "themes", "styles", "colors", "layouts", "slides", "text", "shapes", "charts", "tables", "books",
    "checks", "comments"
  ];

  public static void Write(string topic, TextWriter writer)
  {
    string name = topic.Length == 0 ? "index" : topic;
    Assembly assembly = typeof(Documentation).Assembly;
    using Stream stream = assembly.GetManifestResourceStream($"Pintura.Docs.{name}.md")
      ?? throw new InvalidOperationException($"Packaged documentation '{name}' is missing.");
    using var reader = new StreamReader(stream);
    writer.WriteLine(reader.ReadToEnd().TrimEnd());
  }


}