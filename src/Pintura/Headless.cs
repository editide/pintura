using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Pintura;

public record Measurement(List<FontMeasure> Fonts, List<BoxMeasure> Boxes, List<ChartMeasure>? Charts = null,
  List<PictureMeasure>? Pictures = null);

public record FontMeasure(string Family, bool Available);

/// <summary>A text box or table as the headless page drew it. Overflow is in points past the frame; scale is the
/// shrink applied under <c>fit: shrink</c>, and height the grown height under <c>fit: grow</c> or a table's height.
/// A table's paragraphs are its cells' line counts, row by row, and its rows their heights. A text box's ink is each
/// line of its text as drawn, <c>[x, y, width, height]</c> on the slide.</summary>
public record BoxMeasure(
  string Slide,
  string Id,
  string Owner,
  string Fit,
  int Lines,
  int[] Paragraphs,
  double Overflow,
  double? Scale,
  double? Height,
  string Kind = "text",
  double[]? Rows = null,
  double[][]? Ink = null);

/// <summary>The text a chart draws, its axis, data, and legend labels, as the headless page drew it.</summary>
public record ChartMeasure(string Slide, string Id, List<ChartLabel> Labels);

/// <summary>A picture as the headless page drew it: the image's <c>[x, y, width, height]</c> on the slide, inside its
/// frame under <c>fit: contain</c>, and the transparent margins built into the image, as fractions of its width and
/// height, <c>[left, top, right, bottom]</c>; null for a cropped picture.</summary>
public record PictureMeasure(string Slide, string Id, string Owner, double[] Drawn, double[]? Margins = null);

/// <summary>A chart label's text and its <c>[x, y, width, height]</c> on the slide.</summary>
public record ChartLabel(string Text, double[] Rect);

/// <summary>Headless Chromium on the server's own headless page, launched on the first request and kept until the
/// server stops. Requests take turns, since they share one page.</summary>
public sealed class HeadlessBrowser : IAsyncDisposable
{
  public const string InstallArgument = "--install-browser";
  private static readonly string[] Channels = ["msedge", "chrome"];
  private const float DeviceScale = 2;
  private const int ThemeWidth = 1440;

  private readonly string url;
  private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
  private IPlaywright? playwright;
  private IBrowser? browser;
  private IPage? page;

  public HeadlessBrowser(string url)
  {
    this.url = url;
  }

  /// <summary>One slide at twice its size in points, or with a null slide a contact sheet of them all.</summary>
  public async Task<byte[]> Screenshot(string? slide)
  {
    return await Use(async current =>
    {
      if (slide is null)
      {
        await current.EvaluateAsync("() => pintura.showSheet()");
      }
      else
      {
        await current.EvaluateAsync("id => pintura.showSlide(id)", slide);
      }
      return await current.Locator("#root > *").ScreenshotAsync(new LocatorScreenshotOptions
      {
        Type = ScreenshotType.Png,
        Animations = ScreenshotAnimations.Disabled
      });
    });
  }

  /// <summary>The preview's theme view, whole, at the width of a laptop screen. It is the preview page rather than
  /// the headless one, opened on a page of its own and closed after, since its live connection would otherwise keep
  /// the server from going idle.</summary>
  public async Task<byte[]> ThemeScreenshot()
  {
    return await Use(async _ =>
    {
      IPage preview = await browser!.NewPageAsync(new BrowserNewPageOptions
      {
        ViewportSize = new ViewportSize { Width = ThemeWidth, Height = 900 }
      });
      try
      {
        await preview.AddInitScriptAsync(
          "localStorage.setItem('pintura.view', JSON.stringify({ view: 'theme', nav: false }))");
        await preview.GotoAsync($"{url}/");
        await preview.WaitForSelectorAsync("#theme-view .theme-section");
        int height = await preview.EvaluateAsync<int>("""
          async () => {
            await document.fonts.ready;
            await Promise.all([...document.images].filter((image) => !image.complete)
              .map((image) => new Promise((done) => { image.onload = image.onerror = done; })));
            const view = document.getElementById('theme-view');
            return Math.ceil(view.getBoundingClientRect().top + view.scrollHeight);
          }
          """);
        await preview.SetViewportSizeAsync(ThemeWidth, height);
        return await preview.Locator("#theme-view").ScreenshotAsync(new LocatorScreenshotOptions
        {
          Type = ScreenshotType.Png,
          Animations = ScreenshotAnimations.Disabled
        });
      }
      finally
      {
        await preview.CloseAsync();
      }
    });
  }

  /// <summary>Every slide printed to a page of its size in points, keeping its text as text. The page draws a point
  /// as a pixel, which prints at three quarters of a point, so the print is scaled back up by a third.</summary>
  public async Task<byte[]> Print(double[] size)
  {
    return await Use(async current =>
    {
      await current.EvaluateAsync("() => pintura.showPrint()");
      return await current.PdfAsync(new PagePdfOptions
      {
        Width = Inches(size[0]),
        Height = Inches(size[1]),
        Scale = 96f / 72,
        PrintBackground = true,
        Margin = new Margin { Top = "0", Right = "0", Bottom = "0", Left = "0" }
      });
    });
  }

  /// <summary>The slide, or with a null slide every slide the change touched, as the revision drew it beside the
  /// working tree, with the changes marked; null with the reason when there's nothing to draw.</summary>
  public async Task<(byte[]? Png, string? Problem)> Compare(string? slide, string revision)
  {
    return await Use(async current =>
    {
      string? problem = await current.EvaluateAsync<string?>("([id, base]) => pintura.showCompare(id, base)",
        new[] { slide, revision });
      if (problem is not null)
      {
        return ((byte[]?)null, (string?)problem);
      }
      byte[] png = await current.Locator("#root > *").ScreenshotAsync(new LocatorScreenshotOptions
      {
        Type = ScreenshotType.Png,
        Animations = ScreenshotAnimations.Disabled
      });
      return (png, null);
    });
  }

  public async Task<Measurement> Measure(IReadOnlyList<string>? slides)
  {
    return await Use(async current =>
    {
      JsonElement result = await current.EvaluateAsync<JsonElement>("ids => pintura.measure(ids)", slides);
      return result.Deserialize<Measurement>(PayloadJson.Options)!;
    });
  }

  public async ValueTask DisposeAsync()
  {
    if (browser is not null)
    {
      await browser.DisposeAsync();
    }
    playwright?.Dispose();
    gate.Dispose();
  }

  /// <summary>The page is reloaded for every request, so an edit to the page's own files is always picked up.
  /// </summary>
  private async Task<T> Use<T>(Func<IPage, Task<T>> action)
  {
    await gate.WaitAsync();
    try
    {
      if (page is null || page.IsClosed || browser is not { IsConnected: true })
      {
        page = await Open();
      }
      await page.GotoAsync($"{url}/headless.html");
      await page.WaitForFunctionAsync("() => window.pintura");
      return await action(page);
    }
    finally
    {
      gate.Release();
    }
  }

  private async Task<IPage> Open()
  {
    playwright ??= await Playwright.CreateAsync();
    if (browser is not null)
    {
      await browser.DisposeAsync();
    }
    browser = await Start(playwright);
    return await browser.NewPageAsync(new BrowserNewPageOptions
    {
      DeviceScaleFactor = DeviceScale,
      ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
    });
  }

  private static string Inches(double points)
  {
    return (points / 72).ToString("0.######", CultureInfo.InvariantCulture) + "in";
  }

  /// <summary>Each SVG drawn as a PNG of the size asked, in pixels, with its transparency. An image draws the SVG,
  /// which runs none of its scripts and fetches nothing it links to.</summary>
  public static async Task<List<byte[]>> Rasterize(IReadOnlyList<(byte[] Svg, int Width, int Height)> pictures)
  {
    using IPlaywright playwright = await Playwright.CreateAsync();
    await using IBrowser browser = await Start(playwright);
    IPage page = await browser.NewPageAsync();
    var pngs = new List<byte[]>();
    foreach ((byte[] svg, int width, int height) in pictures)
    {
      await page.SetViewportSizeAsync(width, height);
      await page.SetContentAsync("<body style=\"margin: 0\"><img style=\"display: block; width: 100vw; height: 100vh\" " +
        $"src=\"data:image/svg+xml;base64,{Convert.ToBase64String(svg)}\"></body>");
      await page.EvaluateAsync("() => document.images[0].decode().catch(() => {})");
      pngs.Add(await page.ScreenshotAsync(new PageScreenshotOptions
      {
        Type = ScreenshotType.Png,
        OmitBackground = true
      }));
    }
    return pngs;
  }

  /// <summary>An installed Edge or Chrome, so most machines fetch no browser, or else Playwright's own Chromium,
  /// fetched the first time.</summary>
  private static async Task<IBrowser> Start(IPlaywright playwright)
  {
    try
    {
      return await Launch(playwright);
    }
    catch (PlaywrightException exception) when (exception.Message.Contains("Executable doesn't exist"))
    {
      Install();
      return await Launch(playwright);
    }
  }

  private static async Task<IBrowser> Launch(IPlaywright playwright)
  {
    foreach (string channel in Channels)
    {
      try
      {
        return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = channel });
      }
      catch (PlaywrightException)
      {
      }
    }
    return await playwright.Chromium.LaunchAsync();
  }

  /// <summary>Fetches Playwright's headless Chromium in a child process, whose progress bars go to standard error so
  /// they never mix with a command's output.</summary>
  private static void Install()
  {
    Console.Error.WriteLine("  fetching Chromium for Playwright, once");
    string executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable)
    {
      UseShellExecute = false,
      RedirectStandardOutput = true
    };
    if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
    {
      start.ArgumentList.Add(typeof(HeadlessBrowser).Assembly.Location);
    }
    start.ArgumentList.Add(InstallArgument);
    using Process process = Process.Start(start)!;
    process.OutputDataReceived += (_, line) =>
    {
      if (line.Data is not null)
      {
        Console.Error.WriteLine(line.Data);
      }
    };
    process.BeginOutputReadLine();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
      throw new PlaywrightException("could not fetch Chromium for Playwright; see the output above.");
    }
  }


}

public static class FontFolders
{
  /// <summary>The font files fontconfig knows, or none where there is no fontconfig.</summary>
  public static IEnumerable<string> Files()
  {
    if (!OperatingSystem.IsLinux())
    {
      return [];
    }
    try
    {
      var start = new ProcessStartInfo("fc-list", ["--format", "%{file}\n"])
      {
        UseShellExecute = false,
        RedirectStandardOutput = true
      };
      using Process process = Process.Start(start)!;
      string output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    catch (System.ComponentModel.Win32Exception)
    {
      return [];
    }
  }

  /// <summary>Whether fontconfig reads the Windows fonts, as a WSL distribution does once they are configured.
  /// </summary>
  public static bool WindowsFontsVisible(IEnumerable<string> files)
  {
    return files.Any(file => file.StartsWith("/mnt/c/Windows/Fonts/", StringComparison.OrdinalIgnoreCase));
  }


}