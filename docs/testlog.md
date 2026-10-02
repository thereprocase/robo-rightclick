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
