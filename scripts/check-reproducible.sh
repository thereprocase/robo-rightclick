#!/usr/bin/env bash
# Publishes the current commit twice from clean outputs and compares the hashes, so a change
# that makes the package depend on the builder (a timestamp, a umask, a file order) shows up
# here rather than in a tester's rebuild. Takes as long as two publishes.
set -euo pipefail
cd "$(dirname "$0")/.."

# publish.sh ends with six lines: the SDK, then both hash files and their contents.
run() {
  ./scripts/publish.sh | tail -n 6
}

first="$(umask 022 && run)"
second="$(umask 077 && run)"
if [[ "$first" != "$second" ]]; then
  echo "check-reproducible: the two publishes differ:" >&2
  diff <(echo "$first") <(echo "$second") >&2 || true
  exit 1
fi
echo "check-reproducible: both publishes match"
echo "$first"
