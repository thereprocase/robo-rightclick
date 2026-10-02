# Test log (append-only)

Format: date · Windows build · machine (VM/physical) · what ran · observed result.

## 2026-10-02 · build 26200 (robocopy 10.0.26100.1) · VM · M0 spikes 2, 3, 4

Disposable Windows 11 Enterprise evaluation VM, no network egress. Scripts in `spikes/m0/`,
raw results in `spikes/m0/results/`.

**Spike 2: robocopy output channel** (`robocopy-capture.ps1`, `robocopy-pipe.ps1`)
- Redirected stdout with `/UNICODE` is **not** Unicode. It's a UTF-16 BOM (`FF FE`) followed
  by narrow single-byte text. `📁` and CJK names become `?`. Without `/UNICODE`, stdout is
  code page 437 (`ü` → `0x81`), and CJK is lost the same way. **Stdout can't carry filenames.**
- `/UNILOG:\\.\pipe\<name>` works. Robocopy connects to a named pipe the reader owns and
  writes true UTF-16LE with a BOM. Emoji, CJK and Latin-1 names arrive exactly. Stdout carries
  only a 36-byte header. No log file appeared on disk (searched the work dir and `%TEMP%`).
- Under `/MT:32`, each file's line arrives **when the file finishes**. Three 512 MB files
  reported at about 3.08 s, matching the I/O counters reaching the full 1.61 GB.
- A failing file (locked by another process, error 32) still gets its file line **first**,
  immediately followed by the ERROR line, the message line, a blank line, and
  `ERROR: RETRY LIMIT EXCEEDED.` Exit code 9.
- Destination files are **allocated at full length** before data is written (all 3 × 512 MB
  were full length at the first 100 ms poll). Destination size can't measure progress.
  The process's `GetProcessIoCounters` read bytes track progress continuously.
- Line format with `/NP /NDL /NC /NJH /NJS /BYTES /FP`: `\t  \t\t<size right-aligned>\t<full path>`.
  Captured pipe output is committed as test fixtures (`tests/.../Fixtures/*.utf16`).

**Spike 3: pause** (`suspend.ps1`, 6 × 768 MB, `/MT:32`)
- `NtSuspendProcess` returned 0. The suspend landed mid-copy (1.73 GB of about 9.7 GB
  read+write done). I/O counters moved **0 bytes** during 5 s suspended.
  `NtResumeProcess` returned 0. Exit code 1 (success), and total destination size equals source.

**Spike 4: Explorer parity** (`parity.ps1`, Explorer copy via `Shell.Application.CopyHere`
on the interactive desktop through a scheduled task)
- Results in docs/parity.md. Candidate flags `/COPY:DAT /DCOPY:DA /A+:A /XJD` re-run and
  diffed against Explorer: matches on attributes, archive bit, streams, file symlink content,
  folder created time, and not following junctions or folder symlinks. Remaining deviations
  are listed in parity.md.

**Not yet run:** spike 1 (DelegateExecute multi-select into a running COM server) and
spike 5 (icon ghosting after Robo-Cut). Both need the host's COM server.

**VM housekeeping:** the test account's password had expired (2026-09-21), which broke
autologon and left the VM at the sign-in screen. The account was set to
`PasswordNeverExpires`. The password itself was not changed.
