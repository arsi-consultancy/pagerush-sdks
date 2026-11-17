using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PageRush.Tests;

public sealed class WebhookTests
{
    private const string Secret = "whsec_test";
    private static readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1767225600);

    public static IEnumerable<object[]> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "webhooks.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [c.GetProperty("name").GetString()!, c.GetRawText()];
        }
    }

    [Fact]
    public void FixtureHasCases() => Assert.True(Vectors().Count() >= 20);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedVectors(string name, string json)
    {
        var c = JsonDocument.Parse(json).RootElement;
        var body = Convert.FromBase64String(c.GetProperty("body_base64").GetString()!);
        var headers = c.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var secrets = c.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!).ToArray();
        var options = new WebhookVerifyOptions
        {
            Now = DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("now").GetInt64()),
            Tolerance = TimeSpan.FromSeconds(c.GetProperty("tolerance_seconds").GetInt64()),
        };

        if (!c.GetProperty("valid").GetBoolean())
        {
            var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, headers, secrets, options));
            Assert.Equal(c.GetProperty("reason").GetString(), e.Reason);
            return;
        }

        Webhooks.Verify(body, headers, secrets, options);
        var ev = Webhooks.ConstructEvent(body, headers, secrets, options);
        Assert.Equal(headers["X-PageRush-Event"], ev.Event);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(long.Parse(headers["X-PageRush-Signature"].Split(',').Select(p => p.Trim()).First(p => p.StartsWith("t=", StringComparison.Ordinal)).Substring(2))), ev.Timestamp);
        if (ev.Event == WebhookEvents.BatchCompleted)
        {
            Assert.Null(ev.JobId);
            Assert.Equal(headers["X-PageRush-Batch-Id"], ev.BatchId);
            Assert.NotNull(ev.Batch);
        }
        else
        {
            Assert.Equal(headers["X-PageRush-Job-Id"], ev.JobId);
            Assert.Null(ev.BatchId);
        }

        if (ev.Event == WebhookEvents.PdfSucceeded)
        {
            Assert.Equal(body, ev.Pdf);
        }

        // The same case through other header containers, with lower-case names.
        var lower = headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => new[] { h.Value });
        Webhooks.Verify(body, lower, secrets, options);
        var collection = new NameValueCollection();
        foreach (var h in headers)
        {
            collection.Add(h.Key.ToUpperInvariant(), h.Value);
        }

        Webhooks.Verify(body, collection, secrets, options);
        _ = name;
    }

    [Fact]
    public void AcceptsHttpHeaders()
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        using var message = new HttpRequestMessage();
        foreach (var h in Sign(body, "pdf.succeeded", "job_1", null))
        {
            message.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        var ev = Webhooks.ConstructEvent(body, message.Headers, Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Equal("job_1", ev.JobId);
    }

    [Fact]
    public void MissingSecretsAreArgumentErrors()
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        var headers = Sign(body, "pdf.succeeded", "job_1", null);
        Assert.Throws<ArgumentException>(() => Webhooks.Verify(body, headers, ""));
        Assert.Throws<ArgumentException>(() => Webhooks.Verify(body, headers, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => Webhooks.Verify(body, headers, ["ok", ""]));
        Assert.Throws<ArgumentNullException>(() => Webhooks.Verify(body, headers, (IEnumerable<string>)null!));
        Assert.Throws<ArgumentException>(() => Webhooks.ConstructEvent(body, headers, (string)null!));
    }

    [Fact]
    public void FailureMessagesDoNotContainTheSecret()
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        var headers = Sign(body, "pdf.succeeded", "job_1", null);
        var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, headers, "whsec_other_secret", new WebhookVerifyOptions { Now = _now }));
        Assert.Equal(WebhookReasons.Signature, e.Reason);
        Assert.DoesNotContain("whsec_other_secret", e.ToString());
    }

    [Fact]
    public void MissingEventOrIdHeaderIsHeaderReason()
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        var headers = Sign(body, "pdf.succeeded", "job_1", null);
        var noEvent = headers.Where(h => h.Key != "X-PageRush-Event").ToDictionary(h => h.Key, h => h.Value);
        var noId = headers.Where(h => h.Key != "X-PageRush-Job-Id").ToDictionary(h => h.Key, h => h.Value);
        Assert.Equal("header", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, noEvent, Secret, new WebhookVerifyOptions { Now = _now })).Reason);
        Assert.Equal("header", Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, noId, Secret, new WebhookVerifyOptions { Now = _now })).Reason);
    }

    [Fact]
    public void DefaultToleranceIs300Seconds()
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        var headers = Sign(body, "pdf.succeeded", "job_1", null);
        Webhooks.Verify(body, headers, Secret, new WebhookVerifyOptions { Now = _now.AddSeconds(300) });
        var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, headers, Secret, new WebhookVerifyOptions { Now = _now.AddSeconds(301) }));
        Assert.Equal(WebhookReasons.Timestamp, e.Reason);

        // Without Now, the current time is used: a 2026-01-01 signature is far too old.
        Assert.Equal(WebhookReasons.Timestamp, Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, headers, Secret)).Reason);
    }

    [Fact]
    public void ConstructEventSucceeded()
    {
        var body = Encoding.ASCII.GetBytes("%PDF-1.7 body");
        var headers = Sign(body, "pdf.succeeded", "job_1", """{"order":"42","amount":"₹1,250"}""");
        var ev = Webhooks.ConstructEvent(body, headers, Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Equal(WebhookEvents.PdfSucceeded, ev.Event);
        Assert.Equal("job_1", ev.JobId);
        Assert.Equal(body, ev.Pdf);
        Assert.Null(ev.Failure);
        Assert.Null(ev.Batch);
        Assert.Equal("42", ev.Metadata!["order"]);
        Assert.Equal("₹1,250", ev.Metadata["amount"]);
        Assert.Equal(_now, ev.Timestamp);
    }

    [Theory]
    [InlineData("""{"order":42}""")]
    [InlineData("""["a"]""")]
    [InlineData("not json")]
    [InlineData("null")]
    public void MetadataThatIsNotAnObjectOfStringsIsNull(string metadata)
    {
        var body = Encoding.ASCII.GetBytes("%PDF");
        var ev = Webhooks.ConstructEvent(body, Sign(body, "pdf.succeeded", "job_1", metadata), Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Null(ev.Metadata);
    }

    [Fact]
    public void ConstructEventFailed()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"pdf.failed","job_id":"job_1","template_id":"tpl_1","error":{"code":"TEMPLATE_COMPILE_FAILED","message":"unknown variable: foo"},"metadata":{"order":"42"},"created_at":"2026-01-01T00:00:00.000Z","extra":true}""");
        var ev = Webhooks.ConstructEvent(body, Sign(body, "pdf.failed", "job_1", null), Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Null(ev.Pdf);
        Assert.Equal("TEMPLATE_COMPILE_FAILED", ev.Failure!.Error!.Code);
        Assert.Equal("unknown variable: foo", ev.Failure.Error.Message);
        Assert.Equal("tpl_1", ev.Failure.TemplateId);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), ev.Failure.CreatedAt);
        Assert.True(ev.Failure.Json.GetProperty("extra").GetBoolean());
        Assert.Equal("42", ev.Metadata!["order"]);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("not json")]
    public void FailedBodyThatIsNotAnObjectIsHeaderReason(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(body, Sign(body, "pdf.failed", "job_1", null), Secret, new WebhookVerifyOptions { Now = _now }));
        Assert.Equal(WebhookReasons.Header, e.Reason);
    }

    [Fact]
    public void UnknownEventHasNoPayload()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var ev = Webhooks.ConstructEvent(body, Sign(body, "pdf.something", "job_1", """{"a":"b"}"""), Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Equal("pdf.something", ev.Event);
        Assert.Equal("job_1", ev.JobId);
        Assert.Null(ev.Pdf);
        Assert.Null(ev.Failure);
        Assert.Null(ev.Metadata);
    }

    [Fact]
    public void ConstructEventBatchCompleted()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"batch.completed\",\"batch\":" + Samples.Batch("partial") + "}");
        var headers = Sign(body, "batch.completed", "bat_1", """{"run":"2026-11"}""", idHeader: "X-PageRush-Batch-Id");
        var ev = Webhooks.ConstructEvent(body, headers, Secret, new WebhookVerifyOptions { Now = _now });
        Assert.Equal(WebhookEvents.BatchCompleted, ev.Event);
        Assert.Null(ev.JobId);
        Assert.Equal("bat_1", ev.BatchId);
        Assert.Equal("bat_1", ev.Batch!.Id);
        Assert.Equal(BatchStatus.Partial, ev.Batch.Status);
        Assert.Equal(1, ev.Batch.Succeeded);
        Assert.Equal("2026-11", ev.Metadata!["run"]);
        Assert.Null(ev.Pdf);
    }

    [Fact]
    public void BatchCompletedNeedsTheBatchIdHeader()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"batch.completed\",\"batch\":" + Samples.Batch("succeeded") + "}");
        var headers = Sign(body, "batch.completed", "bat_1", null, idHeader: "X-PageRush-Job-Id");
        var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.Verify(body, headers, Secret, new WebhookVerifyOptions { Now = _now }));
        Assert.Equal(WebhookReasons.Header, e.Reason);
    }

    [Theory]
    [InlineData("""{"event":"batch.completed"}""")]
    [InlineData("""{"batch":[1]}""")]
    [InlineData("""{"batch":{"id":"bat_1","item_count":"many"}}""")]
    [InlineData("[]")]
    [InlineData("nope")]
    public void BatchCompletedWithBadBodyIsHeaderReason(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var headers = Sign(body, "batch.completed", "bat_1", null, idHeader: "X-PageRush-Batch-Id");
        var e = Assert.Throws<WebhookVerificationException>(() => Webhooks.ConstructEvent(body, headers, Secret, new WebhookVerifyOptions { Now = _now }));
        Assert.Equal(WebhookReasons.Header, e.Reason);
    }

    [Fact]
    public void FixedTimeEqualsComparesExactly()
    {
        Assert.True(Webhooks.FixedTimeEquals("abc", "abc"));
        Assert.False(Webhooks.FixedTimeEquals("abc", "abd"));
        Assert.False(Webhooks.FixedTimeEquals("abc", "ABC"));
        Assert.False(Webhooks.FixedTimeEquals("abc", "abcd"));
    }

    private static Dictionary<string, string> Sign(byte[] body, string eventName, string id, string? metadata, string idHeader = "X-PageRush-Job-Id")
    {
        const long t = 1767225600;
        var prefix = Encoding.UTF8.GetBytes($"{t}\n{eventName}\n{id}\n{metadata ?? ""}\n");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var hex = BitConverter.ToString(hmac.ComputeHash(prefix.Concat(body).ToArray())).Replace("-", "").ToLowerInvariant();
        var headers = new Dictionary<string, string>
        {
            ["X-PageRush-Signature"] = $"t={t},v2={hex}",
            ["X-PageRush-Event"] = eventName,
            [idHeader] = id,
        };
        if (metadata != null)
        {
            headers["X-PageRush-Metadata"] = metadata;
        }

        return headers;
    }
}
