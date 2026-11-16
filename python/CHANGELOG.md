# Changelog

All notable changes to the `pagerush` Python SDK are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-11-16

### Added

- Batches: `client.batches.create` (`POST /v1/templates/{id}/batches`, 1-1,000 items of `{data, filename?}`, with
  `version`, `metadata`, `delete_after_download`, `callback_url` and an automatic `Idempotency-Key` reused across
  retries), `get`, `wait` (polls until `succeeded`, `partial` or `failed`; `WAIT_TIMEOUT` when it gives up) and
  `download_zip` (sends the API key; a 200 that is not `application/zip` raises `INVALID_RESPONSE`).
- `Batch`, `BatchItem`, `BatchItemInput` and `BatchStatus` types.
- Batch creation is retried on connection errors, timeouts, 429, 502, 503 and 504, never on `IDEMPOTENCY_CONFLICT`.
- Webhooks: `batch.completed` deliveries. `verify` reads `X-PageRush-Batch-Id` instead of `X-PageRush-Job-Id` for
  that event; `construct_event` returns `batch_id` and `batch` (with `job_id` set to `None`). New constants
  `BATCH_ID_HEADER` and `BATCH_COMPLETED`.
- `examples/batch.py`; the webhook receiver example handles `batch.completed`.

### Changed

- `WebhookEvent.job_id` is typed `Optional[str]`: it is `None` for `batch.completed`. `WebhookEvent` gains
  `batch_id` and `batch` fields (both default to `None`).

## [0.1.0] - 2026-11-09

### Added

- `PageRush` client for API `v1`: API key from the argument or `PAGERUSH_API_KEY`, base URL from the argument,
  `PAGERUSH_BASE_URL` or `https://pagerush.dev`, per-attempt `timeout`, `max_retries`, and injectable
  `transport` and `sleep`.
- Templates: `list`, `list_all` (generator over every page), `create`, `get`, `rename`, `delete`,
  `create_version`, `get_version`, `publish`, `render`.
- `RenderResult` with the PDF bytes and render headers (job id, template version, engine, page count,
  warnings, request id, file name), or the queued job for HTTP 202; `save(path)`.
- Jobs: `get`, `wait` (polls until `succeeded` or `failed`; `WAIT_TIMEOUT` when it gives up), `download` (fetches the signed link without the
  API key).
- Automatic `Idempotency-Key` (UUID v4) on every render, reused across retries.
- Retries with `Retry-After` support (capped at 30 seconds; unusable values fall back to backoff) and exponential
  backoff with jitter. Renders are also retried on 409 `CONFLICT`; create, create_version, publish and rename are
  retried only on 429 and 503.
- Input checks before sending: the API key is trimmed and rejected if it contains spaces or control characters,
  the idempotency key must match the server's rule, and a body that cannot be encoded as JSON raises
  `VALIDATION_FAILED`. Error messages and `repr` never include the API key or a signed URL's query string.
- The default transport drops the `Authorization` header on redirects to another host or from https to http.
- `PageRushError` with `status`, `code`, `title`, `detail`, `errors`, `request_id`, `retry_after`, `type`;
  `PageRushConnectionError` for network failures and timeouts; code `INVALID_RESPONSE` when a 2xx body
  cannot be used. All SDK exceptions can be pickled.
- Webhooks: `pagerush.webhooks.verify` and `construct_event` check the `X-PageRush-Signature` (`t=` and `v2=`
  HMAC-SHA256 over the timestamp, the event, job id and metadata headers, and the raw body), with constant-time
  comparison, several secrets for rotation and a timestamp tolerance. `WebhookEvent` and `WebhookVerificationError`
  (with `reason`). Empty secrets raise `ValueError`.
- `template_file` and `file_from_path` helpers that choose UTF-8 or base64 encoding (base64 for text files that
  are not valid UTF-8).
- Type hints throughout (PEP 561 `py.typed`), examples, and an offline test suite.

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/compare/python-v0.2.0...HEAD
[0.2.0]: https://github.com/arsi-consultancy/pagerush-sdks/compare/python-v0.1.0...python-v0.2.0
[0.1.0]: https://github.com/arsi-consultancy/pagerush-sdks/releases/tag/python-v0.1.0
