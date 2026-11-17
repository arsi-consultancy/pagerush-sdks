using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PageRush;

/// <summary>Header names of <c>callback_url</c> deliveries.</summary>
public static class WebhookHeaders
{
    /// <summary>The event: <c>pdf.succeeded</c>, <c>pdf.failed</c> or <c>batch.completed</c>.</summary>
    public const string Event = "X-PageRush-Event";
    /// <summary>The job id, the same on every retry of a delivery.</summary>
    public const string JobId = "X-PageRush-Job-Id";
    /// <summary>The batch id of a <c>batch.completed</c> notice, sent instead of <see cref="JobId"/>.</summary>
    public const string BatchId = "X-PageRush-Batch-Id";
    /// <summary>Informational send time; the signed timestamp is the <c>t</c> in <see cref="Signature"/>.</summary>
    public const string Timestamp = "X-PageRush-Timestamp";
    /// <summary>The signature: <c>t=&lt;unix seconds&gt;,v2=&lt;hex&gt;[,v2=&lt;hex&gt;]</c>.</summary>
    public const string Signature = "X-PageRush-Signature";
    /// <summary>The job's or batch's metadata as a JSON object of strings, when it has metadata.</summary>
    public const string Metadata = "X-PageRush-Metadata";
}

/// <summary>Values of <see cref="WebhookEvent.Event"/>.</summary>
public static class WebhookEvents
{
    /// <summary>A render succeeded; the body is the PDF.</summary>
    public const string PdfSucceeded = "pdf.succeeded";
    /// <summary>A render failed; the body is JSON.</summary>
    public const string PdfFailed = "pdf.failed";
    /// <summary>A batch finished; the body is JSON with the batch (without items), not the PDFs.</summary>
    public const string BatchCompleted = "batch.completed";
}

/// <summary>Values of <see cref="WebhookVerificationException.Reason"/>.</summary>
public static class WebhookReasons
{
    /// <summary>A required header is missing or malformed, or the body is not the expected JSON.</summary>
    public const string Header = "header";
    /// <summary>The signed timestamp is outside the tolerance.</summary>
    public const string Timestamp = "timestamp";
    /// <summary>No v2 signature matched any secret.</summary>
    public const string Signature = "signature";
}

/// <summary>Thrown when a webhook delivery does not verify. Answer 400 and don't trust the request.</summary>
public sealed class WebhookVerificationException : Exception
{
    /// <summary>Creates a verification error.</summary>
    /// <param name="reason">One of the <see cref="WebhookReasons"/> values.</param>
    /// <param name="message">What failed. Never includes a secret.</param>
    public WebhookVerificationException(string reason, string message)
        : base($"Webhook verification failed ({reason}): {message}")
    {
        Reason = reason;
    }

    /// <summary><c>header</c>, <c>timestamp</c> or <c>signature</c>; see <see cref="WebhookReasons"/>.</summary>
    public string Reason { get; }
}

/// <summary>Options for <see cref="Webhooks.Verify(byte[], IEnumerable{KeyValuePair{string, string}}, string, WebhookVerifyOptions?)"/> and <c>ConstructEvent</c>.</summary>
public sealed class WebhookVerifyOptions
{
    /// <summary>How far the signed timestamp may be from now (default 300 s).</summary>
    public TimeSpan Tolerance { get; set; } = Webhooks.DefaultTolerance;

    /// <summary>The time to check the timestamp against, instead of the current time. Useful in tests.</summary>
    public DateTimeOffset? Now { get; set; }
}

/// <summary>A verified <c>callback_url</c> delivery. Every field comes from signed data.</summary>
public sealed class WebhookEvent
{
    /// <summary>The event: see <see cref="WebhookEvents"/>. Other values may appear; for them <see cref="Pdf"/>, <see cref="Failure"/> and <see cref="Batch"/> are null.</summary>
    public string Event { get; init; } = "";

    /// <summary>The job id, or null for <c>batch.completed</c>. Delivery is at-least-once: dedupe on it.</summary>
    public string? JobId { get; init; }

    /// <summary>The batch id (<c>batch.completed</c> only). Dedupe on it.</summary>
    public string? BatchId { get; init; }

    /// <summary>The finished batch without items (<c>batch.completed</c> only).</summary>
    public Batch? Batch { get; init; }

    /// <summary>The signed timestamp (<c>t</c>).</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The PDF (<c>pdf.succeeded</c> only).</summary>
    public byte[]? Pdf { get; init; }

    /// <summary>The parsed body (<c>pdf.failed</c> only).</summary>
    public WebhookFailure? Failure { get; init; }

    /// <summary>
    /// The metadata: from the <c>X-PageRush-Metadata</c> header on <c>pdf.succeeded</c> and <c>batch.completed</c>,
    /// from the body on <c>pdf.failed</c>. Null if absent or not a JSON object of strings.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>The JSON body of a <c>pdf.failed</c> delivery.</summary>
public sealed class WebhookFailure
{
    /// <summary>The body's <c>event</c>.</summary>
    public string? Event { get; init; }

    /// <summary>The body's <c>job_id</c>.</summary>
    public string? JobId { get; init; }

    /// <summary>The body's <c>template_id</c>, or null.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Why the job failed.</summary>
    public JobError? Error { get; init; }

    /// <summary>The job's metadata, or null if it has none or it is not an object of strings.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>The body's <c>created_at</c>, or null.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The whole body as parsed JSON, including any fields not listed above.</summary>
    public JsonElement Json { get; init; }
}

/// <summary>
/// Verifies and parses <c>callback_url</c> deliveries. The signature in <c>X-PageRush-Signature</c> covers the
/// timestamp, the event, job id (batch id for <c>batch.completed</c>) and metadata headers, and the raw body.
/// These methods need no API key or client.
/// </summary>
/// <remarks>
/// Pass the exact bytes received. Don't parse and re-serialise JSON first: the bytes change and verification fails.
/// Headers can be a dictionary of strings, any <c>IEnumerable&lt;KeyValuePair&lt;string, TValues&gt;&gt;</c> whose values
/// are string sequences (ASP.NET Core's <c>Request.Headers</c>, <see cref="System.Net.Http.Headers.HttpHeaders"/>), or a
/// <see cref="NameValueCollection"/>. Names are matched case-insensitively. During a secret rotation, pass both secrets.
/// A null or empty secret list, or an empty secret, throws <see cref="ArgumentException"/>: it is a setup mistake,
/// not a verification failure.
/// </remarks>
public static class Webhooks
{
    /// <summary>The default tolerance for the signed timestamp: 300 seconds.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

    /// <summary>Checks that a delivery was signed by PageRush with <paramref name="secret"/>.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify(byte[] body, IEnumerable<KeyValuePair<string, string>> headers, string secret, WebhookVerifyOptions? options = null) =>
        VerifyCore(body, StringLookup(headers), [secret], options);

    /// <summary>Checks that a delivery was signed by PageRush with any of <paramref name="secrets"/>.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secrets">Every secret you accept (during a rotation, the new and the old one).</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify(byte[] body, IEnumerable<KeyValuePair<string, string>> headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null) =>
        VerifyCore(body, StringLookup(headers), secrets, options);

    /// <summary>Checks that a delivery was signed by PageRush with <paramref name="secret"/>.</summary>
    /// <typeparam name="TValues">The header value type, such as <c>StringValues</c> or <c>IEnumerable&lt;string&gt;</c>.</typeparam>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as ASP.NET Core's <c>Request.Headers</c>.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify<TValues>(byte[] body, IEnumerable<KeyValuePair<string, TValues>> headers, string secret, WebhookVerifyOptions? options = null)
        where TValues : IEnumerable<string?> =>
        VerifyCore(body, MultiLookup(headers), [secret], options);

    /// <summary>Checks that a delivery was signed by PageRush with any of <paramref name="secrets"/>.</summary>
    /// <typeparam name="TValues">The header value type, such as <c>StringValues</c> or <c>IEnumerable&lt;string&gt;</c>.</typeparam>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as ASP.NET Core's <c>Request.Headers</c>.</param>
    /// <param name="secrets">Every secret you accept.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify<TValues>(byte[] body, IEnumerable<KeyValuePair<string, TValues>> headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null)
        where TValues : IEnumerable<string?> =>
        VerifyCore(body, MultiLookup(headers), secrets, options);

    /// <summary>Checks that a delivery was signed by PageRush with <paramref name="secret"/>.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as <c>HttpListenerRequest.Headers</c>.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify(byte[] body, NameValueCollection headers, string secret, WebhookVerifyOptions? options = null) =>
        VerifyCore(body, CollectionLookup(headers), [secret], options);

    /// <summary>Checks that a delivery was signed by PageRush with any of <paramref name="secrets"/>.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as <c>HttpListenerRequest.Headers</c>.</param>
    /// <param name="secrets">Every secret you accept.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <exception cref="WebhookVerificationException">The delivery does not verify.</exception>
    public static void Verify(byte[] body, NameValueCollection headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null) =>
        VerifyCore(body, CollectionLookup(headers), secrets, options);

    /// <summary>Verifies a delivery, as <c>Verify</c> does, and parses it.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent(byte[] body, IEnumerable<KeyValuePair<string, string>> headers, string secret, WebhookVerifyOptions? options = null) =>
        ConstructCore(body, StringLookup(headers), [secret], options);

    /// <summary>Verifies a delivery against any of <paramref name="secrets"/> and parses it.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secrets">Every secret you accept.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent(byte[] body, IEnumerable<KeyValuePair<string, string>> headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null) =>
        ConstructCore(body, StringLookup(headers), secrets, options);

    /// <summary>Verifies a delivery, as <c>Verify</c> does, and parses it.</summary>
    /// <typeparam name="TValues">The header value type, such as <c>StringValues</c> or <c>IEnumerable&lt;string&gt;</c>.</typeparam>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as ASP.NET Core's <c>Request.Headers</c>.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent<TValues>(byte[] body, IEnumerable<KeyValuePair<string, TValues>> headers, string secret, WebhookVerifyOptions? options = null)
        where TValues : IEnumerable<string?> =>
        ConstructCore(body, MultiLookup(headers), [secret], options);

    /// <summary>Verifies a delivery against any of <paramref name="secrets"/> and parses it.</summary>
    /// <typeparam name="TValues">The header value type, such as <c>StringValues</c> or <c>IEnumerable&lt;string&gt;</c>.</typeparam>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as ASP.NET Core's <c>Request.Headers</c>.</param>
    /// <param name="secrets">Every secret you accept.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent<TValues>(byte[] body, IEnumerable<KeyValuePair<string, TValues>> headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null)
        where TValues : IEnumerable<string?> =>
        ConstructCore(body, MultiLookup(headers), secrets, options);

    /// <summary>Verifies a delivery, as <c>Verify</c> does, and parses it.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as <c>HttpListenerRequest.Headers</c>.</param>
    /// <param name="secret">Your project's webhook secret.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent(byte[] body, NameValueCollection headers, string secret, WebhookVerifyOptions? options = null) =>
        ConstructCore(body, CollectionLookup(headers), [secret], options);

    /// <summary>Verifies a delivery against any of <paramref name="secrets"/> and parses it.</summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="headers">The request headers, such as <c>HttpListenerRequest.Headers</c>.</param>
    /// <param name="secrets">Every secret you accept.</param>
    /// <param name="options">Tolerance and clock, or null.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="WebhookVerificationException">The delivery does not verify, or its body is not the JSON its event needs (reason <c>header</c>).</exception>
    public static WebhookEvent ConstructEvent(byte[] body, NameValueCollection headers, IEnumerable<string> secrets, WebhookVerifyOptions? options = null) =>
        ConstructCore(body, CollectionLookup(headers), secrets, options);

    private readonly struct Signed(string eventName, string id, string? metadata, long timestamp)
    {
        public string EventName { get; } = eventName;

        public string Id { get; } = id;

        public string? Metadata { get; } = metadata;

        public long Timestamp { get; } = timestamp;
    }

    private static void VerifyCore(byte[] body, Func<string, string?> header, IEnumerable<string> secrets, WebhookVerifyOptions? options) =>
        _ = Check(body, header, secrets, options);

    private static Signed Check(byte[] body, Func<string, string?> header, IEnumerable<string> secrets, WebhookVerifyOptions? options)
    {
        Guard.NotNull(body, nameof(body));

        var secretList = CheckSecrets(secrets);

        string? rawTs = null;
        long ts = 0;
        var signatures = new List<string>();
        foreach (var part in (header(WebhookHeaders.Signature) ?? "").Split(','))
        {
            var pair = part.Trim();
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = pair.Substring(0, eq);
            var value = pair.Substring(eq + 1);
            if (key == "t")
            {
                if (rawTs != null || !IsTimestamp(value))
                {
                    throw HeaderError("need exactly one t of 1 to 15 digits");
                }

                rawTs = value;
                ts = long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (key == "v2")
            {
                signatures.Add(value);
            }
        }

        if (rawTs is null || signatures.Count == 0)
        {
            throw HeaderError($"need t=<unix seconds> and at least one v2=<hex> in {WebhookHeaders.Signature}");
        }

        var eventName = header(WebhookHeaders.Event) ?? throw HeaderError("missing " + WebhookHeaders.Event);
        var idHeader = eventName == WebhookEvents.BatchCompleted ? WebhookHeaders.BatchId : WebhookHeaders.JobId;
        var id = header(idHeader) ?? throw HeaderError("missing " + idHeader);
        var metadata = header(WebhookHeaders.Metadata);

        var now = (options?.Now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var tolerance = options?.Tolerance ?? DefaultTolerance;
        var age = Math.Abs((double)now - ts);
        if (age > tolerance.TotalSeconds)
        {
            throw new WebhookVerificationException(
                WebhookReasons.Timestamp,
                $"timestamp is {age.ToString("0", CultureInfo.InvariantCulture)} s from now, tolerance is {tolerance.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }

        // The signed string uses the timestamp exactly as it appears in the header.
        var prefix = Encoding.UTF8.GetBytes(rawTs + "\n" + eventName + "\n" + id + "\n" + (metadata ?? "") + "\n");
        var signed = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, signed, prefix.Length, body.Length);

        var matched = false;
        foreach (var secret in secretList)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = ToLowerHex(hmac.ComputeHash(signed));
            foreach (var sig in signatures)
            {
                // Check every pair so the time taken doesn't depend on which one matched.
                matched |= FixedTimeEquals(expected, sig);
            }
        }

        if (!matched)
        {
            throw new WebhookVerificationException(WebhookReasons.Signature, "no v2 signature matches");
        }

        return new Signed(eventName, id, metadata, ts);
    }

    private static WebhookEvent ConstructCore(byte[] body, Func<string, string?> header, IEnumerable<string> secrets, WebhookVerifyOptions? options)
    {
        var signed = Check(body, header, secrets, options);
        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(signed.Timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new WebhookVerificationException(WebhookReasons.Timestamp, "timestamp is out of range");
        }

        switch (signed.EventName)
        {
            case WebhookEvents.PdfSucceeded:
                return new WebhookEvent
                {
                    Event = signed.EventName,
                    JobId = signed.Id,
                    Timestamp = timestamp,
                    Pdf = body,
                    Metadata = DecodeMetadata(signed.Metadata),
                };

            case WebhookEvents.PdfFailed:
                {
                    var root = ParseObject(body) ?? throw HeaderError("pdf.failed body is not a JSON object");
                    var failure = new WebhookFailure
                    {
                        Event = Transport.StringProp(root, "event"),
                        JobId = Transport.StringProp(root, "job_id"),
                        TemplateId = Transport.StringProp(root, "template_id"),
                        Error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                            ? new JobError { Code = Transport.StringProp(err, "code"), Message = Transport.StringProp(err, "message") }
                            : null,
                        Metadata = root.TryGetProperty("metadata", out var md) ? StringMap(md) : null,
                        CreatedAt = DateTimeOffset.TryParse(Transport.StringProp(root, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : null,
                        Json = root,
                    };
                    return new WebhookEvent
                    {
                        Event = signed.EventName,
                        JobId = signed.Id,
                        Timestamp = timestamp,
                        Failure = failure,
                        Metadata = failure.Metadata,
                    };
                }

            case WebhookEvents.BatchCompleted:
                {
                    var root = ParseObject(body);
                    if (root is null || !root.Value.TryGetProperty("batch", out var batchJson) || batchJson.ValueKind != JsonValueKind.Object)
                    {
                        throw HeaderError("batch.completed body is not a JSON object with a batch object");
                    }

                    Batch? batch;
                    try
                    {
                        batch = batchJson.Deserialize<Batch>(Transport.JsonOptions);
                    }
                    catch (JsonException)
                    {
                        throw HeaderError("batch.completed body has a malformed batch object");
                    }

                    return new WebhookEvent
                    {
                        Event = signed.EventName,
                        BatchId = signed.Id,
                        Batch = batch,
                        Timestamp = timestamp,
                        Metadata = DecodeMetadata(signed.Metadata),
                    };
                }

            default:
                return new WebhookEvent { Event = signed.EventName, JobId = signed.Id, Timestamp = timestamp };
        }
    }

    private static List<string> CheckSecrets(IEnumerable<string> secrets)
    {
        const string hint = "Pass your project's webhook secret (is PAGERUSH_WEBHOOK_SECRET set?).";
        Guard.NotNull(secrets, nameof(secrets));

        var list = secrets.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("No webhook secret. " + hint, nameof(secrets));
        }

        if (list.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("A webhook secret is null or empty. " + hint, nameof(secrets));
        }

        return list;
    }

    private static bool IsTimestamp(string value)
    {
        if (value.Length is < 1 or > 15)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static WebhookVerificationException HeaderError(string message) => new(WebhookReasons.Header, message);

    private static string ToLowerHex(byte[] bytes)
    {
        const string digits = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = digits[bytes[i] >> 4];
            chars[(i * 2) + 1] = digits[bytes[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>Compares two strings in time that depends only on their length.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    private static JsonElement? ParseObject(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? DecodeMetadata(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw!);
            return StringMap(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string>? StringMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            map[p.Name] = p.Value.GetString()!;
        }

        return map;
    }

    private static Func<string, string?> StringLookup(IEnumerable<KeyValuePair<string, string>> headers)
    {
        Guard.NotNull(headers, nameof(headers));

        return name =>
        {
            foreach (var h in headers)
            {
                if (h.Value != null && string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return h.Value;
                }
            }

            return null;
        };
    }

    private static Func<string, string?> MultiLookup<TValues>(IEnumerable<KeyValuePair<string, TValues>> headers)
        where TValues : IEnumerable<string?>
    {
        Guard.NotNull(headers, nameof(headers));

        return name =>
        {
            foreach (var h in headers)
            {
                if (h.Value != null && string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var v in h.Value)
                    {
                        if (v != null)
                        {
                            return v;
                        }
                    }
                }
            }

            return null;
        };
    }

    private static Func<string, string?> CollectionLookup(NameValueCollection headers)
    {
        Guard.NotNull(headers, nameof(headers));

        return name =>
        {
            foreach (var key in headers.AllKeys)
            {
                if (key != null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return headers.Get(key);
                }
            }

            return null;
        };
    }
}
