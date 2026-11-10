.PHONY: check check-testdata sync-testdata node python php go

FIXTURES := node/test/fixtures/webhooks.json \
	python/tests/fixtures/webhooks.json \
	php/tests/fixtures/webhooks.json \
	go/testdata/webhooks.json

check: check-testdata node python php go

# Each SDK keeps its own copy of the shared webhook vectors so its tests also run from a
# published package or a split repository.
sync-testdata:
	@for f in $(FIXTURES); do mkdir -p "$$(dirname "$$f")" && cp testdata/webhooks.json "$$f"; done

check-testdata:
	@for f in $(FIXTURES); do \
		cmp -s testdata/webhooks.json "$$f" || { echo "$$f differs from testdata/webhooks.json (run make sync-testdata)"; exit 1; }; \
	done

node:
	cd node && pnpm install --frozen-lockfile && pnpm typecheck && pnpm lint && pnpm test && pnpm build

python:
	cd python && .venv/bin/ruff check . && .venv/bin/ruff format --check . && .venv/bin/mypy && .venv/bin/pytest -q

php:
	cd php && composer install --no-interaction --quiet && composer check

go:
	cd go && test -z "$$(gofmt -l .)" && go vet ./... && go test -race ./...
