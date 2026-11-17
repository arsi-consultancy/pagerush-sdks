# PageRush .NET SDK

The official .NET client for the [PageRush](https://pagerush.dev) API, which turns Typst templates and JSON data into PDFs.

It targets .NET Standard 2.0 and .NET 10, so it runs on .NET Framework 4.6.2+ as well as modern .NET. On .NET 10 it has no dependencies; on .NET Standard 2.0 it depends on `System.Text.Json`. Every call is async and takes a `CancellationToken`.

## Install

```sh
dotnet add package PageRush
```

## Quickstart

Create a project API key in the PageRush dashboard and set it as `PAGERUSH_API_KEY`. Then create a console app (`dotnet new console`), add the package, and replace `Program.cs` with:

```csharp
using System;
using PageRush;

var client = new PageRushClient(); // reads PAGERUSH_API_KEY

var created = await client.Templates.CreateAsync(new CreateTemplateOptions
{
    Name = "Quickstart invoice",
    Source = string.Join("\n",
        "#let data = json(\"data.json\")",
        "= Invoice #data.number",
        "Bill to: #data.customer",
        "",
        "#table(columns: 2, ..data.items.map(i => (i.name, str(i.price))).flatten())"),
});

var result = await client.Templates.RenderAsync(created.Template.Id, new RenderOptions
{
    Data = new
    {
        number = "INV-001",
        customer = "Example Corp",
        items = new[]
        {
            new { name = "Design", price = 400 },
            new { name = "Build", price = 1200 },
        },
    },
});

await result.SaveAsync("invoice.pdf");
Console.WriteLine($"Saved invoice.pdf (job {result.JobId})");
```

Run it with `dotnet run`. A template reads the render's `Data` with `#let data = json("data.json")`. More programs are in `examples/`: [`Quickstart`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/examples/Quickstart/Program.cs), [`AsyncRender`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/examples/AsyncRender/Program.cs) and [`WebhookReceiver`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/examples/WebhookReceiver/Program.cs).

## Usage

### Client configuration

```csharp
using PageRush;

var client = new PageRushClient(new PageRushClientOptions
{
    ApiKey = "pk_live_...", // default: env PAGERUSH_API_KEY; the constructor throws if neither is set
    BaseUrl = "https://pagerush.dev", // default: env PAGERUSH_BASE_URL, else https://pagerush.dev
    Timeout = TimeSpan.FromSeconds(60), // per HTTP attempt, including reading the body
    MaxRetries = 2, // retries after the first attempt
    HttpClient = myHttpClient, // optional: your own HttpClient (proxies, tests)
});
```

`new PageRushClient("pk_live_...")` is a shortcut for setting only the key. Create one client and reuse it; it is safe to use from several threads. Without `HttpClient`, all clients share one internal `HttpClient`, so creating many clients doesn't exhaust sockets.

The SDK follows redirects itself, so it can drop the `Authorization` header when a redirect leaves the API's origin. If you pass your own `HttpClient`, build it on a handler with `AllowAutoRedirect = false` so that rule applies:

```csharp
var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = new WebProxy("http://proxy:8080") });
var client = new PageRushClient(new PageRushClientOptions { HttpClient = http });
```

Every method takes an optional `CancellationToken` as its last argument. Cancelling throws `OperationCanceledException` and also stops a pending retry wait or a `WaitAsync` poll.

Request and response properties map to the API's own snake_case fields (`LiveVersion` is `live_version`, `NextCursor` is `next_cursor`), so the JSON matches the [API reference](https://pagerush.dev/v1/openapi.json) one to one.

### Templates

A template version is a Typst project. `Source` is its `main.typ`. `Files` holds any other files it imports or reads. Use `TemplateFile.FromString`, `FromBytes` or `FromPath` to build file entries: text files (`.typ .txt .csv .json .svg .xml .md .yml .yaml .bib`) are sent as `utf8`, everything else (images, fonts) as `base64`.

```csharp
var created = await client.Templates.CreateAsync(new CreateTemplateOptions
{
    Name = "Invoice",
    Source = "#import \"parts/header.typ\": header\n#header\n#image(\"logo.png\", width: 3cm)",
    Files =
    [
        TemplateFile.FromString("parts/header.typ", "#let header = [*Example Corp*]"),
        TemplateFile.FromBytes("logo.png", File.ReadAllBytes("assets/logo.png")),
        TemplateFile.FromPath("assets/Inter.ttf", "fonts/Inter.ttf"),
    ],
    SampleData = new { number = "INV-001" }, // used by test renders in the dashboard
});
var template = created.Template;
```

Add a version. It becomes the live version unless you set `Publish = false`:

```csharp
var draft = await client.Templates.CreateVersionAsync(template.Id, new CreateVersionOptions
{
    Source = "...",
    Publish = false,
});
```

Publish a version, or roll back by publishing an older one:

```csharp
await client.Templates.PublishAsync(template.Id, draft.Version.Version);
await client.Templates.PublishAsync(template.Id, 1); // roll back
```

List templates one page at a time, or iterate over all of them:

```csharp
var page = await client.Templates.ListAsync(new ListTemplatesOptions { Limit = 20 });
if (page.NextCursor != null)
{
    var next = await client.Templates.ListAsync(new ListTemplatesOptions { Limit = 20, Cursor = page.NextCursor });
}

await foreach (var t in client.Templates.ListAllAsync())
{
    Console.WriteLine($"{t.Id} {t.Name} {t.LiveVersion}");
}
```

Get, rename, read a version, delete:

```csharp
var t = await client.Templates.GetAsync(template.Id); // includes t.Versions, newest first
await client.Templates.RenameAsync(template.Id, "Invoice v2");
var v = await client.Templates.GetVersionAsync(template.Id, 1); // v.Source, v.Files, v.TypstVersion
await client.Templates.DeleteAsync(template.Id);
```

### Rendering

```csharp
var result = await client.Templates.RenderAsync(template.Id, new RenderOptions
{
    Data = new { number = "INV-002" }, // required: any JSON value
    Version = 3, // optional: render this version instead of the live one
    Filename = "INV-002.pdf", // optional: name in Content-Disposition
    Metadata = new Dictionary<string, string> { ["order"] = "42" }, // optional: up to 20 string pairs (4 KB), echoed on the job and callback
    DeleteAfterDownload = true, // optional: don't keep the PDF for 24 hours
});

byte[] pdf = result.Pdf!;
Console.WriteLine(result.JobId); // e.g. "job_..."
Console.WriteLine(result.TemplateVersion); // the version that was rendered
Console.WriteLine(result.PageCount); // int?, from X-Page-Count
Console.WriteLine(result.Warnings); // int?, the number of Typst warnings
await result.SaveAsync("INV-002.pdf");
```

`Data` is serialised with System.Text.Json exactly as given: a dictionary, an anonymous object, your own class, or a `JsonElement`. Property names are not renamed, so use the names your template reads.

**Idempotency keys.** Every render sends an `Idempotency-Key` header. If you don't set one, the SDK creates a random UUID and reuses it for that call's retries, so a retried render is never rendered or charged twice. Set your own key to make a render safe to repeat across processes, for example after a crash:

```csharp
await client.Templates.RenderAsync(template.Id, new RenderOptions { Data = data, IdempotencyKey = $"invoice-{invoiceId}" });
```

A key must be 1-255 characters from `A-Z a-z 0-9 _ - : .`. Anything else throws `PageRushException` with code `VALIDATION_FAILED` (status 0) before the request is sent.

**Asynchronous fallback.** With `PreferAsync = true` the server still renders straight away. If that attempt fails for a temporary reason (for example, no render capacity), it queues the render and answers with a job instead of an error. Check `result.Queued`:

```csharp
var result = await client.Templates.RenderAsync(template.Id, new RenderOptions { Data = data, PreferAsync = true });
var pdf = result.Pdf;
if (result.Queued)
{
    var job = await client.Jobs.WaitAsync(result.JobId!, new WaitOptions { Timeout = TimeSpan.FromMinutes(2) });
    if (job.Status == JobStatus.Failed)
    {
        throw new Exception($"{job.Error?.Code}: {job.Error?.Message}");
    }

    pdf = await client.Jobs.DownloadAsync(job.Id);
}
```

`Jobs.WaitAsync` returns the job once it has `succeeded` or `failed`; it polls every second by default. It throws a `PageRushException` with code `WAIT_TIMEOUT` if the job is still running after the timeout (default 120 seconds). `Jobs.DownloadAsync` fetches the PDF through the job's signed `download_url` (it does not send your API key there). It throws code `FILE_EXPIRED` when the job has no PDF to download: not finished, failed, older than 24 hours, or already delivered with `DeleteAfterDownload`.

**Callbacks.** With `CallbackUrl` the render is always queued, and PageRush POSTs the signed PDF to your https URL when it is ready:

```csharp
var result = await client.Templates.RenderAsync(template.Id, new RenderOptions
{
    Data = data,
    CallbackUrl = "https://example.com/pagerush",
    Metadata = new Dictionary<string, string> { ["order"] = "42" },
});
Console.WriteLine($"{result.Queued} {result.JobId}"); // True job_...
```

### Batches

A batch renders one template once per item (up to 10,000 items) and collects the PDFs into a ZIP. `Batches.CreateAsync` returns at once with the queued batch; wait for it, then download the ZIP:

```csharp
var customers = new[]
{
    new { id = "C-001", name = "Ada Lovelace", total = 120 },
    new { id = "C-002", name = "Grace Hopper", total = 80 },
};

var created = await client.Batches.CreateAsync(template.Id, new CreateBatchOptions
{
    Items = customers.Select(c => new BatchItemInput { Data = c, Filename = $"{c.id}.pdf" }).ToList(), // Filename is optional
    Metadata = new Dictionary<string, string> { ["run"] = "2026-11" }, // optional, as for renders
    // Version, DeleteAfterDownload and CallbackUrl work as for renders
});

var batch = await client.Batches.WaitAsync(created.Id); // polls every 2 s, for up to 10 minutes
Console.WriteLine($"{batch.Status} {batch.Succeeded} {batch.Failed}"); // succeeded | partial | failed
foreach (var item in batch.Items ?? [])
{
    if (item.Error != null)
    {
        Console.Error.WriteLine($"{item.Index} {item.Error.Code} {item.Error.Message}");
    }
}

if (batch.Succeeded > 0)
{
    await File.WriteAllBytesAsync("invoices.zip", await client.Batches.DownloadZipAsync(batch.Id));
}
```

`Batches.WaitAsync` returns the batch once it is `succeeded`, `partial` (some items failed) or `failed`; it doesn't throw for failed items. It throws a `PageRushException` with code `WAIT_TIMEOUT` if the batch is still running after the timeout (default 600 seconds). `Batches.GetAsync` returns the batch with its `Items`: `Index`, `JobId`, `Status`, `Filename`, `Bytes`, `Renders`, `Error`.

`Batches.DownloadZipAsync` sends your API key (the batch's `ZipUrl` needs it, unlike a job's signed `download_url`). While the batch is still rendering it throws status 409 with code `CONFLICT` and `RetryAfter`; once no PDFs are left it throws status 410 with code `FILE_EXPIRED`. A batch's ZIP can be downloaded a limited number of times; after that it throws 429 `RATE_LIMITED`, and single PDFs stay available through `Jobs.DownloadAsync(item.JobId)`.

Like renders, `Batches.CreateAsync` always sends an `Idempotency-Key` (a random UUID unless you set `IdempotencyKey`) and reuses it across retries, so a retry never creates a second batch. The server refuses a batch your quota and credits can't cover (402 `QUOTA_EXCEEDED`) and a fourth unfinished batch (429 `CONCURRENCY_LIMIT`). The SDK doesn't split large lists into several batches.

With `CallbackUrl`, PageRush also POSTs a signed `batch.completed` notice when the batch has finished (see Webhooks).

### Webhooks

PageRush sends `pdf.succeeded` (the body is the PDF) or `pdf.failed` (the body is JSON) to your `CallbackUrl`. Each delivery is signed with your project's webhook secret: the signature in `X-PageRush-Signature` covers the timestamp, the event, job id and metadata headers, and the raw body. `Webhooks.ConstructEvent` verifies the signature and timestamp, then parses the delivery:

```csharp
var ev = Webhooks.ConstructEvent(rawBody, request.Headers, secret);
// ev.Event: "pdf.succeeded", "pdf.failed" or "batch.completed" (see WebhookEvents)
// ev.JobId: dedupe on this (null for batch.completed)
// ev.Pdf: the PDF bytes for pdf.succeeded, else null
// ev.Failure: ev.Failure.Error.Code and .Message for pdf.failed, else null
// ev.Metadata: the render's metadata, or null
```

A batch's `batch.completed` notice is signed the same way, with `X-PageRush-Batch-Id` in place of `X-PageRush-Job-Id`. For it, `ConstructEvent` returns `JobId = null`, `BatchId` (dedupe on this) and `Batch` (the `Batch` without `Items`). The notice is only a summary: download the PDFs with `client.Batches.DownloadZipAsync(ev.BatchId!)`, which needs an API key.

Every field comes from signed data. Use `Webhooks.Verify(rawBody, headers, secret)` if you only need the check. Both throw `WebhookVerificationException` with a `Reason` of `header`, `timestamp` (older or newer than `Tolerance`, default 300 seconds) or `signature`. During a secret rotation, pass both secrets: `Webhooks.ConstructEvent(rawBody, headers, new[] { newSecret, oldSecret })`. These methods are static and don't need an API key or a client. A missing or empty secret is a setup mistake, so it throws `ArgumentException` rather than `WebhookVerificationException`: check that `PAGERUSH_WEBHOOK_SECRET` is set when your app starts.

`headers` can be ASP.NET Core's `Request.Headers`, an `HttpHeaders`, a `NameValueCollection` (for example `HttpListenerRequest.Headers`) or a dictionary. Header names are matched case-insensitively.

> **Use the raw body.** The signature covers the exact bytes PageRush sent. Read the request body into a `byte[]` yourself. If you bind it to a model or parse JSON and serialise it again, the bytes change and verification fails.

> **Dedupe on `JobId`** (or `BatchId` for `batch.completed`). Delivery is at-least-once: a callback can arrive more than once, always with the same `X-PageRush-Job-Id` (or `X-PageRush-Batch-Id`). Answer 2xx quickly and ignore repeats. Record an id as processed only after your work has succeeded, so a failed attempt is retried. Check the id against `^[A-Za-z0-9_-]{1,64}$` before using it in a file name.

A complete ASP.NET Core minimal API receiver:

```csharp
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PageRush;

var secret = Environment.GetEnvironmentVariable("PAGERUSH_WEBHOOK_SECRET");
if (string.IsNullOrEmpty(secret))
{
    throw new InvalidOperationException("Set PAGERUSH_WEBHOOK_SECRET to your project's webhook secret.");
}

var safeId = new Regex("^[A-Za-z0-9_-]{1,64}$");
var processed = new ConcurrentDictionary<string, bool>(); // use your database in production

var app = WebApplication.CreateBuilder(args).Build();

app.MapPost("/pagerush", async (HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer); // the raw bytes, not a bound model
    WebhookEvent ev;
    try
    {
        ev = Webhooks.ConstructEvent(buffer.ToArray(), request.Headers, secret);
    }
    catch (WebhookVerificationException e)
    {
        return Results.BadRequest(e.Reason);
    }

    var id = ev.JobId ?? ev.BatchId ?? ""; // batch.completed carries BatchId
    if (!safeId.IsMatch(id))
    {
        return Results.BadRequest("unexpected id");
    }

    if (processed.ContainsKey(id))
    {
        return Results.Ok();
    }

    switch (ev.Event)
    {
        case WebhookEvents.PdfSucceeded:
            await File.WriteAllBytesAsync($"{id}.pdf", ev.Pdf!);
            break;
        case WebhookEvents.PdfFailed:
            Console.Error.WriteLine($"{ev.Failure?.Error?.Code}: {ev.Failure?.Error?.Message}");
            break;
        case WebhookEvents.BatchCompleted:
            Console.WriteLine($"batch {id}: {ev.Batch?.Status}"); // then client.Batches.DownloadZipAsync(id)
            break;
    }

    processed[id] = true; // only after the work succeeded, so a failed attempt is retried
    return Results.Ok();
});

app.Run();
```

If an exception escapes the handler, ASP.NET Core answers 500 and PageRush retries the delivery. Kestrel limits request bodies to about 30 MB by default; raise `MaxRequestBodySize` if your PDFs can be larger. The same program, with logging and a second secret for rotations, is in [`examples/WebhookReceiver`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/examples/WebhookReceiver/Program.cs).

### Errors

Every non-2xx response throws a `PageRushException`:

| property     | meaning                                                                                              |
| ------------ | ---------------------------------------------------------------------------------------------------- |
| `Status`     | HTTP status, or 0 when there was no response                                                         |
| `Code`       | machine-readable code, e.g. `TEMPLATE_NOT_FOUND`; `UNKNOWN` if the error body was not problem+json   |
| `Title`      | short summary, or null                                                                               |
| `Detail`     | longer explanation, or null                                                                          |
| `Errors`     | list of `FieldError` (`Path`, `Message`); on `TEMPLATE_COMPILE_FAILED` these are Typst diagnostics   |
| `RequestId`  | include this when you contact support                                                                |
| `RetryAfter` | the `Retry-After` header as a `TimeSpan?`                                                            |
| `Type`       | problem type URI, or null                                                                            |

The message is `"<code>: <detail or title>"`. When the request never got a response, the SDK throws `PageRushConnectionException` (a subclass with `Status` 0) with code `CONNECTION_ERROR`, or `TIMEOUT` if an attempt took longer than `Timeout`. The transport's exception is its `InnerException`.

If the server answers 2xx with a body the SDK can't use (not JSON, missing the expected fields, or a 200 render that isn't `application/pdf`), it throws `PageRushException` with code `INVALID_RESPONSE` and the real HTTP status. This usually means a proxy or firewall answered instead of PageRush.

Some problems are caught before anything is sent, and are never retried: an `IdempotencyKey` the server would reject, or a request body JSON can't represent (`NaN`, infinity, a string with an unpaired surrogate, a reference cycle, or a type System.Text.Json can't serialise). These throw `PageRushException` with code `VALIDATION_FAILED` and status 0. A null or empty id, `"."` or `".."` throws `ArgumentException`. The constructor throws `ArgumentException` for a missing API key, a key with spaces, control characters or non-ASCII characters (the message never includes the key), or a `BaseUrl` that isn't an http(s) URL.

The API key never appears in error messages, `ToString()` of the client or its options, or their JSON serialisation. Signed download links appear in errors without their query string.

```csharp
try
{
    await client.Templates.RenderAsync(templateId, new RenderOptions { Data = data });
}
catch (PageRushConnectionException e)
{
    Console.Error.WriteLine($"network problem: {e.Code} {e.InnerException?.Message}");
}
catch (PageRushException e) when (e.Code == ErrorCodes.TemplateCompileFailed)
{
    foreach (var d in e.Errors)
    {
        Console.Error.WriteLine($"{d.Path}: {d.Message}");
    }
}
catch (PageRushException e)
{
    if (e.Code == ErrorCodes.QuotaExceeded)
    {
        // upgrade the plan or wait for the next period
    }

    Console.Error.WriteLine($"{e.Message} (request {e.RequestId})");
}
```

Codes are plain strings, so new server codes come through unchanged. `ErrorCodes` lists the known ones as constants.

### Retries and timeouts

The SDK retries a failed call up to `MaxRetries` times (default 2, so at most 3 attempts):

- `GET` and `DELETE`: on connection errors, timeouts, 429, 502, 503 and 504.
- `Batches.CreateAsync`: the same as `GET`. It always carries an idempotency key, so a retry never creates a second batch. `IDEMPOTENCY_CONFLICT` is never retried.
- Renders: the same, plus 409 with code `CONFLICT`, which means the server is still rendering an earlier attempt with the same idempotency key. Renders are safe to retry because they always carry an idempotency key. `IDEMPOTENCY_CONFLICT` (the same key with a different body) is never retried.
- `CreateAsync`, `CreateVersionAsync`, `PublishAsync` and `RenameAsync`: only on 429 and 503, which the server returns before doing any work. They are not retried on connection errors, 502 or 504, because the change may already have been made.

Before retry _n_ it waits for `Retry-After` when the server sends it (capped at 30 seconds), and otherwise `min(8, 0.5 × 2^(n-1))` seconds with ±25 % jitter. `Timeout` (default 60 seconds) applies to each attempt and covers reading the whole response body; set it to `Timeout.InfiniteTimeSpan` to turn it off. When you pass your own `HttpClient`, its `HttpClient.Timeout` also applies. Redirects are followed for up to 5 hops; the `Authorization` header is dropped when a redirect leaves the API's origin (another host or port, or plain http).

In tests, replace the waits with `PageRushClientOptions.Sleep`, a `Func<TimeSpan, CancellationToken, Task>`.

## API reference

Every method below is async, returns a `Task`, and takes an optional `CancellationToken cancellationToken = default` as its last parameter.

### `PageRushClient`

| member                                         | notes                                                                          |
| ---------------------------------------------- | ------------------------------------------------------------------------------ |
| `new PageRushClient()`                         | key from `PAGERUSH_API_KEY`                                                    |
| `new PageRushClient(string? apiKey)`           |                                                                                |
| `new PageRushClient(PageRushClientOptions)`    | throws `ArgumentException` for a missing or invalid key or base URL            |
| `Templates`, `Jobs`, `Batches`                 | the resources below                                                            |
| `BaseUrl`                                      | the API base URL in use                                                        |
| `PageRushClient.Version`                       | this SDK's version; sent as `User-Agent: pagerush-dotnet/<version>`            |
| `PageRushClient.DefaultBaseUrl`                | `https://pagerush.dev`                                                         |

`PageRushClientOptions`: `ApiKey`, `BaseUrl`, `Timeout` (default 60 s), `MaxRetries` (default 2), `HttpClient`, `Sleep`.

### `client.Templates`

| method                                                    | HTTP                                  | returns                                  |
| --------------------------------------------------------- | ------------------------------------- | ---------------------------------------- |
| `ListAsync(ListTemplatesOptions? options)`                | `GET /v1/templates`                   | `TemplateList` (`Items`, `NextCursor`)   |
| `ListAllAsync(ListTemplatesOptions? options)`             | repeated `ListAsync`                  | `IAsyncEnumerable<Template>`             |
| `CreateAsync(CreateTemplateOptions options)`              | `POST /v1/templates`                  | `TemplateWithVersion`                    |
| `GetAsync(string id)`                                     | `GET /v1/templates/{id}`              | `Template` (with `Versions`)             |
| `RenameAsync(string id, string name)`                     | `PATCH /v1/templates/{id}`            | `Template`                               |
| `DeleteAsync(string id)`                                  | `DELETE /v1/templates/{id}`           | `Task`                                   |
| `CreateVersionAsync(string id, CreateVersionOptions options)` | `POST /v1/templates/{id}/versions` | `TemplateWithVersion`                    |
| `GetVersionAsync(string id, int version)`                 | `GET /v1/templates/{id}/versions/{v}` | `TemplateVersion`                        |
| `PublishAsync(string id, int version)`                    | `POST /v1/templates/{id}/publish`     | `Template`                               |
| `RenderAsync(string id, RenderOptions options)`           | `POST /v1/templates/{id}/render`      | `RenderResult`                           |

- `ListTemplatesOptions`: `Limit`, `Cursor`.
- `CreateTemplateOptions`: `Name` (required), `Source`, `Document`, `Files`, `SampleData`, `TypstVersion`. `CreateVersionOptions`: the same without `Name`, plus `Publish`.
- `RenderOptions`: `Data` (required), `Version`, `Filename`, `Metadata`, `DeleteAfterDownload`, `CallbackUrl`, `IdempotencyKey`, `PreferAsync`.
- `RenderResult`: `Queued`, `Pdf`, `Job`, `JobId`, `TemplateVersion`, `Engine`, `PageCount`, `Warnings`, `RequestId`, `Filename`, and `SaveAsync(path)`. When `Queued` is false, `Pdf` holds the bytes and `Job` is null. When it is true, `Pdf` is null, `Job` is the queued `Job`, and the header properties are null. `SaveAsync` throws `InvalidOperationException` if the render was queued.

### `client.Jobs`

| method                                         | returns                         | throws                                         |
| ---------------------------------------------- | ------------------------------- | ---------------------------------------------- |
| `GetAsync(string id)`                          | `Job`                           | `NOT_FOUND`                                    |
| `WaitAsync(string id, WaitOptions? options)`   | `Job` (`succeeded` or `failed`) | `WAIT_TIMEOUT` (defaults: 120 s, every 1 s)    |
| `DownloadAsync(string id)`                     | `byte[]`                        | `FILE_EXPIRED` if there is no `download_url`   |

`WaitOptions`: `Timeout`, `Interval`.

### `client.Batches`

| method                                                     | HTTP                              | returns                                      | throws                                                                   |
| ---------------------------------------------------------- | --------------------------------- | -------------------------------------------- | ------------------------------------------------------------------------ |
| `CreateAsync(string templateId, CreateBatchOptions options)` | `POST /v1/templates/{id}/batches` | `Batch` (queued)                           | `QUOTA_EXCEEDED`, `CONCURRENCY_LIMIT`, `VALIDATION_FAILED`               |
| `GetAsync(string id)`                                      | `GET /v1/batches/{id}`            | `Batch` with `Items`                         | `NOT_FOUND`                                                              |
| `WaitAsync(string id, WaitOptions? options)`               | polls `GetAsync`                  | `Batch` (`succeeded`, `partial` or `failed`) | `WAIT_TIMEOUT` (defaults: 600 s, every 2 s)                              |
| `DownloadZipAsync(string id)`                              | `GET /v1/batches/{id}/zip`        | `byte[]` (the ZIP)                           | 409 `CONFLICT` (still rendering), 410 `FILE_EXPIRED`, 429 `RATE_LIMITED`, `INVALID_RESPONSE` |

`CreateBatchOptions`: `Items` (required, 1 to 10,000 `BatchItemInput` with `Data` and optional `Filename`), `Version`, `Metadata`, `DeleteAfterDownload`, `CallbackUrl`, `IdempotencyKey`. `Batch`: `Id`, `Status`, `TemplateId`, `TemplateVersion`, `ItemCount`, `Succeeded`, `Failed`, `ZipUrl`, `ExpiresAt`, `Metadata`, `DeleteAfterDownload`, `Delivery`, `CreatedAt`, `FinishedAt`, `Items`, `IsDone`. `BatchItem`: `Index`, `JobId`, `Status`, `Filename`, `Bytes`, `Renders`, `Error`.

### `Webhooks` (static)

| method                                                              | returns        | throws                                                 |
| ------------------------------------------------------------------- | -------------- | ------------------------------------------------------ |
| `Verify(byte[] body, headers, string secret, WebhookVerifyOptions?)` | `void`        | `WebhookVerificationException`, `ArgumentException`    |
| `Verify(byte[] body, headers, IEnumerable<string> secrets, WebhookVerifyOptions?)` | `void` | the same                                     |
| `ConstructEvent(byte[] body, headers, secret or secrets, WebhookVerifyOptions?)` | `WebhookEvent` | the same                                  |

These are synchronous. `headers` is an `IEnumerable<KeyValuePair<string, string>>`, an `IEnumerable<KeyValuePair<string, TValues>>` where `TValues` is a sequence of strings (ASP.NET Core `Request.Headers`, `HttpHeaders`), or a `NameValueCollection`. `WebhookVerifyOptions`: `Tolerance` (default 300 s), `Now`. `WebhookEvent`: `Event`, `JobId`, `BatchId`, `Batch`, `Timestamp`, `Pdf`, `Failure`, `Metadata`. `WebhookFailure`: `Event`, `JobId`, `TemplateId`, `Error`, `Metadata`, `CreatedAt`, `Json` (the whole body).

### Other types

- `TemplateFile.FromString(path, text)`, `TemplateFile.FromBytes(path, bytes)`, `TemplateFile.FromPath(localPath, targetPath?)`: build a `TemplateFile` (`Path`, `Content`, `Encoding`). `FromBytes` throws `ArgumentException` for a text file that isn't valid UTF-8.
- Errors: `PageRushException`, `PageRushConnectionException`, `FieldError`, `WebhookVerificationException`, `ErrorCodes`.
- Models: `Template`, `TemplateVersionSummary`, `TemplateVersion`, `TemplateWithVersion`, `TemplateList`, `Job`, `JobError`, `JobDelivery`, `Batch`, `BatchItem`.
- String constants: `JobStatus`, `BatchStatus`, `WebhookEvents`, `WebhookHeaders`, `WebhookReasons`.

Every public symbol has XML doc comments, so your editor shows this documentation inline.

## Supported versions

- .NET 10 or later, or any platform that supports .NET Standard 2.0 (including .NET Framework 4.6.2 or later). CI runs the tests on .NET 10 on Linux and Windows, and on .NET Framework on Windows.
- On .NET Standard 2.0 the package depends on `System.Text.Json`, which brings `Microsoft.Bcl.AsyncInterfaces` (for `IAsyncEnumerable`) and other packages with it. On .NET 10 it has no dependencies.
- API version: `v1`.

## Versioning

This package follows [Semantic Versioning 2.0.0](https://semver.org). While it is `0.x`, a minor release may contain breaking changes. From `1.0.0`, breaking changes need a new major version. The SDK version is separate from the API version (`/v1`). See [CHANGELOG.md](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/CHANGELOG.md) for what changed in each release.

## License

MIT. See [LICENSE](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/dotnet/LICENSE).
