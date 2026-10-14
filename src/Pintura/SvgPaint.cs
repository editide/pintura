using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Pintura;

/// <summary>Repaints an SVG in one color, as PowerPoint's Graphics Fill does: every fill, stroke, and gradient stop
/// but <c>none</c> takes the color, so an icon from any set follows the theme.</summary>
public static partial class SvgPaint
{
  private static readonly string[] PaintKeys = ["fill", "stroke", "stop-color"];
  // currentColor resolves to it.
  private static readonly string[] Keys = [.. PaintKeys, "color"];
  private static readonly string[] Unpainted = ["none", "transparent", "inherit"];

  public static bool IsHex(string color)
  {
    return HexColor().IsMatch(color);
  }

  public static string Recolor(string svg, string hex)
  {
    XDocument document = Parse(svg);
    XElement root = document.Root!;
    // Shapes with no fill of their own are black.
    if (root.Attribute("fill") is null && !Declares(root.Attribute("style")?.Value, "fill"))
    {
      root.SetAttributeValue("fill", hex);
    }
    root.SetAttributeValue("color", hex);
    foreach (XElement element in document.Descendants())
    {
      foreach (XAttribute attribute in element.Attributes().Where(attribute => Keys.Contains(attribute.Name.LocalName)))
      {
        if (Painted(attribute.Value) && !(element.Name.LocalName.StartsWith("animate") && attribute.Name == "fill"))
        {
          attribute.Value = hex;
        }
      }
      if (element.Attribute("style") is XAttribute style)
      {
        style.Value = Declarations(style.Value, hex);
      }
      if (element.Name.LocalName == "style")
      {
        element.Value = Declarations(element.Value, hex);
      }
    }
    return document.ToString(SaveOptions.DisableFormatting);
  }

  /// <summary>How many different paints an SVG names, so a recolor flattening a multi-colored one can be reported.
  /// </summary>
  public static int Paints(string svg)
  {
    XDocument document = Parse(svg);
    IEnumerable<string> attributes = document.Descendants()
      .Where(element => !element.Name.LocalName.StartsWith("animate"))
      .SelectMany(element => element.Attributes())
      .Where(attribute => PaintKeys.Contains(attribute.Name.LocalName))
      .Select(attribute => attribute.Value);
    IEnumerable<string> declared = document.Descendants()
      .SelectMany(element =>
        new[] { element.Attribute("style")?.Value, element.Name.LocalName == "style" ? element.Value : null })
      .OfType<string>()
      .SelectMany(css => Declaration().Matches(css).Where(match => match.Groups["key"].Value != "color"))
      .Select(match => match.Groups["value"].Value);
    return attributes.Concat(declared)
      .Select(value => value.Trim().ToLowerInvariant())
      .Where(value => Painted(value) && !value.StartsWith("url("))
      .Distinct()
      .Count();
  }

  /// <summary>Why the SVG can't be parsed to recolor, or null when it can.</summary>
  public static string? Malformed(string svg)
  {
    try
    {
      Parse(svg);
      return null;
    }
    catch (XmlException exception)
    {
      return exception.Message;
    }
  }

  // An SVG can name a DTD, whose entities would otherwise be expanded.
  private static XDocument Parse(string svg)
  {
    using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
    {
      DtdProcessing = DtdProcessing.Ignore,
      XmlResolver = null
    });
    return XDocument.Load(reader);
  }

  private static bool Painted(string value)
  {
    return !Unpainted.Contains(value.Trim().ToLowerInvariant());
  }

  private static bool Declares(string? css, string key)
  {
    return css is not null && Declaration().Matches(css).Any(match => match.Groups["key"].Value == key);
  }

  private static string Declarations(string css, string hex)
  {
    return Declaration().Replace(css, match => Painted(match.Groups["value"].Value)
      ? $"{match.Groups["key"].Value}: {hex}"
      : match.Value);
  }

  [GeneratedRegex(@"(?<![\w-])(?<key>fill|stroke|stop-color|color)\s*:\s*(?<value>[^;}""]+?)(?=\s*(;|}|$))")]
  private static partial Regex Declaration();

  [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
  private static partial Regex HexColor();


}