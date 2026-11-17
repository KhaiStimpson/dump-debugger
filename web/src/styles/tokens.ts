// Design tokens for code that renders colour outside a stylesheet.
// `colors` are CSS variable references, so SVG, inline styles and React Flow edges follow the
// active theme. `palettes` mirror tokens.css as hex for output that leaves the page (the
// exported report); keep both in sync with tokens.css.

export type ThemeName = 'dark' | 'light';

const dark = {
  bg0: '#1e1e1e',
  bg1: '#252525',
  bg2: '#2b2b2b',
  bg3: '#333333',
  bg4: '#444444',
  line: '#3c3c3c',
  line2: '#555555',
  fg0: '#f2f2f2',
  fg1: '#dcdcdc',
  fg2: '#c4c4c4',
  fg3: '#9e9e9e',
  accent: '#7cc0ff',
  accentDim: '#7cc0ff22',
  accentInk: '#06263f',
  sel: '#0a4a78',
  crit: '#ff7b72',
  critDim: '#ff7b721f',
  warn: '#e9b44c',
  warnDim: '#e9b44c1f',
  info: '#7cc0ff',
  infoDim: '#7cc0ff1f',
  ok: '#6fcf97',
  okDim: '#6fcf971f',
  code: '#d6b4ff',
};

type Palette = typeof dark;

const light: Palette = {
  bg0: '#ffffff',
  bg1: '#fafafa',
  bg2: '#f3f3f3',
  bg3: '#e8e8e8',
  bg4: '#dcdcdc',
  line: '#e1e1e1',
  line2: '#c6c6c6',
  fg0: '#1b1b1b',
  fg1: '#2f2f2f',
  fg2: '#4d4d4d',
  fg3: '#6b6b6b',
  accent: '#0a64c8',
  accentDim: '#0a64c81a',
  accentInk: '#ffffff',
  sel: '#d6e6f8',
  crit: '#b42318',
  critDim: '#b4231814',
  warn: '#8a5700',
  warnDim: '#8a570014',
  info: '#0a64c8',
  infoDim: '#0a64c814',
  ok: '#1d7a3a',
  okDim: '#1d7a3a14',
  code: '#7a3fc4',
};

export const palettes: Record<ThemeName, Palette> = { dark, light };

const cssVar = (key: string) =>
  `var(--${key
    .replace(/([A-Z])/g, '-$1')
    .replace(/(\d)/, '-$1')
    .toLowerCase()})`;

/** `var(--…)` references for every token: `colors.critDim` is `var(--crit-dim)`. */
export const colors = Object.fromEntries(Object.keys(dark).map((k) => [k, cssVar(k)])) as Record<keyof Palette, string>;

export const fonts = {
  sans: '"Segoe UI Variable Text","Segoe UI Variable","Inter","Segoe UI",system-ui,sans-serif',
  mono: '"Cascadia Code","JetBrains Mono","Consolas",ui-monospace,monospace',
} as const;
