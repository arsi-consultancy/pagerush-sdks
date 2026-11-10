# PageRush PHP SDK

The official PHP client for [PageRush](https://pagerush.dev): manage Typst templates, render them to
PDF, and verify webhook deliveries.

- Requires PHP 8.1 or later with `ext-curl` and `ext-json`. No other runtime dependencies.
- Targets the PageRush HTTP API `v1`.

## Install

```bash
composer require pagerush/pagerush-php
```

Report issues at https://github.com/arsi-consultancy/pagerush-sdks/issues.

## Quickstart

Save this as `quickstart.php`, then run `PAGERUSH_API_KEY=pk_live_... php quickstart.php`.

```php
<?php

declare(strict_types=1);

require __DIR__ . '/vendor/autoload.php';

use PageRush\PageRush;

$client = new PageRush(); // reads PAGERUSH_API_KEY

$created = $client->templates->create([
    'name' => 'Invoice',
    'source' => "#let data = json(\"data.json\")\n= Invoice #data.number\n\nBill to: #data.customer",
]);

$result = $client->templates->render($created['template']['id'], [
    'data' => ['number' => 'INV-001', 'customer' => 'Acme Ltd'],
]);
$result->save('invoice.pdf');

echo "Saved invoice.pdf (job {$result->job_id})\n";
```

The template reads the render data with `json("data.json")`. More complete programs are in
[`examples/`](examples): `quickstart.php`, `async-render.php` and `webhook-receiver.php`.

## Usage

Request and response bodies are plain PHP arrays that use the API's own snake_case field names
(`live_version`, `next_cursor`, `download_url`, ...), so the [API reference](https://pagerush.dev/v1/openapi.json)
applies as written. Options that belong to the SDK rather than the API (`idempotency_key`,
`prefer_async`, `timeout`, ...) are snake_case too, to match.

PHP encodes an empty array as a JSON list (`[]`). Where the API expects an object and you have no
fields, pass `new \stdClass()`.

### Client configuration

```php
use PageRush\PageRush;

$client = new PageRush('pk_live_...');          // just the key

$client = new PageRush([
    'api_key' => 'pk_live_...',                 // default: env PAGERUSH_API_KEY
    'base_url' => 'https://pagerush.dev',       // default: env PAGERUSH_BASE_URL, else https://pagerush.dev
    'timeout' => 60,                            // seconds per HTTP attempt (default 60)
    'max_retries' => 2,                         // retries after the first attempt (default 2)
]);
```

The key is trimmed. The constructor throws `PageRush\Exception\InvalidArgumentException` when there
is no key in the options or the environment, when the key contains spaces or control characters (the
message never includes the key), and when an option is unknown or has the wrong type (for example
`'max_retries' => '2'`), so mistakes fail early.

The client never shows the API key: `var_dump()`, `print_r()`, `var_export()` and `json_encode()`
of the client print it redacted or not at all, and serialising the client throws.

Two more options exist for tests and custom HTTP stacks:

- `transport`: a `PageRush\Transport\Transport` implementation. The default is `CurlTransport`.
  Implement the one-method interface to use another HTTP client (for example a PSR-18 client you
  already have); the SDK itself has no PSR dependencies.
- `sleep` (`callable(float): void`) and `clock` (`callable(): float`): replace `usleep()` and
  `microtime(true)`, so tests of retries and `jobs->wait()` run instantly.

### Templates

Create a template with extra files and images. `TemplateFile` sends text files (`.typ .txt .csv .json
.svg .xml .md .yml .yaml .bib`) as UTF-8 and everything else (images, fonts) as base64.

```php
use PageRush\TemplateFile;

$created = $client->templates->create([
    'name' => 'Invoice',
    'source' => <<<'TYP'
        #import "parts/header.typ": header
        #let data = json("data.json")
        #header(data.customer)
        #image("logo.png", width: 3cm)
        TYP,
    'files' => [
        TemplateFile::fromPath(__DIR__ . '/logo.png'),                         // path "logo.png"
        TemplateFile::fromPath(__DIR__ . '/header.typ', 'parts/header.typ'),   // choose the path
        TemplateFile::fromContents('terms.txt', 'Payment due within 30 days.'),
    ],
    'sample_data' => ['customer' => 'Acme Ltd'],   // used by dashboard test renders
]);
$templateId = $created['template']['id'];
```

Add a version. It goes live straight away unless you pass `'publish' => false`:

```php
$draft = $client->templates->createVersion($templateId, [
    'source' => '...',
    'files' => [TemplateFile::fromPath(__DIR__ . '/logo.png')],
    'publish' => false,
]);
$draftVersion = $draft['version']['version'];
```

Publish a version, or roll back by publishing an older one:

```php
$client->templates->publish($templateId, $draftVersion);
$client->templates->publish($templateId, 1); // roll back
```

List, get, rename, delete:

```php
$page = $client->templates->list(['limit' => 20]);           // one page
$next = $client->templates->list(['limit' => 20, 'cursor' => $page['next_cursor']]);

foreach ($client->templates->listAll() as $template) {       // every template, page by page
    echo $template['id'], ' ', $template['name'], PHP_EOL;
}

$template = $client->templates->get($templateId);             // includes 'versions'
$version = $client->templates->getVersion($templateId, 2);    // source, files, sample_data
$client->templates->rename($templateId, 'Invoice (2026)');
$client->templates->delete($templateId);
```

### Rendering

A render returns a `PageRush\RenderResult`. A synchronous render holds the PDF:

```php
$result = $client->templates->render($templateId, [
    'data' => ['customer' => 'Acme Ltd'],    // required: any JSON value
    'version' => 3,                          // render this version instead of the live one
    'filename' => 'invoice-42.pdf',          // Content-Disposition file name
    'metadata' => ['order_id' => '42'],      // up to 20 string pairs, echoed on the job and webhook
    'delete_after_download' => true,         // don't keep the PDF for 24 hours
]);

$result->save('invoice-42.pdf');             // or use $result->pdf (the bytes)
echo $result->job_id, ' v', $result->template_version, ' pages: ', $result->page_count ?? '?', PHP_EOL;
```

`RenderResult` properties: `queued`, `pdf`, `job_id`, `template_version`, `engine`, `page_count`,
`warnings`, `request_id`, `filename` and `job` (the queued job, or null).

#### Idempotency keys

Every render sends an `Idempotency-Key` header. If you don't pass one, the SDK creates a random UUID
v4 for the call and reuses it on every retry of that call, so a retry never renders or bills twice.
Pass your own to make a render safe to repeat across processes, for example after a crash. Keys are
1-255 characters from `A-Z a-z 0-9 _ - : .`; anything else throws `InvalidArgumentException` before
the request is sent:

```php
$client->templates->render($templateId, [
    'data' => $invoice,
    'idempotency_key' => 'invoice-' . $invoice['id'],
]);
```

#### Accepting a queued render (`prefer_async`)

`prefer_async` sends `Prefer: respond-async`. It does not ask for an async render: the API still
renders straight away and usually returns the PDF. Only when that attempt hits a temporary capacity
problem does it queue the job and return HTTP 202, instead of failing with 503. Code that sets
`prefer_async` must handle both outcomes:

```php
$result = $client->templates->render($templateId, ['data' => $data, 'prefer_async' => true]);

if ($result->queued) {
    $job = $client->jobs->wait($result->job_id, ['timeout' => 120, 'interval' => 1]);
    if ($job['status'] === 'succeeded') {
        file_put_contents('out.pdf', $client->jobs->download($job['id']));
    } else {
        echo 'Failed: ', $job['error']['message'] ?? '', PHP_EOL;
    }
} else {
    $result->save('out.pdf'); // the usual case: the PDF came back directly
}
```

- `jobs->wait()` returns the job once its status is `succeeded` or `failed`. It throws a
  `PageRushException` with status 0 and code `WAIT_TIMEOUT` if the job is still running after
  `timeout` seconds.
- `jobs->download()` fetches the job's signed `download_url` without sending your API key. If the job
  has no `download_url` (not finished, failed, already deleted, or older than 24 hours), it throws a
  `PageRushException` with code `FILE_EXPIRED`.

#### Callback URLs

With `callback_url`, the API always queues the render and POSTs the result to your https URL when
it is ready:

```php
$result = $client->templates->render($templateId, [
    'data' => $data,
    'metadata' => ['order_id' => '42'],
    'callback_url' => 'https://example.com/webhooks/pagerush',
]);
// $result->queued === true; store $result->job_id if you want to match the delivery later.
```

### Webhooks

Each delivery is signed with your project's webhook secret. The `X-PageRush-Signature` header signs
the timestamp, the event, job ID and metadata headers, and the body, so every field of a verified
event can be trusted. `PageRush\Webhook` is static, so a receiver needs only the secret, not an API
key.

```php
use PageRush\Webhook;

Webhook::verify($rawBody, $headers, $secret);                 // returns, or throws
$event = Webhook::constructEvent($rawBody, $headers, $secret); // verifies, then parses
```

`$headers` can be `getallheaders()`, `$_SERVER`, Laravel/Symfony's `$request->headers->all()` or
PSR-7's `getHeaders()`. Names are matched without regard to case.

`constructEvent()` returns a `PageRush\WebhookEvent`:

| property | value |
|---|---|
| `event` | `pdf.succeeded` or `pdf.failed`. Other values may appear later; they have `pdf` and `failure` null |
| `job_id` | the job ID; the same on every retry of a delivery |
| `timestamp` | the signed Unix timestamp |
| `pdf` | the PDF bytes (`pdf.succeeded` only) |
| `failure` | the parsed JSON body: `event, job_id, template_id, error{code,message}, metadata, created_at` (`pdf.failed` only) |
| `metadata` | the render's metadata as a string map, or null when there is none |

**Use the raw body.** The signature covers the exact bytes the API sent. Read them with
`file_get_contents('php://input')` (or your framework's raw-content method). Never `json_decode` the
body and encode it again before verifying: the bytes change and verification fails.

**Deliveries are at-least-once.** A retry carries the same job ID. Record the job IDs you have handled,
after the work succeeds, and skip repeats. Check a job ID against `^[A-Za-z0-9_-]{1,64}$` before using
it in a file name.

Options: `tolerance` (seconds between the signed timestamp and now, default 300) and `now` (a Unix
timestamp, for tests). During a secret rotation, pass both secrets: `[$newSecret, $oldSecret]`.

On failure, both methods throw `PageRush\Exception\WebhookVerificationException`, whose `reason` is
`header` (a missing or malformed signature, event or job ID header, or a `pdf.failed` body that is
not a JSON object), `timestamp` (outside the tolerance) or `signature` (no match for your secret).
Respond with 400. A missing or empty secret is a programming error and throws
`InvalidArgumentException` instead, so check that your secret setting is present.

#### Plain PHP receiver

```php
<?php

declare(strict_types=1);

require __DIR__ . '/vendor/autoload.php';

use PageRush\Exception\WebhookVerificationException;
use PageRush\Webhook;
use PageRush\WebhookEvent;

$secret = getenv('PAGERUSH_WEBHOOK_SECRET');
if ($secret === false || $secret === '') {
    http_response_code(500);
    exit('PAGERUSH_WEBHOOK_SECRET is not set');
}

$rawBody = (string) file_get_contents('php://input');

try {
    // $_SERVER holds the request headers as HTTP_*; constructEvent() understands that form.
    $event = Webhook::constructEvent($rawBody, $_SERVER, $secret);
} catch (WebhookVerificationException $e) {
    http_response_code(400);
    exit;
}

if (preg_match('/^[A-Za-z0-9_-]{1,64}$/D', $event->job_id) !== 1) {
    http_response_code(400);
    exit;
}
if (already_handled($event->job_id)) {   // your own dedupe store
    http_response_code(200);
    exit;
}

if ($event->event === WebhookEvent::SUCCEEDED) {
    file_put_contents("/var/invoices/{$event->job_id}.pdf", $event->pdf);
} elseif ($event->event === WebhookEvent::FAILED) {
    error_log("Render {$event->job_id} failed: " . $event->failure['error']['message']);
}

// Record the job only after the work succeeded, so a failed attempt is retried.
mark_handled($event->job_id);
http_response_code(200);
```

A runnable version is in [`examples/webhook-receiver.php`](examples/webhook-receiver.php).

#### Laravel

`$request->getContent()` returns the raw body and `$request->headers->all()` the headers. Exclude the
route from CSRF protection.

```php
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Cache;
use Illuminate\Support\Facades\Route;
use Illuminate\Support\Facades\Storage;
use PageRush\Exception\WebhookVerificationException;
use PageRush\Webhook;
use PageRush\WebhookEvent;

Route::post('/webhooks/pagerush', function (Request $request) {
    $secret = config('services.pagerush.webhook_secret');
    abort_if(! is_string($secret) || $secret === '', 500, 'PageRush webhook secret is not configured');

    try {
        $event = Webhook::constructEvent($request->getContent(), $request->headers->all(), $secret);
    } catch (WebhookVerificationException $e) {
        return response('Invalid signature: ' . $e->reason, 400);
    }

    abort_unless(preg_match('/^[A-Za-z0-9_-]{1,64}$/D', $event->job_id) === 1, 400, 'Unexpected job ID');

    // At-least-once delivery: handle each job ID once.
    $key = 'pagerush-job:' . $event->job_id;
    if (Cache::has($key)) {
        return response('Already handled');
    }

    if ($event->event === WebhookEvent::SUCCEEDED) {
        Storage::put("invoices/{$event->job_id}.pdf", $event->pdf);
    } elseif ($event->event === WebhookEvent::FAILED) {
        logger()->error('PageRush render failed', $event->failure ?? []);
    }

    // Record the job only after the work succeeded, so a failed attempt is retried.
    Cache::put($key, true, now()->addDay());

    return response('OK');
});
```

### Errors

Every exception the SDK throws implements `PageRush\Exception\PageRushExceptionInterface`.

| exception | when |
|---|---|
| `PageRushException` | the API returned a non-2xx response, a 2xx response was not what the API documents (`INVALID_RESPONSE`), or `jobs->wait()` / `jobs->download()` could not finish (`WAIT_TIMEOUT`, `FILE_EXPIRED`) |
| `ConnectionException` (extends `PageRushException`) | no response arrived: `errorCode` is `TIMEOUT` or `CONNECTION_ERROR`, `status` is 0 |
| `WebhookVerificationException` | a webhook did not verify; see `reason` |
| `InvalidArgumentException` | caught before any request is sent: a missing or malformed API key, an unknown or mistyped option, a bad idempotency key, a body that can't be encoded as JSON (NAN, INF, invalid UTF-8), or an empty webhook secret. `errorCode` is `VALIDATION_FAILED`, `status` is 0, and it is never retried |
| `LogicException`, `RuntimeException` | saving a queued render, file I/O |

The SDK's own exceptions also extend the SPL class of the same name (`\InvalidArgumentException`,
`\RuntimeException`, ...). Error messages never include the API key, and a failed download shows the
signed URL without its query string.

`PageRushException` properties:

| property | value |
|---|---|
| `status` | HTTP status (0 when no response arrived); also `getCode()` |
| `errorCode` | the API's `code`, e.g. `TEMPLATE_NOT_FOUND`; `UNKNOWN` if the body was not problem+json |
| `title`, `detail`, `type` | from the problem+json body |
| `errors` | list of `['path' => ..., 'message' => ...]`; Typst diagnostics on `TEMPLATE_COMPILE_FAILED` |
| `requestId` | the body's `request_id`, else the `X-Request-Id` header. Quote it when you contact support |
| `retryAfter` | seconds from `Retry-After`, or null |

The message reads `"<code>: <detail or title>"`. PHP reserves `$code` on exceptions for an integer,
so the API's string code is `errorCode`. Codes are plain strings and new ones may appear;
`PageRush\ErrorCode` has constants for the known ones (`ErrorCode::TEMPLATE_COMPILE_FAILED`, ...).

```php
use PageRush\ErrorCode;
use PageRush\Exception\ConnectionException;
use PageRush\Exception\PageRushException;

try {
    $result = $client->templates->render($templateId, ['data' => $data]);
} catch (ConnectionException $e) {
    // Network trouble or timeout, after the SDK's own retries.
} catch (PageRushException $e) {
    switch ($e->errorCode) {
        case ErrorCode::TEMPLATE_COMPILE_FAILED:
            foreach ($e->errors as $diagnostic) {
                echo $diagnostic['path'], ': ', $diagnostic['message'], PHP_EOL;
            }
            break;
        case ErrorCode::QUOTA_EXCEEDED:
        case ErrorCode::RATE_LIMITED:
            echo 'Try again in ', $e->retryAfter ?? 60, " seconds\n";
            break;
        default:
            error_log("PageRush error {$e->getMessage()} (request {$e->requestId})");
    }
}
```

### Retries and timeouts

`timeout` applies to each HTTP attempt (default 60 seconds). The SDK retries up to `max_retries`
times (default 2, so at most 3 attempts):

- GET and DELETE: on connection errors, timeouts, 429, 502, 503 and 504.
- Render: the same, plus 409 with code `CONFLICT` (the API is still working on an earlier attempt with
  the same idempotency key). Renders are safe to retry because they always carry an idempotency key.
  A 409 `IDEMPOTENCY_CONFLICT` (the key was used for a different request) is never retried.
- Create, create version, publish and rename: only on 429 and 503, which the API returns before doing
  any work. They are never retried after a connection error, 502 or 504, because the change may
  already have happened.

Before each retry the SDK waits for `Retry-After` seconds when the response has that header (at most
30 seconds; an unparsable value falls back to the backoff), otherwise 0.5 s, 1 s, 2 s, 4 s, then 8 s
for each further retry, each with ±25 % jitter. Errors found before a request is sent are never retried.
Set `'max_retries' => 0` to turn retries off.

## API reference

All methods throw `PageRushException` (or `ConnectionException`) on failure unless noted.

### `new PageRush(string|array $options = [])`

| option | type | default |
|---|---|---|
| `api_key` | string | env `PAGERUSH_API_KEY` (required) |
| `base_url` | string | env `PAGERUSH_BASE_URL`, else `https://pagerush.dev` |
| `timeout` | int\|float seconds | 60 |
| `max_retries` | int | 2 |
| `transport` | `Transport\Transport` | `Transport\CurlTransport` |
| `sleep` | `callable(float): void` | `usleep` |
| `clock` | `callable(): float` | `microtime(true)` |

Properties: `templates`, `jobs`, `webhooks`.

### `$client->templates`

| method | HTTP | returns |
|---|---|---|
| `list(array $params = [])` — `limit` (1-100), `cursor` | `GET /v1/templates` | `['items' => Template[], 'next_cursor' => ?string]` |
| `listAll(array $params = [])` — `limit` | repeated `list` | `Generator` of Template |
| `create(array $params)` — `name`, `source` or `document`, `files`, `sample_data`, `typst_version` | `POST /v1/templates` | `['template' => Template, 'version' => TemplateVersion]` |
| `get(string $id)` | `GET /v1/templates/{id}` | Template, with `versions` |
| `rename(string $id, string $name)` | `PATCH /v1/templates/{id}` | Template |
| `delete(string $id)` | `DELETE /v1/templates/{id}` | `void` |
| `createVersion(string $id, array $params)` — `source` or `document`, `files`, `sample_data`, `typst_version`, `publish` | `POST /v1/templates/{id}/versions` | `['template' => Template, 'version' => TemplateVersion]` |
| `getVersion(string $id, int $version)` | `GET /v1/templates/{id}/versions/{version}` | TemplateVersion |
| `publish(string $id, int $version)` | `POST /v1/templates/{id}/publish` | Template |
| `render(string $id, array $options)` — `data` (required), `version`, `filename`, `metadata`, `delete_after_download`, `callback_url`, `idempotency_key`, `prefer_async` | `POST /v1/templates/{id}/render` | `RenderResult`; throws `InvalidArgumentException` without `data` or with an unknown option |

### `$client->jobs`

| method | returns |
|---|---|
| `get(string $id)` | Job (`GET /v1/jobs/{id}`) |
| `wait(string $id, array $options = [])` — `timeout` (120), `interval` (1) seconds | Job with status `succeeded` or `failed`; `PageRushException` code `WAIT_TIMEOUT` |
| `download(string $id)` | PDF bytes (string); `PageRushException` code `FILE_EXPIRED` when there is no `download_url` |

### `PageRush\Webhook` (static) and `$client->webhooks`

| method | returns |
|---|---|
| `verify(string $rawBody, array $headers, string\|array $secrets, array $options = [])` | `void`; throws `WebhookVerificationException`, or `InvalidArgumentException` for a missing or empty secret |
| `constructEvent(string $rawBody, array $headers, string\|array $secrets, array $options = [])` | `WebhookEvent`; throws as `verify()`, and `WebhookVerificationException` with reason `header` when a `pdf.failed` body is not a JSON object |

### Other types

- `RenderResult`: see [Rendering](#rendering). `save(string $path): int` writes the PDF and returns
  the bytes written; it throws `LogicException` for a queued render and `RuntimeException` if the
  file can't be written.
- `TemplateFile::fromContents(string $path, string $contents): array` and
  `TemplateFile::fromPath(string $localPath, ?string $targetPath = null): array` build
  `['path', 'content', 'encoding']` entries. A text file that isn't valid UTF-8 is sent as base64.
- `PageRush\Types` declares PHPStan/Psalm array shapes (`Template`, `TemplateVersion`, `Job`, ...).
  Import them with `@phpstan-import-type Template from \PageRush\Types`.
- `PageRush\Version::VERSION`: the SDK version, also sent in the `User-Agent` header
  (`pagerush-php/0.1.0`).

## Development

```bash
composer install
composer test        # PHPUnit; set PAGERUSH_API_KEY to also run the live smoke test
composer analyse     # PHPStan (level max on src/ and examples/)
composer cs          # PHP-CS-Fixer (PSR-12), dry run; `composer cs:fix` applies fixes
```

## Versioning

This package follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). While the
version is `0.x`, a minor release may contain breaking changes; from `1.0.0`, breaking changes need a
major release. The SDK version is separate from the HTTP API version (`v1`). In the monorepo, releases
are tagged `php-vX.Y.Z`. See the [CHANGELOG](CHANGELOG.md).

Supported: PHP 8.1 and later.

## Licence

MIT. See [LICENSE](LICENSE).
