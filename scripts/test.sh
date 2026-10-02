#!/usr/bin/env bash
# Run the Linux-runnable test suites (Core only; the host needs Windows).
set -euo pipefail
cd "$(dirname "$0")/.."
for suite in tests/*/; do
  compgen -G "$suite*.csproj" >/dev/null || continue
  dotnet test "$suite" -c Release
done
