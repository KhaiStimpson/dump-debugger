// Design tokens mirrored from tokens.css for SVG/canvas/graph rendering, where CSS variables
// can't always be used (e.g. React Flow edge markers, exported reports).
export const colors = {
  bg0: '#0b0d10',
  bg1: '#111419',
  bg2: '#171b21',
  bg3: '#1e232b',
  bg4: '#262c35',
  line: '#252a32',
  line2: '#323944',
  fg0: '#eef0f3',
  fg1: '#c5cad3',
  fg2: '#8b93a1',
  fg3: '#7c8491',
  accent: '#2dd4bf',
  accentDim: '#2dd4bf22',
  accentInk: '#04201c',
  crit: '#f2555a',
  critDim: '#f2555a1f',
  warn: '#f5a524',
  warnDim: '#f5a5241f',
  info: '#60a5fa',
  infoDim: '#60a5fa1f',
  ok: '#3ccf91',
  okDim: '#3ccf911f',
  code: '#d6b4ff',
} as const;

export const fonts = {
  sans: '"Segoe UI Variable Text","Segoe UI Variable","Inter","Segoe UI",system-ui,sans-serif',
  mono: '"Cascadia Code","JetBrains Mono","Consolas",ui-monospace,monospace',
} as const;
