# Releasing

Each SDK is versioned and released on its own. Versions follow [Semantic Versioning](https://semver.org):

- **patch** (0.1.0 → 0.1.1): bug fixes, docs.
- **minor** (0.1.0 → 0.2.0): new methods or options. While an SDK is `0.x`, a minor release may also contain
  breaking changes; call them out under **Changed** / **Removed** in the changelog.
- **major** (from 1.0.0 on): breaking changes.

A new API feature usually means a minor release of all four SDKs. They don't need to share version numbers.

## Steps

1. Update the code and tests. Make the change in all four SDKs if it touches `SPEC.md`, and update `SPEC.md` first.
2. Bump the version in the SDK's single source of truth:

   | SDK | File |
   |---|---|
   | node | `node/package.json` `version` and `node/src/version.ts` (a test checks they match) |
   | python | `python/src/pagerush/_version.py` |
   | php | `php/src/Version.php` |
   | go | `go/version.go` |

3. In the SDK's `CHANGELOG.md`, move the `[Unreleased]` entries under a new `## [x.y.z] - YYYY-MM-DD` heading.
4. Run the SDK's checks (`make check` from the repo root runs all of them).
5. Commit, then tag:

   | SDK | Tag |
   |---|---|
   | node | `node-vX.Y.Z` |
   | python | `python-vX.Y.Z` |
   | php | `php-vX.Y.Z`, then push the `php/` subtree to the split repo and tag it `vX.Y.Z` (Packagist reads tags from there) |
   | go | `go/vX.Y.Z` (this exact form is what the Go toolchain expects for a module in `go/`) |

6. Publish:
   - node: `cd node && pnpm build && npm publish --access public`
   - python: `cd python && python -m build && twine upload dist/*`
   - php: Packagist updates from the split repo's tag (set up its GitHub hook once).
   - go: nothing to upload; `proxy.golang.org` picks up the tag. Optionally run
     `GOPROXY=proxy.golang.org go list -m github.com/arsi-consultancy/pagerush-sdks/go@vX.Y.Z` to warm it.

## First-time setup (not done yet)

- Create the GitHub repo `arsi-consultancy/pagerush-sdks` (public) and the PHP split repo
  (e.g. `arsi-consultancy/pagerush-php`), and register it on Packagist under the `pagerush` vendor.
- Claim the `pagerush` names on npm and PyPI (both were free on 2026-11-09).
- Add `NPM_TOKEN` and a PyPI trusted publisher if releases move to CI.
