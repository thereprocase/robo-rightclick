# robo-rightclick

Adds **Robo-Copy**, **Robo-Cut** and **Robo-Paste** to the classic Windows Explorer
right-click menu. They behave like Explorer's Copy, Cut and Paste, including name-conflict
prompts, `- Copy` naming, pause and cancel. The bulk data moves through
`robocopy /MT:32`, which copies 32 files in parallel.

A tray icon shows queued and running jobs. Each job's log is kept on disk, unless
**ephemeral mode** is on, in which case nothing about any job is written to disk.

> **Status: in development.** Nothing has been verified on Windows yet. See
> [docs/testlog.md](docs/testlog.md) for what has actually been run and
> [docs/design.md](docs/design.md) for the plan.

## How it fits into Explorer

- The items live in the classic context menu. On Windows 11 that's
  **Show more options** (or Shift+F10).
- Robo-Copy and Robo-Cut place files on the normal Windows clipboard, so a plain Ctrl+V works
  afterwards. Robo-Paste accepts files copied with a plain Ctrl+C.
- Install is per-user: no administrator rights, and no Explorer settings changed.

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

## License

MIT
