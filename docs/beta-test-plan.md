# Beta test plan

An ordered manual checklist for the first run on Windows. Each unverified row of
docs/host-architecture.md section 12 maps to a script in `scripts/e2e/` or to a manual
step, with what to write in docs/testlog.md. Nothing here has been run: until a testlog entry
exists, every row stays "unverified".

Use a disposable Windows 10 or 11 machine, signed in to an interactive desktop, plus a second
volume for the cut-safety step. Run the steps in order; later steps assume the app is installed.

## Before starting

- [ ] Build the package: `./scripts/publish.sh`. Verify the SHA256 on the Windows machine
      (`Get-FileHash`) against `RoboRightClick-<version>-win-x64.zip.sha256`.
- [ ] Note the Windows build (`winver`) and whether the machine is a VM or physical.
- [ ] Unzip. The package must hold `RoboRightClick.exe`, `LICENSE`, `README.md` and
      `Fonts\LICENSE-IBM-Plex-OFL.txt`.

## Ordered checklist

| # | Step | Verifies (section 12 row) | How |
|---|---|---|---|
| 1 | Start the exe from the unzipped folder with Explorer's double-click. | `SetDefaultDllDirectories(SYSTEM32)` does not break WinForms start-up in a single-file app | Manual. The install offer must appear and render (fonts, buttons). Repeat once with a harmless test DLL named like an import placed next to the exe and confirm it is not loaded (Process Explorer module list, or Sysinternals `listdlls`). |
| 2 | Install: `Install.Tests.ps1` (or `RoboRightClick.exe --install`). | Install footprint; per-user keys, Uninstall entry, AppID values | Script. Also open Settings, Apps, Installed apps and confirm the entry shows the version. |
| 3 | Right-click a file, **Show more options**. Select **Robo-Copy**, then right-click a folder background and select **Robo-Paste**. Repeat with 500 selected items. | Out-of-process DelegateExecute with `REGCLS_MULTIPLEUSE`: 500 items in one call to a running instance (spike 1) | Manual. Record the count that arrived and whether a second Explorer window used the same tray process (one `RoboRightClick.exe` in Task Manager). |
| 4 | Select 50,000 small files in one folder and Robo-Copy them. | `BHID_DataObject` yields `CF_HDROP`; time for 50k items (spike 1) | Manual. Record seconds from click to the job appearing in Jobs. The app logs no timing, so use a stopwatch. |
| 5 | Exit the tray from its menu. Right-click, Robo-Copy a file. | `-Embedding` start when the tray is not running; cold-start time; Run-key race (spike 1) | Manual. Record the delay before the menu action took effect, and whether the tray started once. Then sign out and in with autostart on and repeat the click immediately after sign-in to hit the race. |
| 6 | Select two folders and open the context menu. Open it on one folder, a drive, and the empty background of a folder. | `MultiSelectModel=Single` hides Robo-Paste for multi-folder selections; works on the background verb (spike 1) | Manual. Robo-Paste must be absent for two folders and present for the other three. |
| 7 | Run a Robo-Paste that opens the conflict dialog and the progress window while another app has focus. | Explorer allows the tray to take foreground (spike 1) | Manual. Record whether the windows came to the front or only flashed in the taskbar. |
| 8 | `Verbs.Tests.ps1`. | Metadata flags versus Explorer, re-checked through the app; Ctrl+C interop; `X - Copy` naming | Script. A deviation not listed in docs/parity.md is a defect. |
| 9 | `Security.Tests.ps1`. | A low-integrity process cannot activate or call the server (security spike) | Script. Record both exit codes (control and low). |
| 10 | Confirm Explorer's own copy and the Windows shell still work while the app is installed: copy a file with Ctrl+C, Ctrl+V; open Properties; open the right-click menu on the desktop. | HKCU AppID `AccessPermission`/`LaunchPermission` are honored and do not break Explorer's activation (security spike) | Manual. Together with step 9 this is the full row: refused for low integrity, Explorer unaffected. |
| 11 | Robo-Cut a file and look at its icon in the folder until the paste, then cancel by copying something else. | Explorer ghosts icons after a Robo-Cut clipboard write (spike 5) | Manual. Record: ghosted yes or no, and whether it cleared. Compare with Explorer's own Ctrl+X. |
| 12 | `CutSafety.Tests.ps1 -SecondVolume <folder>`. | Cut never deletes a source whose copy failed (invariant 1); robocopy `/MOV` with a "same" file | Script. Record per scenario. Also note whether robocopy removed the source of the skipped "same" file (the design does not depend on the answer, but the log should have it). |
| 13 | `Cancel.Tests.ps1`. Then, by hand, start `robocopy <src> <dest> /MT:32` on a few large files, end it with `Stop-Process -Force` mid-copy, and compare each destination file's length and hash with its source. | A killed robocopy leaves in-flight files at full length (cancel cleanup's premise) | Script for the outcome: no partial file left, pre-existing files untouched; note how many files had finished. The premise itself is only visible in the manual run, because the app's cleanup removes the evidence: record each file as full length with the wrong hash, short, or complete. |
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
