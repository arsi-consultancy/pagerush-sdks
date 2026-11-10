# Contributing

Thanks for helping improve the PageRush SDKs. Bug reports, documentation fixes and pull requests are
all welcome.

## Before you start

- For a bug, open an issue with the SDK, its version, your language version, and a minimal example.
  Include the `request_id` from the error if there is one; it lets us find the request on our side.
- For a new feature, open an issue first. The four SDKs implement one shared contract,
  [SPEC.md](SPEC.md), so a new feature usually means a change to the spec and to every SDK.
- Security problems: see [SECURITY.md](SECURITY.md) instead of opening an issue.

## Development

Each SDK lives in its own directory and has no runtime dependencies. `make check` from the repository
root runs every SDK's checks; you can also run one SDK's checks on its own:

| SDK | Requirements | Checks |
|---|---|---|
| `node/` | Node.js 20+, pnpm 10 | `pnpm install && pnpm typecheck && pnpm lint && pnpm test` |
| `python/` | Python 3.9+ | `pip install -e '.[dev]' && ruff check . && ruff format --check . && mypy && pytest` |
| `php/` | PHP 8.1+ with `ext-curl`, Composer | `composer install && composer check` |
| `go/` | Go 1.21+ | `gofmt -l . && go vet ./... && go test -race ./...` |

The tests run offline against a mock transport or a local HTTP server. Each SDK also has a live test
that calls the real API; it is skipped unless `PAGERUSH_API_KEY` is set.

Webhook signature test vectors live in `testdata/webhooks.json`, with a copy in each SDK so that its
tests also run from a published package. After changing them, run `make sync-testdata`.

## Pull requests

- Keep a change to one concern, with tests for new behaviour and for the bug being fixed.
- Follow the SDK's existing style. The formatters and linters above are what CI enforces.
- Add a line under `[Unreleased]` in the SDK's `CHANGELOG.md` for anything users would notice.
- If you change `SPEC.md`, update all four SDKs in the same pull request, or say in the description
  which ones still need the change.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
