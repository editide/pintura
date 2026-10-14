using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ColorTests
{
  private static readonly Theme Office = new Theme
  {
    Colors = Color.ThemeColors.ToDictionary(name => name, name => name == "accent1" ? "#4472C4" : "#000000"),
    NamedColors = new Dictionary<string, Color> { ["brand"] = new Color { Theme = "accent1", Darker = 25 } }
  };

  private static Color Parse(string text)
  {
    return Color.Parse(text, name => Office.NamedColors.ContainsKey(name),
      problem => new LoadException(problem, new Origin("theme.yaml", 1, 1), "colors"));
  }

  [Theory]
  [InlineData("accent1", "#4472C4")]
  [InlineData("accent1 lighter 80", "#DAE3F3")]
  [InlineData("accent1 lighter 60", "#B4C7E7")]
  [InlineData("accent1 lighter 40", "#8FAADC")]
  [InlineData("accent1 darker 25", "#2F5597")]
  [InlineData("accent1 darker 50", "#203864")]
  [InlineData("brand", "#2F5597")]
  [InlineData("#c0392b", "#C0392B")]
  public void Steps_match_PowerPoints_palette(string text, string hex)
  {
    // Act
    string resolved = ColorMath.Resolve(Parse(text), Office);

    // Assert
    resolved.ShouldBe(hex);
  }

  [Theory]
  [InlineData("accent7", "unknown color accent7")]
  [InlineData("accent1 lighter 100", "color steps run from 1 to 99: accent1 lighter 100")]
  [InlineData("brand lighter 20",
    "brand is a named color, which takes no steps; step the theme color it names instead")]
  [InlineData("accent1 60%", "not a color: accent1 60%")]
  public void Bad_colors_say_what_is_wrong(string text, string problem)
  {
    // Act
    LoadException error = Should.Throw<LoadException>(() => Parse(text));

    // Assert
    error.Problem.ShouldBe(problem);
  }


}