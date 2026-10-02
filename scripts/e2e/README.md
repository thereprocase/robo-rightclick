# End-to-end scripts (Windows)

Plain PowerShell scripts that drive an installed `RoboRightClick.exe` the way a user does:
install, the three verbs through the same COM path as a right-click, cancel, ephemeral
mode, the integrity-level check, uninstall. Each `*.Tests.ps1` throws on failure and exits 0
on success; there is no test framework.

**Status:** written and cross-checked on Linux only. They have not been run on Windows. Until
a dated entry in [docs/testlog.md](../../docs/testlog.md) says they ran, treat a failure as just
as likely to be a script defect as an app defect.

## Prerequisites

- Windows 10 or 11, signed in to an interactive desktop session. The clipboard, UI Automation
  and Explorer's copy engine need one; a service or a plain SSH session is not enough.
- Windows PowerShell 5.1 or PowerShell 7. No modules beyond the ones that ship with Windows.
- `RoboRightClick.exe`: the one from the release zip, or `artifacts/publish` after
  `./scripts/publish.sh`.
- Not already installed: `Install.Tests.ps1` refuses to overwrite an install.
- Use a disposable machine or VM. The scripts install and uninstall the app, restart the tray,
  write and restore `config.json`, and create files under `-Root`. `Cancel.Tests.ps1` writes about
  2 GB, `Ephemeral.Tests.ps1` hashes your profile folders.
- `CutSafety.Tests.ps1` scenario A and the cross-volume cut need a second volume: a writable
  folder on a different drive letter (for example a small attached virtual disk), passed as
  `-SecondVolume`. Without it the same-volume part runs and the script reports SKIP, not PASS.

## Run

    .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe
    .\Run-All.ps1 -Exe C:\path\to\RoboRightClick.exe -Root D:\rrc-e2e -SecondVolume E:\scratch

Run-All runs the scripts in this order, each in its own process, and prints a summary to
stdout. It writes no files. Exit code 0 means no test failed (a skipped test is not a failure).

| Script | Checks |
|---|---|
| `Install.Tests.ps1` | `--install`, every registry value, the Uninstall entry, the AppID security descriptors, the tray process |
| `Verbs.Tests.ps1` | copy and paste against Explorer's `CopyHere` on the same tree (deviations from docs/parity.md only), Ctrl+C interop, `X - Copy` naming, paste into own subfolder refused |
| `CutSafety.Tests.ps1` | cross-volume cut with a locked file keeps that source; `skip` conflict with same size and time keeps the source (hash); a movable file in each cut proves the job ran |
| `Cancel.Tests.ps1` | Cancel through UI Automation leaves no partial file and does not touch pre-existing destinations |
| `Ephemeral.Tests.ps1` | five ephemeral jobs leave no new or changed file in `%APPDATA%`, `%LOCALAPPDATA%` or `%TEMP%` except `config.json` and `%TEMP%\.net`, and no file with the test marker; Windows' notification database may change but is searched for the marker (toasts carry no path) |
| `Security.Tests.ps1` | a low-integrity copy of the exe cannot run a verb: exit 1 (a normal copy exits 0); any other code is reported as inconclusive |
| `Uninstall.Tests.ps1` | `--uninstall` removes every key, the Run value, the folders and the tray, and keeps the shared parent keys |

A script can also be run alone, for example
`powershell -STA -File .\Verbs.Tests.ps1 -Root D:\rrc-e2e`. Tests other than Install and
Security use the installed copy of the app. A script that checked nothing exits with code 3,
which Run-All shows as SKIP.

## Parameters

Every location is a parameter. Defaults are under `$env:TEMP\rrc-e2e`. Nothing in the scripts
names a machine, an address or a user.

| Parameter | Used by | Meaning |
|---|---|---|
| `-Exe` | Run-All, Install, Security | Path to `RoboRightClick.exe` |
| `-Root` | all | Scratch folder for test trees (default `$env:TEMP\rrc-e2e`) |
| `-SecondVolume` | Run-All, CutSafety | Writable folder on another volume |
| `-LargeMB` | Verbs | Size of the large file in the test tree (default 64) |
| `-Files`, `-FileMB` | Cancel | Size of the large copy (default 8 files of 256 MB) |
| `-AllowPath` | Ephemeral | Substrings of paths to ignore after a person has judged them to be unrelated noise |

## Things the scripts assume

- Install and uninstall run with `--quiet` and report only through the exit code. As a
  fallback, `Invoke-RoboCommand` in `Common.ps1` presses a message box's `OK` button through UI
  Automation if one still appears.
- The progress window's Cancel button has the accessible name `Cancel`.
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
