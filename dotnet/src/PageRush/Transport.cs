using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PageRush;

/// <summary>Which failures a request may be retried on.</summary>
internal enum RetryPolicy
{
    /// <summary>GET, DELETE and batch create: connection errors, timeouts, 429, 502, 503, 504.</summary>
    Safe,

    /// <summary>Other POST and PATCH: only 429 and 503, which the server returns before doing anything.</summary>
    Unsafe,

    /// <summary>Render: as <see cref="Safe"/>, plus 409 with code CONFLICT.</summary>
    Render,
}

internal sealed class ApiRequest
{
    public HttpMethod Method { get; set; } = HttpMethod.Get;

    public string Url { get; set; } = "";

    public byte[]? Body { get; set; }

    public string Accept { get; set; } = "application/json";

    public string? IdempotencyKey { get; set; }

    public bool PreferAsync { get; set; }

    public bool NoAuth { get; set; }

    public RetryPolicy Policy { get; set; }
}

internal sealed class ApiResponse(int status, Dictionary<string, string> headers, byte[] body)
{
    public int Status { get; } = status;

    public byte[] Body { get; } = body;

    public string? Header(string name) => headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>The media type of Content-Type, lower-cased, or null.</summary>
    public string? MediaType()
    {
        var ct = Header("Content-Type");
        return ct != null && MediaTypeHeaderValue.TryParse(ct, out var parsed) ? parsed.MediaType?.ToLowerInvariant() : null;
    }
}

/// <summary>Sends requests with retries, timeouts and safe redirects, and maps failures to exceptions.</summary>
internal sealed class Transport
{
    internal const int MaxRedirects = 5;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new StrictStringConverter() },
    };

    private static readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly Regex _idempotencyKeyPattern = new(@"^[A-Za-z0-9_\-:.]{1,255}$", RegexOptions.CultureInvariant);
    private static readonly Random _jitter = new();
    private static readonly string _userAgent = "pagerush-dotnet/" + PageRushClient.Version;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;

    public Transport(HttpClient http, string apiKey, string baseUrl, TimeSpan timeout, int maxRetries, Func<TimeSpan, CancellationToken, Task> sleep)
    {
        _http = http;
        _apiKey = apiKey;
        BaseUrl = baseUrl;
        _timeout = timeout;
        _maxRetries = maxRetries;
        Sleep = sleep;
    }

    public string BaseUrl { get; }

    public Func<TimeSpan, CancellationToken, Task> Sleep { get; }

    /// <summary>The clock used by waits. Tests replace it.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public static string Segment(string id, string paramName)
    {
        Guard.NotNull(id, paramName);

        if (id.Length == 0)
        {
            throw new ArgumentException("The id must not be empty.", paramName);
        }

        // URI normalisation would remove "." and ".." from the path, even when escaped.
        if (id is "." or "..")
        {
            throw new ArgumentException("The id must not be \".\" or \"..\".", paramName);
        }

        return Uri.EscapeDataString(id);
    }

    public string Url(string path) => BaseUrl + path;

    public static string ResolveIdempotencyKey(string? key)
    {
        if (key is null)
        {
            return Guid.NewGuid().ToString("D");
        }

        if (!_idempotencyKeyPattern.IsMatch(key))
        {
            throw new PageRushException(0, ErrorCodes.ValidationFailed, detail: "IdempotencyKey must be 1 to 255 characters: letters, digits, '_', '-', ':' or '.'.");
        }

        return key;
    }

    public static byte[] Encode(object body)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException)
        {
            throw new PageRushException(0, ErrorCodes.ValidationFailed, detail: "The request body cannot be encoded as JSON: " + e.Message, innerException: e);
        }
    }

    public async Task<T> SendJsonAsync<T>(ApiRequest request, Func<T, bool> valid, CancellationToken cancellationToken)
        where T : class
    {
        var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ReadJson(response, valid);
    }

    public static T ReadJson<T>(ApiResponse response, Func<T, bool> valid)
        where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(response.Body, JsonOptions);
        }
        catch (JsonException e)
        {
            throw InvalidResponse(response.Status, "The response body is not the expected JSON.", e);
        }

        if (value is null || !valid(value))
        {
            throw InvalidResponse(response.Status, "The response body is missing required fields.");
        }

        return value;
    }

    public static PageRushException InvalidResponse(int status, string detail, Exception? inner = null) =>
        new(status, ErrorCodes.InvalidResponse, detail: detail, innerException: inner);

    /// <summary>Sends a request with retries and returns the final 2xx response.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageRushException error;
            ExceptionDispatchInfo? thrown = null;
            try
            {
                var response = await AttemptAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Status is >= 200 and < 300)
                {
                    return response;
                }

                error = ErrorFromResponse(response);
            }
            catch (PageRushConnectionException e)
            {
                error = e;
                thrown = ExceptionDispatchInfo.Capture(e);
            }

            if (attempt >= _maxRetries || !IsRetryable(request.Policy, error))
            {
                thrown?.Throw();
                throw error;
            }

            var delay = error.RetryAfter is { } retryAfter
                ? (retryAfter > _maxRetryAfter ? _maxRetryAfter : retryAfter)
                : Backoff(attempt + 1);
            await Sleep(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Polls until <paramref name="isDone"/> is true, or throws WAIT_TIMEOUT.</summary>
    public async Task<T> PollAsync<T>(
        Func<CancellationToken, Task<T>> fetch,
        Func<T, bool> isDone,
        WaitOptions? options,
        TimeSpan defaultTimeout,
        TimeSpan defaultInterval,
        Func<T, TimeSpan, PageRushException> onTimeout,
        CancellationToken cancellationToken)
    {
        var timeout = options?.Timeout is { } t && t > TimeSpan.Zero ? t : defaultTimeout;
        var interval = options?.Interval is { } i && i > TimeSpan.Zero ? i : defaultInterval;
        var deadline = Now() + timeout;
        while (true)
        {
            var value = await fetch(cancellationToken).ConfigureAwait(false);
            if (isDone(value))
            {
                return value;
            }

            var remaining = deadline - Now();
            if (remaining <= TimeSpan.Zero)
            {
                throw onTimeout(value, timeout);
            }

            await Sleep(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static TimeSpan Backoff(int n)
    {
        var seconds = Math.Min(8.0, 0.5 * Math.Pow(2, n - 1));
        double factor;
        lock (_jitter)
        {
            factor = 0.75 + (_jitter.NextDouble() * 0.5);
        }

        return TimeSpan.FromSeconds(seconds * factor);
    }

    internal static bool IsRetryable(RetryPolicy policy, PageRushException error)
    {
        var idempotent = policy is RetryPolicy.Safe or RetryPolicy.Render;
        if (error.Status == 0)
        {
            return idempotent && error is PageRushConnectionException;
        }

        return error.Status switch
        {
            429 or 503 => true,
            502 or 504 => idempotent,
            409 => policy == RetryPolicy.Render && error.Code == ErrorCodes.Conflict,
            _ => false,
        };
    }

    internal static PageRushException ErrorFromResponse(ApiResponse response)
    {
        string code = ErrorCodes.Unknown;
        string? type = null, title = null, detail = null, requestId = null;
        List<FieldError>? errors = null;
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                code = StringProp(root, "code") is { Length: > 0 } c ? c : code;
                type = StringProp(root, "type");
                title = StringProp(root, "title");
                detail = StringProp(root, "detail");
                requestId = StringProp(root, "request_id");
                if (root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    errors = [];
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            errors.Add(new FieldError { Path = StringProp(item, "path"), Message = StringProp(item, "message") });
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not problem+json: keep code UNKNOWN.
        }

        if (string.IsNullOrEmpty(requestId))
        {
            requestId = response.Header("X-Request-Id");
        }

        return new PageRushException(
            response.Status,
            code,
            title,
            detail,
            errors,
            requestId,
            ParseRetryAfter(response.Header("Retry-After"), DateTimeOffset.UtcNow),
            type);
    }

    internal static string? StringProp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Parses Retry-After as seconds or an HTTP date. Unparsable gives null; values are clamped to 0..1 day.</summary>
    internal static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value!.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (double.IsNaN(seconds))
            {
                return null;
            }

            return TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, 86400)));
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delay = date - now;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
        }

        return null;
    }

    /// <summary>One HTTP attempt, following redirects by hand. The timeout covers redirects and reading the body.</summary>
    private async Task<ApiResponse> AttemptAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(_timeout);
        }

        var origin = new Uri(request.Url);
        var current = origin;
        var method = request.Method;
        var body = request.Body;
        var sendAuth = !request.NoAuth;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var message = BuildMessage(request, method, current, body, sendAuth);
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var location = response.Headers.Location;
                if (status is 301 or 302 or 303 or 307 or 308 && location != null && redirects < MaxRedirects)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
                    {
                        throw new PageRushConnectionException(ErrorCodes.ConnectionError, "Redirect to an unsupported URL from " + Origin(origin) + ".");
                    }

                    // Never send the API key to another origin, which includes https -> http.
                    if (!SameOrigin(next, origin))
                    {
                        sendAuth = false;
                    }

                    // 303, and 301/302 after a POST, switch to GET without a body, as browsers do.
                    if (status == 303 || (status is 301 or 302 && method == HttpMethod.Post))
                    {
                        method = HttpMethod.Get;
                        body = null;
                    }

                    current = next;
                    continue;
                }

                var bytes = await ReadBodyAsync(response, cts.Token).ConfigureAwait(false);
                return new ApiResponse(status, CollectHeaders(response), bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PageRushException)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            // Our per-attempt timeout, or the HttpClient's own Timeout.
            throw new PageRushConnectionException(ErrorCodes.Timeout, $"Request to {Origin(origin)} timed out.", SafeInner(e, request.Url));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Net.WebException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The call was cancelled.", e, cancellationToken);
            }

            if (cts.IsCancellationRequested)
            {
                throw new PageRushConnectionException(ErrorCodes.Timeout, $"Request to {Origin(origin)} timed out.", SafeInner(e, request.Url));
            }

            // The underlying message is left out of ours: it can quote URLs or header values.
            throw new PageRushConnectionException(ErrorCodes.ConnectionError, $"Could not connect to {Origin(origin)}.", SafeInner(e, request.Url));
        }
    }

    private HttpRequestMessage BuildMessage(ApiRequest request, HttpMethod method, Uri url, byte[]? body, bool sendAuth)
    {
        try
        {
            var message = new HttpRequestMessage(method, url);
            if (sendAuth)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            message.Headers.TryAddWithoutValidation("Accept", request.Accept);
            if (request.IdempotencyKey != null)
            {
                message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
            }

            if (request.PreferAsync)
            {
                message.Headers.TryAddWithoutValidation("Prefer", "respond-async");
            }

            if (body != null)
            {
                message.Content = new ByteArrayContent(body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return message;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new PageRushException(0, ErrorCodes.ValidationFailed, detail: "Cannot build the request.", innerException: e);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Disposing the response breaks a read that ignores the token.
        using var registration = cancellationToken.Register(response.Dispose);
#if NET
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(HttpHeaders source)
        {
            foreach (var h in source)
            {
                if (!headers.ContainsKey(h.Key))
                {
                    headers[h.Key] = string.Join(", ", h.Value);
                }
            }
        }

        Add(response.Headers);
        Add(response.Content.Headers);
        return headers;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static string Origin(Uri u) => u.GetLeftPart(UriPartial.Authority);

    /// <summary>Drops the inner exception if it would show a signed URL's query string.</summary>
    private static Exception? SafeInner(Exception e, string url)
    {
        var q = url.IndexOfAny(['?', '#']);
        if (q < 0)
        {
            return e;
        }

        return e.ToString().Contains(url.Substring(q + 1)) ? null : e;
    }

    /// <summary>Removes the query string and fragment from a URL, for error messages.</summary>
    internal static string StripQuery(string url)
    {
        var q = url.IndexOfAny(['?', '#']);
        return q < 0 ? url : url.Substring(0, q);
    }
}
