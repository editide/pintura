using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ThemeFontTests
{
  private static void Ship(DeckFixture deck, string file, byte[] bytes)
  {
    string path = deck.Folder.PathTo($"theme/fonts/{file}");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
  }

  private static List<string> Faces(Presentation presentation)
  {
    return presentation.Theme.Faces
      .Select(face =>
        $"{face.Family} {face.Weight} {(face.Italic ? "italic" : "normal")} {Path.GetFileName(face.Path)}")
      .ToList();
  }

  [Fact]
  public void A_weight_beyond_regular_and_bold_answers_to_its_windows_family_and_to_its_family_at_its_weight()
  {
    // Arrange
    using var deck = new DeckFixture();
    Ship(deck, "Plex-Medium.ttf", TestFont.Bytes("Plex Medm", 500, typographic: "Plex"));

    // Act
    List<string> faces = Faces(deck.Load());

    // Assert
    faces.ShouldBe(["Plex 500 normal Plex-Medium.ttf", "Plex Medm 400 normal Plex-Medium.ttf"]);
  }

  [Fact]
  public void A_bold_italic_face_is_one_family_at_its_weight_and_slant()
  {
    // Arrange
    using var deck = new DeckFixture();
    Ship(deck, "Plex-BoldItalic.otf", TestFont.Bytes("Plex", 700, bold: true, italic: true));

    // Act
    List<string> faces = Faces(deck.Load());

    // Assert
    faces.ShouldBe(["Plex 700 italic Plex-BoldItalic.otf"]);
  }

  [Fact]
  public void Files_that_are_not_fonts_are_passed_over()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/fonts/OFL.txt", "SIL Open Font License");
    Ship(deck, "Broken.ttf", [1, 2, 3]);

    // Act
    List<string> faces = Faces(deck.Load());

    // Assert
    faces.ShouldBeEmpty();
  }

  [Fact]
  public void The_deck_carries_the_themes_faces_for_the_page_to_load_and_the_server_serves_them()
  {
    // Arrange
    using var deck = new DeckFixture();
    Ship(deck, "Plex-Light.ttf", TestFont.Bytes("Plex Light", 300, typographic: "Plex"));
    Presentation presentation = deck.Load();

    // Act
    DeckPayload payload = PayloadBuilder.Build(presentation, 1, (path, _) => $"url:{Path.GetFileName(path)}");

    // Assert
    payload.Fonts.ShouldBe([
      new FontFacePayload("Plex", 300, "normal", "url:Plex-Light.ttf"),
      new FontFacePayload("Plex Light", 400, "normal", "url:Plex-Light.ttf")
    ]);
    PresentationStore.Media(presentation).ShouldContain(deck.Folder.PathTo("theme/fonts/Plex-Light.ttf"));
    PresentationStore.ServedType("Plex-Light.ttf").ShouldBe("font/ttf");
  }

  [Fact]
  public void The_html_export_inlines_the_themes_fonts()
  {
    // Arrange
    using var deck = new DeckFixture();
    Ship(deck, "Plex-Regular.ttf", TestFont.Bytes("Plex", 400));
    string assets = Path.Combine(LoaderTests.RepositoryRoot(), "src", "Pintura", "wwwroot");

    // Act
    string html = HtmlExport.Build(deck.Load(), assets);

    // Assert
    html.ShouldContain("\"url\":\"data:font/ttf;base64,");
  }


}