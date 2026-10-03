#!/usr/bin/env bash
# Run the Linux-runnable test suites (Core only; the host needs Windows), after the
# source checks below.
set -euo pipefail
cd "$(dirname "$0")/.."

# The Robo-Paste hotkey's keyboard hook stays in two files, and neither may reach a log, a
# file, the console or a job sink: a keyboard hook that could record keys would be a key
# logger, whatever it was meant for (docs/decisions/0001-paste-hotkey.md). grep rather than
# rg so the check runs wherever bash does, including Git Bash on Windows.
hook_files=(src/RoboRightClick/App/PasteHotkey.cs src/RoboRightClick/App/KeyboardHookNative.cs)
for file in "${hook_files[@]}"; do
  [ -f "$file" ] || { echo "hook confinement: $file is missing" >&2; exit 1; }
done
outside=$(grep -rlE 'SetWindowsHookEx|KBDLLHOOKSTRUCT|GetAsyncKeyState' --include='*.cs' src tests \
  | grep -vxF -e "${hook_files[0]}" -e "${hook_files[1]}" || true)
if [ -n "$outside" ]; then
  echo "hook confinement: keyboard-hook APIs used outside ${hook_files[*]}:" >&2
  echo "$outside" >&2
  exit 1
fi
sinks=$(grep -nE '\b(IJobSink|NullJobSink|FileJobSink|JobLogStore|CrashLog|ILogger|Logger|Trace|Debug|Console|EventLog|StreamWriter|FileStream|FileInfo|FileMode)\b|\b(File|Directory)\s*\.|\bSystem\.IO\b|\bOutputDebugString' \
  "${hook_files[@]}" || true)
if [ -n "$sinks" ]; then
  echo "hook confinement: the hook files must not reference a log, a file, the console or a job sink:" >&2
  echo "$sinks" >&2
  exit 1
fi

for suite in tests/*/; do
  compgen -G "$suite*.csproj" >/dev/null || continue
  dotnet test "$suite" -c Release
done
