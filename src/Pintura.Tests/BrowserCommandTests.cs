using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class BrowserCommandTests
{
  private sealed class StubServer : IAsyncDisposable
  {
    private readonly WebApplication app;
    private readonly InstanceRegistry registry;
    private readonly ServerInstance instance;

    public List<string> Requests { get; }

    public StubServer(WebApplication app, InstanceRegistry registry, ServerInstance instance,
      List<string> requests)
    {
      this.app = app;
      this.registry = registry;
      this.instance = instance;
      Requests = requests;
    }

    public async ValueTask DisposeAsync()
    {
      registry.Unregister(instance);
      await app.StopAsync();
      await app.DisposeAsync();
    }


  }

  private static async Task<StubServer> StartStub(string deckRoot, InstanceRegistry registry,
    Func<HttpRequest, Measurement> measure, Func<HttpRequest, IResult>? screenshot = null)
  {
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    WebApplication app = builder.Build();
    var requests = new List<string>();
    app.MapGet("/api/measure", (HttpRequest request) =>
    {
      requests.Add($"{request.Path}{request.QueryString}");
      return Results.Json(measure(request), PayloadJson.Options);
    });
    app.MapGet("/api/screenshot", (HttpRequest request) =>
    {
      requests.Add($"{request.Path}{request.QueryString}");
      return screenshot is null ? Results.Bytes([0x89, 0x50, 0x4e, 0x47], "image/png") : screenshot(request);
    });
    await app.StartAsync();
    int port = new Uri(app.Urls.First()).Port;
    var instance = new ServerInstance(Environment.ProcessId, deckRoot, port, Headless: true,
      Build: ServerInstance.CurrentBuild);
    registry.Register(instance);
    return new StubServer(app, registry, instance, requests);
  }

  private static Measurement CleanMeasurement() => new Measurement([], [], [], [], []);

  [Fact]
  public async Task Check_reports_a_load_error_without_starting_a_server()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("deck");
    deck.Write("deck/presentation.yaml", "name: Broken\ntheme: ../missing\nslides: [one]");
    var registry = new InstanceRegistry(deck.Folder.PathTo("servers"));
    var text = new StringWriter();
    var json = new StringWriter();

    // Act
    int textCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check"]), registry, text);
    int jsonCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check", "--json"]), registry, json);

    // Assert
    textCode.ShouldBe(1);
    text.ToString().ShouldContain("error");
    jsonCode.ShouldBe(1);
    using JsonDocument parsed = JsonDocument.Parse(json.ToString());
    parsed.RootElement.GetProperty("errors").GetInt32().ShouldBe(1);
    parsed.RootElement.GetProperty("findings").GetArrayLength().ShouldBe(1);
  }

  [Fact]
  public async Task Check_exits_zero_when_clean_and_one_on_measured_errors_and_filters_by_slides()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("deck");
    var registry = new InstanceRegistry(deck.Folder.PathTo("servers"));
    var overflow = new Measurement(
      [],
      [new BoxMeasure("two", "title", "slide", "none", 1, [1], 12, null, null)],
      [], [], []);
    bool fail = false;
    await using StubServer stub = await StartStub(root, registry,
      _ => fail ? overflow : CleanMeasurement());
    var clean = new StringWriter();
    var errored = new StringWriter();
    var filtered = new StringWriter();

    // Act
    int cleanCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check"]), registry, clean);
    fail = true;
    int errorCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check"]), registry, errored);
    int filteredCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check", "--slides", "one"]), registry, filtered);

    // Assert
    cleanCode.ShouldBe(0);
    clean.ToString().ShouldContain("no findings");
    errorCode.ShouldBe(1);
    errored.ToString().ShouldContain("runs past its frame by 12pt");
    filteredCode.ShouldBe(0);
    filtered.ToString().ShouldContain("no findings");
  }

  [Fact]
  public async Task Screenshot_writes_the_png_and_skips_measurement_for_the_theme_view()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("deck");
    var registry = new InstanceRegistry(deck.Folder.PathTo("servers"));
    byte[] fakePng = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    await using StubServer stub = await StartStub(root, registry,
      _ => CleanMeasurement(), _ => Results.Bytes(fakePng, "image/png"));
    string slideOut = deck.Folder.PathTo("out/one.png");
    string themeOut = deck.Folder.PathTo("out/theme.png");
    var slideOutput = new StringWriter();
    var themeOutput = new StringWriter();

    // Act
    int slideCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--screenshot", "one", "--output", slideOut]), registry, slideOutput);
    int themeCode = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--screenshot", "theme", "--output", themeOut]), registry, themeOutput);

    // Assert
    slideCode.ShouldBe(0);
    File.ReadAllBytes(slideOut).ShouldBe(fakePng);
    slideOutput.ToString().ShouldContain(slideOut);
    themeCode.ShouldBe(0);
    File.ReadAllBytes(themeOut).ShouldBe(fakePng);
    stub.Requests.ShouldContain("/api/measure?slides=one");
    stub.Requests.Count(request => request.StartsWith("/api/measure", StringComparison.Ordinal)).ShouldBe(1);
  }

  [Fact]
  public async Task Screenshot_rejects_a_revision_range_and_surfaces_server_errors()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("deck");
    var registry = new InstanceRegistry(deck.Folder.PathTo("servers"));
    await using StubServer stub = await StartStub(root, registry,
      _ => CleanMeasurement(), _ => Results.Problem("slide missing", statusCode: (int)HttpStatusCode.NotFound));

    // Act & Assert
    InvalidDataException rangeError = await Should.ThrowAsync<InvalidDataException>(() =>
      BrowserCommands.Run(root,
        CommandLineOptions.Parse([root, "--screenshot", "one", "--diff", "HEAD~1..HEAD"]),
        registry, new StringWriter()));
    rangeError.Message.ShouldContain("one revision rather than two");

    InvalidDataException serverError = await Should.ThrowAsync<InvalidDataException>(() =>
      BrowserCommands.Run(root,
        CommandLineOptions.Parse([root, "--screenshot", "one", "--output", deck.Folder.PathTo("x.png")]),
        registry, new StringWriter()));
    serverError.Message.ShouldContain("slide missing");
  }

  [Fact]
  [Trait("Needs", "git")]
  public async Task Check_diff_reports_only_introduced_findings_and_counts_cleared_ones()
  {
    // Arrange
    using var deck = new DeckFixture();
    string root = deck.Folder.PathTo("deck");
    deck.Folder.Commit();
    var registry = new InstanceRegistry(deck.Folder.PathTo("servers"));
    var beforeOverflow = new Measurement(
      [],
      [new BoxMeasure("one", "title", "slide", "none", 1, [1], 10, null, null)],
      [], [], []);
    var afterOverflow = new Measurement(
      [],
      [new BoxMeasure("two", "title", "slide", "none", 1, [1], 10, null, null)],
      [], [], []);
    await using StubServer stub = await StartStub(root, registry,
      request => request.Query.ContainsKey("base") ? beforeOverflow : afterOverflow);
    var output = new StringWriter();

    // Act
    int code = await BrowserCommands.Run(root,
      CommandLineOptions.Parse([root, "--check", "--diff", "HEAD", "--json"]), registry, output);

    // Assert
    code.ShouldBe(1);
    using JsonDocument parsed = JsonDocument.Parse(output.ToString());
    parsed.RootElement.GetProperty("errors").GetInt32().ShouldBe(1);
    parsed.RootElement.GetProperty("findings").GetArrayLength().ShouldBe(1);
    parsed.RootElement.GetProperty("findings")[0].GetProperty("slide").GetString().ShouldBe("two");
    parsed.RootElement.GetProperty("gone").GetArrayLength().ShouldBe(1);
    parsed.RootElement.GetProperty("gone")[0].GetProperty("slide").GetString().ShouldBe("one");
  }


}
