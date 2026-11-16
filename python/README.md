# PageRush Python SDK

The official Python client for the [PageRush](https://pagerush.dev) API. PageRush renders Typst templates to PDF.

- Python 3.9 or newer. No dependencies outside the standard library.
- Targets API version `v1`.
- Fully typed (PEP 561). API resources are plain `dict`s described by `TypedDict`s.

## Install

```sh
pip install pagerush
```

## Quickstart

Create a template, render it with data and save `invoice.pdf`:

```python
import pagerush

client = pagerush.PageRush()  # reads PAGERUSH_API_KEY

created = client.templates.create(
    name="Invoice",
    source='#let data = json("data.json")\n= Invoice #data.number\n\nBilled to: #data.customer',
)
template_id = created["template"]["id"]

result = client.templates.render(template_id, data={"number": "INV-001", "customer": "Example Ltd"})
result.save("invoice.pdf")
print(f"Saved invoice.pdf ({result.page_count} page(s), job {result.job_id})")
```

Run it with `PAGERUSH_API_KEY=pk_live_... python quickstart.py`. A template reads the render data with
`#let data = json("data.json")`.

## Usage

### Client configuration

```python
import pagerush

client = pagerush.PageRush(
    api_key="pk_live_...",  # default: env PAGERUSH_API_KEY
    base_url="https://pagerush.dev",  # default: env PAGERUSH_BASE_URL, else https://pagerush.dev
    timeout=60.0,  # seconds per HTTP attempt
    max_retries=2,  # retries after the first attempt
)
```

If no API key is passed or set in the environment, the constructor raises `PageRushError` with code
`MISSING_API_KEY`. Surrounding whitespace is trimmed; a key that still contains spaces or control characters is
rejected with code `VALIDATION_FAILED`, and the message does not include the key. The client holds no open
connections, so there is nothing to close.

### Templates

Create a template with extra files. Text files (`.typ .txt .csv .json .svg .xml .md .yml .yaml .bib`) are sent
as UTF-8; everything else (images, fonts) as base64. `template_file` and `file_from_path` pick the encoding:

```python
from pathlib import Path

import pagerush

client = pagerush.PageRush()

created = client.templates.create(
    name="Invoice",
    source='#import "parts/header.typ": header\n#let data = json("data.json")\n#header(data)\n#image("logo.png")',
    files=[
        pagerush.template_file("parts/header.typ", "#let header(d) = [= Invoice #d.number]"),
        pagerush.file_from_path(Path("assets/logo.png"), "logo.png"),
    ],
    sample_data={"number": "INV-001"},  # used by test renders in the dashboard
)
template = created["template"]
print(template["id"], template["live_version"])
```

The first version is published (live) straight away.

Add a version, publish it, roll back:

```python
# publish=False adds a draft; renders keep using the live version.
draft = client.templates.create_version(template["id"], source="...", publish=False)
version = draft["version"]["version"]

client.templates.render(template["id"], data={"number": "INV-002"}, version=version)  # test the draft
client.templates.publish(template["id"], version)  # make it live
client.templates.publish(template["id"], version - 1)  # roll back
```

`create_version` publishes by default (`publish` omitted means the server's default, `True`). It also
accepts `files`, `sample_data`, `document` and `typst_version`. A new version keeps the previous version's
Typst release unless you pass `typst_version`.

List, get, rename, delete:

```python
page = client.templates.list(limit=20)  # {"items": [...], "next_cursor": ...}
if page["next_cursor"]:
    page = client.templates.list(limit=20, cursor=page["next_cursor"])

for template in client.templates.list_all():  # generator; fetches pages as you iterate
    print(template["id"], template["name"])

template = client.templates.get("tpl_123")  # includes template["versions"], newest first
source = client.templates.get_version("tpl_123", 2)["source"]
client.templates.rename("tpl_123", "Invoice (2026)")
client.templates.delete("tpl_123")
```

### Rendering

A synchronous render returns the PDF:

```python
result = client.templates.render(
    "tpl_123",
    data={"number": "INV-001"},
    version=3,  # optional: render this version instead of the live one
    filename="invoice-001",  # optional: Content-Disposition file name
    metadata={"order_id": "42"},  # optional: up to 20 string pairs (4 KB), echoed on the job and webhook
    delete_after_download=True,  # optional: don't keep the PDF for 24 hours
    idempotency_key="order-42-pdf",  # optional: see "Retries and idempotency"
)
result.pdf  # bytes
result.job_id  # X-Job-Id
result.template_version, result.engine, result.page_count, result.warnings, result.request_id, result.filename
result.save("invoice.pdf")
```

**`prefer_async`.** With `prefer_async=True` the SDK sends `Prefer: respond-async`. This does not ask for an
async render: the API still renders straight away and returns the PDF. Only when that attempt hits a temporary
capacity problem does it queue the job and return HTTP 202 instead of failing with 503. So handle both outcomes,
and check `result.queued`:

```python
result = client.templates.render("tpl_123", data={"number": "INV-001"}, prefer_async=True)
if result.queued:
    job = client.jobs.wait(result.job_id, timeout=120, interval=1)  # polls until succeeded or failed
    if job["status"] == "succeeded":
        pdf = client.jobs.download(job["id"])  # bytes
    else:
        print(job["error"])
else:
    pdf = result.pdf
```

`jobs.wait` returns failed jobs rather than raising; it raises `PageRushError` (status 0) with code `WAIT_TIMEOUT` if the job is
not finished in time. `jobs.download` fetches the job, then the PDF from its signed `download_url` without
sending your API key. If the job has no `download_url` (not finished, expired after 24 hours, or deleted by
`delete_after_download`) it raises `PageRushError` with code `FILE_EXPIRED`.

**Async with `callback_url`.** The render is queued (`result.queued` is `True`) and PageRush POSTs the signed PDF
to your https URL when it is ready:

```python
result = client.templates.render(
    "tpl_123",
    data={"number": "INV-001"},
    callback_url="https://example.com/pagerush/webhook",
    metadata={"order_id": "42"},
)
print("queued", result.job_id)
```

### Batches

A batch renders one template once per item (1 to 1,000 items) in the background and collects the PDFs in one
ZIP. Each item is `{"data": ..., "filename": ...}`; `filename` is optional and names the PDF inside the ZIP.

```python
import pagerush

client = pagerush.PageRush()

records = [
    {"number": "INV-001", "customer": "Example Ltd"},
    {"number": "INV-002", "customer": "Sample GmbH"},
]
batch = client.batches.create(
    "tpl_123",
    items=[{"data": record, "filename": record["number"]} for record in records],
    metadata={"run": "2026-11"},  # optional, as for render; also version, delete_after_download, callback_url
)

batch = client.batches.wait(batch["id"], timeout=600, interval=2)  # polls until succeeded, partial or failed
print(batch["status"], batch["succeeded"], "succeeded,", batch["failed"], "failed")
for item in batch["items"]:
    if item["error"]:
        print(item["index"], item["error"]["code"], item["error"]["message"])

if batch["succeeded"]:
    with open("invoices.zip", "wb") as f:
        f.write(client.batches.download_zip(batch["id"]))
```

From a CSV file, turn each row into an item. `items` takes any iterable, so a generator works:

```python
import csv

with open("invoices.csv", newline="", encoding="utf-8") as f:
    batch = client.batches.create("tpl_123", items=({"data": row} for row in csv.DictReader(f)))
```

- `batches.create` returns the queued `Batch` (HTTP 202) without `items`. `batches.get` and `batches.wait` return
  it with `items`: `index`, `job_id`, `status`, `filename`, `bytes`, `renders`, `error`.
- `batches.wait` returns partial and failed batches rather than raising; it raises `PageRushError` (status 0) with
  code `WAIT_TIMEOUT` if the batch is not finished in time.
- `batches.download_zip` sends your API key (the batch's `zip_url` is an API URL, not a signed link). While the
  batch is still rendering it raises `PageRushError` with code `CONFLICT` (409, `retry_after` set); when no PDFs are
  left (expired, or deleted by `delete_after_download` after a complete download) it raises `FILE_EXPIRED` (410).
- `batches.create` sends an `Idempotency-Key` like `render` does, so a retry never creates a second batch. The
  server refuses a batch your quota and credits cannot cover (402 `QUOTA_EXCEEDED`) and a fourth unfinished batch
  (429 `CONCURRENCY_LIMIT`). The SDK does not split large batches.
- With `callback_url`, PageRush POSTs a signed `batch.completed` notice when the batch finishes (see
  [Webhooks](#webhooks)).

A complete program is in
[`examples/batch.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/batch.py).

### Webhooks

Each delivery carries `X-PageRush-Event` (`pdf.succeeded` with the PDF as the body, or `pdf.failed` with a JSON
body), `X-PageRush-Job-Id`, `X-PageRush-Metadata` (on `pdf.succeeded`, when the job has metadata) and
`X-PageRush-Signature`. The signature covers the timestamp, the event, job id and metadata headers, and the body, so
every field of a verified event can be trusted. A batch's `batch.completed` notice is signed the same way, with
`X-PageRush-Batch-Id` in place of `X-PageRush-Job-Id`; its JSON body is `{"event", "batch"}`. The webhook functions
need no API key, so use the `pagerush.webhooks` module directly:

```python
from pagerush import webhooks

event = webhooks.construct_event(raw_body, request_headers, secret)  # verifies first
event.event  # "pdf.succeeded" | "pdf.failed" | "batch.completed"
event.job_id  # None for batch.completed
event.pdf  # bytes (pdf.succeeded only)
event.failure  # parsed JSON body (pdf.failed only): {"error": {"code", "message"}, ...}
event.metadata  # dict of strings, or None
event.batch_id, event.batch  # batch.completed only: the batch id and the Batch (without items)

webhooks.verify(raw_body, request_headers, secret)  # only check the signature
```

- **Use the raw body.** Pass the exact bytes you received. If you parse the JSON and serialise it again, the
  bytes change and verification fails.
- **Pass the request headers as they are.** Any mapping or object with `.items()` works; names are matched
  case-insensitively.
- **Deduplicate on `job_id`** (on `batch_id` for `batch.completed`). Delivery is at-least-once, and the id is the
  same on every retry. Record a delivery as handled only after your work succeeded, so a failed attempt is
  delivered again.
- **Validate the id before using it in a file name**, for example with `^[A-Za-z0-9_-]{1,64}$`.
- **`batch.completed` carries no PDFs.** Download the ZIP with `client.batches.download_zip(event.batch_id)`, which
  needs your API key.
- **Pass a real secret.** An empty secret, or an empty list, raises `ValueError`. Check that your environment
  variable is set at startup.
- **Secret rotation.** Pass a list to accept either secret: `construct_event(body, headers, [new, old])`.
- Signatures older or newer than `tolerance` seconds (default 300) are rejected.
- `event.metadata` is `None` when the job or batch has no metadata or it is not a JSON object of strings.
- On failure both functions raise `WebhookVerificationError`, whose `reason` is `"header"`, `"timestamp"` or
  `"signature"`.

A complete Flask receiver (`pip install flask`):

```python
import os
import re

from flask import Flask, abort, request

from pagerush import WebhookVerificationError, webhooks

app = Flask(__name__)
SECRET = os.environ.get("PAGERUSH_WEBHOOK_SECRET", "")
if not SECRET:
    raise SystemExit("Set PAGERUSH_WEBHOOK_SECRET")
DELIVERY_ID = re.compile(r"[A-Za-z0-9_-]{1,64}")
handled = set()  # use your database in production


@app.post("/pagerush/webhook")
def pagerush_webhook():
    try:
        event = webhooks.construct_event(request.get_data(), request.headers, SECRET)
    except WebhookVerificationError as err:
        abort(400, f"invalid signature ({err.reason})")
    # batch.completed carries a batch id; the pdf.* events carry a job id.
    delivery_id = event.batch_id if event.event == "batch.completed" else event.job_id
    if delivery_id is None or not DELIVERY_ID.fullmatch(delivery_id):
        abort(400, "unexpected id")

    if delivery_id in handled:
        return "", 200

    if event.event == "pdf.succeeded":
        with open(f"{delivery_id}.pdf", "wb") as f:
            f.write(event.pdf)
    elif event.event == "pdf.failed":
        print("render failed", delivery_id, event.failure["error"])
    elif event.event == "batch.completed":
        print("batch", delivery_id, event.batch["status"], event.batch["succeeded"], "succeeded")

    # Record the delivery only after the work succeeded. If it raised, the 500 makes PageRush retry.
    handled.add(delivery_id)
    return "", 200
```

`request.get_data()` returns the raw bytes. In FastAPI or Starlette, use `raw_body = await request.body()` and
pass `request.headers`. A standard-library version (`http.server`) is in
[`examples/webhook_receiver.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/webhook_receiver.py).

Return a 2xx status quickly. PageRush retries other responses.

### Errors

Every non-2xx response raises `PageRushError`:

```python
import pagerush

try:
    client.templates.render("tpl_123", data={"number": 1})
except pagerush.PageRushConnectionError as err:  # network failure or timeout: code CONNECTION_ERROR / TIMEOUT
    print("could not reach PageRush:", err)
except pagerush.PageRushError as err:
    if err.code == "TEMPLATE_COMPILE_FAILED":
        for diagnostic in err.errors:  # Typst diagnostics
            print(diagnostic["path"], diagnostic["message"])
    elif err.code == "QUOTA_EXCEEDED":
        ...
    print(err.status, err.code, err.detail, "request id:", err.request_id)
```

| attribute | meaning |
|---|---|
| `status` | HTTP status (`0` when there was no response) |
| `code` | e.g. `VALIDATION_FAILED`, `INVALID_API_KEY`, `QUOTA_EXCEEDED`, `RATE_LIMITED`, `TEMPLATE_NOT_FOUND`, `TEMPLATE_NOT_PUBLISHED`, `INVALID_DATA`, `TEMPLATE_COMPILE_FAILED`, `RENDER_TIMEOUT`, `RENDER_UNAVAILABLE`, `FILE_EXPIRED`; `CONFLICT`, `IDEMPOTENCY_CONFLICT`, `URL_NOT_ALLOWED`, `INVALID_OUTPUT`, `UNAUTHENTICATED`, `FORBIDDEN`; `VALIDATION_FAILED` (status 0) for problems found before sending; `UNKNOWN` if an error body was not a problem document; `INVALID_RESPONSE` (with the real status) if a 2xx body could not be used, such as non-JSON, a missing wrapper, or a render 200 that is not `application/pdf`. New codes may appear, so compare strings. |
| `title`, `detail`, `type` | from the `application/problem+json` body |
| `errors` | list of `{"path", "message"}`; Typst diagnostics on `TEMPLATE_COMPILE_FAILED` |
| `request_id` | quote this when you contact support |
| `retry_after` | seconds from `Retry-After`, or `None` |

`str(err)` is `"<code>: <detail or title>"`.

### Retries and idempotency

The client retries failed attempts up to `max_retries` times (default 2):

| request | retried on |
|---|---|
| GET, DELETE | connection errors, timeouts, 429, 502, 503, 504 |
| render | the same, plus 409 with code `CONFLICT` (an earlier attempt with the same idempotency key is still rendering) |
| batches.create | connection errors, timeouts, 429, 502, 503, 504 (it always carries an idempotency key) |
| create, create_version, publish, rename | 429 and 503 only (the server rejected the request before doing anything) |

Before retry *n* the client waits `Retry-After` seconds when the response has a usable value for that header (at
most 30), else `min(8, 0.5 * 2^(n-1))` seconds with ±25 % jitter. `409 IDEMPOTENCY_CONFLICT` (the same key sent with
a different body) is never retried.

Every render sends an `Idempotency-Key` header. If you don't pass `idempotency_key`, the SDK generates a random
UUID and reuses it on retries of that call, so a retry never renders or charges twice. Pass your own key (for
example derived from an order id) to make retries across separate calls or processes safe too. A key must be 1-255
characters from `A-Z a-z 0-9 _ - : .`; any other key raises `PageRushError` with code `VALIDATION_FAILED` before
anything is sent.

With `delete_after_download=True` the PDF is not stored. If a response is lost and the render is retried with the
same key, the API cannot replay the PDF and returns 410 `FILE_EXPIRED`; render again with a new key.

A request body that cannot be encoded as JSON (for example `NaN`, `Infinity`, a `set` or `bytes`) raises
`PageRushError` with code `VALIDATION_FAILED`; nothing is sent.

`timeout` (default 60 seconds) applies to each attempt's connection and to each read of the response, so a
stalled connection fails with `TIMEOUT`.

Redirects are followed, but the `Authorization` header is dropped when a redirect leads to another host or from
https to http. Connection error messages leave out URL query strings, so signed download links are not exposed.

### Custom transport

`transport` takes any callable that accepts a `pagerush.HTTPRequest` and returns a `pagerush.HTTPResponse`. It is
useful for tests or for routing through your own HTTP stack:

```python
from pagerush import HTTPRequest, HTTPResponse, PageRush


def fake(request: HTTPRequest) -> HTTPResponse:
    return HTTPResponse(
        status=200, headers={"Content-Type": "application/json"}, body=b'{"items": [], "next_cursor": null}'
    )


client = PageRush(api_key="pk_test_123", transport=fake, sleep=lambda seconds: None)
```

A transport returns responses for every status code and raises `PageRushConnectionError` (or any `OSError`) when
the network fails. If nothing could be sent (an invalid URL or header value) it raises `ValueError`; the client
turns that into `PageRushError` with code `VALIDATION_FAILED` and drops the original message, which could
contain header values. `repr(HTTPRequest)` hides the `Authorization` header and the URL's query string.

## API reference

### `PageRush(api_key=None, *, base_url=None, timeout=60.0, max_retries=2, transport=None, sleep=None)`

| method | HTTP | returns |
|---|---|---|
| `templates.list(*, limit=None, cursor=None)` | `GET /v1/templates` | `TemplateList` (`items`, `next_cursor`) |
| `templates.list_all(*, limit=None)` | repeated `GET /v1/templates` | `Iterator[Template]` |
| `templates.create(*, name, source=None, document=None, files=None, sample_data=None, typst_version=None)` | `POST /v1/templates` | `TemplateWithVersion` (`template`, `version`) |
| `templates.get(template_id)` | `GET /v1/templates/{id}` | `Template` (with `versions`) |
| `templates.rename(template_id, name)` | `PATCH /v1/templates/{id}` | `Template` |
| `templates.delete(template_id)` | `DELETE /v1/templates/{id}` | `None` |
| `templates.create_version(template_id, *, source=None, document=None, files=None, sample_data=None, typst_version=None, publish=None)` | `POST /v1/templates/{id}/versions` | `TemplateWithVersion` |
| `templates.get_version(template_id, version)` | `GET /v1/templates/{id}/versions/{version}` | `TemplateVersion` |
| `templates.publish(template_id, version)` | `POST /v1/templates/{id}/publish` | `Template` |
| `templates.render(template_id, *, data, version=None, filename=None, metadata=None, delete_after_download=None, callback_url=None, idempotency_key=None, prefer_async=False)` | `POST /v1/templates/{id}/render` | `RenderResult` |
| `jobs.get(job_id)` | `GET /v1/jobs/{id}` | `Job` |
| `jobs.wait(job_id, *, timeout=120.0, interval=1.0)` | polls `jobs.get` | `Job` (status `succeeded` or `failed`) |
| `jobs.download(job_id)` | `jobs.get`, then `GET download_url` | `bytes` |
| `batches.create(template_id, *, items, version=None, metadata=None, delete_after_download=None, callback_url=None, idempotency_key=None)` | `POST /v1/templates/{id}/batches` | `Batch` (HTTP 202, without `items`) |
| `batches.get(batch_id)` | `GET /v1/batches/{id}` | `Batch` (with `items`) |
| `batches.wait(batch_id, *, timeout=600.0, interval=2.0)` | polls `batches.get` | `Batch` (status `succeeded`, `partial` or `failed`); `WAIT_TIMEOUT` when it gives up |
| `batches.download_zip(batch_id)` | `GET /v1/batches/{id}/zip` | `bytes` (ZIP); `CONFLICT` (409) while rendering, `FILE_EXPIRED` (410), `INVALID_RESPONSE` for a 200 that is not `application/zip` |
| `webhooks.verify(...)`, `webhooks.construct_event(...)` | none | see below |

All methods raise `PageRushError` (or `PageRushConnectionError`) on failure.

### Webhook functions (`pagerush.webhooks`, also `client.webhooks`)

- `verify(raw_body, headers, secret, *, tolerance=300, now=None) -> int`: returns the verified timestamp; raises
  `WebhookVerificationError`. `secret` may be a string or a list of strings; an empty secret or list raises
  `ValueError`.
- `construct_event(raw_body, headers, secret, *, tolerance=300, now=None) -> WebhookEvent`: verifies, then
  parses. `headers` is any mapping or object with `.items()`; names are matched case-insensitively. Raises
  `WebhookVerificationError` (reason `"header"` also when a `pdf.failed` body is not a JSON object, or a
  `batch.completed` body is not a JSON object with a `batch` object), or `ValueError` if the secret is empty.
- The id header is `X-PageRush-Batch-Id` when the event is `batch.completed` and `X-PageRush-Job-Id` otherwise;
  a `batch.completed` delivery with only a job id header fails with reason `"header"`.
- Constants: `SIGNATURE_HEADER`, `EVENT_HEADER`, `JOB_ID_HEADER`, `BATCH_ID_HEADER`, `METADATA_HEADER`,
  `BATCH_COMPLETED`, `DEFAULT_TOLERANCE`.

### Helpers

- `template_file(path, contents) -> TemplateFile`: `contents` is `str` or `bytes`.
- `file_from_path(local_path, target_path=None) -> TemplateFile`: `local_path` is a `str` or `os.PathLike`;
  `target_path` defaults to the file name.

### Types

- `RenderResult` (dataclass): `queued`, `pdf`, `job_id`, `template_version`, `engine`, `page_count`, `warnings`,
  `request_id`, `filename`, `job`; `save(path)` writes the PDF and raises `ValueError` if the render was queued.
- `WebhookEvent` (dataclass): `event`, `job_id` (`None` for `batch.completed`), `timestamp`, `pdf`, `failure`,
  `metadata`, `batch_id`, `batch`.
- `TypedDict`s: `Template`, `TemplateVersionSummary`, `TemplateVersion`, `TemplateFile`, `TemplateList`,
  `TemplateWithVersion`, `Job`, `JobError`, `JobDelivery`, `Batch`, `BatchItem`, `BatchItemInput`, `ErrorItem`.
- `JobStatus`, `BatchStatus`: the status strings.
- Exceptions: `PageRushError`, `PageRushConnectionError(PageRushError)`, `WebhookVerificationError`.
- Transport: `HTTPRequest`, `HTTPResponse`, `Transport`, `urllib_transport`.

## Examples

- [`examples/quickstart.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/quickstart.py): create, render, save.
- [`examples/async_render.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/async_render.py): `prefer_async`, `jobs.wait`, `jobs.download`.
- [`examples/batch.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/batch.py): a batch from CSV rows, `batches.wait`, `batches.download_zip`.
- [`examples/webhook_receiver.py`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/examples/webhook_receiver.py): standard-library webhook receiver, including `batch.completed`.

They read the key from `PAGERUSH_API_KEY`.

## Development

```sh
python3 -m venv .venv
.venv/bin/pip install -e '.[dev]'
.venv/bin/pytest
.venv/bin/mypy src examples
.venv/bin/ruff check . && .venv/bin/ruff format --check .
```

`tests/test_live.py` runs against the real API only when `PAGERUSH_API_KEY` is set. It creates a template,
renders it and deletes it.

## Versioning

This SDK follows [Semantic Versioning 2.0.0](https://semver.org/). While the version is `0.x`, a minor release
may contain breaking changes; from `1.0.0` on, breaking changes need a major release. The SDK version is
independent of the HTTP API version (`v1`). See [CHANGELOG.md](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/CHANGELOG.md).

## Licence

MIT. See [LICENSE](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/python/LICENSE).
