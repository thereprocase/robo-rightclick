# Host architecture (M2/M3)

The Windows host, `src/RoboRightClick`, and the Core contracts it depends on. The plan this
implements is docs/design.md; the decisions there are fixed. This document makes them
precise enough that several people can each own a set of files and build in parallel.

**Status:** the skeleton cross-compiles. Nothing in the host has run on Windows. Runtime facts
cited below come from docs/testlog.md (2026-10-02, M0 spikes 2-4). Everything else is a
design claim until a testlog entry says otherwise.

## 1. Component map

One process, `RoboRightClick.exe`. Arrows are "calls".

```
Explorer ──COM (out-of-proc)──▶ Com/ClassFactory ─▶ Com/VerbCommand ─▶ IVerbHandler
CLI (same exe) ─▶ Cli/CliRunner ─▶ Com/ComClient ──COM──┘                   │
                                                                            ▼
                                Verbs/VerbDispatcher ─▶ Verbs/ClipboardService (Win32 clipboard)
                                          │
                                          ▼
UI/* ◀── events / snapshots ── Jobs/JobManager ─▶ Jobs/Job ─▶ Jobs/RobocopyRun ─▶ robocopy.exe
  ▲                                       │            │          ├─ Jobs/RobocopyPipe (/UNILOG pipe)
  │                                       │            │          └─ Jobs/ProgressSampler (IO counters)
App/TrayApplication (composition root)    │            ├─ Jobs/InProcessCopier (rename, CopyFileEx)
                                          │            └─ Core: PastePlanner, JobScanner, ExecutionPlanner,
                                          ▼                     RobocopyArgs, RobocopyOutputParser,
                         Logging/JobLogStore + FileJobSink      CancelCleanup, JobOutcome, RetryPlanner
                         (normal mode only, via JobSinks.For)
```

### Host files (`src/RoboRightClick`)

| File | Purpose |
|---|---|
| `Program.cs` | `[STAThread]` entry; `CommandLine.Parse` and dispatch. Done. |
| `App/TrayApplication.cs` | Composition root, tray icon and menu, start/exit order (doc comment is the spec). |
| `App/SingleInstance.cs` | Session mutex, cross-process exit-request event. |
| `App/SettingsStore.cs` | config.json load (per-field fallback), atomic save, change event. |
| `App/TrayIcons.cs` | GDI+-drawn icons per `TrayIconState` × ephemeral tint. |
| `App/HostEnvironment.cs` | `AppPaths`, version string, exe path. Done. |
| `App/AppNative.cs` | `AttachConsole`, `DestroyIcon`. Done. |
| `Com/ComInterfaces.cs` | `[GeneratedComInterface]` declarations, HRESULTs, shell constants. Done. |
| `Com/ComNative.cs` | ole32/shell32 imports, the process `StrategyBasedComWrappers`. Done. |
| `Com/ClassFactory.cs` | `IVerbHandler` boundary; one `IClassFactory` per verb. |
| `Com/VerbCommand.cs` | `IExecuteCommand` + `IObjectWithSelection` + `IInitializeCommand`. |
| `Com/ComServer.cs` | `CoRegisterClassObject` ×3 on the UI thread, revoke on exit. |
| `Com/ShellSelection.cs` | `IShellItemArray` → paths (server); paths → `IShellItemArray` (client). |
| `Com/ComClient.cs` | CLI side: `CoCreateInstance` + `SetSelection` + `Execute`. |
| `Verbs/VerbDispatcher.cs` | What copy / cut / paste do once paths arrive. |
| `Verbs/ClipboardService.cs` | Raw Win32 clipboard with a message-only owner window. |
| `Verbs/ClipboardNative.cs` | user32/kernel32/shell32 clipboard imports. Done. |
| `Jobs/JobManager.cs` | All jobs: enqueue, queue policy, control, snapshots, events, retry. |
| `Jobs/Job.cs` | One job's lifecycle (doc comment is the flow spec); `JobServices`, `JobStart`. |
| `Jobs/PauseGate.cs` | A job's pause switch shared by its steps. |
| `Jobs/RobocopyRun.cs` | One robocopy process: pipe, parser, sampler, suspend/resume, kill. |
| `Jobs/RobocopyPipe.cs` | Current-user-only single-instance pipe, client PID check, UTF-16 lines. |
| `Jobs/ProgressSampler.cs` | One app-wide timer reading `GetProcessIoCounters`. |
| `Jobs/InProcessCopier.cs` | Rename steps (`MoveFileEx`, flags 0) and single-file copies (`CopyFileEx`). |
| `Jobs/FileSystemFacts.cs` | `IPlanningFacts` + `IScanFacts` from the real disk. |
| `Jobs/IJobPrompts.cs` | The conflict question a job asks the UI. |
| `Jobs/ProcessNative.cs` | ntdll/kernel32 imports: suspend, IO counters, pipe PID, copy, job object. Done. |
| `Logging/JobLogStore.cs` | Log folder layout, history append, pruning, sink factory. |
| `Logging/FileJobSink.cs` | Normal-mode `IJobSink`: job.json, robocopy.log, history line. |
| `UI/JobsWindow.cs` | Jobs list with progress and row actions. |
| `UI/SettingsWindow.cs` | Single-page settings editor. |
| `UI/ConflictDialog.cs` | Replace / Skip / Let me decide (per-file list). |
| `UI/ErrorSummaryDialog.cs` | End-of-job errors: Try again (N) / Skip. |
| `UI/UiPrompts.cs` | `IJobPrompts` on the UI thread (modeless dialogs). |
| `UI/Notifier.cs` | Balloon toasts; text only from `ToastText`. |
| `Install/Installer.cs` | `--install` / `--uninstall` sequences. |
| `Install/RegistryWriter.cs` | Executes Core's registry lists against HKCU. |
| `Cli/CliRunner.cs` | Verb invocation from the command line, usage, console attach. |

"Done" means the file is complete for its purpose (declarations or wiring), not that it ran on Windows.

### Core additions (`src/RoboRightClick.Core`)

| File | Purpose | State |
|---|---|---|
| `AppPaths.cs` | `AppInfo` names; every on-disk location derived from the two known folders. | implemented, tested |
| `ShellVerbs.cs` | Verb table: CLSIDs, labels, associations; paste-destination rule. | implemented, tested |
| `Registration.cs` | HKCU install values and uninstall removals, as data. | implemented, tested |
| `CommandLine.cs` | CLI grammar → `CliCommand`; exit codes. | implemented, tested |
| `ClipboardPayload.cs` | `DROPFILES` and `Preferred DropEffect` bytes; ephemeral exclusion formats; paste verb rule. | implemented, tested |
| `JobScan.cs` | `IScanFacts`, `ScanResult`; `JobScanner.Scan`. | contract only |
| `ConflictDecisions.cs` | `ConflictChoice`, `FileDecision`, `KeepBothStep`, `ExecutionPlan`; `ExecutionPlanner.Apply`. | contract only |
| `JobOutcome.cs` | `StepOutcome`, `JobOutcome.FinalState`; `RetryPlanner.ForFailures`. | FinalState tested; retry contract only |
| `CancelCleanup.cs` | Which destination files a cancel deletes. | implemented, tested, sabotage-checked |
| `JobRuntime.cs` | `JobQueuePolicy`, `PipeNames`. | implemented, tested |
| `TrayStatus.cs` | `JobSnapshot`; tray icon state and tooltip. | implemented, tested |
| `DisplayText.cs` | Sizes (StrFormatByteSize style), speed, durations, source summaries. | implemented, tested |
| `ToastText.cs` | Toast text; ephemeral toasts carry no names. | implemented, tested |
| `JobRecords.cs` | job.json and history.jsonl formats. | contract only |

Changed: `IJobSink` gained `CommandFinished(jobId, exitCode)` (job.json records exit codes);
`RobocopyArgs.Build` gained an optional `excludedFiles` (`/XF`, full source paths).

## 2. Threading model

| Thread | Runs | Rules |
|---|---|---|
| **UI (main, STA)** | WinForms message loop; COM class objects (registered here, so activations and every `VerbCommand` call arrive here through the loop); clipboard (owner window lives here); all forms, NotifyIcon, toasts. | Never blocks. `VerbCommand.Execute` reads the selection, calls `IVerbHandler.Invoke`, returns. `VerbDispatcher` posts its work back to the same context, so Explorer's call returns at once and verbs keep click order. |
| **Job workers** | `Job.RunAsync` on the thread pool, one logical flow per job; steps sequential inside a job, jobs parallel (`JobQueuePolicy`). | Never touch UI objects. Ask the user only through `IJobPrompts` (awaits a task). Clipboard clear goes through `JobServices.ClearClipboardIfUnchanged`, which posts to the UI thread. |
| **Pipe reader** | Per robocopy run: async read loop on `RobocopyPipe`, feeding `RobocopyOutputParser` and the sink. | Raises `IRobocopyObserver` callbacks on its own thread; the job applies them under its lock. |
| **Progress sampler** | One `ProgressSampler` timer (333 ms) for the app. | Reads IO counters of tracked processes; callbacks on the thread pool; a failed read skips the tick. |

Shared state: each `Job` guards lifecycle, progress and error list with one private lock;
`Snapshot()` copies under it into an immutable `JobSnapshot`. `JobManager` guards its job list
with its own lock and never holds it while calling into a job. UI updates: `JobManager.Changed`
is coalesced (≤ ~4/s) and posted to the UI thread; listeners pull `Snapshot()`. The Jobs window
additionally polls every 250 ms while visible. Time comes from `TimeProvider.System` (in .NET,
no package) and is passed into Core types.

## 3. COM server

### Interfaces

Vtable order is the IDL order, after `IUnknown`'s three slots. The Microsoft Learn method tables
are alphabetical and must not be used for ordering. IIDs and order were checked against
`shobjidl_core.h` as mirrored in Wine's and ReactOS's `shobjidl.idl`; `IClassFactory` is
`unknwn.h`. Declared in `Com/ComInterfaces.cs`, all methods `[PreserveSig]`.

| Interface | IID | Methods in vtable order |
|---|---|---|
| `IClassFactory` | `00000001-0000-0000-C000-000000000046` | CreateInstance(pUnkOuter, riid, ppv), LockServer(fLock) |
| `IExecuteCommand` | `7F9185B0-CB92-43C5-80A9-92277A4F7B54` | SetKeyState(DWORD), SetParameters(LPCWSTR), SetPosition(POINT), SetShowWindow(int), SetNoShowUI(BOOL), SetDirectory(LPCWSTR), Execute() |
| `IObjectWithSelection` | `1C9CD5BB-98E9-4491-A60F-31AACC72B83C` | SetSelection(IShellItemArray*), GetSelection(riid, ppv) |
| `IInitializeCommand` | `85075ACF-231F-40EA-9610-D26B7B58F638` | Initialize(LPCWSTR pszCommandName, IPropertyBag* ppb) |
| `IShellItemArray` | `B63EA76D-1F85-456F-A19C-48159EFA858B` | BindToHandler, GetPropertyStore, GetPropertyDescriptionList, GetAttributes, GetCount, GetItemAt, EnumItems |
| `IShellItem` | `43826D1E-E718-42EE-BC55-A1E261C37BFE` | BindToHandler, GetParent, GetDisplayName(SIGDN, LPWSTR*), GetAttributes, Compare |

`SIGDN_FILESYSPATH = 0x80058000`. Strings from `GetDisplayName` are freed with
`Marshal.FreeCoTaskMem`. Unused methods keep placeholder signatures because they hold vtable slots.

### Identities (fixed; changing one orphans installed keys)

| Verb | CLSID |
|---|---|
| Robo-Copy | `{BD15DC6A-FBC1-4949-B61D-3B8FC390062F}` |
| Robo-Cut | `{1A061376-A3F7-41BF-A516-E635ED91ACDF}` |
| Robo-Paste | `{9D1BAE79-13C3-427F-A7E6-34150D5C49AB}` |
| AppID (shared) | `{B708F29C-8ED8-40BD-832E-F05180F1B285}` |

### Activation and call sequence

1. Tray start: `ComServer.Register` calls `CoRegisterClassObject(clsid, factory,
   CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED)` for all three, then
   `CoResumeClassObjects`. Pointers come from `ComNative.Wrappers` (`StrategyBasedComWrappers`).
2. Not running: COM starts `"<exe>" -Embedding`, which parses to `CliRunTray(StartedByCom: true)`;
   the tray starts normally and stays resident.
3. Explorer: `CreateInstance` → (`IInitializeCommand::Initialize`) → `SetSelection` → setters →
   `Execute`. `Execute` reads all paths synchronously (`ShellSelection.ReadPaths`: the array is a
   cross-process proxy), releases the array, calls `IVerbHandler.Invoke`, returns `S_OK`.
4. No exception crosses the COM boundary; every implementation catches and returns an HRESULT.
5. Exit: revoke first, then cancel jobs, then release the single-instance mutex.

The pattern (DelegateExecute to an out-of-process local server) is Microsoft's
`ExecuteCommandVerb` sample (Windows-classic-samples, Win7Samples/winui/shell/appshellintegration),
which uses `REGCLS_SINGLEUSE`; `REGCLS_MULTIPLEUSE` is this design's change and is what spike 1
must prove.

## 4. Registration data

Produced by `Registration.InstallValues(exe, startWithWindows)`; all `REG_SZ` under
`HKEY_CURRENT_USER`. Uninstall deletes exactly `Registration.UninstallRemovals()`: the key trees
marked ⌫ and the Run value. Tests prove every written value is covered and no shared parent
(`...\shell`, `...\CLSID`) is ever deleted.

| Key (under `HKCU\`) | Value | Data |
|---|---|---|
| ⌫ `Software\Classes\AppID\{B708F29C-…}` | (default) | `RoboRightClick` |
| ⌫ `Software\Classes\CLSID\{verb clsid}` | (default) / `AppID` | `RoboRightClick Robo-Copy` / `{B708F29C-…}` |
| `…\CLSID\{verb clsid}\LocalServer32` | (default) | `"<install dir>\RoboRightClick.exe"` |
| ⌫ `Software\Classes\<assoc>\shell\<Verb>` | `MUIVerb` / `MultiSelectModel` | `Robo-Copy` / `Player` |
| `…\shell\<Verb>\command` | `DelegateExecute` | `{verb clsid}` |
| `Software\Microsoft\Windows\CurrentVersion\Run` | `RoboRightClick` | `"<exe>"` (only with startWithWindows; always removed) |

Associations: RoboCopy and RoboCut on `AllFilesystemObjects`; RoboPaste on
`Directory\Background`, `Directory`, `Drive`. `DelegateExecute` sits on the verb's `command`
subkey (as in the Microsoft sample), `MultiSelectModel` on the verb key. No `Icon` value yet
(the exe has no icon resource; see open questions).

## 5. Verbs and clipboard

- **Robo-Copy / Robo-Cut:** `ClipboardService.WriteFiles(paths, verb, mode)` writes every entry of
  `ClipboardPayload.ForFiles`: `CF_HDROP` (wide `DROPFILES`), `Preferred DropEffect` (1 copy, 2
  move) and, in ephemeral mode, `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0`. No job starts.
- **Robo-Paste:** destination = `ShellVerbs.PasteDestination(selection)` (exactly one item; the
  background verb passes the folder itself). `ClipboardService.ReadFiles` reads `CF_HDROP` (Core
  decoder; `DragQueryFileW` for ANSI) and `Preferred DropEffect`; verb =
  `ClipboardPayload.VerbForPaste` (move only for a pure move marker, so ambiguity means copy). Plain
  Ctrl+C / Ctrl+X data works the same way. `JobManager.Enqueue(request, cut ? sequence : null)`.
- **After a cut-paste ends `Done`:** `ClearIfUnchanged(sequence)`; a newer clipboard write by
  anyone is left alone. `DoneWithErrors` keeps the clipboard (its sources still exist).
- Clipboard calls are UI-thread only; `OpenClipboard` is retried briefly when another process
  holds it.

## 6. Job engine contracts

Pipeline per job (`Job.RunAsync`; the `Job` doc comment is normative):

| State | Work | Core contract |
|---|---|---|
| Queued | Waits for `JobQueuePolicy.FreeSlots` (0 = unlimited). | `JobQueuePolicy` |
| Scanning | `PastePlanner.Plan(request, facts)`, then `JobScanner.Scan(plan, facts, ct)`: planned files, totals, directory links, conflicts. | `PastePlan`, `ScanResult` |
| AwaitingDecision | Only if conflicts and policy `Ask`: `IJobPrompts.ResolveConflictsAsync`; null = Canceled. | `ConflictChoice`, `ConflictScan.Resolve` |
| Running | Steps of `ExecutionPlanner.Apply(scan, configured, choice, taken)` in order. | `ExecutionPlan` |
| Paused | `PauseGate` closed: robocopy `NtSuspendProcess`, CopyFileEx callback blocks, loop waits between steps. | |
| Finalizing | Create `LinkFolders` empty; clear clipboard after a `Done` cut; build `JobSummary`. | `JobOutcome.FinalState` |
| terminal | `Done` / `DoneWithErrors` / `Failed` / `Canceled`; `Finished` event → toast. | `ToastText.ForFinished` |

Step execution:

- `RobocopyStep` → `RobocopyRun` with `RobocopyArgs.Build(step, settings, policy,
  PipeNames.ForStep(jobId, index, csprngNonce), excludedFiles)`. Robocopy from
  `%SystemRoot%\System32\robocopy.exe` (absolute), no window, stdout drained and discarded,
  placed in a kill-on-close job object. Pipe created **before** start; first connection's PID
  must equal the process ID or the step fails without reading. Lines → parser → `FileReported`
  (completed file + bytes), `ErrorReported` (collected); raw lines → `sink.OutputLine`.
- Progress: completed bytes from `FileReported`; live bytes = bytes of finished steps + this run's
  `ReadTransferCount`, into `JobProgress.SetObservedBytes`.
- `RenameStep` → `MoveFileEx(src, dst, 0)` for files, `Directory.Move` for folders. Neither copies
  across volumes nor overwrites. `ERROR_NOT_SAME_DEVICE` re-plans that item as a robocopy `/MOVE`
  step. This tightens design.md's "File/Directory.Move": `File.Move` would silently copy and delete
  across volumes, an app-initiated source deletion that invariant 1 forbids.
- `DuplicateFileStep`, copy-mode `KeepBothStep` → `CopyFileEx(COPY_FILE_FAIL_IF_EXISTS)`.
- **Cancel** (any non-terminal state): kill robocopy and wait; then
  `CancelCleanup.Select(startedFiles, completedSources, preExisting, move, sourceStillExists)` and
  delete `Delete` entries if present; report `LeftInPlace`. Rules (each tested, and each test seen
  failing with the rule removed): reported-complete files stay; pre-existing destinations are never
  deleted; for a cut, a destination whose source is gone stays (robocopy may have finished and
  deleted the source before its line was read).
- **Errors** → `DoneWithErrors`; "Try again (N)" = `JobManager.Retry(parent)` →
  `RetryPlanner.ForFailures` (Replace policy, same Move flag, failed files only), a child job with
  `ParentId`, skipping Scanning and the prompt.
- **Invariant 1:** the job never deletes a source. Only robocopy `/MOV`/`/MOVE` and renames move
  anything.

## 7. UI surfaces

- **Tray** (`TrayApplication`): icon and tooltip from `TrayStatus.Derive(snapshots, mode)`
  (attention > running > paused > idle; tooltip ≤ 127 chars). Menu: Jobs…, Pause all, Resume all,
  Ephemeral mode (check), Settings…, Open logs (hidden in ephemeral), Exit (confirms when jobs are
  active). Double-click opens Jobs.
- **Jobs window**, **Settings window**, **Conflict dialog**, **Error summary**: specified in the
  doc comments of their files. All built in code, no designer files or resources. Dialogs are
  modeless, because several jobs can ask at once.
- **Toasts** (`Notifier`): `NotifyIcon.ShowBalloonTip` with `ToastText` only. A clean finish obeys
  `notifyOnComplete`; errors always notify; cancel never does. A balloon click opens Jobs at that job.

## 8. Logging and ephemeral mode

- The sink is chosen once per job at creation: `JobSinks.For(settings.Logging,
  () => logStore.CreateSink(job))`. In ephemeral mode the factory is never invoked.
- Normal: `jobs\<JobLogNames.FolderName>\job.json` (`JobRecords.ToJson`, rewritten on each state
  change), `robocopy.log` (UTF-8 copy of the pipe lines with one header per command),
  `history.jsonl` (`JobRecords.ToHistoryLine`), then `JobLogStore.Prune()` to `logRetentionJobs`.
  Log write failures never fail a job.
- Ephemeral: `NullJobSink`, in-memory history only (`JobManager.InMemoryHistoryLimit`),
  path-free toasts, clipboard exclusion formats, no temp files. The one write is config.json when
  the mode is toggled.
- Toggling affects new jobs only; jobs carry their own `LoggingMode` in `JobSnapshot.Logging`, and
  `ToastText` decides on the job's mode, not the current one.

## 9. CLI

`CommandLine.Parse` (Core) defines the grammar; `CommandLine.Usage` is the help text.

| Command | Effect |
|---|---|
| *(none)* | Tray (exits 0 if one is already running). |
| `-Embedding` | Tray started by COM. |
| `--install [--no-autostart]` | Install (section 10). |
| `--uninstall` | Uninstall (section 10). |
| `copy <path>...`, `cut <path>...`, `paste <folder>` | `ComClient.Invoke`: `CoCreateInstance(CLSCTX_LOCAL_SERVER)` on the verb's CLSID, `SetSelection(SHCreateShellItemArrayFromIDLists(SHParseDisplayName(...)))`, `Execute`. The same path as a right-click, started by the CLI process instead of Explorer. Returns when the verb is accepted. |

Exit codes: 0 ok, 1 failed, 2 usage. Output goes to the parent console via `AttachConsole`.

## 10. Install and uninstall

- `--install`: stop a running tray (exit request; refused while jobs run) → copy exe to
  `%LOCALAPPDATA%\Programs\RoboRightClick\` → write `Registration.InstallValues` → write the default
  config.json only if absent → start the installed tray.
- `--uninstall`: stop the tray → remove `Registration.UninstallRemovals()` → delete config.json,
  the data folder (logs, history) and the install folder (via a short-lived `cmd /c` when running
  from it). Nothing outside `AppPaths` and the registry list.
- No Explorer setting, no Explorer restart, no admin rights.

## 11. Error-handling principles

1. Nothing throws into Explorer: COM methods return HRESULTs.
2. Nothing in a job throws out of `RunAsync`: unexpected exceptions end the job `Failed` with the
   message kept in memory (and in job.json in normal mode).
3. When in doubt, keep data: cancel cleanup, clipboard clearing and uninstall pruning all err
   towards leaving files in place.
4. Untrusted input is checked at the boundary: pipe client PID, clipboard byte layouts (Core
   decoders tolerate truncation), config fields (per-field fallback), CLI arguments.
5. Logging and toasts are best-effort and never change a job's outcome.
6. No crash log in the beta. A crash file would be a disk write that ephemeral mode forbids, and
   normal mode does not need one yet.

## 12. Verified vs unverified on Windows

| Claim | Status |
|---|---|
| `/UNILOG` to a named pipe carries exact UTF-16; stdout cannot | verified (testlog 2026-10-02) |
| Under `/MT:32` file lines arrive at completion; failed file line precedes its ERROR | verified (testlog 2026-10-02) |
| `GetProcessIoCounters` tracks progress; destination size does not | verified (testlog 2026-10-02) |
| `NtSuspendProcess`/`NtResumeProcess` pause and resume `/MT:32` cleanly | verified (testlog 2026-10-02) |
| Metadata flags vs Explorer | verified, deviations in docs/parity.md |
| Out-of-process DelegateExecute with `REGCLS_MULTIPLEUSE`: 500 items in one call to a running instance | **unverified** (M0 spike 1) |
| `-Embedding` start when the tray is not running; behavior when a Run-key start races it | **unverified** |
| Explorer ghosts icons after a Robo-Cut clipboard write | **unverified** (M0 spike 5) |
| `/XF` with full source paths excludes exactly those files | **unverified** |
| `CopyFileEx` sets the archive bit like Explorer's copy | **unverified** |
| Generated COM vtables match the shell's (`[GeneratedComInterface]` on these IDLs) | **unverified**; compile-time only |
| Everything else in the host | **unverified**; cross-compiles only |

## 13. Work packages (disjoint file ownership)

| Package | Files |
|---|---|
| A. COM server + CLI client | `Com/ClassFactory.cs`, `Com/VerbCommand.cs`, `Com/ComServer.cs`, `Com/ShellSelection.cs`, `Com/ComClient.cs`, `Cli/CliRunner.cs` |
| B. Clipboard + verbs | `Verbs/ClipboardService.cs`, `Verbs/VerbDispatcher.cs` |
| C. Core job logic (with tests) | Core `JobScan.cs` (`JobScanner`), `ConflictDecisions.cs` (`ExecutionPlanner`), `JobOutcome.cs` (`RetryPlanner`) |
| D. Robocopy runner | `Jobs/RobocopyRun.cs`, `Jobs/RobocopyPipe.cs`, `Jobs/ProgressSampler.cs`, `Jobs/InProcessCopier.cs`, `Jobs/FileSystemFacts.cs` |
| E. Job orchestration | `Jobs/Job.cs`, `Jobs/JobManager.cs`, `Jobs/PauseGate.cs` |
| F. Logging | `Logging/*`, Core `JobRecords.cs` (with tests) |
| G. UI | `UI/*`, `App/TrayIcons.cs` |
| H. App shell + install | `App/TrayApplication.cs`, `App/SingleInstance.cs`, `App/SettingsStore.cs`, `Install/*` |

Contracts between packages are the types already in the skeleton. A package that needs a
contract changed changes it in its own commit and says so.

## 14. Open questions

1. **Keep-both on a cut** needs an OS move (`MoveFileWithProgress` + `MOVEFILE_COPY_ALLOWED`) as a
   second source-deleting path beside robocopy `/MOV`. That needs an ADR under invariant 1. Until
   then the dialog offers no keep-both for cuts, which deviates from Explorer and must be listed in
   docs/parity.md.
2. **"Let me decide" with very many skips:** the `/XF` list can exceed the command-line budget;
   `ExecutionPlanner` refuses such a step as a `PlanIssue`. A better split (per-subfolder steps)
   may be needed if this is hit in practice.
3. **Preferred DropEffect for copy:** the design fixes 1. Explorer's own Ctrl+C is commonly
   reported as 5 (copy | link). Measure in M4. Both values paste as a copy.
4. **Menu icon:** without an `.ico` the verbs have no icon. Adding one means a binary asset and
   an `Icon` registry value.
5. **Uninstall deletes config and logs** (the privacy-preserving choice). A `--keep-data` flag is
   possible if users want settings to survive reinstall.
6. **Retry when robocopy exits ≥ 8 with no per-file ERROR line:** there is nothing to target;
   currently `DoneWithErrors` without a retry set. Decide whether to retry the whole step.
7. **Partially overwritten pre-existing files on cancel** are reported, not restored. Explorer's
   behavior here is unmeasured.
8. **CLI `--wait`** (block until the paste finishes) would make VM automation simpler. It needs a
   second channel, since COM `Execute` returns immediately. Deferred.
