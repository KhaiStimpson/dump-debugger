// Design tokens mirrored from tokens.css for SVG/canvas/graph rendering, where CSS variables
// can't always be used (e.g. React Flow edge markers, exported reports).
export const colors = {
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
} as const;

export const fonts = {
  sans: '"Segoe UI Variable Text","Segoe UI Variable","Inter","Segoe UI",system-ui,sans-serif',
  mono: '"Cascadia Code","JetBrains Mono","Consolas",ui-monospace,monospace',
} as const;
