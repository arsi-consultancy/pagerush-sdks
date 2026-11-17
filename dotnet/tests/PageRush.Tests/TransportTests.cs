using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PageRush.Tests;

public sealed class RetryTests
{
    [Fact]
    public async Task RetriesOn503UsingRetryAfter()
    {
        var t = TestClient.Sequence(
            () => Responses.Problem(503, "RENDER_UNAVAILABLE", headers: ("Retry-After", "2")),
            () => Responses.Json(200, $$"""{"template":{{Samples.TemplateJson}}}"""));
        await t.Client.Templates.GetAsync("tpl_1");
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], t.Sleeps.ToArray());
    }

    [Fact]
    public async Task RetryAfterIsCappedAt30Seconds()
    {
        var t = TestClient.Sequence(
            () => Responses.Problem(429, "RATE_LIMITED", headers: ("Retry-After", "999999999")),
            () => Responses.Problem(429, "RATE_LIMITED", headers: ("Retry-After", "Infinity")),
            () => Responses.Json(200, """{"items":[],"next_cursor":null}"""));
        await t.Client.Templates.ListAsync();
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], t.Sleeps.ToArray());
    }

    [Fact]
    public async Task UnparsableRetryAfterFallsBackToBackoff()
    {
        var t = TestClient.Sequence(
            () => Responses.Problem(503, "X", headers: ("Retry-After", "soon")),
            () => Responses.Problem(503, "X", headers: ("Retry-After", "")),
            () => Responses.Json(200, """{"items":[],"next_cursor":null}"""));
        await t.Client.Templates.ListAsync();
        Assert.InRange(t.Sleeps[0].TotalSeconds, 0.375, 0.625);
        Assert.InRange(t.Sleeps[1].TotalSeconds, 0.75, 1.25);
    }

    [Fact]
    public void RetryAfterParsing()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(1.5), Transport.ParseRetryAfter(" 1.5 ", now));
        Assert.Equal(TimeSpan.Zero, Transport.ParseRetryAfter("-5", now));
        Assert.Equal(TimeSpan.FromDays(1), Transport.ParseRetryAfter("1e300", now));
        Assert.Null(Transport.ParseRetryAfter("NaN", now));
        Assert.Null(Transport.ParseRetryAfter("later", now));
        Assert.Equal(TimeSpan.FromSeconds(90), Transport.ParseRetryAfter("Thu, 01 Jan 2026 00:01:30 GMT", now));
        Assert.Equal(TimeSpan.Zero, Transport.ParseRetryAfter("Wed, 31 Dec 2025 00:00:00 GMT", now));
    }

    [Fact]
    public void BackoffDoublesWithJitterAndCapsAt8Seconds()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(Transport.Backoff(1).TotalSeconds, 0.375, 0.625);
            Assert.InRange(Transport.Backoff(3).TotalSeconds, 1.5, 2.5);
            Assert.InRange(Transport.Backoff(10).TotalSeconds, 6, 10);
        }
    }

    [Fact]
    public async Task CreateIsNotRetriedOn502()
    {
        var t = TestClient.Sequence(() => Responses.Problem(502, "INTERNAL"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.CreateAsync(new CreateTemplateOptions { Name = "x", Source = "y" }));
        Assert.Equal(502, e.Status);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task CreateIsNotRetriedOnConnectionError()
    {
        var t = TestClient.Answering((_, _) => throw new HttpRequestException("connection reset"));
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Templates.CreateAsync(new CreateTemplateOptions { Name = "x", Source = "y" }));
        Assert.Equal(ErrorCodes.ConnectionError, e.Code);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task UnsafeMethodsAreRetriedOn429And503Only()
    {
        foreach (var status in new[] { 429, 503 })
        {
            var t = TestClient.Sequence(() => Responses.Problem(status, "X"), () => Responses.Json(200, $$"""{"template":{{Samples.TemplateJson}}}"""));
            await t.Client.Templates.PublishAsync("tpl_1", 1);
            Assert.Equal(2, t.Requests.Count);
        }

        foreach (var status in new[] { 500, 502, 504, 409 })
        {
            var t = TestClient.Sequence(() => Responses.Problem(status, status == 409 ? "CONFLICT" : "X"));
            await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenameAsync("tpl_1", "n"));
            Assert.Single(t.Requests);
        }
    }

    [Fact]
    public async Task GetIsRetriedOnConnectionErrorsAndGatewayErrors()
    {
        var calls = 0;
        var t = TestClient.Answering((_, i) =>
        {
            calls++;
            return i switch
            {
                0 => throw new HttpRequestException("reset"),
                1 => Responses.Problem(504, "X"),
                _ => Responses.Json(200, Samples.Job("succeeded")),
            };
        });
        var job = await t.Client.Jobs.GetAsync("job_1");
        Assert.Equal("job_1", job.Id);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task StopsAfterMaxRetries()
    {
        var t = TestClient.Sequence(() => Responses.Problem(503, "RENDER_UNAVAILABLE", "busy"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal(503, e.Status);
        Assert.Equal(3, t.Requests.Count);
        Assert.Equal(2, t.Sleeps.Count);

        var none = TestClient.Answering((_, _) => Responses.Problem(503, "X"), o => o.MaxRetries = 0);
        await Assert.ThrowsAsync<PageRushException>(() => none.Client.Jobs.GetAsync("job_1"));
        Assert.Single(none.Requests);

        var negative = TestClient.Answering((_, _) => Responses.Problem(503, "X"), o => o.MaxRetries = -3);
        await Assert.ThrowsAsync<PageRushException>(() => negative.Client.Jobs.GetAsync("job_1"));
        Assert.Single(negative.Requests);
    }

    [Fact]
    public async Task OtherClientErrorsAreNotRetried()
    {
        foreach (var status in new[] { 400, 401, 404, 409, 410, 422, 500 })
        {
            var t = TestClient.Sequence(() => Responses.Problem(status, status == 409 ? "CONFLICT" : "X"));
            await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.GetAsync("job_1"));
            Assert.Single(t.Requests);
        }
    }

    [Fact]
    public async Task RenderIsRetriedOnConflictButNotIdempotencyConflict()
    {
        var t = TestClient.Sequence(
            () => Responses.Problem(409, "CONFLICT", headers: ("Retry-After", "2")),
            () => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], t.Sleeps.ToArray());

        var t2 = TestClient.Sequence(() => Responses.Problem(409, "IDEMPOTENCY_CONFLICT"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t2.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 }));
        Assert.Equal(ErrorCodes.IdempotencyConflict, e.Code);
        Assert.Single(t2.Requests);
    }

    [Fact]
    public async Task RenderIsRetriedOnConnectionErrorsWithTheSameKey()
    {
        var t = TestClient.Answering((_, i) => i == 0 ? throw new HttpRequestException("reset") : Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal(t.Requests[0].Header("Idempotency-Key"), t.Requests[1].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task CancellingStopsARetryWait()
    {
        var handler = new FakeHandler((_, _) => Responses.Problem(503, "X", headers: ("Retry-After", "30")));
        var client = new PageRushClient(new PageRushClientOptions { ApiKey = "pk_x", BaseUrl = "https://api.test", HttpClient = new HttpClient(handler) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Jobs.GetAsync("job_1", cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the retry wait was not cancelled");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AlreadyCancelledTokenSendsNothing()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("queued")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.Jobs.GetAsync("job_1", cts.Token));
        Assert.Empty(t.Requests);
    }
}

public sealed class ErrorTests
{
    [Fact]
    public async Task ProblemJsonIsMapped()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(
            422,
            """{"type":"https://pagerush.dev/errors/compile","title":"Compile failed","status":422,"code":"TEMPLATE_COMPILE_FAILED","detail":"2 errors","errors":[{"path":"main.typ:3:5","message":"unknown variable: foo"},{"path":"main.typ:9:1","message":"expected expression"}],"request_id":"req_body"}""",
            ("X-Request-Id", "req_header"),
            ("Retry-After", "7")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 }));
        Assert.Equal(422, e.Status);
        Assert.Equal(ErrorCodes.TemplateCompileFailed, e.Code);
        Assert.Equal("Compile failed", e.Title);
        Assert.Equal("2 errors", e.Detail);
        Assert.Equal("https://pagerush.dev/errors/compile", e.Type);
        Assert.Equal("req_body", e.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(7), e.RetryAfter);
        Assert.Equal(2, e.Errors.Count);
        Assert.Equal("main.typ:3:5", e.Errors[0].Path);
        Assert.Equal("unknown variable: foo", e.Errors[0].Message);
        Assert.Equal("TEMPLATE_COMPILE_FAILED: 2 errors", e.Message);
    }

    [Fact]
    public async Task NonJsonErrorIsUnknownWithRequestIdFromHeader()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(500, "<html>oops</html>"u8.ToArray(), "text/html", ("X-Request-Id", "req_header")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal(500, e.Status);
        Assert.Equal(ErrorCodes.Unknown, e.Code);
        Assert.Equal("req_header", e.RequestId);
        Assert.Null(e.Title);
        Assert.Null(e.Detail);
        Assert.Null(e.RetryAfter);
        Assert.Empty(e.Errors);
        Assert.Equal("UNKNOWN: HTTP 500", e.Message);
    }

    [Fact]
    public async Task MessageFallsBackToTitle()
    {
        var t = TestClient.Answering((_, _) => Responses.Problem(404, "TEMPLATE_NOT_FOUND"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.GetAsync("tpl_x"));
        Assert.Equal("TEMPLATE_NOT_FOUND: Title of TEMPLATE_NOT_FOUND", e.Message);
        Assert.Null(e.RequestId);
    }

    [Fact]
    public async Task UnknownCodesComeThroughUnchanged()
    {
        var t = TestClient.Answering((_, _) => Responses.Problem(418, "SOMETHING_NEW", "a new code"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal("SOMETHING_NEW", e.Code);
    }

    [Fact]
    public async Task AttemptTimeoutIsTimeoutError()
    {
        var t = new TestClient(
            new FakeHandler(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Responses.Empty(200);
            }),
            o =>
            {
                o.Timeout = TimeSpan.FromMilliseconds(50);
                o.MaxRetries = 1;
            });
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal(ErrorCodes.Timeout, e.Code);
        Assert.Equal(0, e.Status);
        Assert.Equal(2, t.Requests.Count);
    }

    [Fact]
    public async Task TimeoutCoversReadingTheBody()
    {
        var t = new TestClient(
            new FakeHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }),
            o =>
            {
                o.Timeout = TimeSpan.FromMilliseconds(100);
                o.MaxRetries = 0;
            });
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal(ErrorCodes.Timeout, e.Code);
    }

    [Fact]
    public async Task ConnectionFailureIsConnectionError()
    {
        var t = TestClient.Answering((_, _) => throw new HttpRequestException("No such host is known."), o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.Equal(ErrorCodes.ConnectionError, e.Code);
        Assert.Equal(0, e.Status);
        Assert.IsType<HttpRequestException>(e.InnerException);
        Assert.StartsWith("CONNECTION_ERROR: Could not connect to https://api.test", e.Message);
    }

    [Fact]
    public async Task ApiKeyNeverAppearsInErrors()
    {
        var t = TestClient.Answering((r, _) => Responses.Problem(401, "INVALID_API_KEY", "bad key " + r.Header("Authorization")!.Length), o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.GetAsync("job_1"));
        Assert.DoesNotContain(Samples.ApiKey, e.ToString());

        var c = TestClient.Answering((_, _) => throw new HttpRequestException("reset"), o => o.MaxRetries = 0);
        var ce = await Assert.ThrowsAsync<PageRushConnectionException>(() => c.Client.Jobs.GetAsync("job_1"));
        Assert.DoesNotContain(Samples.ApiKey, ce.ToString());
    }

    /// <summary>A stream that never returns data until cancelled or disposed.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly TaskCompletionSource<int> _never = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _never.Task.GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _never.TrySetException(new ObjectDisposedException(nameof(StallingStream)));
            base.Dispose(disposing);
        }
    }
}

public sealed class RedirectTests
{
    private static TestClient Redirecting(string location, int status = 302) =>
        TestClient.Answering((_, i) => i == 0
            ? Responses.Empty(status, ("Location", location))
            : Responses.Json(200, """{"items":[],"next_cursor":null}"""));

    [Fact]
    public async Task SameOriginRedirectKeepsAuthorization()
    {
        var t = Redirecting("/v1/templates?moved=1");
        await t.Client.Templates.ListAsync();
        Assert.Equal("https://api.test/v1/templates?moved=1", t.Requests[1].Url.ToString());
        Assert.Equal("Bearer " + Samples.ApiKey, t.Requests[1].Header("Authorization"));
    }

    [Theory]
    [InlineData("https://other.test/v1/templates")]
    [InlineData("http://api.test/v1/templates")]
    [InlineData("https://api.test:8443/v1/templates")]
    public async Task RedirectToAnotherOriginDropsAuthorization(string location)
    {
        var t = Redirecting(location, 307);
        await t.Client.Templates.ListAsync();
        Assert.Equal(location, t.Requests[1].Url.ToString());
        Assert.Null(t.Requests[1].Header("Authorization"));
        Assert.Equal("pagerush-dotnet/" + PageRushClient.Version, t.Requests[1].Header("User-Agent"));
    }

    [Fact]
    public async Task AuthorizationStaysDroppedAfterLeavingTheOrigin()
    {
        var t = TestClient.Answering((_, i) => i switch
        {
            0 => Responses.Empty(302, ("Location", "https://other.test/a")),
            1 => Responses.Empty(302, ("Location", "https://api.test/v1/templates")),
            _ => Responses.Json(200, """{"items":[],"next_cursor":null}"""),
        });
        await t.Client.Templates.ListAsync();
        Assert.Null(t.Requests[2].Header("Authorization"));
    }

    [Fact]
    public async Task SeeOtherTurnsPostIntoGet()
    {
        var t = TestClient.Answering((_, i) => i == 0
            ? Responses.Empty(303, ("Location", "/v1/jobs/job_1"))
            : Responses.Json(202, Samples.Job("queued")));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.Equal("POST", t.Requests[0].Method);
        Assert.Equal("GET", t.Requests[1].Method);
        Assert.Null(t.Requests[1].Body);
    }

    [Fact]
    public async Task TemporaryRedirectKeepsMethodAndBody()
    {
        var t = TestClient.Answering((_, i) => i == 0
            ? Responses.Empty(307, ("Location", "/v1/templates/tpl_1/render2"))
            : Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.Equal("POST", t.Requests[1].Method);
        Assert.Equal(t.Requests[0].Body, t.Requests[1].Body);
    }

    [Fact]
    public async Task StopsAfterFiveRedirects()
    {
        var t = TestClient.Answering((_, i) => Responses.Empty(302, ("Location", "/hop" + i)));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.ListAsync());
        Assert.Equal(302, e.Status);
        Assert.Equal(Transport.MaxRedirects + 1, t.Requests.Count);
    }

    [Fact]
    public async Task RedirectToUnsupportedSchemeIsConnectionError()
    {
        var t = TestClient.Answering((_, _) => Responses.Empty(302, ("Location", "ftp://files.test/x")));
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Templates.ListAsync());
        Assert.Equal(ErrorCodes.ConnectionError, e.Code);
    }
}
