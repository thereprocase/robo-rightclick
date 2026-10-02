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

Design deviations (decided, not yet measured against Explorer):

- **Overlapping pastes run one after another.** Explorer runs every paste at once and asks
  about each conflict as it writes. Robo-Paste asks once, after its scan, so a second paste
  whose files overlap where another running paste writes waits until that one finishes;
  otherwise its scan would miss files about to appear and a copy could overwrite files a cut
  had just moved. Pastes into unrelated folders still run in parallel.
- **No keep-both for a cut between drives.** It would need the app itself to delete the source
  after copying, which invariant 1 rules out until an ADR exists. Keep-both works for copies
  and for cuts within one drive (a rename). The conflict dialog says why the option is missing.
- **A selected folder link (junction or directory symlink) is only moved within a drive.**
  Robocopy follows a link given as its source root, so a cross-drive move would empty the
  link's target. Copying a selected link, or moving it to another drive, is refused with a
  message pointing to Explorer's Paste. Explorer's own behavior here is not yet measured.
- **Files that appear at the destination after the scan are skipped, not overwritten.**
  Explorer would ask about them; Robo-Paste reports them in the job summary.
- **"Keep newer" also keeps a destination with the same time but a different size.**
  (`/XC /XO`). Explorer has no such option; it exists only as a configured default.
- **A retried cut can leave empty source folders.** After "Try again" moves the files that
  failed the first time, their now-empty source folders stay. Removing them would be an
  app-initiated deletion in the source tree, which needs an ADR under invariant 1.
