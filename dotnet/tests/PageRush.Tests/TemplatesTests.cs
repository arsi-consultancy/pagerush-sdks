using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace PageRush.Tests;

public sealed class TemplatesTests
{
    private static readonly string _created = $$"""{"template":{{Samples.TemplateJson}},"version":{{Samples.VersionJson}}}""";

    [Fact]
    public async Task ListSendsLimitAndCursor()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, $$"""{"items":[{{Samples.TemplateJson}}],"next_cursor":"c2"}"""));
        var page = await t.Client.Templates.ListAsync(new ListTemplatesOptions { Limit = 20, Cursor = "a b/c" });
        Assert.Equal("GET", t.Requests[0].Method);
        Assert.Equal("/v1/templates?limit=20&cursor=a%20b%2Fc", t.Requests[0].PathAndQuery);
        Assert.Equal("tpl_1", Assert.Single(page.Items).Id);
        Assert.Equal(1, page.Items[0].LiveVersion);
        Assert.Equal("c2", page.NextCursor);
    }

    [Fact]
    public async Task ListAllFollowsCursorAcrossPages()
    {
        var t = TestClient.Answering((r, i) => i == 0
            ? Responses.Json(200, """{"items":[{"id":"tpl_1","name":"a","live_version":null,"latest_version":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"},{"id":"tpl_2","name":"b","live_version":1,"latest_version":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],"next_cursor":"next"}""")
            : Responses.Json(200, """{"items":[{"id":"tpl_3","name":"c","live_version":1,"latest_version":2,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z"}],"next_cursor":null}"""));
        var all = await t.Client.Templates.ListAllAsync(new ListTemplatesOptions { Limit = 2 }).ToListAsync();
        Assert.Equal(["tpl_1", "tpl_2", "tpl_3"], all.Select(x => x.Id).ToArray());
        Assert.Null(all[0].LiveVersion);
        Assert.Equal(["/v1/templates?limit=2", "/v1/templates?limit=2&cursor=next"], t.Requests.Select(r => r.PathAndQuery).ToArray());
    }

    [Fact]
    public async Task ListAllStopsOnError()
    {
        var t = TestClient.Answering((_, i) => i == 0
            ? Responses.Json(200, $$"""{"items":[{{Samples.TemplateJson}}],"next_cursor":"next"}""")
            : Responses.Problem(404, "NOT_FOUND"));
        var seen = new List<string>();
        var e = await Assert.ThrowsAsync<PageRushException>(async () =>
        {
            await foreach (var x in t.Client.Templates.ListAllAsync())
            {
                seen.Add(x.Id);
            }
        });
        Assert.Equal("NOT_FOUND", e.Code);
        Assert.Equal(["tpl_1"], seen.ToArray());
    }

    [Fact]
    public async Task CreateSendsSnakeCaseBodyAndOmitsNulls()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(201, _created));
        var result = await t.Client.Templates.CreateAsync(new CreateTemplateOptions
        {
            Name = "Invoice",
            Source = "#let data = json(\"data.json\")\nHello é",
            Files = [TemplateFile.FromString("parts/h.typ", "#let h = 1"), TemplateFile.FromBytes("logo.png", [1, 2, 3])],
            SampleData = new Dictionary<string, object?> { ["name"] = "Ada", ["none"] = null },
            TypstVersion = "0.13.1",
        });
        var r = Assert.Single(t.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/v1/templates", r.PathAndQuery);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var body = r.Json();
        Assert.Equal(["name", "source", "files", "sample_data", "typst_version"], body.Keys());
        Assert.Equal("#let data = json(\"data.json\")\nHello é", body.GetProperty("source").GetString());
        Assert.Equal("""[{"path":"parts/h.typ","content":"#let h = 1","encoding":"utf8"},{"path":"logo.png","content":"AQID","encoding":"base64"}]""", body.GetProperty("files").GetRawText());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sample_data").GetProperty("none").ValueKind);
        Assert.Equal("tpl_1", result.Template.Id);
        Assert.Equal(1, result.Version.Version);
        Assert.Equal("Ada", result.Version.SampleData!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task OtherTemplateMethodsUseTheirPathsAndBodies()
    {
        var t = TestClient.Answering((r, _) => r.PathAndQuery switch
        {
            "/v1/templates/tpl_1/versions" => Responses.Json(201, _created),
            "/v1/templates/tpl_1/versions/3" => Responses.Json(200, $$"""{"version":{{Samples.VersionJson}}}"""),
            _ when r.Method == "DELETE" => Responses.Empty(204),
            _ => Responses.Json(200, $$"""{"template":{{Samples.TemplateJson.Replace("}", ",\"versions\":[{\"version\":1,\"live\":true,\"bytes\":10,\"file_count\":0,\"has_sample_data\":true,\"typst_version\":\"0.13.1\",\"created_at\":\"2026-01-01T00:00:00Z\"}]}")}}}"""),
        });

        var got = await t.Client.Templates.GetAsync("tpl_1");
        Assert.True(Assert.Single(got.Versions!).Live);
        await t.Client.Templates.RenameAsync("tpl_1", "New name");
        await t.Client.Templates.DeleteAsync("tpl_1");
        await t.Client.Templates.CreateVersionAsync("tpl_1", new CreateVersionOptions { Source = "v2", Publish = false });
        var version = await t.Client.Templates.GetVersionAsync("tpl_1", 3);
        await t.Client.Templates.PublishAsync("tpl_1", 2);

        var calls = t.Requests.Select(r => $"{r.Method} {r.PathAndQuery} {(r.Body is null ? "" : r.Body.Utf8())}").ToArray();
        Assert.Equal(
            [
                "GET /v1/templates/tpl_1 ",
                """PATCH /v1/templates/tpl_1 {"name":"New name"}""",
                "DELETE /v1/templates/tpl_1 ",
                """POST /v1/templates/tpl_1/versions {"source":"v2","publish":false}""",
                "GET /v1/templates/tpl_1/versions/3 ",
                """POST /v1/templates/tpl_1/publish {"version":2}""",
            ],
            calls);
        Assert.Equal("Hello", version.Source);
    }

    [Fact]
    public async Task PathSegmentsAreEscaped()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, $$"""{"template":{{Samples.TemplateJson}}}"""));
        await t.Client.Templates.GetAsync("a/b?c#d e");
        Assert.Equal("/v1/templates/a%2Fb%3Fc%23d%20e", t.Requests[0].Url.AbsolutePath);
    }

    [Fact]
    public async Task EmptyOrNullIdIsRejectedWithoutARequest()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, "{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Templates.GetAsync(""));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Client.Templates.GetAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Templates.GetAsync(".."));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Batches.GetAsync("."));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Templates.RenderAsync("", new RenderOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Jobs.GetAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Client.Batches.DownloadZipAsync(""));
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task MissingWrapperIsInvalidResponse()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, Samples.TemplateJson));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.GetAsync("tpl_1"));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(200, e.Status);

        var t2 = TestClient.Answering((_, _) => Responses.Json(201, $$"""{"template":{{Samples.TemplateJson}}}"""));
        e = await Assert.ThrowsAsync<PageRushException>(() => t2.Client.Templates.CreateAsync(new CreateTemplateOptions { Name = "x", Source = "y" }));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(201, e.Status);
    }

    [Fact]
    public async Task NonJsonSuccessBodyIsInvalidResponse()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, "<html>proxy</html>"u8.ToArray(), "text/html"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.ListAsync());
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(200, e.Status);
        Assert.IsType<JsonException>(e.InnerException);
    }

    [Fact]
    public async Task RenderReturnsPdfWithParsedHeaders()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(
            200,
            Samples.Pdf,
            "application/pdf",
            ("X-Job-Id", "job_9"),
            ("X-Template-Version", "4"),
            ("X-Render-Engine", "typst"),
            ("X-Page-Count", "2"),
            ("X-Template-Warnings", "0"),
            ("X-Request-Id", "req_1"),
            ("Content-Disposition", "attachment; filename=\"INV-1.pdf\"")));
        var result = await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions
        {
            Data = new { number = "INV-1", total = 12.5 },
            Version = 4,
            Filename = "INV-1.pdf",
            Metadata = new Dictionary<string, string> { ["order"] = "42" },
            DeleteAfterDownload = true,
        });

        var r = Assert.Single(t.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/v1/templates/tpl_1/render", r.PathAndQuery);
        Assert.Equal("application/pdf, application/json", r.Header("Accept"));
        Assert.Null(r.Header("Prefer"));
        Assert.Equal("""{"data":{"number":"INV-1","total":12.5},"version":4,"filename":"INV-1.pdf","metadata":{"order":"42"},"delete_after_download":true}""", r.Body!.Utf8());

        Assert.False(result.Queued);
        Assert.Equal(Samples.Pdf, result.Pdf);
        Assert.Null(result.Job);
        Assert.Equal("job_9", result.JobId);
        Assert.Equal(4, result.TemplateVersion);
        Assert.Equal("typst", result.Engine);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(0, result.Warnings);
        Assert.Equal("req_1", result.RequestId);
        Assert.Equal("INV-1.pdf", result.Filename);
    }

    [Fact]
    public async Task RenderWithoutOptionalHeaders()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf", ("X-Job-Id", "job_9"), ("X-Page-Count", "many")));
        var result = await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = null });
        Assert.Equal("""{"data":null}""", t.Requests[0].Body!.Utf8());
        Assert.Null(result.PageCount);
        Assert.Null(result.Warnings);
        Assert.Null(result.TemplateVersion);
        Assert.Null(result.Filename);
        Assert.Null(result.RequestId);
    }

    [Fact]
    public async Task RenderQueuedReturnsJob()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(202, Samples.Job("queued")));
        var result = await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions
        {
            Data = new Dictionary<string, int> { ["n"] = 1 },
            CallbackUrl = "https://example.com/hook",
            PreferAsync = true,
        });
        var r = t.Requests[0];
        Assert.Equal("respond-async", r.Header("Prefer"));
        Assert.Equal("https://example.com/hook", r.Json().GetProperty("callback_url").GetString());
        Assert.True(result.Queued);
        Assert.Null(result.Pdf);
        Assert.Equal("job_1", result.JobId);
        Assert.Equal("job_1", result.Job!.Id);
        Assert.Equal(2, result.TemplateVersion);
        Assert.Null(result.Engine);
        Assert.Equal("42", result.Job.Metadata!["order"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => result.SaveAsync(Path.GetTempFileName()));
    }

    [Fact]
    public async Task RenderNonPdf200IsInvalidResponse()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(200, "{}"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 }));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(200, e.Status);
    }

    [Fact]
    public async Task Render202WithoutJobIdIsInvalidResponse()
    {
        var t = TestClient.Answering((_, _) => Responses.Json(202, """{"status":"queued"}"""));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 }));
        Assert.Equal(ErrorCodes.InvalidResponse, e.Code);
        Assert.Equal(202, e.Status);
    }

    [Fact]
    public async Task SaveWritesThePdf()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        var result = await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        var path = Path.Combine(Path.GetTempPath(), "pagerush-test-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await result.SaveAsync(path);
            Assert.Equal(Samples.Pdf, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IdempotencyKeyIsGeneratedAndReusedAcrossRetries()
    {
        var t = TestClient.Sequence(
            () => Responses.Problem(503, "RENDER_UNAVAILABLE"),
            () => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.Equal(2, t.Requests.Count);
        var key = t.Requests[0].Header("Idempotency-Key");
        Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), key);
        Assert.Equal(key, t.Requests[1].Header("Idempotency-Key"));

        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1 });
        Assert.NotEqual(key, t.Requests[2].Header("Idempotency-Key"));
    }

    [Fact]
    public async Task CustomIdempotencyKeyIsSent()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1, IdempotencyKey = "invoice-42:v1.0_x" });
        Assert.Equal("invoice-42:v1.0_x", t.Requests[0].Header("Idempotency-Key"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/no")]
    [InlineData("café")]
    [InlineData("line\nbreak")]
    public async Task InvalidIdempotencyKeyFailsBeforeSending(string key)
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1, IdempotencyKey = key }));
        Assert.Equal(ErrorCodes.ValidationFailed, e.Code);
        Assert.Equal(0, e.Status);
        Assert.IsNotType<PageRushConnectionException>(e);
        Assert.Empty(t.Requests);

        e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Batches.CreateAsync("tpl_1", new CreateBatchOptions { Items = [new BatchItemInput { Data = 1 }], IdempotencyKey = key }));
        Assert.Equal(ErrorCodes.ValidationFailed, e.Code);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task IdempotencyKeyOf255CharactersIsAccepted()
    {
        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        await t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1, IdempotencyKey = new string('a', 255) });
        await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = 1, IdempotencyKey = new string('a', 256) }));
        Assert.Single(t.Requests);
    }

    public static IEnumerable<object[]> UnencodableData()
    {
        yield return [double.NaN];
        yield return [new Dictionary<string, object> { ["x"] = double.PositiveInfinity }];
        yield return [new { text = "lone \ud800 surrogate" }];
        yield return [new Func<int>(() => 1)];
        yield return ["cycle"];
    }

    [Theory]
    [MemberData(nameof(UnencodableData))]
    public async Task UnencodableBodyIsValidationFailed(object data)
    {
        if (data is "cycle")
        {
            var cycle = new Dictionary<string, object>();
            cycle["self"] = cycle;
            data = cycle;
        }

        var t = TestClient.Answering((_, _) => Responses.Bytes(200, Samples.Pdf, "application/pdf"));
        var e = await Assert.ThrowsAsync<PageRushException>(() => t.Client.Templates.RenderAsync("tpl_1", new RenderOptions { Data = data }));
        Assert.Equal(ErrorCodes.ValidationFailed, e.Code);
        Assert.Equal(0, e.Status);
        Assert.Empty(t.Requests);
    }

    [Fact]
    public void ContentDispositionFilenameForms()
    {
        Assert.Equal("a b.pdf", TemplatesResource.ContentDispositionFilename("attachment; filename=\"a b.pdf\""));
        Assert.Equal("plain.pdf", TemplatesResource.ContentDispositionFilename("inline; filename=plain.pdf"));
        Assert.Equal("x.pdf", TemplatesResource.ContentDispositionFilename("attachment; filename*=UTF-8''y.pdf; filename=\"x.pdf\""));
        Assert.Null(TemplatesResource.ContentDispositionFilename("attachment"));
        Assert.Null(TemplatesResource.ContentDispositionFilename(null));
    }
}
