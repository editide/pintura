using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class EditideTests
{
  /// <summary>editide's API as a list of answers, keyed by method and path, with every request it saw.</summary>
  private sealed class FakeEditide : HttpMessageHandler
  {
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)>> answers =
      new Dictionary<string, Queue<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)>>(StringComparer.Ordinal);

    public List<(string Route, string? Key, string Body)> Requests { get; } = new List<(string, string?, string)>();

    public List<string> Agents { get; } = new List<string>();

    public FakeEditide Answer(string route, HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
      if (!answers.TryGetValue(route, out Queue<(HttpStatusCode, string, TimeSpan?)>? queue))
      {
        queue = new Queue<(HttpStatusCode, string, TimeSpan?)>();
        answers[route] = queue;
      }
      queue.Enqueue((status, body, retryAfter));
      return this;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      string route = $"{request.Method} {request.RequestUri!.AbsolutePath}";
      string body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).Result;
      Agents.Add(request.Headers.UserAgent.ToString());
      Requests.Add((route, request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys)
        ? keys.Single()
        : null, body));
      if (!answers.TryGetValue(route, out Queue<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)>? queue)
        || queue.Count == 0)
      {
        return new HttpResponseMessage(HttpStatusCode.NotFound);
      }
      (HttpStatusCode status, string answer, TimeSpan? retryAfter) = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
      var response = new HttpResponseMessage(status)
      {
        Content = new StringContent(answer, Encoding.UTF8, "application/json")
      };
      if (retryAfter is TimeSpan delta)
      {
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
      }
      return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
      CancellationToken cancellationToken)
    {
      return Task.FromResult(Send(request, cancellationToken));
    }


  }

  private static EditideTools Tools(FakeEditide editide, List<TimeSpan>? waits = null)
  {
    return new EditideTools(new HttpClient(editide) { BaseAddress = new Uri("https://editide.test") }, "key-1",
      wait => waits?.Add(wait));
  }

  private static FakeEditide Uploaded()
  {
    return new FakeEditide()
      .Answer("GET /version", HttpStatusCode.OK, """{ "version": "1.0.6" }""")
      .Answer("POST /api/presentations", HttpStatusCode.OK, """{ "file_id": "deck-1", "filename": "deck.pptx" }""");
  }

  private static IToolSession Open(FakeEditide editide, TempFolder folder)
  {
    string deck = Path.Combine(folder.Root, "deck.pptx");
    File.WriteAllText(deck, "pptx");
    return Tools(editide).Open(deck);
  }

  [Fact]
  public void A_tool_call_names_the_deck_sends_the_key_and_answers_the_tools_data()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded().Answer("POST /api/tools/list_slides", HttpStatusCode.OK,
      """{ "success": true, "message": "", "data": "[{\"index\":1}]" }""");
    using IToolSession session = Open(editide, folder);

    // Act
    JsonNode? answer = session.Call("list_slides", new JsonObject { ["mode"] = "brief" });

    // Assert
    answer!.ToJsonString().ShouldBe("""[{"index":1}]""");
    (string route, string? key, string body) = editide.Requests.Last();
    route.ShouldBe("POST /api/tools/list_slides");
    key.ShouldBe("key-1");
    JsonNode.Parse(body)!["presentation_id"]!.GetValue<string>().ShouldBe("deck-1");
    JsonNode.Parse(body)!["mode"]!.GetValue<string>().ShouldBe("brief");
  }

  [Fact]
  public void A_tool_that_answers_only_a_message_answers_its_text()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded().Answer("POST /api/tools/edit_slide", HttpStatusCode.OK,
      """{ "success": true, "message": "Slide 1 updated" }""");
    using IToolSession session = Open(editide, folder);

    // Act
    JsonNode? answer = session.Call("edit_slide", new JsonObject());

    // Assert
    answer!.GetValue<string>().ShouldBe("Slide 1 updated");
  }

  [Fact]
  public void A_throttled_call_is_retried_until_it_goes_through()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded()
      .Answer("POST /api/tools/add_slide", HttpStatusCode.TooManyRequests, """{ "error": "Rate limit exceeded" }""")
      .Answer("POST /api/tools/add_slide", HttpStatusCode.TooManyRequests, """{ "error": "Rate limit exceeded" }""")
      .Answer("POST /api/tools/add_slide", HttpStatusCode.OK, """{ "success": true, "data": { "index": 2 } }""");
    using IToolSession session = Open(editide, folder);

    // Act
    JsonNode? answer = session.Call("add_slide", new JsonObject());

    // Assert
    answer!["index"]!.GetValue<int>().ShouldBe(2);
    editide.Requests.Count(request => request.Route == "POST /api/tools/add_slide").ShouldBe(3);
  }

  [Fact]
  public void A_throttled_call_waits_as_long_as_Retry_After_asks()
  {
    // Arrange
    var waits = new List<TimeSpan>();
    FakeEditide editide = new FakeEditide()
      .Answer("GET /api/auth/whoami", HttpStatusCode.TooManyRequests, """{ "error": "Rate limit exceeded" }""",
        TimeSpan.FromSeconds(30))
      .Answer("GET /api/auth/whoami", HttpStatusCode.OK, """{ "authenticated": true, "product": "api", "tier": "max" }""");

    // Act
    Tools(editide, waits).Plan();

    // Assert
    waits.ShouldBe([TimeSpan.FromSeconds(30)]);
  }

  [Fact]
  public void A_throttled_call_whose_Retry_After_outlasts_five_minutes_fails_without_waiting()
  {
    // Arrange
    var waits = new List<TimeSpan>();
    FakeEditide editide = new FakeEditide().Answer("GET /api/auth/whoami", HttpStatusCode.TooManyRequests,
      """{ "error": "Rate limit exceeded" }""", TimeSpan.FromMinutes(10));

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Tools(editide, waits).Plan());

    // Assert
    failure.Message.ShouldContain("still throttling");
    waits.ShouldBeEmpty();
    editide.Requests.Count.ShouldBe(1);
  }

  [Fact]
  public void A_failed_tool_says_which_tool_and_why()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded().Answer("POST /api/tools/edit_slide", HttpStatusCode.BadRequest,
      """{ "success": false, "message": "Element 'title' not found on slide 3" }""");
    using IToolSession session = Open(editide, folder);

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() =>
      session.Call("edit_slide", new JsonObject()));

    // Assert
    failure.Message.ShouldContain("edit_slide");
    failure.Message.ShouldContain("Element 'title' not found on slide 3");
  }

  [Fact]
  public void A_rejected_key_says_how_to_sign_in_again()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = new FakeEditide()
      .Answer("GET /version", HttpStatusCode.OK, """{ "version": "1.0.6" }""")
      .Answer("POST /api/presentations", HttpStatusCode.Unauthorized, """{ "error": "Invalid API key" }""");

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Open(editide, folder));

    // Assert
    failure.Message.ShouldContain("pintura --login");
    failure.Message.ShouldContain("EDITIDE_API_KEY");
  }

  [Fact]
  public void A_keys_plan_is_read_from_whoami()
  {
    // Arrange
    FakeEditide editide = new FakeEditide().Answer("GET /api/auth/whoami", HttpStatusCode.OK, """
      { "authenticated": true, "product": "api", "tier": "max", "slides_delivered_this_period": 112,
        "tier_definition": { "monthly_slides_included": 750 } }
      """);

    // Act
    AccountPlan plan = Tools(editide).Plan();

    // Assert
    plan.ShouldBe(new AccountPlan("api", "max", 112, 750));
    editide.Requests.Single().Key.ShouldBe("key-1");
  }

  [Fact]
  public void Every_request_names_pintura_as_its_user_agent()
  {
    // Arrange
    FakeEditide editide = new FakeEditide().Answer("GET /api/auth/whoami", HttpStatusCode.OK, """
      { "authenticated": true, "product": "api", "tier": "max" }
      """);

    // Act
    Tools(editide).Plan();

    // Assert
    editide.Agents.Single().ShouldStartWith("Pintura/");
  }

  [Fact]
  public void An_html_error_page_is_named_by_its_status_rather_than_printed()
  {
    // Arrange
    FakeEditide editide = new FakeEditide().Answer("GET /api/auth/whoami", HttpStatusCode.Forbidden, """
      <html>
      <head><title>403 Forbidden</title></head>
      <body><center><h1>403 Forbidden</h1></center></body>
      </html>
      """);

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Tools(editide).Plan());

    // Assert
    failure.Message.ShouldBe("checking your plan failed: editide answered 403 (Forbidden)");
  }

  [Fact]
  public void A_spent_plan_passes_on_what_editide_says_to_do()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded().Answer("GET /api/presentations/deck-1", HttpStatusCode.PaymentRequired,
      """{ "error": "Monthly slide limit reached. Upgrade your plan to continue.", "code": "slide_quota_exceeded" }""");
    using IToolSession session = Open(editide, folder);

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() =>
      session.Save(Path.Combine(folder.Root, "out.pptx")));

    // Assert
    failure.Message.ShouldContain("Monthly slide limit reached. Upgrade your plan to continue.");
  }

  [Fact]
  public void An_older_api_is_refused_before_anything_is_uploaded()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = new FakeEditide().Answer("GET /version", HttpStatusCode.OK, """{ "version": "1.0.5" }""");

    // Act
    InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Open(editide, folder));

    // Assert
    failure.Message.ShouldContain("1.0.5");
    editide.Requests.ShouldNotContain(request => request.Route == "POST /api/presentations");
  }

  [Fact]
  public void A_picture_is_attached_to_the_deck_and_named_by_its_attachment_id()
  {
    // Arrange
    using var folder = new TempFolder();
    string picture = Path.Combine(folder.Root, "logo.png");
    File.WriteAllText(picture, "png");
    FakeEditide editide = Uploaded().Answer("POST /api/presentations/deck-1/attachments", HttpStatusCode.OK,
      """{ "attachment_id": "att-7", "filename": "logo.png" }""");
    using IToolSession session = Open(editide, folder);

    // Act
    string uri = session.Attach(picture);

    // Assert
    uri.ShouldBe("att-7");
    editide.Requests.Last().Body.ShouldContain("filename=logo.png");
  }

  [Fact]
  public void Saving_downloads_the_deck_as_the_tools_left_it()
  {
    // Arrange
    using var folder = new TempFolder();
    FakeEditide editide = Uploaded().Answer("GET /api/presentations/deck-1", HttpStatusCode.OK, "edited deck");
    using IToolSession session = Open(editide, folder);
    string output = Path.Combine(folder.Root, "out.pptx");

    // Act
    session.Save(output);

    // Assert
    File.ReadAllText(output).ShouldBe("edited deck");
  }

  [Theory]
  [InlineData("https://cloud.editide.com", true)]
  [InlineData("http://localhost:5000", true)]
  [InlineData("http://127.0.0.1:8080", true)]
  [InlineData("http://cloud.editide.com", false)]
  [InlineData("ftp://cloud.editide.com", false)]
  [InlineData("cloud.editide.com", false)]
  public void The_key_goes_only_to_an_https_address_or_to_this_machine(string url, bool taken)
  {
    // Act
    Func<Uri> address = () => EditideTools.Address(url);

    // Assert
    if (taken)
    {
      address().ShouldBe(new Uri(url));
    }
    else
    {
      Should.Throw<InvalidOperationException>(address).Message.ShouldContain("isn't an https address");
    }
  }


}