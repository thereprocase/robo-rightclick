# Host architecture (M2/M3)

The Windows host, `src/RoboRightClick`, and the Core contracts it depends on. The plan this
implements is docs/design.md; the decisions there are fixed. This document makes them
precise enough that several people can each own a set of files and build in parallel.

**Status:** the host is implemented end to end (the nine work packages of section 13 are
merged) and cross-compiles. Activation, the three verbs and the clipboard have run on a
Windows 11 VM (docs/testlog.md 2026-10-02, M0 spikes 1-5); section 12 lists which claims that
covers. Everything else is a design claim until a testlog entry says otherwise. Section 15 records the architecture review of 2026-10-02:
which findings changed this design and which were declined, with reasons. Section 16 records
where the merged implementation settled questions the packages raised.

## 1. Component map

One process, `RoboRightClick.exe`. Arrows are "calls".

```
Explorer ──COM (out-of-proc)──▶ Com/ClassFactory ─▶ Com/VerbCommand ─▶ IVerbHandler
CLI (same exe) ─▶ Cli/CliRunner ─▶ Com/ComClient ──COM──┘                   ▲  │
Keyboard ─▶ App/PasteHotkey (LL hook) ─▶ Verbs/ExplorerFolderLocator ───────┘  │ (UI post)
                                         └──COM──▶ Explorer's ShellWindows     ▼
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
| `App/CrashPolicy.cs` | Unhandled-exception handling; crash.log in normal mode; no WER report while ephemeral jobs run. |
| `App/SingleInstance.cs` | Session mutex, exit-request and ready events, all with a current-user DACL. |
| `App/SettingsStore.cs` | config.json load (per-field fallback), watch, atomic save, `.bad` copy, change event. |
| `App/TrayIcons.cs` | GDI+-drawn icons per `TrayIconState` × ephemeral tint. |
| `App/HostEnvironment.cs` | `AppPaths`, version, exe path, user SID, install-location check. |
| `App/AppNative.cs` | Small user32/kernel32 imports for the app shell (and the Settings hotkey probe). |
| `App/PasteHotkey.cs` | The Robo-Paste hotkey's hook thread: foreground WinEvent hook, scoped `WH_KEYBOARD_LL` hook, latch, capture slot (docs/decisions/0001-paste-hotkey.md). |
| `App/KeyboardHookNative.cs` | The hook imports. With PasteHotkey.cs the only file allowed to name the hook APIs (`scripts/test.sh`). |
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
| `Verbs/ExplorerFolderLocator.cs` | Hotkey press → open folder (ShellWindows, tab match, `IFolderView::GetFolder`) or desktop folder; watchdog; clipboard guard; hands the folder to `IVerbHandler.Invoke`. |
| `Jobs/JobManager.cs` | All jobs: enqueue, queue policy, claims, control, snapshots, events, retry. |
| `Jobs/Job.cs` | One job's lifecycle (doc comment is the flow spec); `JobServices`, `JobStart`. |
| `Jobs/PauseGate.cs` | A job's pause latch shared by its steps. |
| `Jobs/RobocopyRun.cs` | One robocopy process: pipe, parser, sampler, suspend/resume, kill; on cancel suspend, let the job observe, then kill. |
| `Jobs/RobocopyPipe.cs` | Current-user single-instance pipe, client PID check, UTF-16 line batches. |
| `Jobs/ProgressSampler.cs` | One app-wide timer loop reading `GetProcessIoCounters`. |
| `Jobs/InProcessCopier.cs` | Renames (`MoveFileEx`, flags 0) and single-file copies (`CopyFileEx`). |
| `Jobs/FileSystemFacts.cs` | `IPlanningFacts` + `IScanFacts` from the real disk. |
| `Jobs/ShellNotify.cs` | `SHChangeNotify` so Explorer views of network shares refresh. |
| `Jobs/IJobPrompts.cs` | The conflict question a job asks the UI. |
| `Jobs/ProcessNative.cs` | ntdll/kernel32 imports: suspend, IO counters, pipe PID, copy, job object; kill-time observation (who holds a file open, file identity) and the identity-checked delete. |
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
| `CancelCleanup.cs` | Which destination files a cancel deletes: only those robocopy held open at the kill. | implemented, tested, sabotage-checked; verified on Windows (testlog 2026-10-02, cancel) |
| `JobRuntime.cs` | `JobFootprint`, `JobQueuePolicy` (overlap, wait reasons, scan cap), `PipeNames`. | implemented, tested, sabotage-checked |
| `Jobs.cs` | State table, lifecycle, progress (`ResetRate`). | implemented, tested, sabotage-checked |
| `JobLogging.cs` | `IJobSink`, `JobSinks.For` / `ForDerivedJob`, log naming and pruning. | implemented, tested, sabotage-checked |
| `ClipboardPayload.cs` | `DROPFILES` and drop-effect bytes, decode limits, exclusion formats. | implemented, tested, sabotage-checked |
| `ComSecurity.cs` | COM access and launch descriptors (SDDL). | implemented, tested, sabotage-checked |
| `Registration.cs` | HKCU footprint as typed data; Installed-apps entry; autostart resolver. | implemented, tested |
| `ShellVerbs.cs` | Verb table: CLSIDs, labels, menu labels with access keys, associations, `MultiSelectModel`. | implemented, tested, sabotage-checked (access keys) |
| `CommandLine.cs` | CLI grammar → `CliCommand`; exit codes. | implemented, tested |
| `TrayStatus.cs` | `JobSnapshot` (incl. refusals, damage, failure, wait, cancel); tray state, tooltip. | implemented, tested |
| `ToastText.cs` | Job toasts (path-free in ephemeral), refusal and settings toasts. | implemented, tested |
| `DisplayText.cs` | Sizes, speed, durations, source summaries. | implemented, tested |
| `JobScan.cs` | `IScanFacts`, `ScanResult` (`StepScan.Directories` recreates empty folders of a split tree); `JobScanner.Scan`, `FileInTheWayReason`. | implemented, tested, sabotage-checked |
| `ConflictDecisions.cs` | `ExecutionPlan`; `ExecutionPlanner.Apply` (exclusion by construction); the one command-line chunking rule. | implemented, tested, sabotage-checked |
| `StepLedger.cs` | Planned vs reported reconciliation for progress, cancel, retry, summary; in-process completed / re-planned / refused. | implemented, tested, sabotage-checked |
| `JobOutcome.cs` | `StepOutcome`, `FinalState`, `FailureText`, `RetryPlanner`. | implemented, tested, sabotage-checked |
| `JobRecords.cs` | job.json and history.jsonl formats (with a format version); `LastState`; the interrupted-job check and marker. | implemented, tested, sabotage-checked |
| `CrashLog.cs`, `PathHeuristic.cs` | crash.log entries, when they may be written, rotation; the path heuristic shared with `FailureText`. | implemented, tested, sabotage-checked |
| `Utf16Lines.cs` | Incremental UTF-16LE line splitter for the pipe (`MaxLineChars`, shared with `RobocopyPipe`). | implemented, tested |
| `JobScheduler.cs` | Which queued jobs start, in click order (`JobQueuePolicy` over the manager's states). | implemented, tested |
| `VerbRules.cs` | Selection, paste-destination and clipboard refusals; bounded ANSI `DROPFILES` splitter. | implemented, tested |
| `UninstallPlan.cs` | Exactly which files and folders uninstall removes; the validated self-delete command. | implemented, tested, sabotage-checked |
| `ConflictSelection.cs`, `JobStateText.cs`, `ProgressWindowPolicy.cs`, `ToastBatch.cs` | Conflict-dialog ticks, state wording, when a progress window opens or turns into the summary, toast coalescing. | implemented, tested |
| `StartupRules.cs` | Tray start, exit, menu state, startup toast choice, conflict-to-front. | implemented, tested |
| `HotkeySpec.cs` | `pasteHotkey` grammar, canonical form, reserved combinations with reasons, virtual-key mapping. | implemented, tested, sabotage-checked |
| `HotkeyGate.cs` | `HotkeyMatcher`, `HotkeyLatch`, `HotkeyRepeatGuard` (double tap), `HotkeyGate.Decide` (file-list allow-list), window-class classification, `HotkeyStatus`. | implemented, tested, sabotage-checked |
| `HotkeyTarget.cs` | `HotkeyPress`, `TabMatch.Choose`, `PasteFolderRule`, `HotkeyDeadline`, `ClipboardGuard`. | implemented, tested, sabotage-checked |
| `WinPath.cs` | Windows path rules as strings, incl. `ExtendedLengthPath` / `StripVerbatimPrefix` for raw Win32 calls. | implemented, tested |

## 2. Threading model

| Thread | Runs | Rules |
|---|---|---|
| **UI (main, STA)** | WinForms message loop; COM class objects (registered here, so activations and every `VerbCommand` call arrive here through the loop); clipboard (owner window lives here); all forms, NotifyIcon, toasts. | Never blocks and never touches the file system: a stat on a dead SMB share would freeze the tray and every right-click. `VerbCommand.Execute` reads the selection, calls `IVerbHandler.Invoke`, returns. `VerbDispatcher` runs verbs one at a time in click order; clipboard retries are awaited delays, so the loop keeps pumping. |
| **Scan** | One dedicated thread per scanning job (`TaskCreationOptions.LongRunning`), at most `JobQueuePolicy.MaxConcurrentScans` (4). | Lazy enumeration; checks cancellation per directory and every 1,000 entries; reports `ScanProgress`. |
| **Job workers** | `Job.RunAsync` continuations on the thread pool; steps sequential inside a job, jobs parallel. | Never touch UI objects. Ask the user only through `IJobPrompts`. Clipboard clear goes through `JobServices.ClearClipboardIfUnchanged`, which posts to the UI thread. Never call the sink, an event or the UI while holding the job lock. |
| **Pipe reader + consumer** | Per robocopy run: a reader that only drains the pipe into a bounded channel of line batches (64), and a consumer that parses, updates the ledger (one lock per batch) and writes to the sink. | The channel absorbs bursts: a sink that is briefly slow (antivirus scanning robocopy.log) does not hold up the pipe, and a sink or parser that throws never stops the drain. It is bounded on purpose, so memory stays bounded: a sink that stays slower than robocopy's output, or blocks, fills the channel, the reader then waits, and robocopy waits on its next log write until the consumer catches up. |
| **Progress sampler** | One `PeriodicTimer` loop for the app (333 ms). | Skips runs whose gate is closed. Disposing a run's tracking blocks until its in-flight callback returns. |
| **Hotkey hook** (`PasteHotkey`) | One dedicated background thread, above-normal priority, with its own message loop: the foreground WinEvent hook and, while File Explorer or the desktop is in front, the `WH_KEYBOARD_LL` hook. Started after `SignalReady`; settings changes post a new immutable spec to it; on exit WM_QUIT, unhook on this thread, joined for 1 s. | The keyboard callback allocates nothing, calls nothing that sends a window message, passes the key on any failure, and only fills a preallocated slot and signals an event. No logging of any kind. Windows removes the process's hooks if it dies. |
| **Hotkey locator** (`ExplorerFolderLocator`) | One long-lived background MTA thread, one press at a time (a press while one is in flight is taken and ignored). | Calls File Explorer out of process (ShellWindows → `IShellBrowser` → `IFolderView`); a 1.5 s watchdog from the key's tick abandons the press, toasts, `CoCancelCall`s the stuck call and drops a late answer. Never touches UI objects: results go to `IVerbHandler` through a UI-thread post. Abandoned if stuck at exit. |

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
released deterministically with `ComObject.FinalRelease()`, never left to the finalizer
thread: the selection's on the UI thread, the hotkey locator's on the locator thread, which
is the thread (MTA) that obtained them.

The hotkey locator is a COM client of File Explorer's ShellWindows (slots checked against
Wine's `exdisp.idl`, `servprov.idl`, `oleidl.idl` and `shobjidl.idl`):

| Interface | IID | Methods in vtable order |
|---|---|---|
| `IShellWindows` | `85CB6900-4D95-11CF-960C-0080C7F4EE85` | IDispatch's 4, then Count, Item(VARIANT, IDispatch**), _NewEnum, Register, RegisterPending, Revoke, OnNavigate, OnActivated, FindWindowSW, OnCreated, ProcessAttachDetach |
| `IServiceProvider` (`IOleServiceProvider`) | `6D5140C1-7436-11CE-8034-00AA006009FA` | QueryService (one slot; RemoteQueryService is its `call_as` form) |
| `IShellBrowser` | `000214E2-0000-0000-C000-000000000046` | IOleWindow's GetWindow, ContextSensitiveHelp, then InsertMenusSB, SetMenuSB, RemoveMenusSB, SetStatusTextSB, EnableModelessSB, TranslateAcceleratorSB, BrowseObject, GetViewStateStream, GetControlWindow, SendControlMsg (`[local]`, still a slot), QueryActiveShellView (slot 15), OnViewWindowActive, SetToolbarItems |
| `IFolderView` | `CDE725B0-CCC9-4519-917E-325D72FAB4CE` | GetCurrentViewMode, SetCurrentViewMode, GetFolder (slot 5), Item, ItemCount, Items, GetSelectionMarkedItem, GetFocusedItem, GetItemPosition, GetSpacing, GetDefaultSpacing, GetAutoArrange, SelectItem, SelectAndPositionItems |

`CLSID_ShellWindows = {9BA05972-F6A8-11CF-A442-00A0C90A8F39}`,
`SID_STopLevelBrowser = {4C96BE40-915C-11CF-99D3-00AA004AE837}`. The `Item` index is a VT_I4
VARIANT passed by value as a blittable 24-byte struct (`VariantInt32`, win-x64 layout).

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

Layer 1 is in effect: the tray starts with it (after a fix: `CoInitializeSecurity` takes only
an absolute-format descriptor, so the SDDL result is converted with `MakeAbsoluteSD`), and
medium-integrity callers, Explorer and the CLI, are served (testlog 2026-10-02). That a
low-integrity caller is refused, by any of the three layers, is still unverified.

### Activation and call sequence

1. Tray start: `ComServer.Register` calls `CoRegisterClassObject(clsid, factory,
   CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED)` for all three, then
   `CoResumeClassObjects`, then `SingleInstance.SignalReady`.
2. Not running: COM starts `"<exe>" -Embedding`, which parses to `CliRunTray(StartedByCom: true)`;
   the tray starts normally and stays resident. If another tray holds the mutex, the
   `-Embedding` instance polls for up to 10 s (`SingleInstance.WaitForReadyOrAcquire`): the
   other tray's ready event set means exit 0; the mutex released (that tray was shutting down
   and cleared its ready event when it revoked) means take it and become the server.
3. Explorer: `CreateInstance` → (`IInitializeCommand::Initialize`) → `SetSelection` → setters →
   `Execute`. For a folder-background click the observed order is `CreateInstance`,
   `SetDirectory(<the open folder>)`, `SetSelection(NULL)`, `Initialize`, `Execute`: the folder
   arrives only through `SetDirectory`. For a click on items the directory is their parent, so
   `ShellVerbs.InvocationItems` uses it only for Robo-Paste with nothing selected
   (testlog 2026-10-02). `Execute` checks the caller, reads all paths (`ShellSelection.ReadPaths`: one
   `BindToHandler(BHID_DataObject)` + `CF_HDROP` read; per-item fallback), releases the
   array, calls `IVerbHandler.Invoke`, returns `S_OK`.
4. No exception crosses the COM boundary; every implementation catches and returns an HRESULT.
5. Exit: revoke first and clear the ready event, close open conflict questions, then cancel
   jobs, then release the single-instance mutex.

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
| ⌫ `Software\Classes\<assoc>\shell\<Verb>` | `MUIVerb` / `MultiSelectModel` | `Robo-Cop&y` (`Robo-C&ut`, `Ro&bo-Paste`: `ShellVerbInfo.MenuLabel`, one access key each; the CLSID name keeps the plain `Label`) / `Player` (copy, cut) or `Single` (paste) |
| (same key) | `Icon` | `<install dir>\robo-copy.ico` (`robo-cut.ico`, `robo-paste.ico`): written by install beside the exe, deleted by uninstall; one pixel-fitted frame per display scale, 16–48 px |
| `…\shell\<Verb>\command` | `DelegateExecute` | `{verb clsid}` |
| ⌫ `Software\Microsoft\Windows\CurrentVersion\Uninstall\RoboRightClick` | `DisplayName`, `DisplayVersion`, `DisplayIcon`, `InstallLocation`, `UninstallString`, `NoModify`=1, `NoRepair`=1 | Settings → Apps → Installed apps entry |
| `Software\Microsoft\Windows\CurrentVersion\Run` | `RoboRightClick` | `"<exe>"` (only when autostart resolves true; always removed) |

Associations: RoboCopy and RoboCut on `AllFilesystemObjects`; RoboPaste on
`Directory\Background`, `Directory`, `Drive`. `DelegateExecute` sits on the verb's `command`
subkey (as in the Microsoft sample), `MultiSelectModel` on the verb key. `Single` hides
Robo-Paste for a multi-folder selection (Explorer's own Paste is offered there and pastes into
the right-clicked folder, which a DelegateExecute verb is not told; deviation in
docs/parity.md). The `Directory\Background` key has no `MultiSelectModel`
(`ShellVerbs.MultiSelectModelFor`): a background click selects nothing, and with `Single`
Explorer hid the item (testlog 2026-10-02). Autostart on
reinstall comes from `Registration.ResolveStartWithWindows`: an explicit `--autostart` /
`--no-autostart` wins (and is written to config.json, unless a newer version wrote that file
or its version cannot be read: `SettingsSerializer.MayOverwrite`),
otherwise the existing config's choice is kept. Each verb key carries the `Icon` value in the
table above since commit e13fa37. Whether Explorer shows those icons is unverified on Windows:
the 2026-10-02 session ran earlier builds, which wrote no `Icon` value, and saw none.

## 5. Verbs and clipboard

- **Untrusted input.** Every path from the clipboard, the shell selection or the CLI is
  untrusted: any process can write `CF_HDROP`, and any same-user COM client can send a
  selection. `PathPolicy` (Core) accepts only plain drive and UNC paths and refuses anything
  robocopy could read as a switch, a wildcard, a device or a stream. `RobocopyArgs.Quote`
  throws on a quote as a second line.
- **Robo-Copy / Robo-Cut:** selection paths through `PathPolicy` (any refusal: a
  `SelectionNotFiles` toast, nothing written), then `ClipboardService.WriteFilesAsync(paths,
  verb, mode, shellIdList)` writes every entry of `ClipboardPayload.ForFiles`: `CF_HDROP` (wide
  `DROPFILES`), `Preferred DropEffect` (1 copy, 2 move), the selection's `Shell IDList Array`
  when one came with it and `ClipboardPayload.IsShellIdListFor` accepts it for exactly these
  items, and, in ephemeral mode,
  `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0`,
  `CanUploadToCloudClipboard = 0`. No job starts. A clipboard held by another program through
  about one second of retries gives a `ClipboardBusy` toast. The ID list is read in `Execute`
  from the same data object as `CF_HDROP` (no file-system access); without it Explorer's own
  Ctrl+V of a copy into the folder it came from fails with "The source and destination file
  names are the same" instead of creating "name - Copy" (testlog 2026-10-02).
- **Robo-Paste:** destination = `ShellVerbs.PasteDestination(selection)`.
  `ClipboardService.ReadFilesAsync` reads `CF_HDROP` (Core decoder with size and count limits;
  a name without its terminator refuses the whole block; the ANSI form through Core's bounded
  `VerbRules.SplitAnsiDropFiles` plus `MultiByteToWideChar(CP_ACP)` under the same limits),
  `Preferred DropEffect` and then the sequence number, inside one `OpenClipboard`. The service
  also classifies the read (`VerbRules.ClassifyClipboard`), because only it sees whether a
  Shell IDList Array or file-group descriptor is present, and returns a refusal or files.
  Verb = `ClipboardPayload.VerbForPaste` (move only for a pure move marker). A move where every source's parent is the destination is Explorer's
  no-op and does nothing. Otherwise `JobManager.Enqueue(PasteOrder, cut ? sequence : null)`;
  everything that touches the disk happens later, in Scanning, on a worker thread.
- **Refusals** (`VerbRefusal`): empty clipboard, virtual items, clipboard too large, busy, no
  file-system destination, several destinations, non-file selection, same cut already being
  pasted, selection too large, and `Failed` (a verb threw; the dispatcher logs the exception
  type only). Each has one fixed, path-free sentence in `ToastText.ForRefusal`.
- **After a cut-paste ends `Done` with at least one item moved:** `ClearIfUnchangedAsync(sequence)`
  compares the sequence number, opens the clipboard (retried with awaited delays for about a
  second), compares again, then empties. A newer clipboard write by anyone is left alone.
  `DoneWithErrors` keeps the clipboard (its sources still exist).
- **Selection size.** `ShellSelection.ReadPaths` checks Core's `SelectionLimits` (250,000
  items, a 64 MiB `CF_HDROP` block, 32,767 characters per path and 32 Mi characters in all,
  the clipboard's own limits) before it copies anything, for every verb and for the CLI. Over
  a limit, `Execute` hands `VerbRefusal.SelectionTooLarge` to `IVerbHandler.RefuseSelection`,
  which queues the toast behind earlier clicks, and returns `E_FAIL` at once (the CLI exits 1).
  So Robo-Copy never puts more on the clipboard than Robo-Paste reads back. Explorer's own
  Ctrl+C has no such limit (deviation in docs/parity.md).

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
  `StepOutcome.ReplanAsMove` (`StepLedger.InProcessReplannedAsMove`): after the plan's steps,
  the re-planned items are scanned again and run as a **second ledger part**, an
  `ExecutionPlan` with its own `StepLedger` (a ledger is bound to one fixed plan), planned with
  Ask and `SkipAll` so kept names are left out by construction and late arrivals are skipped.
  Progress, cancel cleanup, retry and the summary read both parts. A keep-both cut, or a
  directory link, whose rename turns out to cross volumes is **refused**
  (`StepLedger.InProcessRefused`, a plan issue), never moved by robocopy: robocopy cannot
  write under the keep-both name, and it follows a link given as its source root.
  `File.Move` would silently copy and delete across volumes, an app-initiated source deletion
  that invariant 1 forbids.
- `DuplicateFileStep`, copy-mode `KeepBothStep` → `CopyFileEx(COPY_FILE_FAIL_IF_EXISTS)`.
- **Cancel** (any state before Finalizing): `CancelRequested` at once. The running
  `RobocopyRun` suspends robocopy (`NtSuspendProcess`; a pause change can no longer resume it)
  and calls `IRobocopyObserver.OnSuspendedForCancel`, then kills it and waits. While it is
  suspended, the job (`Job.ObserveAtKill`) takes the step's files not yet reported complete and
  not present before the step, lists each of their destination folders once, and for every one
  that exists calls `ProcessNative.ObserveAtKill`: a handle with `FILE_READ_ATTRIBUTES` only (no
  sharing mode refuses it) and `FILE_FLAG_OPEN_REPARSE_POINT`, the file's identity (volume
  serial, file ID from `FILE_ID_INFO` or, on FAT32/exFAT, the 64-bit file index, plus the
  creation time, because FAT reuses an index for the next file in the same directory slot) and
  the processes that hold it open (`FileProcessIdsUsingFileInformation`). Each file is
  `OpenByRobocopy`, `NotOpenByRobocopy`, `Absent` or `Unknown`. Then
  `CancelCleanup.Select(ledger.KilledRunFiles, ledger.CompletedSources, presence set,
  observations, move, destinationExists, sourceStillExists, claimedByOtherJob)`, skipped entirely
  when the ledger found robocopy's paths unreliable. Each `Delete` entry goes through
  `ProcessNative.DeleteFileIfSameFile`: one handle opened with `FILE_FLAG_OPEN_REPARSE_POINT` (no
  backup semantics, so a folder does not open), identity and attributes checked on that handle,
  deleted with `FileDispositionInfo` on the same handle; no check-then-delete window, and a file
  swapped in under the same name after the kill is not the one observed. Rules (each tested,
  each test seen failing with the rule removed):
  - candidates are only the unfinished files of runs the cancel killed; a run that ended on its
    own contributes nothing (what it did not report it skipped or failed on);
  - a file is deleted only if robocopy held it open while suspended and its identity is known.
    A late arrival that robocopy skipped (it prints nothing for those) is `NotOpenByRobocopy`
    and stays. This closes the race the merge review left open (section 16): a file that
    appeared after the presence check is never deleted, because robocopy never opens a file it
    skips;
  - reported-complete files stay; files present before their step are never deleted and are
    reported as possibly incomplete only under an overwriting policy (`RobocopyArgs.MayOverwriteExisting`:
    Replace, KeepNewer); files another job of this session planned or wrote are never deleted;
    for a cut, a destination whose source is gone stays;
  - without evidence (robocopy could not be suspended, or the file system cannot answer) nothing
    is deleted; an existing new file is reported as possibly incomplete instead.
  `LeftInPlace` becomes `JobSnapshot.DamagedOnCancel` (attention, toast, "Finish copying them" /
  "Finish moving them", which repeats them with Replace). Each cleanup writes one path-free
  debug line (`RoboRightClick cancel cleanup: candidates=… open=… notOpen=… deleted=…`).
  Remaining edges: a late arrival under a Replace step is overwritten by robocopy itself, so a
  cancel deletes the partial overwrite (the loss is the overwrite's, which a normal run makes
  too); robocopy decides skip or copy when it lists a folder, so a file that appears after that
  listing is overwritten in any run, canceled or not (robocopy's own race, not cleanup's).
- **Errors** → `DoneWithErrors`; "Try again (N)" = `JobManager.Retry(parent)` →
  `RetryPlanner.ForFailures(plan, ledger.Retryable, ledger.FailedInProcessSteps)` per ledger
  part; a child job with `ParentId`, logging mode `JobSinks.ForDerivedJob(parent, current)`;
  its Scanning only refreshes totals and presence and never prompts. Only robocopy steps'
  files are retried file by file (a keep-both or duplicate retried as robocopy would be written
  under the source name over the file the user kept); failed in-process steps repeat whole. A
  `Failed` job's "Try again" is `JobManager.Rerun` (the original order, full re-scan), and so is
  a `DoneWithErrors` job whose ledger found robocopy's paths unreliable: re-running a recursive
  `/MOVE` step under Replace is not safe, so the whole paste is scanned and asked about again.
  Refusals are not retryable and are counted separately (`RefusedCount`).
- **Late arrivals** (files skipped because their name appeared after the scan) are not errors;
  they are counted in `JobSnapshot.SkippedAppeared`, and a `Done` job with any opens the
  summary so the user sees which ones (`ProgressWindowPolicy.OnTerminal`).
- **Invariant 1:** the job never deletes a source. Only robocopy `/MOV`/`/MOVE` and renames
  move anything.

## 7. UI surfaces

- **Progress window** (per job, `showProgressWindow`, default on): Explorer's copy dialog
  counterpart, opened about one second after the job is created (or straight into the summary
  when the job already ended needing the user). It closes on Done and turns into the error
  summary on DoneWithErrors, Failed, a damaging cancel or a Done with late arrivals. It owns
  the job's conflict dialog, which is how that dialog reaches the front. A pause latched before
  Running shows as "Paused (waiting)" in every window (`JobSnapshot.PauseRequested`).
- **Error summary** lists retryable errors, refused items with their reasons, files a cancel
  left partly replaced and late arrivals, each per item (`JobManager.ErrorsOf`, `IssuesOf`,
  `DamagedOf`, `SkippedAppearedOf`; first 1,000 of each, exact counts from the snapshot). Only
  Skip or Try again acknowledges a job; closing the summary keeps its attention state.
- **Tray** (`TrayApplication`): icon and tooltip from `TrayStatus.Derive(snapshots, mode)`
  (attention > running > paused > idle; tooltip ≤ 127 chars). Left click and double click open
  Jobs. Menu: Jobs…, Pause all (checked while on), Resume all, Ephemeral mode (check),
  Settings…, Open logs (hidden in ephemeral), Exit (confirms when jobs are active, with the
  Gridline confirmation). Between Ephemeral mode and Settings… a hotkey line
  (`ToastText.HotkeyTrayLine`: the combination, "off", "off (setting invalid)" or "not
  active") opens Settings at the Hotkey field. The installer starts the tray with
  `--after-install`; that start, and only that one, shows the hint about pinning the tray
  icon, which also names the hotkey when it is on (`StartupRules.PickStartupToast`).
  Settings are saved off the UI thread (`SettingsStore.SaveAsync`); a reload re-runs the
  start queue (`JobManager.SettingsChanged`).
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
- Interrupted jobs (normal mode): at start, `JobLogStore.MarkInterrupted` reads each job.json;
  one whose last state is not terminal (`JobRecords.CheckInterrupted`) was left by a run that
  ended mid-paste. It is counted for the one startup toast and rewritten, through the same
  temp-file-then-replace writer, with a final `"interrupted"` state, so the next start does
  not report it again. Jobs created at or after this process started, and folders of active
  jobs, are skipped; a record of a newer or unreadable format version is reported but never
  rewritten, so it is reported at every start, as is one whose rewrite failed. The counting
  and the skips are host code, untested.
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
| `--install [--autostart \| --no-autostart] [--quiet]` | Install (section 10). `--quiet`: no message box, exit code only. Repeated or contradictory options are refused. |
| `--uninstall [--quiet]` | Uninstall (section 10). |
| `--after-install` | Tray started by the installer: shows the first-run hint once. Not meant for users. |
| `copy <path>...`, `cut <path>...`, `paste <folder>` | `ComClient.Invoke`: `CoCreateInstance(CLSCTX_LOCAL_SERVER)` on the verb's CLSID, `CoAllowSetForegroundWindow`, `SetSelection(SHCreateShellItemArrayFromIDLists(SHParseDisplayName(...)))`, `Execute`. The same path as a right-click, started by the CLI process instead of Explorer. Returns when the verb is accepted. |

Exit codes: 0 ok, 1 failed, 2 usage. The exe is a GUI-subsystem program, so shells do not
wait for it: scripts use `start /wait` or `Start-Process -Wait -PassThru`. Verb output goes to
the parent console via `AttachConsole`; install and uninstall results use a message box.

## 10. Install and uninstall

- `--install`: refuse (message, exit code 1, nothing changed) when uninstall would later
  refuse the locations (`UninstallPlan.InstallRefusal`: the same checks uninstall runs, e.g. a
  profile path holding `&`, `%` or `!`) → stop a running tray (exit request; refused while jobs run) → copy exe to
  `%LOCALAPPDATA%\Programs\RoboRightClick\` → write `Registration.InstallValues` → write the
  default config.json only if absent (or apply an explicit autostart flag to the existing one)
  → start the installed tray → message.
- `--uninstall`: stop the tray → remove `Registration.UninstallRemovals()` → delete exactly the
  known files (`UninstallPlan`: config.json and its `.bad` and `.tmp`, history and its rotated
  file, crash.log and crash.1.log, each job folder's job.json, robocopy.log and a leftover
  job.json.tmp), then
  `RemoveDirectory` on each folder after checking its leaf name and that it is not a reparse
  point → the install folder via `%SystemRoot%\System32\cmd.exe` (absolute path, validated
  arguments, its one-second sleep `PING.EXE` also by absolute path) when running from it.
  Nothing outside `AppPaths` and the registry list; never a blind recursive delete.
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
6. Crash log in normal mode only. An unhandled exception, on the UI thread
   (`Application.ThreadException`) or any other (`AppDomain.UnhandledException`), appends one
   entry to `%LOCALAPPDATA%\RoboRightClick\crash.log` (`AppPaths.CrashLogFile`): exception
   types outermost first, messages with path-looking parts replaced by `[path]` (Core
   `PathHeuristic.Scrub`, the heuristic `FailureText` uses; an unquoted path-looking word takes
   the rest of its line, since a path with spaces has no other end), stack traces, app version,
   UTC time, Windows build. No job data is added; a stack trace is written as the runtime
   reports it. The file is rotated to `crash.1.log` (one kept) before it would pass 256 KB, an
   entry is at most 32K characters, and one run writes at most 20 entries. Nothing is written
   in ephemeral mode, once any ephemeral job has existed in this session, or outside the tray
   (install, uninstall, CLI) (`CrashLog.MayWrite`). A tray that fails before its job manager
   exists (the caught "could not start" error included, `CrashPolicy.LogHandled`) has no job
   yet, so config.json's `logging` decides, and an unreadable file means no log
   (`CrashLog.ModeFromConfig`). After teardown the last session's manager still decides. While
   ephemeral jobs run, a crash ends the process without a WER report, as before
   (`CrashPolicy`). Uninstall deletes both files. This reverses the beta's original "no crash
   log" rule, with the owner's approval: without one, a beta bug report depends on the tester
   remembering a message box, which is not enough to find a fault. Cross-compiles; unverified
   on Windows.
7. Sign-out with active jobs: shutdown is vetoed with a reason; if Windows ends the session
   anyway, jobs are canceled with cleanup within 5 s. A job.json left non-terminal is reported
   at the next start and marked so it is not reported again (normal mode; the exceptions are
   under "Interrupted jobs" in section 8).

## 12. Verified vs unverified on Windows

| Claim | Status |
|---|---|
| `/UNILOG` to a named pipe carries exact UTF-16; stdout cannot | verified (testlog 2026-10-02) |
| Under `/MT:32` file lines arrive at completion; failed file line precedes its ERROR | verified (testlog 2026-10-02); after a kill one more line can arrive for a file still being written (4 of 4 probe runs), which the parser drops as a held line (testlog 2026-10-02, cancel entry) |
| `GetProcessIoCounters` tracks progress; destination size does not | verified (testlog 2026-10-02) |
| `NtSuspendProcess`/`NtResumeProcess` pause and resume `/MT:32` cleanly | verified (testlog 2026-10-02) |
| Metadata flags vs Explorer | verified, deviations in docs/parity.md |
| Out-of-process DelegateExecute with `REGCLS_MULTIPLEUSE`: 500 items in one call to a running instance | verified (testlog 2026-10-02) |
| `BHID_DataObject` on Explorer's selection yields `CF_HDROP` | verified (testlog 2026-10-02: 500 items read in 1.6-14 ms, the data-object path, which alone also yields the ID list) |
| Time for 50k items vs per-item reads | **unverified** (spike 1) |
| `-Embedding` start when the tray is not running; cold-start time; Run-key race | verified (testlog 2026-10-02: `Execute` 309-382 ms after COM creates the process; race emulated with a plain start, 14 runs); a right-click right after sign-in **unverified** |
| `MultiSelectModel=Single` hides Robo-Paste for multi-folder selections; works on the background verb | first half verified; the second was false, the background key now has no `MultiSelectModel` (testlog 2026-10-02) |
| A low-integrity process cannot activate or call the server (`icacls /setintegritylevel low` test exe) | verified (testlog 2026-10-02, user experience entry: `Security.Tests.ps1`, low-integrity copy exit 1, control exit 0; before 4b8b677 it crashed in `EnableVisualStyles` first) |
| HKCU AppID `AccessPermission`/`LaunchPermission` do not break Explorer's activation | verified (testlog 2026-10-02) |
| HKCU AppID `AccessPermission`/`LaunchPermission` are honored | a low-integrity call is refused (row above); whether the AppID values or the in-process integrity check refused it **unverified** |
| Explorer allows the tray to take foreground (conflict dialog, progress window) | **unverified** (spike 1) |
| Remote clients are refused by the output pipe | **unverified** |
| Robocopy opens the output pipe under the app's DACL (user ReadWrite\|CreateNewInstance, NETWORK denied); a mismatched client PID is disconnected; a tiny run's output is not lost to the PID check | **unverified** |
| Cancel cleanup deletes the partial copies robocopy held open while suspended for the kill (`FileProcessIdsUsingFileInformation`, identity-checked handle delete) and keeps a late arrival robocopy skipped | verified on NTFS, FAT32 and exFAT (testlog 2026-10-02, cancel entry); through the app on an SMB share **unverified** (the PID query answered on an SMB loopback share with plain robocopy); refusing links and folders, and clearing read-only first, **unverified** |
| A cut never deletes a source whose copy failed: locked source, access-denied destination, full destination, canceled cross-volume cut | verified (testlog 2026-10-02, cancel entry) |
| With `/NC`, robocopy prints destination-only ("extra") files like copied files; `/XX` removes them | verified (testlog 2026-10-02, cancel entry) |
| A losing `-Embedding` start takes over from a tray that is shutting down (`WaitForReadyOrAcquire`) | outcome verified (testlog 2026-10-02: 5 runs, the call served by the new tray); which branch ran **unverified** |
| `ShutdownBlockReasonCreate` on a hidden, never-shown top-level window vetoes sign-out with the reason shown | **unverified** |
| No WER report after `TerminateProcess` while ephemeral jobs run (machine-wide LocalDumps aside) | **unverified** |
| Rename and `CopyFileEx` on paths over 260 characters with the extended-length prefix | **unverified** |
| Gridline fonts and layout under DPI scaling (fonts sized per `DeviceDpi` alongside `AutoScaleMode.Dpi`) | verified at 100% and 150%, including a live switch with windows open (testlog 2026-10-02, user experience entry); IBM Plex renders through GDI (start-up check "6 of 6"); 125%, 175% and above and a second monitor **unverified** |
| `SetDefaultDllDirectories(SYSTEM32)` does not break WinForms start-up in a single-file app | verified (testlog 2026-10-02); the planted-DLL check **unverified** |
| Explorer ghosts icons after a Robo-Cut clipboard write | verified false: no ghosting (testlog 2026-10-02, deviation in docs/parity.md) |
| Explorer shows each verb's `Icon` (commit e13fa37) in the classic menu | **unverified**; the 2026-10-02 builds had no icons |
| crash.log written and rotated; an interrupted job reported once; the oversized-selection toast; install refusing an unsafe profile path | **unverified**; Core decisions tested on Linux |
| Robocopy `/MOV` deletes the source of a "same" file it skipped | **unverified**; the design no longer depends on it either way |
| A killed robocopy leaves its in-flight files at full length | verified (testlog 2026-10-02, cancel entry); cleanup no longer depends on it |
| `CopyFileEx` sets the archive bit like Explorer's copy | **unverified** |
| Generated COM vtables match the shell's (`[GeneratedComInterface]` on these IDLs) | verified for every method the app calls: `IClassFactory`, `IExecuteCommand`, `IObjectWithSelection`, `IInitializeCommand`, `IShellItemArray.BindToHandler`/`GetCount`, `IDataObject.GetData` (testlog 2026-10-02) |
| Install and uninstall footprint, verbs against Explorer, cut safety, cancel cleanup and ephemeral mode through every `scripts/e2e` script | verified (testlog 2026-10-02, user experience entry: `Run-All.ps1`, all seven scripts pass on the final build, Ephemeral with judged Windows noise excluded) |
| Every Gridline surface (install offer and results, progress, Jobs, conflict, error summary, settings, tray menu and icons, toasts) | verified on screen at 100% and 150% (testlog 2026-10-02, user experience entry, `docs/evidence/2026-10-02/12-30`); the tray's four error notices **unverified** |
| Robo-Paste hotkey: hook scoping, gate, latch, tab capture, folder lookup, watchdog, clipboard guard, Settings row, tray line | **unverified**; Core decisions tested and sabotage-checked on Linux; the release gate is in docs/decisions/0001-paste-hotkey.md |
| `IShellBrowser::GetWindow` returns the tab's `ShellTabWindowClass` window | **unverified** (gate 2); if it returns the frame instead, every Explorer hotkey press is refused, never misdirected |
| Classic-menu access keys (Y, U, B) run their items | **unverified** (gate 14) |
| Everything else in the host | **unverified**; cross-compiles only |

## 13. Work packages (disjoint file ownership)

Historical: all nine packages were merged into main on 2026-10-02 and their contract change
requests applied (section 16). The table records who owned what during the parallel build.

The beta's remaining work was split into nine packages that could be built in parallel in separate
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
2. **Preferred DropEffect for copy:** the design fixes 1. Explorer's own Ctrl+C writes 5
   (copy | link), measured 2026-10-02. Both values paste as a copy, in both directions
   (testlog 2026-10-02); 1 stays.
3. **Menu icon:** settled in commit e13fa37. Each verb has an `.ico` written beside the
   installed exe and an `Icon` registry value pointing at it (section 4); uninstall deletes
   both. Explorer's own classic Cut, Copy and Paste show no icon (measured 2026-10-02), so this
   is a deviation, listed in docs/parity.md. That Explorer displays the icons is unverified
   on Windows.
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

## 16. Merge record (2026-10-02)

Questions the parallel packages raised, and how the merged code answers them.

| Question | Settled as |
|---|---|
| How are robocopy moves that replace failed renames tracked? | A second ledger part (own `ExecutionPlan` + `StepLedger`); no ledger append API (section 6). |
| Retry when `PathsUnreliable` | `JobManager.Retry` re-runs the whole paste (`Rerun`); no whole-step retry API. |
| Which in-process steps does "Try again" repeat? | `StepLedger.FailedInProcessSteps`; the job reports re-planned and refused steps to the ledger. |
| `ConflictScan.Resolve` vs the planner's per-step policy | The planner decides per step; `Resolve` is the job-wide answer only. |
| `StepOutcome.Failure` text | One path-free sentence from its producer; `FailureText` filters again. |
| `IJobSink` call order | Documented on the interface; the job disposes a disposable sink after `JobFinished`. |
| Pause latched before Running, refused items, damaged files, late arrivals in the UI | `JobSnapshot.PauseRequested` / `SkippedAppeared`; `JobManager.IssuesOf` / `DamagedOf` / `SkippedAppearedOf`. |
| `JobSnapshot.Acknowledged` vs `ErrorSummaryChoice.None` | Only Skip or Try again acknowledge. |
| Conflict dialog: both boxes ticked where keep-both is not allowed | Skip (keep the destination the user ticked); the dialog makes the boxes exclusive there. |
| Tray icon tint | Gridline tokens (ink tile, gray frame for ephemeral), not a violet ring. |
| First-run hint | `--after-install` from the installer; no marker file. |
| Whole-file config failure | `SettingsLoadResult.Unreadable`. |
| Extended-length paths | Core `WinPath.ExtendedLengthPath` / `StripVerbatimPrefix`, tested. |

Known gaps carried into the Windows phase:

- Cancel cleanup cannot tell a partial file from a late arrival robocopy skipped silently in
  the same killed run (section 6). Resolved after the merge: cleanup deletes only files robocopy
  held open at the kill (section 6, testlog 2026-10-02 cancel entry).
- `CF_HDROP` size is not capped on Robo-Copy; Robo-Paste refuses past its limit.
- No COM call timeout guards against a hostile `IShellItemArray` that blocks. (An oversized
  selection is now refused with a toast, section 5; unverified on Windows.)
- The first-run install offer (`Installer.OfferInstall`) uses the native TaskDialog, before any
  Gridline font is loaded. Resolved: it is a Gridline `MessageDialog` (testlog 2026-10-02, user
  experience entry).
- Hand edits of `startWithWindows` reload the setting but do not rewrite the Run value; only a
  save from the app does.
- `scripts/publish.sh` publishes without `-r win-x64` and with the single-file analyzer off,
  because the lockfiles carry no runtime identifier and the ILLink package is not in them.
  Changing either is a separate, reviewed dependency commit.

