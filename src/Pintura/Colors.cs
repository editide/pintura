using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>A color as written: a theme color with an optional step, a named color, a hex value, or none. Theme and
/// named colors stay references, so the export can write them as scheme colors.</summary>
public partial record Color
{
  public static readonly string[] ThemeColors =
  [
    "dark1", "light1", "dark2", "light2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6",
    "hyperlink", "followed_hyperlink"
  ];

  public string? Theme { get; init; }
  public string? Named { get; init; }
  public string? Hex { get; init; }
  public int? Lighter { get; init; }
  public int? Darker { get; init; }
  public bool? None { get; init; }

  [JsonIgnore]
  public bool IsNone => None == true;

  public static Color Parse(string text, Func<string, bool> isNamed, Func<string, LoadException> error)
  {
    string value = text.Trim();
    if (value == "none")
    {
      return new Color { None = true };
    }
    if (HexPattern().IsMatch(value))
    {
      return new Color { Hex = value.ToUpperInvariant() };
    }
    Match match = ColorPattern().Match(value);
    if (!match.Success)
    {
      throw error($"not a color: {text}");
    }
    string name = match.Groups["name"].Value;
    int? step = null;
    if (match.Groups["step"].Success)
    {
      step = int.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
      if (step is < 1 or > 99)
      {
        throw error($"color steps run from 1 to 99: {text}");
      }
    }
    bool lighter = match.Groups["step"].Value == "lighter";
    if (ThemeColors.Contains(name))
    {
      return new Color { Theme = name, Lighter = lighter ? step : null, Darker = lighter ? null : step };
    }
    if (isNamed(name))
    {
      if (step is not null)
      {
        throw error($"{name} is a named color, which takes no steps; step the theme color it names instead");
      }
      return new Color { Named = name };
    }
    throw error($"unknown color {name}");
  }

  public override string ToString()
  {
    if (IsNone)
    {
      return "none";
    }
    if (Hex is not null)
    {
      return Hex;
    }
    string name = Theme ?? Named ?? "";
    return Lighter is not null ? $"{name} lighter {Lighter}" : Darker is not null ? $"{name} darker {Darker}" : name;
  }

  [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
  private static partial Regex HexPattern();

  [GeneratedRegex(@"^(?<name>[a-z][a-z0-9_]*)(\s+(?<step>lighter|darker)\s+(?<amount>[0-9]{1,9}))?$")]
  private static partial Regex ColorPattern();


}

/// <summary>Resolves colors to hex with PowerPoint's arithmetic: "Lighter 40%" is luminance modulated by 60% and
/// offset by 40%, and "Darker 25%" is luminance modulated by 75%.</summary>
public static class ColorMath
{
  public static string Resolve(Color color, Theme theme)
  {
    if (color.Hex is not null)
    {
      return color.Hex;
    }
    if (color.Named is not null)
    {
      return Resolve(theme.NamedColors[color.Named], theme);
    }
    string hex = theme.Colors[color.Theme!];
    if (color.Lighter is int lighter)
    {
      return Luminance(hex, (100 - lighter) / 100.0, lighter / 100.0);
    }
    if (color.Darker is int darker)
    {
      return Luminance(hex, (100 - darker) / 100.0, 0);
    }
    return hex;
  }

  public static string Luminance(string hex, double modulation, double offset)
  {
    (double hue, double saturation, double luminance) = ToHsl(hex);
    return FromHsl(hue, saturation, Math.Clamp(luminance * modulation + offset, 0, 1));
  }

  private static (double Hue, double Saturation, double Luminance) ToHsl(string hex)
  {
    double red = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
    double green = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
    double blue = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
    double max = Math.Max(red, Math.Max(green, blue));
    double min = Math.Min(red, Math.Min(green, blue));
    double luminance = (max + min) / 2;
    if (max == min)
    {
      return (0, 0, luminance);
    }
    double delta = max - min;
    double saturation = luminance > 0.5 ? delta / (2 - max - min) : delta / (max + min);
    double hue = max == red ? (green - blue) / delta + (green < blue ? 6 : 0)
      : max == green ? (blue - red) / delta + 2
      : (red - green) / delta + 4;
    return (hue / 6, saturation, luminance);
  }

  private static string FromHsl(double hue, double saturation, double luminance)
  {
    if (saturation == 0)
    {
      return Hex(luminance, luminance, luminance);
    }
    double q = luminance < 0.5 ? luminance * (1 + saturation) : luminance + saturation - luminance * saturation;
    double p = 2 * luminance - q;
    return Hex(Channel(p, q, hue + 1.0 / 3), Channel(p, q, hue), Channel(p, q, hue - 1.0 / 3));
  }

  private static double Channel(double p, double q, double t)
  {
    t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
    return t < 1.0 / 6 ? p + (q - p) * 6 * t
      : t < 0.5 ? q
      : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
      : p;
  }

  private static string Hex(double red, double green, double blue)
  {
    return $"#{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}";
  }

  private static int Byte(double channel)
  {
    return (int)Math.Round(Math.Clamp(channel, 0, 1) * 255, MidpointRounding.AwayFromZero);
  }


}