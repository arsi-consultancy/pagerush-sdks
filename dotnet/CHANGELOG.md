# Changelog

All notable changes to the PageRush .NET SDK are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this package
follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `PageRushClient` for the `/v1` API, configured with `PageRushClientOptions` or the `PAGERUSH_API_KEY`
  and `PAGERUSH_BASE_URL` environment variables. Options: `ApiKey`, `BaseUrl`, `Timeout` (per attempt,
  default 60 s), `MaxRetries` (default 2), `HttpClient` and `Sleep`.
- `client.Templates`: `ListAsync`, `ListAllAsync` (an `IAsyncEnumerable<Template>` that follows
  `next_cursor`), `CreateAsync`, `GetAsync`, `RenameAsync`, `DeleteAsync`, `CreateVersionAsync`,
  `GetVersionAsync`, `PublishAsync` and `RenderAsync`.
- `RenderAsync` returns a `RenderResult` with the PDF and its response headers parsed (`JobId`,
  `TemplateVersion`, `Engine`, `PageCount`, `Warnings`, `RequestId`, `Filename`), or the queued `Job`.
  `RenderResult.SaveAsync(path)` writes the PDF and throws if the render was queued.
- An `Idempotency-Key` on every render and batch create: a random UUID v4 per call unless you set one
  (validated before sending), reused on every retry of that call.
- `PreferAsync` (`Prefer: respond-async`) and `CallbackUrl` support.
- `client.Jobs`: `GetAsync`, `WaitAsync` (polls until `succeeded` or `failed`; defaults 120 s, every 1 s)
  and `DownloadAsync` (through the signed `download_url`, without the API key).
- `client.Batches`: `CreateAsync` (1 to 10,000 items), `GetAsync`, `WaitAsync` (polls until
  `succeeded`, `partial` or `failed`; defaults 600 s, every 2 s) and `DownloadZipAsync` (with the API key;
  a 200 that is not `application/zip` throws `INVALID_RESPONSE`).
- Automatic retries with `Retry-After` support (capped at 30 seconds) and exponential backoff with
  jitter. Renders are also retried on 409 `CONFLICT`; `IDEMPOTENCY_CONFLICT` is never retried. Create,
  create-version, publish and rename are retried only on 429 and 503.
- Every method takes a `CancellationToken`, which also cancels a pending retry wait or poll.
- Redirects are followed by hand (up to 5), dropping the `Authorization` header when a redirect leaves
  the API's origin, which includes switching from https to http.
- Checks before anything is sent: the API key (trimmed; spaces, control characters and non-ASCII
  characters rejected without echoing the key), the base URL, the idempotency key, ids, and request
  bodies JSON can't represent (`VALIDATION_FAILED`, status 0).
- The API key is kept out of `ToString()`, JSON serialisation and error messages. Signed download links
  appear in errors without their query string.
- `PageRushException` (with `Status`, `Code`, `Title`, `Detail`, `Errors`, `RequestId`, `RetryAfter` and
  `Type`), `PageRushConnectionException` (`CONNECTION_ERROR`, `TIMEOUT`) and `ErrorCodes` constants.
- `Webhooks.Verify` and `Webhooks.ConstructEvent` for `X-PageRush-Signature` on `pdf.succeeded`,
  `pdf.failed` and `batch.completed` deliveries. They accept ASP.NET Core `Request.Headers`,
  `HttpHeaders`, `NameValueCollection` or a dictionary, several secrets for rotation, a timestamp
  tolerance, and compare signatures in constant time. `WebhookVerificationException` has a `Reason`.
- `TemplateFile.FromString`, `FromBytes` and `FromPath`, which pick `utf8` or `base64` by file extension.
- Targets .NET Standard 2.0 and .NET 10. Nullable annotations and XML docs on every public symbol.

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/commits/main/dotnet
