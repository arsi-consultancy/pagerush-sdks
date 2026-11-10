#!/usr/bin/env bash
# Usage: scripts/release-notes.sh <tag>
#
# Checks that a release tag (node-v1.2.3, python-v1.2.3, php-v1.2.3 or go/v1.2.3) matches the SDK's
# version source and has a dated CHANGELOG.md entry, then prints that entry's notes.
set -euo pipefail

tag="${1:?usage: $0 <tag>}"
case "$tag" in
  node-v*)   sdk=node;   version="${tag#node-v}" ;;
  python-v*) sdk=python; version="${tag#python-v}" ;;
  php-v*)    sdk=php;    version="${tag#php-v}" ;;
  go/v*)     sdk=go;     version="${tag#go/v}" ;;
  *) echo "unrecognised tag: $tag" >&2; exit 1 ;;
esac

# Semantic versions, plus PEP 440 pre-releases (1.2.0b1, 1.2.0rc1) for the Python SDK.
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] &&
   ! [[ "$sdk" == python && "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(a|b|rc)[0-9]+$ ]]; then
  echo "not a valid version: $version" >&2
  exit 1
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
case "$sdk" in
  node)   source_file="$root/node/package.json";                pattern="\"version\": \"$version\"" ;;
  python) source_file="$root/python/src/pagerush/_version.py";  pattern="__version__ = \"$version\"" ;;
  php)    source_file="$root/php/src/Version.php";              pattern="VERSION = '$version';" ;;
  go)     source_file="$root/go/version.go";                    pattern="const Version = \"$version\"" ;;
esac
if ! grep -qF "$pattern" "$source_file"; then
  echo "$tag: ${source_file#"$root/"} does not set version $version" >&2
  exit 1
fi

changelog="$root/$sdk/CHANGELOG.md"
escaped="${version//./\\.}"
if ! grep -qE "^## \[$escaped\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" "$changelog"; then
  echo "$tag: $sdk/CHANGELOG.md has no dated '## [$version] - YYYY-MM-DD' heading" >&2
  exit 1
fi

# The entry runs from its heading to the next "## [" heading or the link references at the end.
awk -v heading="## [$version] - " '
  index($0, heading) == 1 { inside = 1; next }
  inside && (/^## \[/ || /^\[[^]]+\]: /) { exit }
  inside { print }
' "$changelog" | sed -e '/./,$!d'
