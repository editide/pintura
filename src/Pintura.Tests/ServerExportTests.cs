using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ServerExportTests
{
  [Fact]
  public async Task A_powerpoint_export_refuses_a_slide_that_doesnt_load_rather_than_leave_it_out()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\nelements:\n  - { id: box, frame: [0, 0] }");
    await using Served served = await Served.Start(deck);

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/pptx", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    (await response.Content.ReadAsStringAsync()).ShouldContain("two doesn't load");
  }

  [Fact]
  public async Task Exporting_to_html_downloads_the_presentation_as_one_page_named_for_its_title()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck, Served.Interface());

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/html", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Content.Headers.ContentDisposition.ShouldNotBeNull().FileName.ShouldBe("Test-deck.html");
    string html = await response.Content.ReadAsStringAsync();
    html.ShouldContain("<title>Test deck</title>");
    html.ShouldNotContain("<script src=");
    response.Headers.Contains("Pintura-Findings").ShouldBeFalse();
  }

  [Theory]
  [InlineData("Q3 Board Update", "deck", "Q3-Board-Update")]
  [InlineData("Revenue: 2026 / 2027?", "deck", "Revenue-2026-2027")]
  [InlineData("Acme’s Year", "deck", "Acmes-Year")]
  [InlineData("  ", "deck", "deck")]
  public void A_download_is_named_for_the_title_with_hyphens_between_words(string title, string folder,
    string expected)
  {
    // Act
    string stem = PreviewServer.DownloadStem(title, folder);

    // Assert
    stem.ShouldBe(expected);
  }

  [Fact]
  public async Task Exporting_to_html_lists_the_fonts_the_theme_doesnt_ship()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("theme/theme.yaml", DeckFixture.Theme.Replace("heading: Georgia", "heading: DM Sans"));
    await using Served served = await Served.Start(deck, Served.Interface());

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/html", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(response.Headers.GetValues("Pintura-Findings")
      .Single())).ShouldBe(HtmlExport.Warnings(deck.Load()));
  }

  [Fact]
  [Trait("Needs", "browser")]
  public async Task Exporting_to_pdf_prints_a_page_per_slide_at_its_size_in_points_with_its_text_as_text()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck, Served.Interface());

    // Act
    HttpResponseMessage response = await served.Client.PostAsync($"{served.Url}/api/export/pdf", null);

    // Assert
    response.StatusCode.ShouldBe(HttpStatusCode.OK);
    response.Content.Headers.ContentDisposition.ShouldNotBeNull().FileName.ShouldBe("Test-deck.pdf");
    string pdf = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());
    pdf.ShouldStartWith("%PDF-");
    pdf.ShouldContain("/Count 2");
    Regex.Matches(pdf, @"/MediaBox \[0 0 960 540\]").Count.ShouldBe(2);
    pdf.ShouldContain("/BaseFont");
    pdf.ShouldNotContain("/Subtype /Image");
  }

  [Fact]
  [Trait("Needs", "browser")]
  public async Task Small_text_is_measured_at_its_true_width_rather_than_at_whole_pixel_glyph_advances()
  {
    // Arrange
    // Copy an installed TrueType font into theme/fonts so Chromium loads it as a theme font on every OS.
    string? font = FontFolders.Files().FirstOrDefault(file => Path.GetFileName(file) == "DejaVuSans.ttf")
      ?? FontFolders.Files().FirstOrDefault(file => file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
    using var deck = new DeckFixture();
    Directory.CreateDirectory(deck.Folder.PathTo("theme/fonts"));
    string family = "DejaVu Sans";
    if (font is not null)
    {
      string copied = deck.Folder.PathTo($"theme/fonts/{Path.GetFileName(font)}");
      File.Copy(font, copied);
      FontFace? face = ThemeFonts.Read(deck.Folder.PathTo("theme/fonts")).FirstOrDefault();
      if (face is null)
      {
        return;
      }
      family = face.Family;
    }
    else
    {
      return;
    }
    deck.Write("theme/theme.yaml", DeckFixture.Theme.Replace("body: Arial", $"body: {family}"));
    deck.Write("deck/slides/two.yaml", """
      layout: content
      title: Results
      elements:
        - id: small
          text: "2026 loans: SOFR + 2.25% to 5.50%, about 15% all-in"
          size: 11
          insets: [0, 0, 0, 0]
          fit: grow
          wrap: false
          frame: [40, 100, 20, 20]
        - id: large
          text: "2026 loans: SOFR + 2.25% to 5.50%, about 15% all-in"
          size: 110
          insets: [0, 0, 0, 0]
          fit: grow
          wrap: false
          frame: [40, 200, 20, 200]
      """);
    await using Served served = await Served.Start(deck, Served.Interface());
    await using var headless = new HeadlessBrowser(served.Url);

    // Act
    Measurement measurement = await headless.Measure(["two"]);

    // Assert
    double small = measurement.Boxes.Single(box => box.Id == "small").Width.ShouldNotBeNull();
    double large = measurement.Boxes.Single(box => box.Id == "large").Width.ShouldNotBeNull();
    (small * 10).ShouldBe(large, large * 0.003);
  }

  [Fact]
  public async Task An_export_that_cannot_be_built_answers_with_why_for_the_page_to_show()
  {
    // Arrange
    using var deck = new DeckFixture();
    await using Served served = await Served.Start(deck);

    // Act
    HttpResponseMessage unknown = await served.Client.PostAsync($"{served.Url}/api/export/docx", null);
    deck.Write("deck/presentation.yaml", "name: Test deck\ntheme: ../missing\nslides: [one]");
    HttpResponseMessage broken = await served.Client.PostAsync($"{served.Url}/api/export/html", null);

    // Assert
    unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    (await unknown.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString()
      .ShouldBe("there is no docx export; choose html, pptx, or pdf");
    broken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    (await broken.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString()
      .ShouldBe("the presentation doesn't load; fix its error first");
  }



}
