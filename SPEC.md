# PageRush SDK contract

Every SDK in this repo (node, python, php, go) implements this contract. Behaviour must match
across languages; only naming follows each language's conventions. The source of truth for the
HTTP API is the server's OpenAPI document at `https://pagerush.dev/v1/openapi.json`. If this file and the server disagree, the server wins and this
file gets fixed.

Version for the first release: **0.1.0**. Licence: MIT, "Copyright (c) 2026 Arsi Consultancy".
No runtime dependencies beyond the language's standard library (Node: built-in `fetch` and Web Crypto;
PHP: ext-curl, ext-json; Python: stdlib; Go: stdlib).

## Client

Constructor options:

| option | default | notes |
|---|---|---|
| api key | required | Project key (`pk_live_...`). Falls back to env `PAGERUSH_API_KEY` if not passed. Missing → error at construction. |
| base URL | `https://pagerush.dev` | Env `PAGERUSH_BASE_URL` overrides the default. Strip trailing `/`. |
| timeout | 60 s | Per HTTP attempt. |
| max retries | 2 | Retries after the first attempt (so up to 3 attempts). |
| transport | language default | Injectable for tests (Node: `fetch` option; Python: an opener/transport callable; PHP: a callable or interface; Go: `*http.Client`). |

Every request sends:
- `Authorization: Bearer <key>`
- `User-Agent: pagerush-<lang>/<sdk version>` (e.g. `pagerush-node/0.1.0`)
- `Accept: application/json` (render: `Accept: application/pdf, application/json`)
- `Content-Type: application/json` when there is a body.

Request and response bodies use the API's own **snake_case field names** in every language (Go:
struct fields with `json:"snake_case"` tags; Node: TypeScript interfaces with snake_case keys). Only
SDK-level options (idempotency key, prefer async, timeouts) use idiomatic names. Don't rename API
fields; that avoids a mapping layer that can drift from the server.

## Methods

| SDK method (Node naming) | HTTP | returns |
|---|---|---|
| `templates.list({ limit?, cursor? })` | `GET /v1/templates?limit=&cursor=` | `{ items: Template[], next_cursor: string \| null }` |
| `templates.listAll({ limit? })` | repeated list | async iterator / generator over every Template, following `next_cursor` until null |
| `templates.create({ name, source? , document?, files?, sample_data?, typst_version? })` | `POST /v1/templates` | `{ template, version }` |
| `templates.get(id)` | `GET /v1/templates/{id}` | `Template` (unwrap `.template`) |
| `templates.rename(id, name)` | `PATCH /v1/templates/{id}` body `{name}` | `Template` (unwrap) |
| `templates.delete(id)` | `DELETE /v1/templates/{id}` | nothing (204) |
| `templates.createVersion(id, { source?, document?, files?, sample_data?, typst_version?, publish? })` | `POST /v1/templates/{id}/versions` | `{ template, version }` |
| `templates.getVersion(id, version)` | `GET /v1/templates/{id}/versions/{version}` | `TemplateVersion` (unwrap `.version`) |
| `templates.publish(id, version)` | `POST /v1/templates/{id}/publish` body `{version}` | `Template` (unwrap) |
| `templates.render(id, options)` | `POST /v1/templates/{id}/render` | `RenderResult` (below) |
| `jobs.get(id)` | `GET /v1/jobs/{id}` | `Job` |
| `jobs.wait(id, { timeout = 120 s, interval = 1 s })` | polls `jobs.get` | `Job` once status is `succeeded` or `failed` (a failed job is returned, not raised); timeout → `PageRushError` status 0, code `WAIT_TIMEOUT` |
| `jobs.download(id)` | `jobs.get`, then GET `download_url` | PDF bytes. `download_url` null → `PageRushError` code `FILE_EXPIRED`. Do **not** send the Authorization header to `download_url` (it is a signed link). |

Path segments (`id`) must be URL-encoded.

Helper: `templateFile(path, contents)` / `fileFromPath(localPath, targetPath?)` builds a
`{ path, content, encoding }` entry: utf8 for `.typ .txt .csv .json .svg .xml .md .yml .yaml .bib`,
base64 for everything else (images, fonts). Name it idiomatically per language.

### Render

Options:

| option | wire |
|---|---|
| `data` (required, any JSON) | body `data` |
| `version` | body `version` |
| `filename` | body `filename` |
| `metadata` (string→string map) | body `metadata` |
| `delete_after_download` | body `delete_after_download` |
| `callback_url` | body `callback_url` |
| `idempotencyKey` | header `Idempotency-Key`. **If not given, the SDK generates a random UUID v4** and reuses it across retries of this call, so retries never render or charge twice. |
| `preferAsync` (bool) | header `Prefer: respond-async`. This does **not** ask for an async render: the server still renders immediately and only returns 202 with a queued job when that attempt hits a temporary capacity problem (instead of a 503). Only `callback_url` always queues. Code that sets it must handle both a PDF and a queued job. |

Response handling:
- **200** `application/pdf`: `RenderResult { queued: false, pdf: bytes, job_id (X-Job-Id), template_version (X-Template-Version, int), engine (X-Render-Engine), page_count (X-Page-Count, int or null), warnings (X-Template-Warnings, int or null), request_id (X-Request-Id or null), filename (from Content-Disposition filename="...", or null) }`.
- **202** JSON Job: `RenderResult { queued: true, pdf: null, job: Job, job_id: job.id, template_version: job.template_version, ... others null }`.
- A convenience `save(path)` / `saveTo` / `WriteFile` on the result where idiomatic (errors if queued).

## Errors

All non-2xx responses raise/return a `PageRushError` (Go: `*pagerush.Error`, usable with `errors.As`) with:
`status` (int), `code` (string from body; `UNKNOWN` if an error body isn't problem+json; `INVALID_RESPONSE` with the real status when a 2xx body can't be used, e.g. non-JSON, missing wrapper, or a 200 render that isn't `application/pdf`), `title`, `detail`,
`errors` (list of `{path, message}`, may be empty — on TEMPLATE_COMPILE_FAILED these are Typst diagnostics),
`request_id` (body `request_id`, else header `X-Request-Id`), `retry_after` (seconds from `Retry-After`, or null),
`type`. Message: `"<code>: <detail or title>"` (fall back to `HTTP <status>`).

Connection failures / timeouts: a `PageRushConnectionError` (subclass of `PageRushError` where the language
allows; status 0, code `CONNECTION_ERROR`; timeouts code `TIMEOUT`).

Error bodies are `application/problem+json`: `{ type, title, status, code, detail?, errors?, request_id? }`.
Known codes include: CONFLICT, UNAUTHENTICATED, FORBIDDEN, URL_NOT_ALLOWED, INVALID_OUTPUT, VALIDATION_FAILED, INVALID_API_KEY, PROJECT_DISABLED, QUOTA_EXCEEDED, CONCURRENCY_LIMIT,
RATE_LIMITED, IDEMPOTENCY_CONFLICT, REQUEST_TOO_LARGE, TEMPLATE_NOT_FOUND, TEMPLATE_NOT_PUBLISHED, INVALID_DATA,
TEMPLATE_COMPILE_FAILED, RENDER_TIMEOUT, RENDER_FAILED, RENDER_UNAVAILABLE, FILE_EXPIRED, NOT_FOUND, INTERNAL.
Don't hard-code an enum that rejects unknown codes; expose them as strings (constants are fine).

## Retries

Retry when **all** of these hold:
1. attempts so far ≤ max retries, and
2. the failure is retryable for the method:
   - GET and DELETE: connection error/timeout, 429, 502, 503, 504.
   - render POST (always carries an idempotency key): connection error/timeout, 429, 502, 503, 504, and **409 with code
     `CONFLICT`** (the server is still rendering the earlier attempt with the same key and sends `Retry-After: 2`).
     Never retry `IDEMPOTENCY_CONFLICT`.
   - other POST / PATCH (create, createVersion, publish, rename): **only 429 and 503** (the server
     rejected them before doing anything). Never retry these on connection errors or 502/504.

Delay before retry n (n = 1, 2, …): `Retry-After` seconds if the header is present (cap at 30 s); otherwise
`min(8, 0.5 * 2^(n-1))` seconds with ±25 % jitter. Make sleep injectable or the base delay configurable so
tests run fast.

## Input validation and secrets

- API key: trim surrounding whitespace; reject one containing control characters or spaces at construction, with an
  error message that does **not** include the key.
- Idempotency key: validate against the server's rule `^[A-Za-z0-9_\-:.]{1,255}$` before sending; a bad one is a
  validation error (code `VALIDATION_FAILED`, status 0, no request, no retry).
- A request body that can't be encoded as JSON (NaN/Infinity, invalid UTF-8, unsupported types) raises the SDK's own
  validation error (code `VALIDATION_FAILED`, status 0), never a raw language exception.
- Errors raised before a request leaves the machine (bad header values, encoding) are never reported or retried as
  connection errors.
- The API key never appears in string/debug/serialised output of any SDK object (repr, util.inspect, JSON.stringify,
  var_dump, `%+v`) or in error messages. Signed `download_url`s appear in error messages without their query string.
- `Retry-After` values that are huge, infinite or unparsable never crash: unparsable → backoff; huge → capped at 30 s.
- Redirects: never forward the Authorization header to another host or from https to http.
- Cancelling a call (abort signal / context) also cancels any pending retry or poll sleep; no timers are left running.

## Webhooks (callback_url deliveries)

The server POSTs to `callback_url` with headers:
- `X-PageRush-Event`: `pdf.succeeded` (body = the PDF, `Content-Type: application/pdf`) or `pdf.failed`
  (body = JSON `{event, job_id, template_id, error:{code,message}, metadata, created_at}`, UTF-8)
- `X-PageRush-Job-Id` (same on every retry; delivery is at-least-once, so dedupe on it)
- `X-PageRush-Timestamp` (informational; the signed timestamp is the `t` in the signature header)
- `X-PageRush-Metadata` (`pdf.succeeded` only, when the job has metadata): a JSON object of strings, written in
  ASCII only (non-ASCII characters are `\uXXXX` escapes), so it parses back to the exact values that were sent.
- `X-PageRush-Signature: t=<unix seconds>,v2=<hex>[,v2=<hex>]`, where hex = lowercase HMAC-SHA256(webhook_secret, signed)
  and signed = `"<t>\n<X-PageRush-Event>\n<X-PageRush-Job-Id>\n<X-PageRush-Metadata, or empty if absent>\n"` followed by
  the raw body bytes. The event, job id and metadata headers are therefore covered as well as the body. During a
  secret rotation there are two v2 values (new secret first).

SDK functions (receivers need no API key, so these work without a client):
- `webhooks.verify(rawBody, headers, secret | secrets[], { tolerance = 300 s, now? })`:
  read the four headers case-insensitively (`headers` accepts the language's usual header containers);
  parse the signature header as comma-separated `key=value` pairs (trim whitespace around each pair; ignore unknown keys
  such as an old `v1`); need exactly one `t` of 1–15 digits and at least one `v2`, and the event and job id headers must
  be present, else reason `header`; `|now - t| > tolerance` → reason `timestamp`; compute the HMAC for each secret and
  compare against each v2 **in constant time** (exact comparison of lowercase hex, so uppercase input fails); none match
  → reason `signature`. On failure raise `WebhookVerificationError` with a `reason` (`header` | `timestamp` |
  `signature`). On success return normally (Go: nil error).
- `webhooks.constructEvent(rawBody, headers, secret(s), opts)`: verifies, then returns
  `{ event, job_id, timestamp, pdf (bytes, succeeded only), failure (parsed JSON object, failed only), metadata }`.
  Every field comes from verified data: `event` and `job_id` from their (signed) headers, `timestamp` = `t`.
  `metadata`: from `X-PageRush-Metadata` on `pdf.succeeded`, from the body on `pdf.failed`; null if absent or not a JSON
  object of strings. A `pdf.failed` body that isn't a JSON object → reason `header`. An unknown event is returned with
  `pdf` and `failure` null.

Additional webhook rules:
- Secrets: a missing/empty list or an empty-string secret is a programming error (argument/value error with a clear
  message, not a verification failure). Docs and examples check that the secret env var is set.
- Docs explain the signed fields in one sentence and show a receiver; they don't ask users to hand-roll verification.
- Receiver examples dedupe on `job_id`, record it as processed only after the work succeeded (so a failed attempt is
  retried), and validate it (`^[A-Za-z0-9_-]{1,64}$`) before using it in a file name.
- Raw body means the exact bytes received; READMEs warn against re-serialising parsed JSON.

Test vectors: the canonical file is `testdata/webhooks.json` at the repo root, generated by
`testdata/generate-webhooks.mjs`. Each case has `body_base64`, `headers` (signature, event, job id and, when present,
metadata), `secrets`, `now`, `tolerance_seconds`, `valid` and, for failures, `reason`. Each SDK keeps an identical copy
inside its own directory (node `test/fixtures/webhooks.json`, python `tests/fixtures/webhooks.json`, php
`tests/fixtures/webhooks.json`, go `testdata/webhooks.json`) so tests also work from a published package or a split repo.
`make sync-testdata` copies it and `make check-testdata` (run in CI) fails if a copy differs. Every SDK must pass every
case by calling its public `verify` with the case's headers.

## Tests

Each SDK ships unit tests that run offline against an injected/mock transport or a local HTTP server:
auth header and user agent; each method's path, method and body; list pagination (`listAll` across 2 pages);
render 200 (headers parsed into the result), render 202; idempotency key generated and identical across a
retry; retry on 503 with `Retry-After`; no retry of `create` on 502; no retry after max retries; error mapping
(problem+json, non-JSON body, request_id fallback to header); jobs.wait; jobs.download without auth header;
the webhook vectors. An optional live smoke test runs only when `PAGERUSH_API_KEY` is set
(create a template with `#let data = json("data.json")\nHello #data.name`, render it, check `%PDF`, delete it).

## Per-SDK README

Install, quickstart (create template, render, save PDF), async with callback + webhook verification example
(Express / Flask / plain PHP / net/http), error handling, retries/idempotency, configuration, licence.
Plain, accurate copy: claim only what the code does.

## Versioning

- **Semantic Versioning 2.0.0**, independently per SDK. Start at `0.1.0`. While `0.x`, a minor bump may break;
  from `1.0.0`, breaking changes need a major bump. The SDK version is separate from the HTTP API version (`/v1`).
- **One source of truth per SDK** for its version, and a test that fails if they drift:
  - node: `package.json` `version`; `src/version.ts` exports `VERSION` (test compares them).
  - python: `src/pagerush/_version.py` `__version__`; `pyproject.toml` reads it dynamically (hatch `[tool.hatch.version]`).
  - php: `src/Version.php` `const VERSION` (Composer takes versions from git tags, so no `version` in composer.json).
  - go: `version.go` `const Version` (Go takes versions from git tags).
  The User-Agent uses that constant.
- **Git tags** (monorepo): `node-v0.1.0`, `python-v0.1.0`, `php-v0.1.0`, and for Go the module-path tag
  `go/v0.1.0` (required by the Go toolchain for a module in the `go/` subdirectory).
- **CHANGELOG.md** in each SDK directory, in Keep a Changelog 1.1.0 format, with an `## [Unreleased]` section
  and `## [0.1.0] - 2026-11-09` listing what this release contains (Added: …).
- Each README states: supported language/runtime versions, the API version targeted (`v1`), and the versioning policy.
- Root `RELEASING.md` covers bump → changelog → tag → publish.

## Documentation (developer-friendly is a requirement, not a nice-to-have)

Each SDK directory contains:
1. `README.md`, ordered for someone in a hurry:
   - one-line description + badges placeholders are NOT needed; keep it plain
   - **Install** (one command)
   - **Quickstart**: a complete copy-paste program (imports included) that creates a template, renders it with data,
     and saves `invoice.pdf`. It must actually run against the API as written (given a key).
   - **Usage** sections with a short code sample each: client configuration; templates (create with extra files and
     images, new version, publish/rollback, list with pagination, get, rename, delete); rendering (sync, choosing a
     version, filename, metadata, delete_after_download, idempotency keys, `preferAsync` + `jobs.wait` + `jobs.download`,
     `callback_url`); webhooks (verify + a full receiver for the language's common framework, with the "use the raw
     body" warning and dedupe-by-job-id note); errors (catching by code, printing Typst diagnostics from `errors`,
     `request_id` for support); retries and timeouts.
   - **API reference**: a table or list of every public method/type with parameters, return value and errors.
   - Supported versions, versioning policy, link to CHANGELOG, licence.
2. `examples/` with small runnable programs: `quickstart`, `async-render` (preferAsync, handling both a PDF and a queued job with jobs.wait/jobs.download), `webhook-receiver`.
   They read the key from `PAGERUSH_API_KEY`. Each must at least compile/type-check/lint in the SDK's tests or CI script.
3. Doc comments on every public symbol (TSDoc / Python docstrings / PHPDoc / Go doc comments), so editors show help
   inline and generated docs (TypeDoc, pkg.go.dev, etc.) are useful. Include a short example in the main client's doc comment.
4. `CHANGELOG.md`, `LICENSE` (MIT, Copyright (c) 2026 Arsi Consultancy).

Writing style: short sentences, active voice, real working code, no marketing claims, no features the code doesn't have.

## Language conventions

Behaviour is shared; style is not. Each SDK should read like a well-made library in its own language.

- **node**: ESM-first with a CJS build, named exports only, `Error` subclasses with `name` set, options objects,
  `for await` pagination, `Uint8Array` in public types, strict TypeScript, Prettier.
- **python**: PEP 8 naming, keyword-only optional args, PEP 561 `py.typed`, generators, exceptions named `...Error`,
  `os.PathLike` paths, ruff (lint + format), mypy --strict.
- **php**: PSR-12, PSR-4, `declare(strict_types=1)`, `Exception` sub-namespace with a marker interface,
  readonly promoted properties, PHPStan, a code-style fixer.
- **go**: Effective Go naming (`TemplateID`, `CallbackURL`), no stutter, functional options, `context.Context`
  first, errors not panics, `Example` functions, gofmt, go vet, staticcheck.

API payload fields stay snake_case everywhere (Go maps them with struct tags), as the Stripe SDKs do.
