# Changelog

All notable changes to the PageRush Go SDK are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Go module versions come from git tags of the form `go/vX.Y.Z`.

## [Unreleased]

### Changed

- Docs: a batch takes up to 10,000 items (the server raised its limit from 1,000; the SDK never enforced it).

## [0.2.0] - 2026-11-16

### Added

- `Batches`: `Create` (renders up to 1,000 PDFs from one template in a single request; always sends
  an `Idempotency-Key`, a random UUID v4 unless you set one, reused on every retry), `Get` (the batch
  with each item's state), `Wait` (polls every 2 s for up to 10 minutes until the batch is
  `succeeded`, `partial` or `failed`) and `DownloadZip` (the ZIP of the rendered PDFs, sent with the
  API key; a 200 that isn't `application/zip` is an `INVALID_RESPONSE` error).
- Types `CreateBatchParams`, `BatchItemParams`, `Batch` and `BatchItem`, the `BatchStatus*`
  constants, and `DefaultBatchWaitTimeout` and `DefaultBatchWaitInterval`.
- Batch create is retried on connection errors, timeouts, 429, 502, 503 and 504, never on
  `IDEMPOTENCY_CONFLICT`.
- Webhooks: the `batch.completed` event (`EventBatchCompleted`). `VerifyWebhook` signs it with the
  `X-PageRush-Batch-Id` header (`HeaderWebhookBatchID`) in the job ID's place, and fails with reason
  `header` when that header is missing. `ConstructEvent` returns its `BatchID`, `Batch` and
  `Metadata`, with `JobID` empty.
- `examples/batch`: renders a slice of records or the rows of a CSV file as one batch and saves the
  ZIP.

### Changed

- `WaitOptions` is shared by `Jobs.Wait` and `Batches.Wait`; each has its own defaults.

## [0.1.0] - 2026-11-09

### Added

- `NewClient` with `WithBaseURL`, `WithHTTPClient`, `WithTimeout` and `WithMaxRetries` options.
  The API key falls back to `PAGERUSH_API_KEY` and the base URL to `PAGERUSH_BASE_URL`. The key is
  trimmed and checked for spaces and control characters, never appears in error messages, and is
  redacted when a `Client` is printed. Redirects never carry the `Authorization` header to another
  host or from https to http.
- `Templates`: `List`, `ListAll` (a `TemplateIterator` that follows `next_cursor`), `Create`, `Get`,
  `Rename`, `Delete`, `CreateVersion`, `GetVersion`, `Publish` and `Render`.
- `Render` returns a `RenderResult` with the PDF and its response headers, or a queued `Job`.
  It always sends an `Idempotency-Key` (a random UUID v4 unless you set one; your own key is
  validated before sending) and supports `PreferAsync` (`Prefer: respond-async`).
  `RenderResult.WriteFile` saves the PDF.
- `Jobs`: `Get`, `Wait` (polls until the job succeeds or fails) and `Download` (fetches the signed
  `download_url` without the Authorization header, and keeps its query string out of errors).
- `NewTemplateFile` and `TemplateFileFromPath` to build template files with the right encoding and
  forward-slash paths.
- `*Error` for API errors, connection errors (`CONNECTION_ERROR`), timeouts (`TIMEOUT`),
  `Jobs.Wait` timeouts (`WAIT_TIMEOUT`), unusable 2xx responses (`INVALID_RESPONSE`) and problems
  found before sending (`VALIDATION_FAILED`, status 0), with `Code*` constants for the API's error
  codes.
- Automatic retries with `Retry-After` support and exponential backoff with jitter; only safe
  failures are retried for non-idempotent requests, and renders are also retried on 409 `CONFLICT`.
  If the context ends during a retry wait, the error that caused the retry is returned.
- `VerifyWebhook` and `ConstructEvent` to verify and parse `callback_url` deliveries from the request
  body and headers. The signature (`t=...,v2=...`) covers the body and the event, job ID and metadata
  headers. Options `WithWebhookTolerance` and `WithWebhookNow`; `*WebhookVerificationError` with a
  `Reason`; several secrets accepted during a rotation; `ErrNoWebhookSecret` for a missing or empty
  secret.

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/compare/go/v0.2.0...HEAD
[0.2.0]: https://github.com/arsi-consultancy/pagerush-sdks/compare/go/v0.1.0...go/v0.2.0
[0.1.0]: https://github.com/arsi-consultancy/pagerush-sdks/releases/tag/go/v0.1.0
