# robo-rightclick

Adds **Robo-Copy**, **Robo-Cut** and **Robo-Paste** to the classic Windows Explorer
right-click menu. They behave like Explorer's Copy, Cut and Paste, including name-conflict
prompts, `- Copy` naming, pause and cancel. The bulk data moves through
`robocopy /MT:32`, which copies 32 files in parallel.

A tray icon shows queued and running jobs. Each job's log is kept on disk, unless
**ephemeral mode** is on, in which case the app writes nothing about any job to disk.

> **Status: beta (1.0.0-beta.1). Unverified on Windows** until
> [docs/testlog.md](docs/testlog.md) says otherwise. The release is cross-compiled on Linux;
> the log lists what has actually been run. The plan is in [docs/design.md](docs/design.md).

## Download and verify

Each release has a zip and a `.sha256` file:

- `RoboRightClick-<version>-win-x64.zip`
- `RoboRightClick-<version>-win-x64.zip.sha256`

The zip holds `RoboRightClick.exe` (self-contained, no .NET install needed), `LICENSE`,
`README.md` and `Fonts\LICENSE-IBM-Plex-OFL.txt`. Check the download in PowerShell and
compare it with the hash in the `.sha256` file:

    Get-FileHash .\RoboRightClick-<version>-win-x64.zip -Algorithm SHA256

## Install

Per user. No administrator rights, and no Explorer settings are changed.

- Double-click `RoboRightClick.exe` outside the install folder. It offers to install.
- Or from a terminal: `RoboRightClick.exe --install [--autostart | --no-autostart]`.
  Without a flag, a first install starts with Windows and a reinstall keeps your setting.

Install copies the exe to `%LOCALAPPDATA%\Programs\RoboRightClick\`, writes the registry
keys listed in [docs/host-architecture.md](docs/host-architecture.md) (section 4) under
`HKEY_CURRENT_USER`, and starts the tray.

## Where the items appear

The items are in the classic context menu. On Windows 11 that is **Show more options**, or
Shift+F10 with the item selected. **Robo-Copy** and **Robo-Cut** appear on files and folders.
**Robo-Paste** appears on a folder, a drive and the empty background of a folder window.

## Uninstall

Settings, Apps, Installed apps, **RoboRightClick**, Uninstall. Or run
`RoboRightClick.exe --uninstall`. It removes the registry keys install wrote, the Run entry,
the config, history and job logs, and the install folder. Nothing else is touched.

## Scripting

The exe is a Windows GUI program, so `cmd` and PowerShell do not wait for it and the exit
code is lost unless you ask for it:

    start /wait RoboRightClick.exe copy "C:\data\a.txt" "C:\data\b.txt"
    start /wait RoboRightClick.exe paste "D:\target"

    $p = Start-Process RoboRightClick.exe -ArgumentList 'paste','D:\target' -Wait -PassThru
    $p.ExitCode

Exit codes: 0 ok, 1 failed, 2 usage error. The exit code reports that the tray accepted the
verb, not that the copy finished. A paste runs as a job in the tray; watch it in the tray's
Jobs window. `copy` and `cut` put the items on the clipboard, and `paste` takes the clipboard.
`RoboRightClick.exe --help` prints the full list.

## How it fits into Explorer

- The items live in the classic context menu. On Windows 11 that's
  **Show more options** (or Shift+F10).
- Robo-Copy and Robo-Cut place files on the normal Windows clipboard, so a plain Ctrl+V works
  afterwards. Robo-Paste accepts files copied with a plain Ctrl+C.

## Ephemeral mode

Set `logging` to `ephemeral` (tray menu, Settings, or `config.json`). Then the app writes
nothing about jobs to disk: no job files, no history, no temp files. Toasts name no paths,
and clipboard writes are marked to stay out of clipboard history and cloud clipboard. The
one file it writes is `config.json`, which holds the setting itself. History is kept in memory
until the app exits. The change applies to jobs started after it.

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
| `threads` | `32` | robocopy `/MT` thread count (1–128) |
| `retries` / `retryWaitSeconds` | `0` / `0` | robocopy `/R` and `/W`; Explorer doesn't retry on its own either |
| `conflictDefault` | `ask` | `ask` (Explorer's Replace/Skip prompt), `replace`, `skip`, `keepNewer` |
| `maxConcurrentJobs` | `0` | `0` = unlimited, as in Explorer |
| `logging` | `normal` | `normal` or `ephemeral` (writes no job data at all) |
| `logRetentionJobs` | `100` | job logs kept in normal mode |
| `startWithWindows` | `true` | start the tray app at sign-in |
| `notifyOnComplete` | `true` | toast when a job finishes (path-free in ephemeral mode) |
| `showProgressWindow` | `true` | open a progress window for each paste, like Explorer's copy dialog |
| `extraArgs.copy` / `extraArgs.move` | `""` | extra robocopy switches from an allow-list (`/J`, `/Z`, `/SL`, `/COMPRESS`, `/NOOFFLOAD`, `/FFT`, `/DST`, `/IORATE:n`, `/IOMAXSIZE:n`, `/THRESHOLD:n`); anything else is rejected |

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
