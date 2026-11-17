import { create } from 'zustand';
import { rpc } from '../rpc/client';
import type { ThemeName } from '../styles/tokens';

export type ThemePreference = 'system' | 'light' | 'dark';

export const themeLabels: Record<ThemePreference, string> = { system: 'System', light: 'Light', dark: 'Dark' };

const KEY = 'dumpdebugger.theme';

const systemQuery = () =>
  typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;

function readPreference(): ThemePreference {
  try {
    const saved = globalThis.localStorage?.getItem(KEY);
    if (saved === 'system' || saved === 'light' || saved === 'dark') return saved;
  } catch {
    // Storage can be unavailable; fall back to following the system.
  }
  return 'system';
}

/** What Windows is set to; dark when it can't be read. */
export const systemTheme = (): ThemeName => (systemQuery()?.matches ? 'light' : 'dark');

const resolve = (p: ThemePreference): ThemeName => (p === 'system' ? systemTheme() : p);

interface ThemeState {
  preference: ThemePreference;
  resolved: ThemeName;
  setPreference(p: ThemePreference): void;
}

/** Light, dark or follow Windows. The preference lives in localStorage; tokens.css does the rest. */
export const useTheme = create<ThemeState>((set) => ({
  preference: readPreference(),
  resolved: resolve(readPreference()),
  setPreference(preference) {
    try {
      globalThis.localStorage?.setItem(KEY, preference);
    } catch {
      // Not persisted; the choice still applies for this session.
    }
    set({ preference, resolved: resolve(preference) });
  },
}));

function apply(theme: ThemeName) {
  document.documentElement.dataset.theme = theme;
  if (rpc.connected) void rpc.call('window.setTheme', { theme }).catch(() => {});
}

/** Applies the theme before first render and keeps the page and host in step with it. */
export function initTheme() {
  apply(useTheme.getState().resolved);
  useTheme.subscribe((s, prev) => s.resolved !== prev.resolved && apply(s.resolved));
  systemQuery()?.addEventListener('change', () => {
    const { preference } = useTheme.getState();
    if (preference === 'system') useTheme.setState({ resolved: resolve('system') });
  });
}
