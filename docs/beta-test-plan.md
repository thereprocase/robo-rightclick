# Beta test plan

An ordered manual checklist for a run on Windows. Each unverified row of
docs/host-architecture.md section 12 maps to a script in `scripts/e2e/` or to a manual
step, with what to write in docs/testlog.md.

What has a testlog entry so far: the e2e scripts Install, Verbs, CutSafety, Cancel, Ephemeral
(jobs 1 to 5), Security, Uninstall and Footprint passed through `Run-All.ps1` on build 26200 in
a VM (docs/testlog.md 2026-10-02, "security and ephemeral audit", "Run-All on the final exe"),
on that entry's exe. Not run on Windows yet: `Hotkey.Tests.ps1`, Ephemeral job 6 (the hotkey
paste), `Update.Tests.ps1`, and every manual step below unless a testlog entry names it. A
script that passed on an earlier exe is evidence for that exe only; a row stays "unverified" for
a new build until an entry covers it.

Use a disposable Windows 10 or 11 machine, signed in to an interactive desktop, plus a second
volume for the cut-safety step. Run the steps in order; later steps assume the app is installed.

## Before starting

- [ ] Build the package: `./scripts/publish.sh`. Verify the SHA256 on the Windows machine
      (`Get-FileHash`) against `RoboRightClick-<version>-win-x64.zip.sha256`, and the unzipped
      exe against `RoboRightClick-<version>-win-x64.exe.sha256`. Record both with the SDK and
      runtime versions `publish.sh` printed.
- [ ] Note the Windows build (`winver`) and whether the machine is a VM or physical.
- [ ] Unzip. The package must hold `RoboRightClick.exe`, `LICENSE`, `README.md` and
      `Fonts\LICENSE-IBM-Plex-OFL.txt`.

## Ordered checklist

| # | Step | Verifies (section 12 row) | How |
|---|---|---|---|
| 0 | With the app absent: `Update.Tests.ps1 -Exe <this build> -OlderExe <a build with a lower version>`. | Update over a running older install, repair, downgrade refusal (exit 1) with and without `--force`, a stale DisplayVersion put back, rollback after a failure past the exe swap, config.json untouched by an update, no `.old` or `.tmp` left, uninstall removing a planted `.old` and the install temp files; a format-1 config.json with `"threads": 32` (and a BOM) reading as auto (commit 33bd421); an update whose tray start is blocked still succeeding (commit e4e88c4) | Script (never run on Windows yet). Leaves the app absent for step 1. Also by hand once: double-click the newer exe over a running older install and read the update dialog's wording. Record each scenario, the `# threads` line scenario A logged, `INCONCLUSIVE` for E if the read-only config.json did not make the install fail, and `INCONCLUSIVE` for G if a tray started despite the deny-execute entry. |
| 0b | By hand, after step 0: install an older build from before the per-drive thread choice (efe8482) with its own config.json, update to this build, open Settings. Then repeat an update with the tray start blocked by a software restriction rule or AppLocker rule on the install folder, without `--quiet`. | The thread migration on a real older config; the blocked-start result message | Manual. Record what Settings shows for threads (must be auto) and the update's result message, which must say the install finished and add the sentence about the tray not starting (`InstallText.TrayNotStarted`), with exit code 0 and the new DisplayVersion. |
| 0c | Fresh install that fails part way: with the app absent, make `%APPDATA%\RoboRightClick\config.json` a read-only file holding `{}` and run `--install`; then delete it and install normally. | The fresh-install failure path, which has no rollback | Manual. Record the exit code, the message, and what the failed install left: install folder, registry keys (Install.Tests' list), Run value, menu items. Anything left that uninstall would not remove is a defect. |
| 1 | Start the exe from the unzipped folder with Explorer's double-click. | `SetDefaultDllDirectories(SYSTEM32)` does not break WinForms start-up in a single-file app | Manual. The install offer must appear and render (fonts, buttons). Repeat once with a harmless test DLL named like an import placed next to the exe and confirm it is not loaded (Process Explorer module list, or Sysinternals `listdlls`). |
| 2 | Install: `Install.Tests.ps1` (or `RoboRightClick.exe --install`). | Install footprint; per-user keys, Uninstall entry, AppID values | Script. Also open Settings, Apps, Installed apps and confirm the entry shows the version. |
| 3 | Right-click a file, **Show more options**. Select **Robo-Copy**, then right-click a folder background and select **Robo-Paste**. Repeat with 500 selected items. | Out-of-process DelegateExecute with `REGCLS_MULTIPLEUSE`: 500 items in one call to a running instance (spike 1) | Manual. Record the count that arrived and whether a second Explorer window used the same tray process (one `RoboRightClick.exe` in Task Manager). |
| 4 | Select 50,000 small files in one folder and Robo-Copy them. | `BHID_DataObject` yields `CF_HDROP`; time for 50k items (spike 1) | Manual. Record seconds from click to the job appearing in Jobs. The app logs no timing, so use a stopwatch. |
| 5 | Exit the tray from its menu. Right-click, Robo-Copy a file. | `-Embedding` start when the tray is not running; cold-start time; Run-key race (spike 1) | Manual. Record the delay before the menu action took effect, and whether the tray started once. Then sign out and in with autostart on and repeat the click immediately after sign-in to hit the race. |
| 6 | Select two folders and open the context menu. Open it on one folder, a drive, and the empty background of a folder. | `MultiSelectModel=Single` hides Robo-Paste for multi-folder selections; works on the background verb (spike 1) | Manual. Robo-Paste must be absent for two folders and present for the other three. |
| 7 | Run a Robo-Paste that opens the conflict dialog and the progress window while another app has focus. | Explorer allows the tray to take foreground (spike 1) | Manual. Record whether the windows came to the front or only flashed in the taskbar. |
| 8 | `Verbs.Tests.ps1`. | Metadata flags versus Explorer, re-checked through the app; Ctrl+C interop; `X - Copy` naming | Script. A deviation not listed in docs/parity.md is a defect. |
| 8b | `Hotkey.Tests.ps1`. | The Robo-Paste hotkey (docs/decisions/0001-paste-hotkey.md release gates 1 to 5 and 13): pastes into the folder in front, active tab only, one paste when held or double-tapped, libraries and zip folders refused, off with `"pasteHotkey": ""`, nothing when the tray is stopped | Script (never run on Windows yet). Record each scenario A to H. |
| 8c | The hotkey by hand: on the desktop; in the address bar and the search box (Ctrl+Shift+V must stay Explorer's paste as plain text there); in the navigation pane (passes); held down for 2 s; double-tapped; with two Explorer windows and with two tabs; in an elevated Explorer; while Explorer is busy; with a second keyboard layout. | The remaining hotkey gates of docs/decisions/0001-paste-hotkey.md | Manual. Record per case: pasted where, once or more, or passed through to Explorer. A paste into a window that was not in front is a defect. |
| 9 | `Security.Tests.ps1 -Exe <exe> [-SecondVolume <folder>]`. | A low-integrity process cannot activate or call the server (security spike); the output pipe, `extraArgs`, clipboard and file-name hardening, including a switch-like name inside a folder a keep-both answer splits (6b, commit 709c6ac) | Script, six sections. Record both exit codes (control and low) and the result of each section, and for 6b the refused row and the job's final state. |
| 9b | `Ephemeral.Tests.ps1` and `Footprint.Tests.ps1 -Exe <exe>`. | Ephemeral jobs leave no trace; install and uninstall touch only what the design lists; a paste whose every conflict is skipped ends Done (job 4) | Script. A path either one lists is a finding to judge, not noise to allow by default. |
| 9c | Two items with one name from a search: search a folder tree for a name two subfolders share, select both results, Robo-Copy, Robo-Paste into an empty folder. | A second selected item that would land on the same name is refused (commit 83f7b51) | Manual. Record what arrived (the first item only), the refused row and its reason in the summary, and the job's state. |
| 9d | On a non-English Windows (for example German or French display language), Robo-Copy a folder holding a file another program locks. | Robocopy ERROR lines are recognised by their shape, not the English word (commit 9ba801a; the localized words are LIKELY, no capture yet) | Manual. Save the job's robocopy.log ERROR line as a fixture under `tests/RoboRightClick.Core.Tests/Fixtures/` (UTF-16, as the pipe delivers it) and add a parser test for it. Record whether the summary listed the locked file with its Windows message. |
| 10 | Confirm Explorer's own copy and the Windows shell still work while the app is installed: copy a file with Ctrl+C, Ctrl+V; open Properties; open the right-click menu on the desktop. | HKCU AppID `AccessPermission`/`LaunchPermission` are honored and do not break Explorer's activation (security spike) | Manual. Together with step 9 this is the full row: refused for low integrity, Explorer unaffected. |
| 11 | Robo-Cut a file and look at its icon in the folder until the paste, then cancel by copying something else. | Explorer ghosts icons after a Robo-Cut clipboard write (spike 5) | Manual. Record: ghosted yes or no, and whether it cleared. Compare with Explorer's own Ctrl+X. |
| 12 | `CutSafety.Tests.ps1 -SecondVolume <folder> -SmallVolume <folder on a nearly full volume>`. | Cut never deletes a source whose copy failed (invariant 1); robocopy `/MOV` with a "same" file; a cut whose only file conflicts, answered Skip or keep both, ends Done rather than Failed (scenario G, commit 5ab772a) | Script. Record per scenario. Also note whether robocopy removed the source of the skipped "same" file (the design does not depend on the answer, but the log should have it). |
| 12b | "Try again" by hand: (a) cut a folder to a share, disconnect the share mid-run, save a new file into the destination by hand under a name the cut was going to write, reconnect, press "Try again"; (b) press "Try again" a second time on the same parent; (c) with `retries` 3, cut a folder holding a file that another program locks for a few seconds; (d) copy a folder while deleting one of its files after the scan. | The child asks before overwriting what it did not write; "Try again" is offered once; robocopy's own retry ends Done; a vanished source is "could not be found", not "appeared at the destination" | Manual. Record what the child asked, whether the parent row shows RETRIED, the job's final state and the robocopy.log lines around the retry (ERROR, "Retrying", the file line). |
| 12c | Cancel, retry and exit by hand: (a) paste a large file, cancel, paste it again into the same folder and cancel mid-file: the second partial is deleted or listed, never silent; (b) copy a folder while a program writes new files into it, cancel mid-file: the partial is listed, not deleted; (c) with `retries` 0, copy to a share, break the share mid-file so robocopy prints an ERROR, then cancel: the summary lists the failed file and offers Try again; (d) with `conflictDefault` skip, kill robocopy mid-file (Task Manager), then "Try again": the child asks about the full-length file with the amber strip and "Let me decide" focused, and keeping it ends the child "Done; 1 file may be incomplete" with a warning toast; (e) "Try again" on a job with errors, then close the child's question: the parent shows Try again and attention again, and "Show newer job" selects the child; (f) with only a damaged canceled job left, Exit asks, an update is refused with the toast, and job.json lists the damaged file; (g) kill the tray during a paste and start it: the notice names the destination folder and its click selects the job.json; (h) with `retries` 3, a share that drops while a folder is created and then recovers ends Done; a link folder that cannot be created ends DoneWithErrors with no Try again button. | The cancel, retry, exit and notice fixes of this beta (commits 51b3753 to b2b5ffc) | Manual. Record per letter what the summary, toast and Jobs row showed, and the job.json lines for (f). |
| 13 | `Cancel.Tests.ps1 -SecondVolume <folder>`. | Cancel cleanup deletes only partial copies robocopy held open at the kill; a late arrival survives; a canceled cut loses nothing | Script: no partial file left, pre-existing files and the late arrival untouched, every cut file in the source or complete at the destination. Scenario B may report INCONCLUSIVE when robocopy could not be caught before it listed the destination; run it again. On a machine with a debug-output viewer, record the `RoboRightClick cancel cleanup:` lines. |
| 14 | Copy a file with the CLI and compare its archive attribute with an Explorer copy: `attrib` on both. Force the in-process path by cutting a file inside one volume. | `CopyFileEx` sets the archive bit like Explorer's copy | Manual. Record both attribute strings. |
| 15 | `Ephemeral.Tests.ps1`. Then finish one more job in ephemeral mode and read the toast in the notification center. | Ephemeral mode writes no job data (invariant 2); path-free toasts | Script for files, and for toasts as far as Windows stores them: it searches the notification database for the marker. Manual for what is shown: the toast text must contain no path or file name. |
| 16 | From a second machine on the network, try to connect to the output pipe name of a running job (for example by listing `\\<host>\pipe\`). | Remote clients are refused by the output pipe | Manual, optional. Record the error returned. |
| 17 | `Uninstall.Tests.ps1`. | Uninstall removes exactly what install wrote (invariant 4) | Script. Then search `HKCU\Software\Classes` for the three CLSIDs by hand. |
| 18 | Throughput: 10,000 small files and two 4 GB files, Explorer versus Robo. | Design M4 throughput item | Manual. Record seconds and the hardware. Do not claim a speedup in the README unless this row shows it. |

The row "Generated COM vtables match the shell's" (`[GeneratedComInterface]`) is exercised
by steps 3 and 6: if the shell calls into the server and a selection arrives, the vtables work.
Record any crash or `E_NOINTERFACE` there.

## What to record in docs/testlog.md

One entry per session, appended (the log is append-only), in the existing format: date, Windows
build, VM or physical, what ran, what was observed. For this plan:

- The exe's SHA256 and version, so a result can be tied to one build.
- Per step number above: pass, fail or not run; the observed value where the step asks for
  one (counts, seconds, exit codes, attribute strings).
- Failures verbatim: the script's thrown text, not a summary.
- Anything that differed from docs/host-architecture.md section 12.

After the entry exists, update the matching rows in section 12 from "unverified" to
"verified (testlog <date>)" in a separate commit, and only for the rows an entry covers. A
physical-machine smoke test (install, three verbs, uninstall, keys gone) is its own entry,
not assumed from the VM run.
