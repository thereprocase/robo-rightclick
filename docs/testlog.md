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

## 2026-10-03 · build 26200 (robocopy 10.0.26100.1) · VM · frozen 1.0.0-beta.1 gate: install, update, Run-All, threads, icons, hotkey, foreground

Same disposable Windows 11 Enterprise evaluation VM (build 26200), no network egress, test
account signed in to the desktop; the desktop work ran through one-shot scheduled tasks and
injected mouse and keyboard input. Test volumes for this entry: a 3 GB and a 48 MB NTFS virtual
disk. Builds (exe SHA256, first 8 hex digits unless stated):

- **frozen**: commit b9cd02d, zip `c8b61abab7bf6bb9930d28980019568ec4c93b43ece0ad0e795e4ebc9fb7b7bc`
  (checked with `Get-FileHash` on the VM before anything ran), exe `d11d3ad8`, version 1.0.0-beta.1.
  Every result below is for this exe unless it names another.
- **draft**: the draft release's test build (asset zip `8147aa9b`, exe `3dd7d70e`), built from
  commit b41a554, also labelled 1.0.0-beta.1.
- **previous-b0**: commit b41a554 rebuilt with its version set to 1.0.0-beta.0 (exe `b5bf8ffc`);
  **frozen-b0**: commit b9cd02d rebuilt the same way (exe `0a4efe41`), the older build with the
  downgrade check for `Update.Tests.ps1 -OlderExe`.
- **fix**: scratch builds of the fixes listed below, the uncommitted tree at the time, version
  1.0.0-beta.2: `dbecdb80` (hotkey, notice, version, scripts) and `ce3389ba` (plus the progress
  window). They are not release builds; the gate re-freezes from main.
- An instrumented build of b9cd02d (exe `982597c6`, version 1.0.0-beta.9, file logging in the
  hotkey and locator code) was used only to find the hotkey defect and is not kept.

### Download simulation (Mark of the Web)

The zip was copied to Downloads with a `Zone.Identifier` stream (ZoneId=3, github.com referrer).
- `Expand-Archive` (Windows PowerShell 5.1) does **not** carry the mark: none of the extracted
  files had a `Zone.Identifier` stream. Double-clicking that exe showed no SmartScreen prompt.
- To match Explorer's own extraction, a second copy had the stream added to every file.
  Double-clicking that exe showed "SmartScreen can't be reached right now", Unknown Publisher,
  Run / Don't Run (`01-smartscreen-motw-exe.png`; the VM is offline, so this is the offline
  variant). After Run, the install offer appeared about 25 s after the double-click (about 12 s
  without the mark). The same exe started with `cmd /c start` from a scheduled task showed no prompt.
- Whether the installed exe keeps the mark depends on the path: a **fresh install** from the
  marked exe left `Zone.Identifier` on `%LOCALAPPDATA%\Programs\RoboRightClick\RoboRightClick.exe`
  (`File.Copy` copies the stream); an **update or repair** keeps whatever the installed file had
  (`File.Replace` keeps the replaced file's streams): over an unmarked install the result was
  unmarked, over a marked one it stayed marked.
- With the marked exe installed: a COM cold start (tray stopped, right-click, Robo-Copy) started
  the tray with no prompt and the verb ran; sign-out and sign-in started the tray from the Run
  value with no prompt (twice, at 150% and 175%). Neither path showed a SmartScreen or
  "Open File - Security Warning" dialog.

### Installer and updater (step 0)

Clean profile first (no install folder, no keys, no Run value).
- **draft installed with `--install --quiet`**, then config.json edited (retries 2, retry wait 7,
  log retention 40, notify off). Double-clicking the frozen exe offered **"RoboRightClick
  1.0.0-beta.1 is installed. Repair it?"**, not an update (`02-repair-offer-same-version-as-draft.png`):
  both builds carry the same version. Repair replaced the exe (hash `d11d3ad8`), wrote the three
  menu icons and the Icon values, kept config.json byte for byte, restarted the tray. **Defect**
  (fixed, d8c6ab1): two different builds under one version; see "Found and fixed".
- **previous-b0 installed**, same config edits. Double-click of the frozen exe: **"Update
  RoboRightClick 1.0.0-beta.0 → 1.0.0-beta.1?"** (`06-update-offer-beta0-to-beta1.png`). Update:
  exe `d11d3ad8`, DisplayVersion 1.0.0-beta.1, Icon values set, config.json unchanged
  (SHA256 before and after equal), tray restarted. The classic menu in the Explorer window that
  was open during the update showed no icons before (`05-...`) and the icons after
  (`08-classic-menu-after-update-icons.png`), without a sign-out. The result dialog read
  "ROBORIGHTCLICK WAS UPDATED FROM 1.0.0-BETA.0 TO 1.0.0..." with the new version cut off
  (`07-updated-result-heading-truncated.png`). **Defect** (fixed, 0d01f51).
- Double-click again: Repair offer, Repair ran, tray restarted, config unchanged.
- **Downgrade**: double-click of frozen-b0 over the frozen install: "A newer RoboRightClick is
  installed", Open and Close only (`09-downgrade-refused-offer.png`). `frozen-b0 --install
  --quiet`: exit 1, exe and DisplayVersion unchanged. With `--force`: exit 0, beta.0 installed,
  config unchanged; frozen `--install --quiet` updated back. **The draft over the frozen
  install** with `--install --quiet`: exit 0 and the frozen exe was replaced by the draft's,
  with no question: the draft predates the downgrade check and the labels are equal anyway.
  The frozen build's icon files and Icon values stayed (the draft does not know them).
- **Update during a long paste**: 4 x 256 MB throttled with `/IORATE` through `extraArgs`. Frozen
  `--install --quiet` while robocopy ran: exit 1 after 10 s, the same robocopy process kept
  running, the tray kept its PID, all four files arrived with matching hashes. The same through
  the double-click Repair button: "RoboRightClick did not close" with the reasons and the ways out
  (`10-repair-refused-while-paste-runs.png`); the copy went on and finished intact.

### Run-All (step 1), frozen exe, `-OlderExe` frozen-b0, `-SecondVolume`, `-SmallVolume`

First run, scripts as committed in b9cd02d:

```
Test      Result   Seconds
----      ------   -------
Install   PASS          17
Verbs     PASS          34
CutSafety PASS         141
Cancel    PASS          62
Hotkey    FAIL (1)       5
Ephemeral FAIL (1)       6
Security  PASS         856
Uninstall PASS           2
Update    FAIL (1)      20
Footprint FAIL (1)       1
```

Hotkey and Ephemeral: `Exception calling "SetFocus" with "0" argument(s): "Target element cannot
receive focus."` (script). Update: scenarios A to E passed, G stopped at `The property 'Count'
cannot be found on this object` (script). Footprint: "Already installed", because Update stopped
before its uninstall. Second run after the two script fixes:

```
Test      Result   Seconds
----      ------   -------
Install   PASS          12
Verbs     PASS          34
CutSafety PASS         140
Cancel    PASS          61
Hotkey    FAIL (1)      36
Ephemeral FAIL (1)       7
Security  PASS         856
Uninstall PASS           1
Update    PASS          21
Footprint PASS         770
```

Hotkey: `Timed out after 30 s waiting for ...\hotkey\a\rrchotkey....txt to appear` (the app
defect below). Ephemeral: `Could not bring the File Explorer window to the front.` (a late
balloon toast held the foreground; see "Observed, not changed"). Update A logged
`# threads /MT:32 (auto: source unknown, destination unknown, same disk)` for a format-1
config.json with `"threads": 32`; G (tray start blocked by a deny-execute entry) exited 0 with
the newer build in place; F removed the planted `.old` and temp files. Footprint: install 19
app-owned registry differences, 10 judged noise, 0 other; after uninstall HKCU matched "before"
apart from shared parents and noise; Windows' UFH\ARP copy 1 match while installed, 0 after.

With the fixed scripts and the **fix** build: `Hotkey.Tests.ps1` PASS, A to H (16 checks), and
`Ephemeral.Tests.ps1 -SecondVolume` PASS (six ephemeral jobs including the hotkey paste, then
the normal-mode phase); its two `unverified` lines are the web cache files of earlier entries.
The first Ephemeral run on the fix build listed two changed files, Explorer's
`iconcache_32.db` and a OneDrive sync engine log; both were searched for the marker and the test
path and held neither; they now sit in the script's judged-noise list. **The same Hotkey script
against the frozen exe fails A** (`Timed out after 30 s ...`), so the test catches the defect.

### Upgrade in place and the install loop (step 2)

previous-b0 installed with the tray running, config.json edited (retries 3, conflict skip),
frozen `--install --quiet`: exit 0, exe hash equal to the package's, DisplayVersion
1.0.0-beta.1, Icon value `...\robo-copy.ico`, config.json unchanged, no `.old` or `.tmp`, new tray
PID. Then 40 installs with the tray running, alternating frozen-b0 `--force` and frozen so a
skipped replace would show in the hash, the tray killed and restarted 3 s before every fourth:
**40 of 40 exit 0, installed hash as expected, a new tray each time, no leftovers**, config.json
unchanged after all 40, 1.2 to 1.6 s each. Sixteen more installs from freshly copied exes (twelve
started with `Start-Process` or `cmd /c start /wait`, four copied in and moved into place seconds
before, as below): all replaced.

The earlier anomaly (exit 0, exe not replaced) was **seen once more** this session, and not with
the frozen exe: an instrumented exe, copied to the VM and moved into a new folder seconds
before, run with `start /wait <exe> --install --quiet` from a scheduled task, returned 0, and
afterwards the installed exe, DisplayVersion and the **tray process (same PID and start time)**
were all unchanged. The tray was not even asked to exit, so the installer's own content check
(it compares the installed file with the source after the swap) never ran. The Application and
Defender event logs held nothing for that minute. Repeating the same sequence (copy, move, start
at once) four times did not reproduce it. Cause unknown; the next process to look at is the
one `start` launched, which nothing recorded. The deploy wrapper's hash check still catches it.

### threads auto (step 3)

Five 512 MB copies (4 x 128 MB, `/IORATE:4M`), Pause and Resume through the progress window's
buttons, robocopy's written bytes from `Win32_Process.WriteTransferCount`:

| Case | `/MT` on robocopy's command line | job log | written while paused (5 s) | written after Resume (4 s) | copy |
|---|---|---|---|---|---|
| auto, C: to C: | 32 | `# threads /MT:32 (auto: source unknown, destination unknown, same disk)` | 0 | 77 MB | intact |
| auto, C: to virtual disk | 32 | `... (auto: source unknown, destination unknown)` | 0 | 80 MB | intact |
| auto, virtual disk to C: | 32 | `... (auto: source unknown, destination unknown)` | 0 | 76 MB | intact |
| `"threads": 4`, C: to virtual disk | 4 | `# threads /MT:4 (fixed in settings)` | 0 | 75 MB | intact |
| `"threads": 8`, C: to virtual disk | 8 | `# threads /MT:8 (fixed in settings)` | 0 | 76 MB | intact |

The VM's system disk is QEMU's emulated SATA disk; Windows itself reports its MediaType as
`Unspecified` (`Get-PhysicalDisk`), and the virtual disks as `Unspecified`, `File Backed
Virtual`. "unknown" is what the design says for both; the solid-state and rotational branches
were **not** exercised here. Progress shown while running, paused and resumed:
`11-progress-paused-mt4.png`, `12-progress-resumed-mt8.png`, `13-progress-running-auto.png`.

### Menu icons and scaling (step 4)

Classic menu (Show more options) on a file and on a folder background: Robo-Copy and Robo-Cut
icons at 100% (`04-...`, `08-...`), 150% (`16-classic-menu-icons-150pct.png`, Robo-Paste on the
background `17-...`) and 175% (`18-classic-menu-icons-175pct.png`), each scale set through
`LogPixels` and a sign-out and sign-in. Magnified 3x to 4x the glyphs have sharp one-pixel
edges at all three scales, no blur. At 175% the progress window (`20-progress-window-175pct.png`),
the conflict dialog (`21-...`) and Settings (`26-settings-hotkey-175pct.png`) laid out without
clipping. Restored to 100% (96) and signed in again at the end.

### Hotkey (step 5b) and access keys

- **Defect (fixed, 0d8697d)**: with the frozen exe, Ctrl+Shift+V in a folder window never
  pasted. A second program holding Ctrl+Shift+V through `RegisterHotKey` received nothing while
  Explorer was in front (the hook took the key) and received the press in Notepad. The
  instrumented build showed every ShellWindows entry unreadable (`entry browser=0 top=0`) and the
  press ending in "Can't tell which folder is open". The same calls from a PowerShell probe:
  `IShellBrowser::GetWindow` returned the tab window (`ShellTabWindowClass`, so the open gate-2
  question is answered: the tab, not the frame) from an STA, and `0x8001010D`
  (`RPC_E_CANTCALLOUT_ININPUTSYNCCALL`) from an MTA. The locator ran in the MTA.
- With the **fix** build: the folder window paste (real keyboard input through the VM, and the
  script's SendKeys), the active tab of a two-tab window (Hotkey E), the address bar and search
  box (D, passed through), the desktop (pasted to Desktop, toast "Copy finished 1 item to
  Desktop"), the desktop rename box (the second program received the press: passed through),
  Notepad (received by the second program), This PC and the Recycle Bin (toast "Can't
  Robo-Paste here", `24-...`, `25-...`), Documents library and a zip (Hotkey F). Turning the
  hotkey off in Settings saved `"pasteHotkey": ""`, and the second program then received the
  press in an Explorer item view: the combination is free.
- Typing stayed responsive during a paste: 20 key presses into Explorer's search box while a
  throttled 512 MB copy ran all arrived in order.
- Access keys in the classic menu (frozen exe, then fix build): Y put the file on the clipboard
  with effect 1 (copy), U with effect 2 (move), B on a folder background moved it (toast "Move
  finished").
- Once, with the fix build, presses in a This PC window and then in R:\ in the same window
  passed through (the second program received them) after the Jobs window had been opened by a
  click on a toast and closed again; after the next switch of windows the hotkey worked. Not
  reproduced in two attempts with the instrumented build; recorded, cause unknown.

### 50,000 items (step 5c)

50,000 files of 26 bytes in one folder, Ctrl+A, right-click, Show more options. Time from the
classic menu closing to the clipboard holding all 50,000 paths: **1.97 s** (two runs: 1.958 and
1.967 s). The first run's responsiveness probe pinged no window (a mistake in the probe) and is
discarded; in the second run Explorer's window answered `WM_NULL` with one stall of **1.44 s**
and every other reply under 250 ms. Explorer's own Copy from the same menu on the same
selection: 3.03 s to the clipboard, one stall of 2.90 s.

### Foreground (step 5d)

- Right-click Robo-Paste on a folder background (frozen exe, 175%): the conflict dialog came to
  the front with focus on Skip (`21-conflict-dialog-in-front-175pct.png`), but the **progress
  window opened behind the Explorer window**, visible only as a taskbar button
  (`19-progress-window-behind-explorer-175pct.png`). **Defect** (fixed, 66abbad): with the fix
  build the window opens on top, its title bar inactive, Explorer keeping the focus
  (`22-fix-progress-window-on-top-not-activated-175pct.png`).
- A hotkey paste on the desktop that asked a conflict question: once the dialog came to the front,
  once it only flashed its taskbar button (`23-...`, instrumented build without the progress
  fix). The hotkey path has no `AllowSetForegroundWindow` from Explorer; the flashing button is
  what the design says then.

### Crash log (step 5)

No test hook exists, so not triggered. By reading the code: `CrashPolicy` appends to crash.log
only when `CrashLog.MayWrite(logging, ephemeralJobsThisSession)` holds (normal mode and no
ephemeral job in this tray session), and only in the tray; Core tests cover `MayWrite` and
`ModeFromConfig`. **Unverified on Windows.** No crash.log existed at the end of this session's
normal-mode use, and Ephemeral.Tests checked that none was written during its ephemeral jobs.

### Found and fixed on main

- 0d8697d `fix(hotkey)`: the folder locator runs in an STA; every folder-window press was refused.
- 0d01f51 `fix(ui)`: result notices show the whole sentence as the heading (`15-fix-update-result-heading-whole.png`).
- d8c6ab1 `build(release)`: version 1.0.0-beta.2, so the draft's test build is older than the
  next freeze (`14-fix-update-offer-beta1-to-beta2.png`: "Update RoboRightClick 1.0.0-beta.1 → 1.0.0-beta.2?").
- bdda428 `test(e2e)`: the focus, window-selection, tab-navigation, strict-mode and noise fixes above.
- 66abbad `fix(progress)`: the progress window opens on top without taking the focus.
- 0194066 `fix(settings)`: config.json was written with `"Ctrl\u002BShift\u002BV"`; Core test,
  seen failing with the encoder line removed. Not run on Windows.

### Observed, not changed

- Balloon toasts queue while the session is idle and appear late, all at once, when input
  resumes; one such late toast held the foreground and made the second Run-All's Ephemeral fail
  to bring Explorer to the front. Windows' behaviour for tray balloons.
- After uninstall, Windows-written HKCU records naming the install path remain:
  `Control Panel\NotifyIconSettings\<id>` (the tray icon), `Explorer\FeatureUsage\AppSwitched`,
  the known `RunNotification` value, and Program Compatibility Assistant entries for the
  downloaded exes. None was written by the app.

### Not verified, or still open

The solid-state and rotational thread choices; crash.log on Windows; 125%; a second monitor; the
hotkey in an elevated Explorer and with a second keyboard layout; the progress and conflict
windows when the paste starts while another program has the focus; the one-off install no-op
and hotkey pass-through above.

**VM housekeeping:** app uninstalled with `Uninstall.Tests.ps1` (PASS: folders, keys and Run
value gone, shared parents kept, no process), both virtual disks detached and deleted, the
scratch folders and downloads of this session and every scheduled task of this session removed,
display scale back to 100%. The pre-existing `RrcParityExplorer` task was left alone.

## 2026-10-03 · build 26200 (robocopy 10.0.26100.1) · VM · second gate: frozen 1.0.0-beta.2

Same disposable Windows 11 Enterprise evaluation VM, no network egress, test account signed in,
screen 1280 x 800; desktop work through one-shot scheduled tasks, injected mouse and keyboard
input and screendumps. Test volumes: a 3 GB (R:) and a 48 MB (S:) NTFS virtual disk. Evidence:
`docs/evidence/2026-10-03/g2-*.png` (account paths in the install dialogs painted over). Builds
(exe SHA256, first 8 hex digits):

- **frozen**: commit 3839e89, zip `be04726687fa5062518fd3e7318bff20e726d138db8d6f4307e22c8eabded082`,
  checked with `Get-FileHash` on the VM before anything ran; a publish of the same commit from a
  fresh clone on the Linux builder produced the same zip hash. Exe `a54bc5e7`, version
  1.0.0-beta.2. Every result below is for this exe unless it names another.
- **draft**: the draft release's asset (zip `8147aa9b`, exe `3dd7d70e`, commit b41a554,
  labelled 1.0.0-beta.1), downloaded with `gh release download`.
- **older**: the first gate's frozen build (commit b9cd02d, zip `c8b61aba`, exe `d11d3ad8`,
  1.0.0-beta.1), which has the downgrade check: `-OlderExe` for Update.Tests and the downgrade
  attempts.

### Download simulation (Mark of the Web)

- The frozen zip was copied to Downloads with a `Zone.Identifier` stream (ZoneId=3, github.com
  referrer and host URL). `Expand-Archive` again carried the mark to none of the four files.
  Extracting the same zip through the shell's zip folder (`Shell.Application` `CopyHere` from
  the zip's namespace, the code path of Explorer's Extract All) put `Zone.Identifier` on all four.
- Double-click of the marked exe (175%, empty profile): "SmartScreen can't be reached right
  now", Unknown Publisher, Run / Don't Run (`g2-27-...`, the offline variant). The first Enter
  on Run did nothing visible; a click on Run then Space started the exe and the install offer
  appeared (`g2-28-...`). Install: the result notice and the tray's first-run toast appeared.
- This time the **installed exe carried no `Zone.Identifier`** (only `:$DATA`), nor did the
  three icons. The first gate entry saw a fresh install from a marked exe keep the mark. The
  source exe was not re-read after Run, so whether choosing Run removed the source's mark is
  unknown. Unresolved; both outcomes are recorded.
- With that (unmarked) install: COM cold start (tray killed, classic menu, Y on a file) started
  `RoboRightClick.exe -Embedding` with no prompt; sign-out and sign-in started the tray from the
  Run value 5 s after Explorer with no prompt. A marked installed exe was not available to repeat
  the first entry's check.

### Installer and updater (step 0)

Clean profile first (no keys, folders, Run value or process, checked value by value).
- **draft installed** with `--install --quiet` (DisplayVersion 1.0.0-beta.1, no Icon values,
  classic menu without icons: `g2-01-...`). config.json edited: retries 2, retry wait 7, log
  retention 40, notify off (SHA256 `ca4d7d4a...`).
- **Double-click of the frozen exe** in `Downloads\RoboRightClick-1.0.0-beta.2-win-x64`:
  **"Update RoboRightClick 1.0.0-beta.1 → 1.0.0-beta.2?"** (`g2-02-...`). Update: "RoboRightClick
  was updated from 1.0.0-beta.1 to 1.0.0-beta.2." with the whole sentence visible (`g2-03-...`;
  the 0d01f51 fix). Exe `a54bc5e7`, DisplayVersion 1.0.0-beta.2, Icon values on all five verb
  keys, three `.ico` files, config.json hash unchanged, new tray process. The Explorer window that
  was open throughout showed Robo-Copy and Robo-Cut icons in the classic menu right after the
  update, without a sign-out (`g2-04-...`).
- **Double-click again**: "RoboRightClick 1.0.0-beta.2 is installed. Repair it?" (`g2-05-...`);
  Repair ran (`g2-06-...`), new tray PID, exe and config.json unchanged.
- **Downgrade**: double-click of the older exe: "A newer RoboRightClick is installed", Open and
  Close only (`g2-07-...`). `older --install --quiet`: exit 1, nothing changed.
  `--force`: exit 0, `d11d3ad8` installed, config unchanged; frozen `--install --quiet` updated
  back. The draft over the frozen install with `--install --quiet`: exit 0, exe replaced by the
  draft's, no question (the draft predates the downgrade check); the frozen build's icons and
  Icon values stayed. Frozen `--install --quiet` updated back.
- **Update during a long paste**: 4 x 128 MB with `/IORATE:1M` in `extraArgs`. Frozen
  `--install --quiet` while robocopy ran: exit 1 after about 10 s, the same robocopy PID kept
  writing, the tray kept its PID. Double-click Repair while the next run copied: "RoboRightClick
  did not close" with the reasons and the ways out (`g2-08-...`), robocopy and tray unchanged;
  all four files arrived with the source hashes, job.json `done`, 4 files, 536,870,912 bytes.
  (A first attempt at `/IORATE:4M` finished before the double-click; /IORATE is per thread.)

### Run-All (step 1), frozen exe, `-OlderExe` older, `-SecondVolume R:\scratch`, `-SmallVolume S:\scratch`

Scripts as committed in 3839e89:

```
Test      Result   Seconds
----      ------   -------
Install   PASS           7
Verbs     PASS          34
CutSafety PASS         140
Cancel    PASS          61
Hotkey    PASS         156
Ephemeral FAIL (1)     104
Security  PASS         860
Uninstall PASS           2
Update    PASS          22
Footprint PASS         776
```

Ephemeral: all six ephemeral jobs ran (the hotkey paste included), then `'C:\Users\<account>\AppData\Local\Temp\rrc-e2e'
in content: ...\Notifications\wpndatabase.db-wal`. Searched by hand: every occurrence of that
path in `wpndatabase.db` (2) and its write-ahead log (18) was one of two **normal-mode** toasts
from the Security script that ran in an earlier session: "Settings problem / config.json:
'extraArgs.copy' ignored: '/LOG:C:\...\rrc-e2e\security\extra\injected.log' is not an allowed
extra switch." Windows copies old database pages into the log when a new toast arrives, so the
log "changed" and held the path although no ephemeral job wrote it. The run's marker was in
neither file. Two findings from that:
- **Defect** (fixed, 3d160de): the settings-problem toast quotes the refused extraArgs token,
  path included, and Windows keeps toast text. It was meant to be path-free in every mode.
- **Script defect** (fixed, 2fc2253): Ephemeral.Tests matched toasts kept from earlier runs.
  With the fixed script (destinations of jobs 1 to 5 now carry the marker; notification files
  that held the test path before the jobs are searched for the marker only), against the
  frozen exe, `Ephemeral.Tests.ps1 -SecondVolume`: **PASS** (ephemeral phase: no new, changed
  or removed file, no marker in HKCU or the event logs, no crash log; normal phase: logs,
  history and retention as designed). Its "unverified" lines: the two notification files
  searched for the marker only, and the two web cache files it cannot open. The first two
  attempts stopped before any job at "Could not bring the File Explorer window to the front"
  while queued balloon toasts held the foreground; dismissing them cleared it.

Hotkey: A to H passed (16 checks). Update: A to G passed; A logged `# threads /MT:32 (auto:
source unknown, destination unknown, same disk)` for a format-1 `"threads": 32`. Footprint:
install 19 app-owned registry differences, 12 judged noise, 0 other; after uninstall HKCU
matched "before" apart from shared parents and noise; Windows' UFH\ARP copy 1 match while
installed, 0 after.

### Upgrade in place and the install loop (step 2)

- draft installed, tray running, config.json edited (retries 3, retry wait 5, conflict skip,
  retention 25, notify off). Frozen `--install --quiet`: exit 0, exe `a54bc5e7` = package,
  DisplayVersion 1.0.0-beta.2, Icon values `...\robo-copy.ico`, `...\robo-cut.ico`,
  `...\robo-paste.ico` (x3), config.json byte for byte unchanged, folder holds only the exe and
  the three icons, new tray PID.
- **20 installs** with the tray running, alternating older `--force` and frozen so that a skipped
  replace shows in the hash: **20 of 20 exit 0, installed hash as expected, DisplayVersion as
  expected, a new tray each time, no `.old` or `.tmp`**, 1.2 to 1.5 s each, config.json
  unchanged after all 20. Then 8 more from exes copied to the VM into a new folder seconds before
  (`start /wait` from a scheduled task): 8 of 8 replaced.
- **The earlier anomaly** (exit 0, exe not replaced, the tray not even asked to exit) is LIKELY
  a harness artifact, not the installer. The machine-local wrapper runs each command through a
  scheduled task with a fixed name, rewriting the task's `.cmd` each time. A task whose previous
  instance is still running ignores `schtasks /run`, and that instance's `cmd.exe` goes on
  reading the rewritten `.cmd` at its old position. Reproduced deliberately: a task still
  running a 20 s command, then the same task name given `start /wait <older exe> --install
  --quiet --force`: the install line never ran (`'--install' is not recognized`, exit 9009) and
  the installed exe, DisplayVersion and tray were unchanged. With a different split of the file
  the stale instance reaches the wrapper's own `echo %ERRORLEVEL%` line and writes 0, which is
  the anomaly as recorded. The wrapper now refuses a task name that is still running.

### threads auto (step 3)

Four 128 MB files, `/IORATE:4M`, Pause and Resume through the progress window's buttons (UI
Automation), robocopy's written bytes from `Win32_Process.WriteTransferCount`:

| Case | `/MT` on robocopy's command line | job log | written while paused (8 s) | after Resume (4 s) | copy |
|---|---|---|---|---|---|
| auto, C: to C: | 32 | `# threads /MT:32 (auto: source unknown, destination unknown, same disk)` | 0 | 96 MB | hashes match |
| auto, C: to virtual disk | 32 | `# threads /MT:32 (auto: source unknown, destination unknown)` | 0 | 93 MB | hashes match |
| auto, virtual disk to C: | 32 | `# threads /MT:32 (auto: source unknown, destination unknown)` | 0 | 96 MB | hashes match |
| `"threads": 4`, C: to virtual disk | 4 | `# threads /MT:4 (fixed in settings)` | 0 | 96 MB | hashes match |
| `"threads": 8`, C: to virtual disk | 8 | `# threads /MT:8 (fixed in settings)` | 0 | 96 MB | hashes match |

`Get-PhysicalDisk` MediaType is `Unspecified` for the QEMU SATA disk and both virtual disks, so
"unknown" is the design's answer; the solid-state and rotational branches were again **not**
exercised. Progress paused and resumed: `g2-09-...` to `g2-12-...` (percent, bytes, files,
Resume/Pause label).

### Menu icons and scaling (step 4)

Classic menu on a file and on a folder background at 100% (`g2-04-...`, `g2-13-...`), 150%
(`g2-23-...`, `g2-24-...`) and 175% (`g2-25-...`), each scale set through `LogPixels` (and
`Win8DpiScaling` 1) and a sign-out and sign-in; magnified 4x (`g2-26-...`) the glyphs have
one-pixel edges at all three, no blur. The SmartScreen prompt and the install offer at 175% (`g2-27`, `g2-28`) laid out without
clipping. Restored to `LogPixels` 96 and
`Win8DpiScaling` 0 (the values found) with a sign-out and sign-in.

### Hotkey (step 5b) and access keys

A second program held Ctrl+Shift+V with `RegisterHotKey` and logged every press it received
with the foreground window's class; a press the app's hook takes never reaches it.
- Ctrl+C on a file in Explorer, then Ctrl+Shift+V in another folder: pasted there ("Copy
  finished, 1 item (12 bytes) to t1", `g2-16-...`); the second program received nothing.
- Two tabs, the second active: pasted into the active tab only (`g2-17-...`).
- Desktop: pasted to `FOLDERID_Desktop` (`g2-20-...`). A second press there with the file
  already present asked the conflict question (in front).
- Passed through to the second program: search box, address bar in edit mode, rename box
  (`g2-18-...`), the desktop's rename box, Notepad.
- This PC and the Recycle Bin: "Can't Robo-Paste here" toast, no job (`g2-21-...`, `g2-30-...`).
- Settings: the Hotkey row named the second program's registration ("Another app also uses
  Ctrl+Shift+V ...", `g2-22-...`). Unticked and saved: config.json `"pasteHotkey": ""`, and the second program then received
  the press in a folder's item list.
- Access keys in the classic menu: Y (Robo-Copy) on a file, U (Robo-Cut) on a folder, B
  (Robo-Paste) on a background: "Move finished, 1 item to t2", the folder moved.
- Typing during a paste: 12 keys into Explorer's search box while a copy throttled with `/IORATE:2M` ran
  all showed (`...bocopytest`, the start scrolled out of view).
- **Seen again, cause unknown**: for about one minute, four presses passed through to the
  second program instead of pasting (or refusing) in two File Explorer windows: This PC (twice,
  the second time with a drive selected, so the item list had the focus) and a folder window
  (twice, each after a click on empty space in the list). Then, without a restart, a press on
  the desktop pasted, and later presses in both windows worked (This PC refused with the
  toast, the folder pasted). A probe in the folder window two minutes later, after another click
  on empty space, found the keyboard focus (`GetGUIThreadInfo`) on the `CabinetWClass` frame
  itself, not the item list; the gate passes then by design. A click on an item moved the focus
  to the list and the next press pasted. That can explain the two presses in the folder window,
  not the press in This PC with a drive selected. The first gate entry saw the same once. A
  hook thread that missed its reinstall, or a hook Windows removed after a callback timeout,
  would look like this; nothing here can tell which. Open.

### 50,000 items (step 5c)

50,000 files of 25 bytes, Ctrl+A, right-click, Show more options, Robo-Copy. A watcher polled for
the classic menu window (`#32768`) and the clipboard sequence number, and pinged Explorer's
window with `WM_NULL` (stalls over 250 ms logged). Menu closed to clipboard holding 50,000
paths: **1.85 s** and **1.78 s** (two runs). Explorer's window: one stall of 1.64 s while its
own menu was being built, and one of **1.46 s** right after Robo-Copy was clicked; no other
reply over 250 ms (second run; the first run's pinger found no window, a probe mistake).

### Foreground (step 5d)

Right-click Robo-Paste from a folder background's classic menu (B), 100%, a conflict on one of
two files: the conflict dialog came to the front with the focus on Skip, and the progress
window opened above the Explorer window too (`g2-14-...`); after Skip the progress window stayed
in front, Explorer keeping the focus (`g2-15-...`). The 66abbad fix holds on the frozen build. A
hotkey paste into a folder with a conflict: the dialog came to the front (`g2-19-...`). Nothing to fix.

Found while doing it: **Defect** (fixed, 6e3ecf0): a copy of one file whose conflict was
answered Skip ran robocopy with nothing to do and toasted "Copy finished, 1 item to t2"
(job.json: 0 files, 0 bytes). The same answer on a cut already ended silently.

### Crash log (step 5)

No test hook exists. Forced a real failure instead: a mutex was created under the name of the
tray's `Local\RoboRightClick.Ready` event, then the tray was started.
- **Normal mode**: the tray showed "RoboRightClick could not start" (`g2-29-...`) and wrote
  `%LOCALAPPDATA%\RoboRightClick\crash.log`, one 576-byte entry: time, version 1.0.0-beta.2,
  Windows 10.0.26200.0, thread UI, `WaitHandleCannotBeOpenedException` with the quoted name
  replaced by `[path]`, and the stack (`SingleInstance.TryAcquire`, `TrayApplication.Run`).
- **Ephemeral mode** (`"logging": "ephemeral"`, crash.log deleted first): the same notice, and
  **no file** in `%LOCALAPPDATA%\RoboRightClick`.
This covers the handled start failure (`CrashPolicy.LogHandled`, config.json deciding the mode);
an unhandled exception in a running tray, and rotation, are still unverified.

### Found and fixed on main

- 3d160de `fix(toast)`: the settings-problem toast no longer quotes a path from config.json.
  Core tests, seen failing with the redaction removed. Not run on Windows.
- 6e3ecf0 `fix(plan)`: a copied file batch whose every file is kept plans no step (no-op, no
  toast). Core tests, seen failing with the check disabled. Not run on Windows.
- 2fc2253 `test(e2e)`: Ephemeral.Tests no longer matches toasts kept from earlier runs. Run once
  on Windows, against the frozen exe: PASS.

### Not verified, or still open

The hotkey pass-through above; the installed exe's mark after a SmartScreen Run; solid-state
and rotational thread choices; crash.log from an unhandled exception and its rotation; 125%; a
second monitor; an elevated Explorer; a second keyboard layout.

**VM housekeeping:** app uninstalled with `Uninstall.Tests.ps1` (PASS: folders, keys and Run
value gone, shared parents kept, no process), and every key, the Run value and the three folders
checked absent by name afterwards; both virtual disks detached and deleted; the scratch folders,
downloads, desktop file and every scheduled task of this session removed; display scale back to
100%. The pre-existing `RrcParityExplorer` task was left alone.

## 2026-10-03 · build 26200 (robocopy 10.0.26100.1) · VM · third gate: frozen 1.0.0-beta.2 (186c79c)

Same disposable Windows 11 Enterprise evaluation VM, no network egress, test account signed in,
screen 1280 x 800; desktop work through one-shot scheduled tasks, injected mouse and keyboard
input and screendumps. Test volumes: a 3 GB (R:) and a 48 MB (S:) NTFS virtual disk. Evidence:
`docs/evidence/2026-10-03/g3-*.png` (account paths in the install dialogs painted over). Builds
(exe SHA256, first 8 hex digits):

- **frozen**: commit 186c79c, zip `aa2a91e165f489766a5bc3dca37d585abe13952fa6e886486b2f395b1ebbc7e1`,
  checked with `Get-FileHash` on the VM before anything ran; `scripts/check-reproducible.sh` on
  the Linux builder (two fresh clones, two umasks) produced the same zip hash. Exe `0076f0bf`,
  version 1.0.0-beta.2. Every result below is for this exe unless it names another.
- **draft**: the draft release's asset (zip `8147aa9b`, exe `3dd7d70e`, commit b41a554,
  labelled 1.0.0-beta.1), downloaded with `gh release download`.
- **older**: the first gate's frozen build (commit b9cd02d, zip `c8b61aba`, exe `d11d3ad8`,
  1.0.0-beta.1), which has the downgrade check: `-OlderExe` for Update.Tests, the downgrade
  attempts and the install loop.

### Download simulation (Mark of the Web)

- The frozen zip was copied to Downloads with a `Zone.Identifier` stream (ZoneId=3, github.com
  referrer and host URL). `Expand-Archive` carried the mark to none of the four files; the
  shell's zip folder (`Shell.Application` `CopyHere`, the code path of Extract All) put it on all
  four. Same as the second gate.
- Empty profile, 100%: double-click of the shell-extracted (marked) exe showed "SmartScreen can't
  be reached right now", Unknown Publisher, Run / Don't Run (`g3-01-...`; the offline variant,
  the VM has no egress). One click on Run; the install offer appeared within 10 s
  (`g3-02-...`). Install: result notice and first-run toast (`g3-03-...`).
- **Choosing Run removed the mark from the source exe**: its streams were read right after the
  click, before Install, and only `:$DATA` was left. The installed exe and the three icons were
  unmarked. This explains the second gate's unmarked install. The first gate's marked install
  is not explained by it; whether that exe went through the prompt's Run is not recorded.
- `Zone.Identifier` (ZoneId=3) was then put on the **installed** exe by hand. COM cold start (tray
  killed, classic menu, Y on a file) started `RoboRightClick.exe -Embedding` with no prompt;
  sign-out and sign-in started the tray from the Run value about 19 s after Explorer, with no
  prompt on screen. A marked installed exe starts silently on both paths.

### Installer and updater (step 0)

Uninstalled first; every key, the Run value, the three folders and the process checked absent by
name.
- **draft installed** with `--install --quiet`: DisplayVersion 1.0.0-beta.1, no Icon values, the
  classic menu without icons or access keys (`g3-04-...`). config.json edited: retries 2, retry
  wait 7, log retention 40, notify off (SHA256 `ca4d7d4a...`).
- **Double-click of the frozen exe** in `Downloads\RoboRightClick-1.0.0-beta.2-win-x64` (the
  `Expand-Archive` copy, unmarked, so no SmartScreen): **"Update RoboRightClick 1.0.0-beta.1 →
  1.0.0-beta.2?"** (`g3-05-...`). Update: "RoboRightClick was updated from 1.0.0-beta.1 to
  1.0.0-beta.2." (`g3-06-...`). Exe `0076f0bf`, DisplayVersion 1.0.0-beta.2, Icon values on all
  five verb keys pointing at `robo-copy.ico`, `robo-cut.ico`, `robo-paste.ico` in the install
  folder, MUIVerb with the access keys, config.json hash unchanged, the old tray gone and a new
  one running. The Explorer window that stayed open showed the Robo-Copy and Robo-Cut icons and
  the Y and U access keys right after the update, without a sign-out (`g3-07-...`).
- **Double-click again**: "RoboRightClick 1.0.0-beta.2 is installed. Repair it?" (`g3-08-...`);
  Repair: "RoboRightClick 1.0.0-beta.2 was repaired." (`g3-09-...`), new tray PID, exe and
  config.json unchanged.
- **Downgrade**: double-click of the older exe: "A newer RoboRightClick is installed", Open and
  Close only, "Nothing was changed" (`g3-10-...`); exe, DisplayVersion and tray PID unchanged.
  `older --install --quiet`: exit 1, nothing changed, same tray. `--force`: exit 0, `d11d3ad8`,
  DisplayVersion 1.0.0-beta.1, config unchanged. Frozen `--install --quiet` updated back.
- **Update during a long paste** (4 x 128 MB of random data):
  - `/IORATE:1M` in `extraArgs.copy`: frozen `--install --quiet` while robocopy ran: **exit 1
    after about 12 s, the same robocopy PID kept running, the tray kept its PID.** The
    double-click that followed was a harness error: the progress window had opened over the
    Explorer window's toolbar and the injected double-click landed on its Cancel button. That
    job ended `canceled` (exit -1) with its partial files removed, the tray unchanged.
  - Repeated with `/IORATE:512K` (2 MB/s): frozen `--install --quiet`: **exit 1 after about
    13 s**, robocopy and tray PIDs unchanged. The progress window was moved aside, then a
    double-click of the frozen exe: the Repair offer, Repair: **"RoboRightClick did not close"**
    with the reasons and the ways out (`g3-11-...`); robocopy and tray unchanged. The job ended
    `done`: 4 files, 536,870,912 bytes, all four hashes equal to the sources. The tray kept one
    PID from before the first paste to after the second.

### Run-All (step 1), frozen exe, `-OlderExe` older, `-SecondVolume R:\scratch`, `-SmallVolume S:\scratch`

Scripts as committed in 186c79c:

```
Test      Result Seconds
----      ------ -------
Install   PASS        17
Verbs     PASS        34
CutSafety PASS       139
Cancel    PASS        62
Hotkey    PASS       146
Ephemeral PASS       148
Security  PASS       853
Uninstall PASS         1
Update    PASS        22
Footprint PASS       770
```

CutSafety G (a cut whose only file conflicts, answered Skip): no summary, the job ended Done,
source and destination unchanged, on one volume and across volumes. Ephemeral's "unverified"
lines: `wpndatabase.db-shm` and `wpndatabase.db-wal` held the test path before the jobs (toasts
from earlier sessions), so they were searched for the marker only; four web cache files and
`UsrClass.dat.LOG2` could not be opened to search. Footprint: use 0 app-owned registry
differences (2 noise); after uninstall 0 app-owned differences remain, the Run value, the three
folders and the process are gone, Windows' UFH\ARP copy holds 0 matches.

### Fixes since the second gate, on Windows

- **3d160de, the settings-problem toast quotes no path.** After Run-All, Windows' notification
  database held no `[path]` text and still held two settings-problem toasts with a raw path
  (`'/LOG:C:\Users\<account>\...\injected.log'`, `'/UNILOG+:...'`), the ones the second gate
  attributed to earlier runs: this run's Security section 4 toasts did not reach the database at
  all. Why is unknown; about twenty toasts from the Security run were still queued on screen
  after Run-All had ended and the app had been uninstalled. Checked directly instead: frozen
  installed, `extraArgs.copy` set to `/LOG:C:\rrc\inj\injected.log`, then to
  `/LOG:C:\rrc\inj\second.log`, while the tray ran. The toast read **"config.json:
  'extraArgs.copy' ignored: '/LOG:[path]' is not an allowed extra switch. Open Settings to
  fix."** (`g3-12-...`). In the database afterwards: `LOG:[path]` present, `C:\rrc\inj` and
  `second.log` absent. Verified.
- **6e3ecf0, a copy whose every file is kept runs no robocopy step.** Notify on, Robo-Copy of one
  file, Robo-Paste from the background menu into a folder holding an older file of that name,
  Skip: no toast within 10 s, the destination unchanged, job.json states
  `queued > scanning > awaitingDecision > finalizing > done`, `commandCount` 0, 0 files.
  Verified.

### Upgrade in place and the install loop (step 2)

- Uninstalled, draft installed, tray running, config.json edited (retries 3, retry wait 5,
  conflict skip, retention 25, notify off; SHA256 `b52984da...`). Frozen `--install --quiet`:
  exit 0, exe `0076f0bf` = package, DisplayVersion 1.0.0-beta.2, Icon values
  `...\robo-copy.ico`, `...\robo-cut.ico`, `...\robo-paste.ico` (x3), config.json byte for byte
  unchanged, the folder holds only the exe and the three icons, new tray PID.
- **20 installs** from one desktop script with the tray running, alternating older `--force` and
  frozen so a skipped replace shows in the hash: **20 of 20 exit 0, installed hash as expected,
  DisplayVersion as expected, a new tray PID each time, no `.old` or `.tmp`**, 1.2 to 1.3 s
  each, config.json unchanged after all 20.
- **The earlier anomaly** (exit 0, exe not replaced) did not occur. The second gate attributed it
  (LIKELY) to the wrapper rerunning a still-running scheduled task; that wrapper now refuses a
  running task name, and this loop ran inside one task without it. One more harness trap seen
  here: the loop's first version used `Start-Process -Wait`, which waits for the started
  process's descendants too, so it waited on the tray the installer started and never
  returned (the install itself had completed). `WaitForExit()` on the process does not.

### threads auto (step 3)

Four 128 MB files, `/IORATE:4M`, Pause and Resume through the progress window's buttons (UI
Automation), robocopy's written bytes from `Win32_Process.WriteTransferCount`, screenshots from
the desktop session (`g3-13-...`: paused at /MT 4, running at /MT 8, paused auto C: to virtual
disk, running auto C: to C:):

| Case | `/MT` on robocopy's command line | job log | written while paused (8 s) | after Resume (4 s) | copy |
|---|---|---|---|---|---|
| auto, C: to C: | 32 | `# threads /MT:32 (auto: source unknown, destination unknown, same disk)` | 0 | 76 MB | 4 of 4 hashes match, done |
| auto, C: to virtual disk | 32 | `# threads /MT:32 (auto: source unknown, destination unknown)` | 0 | 76 MB | 4 of 4, done |
| auto, virtual disk to C: | 32 | `# threads /MT:32 (auto: source unknown, destination unknown)` | 0 | 76 MB | 4 of 4, done |
| `"threads": 4`, C: to virtual disk | 4 | `# threads /MT:4 (fixed in settings)` | 0 | 76 MB | 4 of 4, done |
| `"threads": 8`, C: to virtual disk | 8 | `# threads /MT:8 (fixed in settings)` | 0 | 76 MB | 4 of 4, done |

Each job's states: `running > paused > running > finalizing > done`. `Get-PhysicalDisk`
MediaType is `Unspecified` for the QEMU SATA disk and both virtual disks, so "unknown" is the
design's answer; the solid-state and rotational branches were again **not** exercised. The
format-1 `"threads": 32` kept through the update also read as auto (`# threads /MT:32 (auto:
...)` on the step 0 pastes).

### Menu icons and scaling (step 4)

Classic menu at 100% on a file (`g3-07-...`), a folder (`g3-30-...`) and a folder background
(`g3-14-...`); at 150% (`g3-26-...`, `g3-27-...`) and 175% (`g3-28-...`, `g3-29-...`), each scale
set through `LogPixels` (144, 168) with `Win8DpiScaling` 1 and a sign-out and sign-in. Magnified
4x (`g3-25-...`) the Robo-Copy and Robo-Cut glyphs have one-pixel edges at all three, no blur.
Restored to `LogPixels` 96 and `Win8DpiScaling` 0 (the values found) with a sign-out and
sign-in; the tray came back from the Run value each time.

### Hotkey (step 5b) and access keys

A second program held Ctrl+Shift+V with `RegisterHotKey` and logged every press it received
with the foreground window's class.
- Ctrl+C on a file in Explorer, then Ctrl+Shift+V with a file selected in another folder:
  pasted there ("Copy finished, 1 item (12 bytes) to b", `g3-16-...`); the second program
  received nothing.
- Two tabs, the second active: pasted into the active tab only, the first tab's folder unchanged
  (`g3-17-...`).
- Desktop (an icon selected): pasted to the desktop ("... to Desktop", `g3-19-...`).
- Passed through to the second program: search box, address bar in edit mode, rename box (all
  `CabinetWClass`), the desktop's rename box (`Progman`), Notepad (`Notepad`), Microsoft Store.
- Notepad started from a scheduled task opened behind File Explorer (Windows' focus rules), so
  the first press went to the folder in front: it pasted there, the file already existed and
  the conflict dialog came to the front (`g3-18-...`), as designed for that foreground.
- Recycle Bin and This PC (a drive selected, so the item list had the focus): "Can't Robo-Paste
  here" toast, nothing pasted (`g3-20-...`, `g3-21-...`).
- Settings, opened from the tray's hotkey line: the Hotkey row named the other registration
  ("Another app also uses Ctrl+Shift+V ...", `g3-22-...`). Unticked and saved: config.json
  `"pasteHotkey": ""`, and the next press in a folder's item list reached the second program.
- Access keys in the classic menu: Y (Robo-Copy) on a file (the cold start above), U (Robo-Cut)
  on a folder, then B (Robo-Paste) on another folder's background: the folder moved there.
- Typing during a paste (`/IORATE:2M`, 7.6 MB/s): 12 keys into Explorer's search box all showed
  within 0.5 s of the last (`g3-23-...`).
- The one-minute run of presses passing through File Explorer seen at both earlier gates did
  **not** occur: every press in File Explorer in this session pasted or refused as expected.
  Still open; nothing here shows its cause.

### 50,000 items (step 5c)

50,000 files of 25 bytes, Ctrl+A, Shift+F10, Show more options, Y. A watcher polled for the
classic menu window (`#32768`) and the clipboard sequence number, and pinged Explorer's window
with `WM_NULL` every 50 ms (replies over 250 ms logged). Menu closed to clipboard changed:
**1.91 s** and **1.85 s**; the clipboard then held 50,000 file paths (the watcher's own read of
the list took 17.8 s, after the change, so it is not in the figure; a first run that counted it
is discarded). Explorer's window: one stall of 1.61 to 1.64 s while its own menu was built and
one of **1.45 to 1.47 s** right after Y, no other reply over 250 ms. The tray used 0.3 s of CPU
for the copy.

### Foreground (step 5d)

Right-click Robo-Paste from a folder background's classic menu (B), 100%, a conflict on one of
two files: the conflict dialog came to the front with the focus on Skip and the progress window
opened above the Explorer window (`g3-15-...`); Skip: the other file copied, the conflicting one
kept. After a hotkey paste with a conflict the dialog also came to the front (`g3-18-...`).
Nothing to fix.

### Crash log (step 5)

No test hook exists. Same forced failure as the second gate: a mutex created under the name of
the tray's `Local\RoboRightClick.Ready` event, then the installed exe started.
- **Normal mode**: "RoboRightClick could not start" (`g3-24-...`), exit 1, and
  `%LOCALAPPDATA%\RoboRightClick\crash.log` written: one 576-byte entry with time, version
  1.0.0-beta.2, Windows 10.0.26200.0, thread UI, `WaitHandleCannotBeOpenedException` with the
  quoted name replaced by `[path]`, and the stack (`SingleInstance.TryAcquire`,
  `TrayApplication.Run`). No other file in the data folder was created or changed in size; five
  job folders' timestamps as listed by their parent moved by about 5 ms toward their own write
  times (LIKELY NTFS updating the parent's cached copy when the folders were opened; no content
  changed).
- **Ephemeral mode** (`"logging": "ephemeral"`): the same notice, exit 1, **no crash.log** and no
  new, removed or changed entry in `%LOCALAPPDATA%\RoboRightClick`.
An unhandled exception in a running tray, and rotation, are still unverified.

### Found

No app defect. Two harness errors, recorded above (the cancel click, `Start-Process -Wait`).
Docs: README's "The exe is not signed" section said none of its points had been observed in
the test log; this entry and the two before it observe them. Corrected on main after this run;
README.md is in the zip, so the package changes.

### Not verified, or still open

The hotkey pass-through run (not seen this time); why the Security run's settings-problem
toasts never reached Windows' notification database; the online SmartScreen prompt ("Windows
protected your PC"); the first gate's marked install; solid-state and rotational thread choices;
crash.log from an unhandled exception and its rotation; 125%; a second monitor; an elevated
Explorer; a second keyboard layout.

**VM housekeeping:** app uninstalled with `--uninstall --quiet`; every key, the Run value, the
three folders and the process checked absent by name, and no key named `Robo*` left under
HKCU\Software\Classes. Both virtual disks detached and deleted; the scratch folder, downloads,
desktop file and every scheduled task of this session removed; display scale back to 100%.
The pre-existing `RrcParityExplorer` task was left alone.

## 2026-10-03 · release package for 1.0.0-beta.2

Published from tag `v1.0.0-beta.2` (commit 469243a). It was built in fresh clones at the tag:
two publishes by `scripts/check-reproducible.sh` and a third from a separate clone, all
identical. A build in the long-lived working tree gave a different exe (it reuses build state),
so it was not used. That is the reason release builds come from clean clones.

- `RoboRightClick.exe` / `RoboRightClick-1.0.0-beta.2-setup.exe`: SHA256
  `0076f0bffe0cdaa8487ea84cc95d8f1fc057a80daed02f14a0a6dccc6d9a6bd1`. Byte-identical to the
  exe every runtime result in the third gate entry ran against (commit 186c79c). The commits
  between 186c79c and 469243a change documentation only, and the exe hash confirms it.
- `RoboRightClick-1.0.0-beta.2-win-x64.zip`: SHA256
  `2b63a30766000f30fc3e6f1db3e2d3250eb3f5316ba82c96bc3bd7c5a6f598fa` (the exe, README.md with the
  corrected unsigned-exe notes, LICENSE, Fonts/LICENSE-IBM-Plex-OFL.txt).

Verified on the Windows 11 build 26200 VM only. Not tested: physical machines, network shares,
USB and spinning disks, the online SmartScreen prompt, scaling other than 100/150/175%, a
second monitor. No throughput numbers were measured.
