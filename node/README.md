# PageRush Node.js SDK

The official Node.js and TypeScript client for the [PageRush](https://pagerush.dev) API, which turns Typst templates and JSON data into PDFs.

It has no runtime dependencies. It uses the built-in `fetch` and Web Crypto, so the same package runs on Node.js 20+, Bun, Deno and Cloudflare Workers. It ships ESM, CommonJS and TypeScript types.

## Install

```sh
npm install pagerush
```

## Quickstart

Create a project API key in the PageRush dashboard and set it as `PAGERUSH_API_KEY`. Then save this as `quickstart.mjs` and run `node quickstart.mjs`:

```ts
import { PageRush } from "pagerush";

const client = new PageRush(); // reads PAGERUSH_API_KEY

const { template } = await client.templates.create({
  name: "Quickstart invoice",
  source: [
    '#let data = json("data.json")',
    "= Invoice #data.number",
    "Bill to: #data.customer",
    "",
    "#table(columns: 2, ..data.items.map(i => (i.name, str(i.price))).flatten())",
  ].join("\n"),
});

const result = await client.templates.render(template.id, {
  data: {
    number: "INV-001",
    customer: "Example Corp",
    items: [
      { name: "Design", price: 400 },
      { name: "Build", price: 1200 },
    ],
  },
});

await result.save("invoice.pdf");
console.log(`Saved invoice.pdf (job ${result.job_id})`);
```

A template reads the render's `data` with `#let data = json("data.json")`. More programs are in `examples/`: [`quickstart.ts`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/examples/quickstart.ts), [`async-render.ts`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/examples/async-render.ts) and [`webhook-receiver.ts`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/examples/webhook-receiver.ts).

## Usage

### Client configuration

```ts
import { PageRush } from "pagerush";

const client = new PageRush({
  apiKey: "pk_live_...", // default: env PAGERUSH_API_KEY; the constructor throws if neither is set
  baseUrl: "https://pagerush.dev", // default: env PAGERUSH_BASE_URL, else https://pagerush.dev
  timeout: 60_000, // per HTTP attempt, in ms
  maxRetries: 2, // retries after the first attempt
  fetch: customFetch, // optional: your own fetch (proxies, tests)
});
```

Every method also takes an optional last argument with per-call options:

```ts
const controller = new AbortController();
await client.templates.get("tpl_123", { signal: controller.signal, timeout: 10_000, maxRetries: 0 });
```

An aborted call rejects with the signal's reason (an `AbortError` by default) and is not retried. Aborting also cancels a pending retry wait or `jobs.wait` poll, and clears its timer.

Request and response fields use the API's own snake_case names (`live_version`, `next_cursor`, `delete_after_download`). Options that belong to the SDK itself, such as `idempotencyKey`, `preferAsync` and `maxRetries`, use camelCase.

### Templates

A template version is a Typst project. `source` is its `main.typ`. `files` holds any other files it imports or reads. Use `templateFile` or `fileFromPath` to build file entries: text files (`.typ .txt .csv .json .svg .xml .md .yml .yaml .bib`) are sent as `utf8`, everything else (images, fonts) as `base64`.

```ts
import { readFile } from "node:fs/promises";
import { fileFromPath, templateFile } from "pagerush";

const { template, version } = await client.templates.create({
  name: "Invoice",
  source: '#import "parts/header.typ": header\n#header\n#image("logo.png", width: 3cm)',
  files: [
    templateFile("parts/header.typ", "#let header = [*Example Corp*]"),
    templateFile("logo.png", await readFile("assets/logo.png")),
    await fileFromPath("assets/Inter.ttf", "fonts/Inter.ttf"), // Node, Bun and Deno only
  ],
  sample_data: { number: "INV-001" }, // used by test renders in the dashboard
});
```

Add a version. It becomes the live version unless you pass `publish: false`:

```ts
const { version: draft } = await client.templates.createVersion(template.id, {
  source: "...",
  publish: false,
});
```

Publish a version, or roll back by publishing an older one:

```ts
await client.templates.publish(template.id, draft.version);
await client.templates.publish(template.id, 1); // roll back
```

List templates one page at a time, or iterate over all of them:

```ts
const page = await client.templates.list({ limit: 20 });
const next = page.next_cursor ? await client.templates.list({ limit: 20, cursor: page.next_cursor }) : null;

for await (const t of client.templates.listAll()) {
  console.log(t.id, t.name, t.live_version);
}
```

Get, rename, read a version, delete:

```ts
const t = await client.templates.get(template.id); // includes t.versions, newest first
await client.templates.rename(template.id, "Invoice v2");
const v = await client.templates.getVersion(template.id, 1); // v.source, v.files, v.typst_version
await client.templates.delete(template.id);
```

### Rendering

```ts
const result = await client.templates.render(template.id, {
  data: { number: "INV-002" }, // required: any JSON value
  version: 3, // optional: render this version instead of the live one
  filename: "INV-002.pdf", // optional: name in Content-Disposition
  metadata: { order: "42" }, // optional: up to 20 string pairs (4 KB), echoed on the job and callback
  delete_after_download: true, // optional: don't keep the PDF for 24 hours
});

result.pdf; // Uint8Array
result.job_id; // e.g. "job_..."
result.template_version; // the version that was rendered
result.page_count; // number or null
result.warnings; // number of Typst warnings, or null
await result.save("INV-002.pdf"); // Node, Bun and Deno
```

**Idempotency keys.** Every render sends an `Idempotency-Key` header. If you don't pass one, the SDK creates a random UUID and reuses it for that call's retries, so a retried render is never rendered or charged twice. Pass your own key to make a render safe to repeat across processes, for example after a crash:

```ts
await client.templates.render(template.id, { data, idempotencyKey: `invoice-${invoiceId}` });
```

A key must be 1-255 characters from `A-Z a-z 0-9 _ - : .`. Anything else throws `PageRushError` with code `VALIDATION_FAILED` (status 0) before the request is sent.

**Asynchronous fallback.** With `preferAsync: true` the server still renders straight away. If that attempt fails for a temporary reason (for example, no render capacity), it queues the render and answers with a job instead of an error. Check `result.queued`:

```ts
const result = await client.templates.render(template.id, { data, preferAsync: true });
let pdf = result.pdf;
if (result.queued) {
  const job = await client.jobs.wait(result.job_id, { timeout: 120_000, interval: 1_000 });
  if (job.status === "failed") throw new Error(`${job.error?.code}: ${job.error?.message}`);
  pdf = await client.jobs.download(job.id);
}
```

`jobs.wait` returns the job once it has `succeeded` or `failed`. It throws a `PageRushError` with code `WAIT_TIMEOUT` if the job is still running after `timeout`. `jobs.download` fetches the PDF through the job's signed `download_url` (it does not send your API key there). It throws code `FILE_EXPIRED` when the job has no PDF to download: not finished, failed, older than 24 hours, or already delivered with `delete_after_download`.

**Callbacks.** With `callback_url` the render is always queued, and PageRush POSTs the signed PDF to your https URL when it is ready:

```ts
const result = await client.templates.render(template.id, {
  data,
  callback_url: "https://example.com/pagerush",
  metadata: { order: "42" },
});
console.log(result.queued, result.job_id); // true, "job_..."
```

### Webhooks

PageRush sends `pdf.succeeded` (the body is the PDF) or `pdf.failed` (the body is JSON) to your `callback_url`. Each delivery is signed with your project's webhook secret: the signature in `X-PageRush-Signature` covers the timestamp, the event, job id and metadata headers, and the raw body. `constructEvent` verifies the signature and timestamp, then parses the delivery:

```ts
import { constructEvent } from "pagerush";

const event = await constructEvent(rawBody, headers, process.env.PAGERUSH_WEBHOOK_SECRET!);
event.event; // "pdf.succeeded" | "pdf.failed"
event.job_id; // dedupe on this
event.pdf; // Uint8Array for pdf.succeeded, else null
event.failure; // { error: { code, message }, ... } for pdf.failed, else null
event.metadata; // the render's metadata, or null
```

Every field comes from signed data. Use `verify(rawBody, headers, secret)` if you only need the check. Both throw `WebhookVerificationError` with a `reason` of `header`, `timestamp` (older or newer than `tolerance`, default 300 seconds) or `signature`. During a secret rotation, pass both secrets as an array: `constructEvent(rawBody, headers, [newSecret, oldSecret])`. These functions don't need an API key, and are also available as `client.webhooks`. A missing or empty secret is a setup mistake, so it throws a `TypeError` rather than a `WebhookVerificationError`: check that `PAGERUSH_WEBHOOK_SECRET` is set when your server starts.

> **Use the raw body.** The signature covers the exact bytes PageRush sent. Pass the unparsed request body (`Buffer`, `Uint8Array`, `ArrayBuffer` or string). If you parse JSON and serialise it again, the bytes change and verification fails.

> **Dedupe on `job_id`.** Delivery is at-least-once: a callback can arrive more than once, always with the same `X-PageRush-Job-Id`. Answer 2xx quickly and ignore repeats. Record a job id as processed only after your work has succeeded, so a failed attempt is retried. Check `job_id` against `^[A-Za-z0-9_-]{1,64}$` before using it in a file name.

A complete Express receiver. `express.raw()` keeps the body as a `Buffer`:

```ts
import express from "express";
import { writeFile } from "node:fs/promises";
import { constructEvent, WebhookVerificationError } from "pagerush";

const secret = process.env.PAGERUSH_WEBHOOK_SECRET;
if (!secret) throw new Error("Set PAGERUSH_WEBHOOK_SECRET to your project's webhook secret.");

const app = express();
const processed = new Set<string>(); // use your database in production

app.post("/pagerush", express.raw({ type: "*/*", limit: "50mb" }), async (req, res) => {
  try {
    const event = await constructEvent(req.body, req.headers, secret);
    if (processed.has(event.job_id)) {
      res.sendStatus(200);
      return;
    }

    if (!/^[A-Za-z0-9_-]{1,64}$/.test(event.job_id)) {
      res.status(400).send("unexpected job id");
      return;
    }

    if (event.event === "pdf.succeeded") {
      await writeFile(`${event.job_id}.pdf`, event.pdf!);
    } else if (event.event === "pdf.failed") {
      console.error(event.failure?.error.code, event.failure?.error.message);
    }
    processed.add(event.job_id); // only after the work succeeded, so a failed attempt is retried
    res.sendStatus(200);
  } catch (err) {
    if (err instanceof WebhookVerificationError) {
      res.status(400).send(err.reason);
      return;
    }
    console.error(err);
    res.sendStatus(500); // PageRush retries the delivery
  }
});

app.listen(3000);
```

Register this route before any global `express.json()` middleware, or that middleware will consume the body first. A dependency-free version using `node:http` is in [`examples/webhook-receiver.ts`](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/examples/webhook-receiver.ts). In a Fetch-style handler (Workers, Hono, Bun), pass `await request.arrayBuffer()` and `request.headers`.

### Errors

Every non-2xx response throws a `PageRushError`:

| field         | meaning                                                                                            |
| ------------- | -------------------------------------------------------------------------------------------------- |
| `status`      | HTTP status, or 0 when there was no response                                                       |
| `code`        | machine-readable code, e.g. `TEMPLATE_NOT_FOUND`; `UNKNOWN` if the error body was not problem+json |
| `title`       | short summary                                                                                      |
| `detail`      | longer explanation, or null                                                                        |
| `errors`      | `{ path, message }[]`; on `TEMPLATE_COMPILE_FAILED` these are Typst diagnostics                    |
| `request_id`  | include this when you contact support                                                              |
| `retry_after` | seconds from `Retry-After`, or null                                                                |
| `type`        | problem type URI, or null                                                                          |

The message is `"<code>: <detail or title>"`. When the request never got a response, the SDK throws `PageRushConnectionError` (a subclass with `status` 0) with code `CONNECTION_ERROR`, or `TIMEOUT` if an attempt exceeded `timeout`.

If the server answers 2xx with a body the SDK can't use (not JSON, missing the expected fields, or a 200 render that isn't `application/pdf`), it throws `PageRushError` with code `INVALID_RESPONSE` and the real HTTP status. This usually means a proxy or firewall answered instead of PageRush.

Some problems are caught before anything is sent, and are never retried: an `idempotencyKey` the server would reject, or a request body JSON can't represent (`NaN`, `Infinity`, a `BigInt`, a function, a string with unpaired surrogates, or a cycle). These throw `PageRushError` with code `VALIDATION_FAILED` and status 0. The constructor throws a `TypeError` for a missing API key, a key with spaces or control characters (the message never includes the key), or a `baseUrl` that isn't an http(s) URL.

The API key never appears in error messages, `console.log(client)` or `JSON.stringify(client)`. Signed download links appear in messages without their query string.

```ts
import { PageRushConnectionError, PageRushError } from "pagerush";

try {
  await client.templates.render(templateId, { data });
} catch (err) {
  if (err instanceof PageRushConnectionError) {
    console.error("network problem:", err.code, err.cause);
  } else if (err instanceof PageRushError) {
    if (err.code === "TEMPLATE_COMPILE_FAILED") {
      for (const d of err.errors) console.error(`${d.path}: ${d.message}`);
    } else if (err.code === "QUOTA_EXCEEDED") {
      // upgrade the plan or wait for the next period
    }
    console.error(`${err.message} (request ${err.request_id})`);
  } else {
    throw err;
  }
}
```

Codes are plain strings, so new server codes come through unchanged. `ErrorCodes` lists the known ones as constants.

### Retries and timeouts

The SDK retries a failed call up to `maxRetries` times (default 2, so at most 3 attempts):

- `GET` and `DELETE`: on connection errors, timeouts, 429, 502, 503 and 504.
- Renders: the same, plus 409 with code `CONFLICT`, which means the server is still rendering an earlier attempt with the same idempotency key. Renders are safe to retry because they always carry an idempotency key. `IDEMPOTENCY_CONFLICT` (the same key with a different body) is never retried.
- `create`, `createVersion`, `publish` and `rename`: only on 429 and 503, which the server returns before doing any work. They are not retried on connection errors, 502 or 504, because the change may already have been made.

Before retry _n_ it waits for `Retry-After` when the server sends it (capped at 30 seconds), and otherwise `min(8, 0.5 × 2^(n-1))` seconds with ±25 % jitter. `timeout` (default 60 seconds) applies to each attempt and covers reading the whole response body. Redirects are followed for up to 5 hops; the `Authorization` header is dropped when a redirect leaves the API's origin or switches to plain http.

## API reference

All methods return promises. Every method of `templates` and `jobs` accepts an optional last argument `RequestOptions`: `{ signal?, timeout?, maxRetries? }`.

### `new PageRush(options?)`

| option       | type                                                  | default                                                      |
| ------------ | ----------------------------------------------------- | ------------------------------------------------------------ |
| `apiKey`     | `string`                                              | env `PAGERUSH_API_KEY` (required)                            |
| `baseUrl`    | `string`                                              | env `PAGERUSH_BASE_URL`, else `https://pagerush.dev`         |
| `timeout`    | `number` (ms, per attempt)                            | `60000`                                                      |
| `maxRetries` | `number`                                              | `2`                                                          |
| `fetch`      | `typeof fetch`                                        | global `fetch`                                               |
| `sleep`      | `(ms: number, signal?: AbortSignal) => Promise<void>` | `setTimeout`-based, cancelled by the signal; inject in tests |

Throws `TypeError` if there is no API key, the key contains spaces or control characters, or `baseUrl` is not an http(s) URL.

### `client.templates`

| method                                                                                                                       | HTTP                                  | returns                              |
| ---------------------------------------------------------------------------------------------------------------------------- | ------------------------------------- | ------------------------------------ |
| `list({ limit?, cursor? })`                                                                                                  | `GET /v1/templates`                   | `{ items: Template[], next_cursor }` |
| `listAll({ limit? })`                                                                                                        | repeated `list`                       | `AsyncGenerator<Template>`           |
| `create({ name, source?, document?, files?, sample_data?, typst_version? })`                                                 | `POST /v1/templates`                  | `{ template, version }`              |
| `get(id)`                                                                                                                    | `GET /v1/templates/{id}`              | `Template` (with `versions`)         |
| `rename(id, name)`                                                                                                           | `PATCH /v1/templates/{id}`            | `Template`                           |
| `delete(id)`                                                                                                                 | `DELETE /v1/templates/{id}`           | `void`                               |
| `createVersion(id, { source?, document?, files?, sample_data?, typst_version?, publish? })`                                  | `POST /v1/templates/{id}/versions`    | `{ template, version }`              |
| `getVersion(id, version)`                                                                                                    | `GET /v1/templates/{id}/versions/{v}` | `TemplateVersion`                    |
| `publish(id, version)`                                                                                                       | `POST /v1/templates/{id}/publish`     | `Template`                           |
| `render(id, { data, version?, filename?, metadata?, delete_after_download?, callback_url?, idempotencyKey?, preferAsync? })` | `POST /v1/templates/{id}/render`      | `RenderResult`                       |

`RenderResult`: `{ queued, pdf, job, job_id, template_version, engine, page_count, warnings, request_id, filename, save(path) }`. When `queued` is false, `pdf` holds the bytes and `job` is null. When it is true, `pdf` is null, `job` is the queued `Job`, and the header fields are null. `save` throws if the render was queued.

### `client.jobs`

| method                                       | returns                         | throws                                        |
| -------------------------------------------- | ------------------------------- | --------------------------------------------- |
| `get(id)`                                    | `Job`                           | `NOT_FOUND`                                   |
| `wait(id, { timeout?, interval?, signal? })` | `Job` (`succeeded` or `failed`) | `WAIT_TIMEOUT` (defaults: 120000 ms, 1000 ms) |
| `download(id)`                               | `Uint8Array`                    | `FILE_EXPIRED` if there is no `download_url`  |

### Webhooks

| function                                                                  | returns                 | throws                     |
| ------------------------------------------------------------------------- | ----------------------- | -------------------------- |
| `verify(rawBody, headers, secretOrSecrets, { tolerance?, now? })`         | `Promise<void>`         | `WebhookVerificationError` |
| `constructEvent(rawBody, headers, secretOrSecrets, { tolerance?, now? })` | `Promise<WebhookEvent>` | `WebhookVerificationError` |

`tolerance` and `now` are in seconds. `headers` may be a Fetch `Headers` or a Node `req.headers` object. `WebhookEvent`: `{ event, job_id, timestamp, pdf, failure, metadata }`. Both are also exported together as `webhooks` and available as `client.webhooks`.

### Helpers and types

- `templateFile(path, contents)`: builds a `TemplateFile` (`{ path, content, encoding }`) from a string, `Uint8Array` or `ArrayBuffer`.
- `fileFromPath(localPath, targetPath?)`: reads a local file into a `TemplateFile` (Node, Bun, Deno).
- Errors: `PageRushError`, `PageRushConnectionError`, `WebhookVerificationError`, `ErrorCodes`.
- Types: `Template`, `TemplateVersion`, `TemplateVersionSummary`, `TemplateFile`, `TemplatePage`, `TemplateWithVersion`, `Job`, `JobStatus`, `RenderOptions`, `RenderResult`, `RequestOptions`, `Sleep`, `WaitOptions`, `WebhookEvent`, `WebhookFailure`, `JsonValue`.
- `VERSION`: this SDK's version string.

Every public symbol has TSDoc comments, so your editor shows this documentation inline. `pnpm run docs` builds TypeDoc HTML into `docs/`.

## Supported versions

- Node.js 20 or later. Also Bun, Deno and Cloudflare Workers (anything with `fetch` and Web Crypto). `result.save()` and `fileFromPath()` need a `node:fs` implementation.
- TypeScript 5.0 or later for the bundled types.
- API version: `v1`.

## Versioning

This package follows [Semantic Versioning 2.0.0](https://semver.org). While it is `0.x`, a minor release may contain breaking changes. From `1.0.0`, breaking changes need a new major version. The SDK version is separate from the API version (`/v1`). See [CHANGELOG.md](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/CHANGELOG.md) for what changed in each release.

## License

MIT. See [LICENSE](https://github.com/arsi-consultancy/pagerush-sdks/blob/main/node/LICENSE).
