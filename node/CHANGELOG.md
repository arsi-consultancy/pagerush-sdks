# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this package
follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-09

### Added

- `PageRush` client for the `/v1` API, configured by options or the `PAGERUSH_API_KEY` and
  `PAGERUSH_BASE_URL` environment variables.
- Templates: `list`, `listAll` (async iterator over every page), `create`, `get`, `rename`, `delete`,
  `createVersion`, `getVersion`, `publish` and `render`.
- Rendering returns the PDF with its response headers parsed (`job_id`, `template_version`, `engine`,
  `page_count`, `warnings`, `request_id`, `filename`), or the queued job, and `save(path)` writes the PDF.
- An `Idempotency-Key` on every render: a random UUID per call unless you pass one, reused across retries.
- `preferAsync` (`Prefer: respond-async`) and `callback_url` support.
- Jobs: `get`, `wait` (polls until `succeeded` or `failed`) and `download` (through the signed
  `download_url`, without the API key).
- Automatic retries with `Retry-After` support (capped at 30 seconds) and exponential backoff with
  jitter. Renders are also retried on 409 `CONFLICT`; `IDEMPOTENCY_CONFLICT` is never retried.
- Per-attempt timeouts that cover reading the body, and per-call `signal`, `timeout` and `maxRetries`.
  Aborting also cancels any pending retry wait or `jobs.wait` poll and clears its timer.
- Redirects are followed by hand, dropping the `Authorization` header when a redirect leaves the API's
  origin or switches to plain http.
- Input checks before anything is sent: the API key (trimmed; spaces and control characters rejected
  without echoing the key), the base URL, `idempotencyKey`, and request bodies JSON can't represent
  (`VALIDATION_FAILED`).
- The API key is kept out of error messages, `console.log(client)` and `JSON.stringify(client)`. Signed
  download links appear in messages without their query string.
- `PageRushError`, `PageRushConnectionError` and `ErrorCodes`, with Typst diagnostics in `errors` and
  `request_id` for support. A 2xx response the SDK cannot use raises code `INVALID_RESPONSE`.
- Webhooks: `verify(rawBody, headers, secrets)` and `constructEvent` for `X-PageRush-Signature`. The
  signature covers the timestamp, the event, job id and metadata headers, and the raw body, so every
  field of a parsed event comes from signed data. Supports several secrets for rotation, a timestamp
  tolerance and constant-time comparison. A missing or empty secret throws a `TypeError`. Metadata
  that is not a JSON object of strings gives `metadata: null`; a `pdf.failed` body that is not a JSON
  object is rejected.
- `templateFile` and `fileFromPath` helpers that pick `utf8` or `base64` encoding by file extension.
- ESM and CommonJS builds with TypeScript declarations. Runs on Node.js 20+, Bun, Deno and Cloudflare
  Workers. The CommonJS build loads Node built-ins with `require`, so it also works under Jest.

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/compare/node-v0.1.0...HEAD
[0.1.0]: https://github.com/arsi-consultancy/pagerush-sdks/releases/tag/node-v0.1.0
