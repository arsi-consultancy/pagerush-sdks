# Changelog

All notable changes to `pagerush/pagerush-php` are documented here.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and this
package follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-11-16

### Added

- Batch renders as `$client->batches`: `create` (one template, 1-1,000 data records, from an array or
  any iterable; always sends an `Idempotency-Key`, generated when you don't pass one and reused across
  retries), `get` (with per-item `job_id`, `status`, `filename`, `bytes`, `renders` and `error`),
  `wait` (polls until `succeeded`, `partial` or `failed`; defaults 600 s and 2 s) and `downloadZip`
  (sends the API key and checks that the response is `application/zip`).
- Batch create is retried on connection errors, timeouts, 429, 502, 503 and 504, never on 409.
- `batch.completed` webhooks: `Webhook::verify()` checks the `X-PageRush-Batch-Id` header in place of
  `X-PageRush-Job-Id` for that event, and `constructEvent()` returns the batch ID and the body's
  `batch` object as `WebhookEvent::$batch_id` and `WebhookEvent::$batch`, with the batch's metadata.
- `WebhookEvent::BATCH_COMPLETED` constant and `Batch`, `BatchItem` and `BatchItemInput` array shapes
  in `PageRush\Types`.
- `examples/batch.php`: builds the items from a CSV file, waits for the batch and saves the ZIP.

### Changed

- `WebhookEvent::$job_id` is now `?string`: it is null on `batch.completed`. The webhook receiver
  example dedupes on `job_id ?? batch_id`.

## [0.1.0] - 2026-11-09

### Added

- `PageRush\PageRush` client with `api_key`, `base_url`, `timeout` and `max_retries` options, and
  `PAGERUSH_API_KEY` / `PAGERUSH_BASE_URL` environment fallbacks. The key is trimmed and checked, and
  options are type-checked, at construction.
- Templates: `list`, `listAll` (a generator that follows `next_cursor`), `create`, `get`, `rename`,
  `delete`, `createVersion`, `getVersion`, `publish` and `render`.
- `RenderResult` for PDF and queued (job) renders, with `save()`.
- Jobs: `get`, `wait` (polls until `succeeded` or `failed`) and `download` (the API key is not sent
  to the signed download URL).
- Automatic retries with `Retry-After` support and exponential backoff. Renders always carry an
  `Idempotency-Key` (the SDK creates one when you don't pass one) that is reused across retries, and
  are also retried on 409 `CONFLICT`.
- `PageRush\Webhook::verify()` and `constructEvent()` for signed `callback_url` deliveries. The
  signature covers the timestamp, the event, job ID and metadata headers, and the body. Both accept
  the usual header containers (`getallheaders()`, `$_SERVER`, Symfony/Laravel, PSR-7) and several
  secrets for rotation. Also available as `$client->webhooks`.
- `TemplateFile::fromContents()` and `TemplateFile::fromPath()` to build template file entries.
- `PageRush\ErrorCode` constants for the known error codes.
- Exceptions under `PageRush\Exception`, all implementing `PageRushExceptionInterface`:
  `PageRushException` (API errors), `ConnectionException` (no response or timeout),
  `WebhookVerificationException` (with a `reason`), `InvalidArgumentException` (code
  `VALIDATION_FAILED`, raised before any request), and `LogicException` / `RuntimeException`.
- A `Transport` interface with a curl implementation, so you can plug in another HTTP stack.

### Security

- The API key never appears in `var_dump()`, `print_r()`, `var_export()` or `json_encode()` output or
  in error messages, and the client refuses to be serialised.
- Signed download URLs appear in error messages without their query string.

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/compare/php-v0.2.0...HEAD
[0.2.0]: https://github.com/arsi-consultancy/pagerush-sdks/compare/php-v0.1.0...php-v0.2.0
[0.1.0]: https://github.com/arsi-consultancy/pagerush-sdks/releases/tag/php-v0.1.0
