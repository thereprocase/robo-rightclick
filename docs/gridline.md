# Gridline in robo-rightclick

Every surface of the app uses the **Gridline** design language: the same system as the
project network at thereprocase.github.io. The source tokens are vendored in
`branding/gridline/` (from the Gridline Design System, 9 September 2026). This
document maps them onto WinForms. If this document and the tokens disagree, the
tokens win.

## Rules (non-negotiable)

- **Square corners everywhere.** Radius 0. No rounded buttons, no pills.
- **No gradients, no drop shadows, no glows, no emoji.** The only depth is the 1 px bevel
  on controls (below).
- **System Gray structure, white content fields, Active Blue titles and selection.**
- **Semantic color only.** Cyan = live / running / linked. Amber = attention or a
  qualification note. Red = failure or danger. Green = completed successfully. Never decorative.
- **Typography:** IBM Plex Sans for UI text and IBM Plex Mono for every measured value, caption,
  status line and path. Fonts are embedded (`src/RoboRightClick/Fonts/`, SIL OFL, licence
  file shipped alongside) and loaded with `PrivateFontCollection.AddMemoryFont`. Fallbacks are
  Segoe UI and Consolas if loading fails. Numbers use tabular figures (Plex Mono already has them).
- **Visible structure:** title strips, ruled rows, status bars. No hidden-until-hover controls.
- **Text states facts.** No slogans, taglines or marketing copy in any window, tooltip or toast.

## Tokens

| Token | Value | Use |
|---|---|---|
| `gl-blue` (active) | `#0000A8` | pane title strips, selected row, primary/default button border, focus |
| `gl-blue-deep` | `#000078` | pressed/hover on blue |
| `gl-blue-hover` | `#1414B8` | link hover |
| `gl-gray` (surface) | `#C6C6C6` | window background, button face, status bar |
| `gl-gray-light` (surface-2) | `#E8E8E8` | secondary panes, captions, hover |
| `gl-gray-lighter` | `#F2F2F2` | content area behind panes |
| `gl-white` (field) | `#FFFFFF` | list bodies, text fields, panes |
| `gl-ink` | `#101010` | body text, strong border |
| text-secondary | `#3D3D3D` | secondary text |
| text-disabled | `#7A7A7A` | disabled text |
| `gl-rule` | `#666666` | pane frames, row rules |
| `gl-rule-light` | `#9A9A9A` | light row separators |
| `gl-cyan` (live) | `#008EA1` | running state, progress fill, live counters |
| `gl-green` (positive) | `#18743A` | Done |
| `gl-yellow` (attention) | `#B87900` on `#FFF4DC` | AwaitingDecision, Paused, warnings, ephemeral notice |
| `gl-red` (danger) | `#B3261E` on `#FBEAE9` | Failed, errors, DoneWithErrors marker |
| bevel light / mid / dark / darker | `#FFFFFF` / `#DFDFDF` / `#8A8A8A` / `#4A4A4A` | control relief |

Spacing: 4 / 8 / 12 / 16 / 20 / 24 / 32 px. Row height 26 px (dense 24). Title strip 30 px.
Status bar 24 px. Toolbar 34 px. Sizes are logical pixels; scale with DPI (PerMonitorV2).

Type sizes: dense 12, UI 13, heading 18, title 24, measure 28 (big numbers). Captions use
Plex Mono 600, 12–13 px, UPPERCASE, tracking 0.04 em.

## Component mapping (WinForms)

- **Window:** keep the native Windows title bar and frame. The client area background is `gl-gray`.
  Content sits in **panes**: a 1 px `gl-rule` frame, a white body, and a 30 px **blue title strip**
  (`#0000A8`, white Plex Mono 600 13 px, padding 6/12). A pane for live records may use a cyan
  title strip with ink text.
- **Buttons:** square, `gl-gray` face, 1 px `gl-rule` border, raised bevel (inner top/left
  `#FFFFFF`, inner bottom/right `#8A8A8A`), pressed = inverted bevel. Plex Sans 500 13 px,
  minimum height 34, padding 6/12. The default button gets a 1 px `gl-blue` outer border.
  Disabled text is `#7A7A7A`. Focus is a 1 px dotted ink rectangle inset 3 px. Owner-draw these;
  don't use FlatStyle defaults that round corners or add system visual styles.
- **Text fields / numeric inputs / combo boxes:** white field, inset bevel (top/left `#8A8A8A`,
  bottom/right `#FFFFFF`), square.
- **Lists (Jobs register):** owner-drawn rows of 26 px, white background, 1 px `gl-rule-light`
  separators. Header row in `gl-gray` with Plex Mono 12 UPPERCASE captions and a `gl-rule` bottom
  border. Selected row is `gl-blue` background with white text. Numbers, sizes, speeds, ETAs and
  paths are in Plex Mono.
- **Progress bars:** square, 1 px `gl-rule` frame, white track, fill by state:
  cyan running, amber paused or awaiting decision, green done, red failed. No animation
  beyond value changes, no marquee shimmer.
- **State labels:** Plex Mono 600 UPPERCASE in the state color (`RUNNING` cyan, `PAUSED` amber,
  `DONE` green, `FAILED` red, `QUEUED`/`SCANNING`/`CANCELED` ink/secondary).
- **Caution strip** (warnings, ephemeral notice, prototype notes): `#FFF4DC` background,
  4 px `#B87900` left border, Plex Sans 13 px.
- **Status bar:** `gl-gray`, top border `gl-rule`, Plex Mono 12, cells separated by 1 px
  `gl-rule` vertical rules (for example `3 JOBS │ 1.2 GB/S │ ETA 4 MIN │ EPHEMERAL`).
- **Counts block** (Jobs window header): white boxes with a `gl-rule` frame. Caption in Plex Mono 12
  UPPERCASE, value in Plex Mono 500 28 px tabular.
- **Context menus (tray):** native menus are acceptable, but where the app owns the renderer
  (`ToolStripProfessionalRenderer` subclass), use square edges, a `gl-gray` background, `gl-blue`
  selection with white text, and no image-margin gradient.
- **Dialogs** (conflict, error summary, settings, confirmations) follow the same pane, button and
  field rules. The primary action is the default button.
- **Tray icon:** 16/20/24/32 px square glyph drawn at runtime: a gray `#C6C6C6` tile with a 1 px
  ink frame and a two-arrow copy mark in ink. State is shown by the tile color or a corner block,
  using only Gridline colors: idle gray, running cyan, paused amber, attention red. Ephemeral mode
  adds a distinct marker (for example an ink tile with white glyph) so it is never mistaken for
  normal mode. No anti-aliased gradients.
- **Toasts:** plain sentences, no emoji. Paths are allowed in normal mode and never in ephemeral mode.

## Implementation notes

- Put the tokens in one class (for example `Ui/Gridline.cs`: `static readonly Color Blue = …`,
  fonts, metrics) and reference them everywhere. Never use a literal color elsewhere.
- Embed the TTFs as `EmbeddedResource` and load them once at startup. Ship
  `LICENSE-IBM-Plex-OFL.txt` next to the exe (and mention it in the README).
- Respect DPI: compute pixel sizes from logical units with `DeviceDpi / 96f`.
- Contrast: white on `#0000A8` and ink on `#C6C6C6` both pass WCAG AA. Keep it that way.
