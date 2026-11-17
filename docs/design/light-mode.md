# Light mode

Approved 2026-11-18: variant **A · Paper** from `docs/design/light-mode.html`, with the theme
control in the status bar. The HTML is reference material for proportion and behaviour, not
markup to copy.

## Hierarchy

Unchanged from dark. The grey ladder is inverted: `--bg-0` is the white working surface, and each
step up is darker (`--bg-2` chrome, `--bg-3` controls, `--bg-4` active). Colour still only carries
meaning: critical, warning, healthy, plus one accent for links, focus and the primary action.

Selection is pale blue (`--sel` #d6e6f8) with a 2px accent edge, not a solid fill. Text in a
selected row stays dark and the Blocked and Waiting states keep their colour. Red and amber are
darker than a straight inversion would give (#b42318, #8a5700) so they pass AA on both white and
the selection.

## Mechanism

- `tokens.css`: dark stays on `:root`. `:root[data-theme='light']` overrides the same tokens.
  Each block sets `color-scheme`.
- `web/src/state/theme.ts`: the preference is System, Light or Dark, saved in `localStorage`.
  System follows `prefers-color-scheme`. It is applied before first render to avoid a flash.
- The host is told the resolved theme (`window.setTheme`) so the native caption buttons, the
  WebView background and native dialogs match.

## New tokens

These replace colours that were hardcoded in `app.css`, `debug.css` and TSX:
`--sel-fg`, `--sel-crit`, `--sel-warn`, `--sel-edge`, `--val-str`, `--val-num`, `--tm-fg`,
`--tm-1`…`--tm-9` (the treemap ramp from `Memory.tsx`), `--shadow`, `--scrim`.

`web/src/styles/tokens.ts`: `colors` now holds `var(--…)` references, so SVG and React Flow
follow the theme live. `palettes.dark` / `palettes.light` hold the hex values for the exported
report, which uses the theme that was active at export time.

## Components

Reused: `.btn.ghost` (status bar trigger), `.ctxmenu` (the menu, opened upwards), and the
palette's existing Commands group (`Palette.tsx`).

New: `ThemeMenu` in `web/src/shell/StatusBar.tsx`. Nothing existing was an anchored menu that
opens from a button: `ContextMenu` is positioned at the pointer and tied to entities.

## Rejected

- **B, dark chrome.** Two palettes live at once, so every chrome component needs to work in both.
- **C, cool slate with solid selection.** The tint goes against "colour only for meaning", and
  the solid selection hides the Blocked and Waiting colours.
- **Toggle in the title bar.** It's a drag region and there's no room next to the caption buttons.
- **Toggle in the rail.** The rail only lists views.
- **A Settings view.** Too heavy for one preference.
