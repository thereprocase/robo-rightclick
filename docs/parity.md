# Explorer parity

What Explorer actually does for each situation, measured on Windows, and the
robocopy flags chosen to match it. Rows marked **pending M0** have not been
measured yet. Their current flags are best guesses, not verified behavior.

| Situation | Explorer (measured) | Robo default | Status |
|---|---|---|---|
| File data + attributes + modified time | | `/COPY:DAT` | pending M0 |
| Created time of copied files | | | pending M0 |
| Folder timestamps | | `/DCOPY:DAT` | pending M0 |
| Alternate data streams (e.g. Zone.Identifier) | | | pending M0 |
| Hidden / system files | | copied (robocopy default) | pending M0 |
| Read-only attribute | | | pending M0 |
| Junctions and symlinks inside a copied folder | | | pending M0 |
| Paths longer than 260 characters | | | pending M0 |
| Empty folders | | `/E` | pending M0 |
| Icon ghosting after Robo-Cut | | | pending M0 |

## Known deliberate deviations

- **Per-file errors** are collected and offered as "Try again / Skip" at the end of the job,
  rather than interrupting mid-copy. Robocopy can't pause on an error and wait for input.
