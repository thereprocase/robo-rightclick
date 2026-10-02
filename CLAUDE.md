# CLAUDE.md — agent operating instructions

robo-rightclick is a Windows tray app adding classic right-click **Robo-Copy**,
**Robo-Cut** and **Robo-Paste** items. Each one behaves like Explorer's own verb, item by
item and state by state. The only difference is that bulk data moves through
`robocopy /MT:32`. The design and milestone plan are in docs/design.md.

You build on Linux. The target is Windows. That split drives most of the rules below.

## This repository is public

- Commit as the configured identity (`repro <repro@local>`). No AI attribution and no
  personal names in commits, branches, changelogs or docs.
- Never commit anything about the private test VM: paths, keys, hostnames or ports.
  Wrappers that call it live in `scripts/local/`, which is gitignored.
- README and docs state facts. No slogans or taglines.

## Linux/Windows split

- Everything OS-independent goes in `RoboRightClick.Core` (net10.0): planning, robocopy
  arguments, output parsing, the state machine, config, and log naming/pruning. It must not
  read the file system or the clock, or call Win32. Time and file-system facts come in as
  data. Core is the only code this machine can actually verify, so put as much as possible there.
- `RoboRightClick` (net10.0-windows, WinForms, self-contained win-x64) holds the tray, the COM
  server, clipboard, registry, process control and windows. It is cross-compiled here and
  verified only on Windows.

## Verification honesty

- "It builds" and "it works" are different claims. Never present the first as the second.
- Any statement about Windows runtime behavior must cite a dated entry in
  `docs/testlog.md` (Windows build, VM or physical machine, what ran, what was observed).
  docs/testlog.md is append-only.
- Code that has only been cross-compiled gets "cross-compiles; unverified on Windows" in its
  commit body.
- A test that has only ever been seen passing proves nothing. For a safety rule, break the
  code on purpose once and confirm the test fails.

## Product invariants: never weaken these without an ADR in docs/decisions/

1. **A cut never deletes a source whose copy failed.** Cross-volume moves rely on
   robocopy `/MOV`/`/MOVE` deleting each file only after it has copied. The app itself never
   deletes sources after a robocopy run.
2. **Ephemeral mode writes nothing about jobs to disk.** All job data goes through
   `IJobSink`, and ephemeral mode composes `NullJobSink`. Robocopy's only log target is
   `/UNILOG:\\.\pipe\<name>`, a named pipe the app owns. Never a file, never `/LOG` or `/TEE`.
   Toasts carry no paths. Clipboard writes are excluded from clipboard history.
3. **Defaults match Explorer.** Explorer's actual behavior is recorded in docs/parity.md. If
   the defaults deviate from it, the deviation is listed there with the reason.
4. **Install is per-user and leaves Explorer settings alone.** Only HKCU keys that install
   wrote, and that uninstall removes.

## Code standards

- Clarity over cleverness. Comments explain *why*. No jokes in code, comments or names.
- Conventional commits, each leaving `./scripts/build.sh` and `./scripts/test.sh` green.
- Dependencies: none at runtime. Test packages are pinned and lockfiles are committed
  (restore runs in locked mode). Every dependency change is its own reviewable commit.
