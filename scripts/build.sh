#!/usr/bin/env bash
# Cross-build every project from Linux. Compiling the Windows host proves it
# compiles, nothing more; runtime claims need a docs/testlog.md entry.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build RoboRightClick.slnx -c Release
