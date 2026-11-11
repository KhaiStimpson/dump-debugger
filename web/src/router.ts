import { useSyncExternalStore } from 'react';

export type View =
  'home' | 'analyzing' | 'diagnosis' | 'threads' | 'locks' | 'memory' | 'objects' | 'compare' | 'report';

export const views: View[] = [
  'home',
  'analyzing',
  'diagnosis',
  'threads',
  'locks',
  'memory',
  'objects',
  'compare',
  'report',
];

export interface Route {
  view: View;
  /** Optional view argument, e.g. #/objects/0x1234 or #/memory/System.Byte[] */
  arg?: string;
}

function parse(): Route {
  const [, v, ...rest] = location.hash.replace(/^#/, '').split('/');
  const view = (views as string[]).includes(v) ? (v as View) : 'home';
  const arg = rest.length ? decodeURIComponent(rest.join('/')) : undefined;
  return { view, arg };
}

let current = parse();
const listeners = new Set<() => void>();
window.addEventListener('hashchange', () => {
  current = parse();
  listeners.forEach((l) => l());
});

export function navigate(view: View, arg?: string): void {
  const next = `#/${view}${arg ? `/${encodeURIComponent(arg)}` : ''}`;
  if (location.hash !== next) location.hash = next;
}

export function useRoute(): Route {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => current,
  );
}
