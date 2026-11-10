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
5. Commit and push to `main`, wait for CI to pass, then tag that commit and push the tag:

   | SDK | Tag | Example |
   |---|---|---|
   | node | `node-vX.Y.Z` | `git tag node-v0.2.0 && git push origin node-v0.2.0` |
   | python | `python-vX.Y.Z` | `git tag python-v0.2.0 && git push origin python-v0.2.0` |
   | php | `php-vX.Y.Z` | `git tag php-v0.2.0 && git push origin php-v0.2.0` |
   | go | `go/vX.Y.Z` (the form the Go toolchain expects for a module in `go/`) | `git tag go/v0.2.0 && git push origin go/v0.2.0` |

   A version with a pre-release suffix (`0.2.0-beta.1`, or `0.2.0b1` for Python) is published as a
   pre-release: npm tag `next`, and marked as a pre-release on GitHub.

The tag starts that SDK's release workflow in `.github/workflows/`. It first runs
`scripts/release-notes.sh`, which stops the release if the tag doesn't match the version source or the
changelog has no dated entry for it. Then it runs the SDK's checks again and publishes:

| SDK | Published to | How |
|---|---|---|
| node | npm | `npm publish` through trusted publishing (no token), with provenance |
| python | PyPI | trusted publishing (no token), with attestations |
| php | Packagist | pushes `php/` to the split repository `arsi-consultancy/pagerush-php` and tags it `vX.Y.Z`; Packagist reads that tag |
| go | Go module proxy | asks `proxy.golang.org` to fetch the tag |

Each workflow then creates a GitHub release with the changelog entry as its notes.

A tag can't be published twice: npm and PyPI refuse a version that already exists. If a release fails
before publishing, fix the problem, delete the tag (`git push origin :refs/tags/<tag>` and
`git tag -d <tag>`), and tag again. After a version is published, release a new patch version instead.

## One-time setup

1. **GitHub environments.** In this repository's settings, create the environments `npm`, `pypi` and
   `packagist`, and limit each to tags matching its SDK (`node-v*`, `python-v*`, `php-v*`). Turn on
   private vulnerability reporting (Settings → Security), which `SECURITY.md` points to.
2. **npm.** npm only offers trusted publishing for a package that exists, so publish the first version
   by hand: `cd node && npm publish --access public`, logged in as an owner of the `pagerush` package.
   Then, in the package's settings on npmjs.com, add a trusted publisher: GitHub Actions, repository
   `arsi-consultancy/pagerush-sdks`, workflow `release-node.yml`, environment `npm`. Later versions are
   published by the workflow.
3. **PyPI.** On pypi.org, add a pending trusted publisher for the project `pagerush`: repository
   `arsi-consultancy/pagerush-sdks`, workflow `release-python.yml`, environment `pypi`. The first
   workflow run creates the project.
4. **Packagist.** Create the empty repository `arsi-consultancy/pagerush-php`, marked read-only in its
   description. Generate an SSH key pair; add the public key to that repository as a deploy key with
   write access, and the private key to this repository's `packagist` environment as the secret
   `PHP_SPLIT_DEPLOY_KEY`. After the first release has pushed to it, submit
   `https://github.com/arsi-consultancy/pagerush-php` on packagist.org as `pagerush/pagerush-php` and
   turn on its GitHub hook so new tags are picked up automatically.
5. **Go.** Nothing to set up. The repository must be public for `proxy.golang.org` to fetch it.
