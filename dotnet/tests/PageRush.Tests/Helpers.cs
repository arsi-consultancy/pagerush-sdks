using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush.Tests;

internal sealed class RecordedRequest
{
    public RecordedRequest(string method, Uri url, Dictionary<string, string> headers, byte[]? body)
    {
        Method = method;
        Url = url;
        Headers = headers;
        Body = body;
    }

    public string Method { get; }

    public Uri Url { get; }

    public Dictionary<string, string> Headers { get; }

    public byte[]? Body { get; }

    public string PathAndQuery => Url.PathAndQuery;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public JsonElement Json() => JsonDocument.Parse(Body ?? throw new InvalidOperationException("no body")).RootElement.Clone();
}

/// <summary>An HttpMessageHandler that records requests and answers from a callback.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, int, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly object _lock = new();

    public FakeHandler(Func<RecordedRequest, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public FakeHandler(Func<RecordedRequest, int, HttpResponseMessage> respond)
        : this((r, i, _) => Task.FromResult(respond(r, i)))
    {
    }

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = string.Join(", ", h.Value);
        }

        if (request.Content != null)
        {
            foreach (var h in request.Content.Headers)
            {
                headers[h.Key] = string.Join(", ", h.Value);
            }
        }

        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!, headers, body);
        int index;
        lock (_lock)
        {
            Requests.Add(recorded);
            index = Requests.Count - 1;
        }

        var response = await _respond(recorded, index, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

internal static class Responses
{
    public static HttpResponseMessage Json(int status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddHeaders(response, headers);
        return response;
    }

    public static HttpResponseMessage Json(int status, object value, params (string Name, string Value)[] headers) =>
        Json(status, JsonSerializer.Serialize(value), headers);

    public static HttpResponseMessage Problem(int status, string code, string? detail = null, params (string Name, string Value)[] headers)
    {
        var body = new Dictionary<string, object?> { ["type"] = "https://pagerush.dev/errors/" + code, ["title"] = "Title of " + code, ["status"] = status, ["code"] = code };
        if (detail != null)
        {
            body["detail"] = detail;
        }

        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
        AddHeaders(response, headers);
        return response;
    }

    public static HttpResponseMessage Bytes(int status, byte[] body, string contentType, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        AddHeaders(response, headers);
        return response;
    }

    public static HttpResponseMessage Empty(int status, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent([]) };
        AddHeaders(response, headers);
        return response;
    }

    private static void AddHeaders(HttpResponseMessage response, (string Name, string Value)[] headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}

internal static class Samples
{
    public const string ApiKey = "pk_test_secret_key_123";

    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%test\n%%EOF\n");

    public const string TemplateJson = """{"id":"tpl_1","name":"Invoice","live_version":1,"latest_version":1,"created_at":"2026-01-01T00:00:00.000Z","updated_at":"2026-01-01T00:00:00.000Z"}""";

    public const string VersionJson = """{"version":1,"source":"Hello","document":null,"files":[],"sample_data":{"name":"Ada"},"typst_version":"0.13.1","created_at":"2026-01-01T00:00:00.000Z"}""";

    public static string Job(string status, string? downloadUrl = null) =>
        $$"""{"id":"job_1","status":"{{status}}","mode":"async","region":"eu","template_id":"tpl_1","template_version":2,"engine":null,"bytes":null,"duration_ms":null,"error":null,"delivery":null,"metadata":{"order":"42"},"download_url":{{(downloadUrl is null ? "null" : JsonSerializer.Serialize(downloadUrl))}},"delete_after_download":false,"created_at":"2026-01-01T00:00:00.000Z","completed_at":null}""";

    public static string Batch(string status, bool items = false) =>
        $$"""{"id":"bat_1","status":"{{status}}","template_id":"tpl_1","template_version":3,"item_count":2,"succeeded":1,"failed":0,"zip_url":"https://pagerush.dev/v1/batches/bat_1/zip","expires_at":null,"metadata":{"run":"2026-11"},"delete_after_download":false,"delivery":null,"created_at":"2026-01-01T00:00:00.000Z","finished_at":null{{(items ? ""","items":[{"index":0,"job_id":"job_a","status":"succeeded","filename":"a.pdf","bytes":1234,"renders":1,"error":null},{"index":1,"job_id":"job_b","status":"failed","filename":"b.pdf","bytes":null,"renders":1,"error":{"code":"RENDER_FAILED","message":"boom"}}]""" : "")}}}""";
}

/// <summary>A client wired to a fake handler, with sleeps recorded instead of waited.</summary>
internal sealed class TestClient
{
    public TestClient(FakeHandler handler, Action<PageRushClientOptions>? configure = null)
    {
        Handler = handler;
        var options = new PageRushClientOptions
        {
            ApiKey = Samples.ApiKey,
            BaseUrl = "https://api.test",
            HttpClient = new HttpClient(handler),
            Sleep = (d, ct) =>
            {
                lock (Sleeps)
                {
                    Sleeps.Add(d);
                }

                Clock += d;
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        configure?.Invoke(options);
        Client = new PageRushClient(options);
        Client.Transport.Now = () => Clock;
    }

    public FakeHandler Handler { get; }

    public PageRushClient Client { get; }

    public List<TimeSpan> Sleeps { get; } = [];

    public DateTimeOffset Clock { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public List<RecordedRequest> Requests => Handler.Requests;

    public static TestClient Answering(Func<RecordedRequest, int, HttpResponseMessage> respond, Action<PageRushClientOptions>? configure = null) =>
        new(new FakeHandler(respond), configure);

    public static TestClient Sequence(params Func<HttpResponseMessage>[] responses) =>
        Answering((_, i) => responses[Math.Min(i, responses.Length - 1)]());
}

internal static class Extensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    public static string Utf8(this byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public static string[] Keys(this JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();
}
