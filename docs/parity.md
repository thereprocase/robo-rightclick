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
| Menu icons | none on the classic Cut, Copy, Paste | none on the builds measured; an icon per item since commit e13fa37, not yet observed | **no** (from e13fa37): see deviations |
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
- **The Robo items have menu icons.** Explorer's classic Cut, Copy and Paste have none
  (measured). Since commit e13fa37 each Robo item's registry key has an `Icon` value, so the
  items stand out from Explorer's own Cut, Copy and Paste beside them. Whether Explorer
  displays them is not yet verified on Windows.
- **A file whose name starts with `-` is refused wherever robocopy would have to name it**;
  the rest of the selection runs, and the job lists the file under Refused with the reason.
  Robocopy reads such an argument as a switch even when quoted (measured: a file called `-E`
  given as a file filter switched on `/E`), so a file called `-MOV` or `-S` would change what
  the run does. That covers a selected file (a copy, or a cut between drives), a file inside a
  folder that is copied file by file because a conflict in that folder was skipped or kept both
  ways, "Try again" for such a file, and a cut within one drive whose rename turned out to cross
  volumes (a mount point, or two shares of one NAS) and falls back to robocopy. A folder whose
  name starts with `-`, a file inside a folder robocopy copies whole, and a cut within one drive
  that renames are not affected. Explorer pastes these files; use it for them.
- **No Robo-Paste on a selection of several folders.** Explorer offers Paste there and pastes
  into the folder that was right-clicked. The shell does not tell a DelegateExecute verb which
  item was clicked, so the item is hidden (`MultiSelectModel=Single`) rather than guessing.

Design deviations (decided, not yet measured against Explorer):

- **Robo-Paste hotkey (Ctrl+Shift+V by default).** An addition: Explorer has no second paste
  key. It is taken only in a folder's file list and on the desktop, where Explorer itself is
  expected to do nothing with Ctrl+Shift+V (not yet measured, docs/decisions/0001-paste-hotkey.md
  release gate 1); in text fields it passes, so Explorer's paste-as-plain-text keeps working.
- **The hotkey refuses libraries.** Explorer's Ctrl+V in a library pastes into the library's
  default save location. The hotkey refuses every library, as the right-click Robo-Paste
  does (a library has no file-system path), with the "Can't Robo-Paste here" toast. Inside a
  library, a real folder opened from it works.
- **The hotkey passes in the navigation pane.** Explorer's Ctrl+V with focus on a folder in
  the navigation pane pastes into that folder. The hotkey is taken only in the file list,
  so there it passes and nothing happens; click in the file list first.
- **A paste right after Ctrl+C or Ctrl+X can wait or be refused.** The hook sees the hotkey
  before Explorer has necessarily written a copy or cut made just before it. Robo-Paste then
  waits up to 1 s for the clipboard to change and otherwise refuses ("Clipboard not ready")
  rather than paste the previous clipboard. Explorer's own Ctrl+V has no such race.
- **The hotkey refuses zip folders.** A zip opened as a folder is a file, not a file-system
  folder. Explorer can paste into it; the hotkey refuses it up front ("Can't Robo-Paste
  here") rather than start a job that would fail.

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
- **At most 250,000 items per click.** Robo-Copy, Robo-Cut and Robo-Paste refuse a larger
  selection (or one whose paths add up to more than 64 MiB) with a toast that states the
  limit, so Robo-Copy never writes more to the clipboard than Robo-Paste reads back. Explorer's
  own Copy has no such limit. Selecting the parent folder copies the same files.
- **"Keep newer" also keeps a destination with the same time but a different size.**
  (`/XC /XO`). Explorer has no such option; it exists only as a configured default.
- **A retried cut can leave empty source folders.** After "Try again" moves the files that
  failed the first time, their now-empty source folders stay. Removing them would be an
  app-initiated deletion in the source tree, which needs an ADR under invariant 1.
