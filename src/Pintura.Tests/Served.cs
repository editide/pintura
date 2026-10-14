using Microsoft.AspNetCore.Builder;

namespace Pintura.Tests;

/// <summary>A fixture's presentation served on a free port, with a client to call it, stopped on dispose.</summary>
public sealed class Served : IAsyncDisposable
{
  private Served(string url, PresentationStore store, HeadlessBrowser headless, WebApplication app)
  {
    Url = url;
    Store = store;
    Headless = headless;
    App = app;
  }

  public string Url { get; }
  public PresentationStore Store { get; }
  public HttpClient Client { get; } = new HttpClient();
  private HeadlessBrowser Headless { get; }
  private WebApplication App { get; }

  /// <summary>Serves the fixture's deck, with <paramref name="assets"/> as the page's files, the deck's folder unless
  /// a test needs the real page.</summary>
  public static async Task<Served> Start(DeckFixture deck, string? assets = null)
  {
    string url = $"http://127.0.0.1:{PreviewServer.FreePort()}";
    var store = new PresentationStore(deck.Folder.PathTo("deck"), assets ?? deck.Folder.PathTo("deck"),
      TextWriter.Null);
    var headless = new HeadlessBrowser(url);
    WebApplication app = PreviewServer.Create(assets ?? deck.Folder.Root, url);
    PreviewServer.Map(app, store, headless, null);
    await app.StartAsync();
    return new Served(url, store, headless, app);
  }

  /// <summary>The page's own files, from the source folder.</summary>
  public static string Interface()
  {
    return Path.Combine(LoaderTests.RepositoryRoot(), "src", "Pintura", "wwwroot");
  }

  public Task Stop()
  {
    return App.StopAsync();
  }

  public async ValueTask DisposeAsync()
  {
    Client.Dispose();
    await App.DisposeAsync();
    await Headless.DisposeAsync();
    Store.Dispose();
  }


}