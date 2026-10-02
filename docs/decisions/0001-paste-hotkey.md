# ADR 0001: Robo-Paste hotkey through a scoped low-level keyboard hook

**Status:** accepted 2026-10-02. Implemented; cross-compiles; unverified on Windows. Every
statement below about Windows behavior is LIKELY or UNKNOWN until the release gate at the end
has a dated entry in docs/testlog.md.

## Context

Robo-Paste is reached through the classic context menu only: right-click, Show more options,
Robo-Paste. A keyboard shortcut in File Explorer's file list and on the desktop removes two
clicks from the most frequent action. Windows offers two ways to get a key combination
system-wide:

- **RegisterHotKey.** The combination belongs to the app everywhere, whatever has focus. In
  File Explorer's search box, address bar and rename box, Ctrl+Shift+V is "paste as plain
  text"; a registered hotkey would take it there too. That breaks invariant 3 (defaults
  match Explorer).
- **A low-level keyboard hook (WH_KEYBOARD_LL).** The app sees every key on the desktop and
  decides per key whether to take it. It can let the combination through in text fields. The
  cost is that the process sees keystrokes at all: a hook is what a key logger uses.

## Decision

Use the low-level hook, and bound it so that it cannot become a key logger by accident or by
a later change:

1. **Scope in time.** The keyboard hook exists only while File Explorer (`CabinetWClass`) or
   the desktop (`Progman`, `WorkerW`) is the foreground window. A foreground WinEvent hook
   installs and removes it. With `"pasteHotkey": ""`, or an invalid value, neither hook is
   installed.
2. **Scope in place.** The key is taken only when keyboard focus is in a folder's item list:
   a `DirectUIHWND` (File Explorer) or `SysListView32` (desktop) whose parent is
   `SHELLDLL_DefView`, in the foreground window, with no caret and no menu or move/size mode.
   This is an allow-list (Core `HotkeyGate.Decide`). An `Edit`-class deny-list would miss the
   Windows 11 address bar and search box, which are XAML (LIKELY).
3. **Scope in code.** `App/PasteHotkey.cs` and `App/KeyboardHookNative.cs` are the only files
   that may name `SetWindowsHookEx`, `KBDLLHOOKSTRUCT` or `GetAsyncKeyState`, and neither may
   reference a log, a file, the console or a job sink. `scripts/test.sh` enforces both rules.
4. **No key data kept.** The callback keeps the state of one latch (Idle or Swallowing and the
   tick of the last key-down of the configured key) and, for the clipboard guard below, one
   tick and one clipboard sequence number. It never records which keys were pressed, and
   nothing about the hotkey is logged, in either logging mode.
5. **One press, one paste.** The hook has no repeat flag. Core `HotkeyLatch` takes a held
   combination's repeats without triggering again, and a key-down that fails the gate resets
   it and passes, so a lost key-up can never keep eating the key.
6. **No guessing the target.** The hook captures the focused list's `ShellTabWindowClass`
   ancestor at the press. The locator pastes into the one ShellWindows entry whose
   `IShellBrowser::GetWindow` is that tab, in a window owned by `%SystemRoot%\explorer.exe`,
   or refuses. It never falls back to another tab. Tab visibility is not used; community
   reports say every tab claims to be visible.
7. **Same paste path.** The folder goes to `IVerbHandler.Invoke` from a UI-thread post: the
   dispatcher FIFO, `PasteDestinationRefusal`, `PathPolicy`, the clipboard checks and the job
   manager are those of a right-click. Libraries, This PC, search results, Control Panel and
   zip folders are refused, as the right-click refuses them.

### The Ctrl+C / Ctrl+X timestamp (clipboard guard)

The hook sees a key before File Explorer does. After a fast Ctrl+X then the hotkey, while
Explorer is busy (thumbnails, a slow share), Robo-Paste can read the clipboard before
Explorer has written the cut, and paste the *previous* clipboard. For a previous cut that
moves the wrong files, silently.

Estimate: perhaps 1 hotkey paste in 10-50 follows a Ctrl+C or Ctrl+X within 300 ms; Explorer
is busy for perhaps 1-5% of those; so roughly 1 paste in 1,000-5,000 is exposed. Rare, but the
bad case is a silent move of the wrong files.

So the hook also notes when a Ctrl+C or Ctrl+X (no Shift, Alt or Windows key) goes to an
Explorer view the gate accepts: one tick and `GetClipboardSequenceNumber` at that moment. It
stores no key and never takes or alters those keys. When such a copy or cut came within 2 s
before the hotkey, the locator waits, up to 1 s after the hotkey, for the sequence number to
move past the noted one, and otherwise refuses with a path-free "Clipboard not ready" toast
(Core `ClipboardGuard`). A Ctrl+C with nothing selected writes nothing, so a hotkey right
after it is refused once; that is the price of never pasting a stale cut.

Ctrl+Insert and a Copy or Cut chosen from a menu are not noted; the race needs a keyboard
copy or cut within 2 s of the hotkey, and those paths are far less common. They remain a
known gap.

### Invalid means off

Every other config field falls back to its default when invalid. `pasteHotkey` falls back
to off, with a tray and Settings warning naming the field: the default would switch on a
keyboard hook that the user may have been trying to switch off.

## Consequences

- Product invariants: 1 and 4 are untouched (the hotkey deletes nothing and writes no
  registry key). 2: no hotkey data reaches disk; toasts are path-free. 3: the combination
  passes untouched everywhere except a folder's item list, where Explorer itself does
  nothing with Ctrl+Shift+V (not yet measured: release gate 1). docs/parity.md lists the
  hotkey's deviations.
- The hotkey needs the tray running; a right-click starts the tray, a hotkey cannot.
- With focus in the navigation pane the key passes (Explorer's Ctrl+V would paste into the
  selected tree folder); click in the file list first.
- Ctrl+Z does not undo a Robo-Paste.
- In File Explorer, the hook sees the combination before another app's RegisterHotKey for
  it would. Settings probes for such a registration and notes it.
- Existing installs get the hotkey on by default after upgrading (a config without the field
  means the default); the first-run hint after the install names it.
- Optional copy and cut hotkeys are not part of v1 (design.md, after the beta).

## Release gate (Windows build 26200, dated testlog entries)

1. With the tray off, Ctrl+Shift+V in a folder view does nothing natively. If Explorer pastes,
   record it and reconsider the default.
2. `IShellBrowser::GetWindow` returns each tab's `ShellTabWindowClass` window (3 tabs across
   2 windows); a paste goes to the active tab only; a tab switch during an artificial delay
   refuses rather than redirects.
3. The key reaches the focused control, and Robo does nothing, in the search box, the address
   bar in edit mode, the rename box, the navigation pane, Home and Gallery, an Open/Save
   dialog, Robo's own windows and Notepad.
4. Holding the combination for 2 s gives exactly one job; a double tap gives one; a lost key-up
   (lock the session mid-press) does not eat later V keys anywhere.
5. Each of these refuses with a path-free toast and starts no job: This PC, Recycle Bin, the
   Documents library, search results, a zip root, inside a zip, Control Panel.
6. The desktop paste lands in `FOLDERID_Desktop`, the same folder as the desktop background
   right-click's `SetDirectory`, including with a redirected Desktop.
7. Ctrl+X then Ctrl+Shift+V within 100 ms while Explorer is busy pastes the new clipboard.
8. With `explorer.exe` suspended, typing elsewhere is not delayed, the toast appears after
   1.5 s, and no late paste follows.
9. After a forced hook stall, re-entering Explorer reinstalls the hook; the tray shows "not
   active" when the install fails.
10. With two keyboard layouts and Ctrl+Shift layout switching on, 10 presses leave the layout
    unchanged. If not, add a mask key and measure again.
11. An elevated File Explorer window: the hotkey does nothing and the tray does not crash (UIPI).
12. Ephemeral mode: hotkey pastes write nothing under `%APPDATA%`, `%LOCALAPPDATA%` or `%TEMP%`
    beyond what a right-click writes (`scripts/e2e/Ephemeral.Tests.ps1`).
13. Kill the tray: File Explorer behaves exactly as before install.
14. Classic-menu access letters measured for a file, a folder, a drive and the folder
    background; Y, U and B each run their item directly. The measured letters replace the
    unmeasured table in `RegistrationTests`.
15. COM calls from the locator's thread work under the process-wide
    `CoInitializeSecurity(IDENTIFY, PKT_PRIVACY)`. If not, set `CoSetProxyBlanket` per proxy.

`scripts/e2e/Hotkey.Tests.ps1` scripts parts of 2-5 and 13; the rest are manual.
