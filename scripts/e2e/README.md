# End-to-end scripts (Windows)

Plain PowerShell scripts that drive an installed `RoboRightClick.exe` the way a user does:
install, the three verbs through the same COM path as a right-click, cancel, ephemeral
mode, the integrity-level check, uninstall. Each `*.Tests.ps1` throws on failure and exits 0
on success; there is no test framework.

**Status:** all eight scripts ran on Windows build 26200 (VM) through `Run-All.ps1` with
`-SecondVolume` and `-SmallVolume` (docs/testlog.md, 2026-10-02 security entry): Install, Verbs,
CutSafety, Cancel, Ephemeral, Security, Uninstall and Footprint passed. A script that has not run
against a given build is as likely to fail on a script defect as on an app defect.
On 2026-10-03 (docs/testlog.md) all ten ran on the frozen 1.0.0-beta.1 build: Update and
Footprint passed after a script fix; Hotkey failed on an app defect fixed since (0d8697d), and with
that fix Hotkey and Ephemeral (job 6 included) passed on a scratch build.

## Prerequisites

- Windows 10 or 11, signed in to an interactive desktop session. The clipboard, UI Automation
  and Explorer's copy engine need one; a service or a plain SSH session is not enough.
- Windows PowerShell 5.1 or PowerShell 7. No modules beyond the ones that ship with Windows.
- `RoboRightClick.exe`: the one from the release zip, or `artifacts/publish` after
  `./scripts/publish.sh`.
- Not already installed: `Install.Tests.ps1` refuses to overwrite an install.
- For `Update.Tests.ps1`, a second build with a lower version, passed as `-OlderExe`: for
  example the previous release's exe, or a publish of a scratch checkout whose
  `Directory.Build.props` sets a lower `Version` and `InformationalVersion`. Without it the
  script reports SKIP.
- Use a disposable machine or VM. The scripts install and uninstall the app, restart the tray,
  write and restore `config.json`, and create files under `-Root`. `Cancel.Tests.ps1` writes about
  2 GB, `Ephemeral.Tests.ps1` hashes your profile folders.
- `CutSafety.Tests.ps1` scenarios A and C and the cross-volume cuts need a second volume: a
  writable folder on a different drive letter (for example a small attached virtual disk),
  passed as `-SecondVolume`. Without it the same-volume parts run and the script reports SKIP,
  not PASS. Scenario D needs `-SmallVolume`, a folder on a volume with less free space than
  `-FullFileMB` (default 64 MB; a 40 MB virtual disk works). `Cancel.Tests.ps1` scenario C also
  uses `-SecondVolume`.

## Run

    .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe
    .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe -Root D:\rrc-e2e -SecondVolume E:\scratch -SmallVolume F:\scratch

Run-All runs the scripts in this order, each in its own process, and prints a summary to
stdout. It writes no files. Exit code 0 means no test failed (a skipped test is not a failure).

| Script | Checks |
|---|---|
| `Install.Tests.ps1` | `--install`, every registry value, the Uninstall entry, the AppID security descriptors, the tray process |
| `Verbs.Tests.ps1` | copy and paste against Explorer's `CopyHere` on the same tree (deviations from docs/parity.md only), Ctrl+C interop, `X - Copy` naming, paste into own subfolder refused |
| `CutSafety.Tests.ps1` | cross-volume cut with a locked file, into a folder that refuses new files, and onto a full volume keeps each failed source (hash); `skip` conflict with same size and time keeps the source; a same-volume cut is a rename (file ID kept); the conflict dialog through UI Automation: Replace, Skip, "Let me decide" with both sides ticked (keep both on one volume, skip across volumes); a movable file in each cut proves the job ran; a cut whose only file conflicts, answered Skip or keep both, ends Done with no summary and the source unchanged (G, the zero-step plan) |
| `Cancel.Tests.ps1` | Cancel through UI Automation leaves no partial file and does not touch pre-existing destinations (A); a file that appeared after the job's presence check and that robocopy skipped survives the cancel (B); a canceled cross-volume cut loses no file (C) |
| `Hotkey.Tests.ps1` | the Robo-Paste hotkey: pastes into the open folder (A) and into the active tab only (E); held 2 s and double-tapped, one paste each (B, C); the address bar and search box keep t [... omitted end of long line]
| `Ephemeral.Tests.ps1` | five ephemeral jobs plus a sixth through the hotkey (copy, cut, cancel, conflict answered Skip, which must end Done with no summary, failure on a locked file) leave no new, changed or removed file in `%APPDATA%` (Recent items, jump lists), `%LOCALAPPDATA%` (WER, CrashDumps), `%TEMP%` or the machine-wide WER folders except `config.json` and `%TEMP%\.net`, no file name or content with the test marker or the test folder's path, no marker in HKCU or the Application and System event logs, and the clipboard carries the three opt-out formats. Windows' notification database and a short list of Windows' own background files (web cache, token cache) may change; they are searched for the marker. Then normal mode with `logRetentionJobs` 3: six jobs, `job.json`, `robocopy.log` and `history.jsonl` as designed, the oldest folders pruned, a folder not named like a job kept, no opt-out formats |
| `Security.Tests.ps1` | six sections, runnable alone with `-Sections`: (1) a low-integrity copy of the exe cannot run a verb (exit 1; any other code is a test failure); (2) a low-integrity COM client is refused at `CoCreateInstance`, also with both AppID descriptors removed (restored in a `finally`); (3) the robocopy output pipe, with robocopy held at its start: DACL, a second server on the name, a same-user client dropped by the PID check, a later client refused; (4) 21 hostile `extraArgs` values never reach robocopy's command line; (5) hostile clipboard contents (25 path forms, malformed and oversized blocks, 100,000 missing paths, device-path destinations) are refused with a canary untouched; (6) file names such as `-MOV` and `-E` are refused and the rest of the selection copies (cross-volume cut with `-SecondVolume`); 6b: `-E` inside a folder a keep-both answer splits is refused and listed, the rest arrives, and the job does not end Failed |
| `Uninstall.Tests.ps1` | `--uninstall` removes every key, the Run value, the folders and the tray, and keeps the shared parent keys |
| `Update.Tests.ps1` | needs the app absent and two builds; installs the older, then with the tray running: the newer one updates it (exe hash, DisplayVersion, tray restarted, config.json byte for byte unchanged, no `.old` or `.tmp` left), the same version repairs, the older one is refused with exit 1 (and puts back a stale DisplayVersion) and installs with `--force`, a failure injected after the exe swap (a read-only config.json with `--autostart`) rolls back to the older exe and version, a format-1 config.json with `"threads": 32` reads as auto after the update (the paste's `# threads` line), an update whose tray start is blocked (an inherited deny-execute entry) still exits 0 with the newer build in place, and uninstall removes a planted `.old` and the install temp files with the folder |
| `Footprint.Tests.ps1` | needs the app absent; installs, runs three jobs, uninstalls, and diffs HKCU, the profile and machine folders, scheduled tasks, services and the machine-wide registry around each step: only the design's keys and folders appear, and all of them go. Windows' own noise is classified in the script and reported; `-AllowKey` and `-AllowPath` add judged noise |

A script can also be run alone, for example
`powershell -STA -File .\Verbs.Tests.ps1 -Root D:\rrc-e2e`. Tests other than Install and Footprint
use the installed copy of the app (Security needs both the installed copy and `-Exe`). A script that checked nothing exits with code 3,
which Run-All shows as SKIP.

## Parameters

Every location is a parameter. Defaults are under `$env:TEMP\rrc-e2e`. Nothing in the scripts
names a machine, an address or a user.

| Parameter | Used by | Meaning |
|---|---|---|
| `-Exe` | Run-All, Install, Security, Update, Footprint | Path to `RoboRightClick.exe` |
| `-OlderExe` | Run-All, Update | A build with a lower version than `-Exe` |
| `-Root` | all | Scratch folder for test trees (default `$env:TEMP\rrc-e2e`) |
| `-SecondVolume` | Run-All, CutSafety, Cancel, Ephemeral, Security | Writable folder on another volume |
| `-LargeMB` | Verbs | Size of the large file in the test tree (default 64) |
| `-SmallVolume`, `-FullFileMB` | Run-All, CutSafety | Folder on a nearly full volume, and the size of the file that must not fit (default 64 MB) |
| `-Files`, `-FileMB` | Cancel | Size of the large copy (default 8 files of 256 MB) |
| `-IoRate` | Cancel | robocopy `/IORATE` value set through `extraArgs` for the test, so a fast disk cannot finish before Cancel is pressed (default `8M`) |
| `-CutFiles`, `-CutFileMB` | Cancel | Size of the canceled cross-volume cut (default 6 files of 32 MB; the first is 1 MB) |
| `-Scenarios` | Cancel | Subset of A, B, C to run (default all; C only with `-SecondVolume`) |
| `-Sections`, `-ManyPaths` | Security | Which sections to run (1 to 6), and how many missing paths the "many but under the limit" clipboard case holds (default 100,000) |
| `-AllowKey`, `-AllowPath` | Footprint | Substrings of HKCU key paths or file paths judged to be Windows noise |
| `-AllowPath` | Run-All, Ephemeral | Substrings of paths to ignore after a person has judged them to be unrelated noise. Through `-File`, `"a","b"` arrives as one string; Ephemeral splits it on `,` and `\|` |

## Things the scripts assume

- Install and uninstall run with `--quiet` and report only through the exit code. As a
  fallback, `Invoke-RoboCommand` in `Common.ps1` presses a message box's `OK` button through UI
  Automation if one still appears.
- The progress window's Cancel button has the accessible name `Cancel`. The conflict dialog's
  controls are found by their accessible names: `Replace`, `Skip`, `Decide`, `SelectAllSource`,
  `SelectAllDestination`, `Continue`.
- `Cancel.Tests.ps1` scenario B suspends the next `robocopy.exe` as soon as a polling thread sees
  it, to put a file in the destination before robocopy lists it. If robocopy had already created
  the destination when it was caught, the run throws `INCONCLUSIVE`; run it again.
- Error scenarios restart the tray afterwards so their summary windows cannot answer a later
  dialog.
- The expected registry table in `Common.ps1` mirrors `Registration.InstallValues`. When that
  changes, change the table in the same commit.
- `copy` and `cut` put exactly the given items on the clipboard. `Invoke-Robo` clears the
  clipboard first and waits for those names, so a paste never takes an earlier selection.
- `Wait-Settled` decides a paste is done when the destination's file count and size stop
  changing for 3 seconds and no `robocopy.exe` is running. There is no `--wait`.

## Recording results

Results go in [docs/testlog.md](../../docs/testlog.md), by hand: date, Windows build, VM or
physical machine, what ran, what was observed. The scripts do not write to it. Run-All's
summary is the observation; copy any failure text with it.
