# Changelog

All notable changes to the `pagerush` Python SDK are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/arsi-consultancy/pagerush-sdks/compare/python-v0.1.0...HEAD
[0.1.0]: https://github.com/arsi-consultancy/pagerush-sdks/releases/tag/python-v0.1.0
