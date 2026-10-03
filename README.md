# robo-rightclick

Adds **Robo-Copy**, **Robo-Cut** and **Robo-Paste** to the classic Windows Explorer
right-click menu. They behave like Explorer's Copy, Cut and Paste, including name-conflict
prompts, `- Copy` naming, pause and cancel. The bulk data moves through
`robocopy /MT:32`, which copies 32 files in parallel.

A tray icon shows queued and running jobs. Each job's log is kept on disk, unless
**ephemeral mode** is on, in which case the app writes nothing about any job to disk.

> **Status: beta (1.0.0-beta.1).** Built on Linux and cross-compiled for Windows. Install,
> the three menu items, the clipboard and uninstall have run on one Windows 11 virtual machine
> ([docs/testlog.md](docs/testlog.md), 2026-10-02). Everything else is unverified on Windows
> until the test log says otherwise. The plan is in [docs/design.md](docs/design.md).

## Supported Windows

Windows 11 on x64. The only Windows build this beta has run on is **26200** (Windows 11
Enterprise evaluation, in a virtual machine; docs/testlog.md 2026-10-02). Other Windows 11
builds, Windows 10, ARM64 machines and physical machines are untested.

## Download and verify

Each release has a zip and a `.sha256` file:

- `RoboRightClick-<version>-win-x64.zip`
- `RoboRightClick-<version>-win-x64.zip.sha256`

The zip holds `RoboRightClick.exe` (self-contained, no .NET install needed), `LICENSE`,
`README.md` and `Fonts\LICENSE-IBM-Plex-OFL.txt`. Check the download in PowerShell, in the
folder holding both files. It prints `True` when the zip matches its `.sha256` file:

    $zip = 'RoboRightClick-<version>-win-x64.zip'
    (Get-FileHash $zip -Algorithm SHA256).Hash -eq (Get-Content "$zip.sha256").Split(' ')[0]

`False` means the download is damaged or not the published file: download it again, and do
not run it. The zip is reproducible: `./scripts/publish.sh` on the release's commit produces the
same SHA256.

### The exe is not signed

`RoboRightClick.exe` has no code signature: the project has no signing certificate. The
SHA256 check above is how you know the file is the published one. Windows is cautious with
unsigned downloads. None of the following has been observed in this project's test log yet;
it is how Windows normally treats such a file:

- **Unblock the zip before extracting it.** Windows marks downloaded files, files extracted
  from a marked zip inherit the mark, and the installed copy of the exe may keep it. After the
  hash check, run `Unblock-File .\RoboRightClick-<version>-win-x64.zip`, then extract.
- **SmartScreen.** If a "Windows protected your PC" window appears when you start the exe,
  choose **More info**, check that the app name is `RoboRightClick.exe`, then **Run anyway**.
- **Smart App Control must be off.** When it is on (Windows Security, App & browser control,
  Smart App Control settings), it blocks unsigned apps and has no per-app exception, so
  RoboRightClick cannot run. Turning it off is a system-wide decision; read Microsoft's
  description of the setting before you change it.

## Install

Per user. No administrator rights, and no Explorer settings are changed.

- Double-click `RoboRightClick.exe` outside the install folder. It offers to install, update or
  repair, depending on what is installed (see Update).
- Or from a terminal: `RoboRightClick.exe --install [--autostart | --no-autostart] [--quiet] [--force]`.
  Without a flag, a first install starts with Windows and a reinstall keeps your setting.
  `--quiet` skips the result message box; the exit code (0 ok, 1 failed) is the result.

Install copies the exe and the three menu icons to `%LOCALAPPDATA%\Programs\RoboRightClick\`,
writes the registry keys listed in [docs/host-architecture.md](docs/host-architecture.md)
(section 4) under `HKEY_CURRENT_USER`, writes a default `config.json` if there is none, and
starts the tray.

Install refuses, with a message and exit code 1, when a folder it would use has a character
in its path that uninstall cannot handle safely (`" & | < > ^ % !`, for example a user name
with `&`). It then changes nothing, so it never installs something it could not remove.

## Update

The single `RoboRightClick.exe` is the installer; there is no separate setup program. It compares
its own version (for example `1.0.0-beta.1`; a beta is older than the same number without a
label, and `beta.2` is older than `rc.1`) with the installed one. The installed version is read
from the installed exe; if the Installed apps entry says something else, the exe is trusted and
the entry is corrected. A version that cannot be read counts as older.

| Installed | Double-click outside the install folder | `--install` |
|---|---|---|
| nothing | "Install RoboRightClick 1.0.0-beta.2?" | installs |
| older | "Update RoboRightClick 1.0.0-beta.1 → 1.0.0-beta.2?" | updates |
| the same | "RoboRightClick 1.0.0-beta.2 is installed. Repair it?", with Open | repairs |
| newer | explains, offers Open, changes nothing | exit code 1, changes nothing |

`--quiet` never shows a window; the exit code (0 ok, 1 failed) is the result.

1. Download the new release, check its hash, unblock the zip and extract it, as above.
2. Let running pastes finish, or cancel them. An update asks the running tray to exit; the tray
   refuses while jobs run, and the update then stops with a message and changes nothing. A
   running copy is never killed.
3. Double-click the new `RoboRightClick.exe`, or run it with `--install`. The exe is replaced
   through a temporary file, the previous exe is kept as `RoboRightClick.exe.old` until the
   update has finished, and the menu icons and the registry keys are rewritten. If any step
   fails, the previous exe is put back and its tray restarted, and the failure is reported.
   `config.json`, job logs, history and the crash log stay as they are: a setting the new
   version added and the file lacks takes its default, and nothing is written to the file. Only
   an explicit `--autostart` or `--no-autostart` changes the start-with-Windows setting.
   Explorer is told to reload its icons; if a menu icon still looks old, sign out and in again.
4. Check the result: Settings, Apps, Installed apps lists RoboRightClick with the new version.
   Or compare the installed exe with the one you extracted:

       (Get-FileHash "$env:LOCALAPPDATA\Programs\RoboRightClick\RoboRightClick.exe").Hash -eq (Get-FileHash .\RoboRightClick.exe).Hash

The installer also compares the installed exe with the one it copied, and fails (restoring the
old one) if they differ. Updating over an installed build ran many times on the test VM before
version-aware install existed (docs/testlog.md 2026-10-02); the version decisions, the
.old rollback and the icon refresh are cross-compiled and unverified on Windows.

### Downgrade

To go back to an older version, run the older exe with `--install --force`. Without `--force`
it refuses, with exit code 1, because the installed version is newer. The older version reads a
`config.json` written by the newer one but never saves over it (see Configuration).

## Where the items appear

The items are in the classic context menu. On Windows 11, right-click, then **Show more
options**. On build 26200, Shift+F10 opens the new menu, whose **Show more options** (access
key W) opens the classic one (docs/testlog.md 2026-10-02). **Robo-Copy** and **Robo-Cut** appear
on files and folders. **Robo-Paste** appears on a folder, a drive and the empty background of
a folder window, but not when several folders are selected. Each item has its own icon since
commit e13fa37; that Explorer shows it is not yet verified on Windows.

With the classic menu open, one letter runs an item, as with Explorer's own: **Y** for
Robo-Cop**y**, **U** for Robo-C**u**t, **B** for Ro**b**o-Paste. Explorer already uses C, T and
P for its Copy, Cut and Paste. If another menu item uses the same letter, pressing it moves
between the two and Enter runs the selected one. An install from before this change gets the
letters when it is installed again. That the letters work as described is not yet verified on
Windows.

## Robo-Paste hotkey

**Ctrl+Shift+V** in a File Explorer folder, or on the desktop, runs Robo-Paste into that
folder: the same paste as right-clicking its background and choosing Robo-Paste. Nothing in
this section is verified on Windows yet; the checks that will verify it are listed in
[docs/decisions/0001-paste-hotkey.md](docs/decisions/0001-paste-hotkey.md).

- It works while the tray app is running. A right-click starts the tray; the hotkey cannot.
- Click in the folder's file list first. With focus in the navigation pane, the search box,
  the address bar or a name being edited, the key goes to that control as usual (in text
  fields Ctrl+Shift+V is paste as plain text).
- Holding the keys down pastes once, and so does pressing them twice within half a second.
- Ctrl+Z does not undo a Robo-Paste.
- Pressed right after a Ctrl+C or Ctrl+X, it waits up to a second for that copy or cut to
  reach the clipboard, and otherwise does nothing and says so, rather than paste what was on
  the clipboard before. A Ctrl+C with nothing selected counts too: wait 2 seconds after it.
- Libraries, This PC, the Recycle Bin, search results, Control Panel and zip folders are
  refused with a notification, as with the right-click.
- In ephemeral mode, keep using Robo-Copy and Robo-Cut: after a plain Ctrl+C or Ctrl+X,
  clipboard managers and other apps that watch the clipboard can record the file list;
  Robo-Copy and Robo-Cut mark it to be skipped. Windows' own clipboard history (Win+V) is
  not expected to keep file lists at all (not yet verified).
- Change it or turn it off in Settings (**Hotkey**), from the tray menu's hotkey line, or in
  `config.json` (`"pasteHotkey": ""` is off). An install from before the hotkey existed gets
  it switched on when upgraded. The message at the end of the install names it (an install
  with `--quiet` shows none), and so does the tray's first notice, unless a notice about
  interrupted pastes or a settings problem takes its place.
- The combination is Ctrl, optionally Shift, and one key: A to Z, 0 to 9 or F1 to F12 (not
  F10). Alt and the Windows key are not allowed, and neither are combinations File Explorer
  already uses (Settings says why for each one).
- In File Explorer and on the desktop the hotkey gets the combination before another app's
  global shortcut for it would. Settings notes it when another app has registered the same
  combination.

How it works: while File Explorer or the desktop is the active window, the app watches the
keyboard with a low-level keyboard hook, and removes the hook as soon as another window is
active. It takes the combination only when the focus is in a file list, and lets every other
key through. It records no keys. The one thing it keeps, in memory only, is the time of the
last Ctrl+C or Ctrl+X in a file list and the clipboard's change counter at that moment, so a
paste right after it can wait for the clipboard. Nothing about the hotkey is written to disk
or to a log.
The code that may touch the keyboard hook is confined to two files, which a test checks.

## Uninstall

Settings, Apps, Installed apps, **RoboRightClick**, Uninstall. Or run
`RoboRightClick.exe --uninstall [--quiet]`. It removes the registry keys install wrote, the Run entry,
the config, history, job logs and crash logs, the menu icons and the install folder. Nothing
else is touched.

## Files the app writes

| What | Where | When |
|---|---|---|
| Program and menu icons | `%LOCALAPPDATA%\Programs\RoboRightClick\` (`RoboRightClick.exe`, `robo-copy.ico`, `robo-cut.ico`, `robo-paste.ico`) | install |
| Settings | `%APPDATA%\RoboRightClick\config.json`; `config.json.bad` is a copy of a file that could not be read, kept before it is replaced | install, Settings window, tray menu; in both modes |
| Job logs | `%LOCALAPPDATA%\RoboRightClick\jobs\<date-time-id>\job.json` and `robocopy.log` | each paste in normal mode; the newest `logRetentionJobs` are kept |
| History | `%LOCALAPPDATA%\RoboRightClick\history.jsonl` (`history.1.jsonl` after 10,000 lines or 8 MB) | each finished paste in normal mode |
| Crash log | `%LOCALAPPDATA%\RoboRightClick\crash.log` (`crash.1.log`, the previous one, after 256 KB) | an unexpected error in the tray, in normal mode only; never in ephemeral mode, and never after an ephemeral job in the same session |

The tray menu's **Open logs** opens `%LOCALAPPDATA%\RoboRightClick`. Registry values are all
under `HKEY_CURRENT_USER` and listed in docs/host-architecture.md section 4.

`crash.log` holds, per error: its type, its message with anything that looks like a path
replaced by `[path]`, the program's stack trace, the app version, the time (UTC) and the
Windows build. An unquoted path also takes the rest of its line with it, since nothing marks
where a path with spaces ends. A tray that fails to start writes its error there too (unless
config.json says ephemeral or cannot be read). Please attach it to a bug report, but read it
first: the path filter is a heuristic, and a bare file name without quotes or folder can get
through.

A paste that was still running when the app ended (a crash, a power cut) is reported at the
next start, with a tray notice that some destination files may be incomplete. Its `job.json`
is then marked `interrupted`, so the notice does not repeat. Two cases repeat it at every
start: a `job.json` written by a newer version of the app (or with a damaged `version`),
which an older version never rewrites, and one the app failed to rewrite. Deleting that
job's folder ends the notice.

## Troubleshooting

**A Robo item is missing from the menu.** Look in the classic menu (**Show more options**),
not the new one. Robo-Paste is only on folders, drives and a folder's empty background, and
not when several folders are selected. To check that the items are registered, run in
PowerShell:

    Test-Path 'HKCU:\Software\Classes\AllFilesystemObjects\shell\RoboCopy\command'
    Test-Path 'HKCU:\Software\Classes\Directory\Background\shell\RoboPaste\command'

`False` means the registration is gone: run `RoboRightClick.exe --install` again.

**The tray icon is not there.** Windows 11 puts new tray icons in the overflow (the `^` next
to the clock); drag it out to keep it visible. The tray does not have to be running for the
menu items: the first click on one starts it (observed on the test VM in about 0.3 to 0.4
seconds, docs/testlog.md 2026-10-02). To start it by hand, run
`%LOCALAPPDATA%\Programs\RoboRightClick\RoboRightClick.exe`. It starts with Windows unless
`startWithWindows` is off; after a sign-in on the test VM it appeared 7.4 seconds after
Explorer started (same entry). `Get-Process RoboRightClick` shows whether it runs.

**The hotkey does nothing.** The tray menu's hotkey line says whether it is on: "off"
(turned off), "off (setting invalid)" (Settings shows the problem) or "not active" (Windows
refused a hook the hotkey needs, or the hotkey could not start). A refused keyboard hook is
tried again the next time File Explorer becomes the active window; if the line still says
"not active", exit the tray from its menu and start it again. Check that the tray is running and that the focus is in the file list, not the
navigation pane or a text field. In a File Explorer window running as administrator the
hotkey is not expected to work, because Windows keeps its keys from a normal app (not yet
verified).

**A click does nothing.** Every refusal (nothing on the clipboard, too many items, a folder
that is not on a drive) is reported as a Windows notification. If notifications are off or
Do not disturb is on, check the notification center. If Explorer reports "Server execution
failed", the installed exe is missing or cannot start: install again. If the app stopped
with an error, see `crash.log` above (normal mode only).

## Differences from Explorer

Robo-Copy, Robo-Cut and Robo-Paste follow Explorer by default. Where they do not, the reason
is in [docs/parity.md](docs/parity.md), which also says which differences were measured on
Windows and which are design decisions not yet measured. In plain words:

- A copied file keeps its original "created" date; Explorer gives the copy the time of copying.
  A copied folder keeps its original "modified" date.
- A junction or folder link inside a copied folder leaves nothing at the destination;
  Explorer leaves an empty folder with its name.
- Files that cannot be copied are collected and offered as **Try again / Skip** at the end of
  the job, instead of a question in the middle of it. **Try again** is offered once per job. It
  overwrites only a file robocopy itself reported failing at a destination that was free, or
  where you had chosen Replace; for every other file whose name is taken by then it asks, as a
  new paste would.
- Paths longer than 260 characters are copied; Explorer skipped them in the measured run.
- Robo-Cut does not dim the cut items' icons the way Ctrl+X does. They are still moved.
- A Robo-Cut pasted with Explorer's own Ctrl+V stays on the clipboard afterwards; pasting it
  again finds the files already gone. Robo-Paste empties the clipboard after a cut, as
  Explorer does.
- Robo-Paste is not offered when several folders are selected; Explorer pastes into the one
  you right-clicked.
- When two selected items have the same name (from a search, for example), the second is
  refused and listed in the summary; Explorer would ask about it once the first is there.
- The Robo items have icons; Explorer's classic Cut, Copy and Paste have none.
- A paste whose files overlap those of a paste still running waits for it to finish; Explorer
  runs both at once. Pastes into unrelated folders run in parallel.
- Cutting between two drives offers no "keep both" for name conflicts; copies, and cuts
  within one drive, do.
- A selected junction or folder link is moved only within one drive; copying it, or moving it
  to another drive, is refused with a message that points to Explorer's Paste.
- Files that appear at the destination while a paste runs are skipped, not overwritten, and
  listed in the job's summary; Explorer would ask about them.
- "Keep newer" (a configured default only) also keeps a destination file with the same time
  but a different size.
- After **Try again** completes a cut, the emptied source folders stay.
- One click takes at most 250,000 items; select their folder instead.
- The Robo-Paste hotkey refuses libraries and zip folders (Explorer's Ctrl+V pastes into
  them), does nothing with focus in the navigation pane (Explorer's Ctrl+V pastes into the
  selected folder there), and may wait or refuse right after a Ctrl+C or Ctrl+X.

## Scripting

The exe is a Windows GUI program, so `cmd` and PowerShell do not wait for it and the exit
code is lost unless you ask for it. Install does not add the exe to `PATH`, so use its full
path. In `cmd`, the empty `""` after `start` is the window title; without it, `start` takes
the quoted exe path as the title and runs nothing.

    start "" /wait "%LOCALAPPDATA%\Programs\RoboRightClick\RoboRightClick.exe" copy "C:\data\a.txt" "C:\data\b.txt"
    echo %ERRORLEVEL%
    start "" /wait "%LOCALAPPDATA%\Programs\RoboRightClick\RoboRightClick.exe" paste "D:\target"

In PowerShell, pass the arguments as one string and quote each path yourself:
`-ArgumentList` does not add quotes, so a path with a space would split in two.

    $exe = "$env:LOCALAPPDATA\Programs\RoboRightClick\RoboRightClick.exe"
    $p = Start-Process $exe -ArgumentList 'paste "D:\My target"' -Wait -PassThru
    $p.ExitCode

Exit codes: 0 ok, 1 failed, 2 usage error. The exit code reports that the tray accepted the
verb, not that the copy finished. A paste runs as a job in the tray; watch it in the tray's
Jobs window. `copy` and `cut` put the items on the clipboard, and `paste` takes the clipboard.
`RoboRightClick.exe --help` prints the full list.

## Clipboard

Robo-Copy and Robo-Cut place files on the normal Windows clipboard, so a plain Ctrl+V works
afterwards. Robo-Paste accepts files copied with a plain Ctrl+C.

## Ephemeral mode

Set `logging` to `ephemeral` (tray menu, Settings, or `config.json`). Then the app writes
nothing about jobs to disk: no job files, no history, no temp files. Toasts name no paths,
and clipboard writes are marked to be skipped by clipboard managers and other apps that
watch the clipboard, clipboard history and cloud clipboard. The
one file it writes is `config.json`, which holds the setting itself. History is kept in memory
until the app exits. The change applies to jobs started after it. No crash log is written in
ephemeral mode, nor for the rest of a session in which any ephemeral job ran. Job logs and a
crash log written earlier in normal mode stay until you delete them (turning ephemeral mode on
offers to delete the job logs; the crash logs are removed only by uninstall, or by you).

Limits. The guarantee covers what this app writes, not what Windows records:

- robocopy's command line, which contains paths, is visible to the user's other processes and
  to process-creation auditing (event 4688, Sysmon)
- Prefetch, the NTFS change journal, and the file-system metadata of the copied files
- the pagefile and the hibernation file
- a crash dump, if Windows Error Reporting is configured machine-wide and the OS ends the process
- the single-file runtime may extract native libraries to `%TEMP%\.net`; those hold no job data

## Configuration

`%APPDATA%\RoboRightClick\config.json`, also editable from the tray's Settings window.

| Setting | Default | Meaning |
|---|---|---|
| `threads` | `"auto"` | robocopy `/MT` threads. `"auto"` picks per drive: 32 for SSDs and network shares, 8 when a spinning disk is involved, 4 within one spinning disk. A number from 1 to 128 fixes it. Each run's count and the drive types it was based on are in the job's `robocopy.log`. Builds before `"auto"` wrote `32` into every config.json; in a version-1 file `32` is therefore read as `"auto"`, and the next save writes `"auto"` with version 2. Any other version-1 number stays fixed |
| `retries` / `retryWaitSeconds` | `0` / `0` | robocopy `/R` and `/W`; Explorer doesn't retry on its own either |
| `conflictDefault` | `ask` | `ask` (Explorer's Replace/Skip prompt), `replace`, `skip`, `keepNewer` |
| `maxConcurrentJobs` | `0` | `0` = unlimited, as in Explorer |
| `logging` | `normal` | `normal` or `ephemeral` (writes no job data at all) |
| `logRetentionJobs` | `100` | job logs kept in normal mode |
| `startWithWindows` | `true` | start the tray app at sign-in |
| `notifyOnComplete` | `true` | toast when a job finishes (path-free in ephemeral mode) |
| `showProgressWindow` | `true` | open a progress window for each paste, like Explorer's copy dialog |
| `extraArgs.copy` / `extraArgs.move` | `""` | extra robocopy switches from an allow-list (`/J`, `/Z`, `/SL`, `/COMPRESS`, `/NOOFFLOAD`, `/FFT`, `/DST`, `/IORATE:n`, `/IOMAXSIZE:n`, `/THRESHOLD:n`); anything else is rejected |
| `pasteHotkey` | `"Ctrl+Shift+V"` | the Robo-Paste hotkey (see above); `""` turns it off. Unlike the other settings, an invalid value turns it off (with a warning) instead of using the default. A file without this setting means the default |
| `version` | `2` | the file's format version, written by the app; leave it as it is. A file without it is read as version 1; one that is not a positive whole number is treated like a newer version's file |

A config.json written by a newer version of the app (a higher `version`) is read by an older
one for the settings it knows, with a tray warning, and is never saved over: saving from the
older version's Settings window or tray menu fails with a message instead, and a reinstall of
the older version leaves the file unchanged. The same holds when `version` is damaged (`"2"`,
`2.0`, `0`): it could be a newer file. Install the newer version, fix the field, or delete
config.json to start again from the defaults.

## Building

On Linux or Windows with the .NET 10 SDK:

    ./scripts/build.sh    # builds everything (the Windows app cross-compiles)
    ./scripts/test.sh     # runs the Core test suite

## Design and fonts

The windows follow the Gridline design language ([docs/gridline.md](docs/gridline.md)). The
app embeds IBM Plex Sans and IBM Plex Mono under the SIL Open Font License 1.1. The licence
text ships in the zip as `Fonts\LICENSE-IBM-Plex-OFL.txt` and in `src/RoboRightClick/Fonts/`.

## Releasing

    ./scripts/publish.sh  # writes artifacts/RoboRightClick-<version>-win-x64.zip and .sha256

End-to-end checks for a Windows machine are in [scripts/e2e/](scripts/e2e/README.md). The
manual beta checklist is [docs/beta-test-plan.md](docs/beta-test-plan.md). Results go in
[docs/testlog.md](docs/testlog.md).

## License

MIT
