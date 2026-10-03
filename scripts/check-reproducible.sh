#!/usr/bin/env bash
# Publishes the current commit twice and compares the hashes, so a change that makes the
# package depend on the builder (an absolute path, a timestamp, a umask, a file order) shows up
# here rather than in a tester's rebuild. Each publish runs in its own fresh clone, at a
# different path and under a different umask, so nothing compiled by an earlier build (obj/,
# bin/) is reused and the compile step itself is compared, not only the packaging. Only
# committed content is checked: uncommitted changes are not in the clones. Takes as long as
# two full publishes.
set -euo pipefail
cd "$(dirname "$0")/.."

commit="$(git rev-parse HEAD)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# publish.sh ends with seven lines: the commit, the SDK, then both hash files and their contents.
run() {
  local dir="$1"
  mkdir -p "$(dirname "$dir")"
  git clone --quiet --no-local . "$dir"
  git -C "$dir" checkout --quiet --detach "$commit"
  (cd "$dir" && ./scripts/publish.sh) | tail -n 7
}

first="$(umask 022 && run "$work/a")"
second="$(umask 077 && run "$work/second/builder/checkout")"
if [[ "$first" != "$second" ]]; then
  echo "check-reproducible: the two publishes differ:" >&2
  diff <(echo "$first") <(echo "$second") >&2 || true
  exit 1
fi
echo "check-reproducible: both publishes match"
echo "$first"
