# Host architecture (M2/M3)

The Windows host, `src/RoboRightClick`, and the Core contracts it depends on. The plan this
implements is docs/design.md; the decisions there are fixed. This document makes them
precise enough that several people can each own a set of files and build in parallel.

**Status:** the skeleton cross-compiles. Nothing in the host has run on Windows. Runtime facts
cited below come from docs/testlog.md (2026-10-02, M0 spikes 2-4). Everything else is a
design claim until a testlog entry says otherwise. Section 15 records the architecture review
of 2026-10-02: which findings changed this design and which were declined, with reasons.

## 1. Component map

One process, `RoboRightClick.exe`. Arrows are "calls".

```
Explorer ──COM (out-of-proc)──▶ Com/ClassFactory ─▶ Com/VerbCommand ─▶ IVerbHandler
CLI (same exe) ─▶ Cli/CliRunner ─▶ Com/ComClient ──COM──┘                   │
                                                                            ▼
                                Verbs/VerbDispatcher ─▶ Verbs/ClipboardService (Win32 clipboard)
                                          │ PasteOrder (raw paths, nothing resolved)
                                          ▼
UI/* ◀── snapshots (polled) ── Jobs/JobManager ─▶ Jobs/Job ─▶ Jobs/RobocopyRun ─▶ robocopy.exe
  ▲        state events                    │            │          ├─ Jobs/RobocopyPipe (/UNILOG pipe)
  │                                        │            │          └─ Jobs/ProgressSampler (IO counters)
App/TrayApplication (composition root)     │            ├─ Jobs/InProcessCopier (rename, CopyFileEx)
                                           │            └─ Core: PastePlanner, JobScanner, ExecutionPlanner,
                                           ▼                     StepLedger, RobocopyArgs, RobocopyOutputParser,
                          Logging/JobLogStore + FileJobSink      CancelCleanup, JobOutcome, RetryPlanner
                          (normal mode only, via JobSinks.For)
```

### Host files (`src/RoboRightClick`)

| File | Purpose |
|---|---|
| `Program.cs` | `[STAThread]` entry; DLL search hardening; `CommandLine.Parse` and dispatch. |
| `App/TrayApplication.cs` | Composition root, tray icon and menu, start/exit/session-end order (doc comment is the spec). |
| `App/CrashPolicy.cs` | Unhandled-exception handling; no WER report while ephemeral jobs run. |
| `App/SingleInstance.cs` | Session mutex, exit-request and ready events, all with a current-user DACL. |
| `App/SettingsStore.cs` | config.json load (per-field fallback), watch, atomic save, `.bad` copy, change event. |
| `App/TrayIcons.cs` | GDI+-drawn icons per `TrayIconState` × ephemeral tint. |
| `App/HostEnvironment.cs` | `AppPaths`, version, exe path, user SID, install-location check. |
| `App/AppNative.cs` | Small user32/kernel32 imports for the app shell. |
| `Com/ComInterfaces.cs` | `[GeneratedComInterface]` declarations, HRESULTs, shell constants. |
| `Com/ComNative.cs` | ole32/shell32 imports, the process `StrategyBasedComWrappers`. |
| `Com/ComCallerSecurity.cs` | `CoInitializeSecurity` and the caller integrity check. |
| `Com/ClassFactory.cs` | `IVerbHandler` boundary; one `IClassFactory` per verb. |
| `Com/VerbCommand.cs` | `IExecuteCommand` + `IObjectWithSelection` + `IInitializeCommand`. |
| `Com/ComServer.cs` | `CoRegisterClassObject` ×3 on the UI thread, revoke on exit. |
| `Com/ShellSelection.cs` | `IShellItemArray` → paths (server); paths → `IShellItemArray` (client). |
| `Com/ComClient.cs` | CLI side: `CoCreateInstance` + `SetSelection` + `Execute`. |
| `Verbs/VerbDispatcher.cs` | What copy / cut / paste do once paths arrive. No file-system calls. |
| `Verbs/ClipboardService.cs` | Raw Win32 clipboard with a message-only owner window. |
| `Verbs/ClipboardNative.cs` | user32/kernel32/shell32 clipboard imports. |
| `Jobs/JobManager.cs` | All jobs: enqueue, queue policy, claims, control, snapshots, events, retry. |
| `Jobs/Job.cs` | One job's lifecycle (doc comment is the flow spec); `JobServices`, `JobStart`. |
| `Jobs/PauseGate.cs` | A job's pause latch shared by its steps. |
| `Jobs/RobocopyRun.cs` | One robocopy process: pipe, parser, sampler, suspend/resume, kill. |
| `Jobs/RobocopyPipe.cs` | Current-user single-instance pipe, client PID check, UTF-16 line batches. |
| `Jobs/ProgressSampler.cs` | One app-wide timer loop reading `GetProcessIoCounters`. |
| `Jobs/InProcessCopier.cs` | Renames (`MoveFileEx`, flags 0) and single-file copies (`CopyFileEx`). |
| `Jobs/FileSystemFacts.cs` | `IPlanningFacts` + `IScanFacts` from the real disk. |
| `Jobs/ShellNotify.cs` | `SHChangeNotify` so Explorer views of network shares refresh. |
| `Jobs/IJobPrompts.cs` | The conflict question a job asks the UI. |
| `Jobs/ProcessNative.cs` | ntdll/kernel32 imports: suspend, IO counters, pipe PID, copy, job object. |
| `Logging/JobLogStore.cs` | Log folder layout, history, pruning, delete-all, interrupted-job check. |
| `Logging/FileJobSink.cs` | Normal-mode `IJobSink`: job.json, robocopy.log, history line. |
| `UI/ProgressWindow.cs` | Per-job progress (Explorer's copy dialog counterpart). |
| `UI/ProgressWindowHost.cs` | Opens and tracks progress windows; owner for conflict dialogs. |
| `UI/JobsWindow.cs` | Jobs list with progress and row actions. |
| `UI/SettingsWindow.cs` | Single-page settings editor. |
| `UI/ConflictDialog.cs` | Replace / Skip / Let me decide (per-file list). |
| `UI/ErrorSummaryDialog.cs` | End-of-job errors, refusals, damage: Try again (N) / Skip. |
| `UI/UiPrompts.cs` | `IJobPrompts` on the UI thread. |
| `UI/Notifier.cs` | Balloon toasts, coalesced; text only from `ToastText`. |
| `Install/Installer.cs` | `--install`, first-run offer, `--uninstall`. |
| `Install/RegistryWriter.cs` | Executes Core's registry lists against HKCU. |
| `Cli/CliRunner.cs` | Verb invocation from the command line, usage, console attach. |

### Core (`src/RoboRightClick.Core`), relevant to the host

| File | Purpose | State |
|---|---|---|
| `PathPolicy.cs` | The gate for every untrusted path (clipboard, selection, CLI). | implemented, tested, sabotage-checked |
| `PastePlanner.cs` | `PasteOrder` → validated, resolved `PastePlan`; resolved-path guards; folder links. | implemented, tested, sabotage-checked |
| `RobocopyArgs.cs` | Arguments; extraArgs allow-list; `Quote` refuses quotes; command-line length. | implemented, tested, sabotage-checked |
| `RobocopyOutput.cs` | Parser; `Complete(processEndedNormally)` drops a held line after a kill. | implemented, tested, sabotage-checked |
| `CancelCleanup.cs` | Which destination files a cancel deletes. | implemented, tested, sabotage-checked |
| `JobRuntime.cs` | `JobFootprint`, `JobQueuePolicy` (overlap, wait reasons, scan cap), `PipeNames`. | implemented, tested, sabotage-checked |
| `Jobs.cs` | State table, lifecycle, progress (`ResetRate`). | implemented, tested, sabotage-checked |
| `JobLogging.cs` | `IJobSink`, `JobSinks.For` / `ForDerivedJob`, log naming and pruning. | implemented, tested, sabotage-checked |
| `ClipboardPayload.cs` | `DROPFILES` and drop-effect bytes, decode limits, exclusion formats. | implemented, tested, sabotage-checked |
| `ComSecurity.cs` | COM access and launch descriptors (SDDL). | implemented, tested, sabotage-checked |
| `Registration.cs` | HKCU footprint as typed data; Installed-apps entry; autostart resolver. | implemented, tested |
| `ShellVerbs.cs` | Verb table: CLSIDs, labels, associations, `MultiSelectModel`. | implemented, tested |
| `CommandLine.cs` | CLI grammar → `CliCommand`; exit codes. | implemented, tested |
| `TrayStatus.cs` | `JobSnapshot` (incl. refusals, damage, failure, wait, cancel); tray state, tooltip. | implemented, tested |
| `ToastText.cs` | Job toasts (path-free in ephemeral), refusal and settings toasts. | implemented, tested |
| `DisplayText.cs` | Sizes, speed, durations, source summaries. | implemented, tested |
| `JobScan.cs` | `IScanFacts`, `ScanResult`; `JobScanner.Scan`. | contract only |
| `ConflictDecisions.cs` | `ExecutionPlan`; `ExecutionPlanner.Apply` (exclusion by construction). | contract only |
| `StepLedger.cs` | Planned vs reported reconciliation for progress, cancel, retry, summary. | contract only |
| `JobOutcome.cs` | `StepOutcome`, `FinalState` (tested); `FailureText`, `RetryPlanner`. | partly contract |
| `JobRecords.cs` | job.json and history.jsonl formats; `LastState`. | contract only |

## 2. Threading model

| Thread | Runs | Rules |
|---|---|---|
| **UI (main, STA)** | WinForms message loop; COM class objects (registered here, so activations and every `VerbCommand` call arrive here through the loop); clipboard (owner window lives here); all forms, NotifyIcon, toasts. | Never blocks and never touches the file system: a stat on a dead SMB share would freeze the tray and every right-click. `VerbCommand.Execute` reads the selection, calls `IVerbHandler.Invoke`, returns. `VerbDispatcher` runs verbs one at a time in click order; clipboard retries are awaited delays, so the loop keeps pumping. |
| **Scan** | One dedicated thread per scanning job (`TaskCreationOptions.LongRunning`), at most `JobQueuePolicy.MaxConcurrentScans` (4). | Lazy enumeration; checks cancellation per directory and every 1,000 entries; reports `ScanProgress`. |
| **Job workers** | `Job.RunAsync` continuations on the thread pool; steps sequential inside a job, jobs parallel. | Never touch UI objects. Ask the user only through `IJobPrompts`. Clipboard clear goes through `JobServices.ClearClipboardIfUnchanged`, which posts to the UI thread. Never call the sink, an event or the UI while holding the job lock. |
| **Pipe reader + consumer** | Per robocopy run: a reader that only drains the pipe into a bounded channel of line batches, and a consumer that parses, updates the ledger (one lock per batch) and writes to the sink. | The pipe is always drained, so a slow sink or antivirus on robocopy.log never stalls robocopy. |
| **Progress sampler** | One `PeriodicTimer` loop for the app (333 ms). | Skips runs whose gate is closed. Disposing a run's tracking blocks until its in-flight callback returns. |

Shared state: each `Job` guards lifecycle, ledger, progress and errors with one private lock;
`Snapshot()` copies under it into an immutable `JobSnapshot`. `JobManager` guards its job list
with its own lock and never holds it while calling into a job. UI updates: windows poll
`JobManager.Snapshots()` every 250 ms while visible (rebuilt only when dirty); the
`StateChanged` event is for state transitions only (tray icon, toasts, progress windows), and
the tray tooltip is set only when its text changes. Time comes from `TimeProvider.System` and
is passed into Core types.

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
| `IDataObject` | `0000010E-0000-0000-C000-000000000046` | GetData, GetDataHere, QueryGetData, GetCanonicalFormatEtc, SetData, EnumFormatEtc, DAdvise, DUnadvise, EnumDAdvise (to be added; `objidl.h`) |

`SIGDN_FILESYSPATH = 0x80058000`. `BHID_DataObject = {B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4}`.
Strings from `GetDisplayName` are freed with `Marshal.FreeCoTaskMem`. Unused methods keep
placeholder signatures because they hold vtable slots. Every proxy received from Explorer is
released deterministically on the UI thread with `ComObject.FinalRelease()`, never left to the
finalizer thread.

### Identities (fixed; changing one orphans installed keys)

| Verb | CLSID |
|---|---|
| Robo-Copy | `{BD15DC6A-FBC1-4949-B61D-3B8FC390062F}` |
| Robo-Cut | `{1A061376-A3F7-41BF-A516-E635ED91ACDF}` |
| Robo-Paste | `{9D1BAE79-13C3-427F-A7E6-34150D5C49AB}` |
| AppID (shared) | `{B708F29C-8ED8-40BD-832E-F05180F1B285}` |

### Caller security

Any process of the same user can `CoCreateInstance` these CLSIDs, including a low-integrity
sandbox (a browser renderer, Office protected view). Unrestricted, that would let it make the
medium-integrity tray copy or move files of its choosing, a sandbox escape. Three layers:

1. `ComCallerSecurity.InitializeProcess` calls `CoInitializeSecurity` once, first thing on the
   UI thread, with `Core.ComSecurity.AccessPermissionSddl(userSid)`: the user and SYSTEM get
   `COM_RIGHTS_EXECUTE | EXECUTE_LOCAL`, nobody gets remote rights, and the mandatory label
   `S:(ML;;NX;;;ME)` refuses callers below medium integrity. Authentication level
   `PKT_PRIVACY`, impersonation `IDENTIFY`.
2. Install writes the same access descriptor and a launch descriptor (adds `ACTIVATE_LOCAL`)
   to the AppID key as `AccessPermission` / `LaunchPermission` (REG_BINARY).
3. `VerbCommand.Execute` impersonates the caller and refuses (`E_ACCESSDENIED`) below medium.

All three are design claims until the spike in section 12 runs.

### Activation and call sequence

1. Tray start: `ComServer.Register` calls `CoRegisterClassObject(clsid, factory,
   CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED)` for all three, then
   `CoResumeClassObjects`, then `SingleInstance.SignalReady`.
2. Not running: COM starts `"<exe>" -Embedding`, which parses to `CliRunTray(StartedByCom: true)`;
   the tray starts normally and stays resident. If a Run-key start won the mutex race, the
   `-Embedding` instance waits up to 10 s for the ready event and exits 0.
3. Explorer: `CreateInstance` → (`IInitializeCommand::Initialize`) → `SetSelection` → setters →
   `Execute`. `Execute` checks the caller, reads all paths (`ShellSelection.ReadPaths`: one
   `BindToHandler(BHID_DataObject)` + `CF_HDROP` read; per-item fallback), releases the
   array, calls `IVerbHandler.Invoke`, returns `S_OK`.
4. No exception crosses the COM boundary; every implementation catches and returns an HRESULT.
5. Exit: revoke first, then cancel jobs, then release the single-instance mutex.

The pattern (DelegateExecute to an out-of-process local server) is Microsoft's
`ExecuteCommandVerb` sample (Windows-classic-samples, Win7Samples/winui/shell/appshellintegration),
which uses `REGCLS_SINGLEUSE`; `REGCLS_MULTIPLEUSE` is this design's change and is what spike 1
must prove.

## 4. Registration data

Produced by `Registration.InstallValues(InstallTarget)`; under `HKEY_CURRENT_USER`, typed by
`RegistryDataKind`. Uninstall deletes exactly `Registration.UninstallRemovals()`: the key
trees marked ⌫ and the Run value. Tests prove every written value is covered, no shared parent
(`...\shell`, `...\CLSID`) is ever deleted, and no key is outside `Software\Classes`, the Run
key and the app's own Uninstall key.

| Key (under `HKCU\`) | Value | Data |
|---|---|---|
| ⌫ `Software\Classes\AppID\{B708F29C-…}` | (default) / `AccessPermission` / `LaunchPermission` | `RoboRightClick` / SD / SD (REG_BINARY from SDDL) |
| ⌫ `Software\Classes\CLSID\{verb clsid}` | (default) / `AppID` | `RoboRightClick Robo-Copy` / `{B708F29C-…}` |
| `…\CLSID\{verb clsid}\LocalServer32` | (default) | `"<install dir>\RoboRightClick.exe"` |
| ⌫ `Software\Classes\<assoc>\shell\<Verb>` | `MUIVerb` / `MultiSelectModel` | `Robo-Copy` / `Player` (copy, cut) or `Single` (paste) |
| `…\shell\<Verb>\command` | `DelegateExecute` | `{verb clsid}` |
| ⌫ `Software\Microsoft\Windows\CurrentVersion\Uninstall\RoboRightClick` | `DisplayName`, `DisplayVersion`, `DisplayIcon`, `InstallLocation`, `UninstallString`, `NoModify`=1, `NoRepair`=1 | Settings → Apps → Installed apps entry |
| `Software\Microsoft\Windows\CurrentVersion\Run` | `RoboRightClick` | `"<exe>"` (only when autostart resolves true; always removed) |

Associations: RoboCopy and RoboCut on `AllFilesystemObjects`; RoboPaste on
`Directory\Background`, `Directory`, `Drive`. `DelegateExecute` sits on the verb's `command`
subkey (as in the Microsoft sample), `MultiSelectModel` on the verb key. `Single` hides
Robo-Paste for a multi-folder selection, as Explorer's own Paste is hidden. Autostart on
reinstall comes from `Registration.ResolveStartWithWindows`: an explicit `--autostart` /
`--no-autostart` wins (and is written to config.json), otherwise the existing config's choice
is kept. No `Icon` value yet (the exe has no icon resource; see open questions).

## 5. Verbs and clipboard

- **Untrusted input.** Every path from the clipboard, the shell selection or the CLI is
  untrusted: any process can write `CF_HDROP`, and any same-user COM client can send a
  selection. `PathPolicy` (Core) accepts only plain drive and UNC paths and refuses anything
  robocopy could read as a switch, a wildcard, a device or a stream. `RobocopyArgs.Quote`
  throws on a quote as a second line.
- **Robo-Copy / Robo-Cut:** selection paths through `PathPolicy` (any refusal: a
  `SelectionNotFiles` toast, nothing written), then `ClipboardService.WriteFilesAsync(paths,
  verb, mode)` writes every entry of `ClipboardPayload.ForFiles`: `CF_HDROP` (wide `DROPFILES`),
  `Preferred DropEffect` (1 copy, 2 move) and, in ephemeral mode,
  `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`,
  `CanUploadToCloudClipboard = 0`. No job starts. A clipboard held by another program through
  about one second of retries gives a `ClipboardBusy` toast.
- **Robo-Paste:** destination = `ShellVerbs.PasteDestination(selection)`.
  `ClipboardService.ReadFilesAsync` reads `CF_HDROP` (Core decoder with size and count limits;
  `DragQueryFileW` for ANSI under the same limits), `Preferred DropEffect` and then the sequence
  number, inside one `OpenClipboard`. Verb = `ClipboardPayload.VerbForPaste` (move only for a
  pure move marker). A move where every source's parent is the destination is Explorer's
  no-op and does nothing. Otherwise `JobManager.Enqueue(PasteOrder, cut ? sequence : null)`;
  everything that touches the disk happens later, in Scanning, on a worker thread.
- **Refusals** (`VerbRefusal`): empty clipboard, virtual items, too large, busy, no
  file-system destination, several destinations, non-file selection, same cut already being
  pasted. Each has one fixed, path-free sentence in `ToastText.ForRefusal`.
- **After a cut-paste ends `Done` with at least one item moved:** `ClearIfUnchanged(sequence)`
  opens the clipboard, compares, then empties. A newer clipboard write by anyone is left alone.
  `DoneWithErrors` keeps the clipboard (its sources still exist).

## 6. Job engine contracts

Pipeline per job (`Job.RunAsync`; the `Job` doc comment is normative):

| State | Work | Core contract |
|---|---|---|
| Queued | Waits while `JobQueuePolicy.WaitReason` is not None: an active or older queued job whose footprint conflicts (one writes where the other reads or writes), the concurrency limit, the scan cap. The reason is in the snapshot. | `JobFootprint`, `JobQueuePolicy` |
| Scanning | `PastePlanner.Plan(order, facts)` (validation, kinds, links, resolved-path guards), then `JobScanner.Scan` with live totals. | `PastePlan`, `ScanResult` |
| AwaitingDecision | Only if conflicts and policy `Ask`: `IJobPrompts.ResolveConflictsAsync`; null = Canceled. | `ConflictChoice` |
| Running | `ExecutionPlanner.Apply(scan, configured, choice, taken)` steps in order; events through `StepLedger`. | `ExecutionPlan`, `StepLedger` |
| Paused | `PauseGate` closed: robocopy `NtSuspendProcess`, CopyFileEx callback blocks, loop waits between steps and before Finalizing. A pause before Running is latched. | |
| Finalizing | Create `LinkFolders` empty; clear clipboard after a `Done` cut that moved something; `ShellNotify`; build `JobSummary`. | `JobOutcome.FinalState` |
| terminal | `Done` / `DoneWithErrors` / `Failed` / `Canceled`; `Finished` → `Notifier`. | `ToastText.ForFinished` |

Every non-terminal state can go to `Failed`; Scanning goes straight to Finalizing when nothing
is left to run; cancel during Finalizing is ignored.

**Conflicts are protected by construction, not by filters.** `ExecutionPlanner` leaves every
file the user chose to keep out of every robocopy step. It never relies on `/XF` (full-path
matching unverified) or on robocopy's class filters under `/MOV`, which can delete the source
of a "same" file it skipped (LIKELY; M4 checks it). Uniform choices keep the plan's steps;
mixed ones split a tree along the directories that hold kept files (details in the
`ExecutionPlanner.Apply` remarks). A run with no conflicts uses `Ask`, whose flags are
`/XC /XN /XO`: a file that appears at the destination after the scan is skipped, never
silently overwritten, and the ledger reports it.

Step execution:

- `RobocopyStep` → `RobocopyRun` with `RobocopyArgs.Build(step, settings, policy,
  PipeNames.ForStep(jobId, index, csprngNonce))`. Robocopy from
  `%SystemRoot%\System32\robocopy.exe` (absolute), working directory System32, no window,
  stdout drained and discarded, placed in a kill-on-close job object. Pipe created **before**
  start with `FirstPipeInstance`; a client whose PID is not robocopy's is disconnected and the
  wait continues. The run ends when the process has exited **and** the reader reached
  end-of-output; then `Complete(processEndedNormally)`.
- Before each step, its destinations are re-checked (one listing per destination folder) and
  added to the presence set cancel cleanup reads.
- Progress: completed files and bytes from the ledger; live bytes = completed bytes of finished
  steps + this run's `ReadTransferCount`, from callbacks of the current run only;
  `JobProgress.ResetRate` on resume; no speed while paused.
- `RenameStep` and a same-volume keep-both cut → `MoveFileEx(src, dst, 0)` for files and folders.
  It never copies across volumes or overwrites. `ERROR_NOT_SAME_DEVICE` sets
  `StepOutcome.ReplanAsMove`: the item is scanned again and appended as a robocopy move with
  policy Skip. `File.Move` would silently copy and delete across volumes, an app-initiated
  source deletion that invariant 1 forbids.
- `DuplicateFileStep`, copy-mode `KeepBothStep` → `CopyFileEx(COPY_FILE_FAIL_IF_EXISTS)`.
- **Cancel** (any state before Finalizing): `CancelRequested` at once; kill robocopy and wait;
  then `CancelCleanup.Select(ledger.StartedRobocopyFiles, ledger.CompletedSources, presence set,
  move, sourceStillExists, claimedByOtherJob)` and delete each `Delete` entry if present, opening
  it with `FILE_FLAG_OPEN_REPARSE_POINT` and refusing a reparse point. Skipped entirely when the
  ledger found robocopy's paths unreliable. Rules (each tested, each test seen failing with the
  rule removed): reported-complete files stay; files present before their step are never
  deleted; files another job of this session planned or wrote are never deleted; for a cut, a
  destination whose source is gone stays. `LeftInPlace` becomes `JobSnapshot.DamagedOnCancel`
  (attention, toast, "Finish replacing them").
- **Errors** → `DoneWithErrors`; "Try again (N)" = `JobManager.Retry(parent)` →
  `RetryPlanner.ForFailures(plan, ledger.Retryable, ledger.FailedInProcessSteps)`; a child
  job with `ParentId`, logging mode `JobSinks.ForDerivedJob(parent, current)`; its Scanning
  only refreshes totals and presence and never prompts. A `Failed` job's "Try again" is
  `JobManager.Rerun` (the original order, full re-scan). Refusals are not retryable and are
  counted separately (`RefusedCount`).
- **Invariant 1:** the job never deletes a source. Only robocopy `/MOV`/`/MOVE` and renames
  move anything.

## 7. UI surfaces

- **Progress window** (per job, `showProgressWindow`, default on): Explorer's copy dialog
  counterpart, opened about one second after the job is created. It closes on Done and turns
  into the error summary on DoneWithErrors, Failed or a damaging cancel. It owns the job's
  conflict dialog, which is how that dialog reaches the front.
- **Tray** (`TrayApplication`): icon and tooltip from `TrayStatus.Derive(snapshots, mode)`
  (attention > running > paused > idle; tooltip ≤ 127 chars). Left click and double click open
  Jobs. Menu: Jobs…, Pause all (checked while on), Resume all, Ephemeral mode (check),
  Settings…, Open logs (hidden in ephemeral), Exit (confirms when jobs are active). First run
  shows a one-time hint about pinning the tray icon.
- **Jobs window**, **Settings window**, **Conflict dialog**, **Error summary**: specified in
  the doc comments of their files. All built in code, no designer files or resources. Dialogs
  are modeless, because several jobs can ask at once; titles name their job.
- **Toasts** (`Notifier`): `ToastText` only. A clean finish obeys `notifyOnComplete`; errors,
  refusals-only outcomes, failures and damaging cancels always notify; a plain cancel and a
  no-op never do. Finishes within about 2 s are coalesced; an error toast is never replaced by
  a success toast. A click opens Jobs filtered to items needing attention.

## 8. Logging and ephemeral mode

- The sink is chosen once per job at creation: `JobSinks.For(mode, () => logStore.CreateSink(job))`.
  In ephemeral mode the factory is never invoked. A derived job (retry, re-run, re-plan) uses
  `JobSinks.ForDerivedJob(parent, current)`: ephemeral wins in both directions.
- Normal: `jobs\<JobLogNames.FolderName>\job.json` (`JobRecords.ToJson`, rewritten via temp
  file + `File.Replace` on each state change, bounded: states, commands, summary, at most
  1,000 errors), `robocopy.log` (UTF-8 copy of the pipe lines, buffered, capped at 50 MB with a
  truncation line), `history.jsonl` (rotated past 10,000 lines). After a normal-mode job
  finishes, the manager prunes off the UI thread to `logRetentionJobs`, never touching folders
  of running jobs. Log write failures never fail a job.
- Ephemeral: `NullJobSink`, in-memory history only (`JobManager.InMemoryHistoryLimit`),
  path-free toasts, clipboard exclusion formats, no temp files. The one write is config.json.
- Turning ephemeral on while job logs exist asks whether to delete them; Settings also has
  "Delete all job logs".
- Toggling affects new jobs only; jobs carry their own `LoggingMode`, and `ToastText` decides
  on the job's mode, not the current one.
- What ephemeral mode does not cover is listed in docs/design.md: the guarantee is that the
  app writes no job data, not that Windows records nothing.

## 9. CLI

`CommandLine.Parse` (Core) defines the grammar; `CommandLine.Usage` is the help text.

| Command | Effect |
|---|---|
| *(none)* | Tray. From outside the install folder: offer to install instead. |
| `-Embedding` | Tray started by COM. |
| `--install [--autostart \| --no-autostart]` | Install (section 10). |
| `--uninstall` | Uninstall (section 10). |
| `copy <path>...`, `cut <path>...`, `paste <folder>` | `ComClient.Invoke`: `CoCreateInstance(CLSCTX_LOCAL_SERVER)` on the verb's CLSID, `CoAllowSetForegroundWindow`, `SetSelection(SHCreateShellItemArrayFromIDLists(SHParseDisplayName(...)))`, `Execute`. The same path as a right-click, started by the CLI process instead of Explorer. Returns when the verb is accepted. |

Exit codes: 0 ok, 1 failed, 2 usage. The exe is a GUI-subsystem program, so shells do not
wait for it: scripts use `start /wait` or `Start-Process -Wait -PassThru`. Verb output goes to
the parent console via `AttachConsole`; install and uninstall results use a message box.

## 10. Install and uninstall

- `--install`: stop a running tray (exit request; refused while jobs run) → copy exe to
  `%LOCALAPPDATA%\Programs\RoboRightClick\` → write `Registration.InstallValues` → write the
  default config.json only if absent (or apply an explicit autostart flag to the existing one)
  → start the installed tray → message.
- `--uninstall`: stop the tray → remove `Registration.UninstallRemovals()` → delete exactly the
  known files (config, history, each job folder's two files), then `RemoveDirectory` on each
  folder after checking its leaf name and that it is not a reparse point → the install folder
  via `%SystemRoot%\System32\cmd.exe` (absolute path, validated arguments) when running from
  it. Nothing outside `AppPaths` and the registry list; never a blind recursive delete.
- No Explorer setting, no Explorer restart, no admin rights.
- DLL search: `[assembly: DefaultDllImportSearchPaths(System32)]` and
  `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` first in `Main`, so a DLL planted
  next to a downloaded exe is not loaded by the app's own imports. (The .NET host loads before
  `Main`; that part is outside the app's control.)

## 11. Error-handling principles

1. Nothing throws into Explorer: COM methods return HRESULTs.
2. Nothing in a job throws out of `RunAsync`: unexpected exceptions end the job `Failed` from
   whatever state it was in, with a path-free reason (`FailureText`).
3. When in doubt, keep data: cancel cleanup, clipboard clearing and uninstall all err towards
   leaving files in place.
4. Untrusted input is checked at the boundary: paths (`PathPolicy`), clipboard byte layouts and
   sizes (Core decoders), pipe client PID, COM caller integrity, config fields (per-field
   fallback), extraArgs (allow-list), CLI arguments.
5. Logging and toasts are best-effort and never change a job's outcome.
6. No crash log in the beta. While ephemeral jobs run, a crash ends the process without a WER
   report (`CrashPolicy`).
7. Sign-out with active jobs: shutdown is vetoed with a reason; if Windows ends the session
   anyway, jobs are canceled with cleanup within 5 s. A job.json left non-terminal is reported
   once at the next start (normal mode).

## 12. Verified vs unverified on Windows

| Claim | Status |
|---|---|
| `/UNILOG` to a named pipe carries exact UTF-16; stdout cannot | verified (testlog 2026-10-02) |
| Under `/MT:32` file lines arrive at completion; failed file line precedes its ERROR | verified (testlog 2026-10-02) |
| `GetProcessIoCounters` tracks progress; destination size does not | verified (testlog 2026-10-02) |
| `NtSuspendProcess`/`NtResumeProcess` pause and resume `/MT:32` cleanly | verified (testlog 2026-10-02) |
| Metadata flags vs Explorer | verified, deviations in docs/parity.md |
| Out-of-process DelegateExecute with `REGCLS_MULTIPLEUSE`: 500 items in one call to a running instance | **unverified** (M0 spike 1) |
| `BHID_DataObject` on Explorer's selection yields `CF_HDROP`; time for 50k items vs per-item reads | **unverified** (spike 1) |
| `-Embedding` start when the tray is not running; cold-start time; Run-key race | **unverified** (spike 1) |
| `MultiSelectModel=Single` hides Robo-Paste for multi-folder selections; works on the background verb | **unverified** (spike 1) |
| A low-integrity process cannot activate or call the server (`icacls /setintegritylevel low` test exe) | **unverified** (security spike) |
| HKCU AppID `AccessPermission`/`LaunchPermission` are honored, and do not break Explorer's activation | **unverified** (security spike) |
| Explorer allows the tray to take foreground (conflict dialog, progress window) | **unverified** (spike 1) |
| Remote clients are refused by the output pipe | **unverified** |
| `SetDefaultDllDirectories(SYSTEM32)` does not break WinForms start-up in a single-file app | **unverified** |
| Explorer ghosts icons after a Robo-Cut clipboard write | **unverified** (M0 spike 5) |
| Robocopy `/MOV` deletes the source of a "same" file it skipped | **unverified**; the design no longer depends on it either way |
| A killed robocopy leaves its in-flight files at full length (cancel cleanup's premise) | **unverified** |
| `CopyFileEx` sets the archive bit like Explorer's copy | **unverified** |
| Generated COM vtables match the shell's (`[GeneratedComInterface]` on these IDLs) | **unverified**; compile-time only |
| Everything else in the host | **unverified**; cross-compiles only |

## 13. Work packages (disjoint file ownership)

The beta's remaining work is split into nine packages that can be built in parallel in separate
worktrees. Each owns its files exclusively; everything else is read-only to it. Contracts
between packages are the types in the skeleton. A package that needs a contract changed stops
and asks rather than editing a file it does not own.

| Package | Owns |
|---|---|
| core-engine | Core `JobScan.cs`, `ConflictDecisions.cs`, `StepLedger.cs`, `JobOutcome.cs`, new Core tests |
| robocopy-runner | `Jobs/RobocopyRun.cs`, `RobocopyPipe.cs`, `ProgressSampler.cs`, `InProcessCopier.cs`, `FileSystemFacts.cs`, `ShellNotify.cs`, `ProcessNative.cs`, Core `Utf16Lines.cs` + tests |
| job-orchestration | `Jobs/Job.cs`, `JobManager.cs`, `PauseGate.cs`, `IJobPrompts.cs` |
| activation | `Com/*`, `Cli/*`, `Install/*`, `App/SingleInstance.cs`, Core `UninstallPlan.cs` + tests |
| clipboard-verbs | `Verbs/*` |
| ui | `UI/*`, `App/TrayIcons.cs`, Core `ProgressWindowPolicy.cs`, `ToastBatch.cs` + tests |
| logging | `Logging/*`, Core `JobRecords.cs` + tests |
| integration | `Program.cs`, `App/TrayApplication.cs`, `App/CrashPolicy.cs`, `App/SettingsStore.cs`, `App/HostEnvironment.cs`, `App/AppNative.cs`, the host csproj |
| publish-e2e | `scripts/publish.sh`, `scripts/e2e/*`, `README.md` |

## 14. Open questions

1. **Keep-both on a cross-volume cut** needs an OS move (`MoveFileWithProgress` +
   `MOVEFILE_COPY_ALLOWED`) as a second source-deleting path beside robocopy `/MOV`, which needs
   an ADR under invariant 1. Same-volume cuts keep both with a rename. The cross-volume gap is
   listed in docs/parity.md.
2. **Preferred DropEffect for copy:** the design fixes 1. Explorer's own Ctrl+C is commonly
   reported as 5 (copy | link). Measure in M4. Both values paste as a copy.
3. **Menu icon:** without an `.ico` the verbs have no icon. Adding one means a binary asset and
   an `Icon` registry value.
4. **Uninstall deletes config and logs** (the privacy-preserving choice). A `--keep-data` flag is
   possible if users want settings to survive reinstall.
5. **CLI `--wait`** (block until the paste finishes) would make VM automation simpler. It needs a
   second channel, since COM `Execute` returns immediately. Deferred; the e2e scripts poll the
   file system instead.
6. **Empty source folders after a successful retry of a cut** are left in place: removing them
   is an app-initiated source deletion under invariant 1 and needs an ADR. Listed in
   docs/parity.md.
7. **Scan memory** is about 500 bytes per planned file (two full paths and facts): about 50 MB
   for 100k files, 0.5 GB for a million. Compact relative paths are deferred until measured.

## 15. Review record (2026-10-02)

Four reviewers assessed the skeleton. Accepted findings changed the sections above; the main
ones:

- **Untrusted paths** (section 5): `PathPolicy`, `Quote` refusing quotes, resolved-path
  self/subfolder guards (`IPlanningFacts.FinalPath`), selected folder links never handed to
  robocopy.
- **COM caller security** (section 3): `CoInitializeSecurity`, AppID descriptors, integrity
  check in `Execute`.
- **extraArgs**: allow-list instead of a deny-list that quoting could bypass and that missed
  selection-changing switches.
- **Conflict handling** (section 6): exclusion by construction replaces `/XF`; `Ask` with no
  conflicts runs with Skip flags; KeepNewer is `/XC /XO`.
- **Cancel cleanup** (section 6): per-step presence, other jobs' claims, normalized path
  matching, reparse-point check, parser drops a held line after a kill, `StepLedger` as the
  single reconciliation.
- **Concurrency** (section 6): footprint-based queueing of conflicting jobs, refusal of a
  repeated cut-paste, a scan cap; this also makes duplicate-name reservations unnecessary.
- **State table**: Failed from every state, Scanning → Finalizing, latched pause, cancel
  ignored in Finalizing.
- **Privacy**: derived jobs inherit ephemeral; crash handling without WER while ephemeral jobs
  run; documented non-guarantees; every ephemeral toast generic.
- **UI-thread hygiene** (section 2): no file-system calls on the UI/COM thread; `PasteOrder`
  resolved in Scanning; awaited clipboard retries; single-call selection read.
- **User experience** (section 7): progress window, refusals vs errors, failure reasons, damage
  on cancel, no-op pastes, Installed-apps entry, first-run install offer, settings reload,
  sign-out handling, `SHChangeNotify`, toast coalescing, Pause all as a mode.
- **Throughput and start-up**: pipe reader decoupled from the sink, buffered logs, bounded
  job.json and error lists, conflict detection by directory listing, uncompressed single file,
  short start order.

Declined, with reasons:

| Finding | Reason |
|---|---|
| Cancel cleanup should also require the file's creation time to be after the step started | Robocopy copies the source's created time (`/COPY:DAT`, docs/parity.md), so the check would keep most genuine partial files and defeat cleanup. Per-step presence and job claims cover the same risk without depending on unmeasured timestamp behavior. |
| Keep `/XF` for Skip choices and add a post-run hash check | Exclusion by construction removes both the `/XF` dependency and the command-line budget problem. A check after the fact cannot undo a deleted source. |
| Session-wide duplicate-name reservation set | Conflicting jobs no longer run at the same time (footprint queueing), so a second paste plans after the first has created its "X - Copy". A foreign program racing for the same name is still caught by `COPY_FILE_FAIL_IF_EXISTS`. |
| Verify the mutex owner by activating the CLSID before exiting | A same-user process that squats the mutex can also rewrite the user's registry and the installed exe; the check adds start-up latency without changing the threat model. The objects get a current-user DACL, which keeps other users out. |
| Tighten the install folder's DACL; refuse `-Embedding` outside the install folder | `%LOCALAPPDATA%` already grants only the user, SYSTEM and Administrators. Neither change stops a same-user attacker, who can rewrite `LocalServer32` itself. The limitation is stated in docs/design.md instead. |
| ReadyToRun compilation for faster cold start | Requires the crossgen2 package at build time; the beta adds no packages. Start-up is shortened by ordering and an uncompressed bundle first; measured in spike 1. |
| Compact relative-path storage in the scan, and not materializing planned files | The ledger, cancel cleanup and retry need per-file data. Memory is about 50 MB per 100k files; revisit when measured (open question 7). |
| Remove empty source folders after a successful retry of a cut | An app-initiated deletion in the source tree; needs an ADR under invariant 1. Recorded as a deviation instead. |
| `WerAddExcludedApplication` to keep crash reports off | It writes an HKCU value outside the install footprint (invariant 4). `CrashPolicy` avoids WER while ephemeral jobs run without touching the registry. |
