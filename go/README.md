# PageRush Go SDK

The official Go client for [PageRush](https://pagerush.dev): render PDFs from Typst templates and JSON data.

- Go 1.21 or newer. Standard library only, no other dependencies.
- Targets the PageRush HTTP API `v1`.
- Reference docs: [pkg.go.dev/github.com/arsi-consultancy/pagerush-sdks/go](https://pkg.go.dev/github.com/arsi-consultancy/pagerush-sdks/go)

## Install

```sh
go get github.com/arsi-consultancy/pagerush-sdks/go@latest
```

The module lives in the `go/` directory of the repository, so its import path ends in `/go`.
Its package name is `pagerush`; import it with an explicit alias so the name is clear in your code:

```go
import pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
```

## Quickstart

Set `PAGERUSH_API_KEY` to a project API key (`pk_live_...`), then run:

```go
package main

import (
	"context"
	"fmt"
	"log"

	pagerush "github.com/arsi-consultancy/pagerush-sdks/go"
)

const source = `#let data = json("data.json")
= Invoice #data.number

Bill to: #data.customer

#table(
  columns: (1fr, auto),
  [*Item*], [*Amount*],
  ..data.items.map(i => ([#i.name], [#i.amount])).flatten(),
)`

func main() {
	client, err := pagerush.NewClient("") // reads PAGERUSH_API_KEY
	if err != nil {
		log.Fatal(err)
	}
	ctx := context.Background()

	created, err := client.Templates.Create(ctx, &pagerush.CreateTemplateParams{
		Name:   "Invoice",
		Source: pagerush.String(source),
	})
	if err != nil {
		log.Fatal(err)
	}

	res, err := client.Templates.Render(ctx, created.Template.ID, &pagerush.RenderParams{
		Data: map[string]any{
			"number":   "INV-001",
			"customer": "Example Ltd",
			"items": []map[string]string{
				{"name": "Design", "amount": "$400"},
				{"name": "Hosting", "amount": "$20"},
			},
		},
	})
	if err != nil {
		log.Fatal(err)
	}
	if err := res.WriteFile("invoice.pdf"); err != nil {
		log.Fatal(err)
	}
	fmt.Printf("Saved invoice.pdf (template %s, job %s)\n", created.Template.ID, res.JobID)
}
```

A template reads the render's `data` with `#let data = json("data.json")`.
This program is also in [`examples/quickstart`](examples/quickstart).

## Usage

Every method takes a `context.Context` first. Cancelling the context stops the request and any retries.

Request and response structs use the API's own field names in their JSON tags (`live_version`,
`callback_url`, ...), so they match the [API reference](https://pagerush.dev/v1/openapi.json).
Optional request fields are pointers; set them with `pagerush.String`, `pagerush.Int` and `pagerush.Bool`.

### Client configuration

```go
client, err := pagerush.NewClient("pk_live_...",
	pagerush.WithBaseURL("https://pagerush.dev"),   // default; or set PAGERUSH_BASE_URL
	pagerush.WithTimeout(30*time.Second),           // per HTTP attempt; default 60 s
	pagerush.WithMaxRetries(3),                     // retries after the first attempt; default 2
	pagerush.WithHTTPClient(&http.Client{Transport: myTransport}),
)
```

| Option | Default | Notes |
|---|---|---|
| API key (first argument) | `PAGERUSH_API_KEY` | Surrounding whitespace is trimmed. `NewClient` returns an error if both are empty or the key contains spaces or control characters. |
| `WithBaseURL` | `PAGERUSH_BASE_URL`, else `https://pagerush.dev` | Must be an absolute `http` or `https` URL. A trailing `/` is removed. |
| `WithTimeout` | 60 s | Applies to each attempt separately. Zero or less disables it. |
| `WithMaxRetries` | 2 | Up to 3 attempts in total. |
| `WithHTTPClient` | `http.DefaultClient` | Use it for proxies, custom transports or tests. |

A `Client` is safe for concurrent use. Create one and reuse it.

The API key is never included in error messages, and printing a `Client` (`%v`, `%+v`, `%#v`) shows
it as `[redacted]`. On a redirect, the `Authorization` header is dropped if the target is another
host or goes from `https` to `http`. Your `http.Client` is copied, not modified, and its own
`CheckRedirect` still applies.

### Templates

Create a template with extra files. Text files (`.typ`, `.csv`, `.json`, `.svg`, ...) are sent as UTF-8;
everything else (images, fonts) as base64. `NewTemplateFile` and `TemplateFileFromPath` choose for you.

```go
logo, err := pagerush.TemplateFileFromPath("assets/logo.png", "logo.png")
if err != nil {
	log.Fatal(err)
}
created, err := client.Templates.Create(ctx, &pagerush.CreateTemplateParams{
	Name: "Invoice",
	Source: pagerush.String(`#import "parts/header.typ": header
#let data = json("data.json")
#header(data)
#image("logo.png", width: 3cm)`),
	Files: []pagerush.TemplateFile{
		pagerush.NewTemplateFile("parts/header.typ", []byte(`#let header(d) = [= Invoice #d.number]`)),
		logo,
	},
	SampleData: map[string]any{"number": "INV-001"}, // for test renders in the dashboard
})
```

Version 1 of a new template goes live at once. Add a version (live by default), or keep it as a draft:

```go
v, err := client.Templates.CreateVersion(ctx, templateID, &pagerush.CreateVersionParams{
	Source:  pagerush.String(newSource),
	Publish: pagerush.Bool(false), // keep the current live version
})
fmt.Println("draft version", v.Version.Version)
```

Publish a version, or roll back by publishing an older one:

```go
tpl, err := client.Templates.Publish(ctx, templateID, 3)
fmt.Println("live version:", *tpl.LiveVersion)
```

List templates a page at a time, or iterate over all of them:

```go
page, err := client.Templates.List(ctx, &pagerush.ListTemplatesParams{Limit: pagerush.Int(20)})
// page.Items, and page.NextCursor (nil on the last page) for the next call's Cursor.

it := client.Templates.ListAll(ctx, nil)
for it.Next() {
	fmt.Println(it.Template().ID, it.Template().Name)
}
if err := it.Err(); err != nil {
	log.Fatal(err)
}
```

Get, rename and delete:

```go
tpl, err := client.Templates.Get(ctx, templateID)       // includes tpl.Versions
ver, err := client.Templates.GetVersion(ctx, templateID, 2) // source, files, sample data
tpl, err = client.Templates.Rename(ctx, templateID, "Invoice (EU)")
err = client.Templates.Delete(ctx, templateID)
```

### Rendering

`Data` is any value that encodes to JSON: a map, a struct, or `json.RawMessage`.

```go
res, err := client.Templates.Render(ctx, templateID, &pagerush.RenderParams{
	Data:                order,                                // any JSON value
	Version:             pagerush.Int(4),                      // render a specific version instead of the live one
	Filename:            pagerush.String("invoice-42.pdf"),    // Content-Disposition filename
	Metadata:            map[string]string{"order_id": "42"},  // echoed on the job and the callback
	DeleteAfterDownload: pagerush.Bool(true),                  // don't keep the PDF for 24 hours
	IdempotencyKey:      "order-42-invoice",                   // optional; see below
})
if err != nil {
	log.Fatal(err)
}
fmt.Println(res.JobID, res.TemplateVersion, res.Engine, res.Filename)
if res.PageCount != nil {
	fmt.Println("pages:", *res.PageCount)
}
err = res.WriteFile("invoice-42.pdf") // or use res.PDF ([]byte)
```

**Idempotency keys.** Every render sends an `Idempotency-Key` header. If you don't set one, the SDK
generates a random UUID v4 for the call and reuses it on each retry, so a retry never renders or
charges twice. Set your own key (1-255 characters: letters, digits, `_ - : .`) to make repeated calls
from your side safe too, for example after your process restarts. Any other key is rejected with
`VALIDATION_FAILED` before anything is sent.

**`PreferAsync`.** This does not ask for an async render. The API still renders right away and
returns the PDF as usual. Only if that attempt hits a temporary capacity problem does it queue the
job and return it (202) instead of failing with a 503. So code that sets `PreferAsync` must handle
both outcomes: check `res.Queued`.

```go
res, err := client.Templates.Render(ctx, templateID, &pagerush.RenderParams{
	Data:        order,
	PreferAsync: true,
})
if err != nil {
	log.Fatal(err)
}
pdf := res.PDF
if res.Queued {
	job, err := client.Jobs.Wait(ctx, res.JobID, nil) // polls every 1 s for up to 120 s
	if err != nil {
		log.Fatal(err)
	}
	if job.Status == pagerush.JobStatusFailed {
		log.Fatalf("%s: %s", job.Error.Code, job.Error.Message)
	}
	pdf, err = client.Jobs.Download(ctx, res.JobID)
	if err != nil {
		log.Fatal(err)
	}
}
```

`Jobs.Wait` returns the job once it has succeeded or failed; a failed job is not an error.
If the job is still running after `WaitOptions.Timeout`, it returns an `*Error` with status 0 and
code `WAIT_TIMEOUT`.
`Jobs.Download` fetches the job's signed `download_url` (it does not send your API key there).
If the job has no `download_url` (not finished, failed, expired after 24 hours, or deleted after
download), it returns an `*Error` with code `FILE_EXPIRED`.

**Callbacks.** With `CallbackURL`, the render is always queued and the PDF is POSTed to your https URL
when it is ready. [`examples/async-render`](examples/async-render) shows `PreferAsync` handling
both a direct PDF and a queued job, with an optional callback.

```go
res, err := client.Templates.Render(ctx, templateID, &pagerush.RenderParams{
	Data:        order,
	CallbackURL: pagerush.String("https://example.com/webhooks/pagerush"),
	Metadata:    map[string]string{"order_id": "42"},
})
// res.Queued is true; res.JobID identifies the delivery.
```

### Webhooks

PageRush signs each callback with your project's webhook secret. The signature covers the body and the
`X-PageRush-Event`, `X-PageRush-Job-Id` and `X-PageRush-Metadata` headers, so everything
`ConstructEvent` returns is verified. The webhook functions are package-level, so a receiver doesn't
need a `Client` or an API key.

> **Use the raw body.** Verify the exact bytes you received. Do not decode JSON and encode it again
> before verifying: the bytes change and the signature no longer matches.

> **Deliveries can repeat.** Delivery is at-least-once, and `X-PageRush-Job-Id` (`event.JobID`) is the
> same on every attempt. Ignore job IDs you have already handled, and record a job ID only after your
> work for it succeeded, so a failed attempt is retried.

A complete `net/http` receiver ([`examples/webhook-receiver`](examples/webhook-receiver) has the full program):

```go
secret := os.Getenv("PAGERUSH_WEBHOOK_SECRET")
if secret == "" {
	log.Fatal("set PAGERUSH_WEBHOOK_SECRET")
}
secrets := []string{secret}
safeJobID := regexp.MustCompile(`^[A-Za-z0-9_-]{1,64}$`)

http.HandleFunc("/webhooks/pagerush", func(w http.ResponseWriter, r *http.Request) {
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 64<<20))
	if err != nil {
		http.Error(w, "cannot read body", http.StatusBadRequest)
		return
	}
	event, err := pagerush.ConstructEvent(body, r.Header, secrets)
	if err != nil {
		http.Error(w, "invalid signature", http.StatusBadRequest)
		return
	}
	if !safeJobID.MatchString(event.JobID) { // check before using it in a file name
		http.Error(w, "unexpected job id", http.StatusBadRequest)
		return
	}
	if alreadyHandled(event.JobID) {
		w.WriteHeader(http.StatusOK)
		return
	}
	switch event.Event {
	case pagerush.EventPDFSucceeded:
		if err := os.WriteFile(event.JobID+".pdf", event.PDF, 0o644); err != nil {
			// Not marked as handled, so PageRush's retry gets another chance.
			http.Error(w, "cannot save", http.StatusInternalServerError)
			return
		}
	case pagerush.EventPDFFailed:
		log.Printf("job %s failed: %s", event.JobID, event.Failure.Error.Message)
	}
	markHandled(event.JobID) // only after the work succeeded
	w.WriteHeader(http.StatusOK)
})
```

`ConstructEvent` returns a `*WebhookEvent`:

| Field | Contents |
|---|---|
| `Event` | `pdf.succeeded` or `pdf.failed` |
| `JobID` | the job ID (dedupe on it) |
| `Timestamp` | the signed timestamp |
| `PDF` | the PDF bytes (`pdf.succeeded` only) |
| `Failure` | the parsed failure body: `JobID`, `TemplateID`, `Error{Code, Message}`, `Metadata`, `CreatedAt` (`pdf.failed` only) |
| `Metadata` | the render's metadata (from `X-PageRush-Metadata` on `pdf.succeeded`, from the body on `pdf.failed`), or nil if absent or not an object of strings |

For any other event, `PDF` and `Failure` are nil. `ConstructEvent` returns a
`*WebhookVerificationError` with reason `header` if a `pdf.failed` body is not a JSON object.

`VerifyWebhook` and `ConstructEvent` return `pagerush.ErrNoWebhookSecret` (not a verification error)
if `secrets` is empty or contains an empty string, so a missing environment variable fails loudly
instead of accepting forged deliveries.

To only check the signature, use `VerifyWebhook` with the request's headers:

```go
err := pagerush.VerifyWebhook(body, r.Header, secrets)
var verr *pagerush.WebhookVerificationError
if errors.As(err, &verr) {
	// verr.Reason is "header" (a missing or malformed header),
	// "timestamp" (outside the tolerance) or "signature" (no match)
}
```

Signatures older or newer than 5 minutes are rejected; change that with `pagerush.WithWebhookTolerance(d)`.
If you rotate the webhook secret and choose to keep the previous one, PageRush signs each callback
with both secrets for 24 hours. Pass every secret you accept in `secrets`; a match on any one is enough.

### Errors

Every non-2xx response, connection failure and timeout is returned as a `*pagerush.Error`:

| Field | Contents |
|---|---|
| `Status` | HTTP status, or 0 for connection errors and timeouts |
| `Code` | machine-readable code, e.g. `TEMPLATE_NOT_FOUND` (`UNKNOWN` if an error body had none; SDK codes `CONNECTION_ERROR`, `TIMEOUT`, `WAIT_TIMEOUT`, `INVALID_RESPONSE`, and `VALIDATION_FAILED` with status 0) |
| `Title`, `Detail` | human-readable description |
| `Errors` | `[]FieldError{Path, Message}`: invalid fields, or Typst diagnostics for `TEMPLATE_COMPILE_FAILED` |
| `RequestID` | quote it when you contact support |
| `RetryAfter` | the `Retry-After` header as a duration (see `HasRetryAfter`) |
| `Type` | problem type URI |

`err.Error()` is `"<code>: <detail or title>"`.

```go
res, err := client.Templates.Render(ctx, templateID, &pagerush.RenderParams{Data: order})
var perr *pagerush.Error
if errors.As(err, &perr) {
	switch perr.Code {
	case pagerush.CodeTemplateCompileFailed:
		for _, d := range perr.Errors {
			fmt.Printf("%s: %s\n", d.Path, d.Message) // Typst diagnostics
		}
	case pagerush.CodeInvalidData:
		fmt.Println("bad data:", perr.Detail)
	case pagerush.CodeQuotaExceeded:
		fmt.Println("out of renders")
	case pagerush.CodeConnectionError, pagerush.CodeTimeout:
		fmt.Println("network problem:", errors.Unwrap(perr))
	default:
		fmt.Printf("%v (request id %s)\n", perr, perr.RequestID)
	}
}
```

The `Code*` constants cover the codes the API documents. New codes may appear; treat unknown ones
as generic failures. Connection errors and timeouts unwrap to their cause, so
`errors.Is(err, context.DeadlineExceeded)` and `errors.Is(err, context.Canceled)` work.

If a 2xx response can't be used (a body that isn't the expected JSON, such as an HTML page from a
proxy, or a render response that isn't a PDF), the error is an `*Error` with code `INVALID_RESPONSE`
and the response's real status. It also wraps `pagerush.ErrUnexpectedResponse`, so
`errors.Is(err, pagerush.ErrUnexpectedResponse)` works too. These responses are not retried.

Problems found before a request is sent (a request body that can't be encoded as JSON, such as one
containing NaN, or an invalid idempotency key) are an `*Error` with status 0 and code
`VALIDATION_FAILED`. They are never retried or reported as connection errors. Note that Go's JSON
encoder does not reject invalid UTF-8 in strings: it replaces each invalid byte with U+FFFD, so such
data is sent in that replaced form.

If the context ends while the client is waiting to retry, you get the error that caused the retry
(for example the 429), and it also matches `errors.Is(err, context.Canceled)` or
`context.DeadlineExceeded`. A signed `download_url` appears in errors without its query string.

### Retries and timeouts

The client retries failed requests up to `WithMaxRetries` times (default 2):

| Request | Retried on |
|---|---|
| `GET`, `DELETE` | connection errors, timeouts, 429, 502, 503, 504 |
| `Render` (which always has an idempotency key) | the same, plus 409 with code `CONFLICT` (an earlier attempt with the same key is still rendering); never `IDEMPOTENCY_CONFLICT` |
| `Create`, `CreateVersion`, `Publish`, `Rename` | 429 and 503 only, which the API returns before doing anything |

Before retry *n* it waits for the `Retry-After` header (capped at 30 s) or, without one,
`min(8 s, 0.5 s × 2^(n-1))` with ±25 % jitter. Retries stop as soon as the context is done.

`WithTimeout` limits each attempt (default 60 s). To limit a whole call including retries, use a
context deadline:

```go
ctx, cancel := context.WithTimeout(ctx, 2*time.Minute)
defer cancel()
res, err := client.Templates.Render(ctx, templateID, params)
```

## API reference

Full documentation with examples: [pkg.go.dev](https://pkg.go.dev/github.com/arsi-consultancy/pagerush-sdks/go).
Every method may return `*Error` as described above.

| Function / method | HTTP | Returns |
|---|---|---|
| `NewClient(apiKey string, opts ...Option)` | | `(*Client, error)` |
| `Templates.List(ctx, *ListTemplatesParams)` | `GET /v1/templates` | `(*TemplateList, error)` |
| `Templates.ListAll(ctx, *ListTemplatesParams)` | repeated `GET /v1/templates` | `*TemplateIterator` (`Next`, `Template`, `Err`) |
| `Templates.Create(ctx, *CreateTemplateParams)` | `POST /v1/templates` | `(*TemplateWithVersion, error)` |
| `Templates.Get(ctx, id)` | `GET /v1/templates/{id}` | `(*Template, error)` |
| `Templates.Rename(ctx, id, name)` | `PATCH /v1/templates/{id}` | `(*Template, error)` |
| `Templates.Delete(ctx, id)` | `DELETE /v1/templates/{id}` | `error` |
| `Templates.CreateVersion(ctx, id, *CreateVersionParams)` | `POST /v1/templates/{id}/versions` | `(*TemplateWithVersion, error)` |
| `Templates.GetVersion(ctx, id, version)` | `GET /v1/templates/{id}/versions/{version}` | `(*TemplateVersion, error)` |
| `Templates.Publish(ctx, id, version)` | `POST /v1/templates/{id}/publish` | `(*Template, error)` |
| `Templates.Render(ctx, id, *RenderParams)` | `POST /v1/templates/{id}/render` | `(*RenderResult, error)` |
| `Jobs.Get(ctx, id)` | `GET /v1/jobs/{id}` | `(*Job, error)` |
| `Jobs.Wait(ctx, id, *WaitOptions)` | polls `GET /v1/jobs/{id}` | `(*Job, error)`; `WAIT_TIMEOUT` if not done in time |
| `Jobs.Download(ctx, id)` | `GET /v1/jobs/{id}`, then the signed `download_url` | `([]byte, error)`; `FILE_EXPIRED` if no link |
| `RenderResult.WriteFile(path)` | | `error`; `ErrQueued` if the render was queued |
| `NewTemplateFile(path, contents)` | | `TemplateFile` |
| `TemplateFileFromPath(localPath, targetPath)` | | `(TemplateFile, error)` |
| `VerifyWebhook(body, http.Header, secrets, ...WebhookOption)` | | `error` (`*WebhookVerificationError`, `ErrNoWebhookSecret`) |
| `ConstructEvent(body, http.Header, secrets, ...WebhookOption)` | | `(*WebhookEvent, error)` |
| `String(v)`, `Int(v)`, `Bool(v)` | | pointers for optional fields |

Main types: `Template`, `TemplateVersionSummary`, `TemplateVersion`, `TemplateFile`, `TemplateWithVersion`,
`TemplateList`, `Job`, `JobError`, `JobDelivery`, `RenderParams`, `RenderResult`, `Error`, `FieldError`,
`WebhookEvent`, `WebhookFailure`, `WebhookVerificationError`.

## Examples

Each example reads the API key from `PAGERUSH_API_KEY`.

- [`examples/quickstart`](examples/quickstart): create a template, render it, save `invoice.pdf`.
- [`examples/async-render`](examples/async-render): render with `PreferAsync`; save the PDF if it comes back directly, or wait for the queued job and download it.
- [`examples/webhook-receiver`](examples/webhook-receiver): a `net/http` server that verifies callbacks and saves each PDF once.

```sh
PAGERUSH_API_KEY=pk_live_... go run ./examples/quickstart
```

## Development

```sh
go vet ./...
go test -race ./...
```

`TestLiveSmoke` calls the real API and runs only when `PAGERUSH_API_KEY` is set.

## Versioning

This SDK follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html), separately from the
other PageRush SDKs and from the HTTP API version (`v1`). While the version is `0.x`, a minor release
may contain breaking changes; from `1.0.0` on, breaking changes need a new major version.

Releases are git tags of the form `go/vX.Y.Z` (for example `go/v0.1.0`), as the Go toolchain requires
for a module in the `go/` subdirectory. `pagerush.Version` holds the SDK version and is sent in the
`User-Agent` header (`pagerush-go/0.1.0`). See [CHANGELOG.md](CHANGELOG.md).

## Licence

MIT. See [LICENSE](LICENSE).
