# PageRush SDKs

Official client libraries for the [PageRush](https://pagerush.dev) API: store Typst templates, render them
with JSON data, and get PDFs back.

| Language | Package | Install | Docs |
|---|---|---|---|
| Node.js / TypeScript | `pagerush` (npm) | `npm install pagerush` | [node/README.md](node/README.md) |
| Python | `pagerush` (PyPI) | `pip install pagerush` | [python/README.md](python/README.md) |
| PHP | `pagerush/pagerush-php` (Packagist) | `composer require pagerush/pagerush-php` | [php/README.md](php/README.md) |
| Go | `github.com/arsi-consultancy/pagerush-sdks/go` | `go get github.com/arsi-consultancy/pagerush-sdks/go@latest` | [go/README.md](go/README.md) |

Every SDK has the same features:

- Templates: create, add versions, publish (or roll back), list, get, rename, delete.
- Rendering: get the PDF back in the response, or queue it and receive it at a `callback_url`.
- Jobs: check status, wait for a queued render, download the PDF.
- Webhooks: verify the `X-PageRush-Signature` on callback deliveries.
- Safe retries: render calls always carry an idempotency key, so a retry never renders or charges twice.
- No runtime dependencies beyond the language's standard library.

## 30-second example (Node)

```ts
import { PageRush } from "pagerush";
import { writeFile } from "node:fs/promises";

const pagerush = new PageRush(); // reads PAGERUSH_API_KEY

const { template } = await pagerush.templates.create({
  name: "Hello",
  source: '#let data = json("data.json")\n= Hello, #data.name!',
});

const result = await pagerush.templates.render(template.id, { data: { name: "Asha" } });
await writeFile("hello.pdf", result.pdf!);
```

Each SDK's README has the same example in its own language.

## Repository layout

```
SPEC.md        the behaviour every SDK implements (methods, retries, errors, webhooks, versioning)
testdata/      shared test vectors (webhook signatures) that every SDK's tests read
node/ python/ php/ go/   one SDK each, with its own README, CHANGELOG and examples
integrations/  no-code integrations (Zapier, n8n), coming later
```

## Versioning

Each SDK follows [Semantic Versioning](https://semver.org) and is released on its own, with its own
`CHANGELOG.md`. SDK versions are separate from the HTTP API version (`/v1`). See [RELEASING.md](RELEASING.md).

## API reference

The HTTP API is described by an OpenAPI 3.1 document at `https://pagerush.dev/v1/openapi.json`.

## Licence

MIT. See [LICENSE](LICENSE).
