# robo-rightclick — design and milestone plan

## Context

Explorer's own copy engine copies one file at a time, which is slow for many small files and for network shares. Robocopy's `/MT` copies files in parallel but has no Explorer integration. This project builds a new per-user Windows tray app with three classic right-click items: **Robo-Copy**, **Robo-Cut** and **Robo-Paste**. By default each item does what Explorer's own item does: the same verb, the same states, the same outcome, with one difference: the copying runs in parallel through `robocopy /MT:32`. On top of that: a simple config, a tray icon, queue states, job tracking, job logging, and an **ephemeral mode that writes nothing about jobs to disk**.

Fixed decisions:
- **Classic menu only, ever.** No sparse package, no IExplorerCommand DLL, no signing cert. On Win11 the items appear under "Show more options" / Shift+F10.
- **.NET 10, self-contained single file, win-x64.** Cross-built from Linux. Native AOT is ruled out because it can't be cross-compiled.

## Architecture

```
robo-rightclick/
  src/RoboRightClick.Core/      net10.0          pure logic (no I/O, no Win32) — tested on Linux
  src/RoboRightClick/           net10.0-windows  WinForms tray app + COM server + robocopy runner
  tests/RoboRightClick.Core.Tests/  net10.0     xunit
  scripts/build.sh test.sh publish.sh
  docs/parity.md   docs/testlog.md (append-only)   CLAUDE.md   README.md   LICENSE (MIT)
```

The Windows host's components, threading, COM contracts and work split are in docs/host-architecture.md.

**One process does everything.** `RoboRightClick.exe` runs in the tray. It is also an out-of-process COM server, registered per-user (`HKCU\Software\Classes\CLSID\{…}\LocalServer32`) with `REGCLS_MULTIPLEUSE`. That way every right-click lands in the instance that is already running. If none is running, COM starts it with `-Embedding` and it stays resident.

**Menu items** (HKCU only, so no admin):
- `AllFilesystemObjects\shell\RoboCopy` and `…\RoboCut`
- `Directory\Background\shell\RoboPaste`, `Directory\shell\RoboPaste`, `Drive\shell\RoboPaste`

Each item is registered with `DelegateExecute={its own CLSID}` and `MultiSelectModel=Player`, plus an icon and a display label. With DelegateExecute, Explorer hands over the **entire selection in one call** through `IObjectWithSelection` → `IShellItemArray`, giving real filesystem paths rather than display names. That removes the per-item process spawn, the 15-item cap, and the dependence on whether file extensions are shown. COM interfaces use .NET's `[GeneratedComInterface]`/`[GeneratedComClass]` source generators, with no NuGet dependency. `Execute()` only enqueues the job and returns, so Explorer never waits on a copy.

**Clipboard is the real Windows clipboard.** Robo-Copy and Robo-Cut write `CF_HDROP` plus `Preferred DropEffect` (copy or move), exactly as Explorer does. So both of these work in either direction:
- Robo-Copy, then a plain Ctrl+V.
- A plain Ctrl+C/Ctrl+X, then Robo-Paste.

There's no helper process and no shared memory. After a successful cut-paste, the clipboard is cleared, matching Explorer.

**Install** is `RoboRightClick.exe --install`. It copies itself to `%LOCALAPPDATA%\Programs\RoboRightClick\`, writes the HKCU keys and the default config, and adds an optional `Run` key. `--uninstall` removes exactly what install wrote. Neither command touches any Explorer setting.

## Defaults: what Explorer does, verb by verb and state by state, but MT32

| Situation | Explorer behavior | Robo default |
|---|---|---|
| Copy files | copy into destination | one `robocopy <parent> <dest> f1 f2 …` per source parent, chunked to stay under 32K chars, `/MT:32` |
| Copy folder | recursive, merges into existing folder | `robocopy <dir> <dest>\<name> /E /MT:32` |
| Move, same volume | rename (instant) | `File/Directory.Move`, no robocopy. If there's a name collision, use the robocopy `/MOVE` path |
| Move, cross volume | copy then delete source | `/MOV` (files) or `/MOVE` (dirs). Robocopy deletes each source file **only after that file copied successfully**. The job ends `DoneWithErrors` if anything fails, and **failed sources are never deleted** |
| Name conflict | Replace / Skip / Let me decide dialog | preflight scan, then the same three-choice dialog. Replace = `/IS /IT /IM`; Skip = `/XC /XN /XO`; Decide = per-file list (any Keep-both files are copied in-process as `name (2).ext`) |
| Paste copy into the same folder | `X - Copy`, `X - Copy (2)` | same naming. Folders go through robocopy to the new name; single files use in-process `CopyFileEx` |
| Paste into own subfolder | error for that item, others continue | same message, same continue behavior |
| Per-file error | prompt: Try again / Skip | `/R:0 /W:0` (Explorer doesn't retry silently either). Errors are **collected and shown at the end of the job** as "Try again (these N) / Skip". This is the one deliberate deviation, because robocopy can't block mid-run |
| Cancel | stops; deletes the partially written file | kill robocopy, then delete any destination files that were in flight and didn't exist before the job started |
| Pause / resume | yes | `NtSuspendProcess` / `NtResumeProcess` on the robocopy process |
| Metadata | measured in docs/parity.md | `/COPY:DAT /DCOPY:DA /A+:A /XJD`; remaining deviations are listed in parity.md |
| Concurrent pastes | each runs at once, in parallel | each runs at once (`maxConcurrentJobs: 0` = unlimited) |

Always-on robocopy flags: `/MT:32 /COPY:DAT /DCOPY:DA /A+:A /XJD /NP /NDL /NC /NJH /NJS /BYTES /FP`. Output goes through `/UNILOG:\\.\pipe\<per-run name>` into a named pipe the app owns. M0 showed redirected stdout can't carry non-ASCII names, even with `/UNICODE`. The pipe gives exact UTF-16 and writes nothing to disk in any mode. The pipe is created with a current-user-only ACL and a single instance, and the app checks that the connected client's PID is the robocopy it started.

## Config (simple)

`%APPDATA%\RoboRightClick\config.json`, editable through a single-page Settings window or the tray's "Open config file":

```json
{
  "threads": 32,
  "retries": 0, "retryWaitSeconds": 0,
  "conflictDefault": "ask",          // ask | replace | skip | keepNewer
  "maxConcurrentJobs": 0,            // 0 = unlimited (Explorer parity)
  "logging": "normal",               // normal | ephemeral
  "logRetentionJobs": 100,
  "startWithWindows": true,
  "notifyOnComplete": true,
  "extraArgs": { "copy": "", "move": "" }
}
```

Validation rejects `extraArgs` that break paste semantics or the logging guarantee: `/MIR`, `/PURGE`, `/CREATE`, `/LOG*`, `/UNILOG*`, `/TEE`, `/EFSRAW`, and `/IPG` (which conflicts with `/MT`). An unknown or invalid config falls back to the defaults and shows a one-line tray warning.

## Queue states and tracking

Job lifecycle, implemented as a pure state machine in Core:

```
Queued → Scanning → [AwaitingDecision] → Running ⇄ Paused → Finalizing → Done | DoneWithErrors | Failed | Canceled
```

- `Scanning` matches Explorer's "Calculating…": it enumerates sizes and file counts and runs the conflict preflight.
- `AwaitingDecision` is the conflict dialog, shown before the run.
- `Finalizing` covers move cleanup, clearing the clipboard, and the summary.
- From `DoneWithErrors`, "Try again" spawns a child job containing only the failed items.

**Tracking.** Per job: bytes and files done out of total, speed, ETA, current file(s), error count. Completed files come from robocopy's per-file lines, which under `/MT` arrive as each file finishes. Live bytes come from robocopy's process I/O counters (`GetProcessIoCounters`). Polling destination sizes doesn't work, because robocopy allocates each file at full length up front (M0).

**Jobs window.** One row per job, with pause/resume/cancel, retry failed, open destination, and open log (normal mode only).

**Tray icon** has four states (idle, running, paused, needs attention) plus a distinct tint in ephemeral mode. The tooltip shows a summary such as "3 jobs · 1.2 GB/s · 4 min".

**Tray menu:** Jobs…, Pause all, Resume all, ✓ Ephemeral mode, Settings…, Open logs (hidden in ephemeral), Exit. Exit asks for confirmation if jobs are still running.

## Logging and ephemeral mode

All job output goes through one `IJobSink` interface:
- **Normal mode** composes a `FileJobSink`. It writes `%LOCALAPPDATA%\RoboRightClick\jobs\<yyyyMMdd-HHmmss>-<id>\` containing:
  - `job.json`: verb, sources, destination, effective args, state transitions with timestamps, exit code, failures.
  - `robocopy.log`: a copy of the pipe output, written by us, not by robocopy.

  It also appends to `history.jsonl`. Retention prunes to the last `logRetentionJobs` jobs.
- **Ephemeral mode** composes a `NullJobSink` and keeps history in memory only, cleared on exit. Guarantees:
  - Nothing about a job is written to disk: no job files, no history, and no temp files (none are needed at all). Robocopy's own log goes only into the in-memory pipe.
  - Completion toasts are generic ("Job finished") and contain **no paths**, because Windows keeps toast text in the notification center.
  - Clipboard writes set `ExcludeClipboardContentFromMonitorProcessing`, so file lists stay out of clipboard history and cloud clipboard.
  - The only file write is the `logging` setting itself in config.json.

  Switching ephemeral mode on while normal-mode jobs are running affects only new jobs. The tray says so.

## Milestones (each gated, recorded in `docs/testlog.md`)

**M0: spikes on a disposable Windows 11 test VM** (spikes 2–4 done 2026-10-02, see docs/testlog.md; 1 and 5 need the host) with no network access. Gate for M2 and later. Machine-specific wrappers for the VM live in the gitignored `scripts/local/`; VM details never go into this repo.
1. Out-of-process DelegateExecute with a multi-use server: a 500-item selection from the classic menu arrives in **one** call to an already-running process. Repeat for the background, folder and drive items.
2. Redirected `robocopy /UNICODE /MT:32` stdout decodes correctly as UTF-16, with emoji and CJK filenames. Record exactly which lines appear, and when, for files that are starting versus finishing.
3. `NtSuspendProcess` reliably pauses an `/MT:32` robocopy and resumes it cleanly.
4. **Explorer parity capture:** copy and move one test tree with `Shell.Application.CopyHere`/`MoveHere`, which uses Explorer's own engine, then diff the metadata. The tree covers timestamps, attributes, alternate data streams, hidden/system files, read-only files, junctions, symlinks, long paths, and empty dirs. Record the results in `docs/parity.md` and settle the flags marked "per M0" above.
5. Check whether Explorer ghosts icons after a Robo-Cut clipboard write. If it doesn't, list that as a known deviation.
6. If (1) fails, the fallback is command-line verbs plus a single-instance named-pipe aggregator with a short debounce. That gets written up as an ADR first, before any code.

**M1: Core + tests on Linux** (`./scripts/test.sh`). Covers:
- Selection → job planner: grouping by parent, chunking, same-volume vs cross-volume, self/subfolder guards, `- Copy` naming.
- Argument builder per verb × conflict policy.
- Validation that rejects forbidden `extraArgs`.
- stdout line parser, using fixtures captured in M0.
- Exit-code bitmask decoder (≥8 = failure).
- State machine transitions.
- Config defaults and validation.
- Retention planner.
- A composition test proving ephemeral mode wires no file-writing sink.

**M2: Windows host.** Tray, COM server, register/unregister, clipboard read/write, robocopy runner (process, parse, pause, cancel, partial-file cleanup), conflict dialog, Jobs window, Settings window. All of it cross-builds via `./scripts/build.sh`. Any claim about runtime behavior must cite an entry in testlog.

**M3: logging + ephemeral mode.** FileJobSink, retention, the ephemeral toggle, path-free toasts, the clipboard exclusion format.

**M4: end-to-end on the VM.** Driven through scripted mouse and keyboard input plus screen captures, with the right-click actually performed in Explorer.
- Parity diff against Explorer on the M0 tree.
- Failure injection: a locked file, a destination with access denied, and a full disk (a small attached VHD). Proof that **cut never deletes a source whose copy failed**.
- Cancel mid-run removes partial files.
- **Ephemeral audit:** snapshot `%APPDATA%`, `%LOCALAPPDATA%` and `%TEMP%` plus the toast DB before and after five ephemeral jobs. Expect zero new files and zero occurrences of the test path strings.
- Throughput vs Explorer: 10k small files and 2× 4 GB files. Record the numbers and don't advertise speedups that weren't measured.

**M5: release.** `scripts/publish.sh` produces a self-contained single-file win-x64 build (`EnableCompressionInSingleFile`), zipped with a SHA256. The README covers install/uninstall and the "Show more options" placement, with no slogans. Tag `v0.1.0`. There's no GitHub Actions in v1 because builds are local. If CI is added later, actions must be SHA-pinned with minimal permissions.

## Verification summary

- **Linux:** `./scripts/build.sh` (cross-build) and `./scripts/test.sh` (all Core tests) green on every commit.
- **Windows:** every runtime claim cites a dated `docs/testlog.md` entry from the VM. "Cross-compiles; unverified on Windows" is stated wherever that's all we have.
- **Physical machine:** a final smoke test of install, three verbs, uninstall, and checking that the HKCU keys are gone. It's recorded as a separate testlog entry, not assumed from the VM run.
