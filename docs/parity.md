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
| Icon ghosting after Robo-Cut | Ctrl+X dims the icon at once | not dimmed, also after F5 | **no**: see deviations |

Not yet measured: cross-volume move (needs a second volume, M4) and conflict prompts (driven
through the real UI in M4).

## Menu and clipboard

Measured on the same VM in the activation session of 2026-10-02 (docs/testlog.md), with the
real classic menu and Explorer's own Ctrl+C, Ctrl+X and Ctrl+V.

| Situation | Explorer (measured) | Robo (measured) | Match |
|---|---|---|---|
| Where the items appear | classic Cut/Copy/Paste under "Show more options"; Shift+F10 opens the new menu, whose "Show more options" (W) opens the classic one | same place, same route | yes |
| Menu icons | none on the classic Cut, Copy, Paste | none | yes |
| Preferred DropEffect of a copy | 5 (copy and link) | 1 (copy) | equivalent: both paste as a copy, in both directions |
| Copy, then Explorer's Ctrl+V into the folder it came from | "name - Copy" | "name - Copy" (Robo-Copy writes the Shell IDList Array beside CF_HDROP) | yes |
| Cut, then Explorer's Ctrl+V | moves; clipboard emptied | moves; the cut stays on the clipboard | **no**: see deviations |
| Explorer's Ctrl+C or Ctrl+X, then a paste | Ctrl+V copies, or moves and empties the clipboard | Robo-Paste copies, or moves and empties the clipboard | yes |
| Cut-paste while the clipboard changed meanwhile | not measured | the newer clipboard is kept | not measured |
| Paste offered on a selection of two folders | yes; pastes into the right-clicked folder only | Robo-Paste not offered | **no**: see deviations |
| Paste on a folder background, a folder, a drive | into the open folder, that folder, the drive root | same | yes |

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
- **Robo-Cut does not dim the cut items' icons.** Explorer dimmed the icon for its own Ctrl+X;
  after a Robo-Cut (Preferred DropEffect 2, CF_HDROP, with or without a Shell IDList Array)
  the icon stayed normal, also after a refresh (M0 spike 5). The cut is still a cut:
  Explorer's Ctrl+V and Robo-Paste both move.
- **A Robo-Cut pasted with Explorer's Ctrl+V stays on the clipboard.** Explorer's own cut is
  emptied from the clipboard after the paste; a Robo-Cut is not. The likely reason (not
  measured): the shell reports a finished move back to the data object that offered it
  ("Paste Succeeded"), and Robo-Cut writes plain clipboard data with no object behind it to
  receive that report. Pasting the stale cut a second time finds the
  sources gone. Robo-Paste of a cut does empty the clipboard, as Explorer does.
- **No Robo-Paste on a selection of several folders.** Explorer offers Paste there and pastes
  into the folder that was right-clicked. The shell does not tell a DelegateExecute verb which
  item was clicked, so the item is hidden (`MultiSelectModel=Single`) rather than guessing.

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
