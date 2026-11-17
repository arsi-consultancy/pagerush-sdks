using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PageRush.Tests;

public sealed class JobsTests
{
    [Fact]
    public async Task GetParsesTheJob()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("succeeded", "https://files.test/job_1.pdf?sig=abc")));
        var job = await t.Client.Jobs.GetAsync("job_1");
        Assert.Equal("GET", t.Requests[0].Method);
        Assert.Equal("/v1/jobs/job_1", t.Requests[0].PathAndQuery);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.True(job.IsDone);
        Assert.Equal("tpl_1", job.TemplateId);
        Assert.Equal("https://files.test/job_1.pdf?sig=abc", job.DownloadUrl);
    }

    [Fact]
    public async Task WaitPollsUntilDone()
    {
        var t = TestClient.Answering((_, i) => Responses.Json(200, Samples.Job(i < 2 ? "rendering" : "succeeded")));
        var job = await t.Client.Jobs.WaitAsync("job_1", new WaitOptions { Interval = TimeSpan.FromMilliseconds(250) });
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(3, t.Requests.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)], t.Sleeps.ToArray());
    }

    [Fact]
    public async Task WaitUsesDefaultInterval()
    {
        var t = TestClient.Answering((_, i) => Responses.Json(200, Samples.Job(i == 0 ? "queued" : "failed")));
        var job = await t.Client.Jobs.WaitAsync("job_1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal([TimeSpan.FromSeconds(1)], t.Sleeps.ToArray());
    }

    [Fact]
    public async Task WaitTimesOut()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("rendering")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.WaitAsync("job_1", new WaitOptions { Timeout = TimeSpan.FromSeconds(3), Interval = TimeSpan.FromSeconds(1) }));
        Assert.Equal(ErrorCodes.WaitTimeout, e.Code);
        Assert.Equal(0, e.Status);
        Assert.Contains("rendering", e.Message);
        Assert.Equal(4, t.Requests.Count);
    }

    [Fact]
    public async Task WaitDefaultTimeoutIs120Seconds()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("rendering")));
        var start = t.Clock;
        await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.WaitAsync("job_1"));
        Assert.Equal(TimeSpan.FromSeconds(120), t.Clock - start);
    }

    [Fact]
    public async Task CancellingStopsThePollWait()
    {
        var handler = new FakeHandler((_, _) => Responses.Json(200, Samples.Job("rendering")));
        var client = new PageRushClient(new PageRushClientOptions { ApiKey = "pk_x", BaseUrl = "https://api.test", HttpClient = new HttpClient(handler) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Jobs.WaitAsync("job_1", new WaitOptions { Interval = TimeSpan.FromMinutes(5) }, cts.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadFetchesTheSignedUrlWithoutAuthorization()
    {
        var t = TestClient.Answering((r, _) => r.Url.Host == "files.test"
            ? Responses.Bytes(200, Samples.Pdf, "application/pdf")
            : Responses.Json(200, Samples.Job("succeeded", "https://files.test/job_1.pdf?sig=abc&exp=1")));
        var pdf = await t.Client.Jobs.DownloadAsync("job_1");
        Assert.Equal(Samples.Pdf, pdf);
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal("Bearer " + Samples.ApiKey, t.Requests[0].Header("Authorization"));
        Assert.Equal("https://files.test/job_1.pdf?sig=abc&exp=1", t.Requests[1].Url.ToString());
        Assert.Null(t.Requests[1].Header("Authorization"));
        Assert.Equal("application/pdf", t.Requests[1].Header("Accept"));
    }

    [Fact]
    public async Task DownloadResolvesARelativeUrlAgainstTheBaseUrl()
    {
        var t = TestClient.Answering((r, _) => r.Url.AbsolutePath.StartsWith("/files", StringComparison.Ordinal)
            ? Responses.Bytes(200, Samples.Pdf, "application/pdf")
            : Responses.Json(200, Samples.Job("succeeded", "/files/job_1.pdf?sig=abc")));
        await t.Client.Jobs.DownloadAsync("job_1");
        Assert.Equal("https://api.test/files/job_1.pdf?sig=abc", t.Requests[1].Url.ToString());
        Assert.Null(t.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task DownloadWithoutUrlIsFileExpired()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("failed")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.DownloadAsync("job_1"));
        Assert.Equal(ErrorCodes.FileExpired, e.Code);
        Assert.Equal(0, e.Status);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task DownloadRejectsNonHttpUrl()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Job("succeeded", "file:///etc/passwd")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Jobs.DownloadAsync("job_1"));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task DownloadErrorsHideTheSignedQuery()
    {
        var t = TestClient.Answering(
            (r, _) => r.Url.Host == "files.test"
                ? throw new HttpRequestException("failed for https://files.test/job_1.pdf?sig=SECRETSIG")
                : Responses.Json(200, Samples.Job("succeeded", "https://files.test/job_1.pdf?sig=SECRETSIG")),
            o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<PageRushConnectionException>(() => t.Client.Jobs.DownloadAsync("job_1"));
        Assert.DoesNotContain("SECRETSIG", e.ToString());
        Assert.Contains("files.test", e.Message);

        var t2 = TestClient.Answering((r, _) => r.Url.Host == "files.test"
            ? Responses.Problem(403, "FORBIDDEN", "expired signature")
            : Responses.Json(200, Samples.Job("succeeded", "https://files.test/job_1.pdf?sig=SECRETSIG")));
        var e2 = await Assert.ThrowsAsync<PageRushException>(() => t2.Client.Jobs.DownloadAsync("job_1"));
        Assert.Equal(403, e2.Status);
        Assert.DoesNotContain("SECRETSIG", e2.ToString());
    }
}

public sealed class BatchesTests
{
    [Fact]
    public async Task CreateSendsPathBodyAndIdempotencyKey()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(202, Samples.Batch("queued")));
        var batch = await t.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions
        {
            Items = [new BatchItemInput { Data = new { name = "Ada" }, Filename = "ada.pdf" }, new BatchItemInput { Data = new { name = "Grace" } }],
            Version = 3,
            Metadata = new System.Collections.Generic.Dictionary<string, string> { ["run"] = "2026-11" },
            DeleteAfterDownload = false,
            CallbackUrl = "https://example.com/hook",
        });
        var r = Assert.Single(t.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/v1/templates/tpl_1/batches", r.PathAndQuery);
        Assert.Equal(
            """{"items":[{"data":{"name":"Ada"},"filename":"ada.pdf"},{"data":{"name":"Grace"}}],"version":3,"metadata":{"run":"2026-11"},"delete_after_download":false,"callback_url":"https://example.com/hook"}""",
            r.Body!.Utf8());
        Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), r.Header("Idempotency-Key"));
        Assert.Equal("bat_1", batch.Id);
        Assert.Equal(BatchStatus.Queued, batch.Status);
        Assert.Equal("https://pagerush.dev/v1/batches/bat_1/zip", batch.ZipUrl);
        Assert.Null(batch.Items);
    }

    [Fact]
    public async Task CreateMinimalBody()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(202, Samples.Batch("queued")));
        await t.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }], IdempotencyKey = "run-1" });
        Assert.Equal("""{"items":[{"data":1}]}""", t.Requests[0].Body!.Utf8());
        Assert.Equal("run-1", t.Requests[0].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task CreateReusesTheKeyAcrossRetries()
    {
        var t = TestClient.Answering((_, i) => i switch
        {
            0 => Responses.Problem(503, "X"),
            1 => throw new HttpRequestException("reset"),
            _ => Responses.Json(202, Samples.Batch("queued")),
        });
        await t.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }] });
        Assert.Equal(3, t.Requests.Count);
        Assert.Single(t.Requests.Select(r => r.Header("Idempotency-Key")).Distinct());
    }

    [Fact]
    public async Task CreateRetryPolicy()
    {
        var t = TestClient.Sequence(() => Responses.Problem(502, "X"), () => Responses.Json(202, Samples.Batch("queued")));
        await t.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }] });
        Assert.Equal(2, t.Requests.Count);

        foreach (var code in new[] { "IDEMPOTENCY_CONFLICT", "CONFLICT" })
        {
            var t2 = TestClient.Sequence(() => Responses.Problem(409, code));
            await Assert.ThrowsAsync<PageRushException>(() => t2.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }] }));
            Assert.Single(t2.Requests);
        }

        var quota = TestClient.Sequence(() => Responses.Problem(402, "QUOTA_EXCEEDED"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => quota.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }] }));
        Assert.Equal(ErrorCodes.QuotaExceeded, e.Code);
        Assert.Single(quota.Requests);
    }

    [Fact]
    public async Task GetReturnsItems()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Batch("partial", items: true)));
        var batch = await t.Client.Batches.GetAsync("bat_1");
        Assert.Equal("/v1/batches/bat_1", t.Requests[0].PathAndQuery);
        Assert.True(batch.IsDone);
        Assert.Equal(2, batch.Items!.Count);
        Assert.Equal("job_a", batch.Items[0].JobId);
        Assert.Equal(1234, batch.Items[0].Bytes);
        Assert.Equal("RENDER_FAILED", batch.Items[1].Error!.Code);
        Assert.Equal("2026-11", batch.Metadata!["run"]);
    }

    [Fact]
    public async Task WaitPollsUntilDoneWithDefaults()
    {
        var t = TestClient.Answering((_, i) => Responses.Json(200, Samples.Batch(i == 0 ? "rendering" : "partial", items: true)));
        var batch = await t.Client.Batches.WaitAsync("bat_1");
        Assert.Equal(BatchStatus.Partial, batch.Status);
        Assert.Equal([TimeSpan.FromSeconds(2)], t.Sleeps.ToArray());
    }

    [Fact]
    public async Task WaitTimesOut()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.Batch("rendering")));
        var start = t.Clock;
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Batches.WaitAsync("bat_1"));
        Assert.Equal(ErrorCodes.WaitTimeout, e.Code);
        Assert.Equal(0, e.Status);
        Assert.Equal(TimeSpan.FromSeconds(600), t.Clock - start);
    }

    [Fact]
    public async Task DownloadZipSendsAuthorization()
    {
        var zip = new byte[] { 0x50, 0x4b, 0x03, 0x04 };
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, zip, "application/zip"));
        var bytes = await t.Client.Batches.DownloadZipAsync("bat_1");
        Assert.Equal(zip, bytes);
        var r = t.Requests[0];
        Assert.Equal("/v1/batches/bat_1/zip", r.PathAndQuery);
        Assert.Equal("Bearer " + Samples.ApiKey, r.Header("Authorization"));
        Assert.Equal("application/zip", r.Header("Accept"));
    }

    [Fact]
    public async Task DownloadZipRejectsNonZip()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, "<html/>"u8.ToArray(), "text/html"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Batches.DownloadZipAsync("bat_1"));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(200, e.Status);
    }

    [Fact]
    public async Task DownloadZipErrors()
    {
        var t = TestClient.Sequence(() => Responses.Problem(409, "CONFLICT", "still rendering", ("Retry-After", "5")));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Batches.DownloadZipAsync("bat_1"));
        Assert.Equal(ErrorCodes.Conflict, e.Code);
        Assert.Equal(TimeSpan.FromSeconds(5), e.RetryAfter);
        Assert.Single(t.Requests);

        var gone = TestClient.Sequence(() => Responses.Problem(410, "FILE_EXPIRED"));
        e = await Assert.ThrowsAsync<PageRushException>(() => gone.Client.Batches.DownloadZipAsync("bat_1"));
        Assert.Equal(410, e.Status);
        Assert.Equal(ErrorCodes.FileExpired, e.Code);
    }
}
