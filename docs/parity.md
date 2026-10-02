# Explorer parity

What Explorer actually does, measured on Windows, and the robocopy flags chosen
to match it. Source of every "measured" cell: the M0 parity capture on
2026-10-02 (docs/testlog.md). Explorer copied a test tree with
`Shell.Application.CopyHere` (Explorer's own copy engine) on the interactive
desktop. Robocopy copied the same tree with the app's flags. Both copies were
diffed field by field (`spikes/m0/parity.ps1`, results in `spikes/m0/results/`).

Robocopy flags: `/COPY:DAT /DCOPY:DA /A+:A /XJD` (plus `/MT:32 /R:0 /W:0` and the output flags).

| Situation | Explorer (measured) | Robo default (measured) | Match |
|---|---|---|---|
| File data, size | copied | copied | yes |
| File modified time | kept | kept | yes |
| File attributes (read-only, hidden, system) | kept | kept | yes |
| Archive attribute | **set** on every copy | set (`/A+:A`) | yes |
| Alternate data streams (`Zone.Identifier`, custom) | kept | kept | yes |
| File created time | **reset** to copy time | kept from source | **no**: see deviations |
| New folder created time | copy time | copy time (`/DCOPY:DA`) | yes |
| New folder modified time | copy time | kept from source | **no**: see deviations |
| Empty folders | copied | copied (`/E`) | yes |
| File symlink | copied as a regular file holding the target's content | same | yes |
| Junction / directory symlink | **empty folder**, link not followed | not followed (`/XJD`); no folder created | **partly**: see deviations |
| Path longer than 260 characters (342 tested) | silently skipped beyond about 200 characters (with no-error-UI flag) | copied | robo is better |
| Icon ghosting after Robo-Cut | | | pending (needs the host) |

Not yet measured: cross-volume move (needs a second volume, M4) and conflict prompts (driven
through the real UI in M4).

## Deviations

- **File created time is kept from the source.** Explorer resets it. Robocopy's `T` copies
  created and modified time together, and dropping it would also lose the modified time,
  which Explorer keeps. Keeping the original created time is the lesser difference.
- **Folder modified time is kept from the source.** Robocopy restores it even with `/DCOPY:DA`.
- **Folder links leave no folder at the destination.** Explorer leaves an empty folder with
  the link's name. Planned: the host recreates those empty folders during `Finalizing` from
  what the scan found.
- **Per-file errors** are collected and offered as "Try again / Skip" at the end of the job,
  rather than interrupting mid-copy. Robocopy can't pause on an error and wait for input.
- **Long paths are copied.** Explorer skipped them in the measured run. This deviation is an
  improvement and is kept on purpose.
