using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Pintura;

/// <summary>editide's tools on its API: a deck is uploaded once, each call names it, and saving downloads it. editide
/// bills the slides an export writes. Sessions are left to expire.</summary>
public sealed class EditideTools : IToolHost
{
  /// <summary>The oldest editide API the fixtures pass on.</summary>
  public static readonly Version Minimum = new Version(1, 0, 6);

  private const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

  private static readonly TimeSpan Throttled = TimeSpan.FromMinutes(5);

  private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

  private readonly HttpClient http;
  private readonly Action<TimeSpan> wait;
  private string? version;

  public EditideTools(HttpClient http, string key, Action<TimeSpan> wait)
  {
    this.http = http;
    this.wait = wait;
    http.DefaultRequestHeaders.Add("X-Api-Key", key);
    // editide's API refuses a request that names no user agent, as HttpClient sends by default.
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pintura",
      typeof(EditideTools).Assembly.GetName().Version?.ToString(3)));
  }

  public static EditideTools Connect(string url)
  {
    string key = Account.Default.Key ?? throw new ArgumentException("exporting and importing run on editide: sign " +
      $"in with `pintura --login`, or set {Account.KeyVariable}");
    return Connect(url, key);
  }

  public static EditideTools Connect(string url, string key)
  {
    return new EditideTools(new HttpClient { BaseAddress = Address(url), Timeout = TimeSpan.FromMinutes(5) }, key,
      Thread.Sleep);
  }

  /// <summary>The API's address, refused unless it's https, or http on this machine for testing, since the key goes
  /// with every call.</summary>
  public static Uri Address(string url)
  {
    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? address) &&
      (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
    {
      return address;
    }
    throw new InvalidOperationException($"{url} isn't an https address, so the API key would travel to it " +
      "unencrypted; set EDITIDE_URL to one, or to http://localhost for testing");
  }

  /// <summary>The key's plan, as <c>whoami</c> answers it.</summary>
  public AccountPlan Plan()
  {
    JsonNode answer = Send(() => new HttpRequestMessage(HttpMethod.Get, "api/auth/whoami"), "checking your plan");
    return new AccountPlan((string?)answer["product"] ?? "", (string?)answer["tier"] ?? "",
      (int?)answer["slides_delivered_this_period"] ?? 0,
      (int?)answer["tier_definition"]?["monthly_slides_included"] ?? 0);
  }

  public string Version => $"{http.BaseAddress} {Checked()}";

  /// <summary>The API keeps 25 attachments a presentation.</summary>
  public int? MostPictures => 25;

  public IToolSession Open(string deck)
  {
    Checked();
    JsonNode answer = Send(() => new HttpRequestMessage(HttpMethod.Post, "api/presentations")
    {
      Content = Form(deck, PptxType)
    }, "uploading the deck");
    return new Session(this, (string)answer["file_id"]!);
  }

  /// <summary>The API's version, refusing one older than <see cref="Minimum"/>.</summary>
  private string Checked()
  {
    if (version is not null)
    {
      return version;
    }
    JsonNode answer = Send(() => new HttpRequestMessage(HttpMethod.Get, "version"), "asking editide's version");
    string reported = (string?)answer["version"] ?? "";
    if (!System.Version.TryParse(reported.Split('-')[0], out Version? parsed) || parsed < Minimum)
    {
      throw new InvalidOperationException($"editide at {http.BaseAddress} runs version {reported}, older than " +
        $"the {Minimum} this Pintura needs");
    }
    version = reported;
    return version;
  }

  private static MultipartFormDataContent Form(string path, string type)
  {
    var file = new ByteArrayContent(File.ReadAllBytes(path));
    file.Headers.ContentType = new MediaTypeHeaderValue(type);
    return new MultipartFormDataContent { { file, "file", Path.GetFileName(path) } };
  }

  /// <summary>Sends a request and answers its JSON body.</summary>
  private JsonNode Send(Func<HttpRequestMessage> request, string doing, string? tool = null)
  {
    (HttpStatusCode status, byte[] body) = Raw(request, doing);
    JsonNode? json = Json(body);
    return (int)status is >= 200 and < 300 && json is not null ? json : throw Failure(status, json, body, doing, tool);
  }

  /// <summary>Sends a request, again after the wait editide's Retry-After asks while it throttles, for up to five
  /// minutes, and answers its status and body; a request is made afresh each time, as sending one disposes its content.
  /// </summary>
  private (HttpStatusCode Status, byte[] Body) Raw(Func<HttpRequestMessage> request, string doing)
  {
    TimeSpan waited = TimeSpan.Zero;
    while (true)
    {
      HttpResponseMessage response;
      try
      {
        using HttpRequestMessage message = request();
        response = http.Send(message);
      }
      catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
      {
        throw new InvalidOperationException($"{doing} failed: couldn't reach editide at {http.BaseAddress} " +
          $"({exception.Message})", exception);
      }
      using (response)
      {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
          RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
          TimeSpan delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? Pause;
          delay = delay < Pause ? Pause : delay;
          if (waited + delay <= Throttled)
          {
            wait(delay);
            waited += delay;
            continue;
          }
        }
        using Stream stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return (response.StatusCode, buffer.ToArray());
      }
    }
  }

  private static JsonNode? Json(byte[] body)
  {
    try
    {
      return body.Length == 0 ? null : JsonNode.Parse(body);
    }
    catch (JsonException)
    {
      return null;
    }
  }

  private InvalidOperationException Failure(HttpStatusCode status, JsonNode? json, byte[] body, string doing,
    string? tool)
  {
    string? told = (string?)json?["error"] ?? (string?)json?["message"];
    string text = Encoding.UTF8.GetString(body).Trim();
    // An HTML page in place of the API's JSON says no more than its status's name.
    string said = (told ?? (text.StartsWith('<') ? ReasonPhrases.GetReasonPhrase((int)status) : text)).Trim();
    string message = status switch
    {
      _ when tool is not null && status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.OK =>
        $"{tool} failed: {said}",
      HttpStatusCode.Unauthorized =>
        $"{doing} failed: editide didn't accept the API key; sign in again with `pintura --login`, or check " +
        $"{Account.KeyVariable}",
      HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden when told is not null => $"{doing} failed: {said}",
      HttpStatusCode.NotFound => $"{doing} failed: editide no longer has the deck, as its session expired; try again",
      HttpStatusCode.TooManyRequests => $"{doing} failed: editide is still throttling after {Throttled.TotalMinutes} " +
        $"minutes ({said})",
      >= HttpStatusCode.InternalServerError => $"{doing} failed: editide answered {(int)status} ({said}); try again " +
        "shortly",
      _ => $"{doing} failed: editide answered {(int)status} ({said})"
    };
    return new InvalidOperationException(message);
  }

  /// <summary>The API's <c>data</c>, or its <c>message</c> when it has none: JSON when it reads as JSON, text
  /// otherwise.</summary>
  private static JsonNode? Answer(JsonNode answer)
  {
    JsonNode? data = answer["data"];
    if (data is not null && data.GetValueKind() != JsonValueKind.String)
    {
      return data.DeepClone();
    }
    string text = ((string?)data ?? (string?)answer["message"] ?? "").Trim();
    return text.StartsWith('{') || text.StartsWith('[') ? JsonNode.Parse(text) : JsonValue.Create(text);
  }

  private sealed class Session : IToolSession
  {
    private readonly EditideTools editide;
    private readonly string id;

    public Session(EditideTools editide, string id)
    {
      this.editide = editide;
      this.id = id;
    }

    public JsonNode? Call(string tool, JsonObject input)
    {
      JsonObject body = input.DeepClone().AsObject();
      body["presentation_id"] = id;
      JsonNode answer = editide.Send(() => new HttpRequestMessage(HttpMethod.Post, $"api/tools/{tool}")
      {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
      }, $"calling {tool}", tool);
      if ((bool?)answer["success"] != true)
      {
        throw editide.Failure(HttpStatusCode.OK, answer, [], $"calling {tool}", tool);
      }
      return Answer(answer);
    }

    public string Attach(string picture)
    {
      JsonNode answer = editide.Send(() => new HttpRequestMessage(HttpMethod.Post, $"api/presentations/{id}/attachments")
      {
        Content = Form(picture, "application/octet-stream")
      }, $"uploading {Path.GetFileName(picture)}");
      return (string)answer["attachment_id"]!;
    }

    public void Save(string output)
    {
      string doing = "downloading the deck";
      (HttpStatusCode status, byte[] body) = editide.Raw(() =>
        new HttpRequestMessage(HttpMethod.Get, $"api/presentations/{id}"), doing);
      if ((int)status is < 200 or >= 300)
      {
        throw editide.Failure(status, Json(body), body, doing, null);
      }
      File.WriteAllBytes(output, body);
    }

    public void Dispose()
    {
    }


  }


}