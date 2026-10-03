# Test log (append-only)

Format: date · Windows build · machine (VM/physical) · what ran · observed result.

## 2026-10-02 · build 26200 (robocopy 10.0.26100.1) · VM · M0 spikes 2, 3, 4

Disposable Windows 11 Enterprise evaluation VM, no network egress. Scripts in `spikes/m0/`,
raw results in `spikes/m0/results/`.

**Spike 2: robocopy output channel** (`robocopy-capture.ps1`, `robocopy-pipe.ps1`)
- Redirected stdout with `/UNICODE` is **not** Unicode. It's a UTF-16 BOM (`FF FE`) followed
  by narrow single-byte text. `📁` and CJK names become `?`. Without `/UNICODE`, stdout is
  code page 437 (`ü` → `0x81`), and CJK is lost the same way. **Stdout can't carry filenames.**
- `/UNILOG:\\.\pipe\<name>` works. Robocopy connects to a named pipe the reader owns and
  writes true UTF-16LE with a BOM. Emoji, CJK and Latin-1 names arrive exactly. Stdout carries
  only a 36-byte header. No log file appeared on disk (searched the work dir and `%TEMP%`).
- Under `/MT:32`, each file's line arrives **when the file finishes**. Three 512 MB files
  reported at about 3.08 s, matching the I/O counters reaching the full 1.61 GB.
- A failing file (locked by another process, error 32) still gets its file line **first**,
  immediately followed by the ERROR line, the message line, a blank line, and
  `ERROR: RETRY LIMIT EXCEEDED.` Exit code 9.
- Destination files are **allocated at full length** before data is written (all 3 × 512 MB
  were full length at the first 100 ms poll). Destination size can't measure progress.
  The process's `GetProcessIoCounters` read bytes track progress continuously.
- Line format with `/NP /NDL /NC /NJH /NJS /BYTES /FP`: `\t  \t\t<size right-aligned>\t<full path>`.
  Captured pipe output is committed as test fixtures (`tests/.../Fixtures/*.utf16`).

**Spike 3: pause** (`suspend.ps1`, 6 × 768 MB, `/MT:32`)
- `NtSuspendProcess` returned 0. The suspend landed mid-copy (1.73 GB of about 9.7 GB
  read+write done). I/O counters moved **0 bytes** during 5 s suspended.
  `NtResumeProcess` returned 0. Exit code 1 (success), and total destination size equals source.

**Spike 4: Explorer parity** (`parity.ps1`, Explorer copy via `Shell.Application.CopyHere`
on the interactive desktop through a scheduled task)
- Results in docs/parity.md. Candidate flags `/COPY:DAT /DCOPY:DA /A+:A /XJD` re-run and
  diffed against Explorer: matches on attributes, archive bit, streams, file symlink content,
  folder created time, and not following junctions or folder symlinks. Remaining deviations
  are listed in parity.md.

**Not yet run:** spike 1 (DelegateExecute multi-select into a running COM server) and
spike 5 (icon ghosting after Robo-Cut). Both need the host's COM server.

**VM housekeeping:** the test account's password had expired (2026-09-21), which broke
autologon and left the VM at the sign-in screen. The account was set to
`PasswordNeverExpires`. The password itself was not changed.

## 2026-10-02 · build 26200 · VM · M0 spikes 1 and 5, activation, clipboard interop

Same disposable Windows 11 Enterprise evaluation VM, no network egress, test account signed
in to the desktop. Each build came from `./scripts/publish.sh`; the zip's SHA256 was checked
on Windows (`Get-FileHash`) before `RoboRightClick.exe --install --quiet` ran on the desktop.
Right-clicks were real mouse input on Explorer; screens are in `docs/evidence/2026-10-02/`.
Calls were counted with the app's own debug line (`OutputDebugString`, one per `Execute`:
verb, item count, read time, process uptime; no paths), read by a listener on the desktop.
Final build: commit e7522ce, exe SHA256
`127e2c2c6080410eade8c7f6996f4ac5bd52bb2346cd7a3434fccb321d0d70d6` (zip
`165036802ece48ca1b3332e1f921b3888a9c66926e79db54f080d5614f2645cc`). Results marked
"(final)" were rerun on it; the rest ran on the intermediate builds of the commits named.

**Defects found and fixed**
- Every tray start failed: message box "RoboRightClick could not start. CoInitializeSecurity
  failed (0x80070551)" (`ERROR_INVALID_SECURITY_DESCR`), first for the installer's tray, then
  for COM's `-Embedding` start after a right-click, which Explorer reported as "Server
  execution failed" (`01-coinitializesecurity-0x80070551.png`). Cause: a self-relative
  descriptor; `CoInitializeSecurity` takes an absolute one. Fixed in bceac51.
- Robo-Paste was missing from the folder-background menu. Removing only
  `MultiSelectModel=Single` from the background key made it appear. Clicking it then did
  nothing: Explorer called `CreateInstance`, `SetDirectory(<folder>)`, `SetSelection(NULL)`,
  `Initialize("RoboPaste")`, `Execute`, and `Execute` returned `E_FAIL` for the missing
  selection. Fixed in e85e4be (`02-background-menu-robo-paste.png`,
  `03-background-paste-copied.png`).
- A same-drive cut-paste of a 4-byte file toasted "1 item (0 bytes) to dst". Fixed in acb9959.
- After a Robo-Copy, Explorer's own Ctrl+V into the source's folder showed "The source and
  destination file names are the same" (Skip/Cancel) instead of creating "name - Copy"
  (`06-explorer-ctrlv-same-folder-after-robo-copy.png`). The same happened with a CF_HDROP
  written by .NET with Preferred DropEffect 5. With a "Shell IDList Array" added, the same
  Ctrl+V created "file006 - Copy". Fixed in e7522ce (`08-...-after-fix.png`).

**Spike 1: out-of-process DelegateExecute into a running multi-use server** (pass)
- Classic menu ("Show more options") on a file shows Robo-Copy and Robo-Cut; on a folder
  Robo-Copy, Robo-Cut and Robo-Paste; on a drive and a folder background Robo-Paste. Labels
  as registered. No icons; Explorer's own classic Cut, Copy and Paste show none either.
- Shift+F10 on this build opens the new (Windows 11) menu, with access keys; its "Show more
  options" (W) opens the classic menu with the Robo items.
- Ctrl+A on 500 files in an Explorer window, Robo-Copy: **one** `Execute` with `items=500
  skipped=0`, selection read 1.6 ms, in the tray started by the installer (same PID before and
  after); clipboard held 500 paths, Preferred DropEffect 1 (`04-500-selected-classic-menu.png`).
  (final): one `Execute`, `items=500`, read 14.0 ms; background Robo-Paste copied all 500,
  content identical, toast "500 items (2.82 KB) to dst".
- Robo-Cut of 498 files from the menu, then Robo-Paste on the drive item of a 512 MB test VHD:
  one `Execute` with `items=498`; all 498 moved across volumes, source folder empty, clipboard
  emptied, toast "Move finished, 498 items (2.81 KB) to R:\" (`09-robo-cut-498-to-drive.png`).
- Robo-Paste via folder right-click pasted into that folder, not its parent; via drive
  right-click into the drive root; via background into the open folder (`fromDirectory=True`).
- Two folders selected: Robo-Paste absent, Robo-Copy and Robo-Cut present
  (`07-two-folders-no-robo-paste.png`). Explorer's own Paste is shown there and pasted into the
  right-clicked (focused) folder only.

**Cold start, start-up race, takeover, single instance**
- Tray exited from its menu, then a classic-menu Robo-Copy: COM started
  `"<install dir>\RoboRightClick.exe" -Embedding` (parent svchost), `Execute` arrived 382 ms
  after that process was created, the file was on the clipboard.
- CLI `copy` with no tray (three runs, process killed in between): `Execute` 309, 321, 323 ms
  after the COM-started process was created; CLI start to exit 444-450 ms. With the tray
  running: CLI start to exit 122-145 ms. (Timing with `Start-Process -Wait` reads about 1 s in
  both cases; that is the wait itself, not the app.)
- Plain tray start (as the Run key does) and a CLI `copy` started 0-400 ms apart, 14 runs:
  every CLI exited 0, one tray remained, and it was the plain one. In the 4 runs watched by a
  process-start trace, COM launched a `-Embedding` process (parent svchost) each time, so that
  start waited for Ready, exited, and the activation reached the other tray.
- Exit event signalled, CLI `copy` 0-100 ms later, five runs: every CLI exited 0 and the
  call was served by a new `-Embedding` tray, the only one left. Which branch of
  `WaitForReadyOrAcquire` ran (waited then acquired, or acquired at once) was not observed.
- A second plain start and a manual `-Embedding` start with the tray running: both exit 0 in
  0.1-0.2 s, the running tray untouched.
- Sign-out ended the tray; sign-in started it from the Run key (parent explorer, no
  `-Embedding`) 7.4 s after Explorer started. A right-click in that window was not tried.

**CLI** (same COM path): `copy`, `cut` and `paste` exit 0 and each produced one `Execute` in
the running tray; the CLI's shell item array also supplies the ID list (`idList=True`).

**Spike 5: icon ghosting after Robo-Cut** (no ghosting)
- Robo-Cut: the file's icon is not dimmed, also after F5, and also once the clipboard carried
  the Shell IDList Array. Explorer's own Ctrl+X dims the icon at once
  (`05-ghosting-robo-cut-vs-explorer-cut.png`). Recorded in docs/parity.md.

**Clipboard interop**
- Explorer's own Ctrl+C writes Preferred DropEffect **5** (measured), plus Shell IDList Array,
  FileGroupDescriptorW and others.
- Robo-Copy (CLI) then Explorer Ctrl+V into another folder: copied, sources kept. Into the
  same folder: see the fixed defect above; (final) after a CLI `copy`, Ctrl+V created
  "s001 - Copy".
- Robo-Cut then Explorer Ctrl+V (and Explorer's Paste on a folder): moved. The clipboard still
  held the cut afterwards (Explorer clears it after its own cut-paste).
- Explorer Ctrl+C then Robo-Paste (folder menu): copied, clipboard unchanged. Explorer Ctrl+X
  then Robo-Paste (CLI): moved by rename, clipboard emptied.
- Cut-paste with the clipboard changed mid-job: "Pause all" on (checked in the tray menu), a
  400 MB cross-volume Robo-Cut + Robo-Paste waited as "Paused" in its progress window; the
  clipboard was set to another file; Resume: the move completed and the clipboard still held
  the other file. "Resume all" cleared the check.

**Uninstall** (final): `--uninstall --quiet` exit 0; tray gone, install folder, config and log
folders gone, all nine class and verb keys, the AppID, the Uninstall entry and the Run value
gone; `Software\Classes\CLSID` and `AllFilesystemObjects\shell` kept.

**Not run in this session:** 50,000-item selection timing, the low-integrity caller check,
remote pipe access, a right-click in the first seconds after sign-in.

**Harness note:** one `--install --quiet` run reported exit 0 but left the previous exe and
registry in place (the tray PID did not change). It did not recur in later runs, each of which
was checked by comparing the installed exe's hash and the tray's start time. Cause unknown.

**VM housekeeping:** the test VHD was detached and deleted, the scratch folder, scheduled tasks
and listener removed, the app uninstalled, the clipboard emptied.

## 2026-10-02 · build 26200 (robocopy 10.0.26100.1) · VM · cut safety and cancel cleanup

Same disposable Windows 11 Enterprise evaluation VM, no network egress, test account signed in
to the desktop. Scripts ran on the desktop through a one-shot scheduled task. Each build came
from `./scripts/publish.sh`; the zip's SHA256 was checked on Windows and the installed exe's hash
compared with the package after `--install --quiet`. Test volumes were virtual disks attached
with diskpart: 300 MB NTFS, 40 MB NTFS (about 30 MB free), 200 MB FAT32, 200 MB exFAT.
Builds: **old** = commit b41a554 (zip `8147aa9b…`); **final** = commit b7576e0, exe SHA256
`90ba48ff534f87bb74dbacaefd860c3db50fe28a81afd92f81019d3d759a33ed` (zip `2277f0b8…`), which also
contains the branding and per-drive thread commits made in parallel (e13fa37, efe8482; the
latter not yet wired into the runner). Results marked "(final)" ran on it; **fixed** = the
intermediate build with 4ecce52 and 07419c0 (zip `17e64fd5…`). Cleanup was observed
through the app's new debug line (`RoboRightClick cancel cleanup: …`, counts only), read by a
debug-output listener on the desktop.

**Plain robocopy and PowerShell probes** (no app)
- Robocopy suspended mid-copy (`/MT:32`, 6 × 512 MB): every in-flight destination file was full
  length; its modified time equalled its creation time (no 1980 marker), and after the kill it
  moved to the kill time. `FileProcessIdsUsingFileInformation` on a handle with
  `FILE_READ_ATTRIBUTES` listed robocopy's PID for each in-flight file and no PID for a file put
  in the destination before the run, which robocopy skipped (`/XC /XN /XO`). Same answer on
  FAT32, exFAT and the SMB loopback share `\\localhost\C$`. `FILE_ID_INFO` fails with error 87
  on FAT32 and exFAT; `GetFileInformationByHandle`'s file index and creation time were the same
  before and after the kill on exFAT.
- Robocopy lists every folder at the start: a folder that comes last was created at the
  destination 34-141 ms after start while the first large file was still copying. With `/MT:1`,
  a file created in the destination after that listing was overwritten with the source version
  (robocopy's own race; cleanup does not change it).
- With `/NC`, robocopy prints a destination file that has no source ("extra") exactly like a
  copied file, with the destination path. With `/XX` those lines are gone; exit code unchanged.
- Killed mid-copy, robocopy delivered one more line after the kill, for the first file, which was
  still open and incomplete at the kill (4 of 4 runs). The app's parser drops a held line after a
  kill, so it is never counted complete.

**Defects found and fixed**
- Old build, `Cancel.Tests.ps1` scenario A: all 7 partial copies were left behind. The job's
  `robocopy.log` held a line for `keep.txt`, an unrelated file already in the destination, with
  its destination path; the ledger matched nothing, marked the paths unreliable and skipped
  cleanup. Fixed by `/XX` (07419c0).
- Old build, scenario B (the merge review's open race): a file put in the destination after the
  job's presence check, which robocopy skipped, was **deleted** by the cancel. Fixed in 4ecce52:
  cleanup deletes only files robocopy held open while suspended for the kill.
- exFAT, the fixed build plus the debug line: debug line `candidates=3 … open=3 … toDelete=3 deleted=0`; the three
  partial copies stayed. `BY_HANDLE_FILE_INFORMATION` was read with the wrong packing, so the
  identity included last-write time bits. Fixed in 9d91ccc; the same run then logged `deleted=3`.

**`Cancel.Tests.ps1`** (final; NTFS on C:, cross-volume to the 300 MB disk) — PASS, 26 checks
- A: 8 × 256 MB copy throttled with `/IORATE:8M`, Cancel pressed in the progress window: no
  partial file left, the two pre-existing destination files unchanged. Debug line
  `candidates=7 observed=7 open=7 notOpen=0 … deleted=7 leftInPlace=0`.
- B: robocopy suspended as it started, a file created at the destination, robocopy resumed,
  Cancel: the late arrival is still there with its content; no partial file left. Debug line
  `candidates=9 observed=9 open=8 notOpen=1 … toDelete=8 deleted=8`.
- C: cross-volume Robo-Cut of 6 files (one of 1 MB), Cancel: 1 moved, 5 still in the source with
  their hashes, no partial copy at the destination. Debug line `candidates=5 … open=5 notOpen=1
  … deleted=5`.
- A and B also passed on FAT32 and on exFAT (3 × 12 MB, `/IORATE:1M`), each with `notOpen=1`
  for the late arrival.
- An SMB loopback share as `-Root` could not be canceled in time: the copy finished in 0.2 s
  (also with `/NOOFFLOAD /J`). Cleanup through the app on SMB is **unverified**.

**`CutSafety.Tests.ps1`** (final; `-SecondVolume` on the 300 MB disk, `-SmallVolume` on the
40 MB disk) — PASS, 75 checks
- Skip conflict with same size and time, same volume and cross volume: source and destination
  unchanged, the other file moved.
- A: cross-volume cut with one source held open (`FileShare.None`): it stayed, unchanged.
- C: cross-volume cut into a folder with a deny "create files" entry: that file stayed in the
  source, unchanged; a file in a subfolder moved.
- D: cross-volume cut of a 64 MB file onto the 40 MB disk: it stayed in the source, unchanged;
  no copy of it at the destination; the small file moved. Summary: "1 item could not be moved to
  scratch", "There is not enough space on the disk", toast "Move finished with errors"
  (`10-full-destination-cut-summary.png`, the same cut repeated by hand on the fixed build).
- E: same-volume cut of a folder: the file kept its file ID (fsutil), so it was renamed.
- Conflict dialog through UI Automation, same and cross volume: Replace (destination holds the
  pasted version, source gone), Skip (both unchanged), "Let me decide" with both sides ticked
  (same volume: `conflict (2).txt` holds the pasted version, the original is unchanged; cross
  volume: the dialog keeps the two boxes exclusive and shows "Continue: 1 file skipped", both
  files unchanged; `11-conflict-decide-cross-volume-keep-both-not-offered.png`, by hand on the
  fixed build).
- Cancel in the conflict dialog (by hand, fixed build): the job ended and nothing moved.

**Script fixes:** `Cancel.Tests.ps1` jobs finished before the progress window opened (2 GB in
about 2 s), so copies are now throttled with `/IORATE`; `Resolve-Path` returned
provider-qualified UNC paths (`ProviderPath` now); `Set-Acl` needed a privilege to write the
owner (icacls now).

**Not run in this session:** cleanup's refusal of links and folders and its read-only clearing;
cleanup through the app on an SMB share; a cancel where robocopy cannot be suspended.

**VM housekeeping:** app uninstalled (install and data folders, CLSID keys gone), the four
virtual disks detached and deleted, the scratch folder, scheduled tasks and the listener
removed. The pre-existing `RrcParityExplorer` task was left alone.

## 2026-10-02 · build 26200 (robocopy 10.0.26100.1) · VM · user experience: every e2e script, every surface, 100% and 150%

Same disposable Windows 11 Enterprise evaluation VM, no network egress, test account signed in
to the desktop, screen 1280 x 800. Scripts and the app ran on the desktop through one-shot
scheduled tasks; the UI was driven with injected mouse and keyboard input and read from
screendumps. Display scale was switched live between 100% and 150% with
`DisplayConfigSetDeviceInfo` (the Settings app's method; no sign-out), so open windows also
received a DPI change. Test volumes: 300 MB and 40 MB NTFS virtual disks. Every package's zip
SHA256 was checked on Windows and the installed exe's hash compared with the package. A
debug-output listener recorded the app's path-free debug lines, including a new start-up line
for fonts. Builds (exe SHA256):
- **B1** `21572bb7…` = commits 534302e…6c38214; **B2** `08cff485…` = B1 + the low-integrity fix
  (later 4b8b677); **B3** `795c280a…` = f1689ff; **B4** `87dc09f3…` = 2b037da; **B5**
  `7d0e91b8…` = 09bbb16; **B6** `95ce781d…` = f647d68;
  **final** `1021f67045e6c0879e0a1a2ff42d9bb57766a8dfad862142e16e165191497069` = 4cda9de
  (zip `fbaa8e7e…`). The exe embeds the commit, so B4…final differ also where sources did not.
- Five earlier builds of this session were used only to find defects (screenshots not kept).

**`Run-All.ps1`** (`-SecondVolume` 300 MB disk, `-SmallVolume` 40 MB disk)
- Before this session's changes (exe of commit 02e7432): Install, CutSafety, Cancel PASS; Verbs,
  Ephemeral, Security, Uninstall FAIL, each traced below.
- B1: Install, Verbs, CutSafety, Cancel, Uninstall PASS. Ephemeral FAIL (Windows noise, below).
  Security FAIL: the low-integrity copy exited `-532462766` (0xE0434352). Repeated by hand with
  stderr captured: `UnauthorizedAccessException: Access to the path
  'C:\Users\<user>\AppData\Local\Temp\<random>' is denied` in
  `ThemingScope.CreateActivationContext` ← `Application.EnableVisualStyles` ←
  `ApplicationConfiguration.Initialize`. Fixed in 4b8b677.
- B2: Security PASS: control exit 0, low-integrity copy exit 1 (refused).
- B3: **all seven PASS** (145 checks), with `-AllowPath` for the noise judged below.
- B6: six PASS; Ephemeral FAIL: "could not search for the marker" for an ActionCenterCache PNG
  Explorer had already deleted. Script fixed in 4cda9de.
- **final**: Install 6, Verbs 18, CutSafety 75, Cancel 26, Security 2, Uninstall 7 checks, PASS.
  Ephemeral listed only `Packages\Microsoft.MicrosoftOfficeHub_8wekyb3d8bbwe\Settings\settings.dat*`
  (another app's settings; no marker in them). Install, Ephemeral (with that path judged) and
  Uninstall then ran again on the final exe: **PASS**, 10 Ephemeral checks.
- Noise judged unrelated in Ephemeral runs, each searched for the marker and none containing
  it: `Microsoft\Windows\WebCache\*`, `Microsoft\Windows\UsrClass.dat.LOG2`,
  `…\EBWebView\…` (Windows client web view cache), PowerShell's own
  `StartupProfileData-NonInteractive`, the Office Hub settings above, and
  `%TEMP%\{GUID}.png`: caught with a file watcher, it is Windows' stock blue "i" balloon icon
  (306 x 306), written while an Info toast shows. Explorer's ActionCenterCache images are
  searched like the notification database.

**Script defects fixed** (bc07ff5, 4cda9de): hidden files need `Get-Item -Force` to list
streams (Verbs); `.Count` on an empty pipeline under strict mode (Uninstall); icacls needs
WRITE_OWNER to lower a label, which a `-Root` under an admin-created `C:\` folder does not give
(Security; the script now grants the user full control of its own work folder); the toast
images above (Ephemeral); `-AllowPath` through `-File` arrives as one string.

**Fonts.** Before 534302e every window drew Arial: the Plex faces were only in a GDI+
PrivateFontCollection, which GDI (TextRenderer, standard controls) never sees, and the Medium and
SemiBold cuts were requested by names Windows does not use. After it the debug line read
"6 of 6 Plex cuts resolved by GDI" in 39 of 55 starts and "5 of 6", always without
`IBM Plex Sans SmBld`, in 16; headings then fell back to Segoe UI Semibold, which is what shots
15 and 18 show for their headings (that tray started with 5 of 6). With c3c8a05 (fonts
added per cut, GDI first, a missing family added again): 10 of 10 starts "6 of 6", no retry
needed, so the retry path itself is **unverified**. Plex Sans, Sans SemiBold and Mono rendering
were checked by eye on zoomed screendumps (shapes of `a`, `g`, `0`, Mono advance).

**Surfaces** (`docs/evidence/2026-10-02/`, build in brackets)
- First-run offer, now a Gridline dialog: version, install folder, settings file, how to remove,
  "Start with Windows" box; Install default. `12-install-offer-gridline.png` (B3). Install
  result `13-installed-notice.png` (B3); uninstall result `30-uninstalled-notice.png` (B6),
  then install, data and config folders gone.
- Progress window running, sized to its lines: `14-progress-window-running.png` (B3). Queued
  ("Waiting for another paste into this folder"), latched pause, paused (amber bar) and the
  ephemeral caution strip seen on earlier builds of this session.
- Conflict dialog: three equal choices, Skip focused, worded for 1 or N files
  `15-conflict-choices.png` (B4); per-file list with SOURCE/DESTINATION readable, mixed
  Replace / keep both / skip, summary "1 file replaced, 1 file pasted under a new name, 3 files
  skipped", full note in a tooltip `16-conflict-decide-replace-keepboth-skip.png` (B4). On disk
  part-1.bin was being rewritten and part-2 to part-4 were unchanged; the keep-both copy was not
  checked by hand (CutSafety checks keep-both). A tray left-click with a
  question waiting brought the dialog to the front.
- Jobs window with five jobs (awaiting decision, queued, paused, two running), Gridline header,
  every column visible at the default size, focus kept on the list after Pause:
  `17-jobs-window-several-jobs.png` (B5).
- Error summary from a locked source (error 32), full message on its own line, coalesced toast
  "2 pastes finished, 1 with errors" seen on an earlier build; `18-error-summary-try-again-and-toast.png` (B4).
  Try again after the lock was released: a 1-file job, Done, toast "Copy finished, 1 item
  (48.0 MB) to Backup E" `21-try-again-done-and-toast.png` (B4).
- Settings with an invalid value: red line under the field, Save disabled, "Fix Retries per failed
  file (marked in red) to save." beside it `20-settings-validation.png` (B4).
- Tray menu: no shadow, Resume all disabled when nothing is paused (B4, B6), Pause all
  checked `19-tray-menu-pause-all-on-old-check-glyph.png` (B4, the system check glyph fixed in
  c0d9514); Gridline check box and "Open logs" hidden in ephemeral mode
  `28-150pct-tray-menu-ephemeral-checked.png` (B6).
- Ephemeral on from the tray: Gridline question (Keep default), full-width notice, EPHEMERAL
  status cell, toast "applies to new jobs" `22-ephemeral-on-confirm-notice-toast.png` (B4);
  ephemeral job toast "Job finished / Open Jobs from the tray icon for details."
  `23-toast-ephemeral.png` (B4).
- Tray icons idle, running, paused (all jobs paused), attention, ephemeral idle, ephemeral
  running `24-tray-icons-idle-running-paused-attention-ephemeral.png` (B4).
- 150%: open windows rescaled live; Jobs window kept on screen; conflict dialog
  `25-150pct-jobs-and-conflict.png` (B5); "Let me decide" opened after the switch
  `26-150pct-decide-after-dpi-change.png` (B6; on B5 its summary line was twice the size,
  fixed in f647d68); question dialog `27-150pct-confirm-dialog.png` (B6); Settings with Save
  on screen `29-150pct-settings-save-on-screen.png` (B6; on an earlier build of this session its Save
  button was below the taskbar).
- Gridline rules, checked on these shots: square corners on every app-drawn control; System Gray
  window surfaces; white panes and fields with closed frames; blue title strips (cyan on a running
  progress pane); cyan running, amber paused or deciding, red errors, green done; no gradients,
  no emoji; no app-drawn shadows (the native window frame's shadow is Windows').

**Product defects found and fixed this session:** Arial instead of Plex and wrong weights
(534302e); pane padding ignored, white behind check boxes, open field frames, menu shadow,
windows taller than the screen at 150% (ee49b89); TaskDialog and message boxes (cf03d93,
f1689ff); conflict wording for one file, file name upper-cased in a title, "kept both", latched
pause text, Resume all always enabled (288414c, 2b037da); native Jobs header, hidden Status
column, stale ephemeral notice, uneven conflict choices, cut-off check-column captions, dates,
sizes and error messages, fixed-height progress window, unexplained disabled Save (6c38214);
low-integrity crash (4b8b677); focus jumping to Cancel after Pause (1d3a674); system check
glyph (c0d9514); truncated Speed and Errors (09bbb16); doubled font after a DPI change
(f647d68); intermittent missing SemiBold (c3c8a05).

**Not run or still open:** 125%, 175% and higher scales and a second monitor; Explorer's own
right-click path for these surfaces (verbs came from the CLI, which uses the same COM call);
whether a conflict dialog takes the foreground when Explorer starts the paste; the four tray
error notices of f1689ff (their error paths were not provoked); keyboard-only use of the
conflict list; screen readers. The running tray draws its icon at runtime; the generated
`tray-*.ico` files are not used (docs/gridline.md).

**VM housekeeping:** app uninstalled (folders and keys gone, checked by Uninstall.Tests), both
virtual disks detached, the scratch folder, this session's scheduled tasks, the debug listener
and the tray icon's "show on taskbar" setting removed, scale back at 100%. The pre-existing
`RrcParityExplorer` task was left alone.

## 2026-10-02 · build 26200 (robocopy 10.0.26100.1) · VM · security and ephemeral audit

Same disposable Windows 11 Enterprise evaluation VM (build 26200), no network egress, test
account signed in to the desktop. Scripts and the app ran on the desktop through one-shot
scheduled tasks; a second, non-interactive logon session of the same account (a service-style
session whose token carries the NETWORK SID) was used for the remote-client probe. Test volumes:
a 300 MB and a 40 MB NTFS virtual disk. Builds (exe SHA256):
- **before** the fixes below: the package of commit e24d6ec (zip `30bb4917...`);
- **final** `8AA20C0EEF7F844767F64FFCB52F82B0F081228670E02FBC91984812F21B0DF7` (zip `8C97D881...`), built
  from the sources of commit fb39d77 (the exe embeds the commit, so a rebuild from a later commit, which changes scripts and docs only,
  hashes differently). Every deploy
  was checked: the installed exe's hash against the package's.
- Sabotage builds (temporary edits, reverted, not kept) are listed under "Rules broken once".

### Run-All on the final exe

`Run-All.ps1 -SecondVolume <300 MB disk> -SmallVolume <40 MB disk>`, all eight PASS: Install,
Verbs, CutSafety, Cancel, Ephemeral (39 checks, 119 s), Security (395 checks, 848 s), Uninstall,
Footprint (32 checks, 771 s). Two lines in the Ephemeral output say `unverified` (below).

### Findings, fixed

- **A file named like a robocopy switch changed the run (eaf0b80).** Measured with robocopy by
  hand: a file called `-E` passed as a quoted file filter switched on `/E` (subfolder files were
  listed); `"-E "` and the other spellings tried were refused or ignored. Robocopy reads `-` like
  `/`. A file called `-MOV` or `-S` in a selection would make a copy a move or recurse. (A
  `-PURGE` in the list did not delete an unrelated file in the destination: with file filters
  purge only considers matching names.) The planner now refuses such a loose file with a
  reason and runs the rest; `RobocopyArgs.Build` throws as the last gate (the conflict split and
  the retry path rebuild name lists from files inside folders). On the **old** build,
  `Security.Tests.ps1` section 6 failed ("robocopy was given ok.txt and neither switch-like
  name"); on the final build it passes: robocopy gets `ok.txt` only, only `ok.txt` arrives, no
  subfolder, every source stays, also for a cut across drives (the switch-like files and the
  subfolder stay). `31-switch-like-names-refused.png`: the job's Refused list with the reason.
  Core tests added for the planner, `Build` and the name predicate; breaking the predicate fails
  6 of them, breaking the planner check fails 3.
- **Refused items made the tray crawl (fb39d77).** A clipboard of 40,000 paths that were all
  refused cost the tray 31 s of CPU (15 s for 20,000) and it stopped answering
  (`Process.Responding` false for the 24 s sampled): the summary list built one owner-drawn row
  per item, about 0.75 ms each, so the 250,000-path limit means minutes. After capping the
  Refused, May-be-incomplete and Skipped lists at 1,000 rows with a count of the rest, 40,000
  paths cost 2.5 s of CPU and the tray stayed responsive; 100,000 paths took 22 s end to end
  in the test, the tray's working set 145 MB to 103 MB. (Without the progress window the same
  list cost 0.5 s, which located the cost in the window.)

### Pipe: squatting, impersonation, remote clients, PID check (Security section 3)

Robocopy is held at its start (suspended by `Start-RobocopyCatcher`), so the pipe exists and
robocopy has not connected. Command line read from the process:
`/UNILOG:\\.\pipe\RoboRightClick-<job id, 32 hex>-<step>-<16 hex nonce>`.
- DACL read back through a READ_CONTROL handle: `D:(D;;0x1f019f;;;NU)(A;;0x12019f;;;<user SID>)`:
  two entries, NETWORK denied, only the user allowed.
- A second server on the live name: refused (`Access to the path is denied` with
  maxInstances 1, `All pipe instances are busy` with unlimited).
- A same-user client connects (the DACL lets the user in) and is disconnected at once (`Pipe is
  broken` on its first write): the PID check. What it sent never reaches any `robocopy.log`
  (normal mode), the real robocopy then connects and the copy of the 48 MB file is intact, and a
  client that tries while robocopy holds the pipe is refused.
- Remote clients: from the other logon session (token with the NETWORK SID, same account) the
  live pipe answers `UnauthorizedAccessException` over `.` and over `127.0.0.1`. A control pipe
  with the same DACL minus the NETWORK deny let the same client in. From the interactive
  session an SMB loopback open (`localhost`, `127.0.0.1`, the machine name) **connected** to a
  pipe that denies NETWORK, so loopback from the desktop is not a network logon here and was not
  used as evidence. A client on another machine was not tried (no second machine).

### COM (Security section 2)

A bare COM client (compiled by the script): at medium integrity `CoCreateInstance` and
`QueryInterface(IExecuteCommand)` return 0; at low integrity `CoCreateInstance` returns
`0x80070005`. With both AppID values (`LaunchPermission`, `AccessPermission`) deleted, low is
still `0x80070005` and medium still served; the values were restored byte for byte (checked).
Not shown: which of the machine's default launch permission and the tray's own
`CoInitializeSecurity` refuses with the values gone; a caller running as another user (no
second account was created).

### extraArgs, clipboard, names (Security sections 4 to 6)

- 21 hostile `extraArgs.copy` values (switches outside the allow-list, quoted and dashed forms,
  `&`, `^`, a quote after an allowed value, a tab and a newline as separator, a 14-digit size, a
  positional path, `/JOB:`, `/LOG:` and `/UNILOG+:` into the test folder, 1,200 characters): the
  robocopy command line ended with the fixed `/XC /XN /XO` every time, no log file appeared, the
  unrelated destination file was untouched, the source stayed, only the selected file was
  copied. Control: `/IORATE:8M /J` is appended.
- Hostile clipboard, each pasted with a Move effect over a canary file an accepted path would
  have moved: 25 path forms (`\\?\GLOBALROOT\Device\...`, `\\?\C:\...`, `\\.\C:\...`, `\??\`,
  `\\?\UNC\localhost\C$\...`, `..` and `.` segments, doubled and forward slashes, drive-relative
  and relative paths, an alternate stream, `::$DATA`, trailing dot and space, `*` and `?`, a quote
  injection, `<>|`, `CON`, a control character, a drive root, `\\server`, `\\.\pipe\x`), the
  narrow (ANSI) form, 7 malformed blocks (shorter than the header, `pFiles` beyond the block, at
  its end and inside the header, a name with no terminator, header only, cut inside the
  terminator), 250,001 paths, a 69 MB block, 100,000 missing paths, and two device-path
  destinations. Each: no robocopy started, nothing reached the destination, the canary unchanged,
  the same tray process, no crash log entry. Controls: the plain canary path copied; a list of
  one valid and one device path copied only the valid one.
- During the first run an odd-length block was accepted: that was a script mistake (a valid
  path followed by one stray byte after the list terminator, which is ignored by design), not
  a defect; the case now cuts the block inside the terminator.

### Ephemeral audit (`Ephemeral.Tests.ps1`)

Five ephemeral jobs with progress window and toasts on: a copy, a cut (a rename; a robocopy
`/MOV` run when `-SecondVolume` is given, as in the Run-All above), a cancel of a throttled
4 x 48 MB folder, a conflict answered with Skip, a copy of a locked file (error summary
`32-ephemeral-failure-summary-locked-file.png`: "Ephemeral: nothing about this job is written
to disk"). Snapshots before and after of `%APPDATA%` (Recent, jump lists), `%LOCALAPPDATA%`
(WER, CrashDumps, Notifications), `%TEMP%` and `ProgramData\Microsoft\Windows\WER` (names, sizes,
times, hashes): no new, changed or removed file other than `config.json`, the notification
database and toast images, and Windows' own background files; no file name or content with the
job marker or the test folder's path; the marker not in an `reg export HKCU` text and not in the
Application and System event logs; no crash log. The clipboard after the ephemeral Robo-Copy
holds the three formats `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`
and `CanUploadToCloudClipboard`; after a normal-mode copy none of them. The Windows noise is now
a list inside the script (web cache database, token broker cache, class hive logs, WebView cache,
an Office hub settings file): it may change, its content is still searched. Two of those files
(`WebCache\V01.log`, `WebCache\WebCacheV01.jfm`) could not be opened for reading and are
reported `unverified`. The Windows clipboard history itself was not queried.

Normal mode (`logRetentionJobs` 3): six jobs; `history.jsonl` held all six (done, no errors);
exactly three job folders remained, the three newest; a folder in `jobs\` not named like a job
survived; `job.json` named verb, source and `done`; `robocopy.log` held robocopy's output (its
first data line starts with a UTF-8 byte order mark that robocopy writes: cosmetic, not changed).

### Footprint (`Footprint.Tests.ps1`, new)

Before, after install, after three jobs, after uninstall: full HKCU export parsed to key and
value lines, the profile, ProgramData, Program Files, Start Menu and Temp listings, scheduled
tasks, services, HKLM Run values, and a search of `HKLM\SOFTWARE` and the services key for the
app's name and GUIDs. Install: 19 to 29 differences, all in keys the design lists (the count
varies with keys that already exist), no Explorer, shell-extension or context-menu-handler key,
no new path outside a RoboRightClick folder (apart from toast images and the Windows noise
above), no task, service or machine-wide Run value. Jobs: no HKCU key outside the design.
Uninstall: HKCU back to the "before" text apart from Windows noise, no path named
RoboRightClick, install, config and data folders gone, tasks and services as before. Two
records are Windows', not the app's: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\UFH\ARP`
holds a copy of the per-user Uninstall entry while installed (0 matches after uninstall), and
`HKCU\...\RunNotification` gets a `StartupTNoti<app name>` DWORD when the Run value is first
added, which stays after uninstall (the app neither writes nor removes it; recorded, not changed).

### Rules broken once, to see the test fail

Each edit was made, built, deployed (hash checked) and the covering script section run, then
reverted: the client PID comparison forced true: Security 3 failed at "disconnected by the PID
check"; the extraArgs check always empty: Security 4 failed at the first hostile value; `?` allowed in
paths: Security 5 failed at the GLOBALROOT case ("no robocopy was started"); the ephemeral
clipboard formats not written: Ephemeral failed at job 1; ephemeral mode composing a file sink:
Ephemeral listed the new job files and the marker in `job.json` and `robocopy.log`; the Run value
left out of uninstall: Footprint failed ("Registry entries survived uninstall"); the switch-like
name rule: above. Two of these first failed for the wrong reason and the script was changed:
the PID test (the DACL probe used up the pipe's only instance, so the intruder could not
connect; the probe now runs last and the connect is asserted) and one deploy that did not
replace the exe, caught by the hash check and repeated.

### Script defects fixed

`Security.Tests.ps1` was two checks and is now six sections; `Ephemeral.Tests.ps1` ran five
copies and now five different jobs plus the normal-mode phase; error summaries have a button
named `SkipErrors`, not `Skip`; `job.json` nests its fields under `job`; a registry-diff
classifier counted every key below a shared parent as the app's (only the parent itself and the
design's own keys count); `foreach` cannot be piped.

### Not verified, or still open

A caller running as another user; which layer refuses a low-integrity caller once the AppID values
are gone; a pipe client on another machine; Windows clipboard history queried through its API;
no WER report after an unhandled crash during an ephemeral job (no way was found to raise one);
a hostile selection arriving from Explorer's own menu (the CLI uses the same COM call);
`install --quiet` returning 0 without replacing the exe, seen once in the earlier session and
once here (after a tray restart a few seconds earlier), not reproduced in an attempt to trigger it
and its cause unknown (the deploy check catches it); 125% and 175% display scales.

**VM housekeeping:** app uninstalled (folders and keys gone, checked by Uninstall.Tests and
Footprint.Tests), both virtual disks detached and deleted, the scratch folder, all scheduled
tasks of this session removed. The pre-existing `RrcParityExplorer` task was left alone. The
Windows-written `RunNotification` value above remains.
