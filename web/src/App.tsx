import { QueryClientProvider } from '@tanstack/react-query';
import { lazy, Suspense, useEffect } from 'react';
import { ContextMenuProvider } from './components/ContextMenu';
import { refFor } from './entities';
import { Inspector } from './inspector/Inspector';
import { rpc } from './rpc/client';
import { navigate, useRoute, type View } from './router';
import { Help } from './shell/Help';
import { Palette } from './shell/Palette';
import { Rail } from './shell/Rail';
import { StatusBar } from './shell/StatusBar';
import { Titlebar } from './shell/Titlebar';
import { usePins } from './state/pins';
import { queryClient } from './state/queries';
import { useApp } from './state/store';
import { Analyzing } from './views/Analyzing';
import { Compare } from './views/Compare';
import { Diagnosis } from './views/Diagnosis';
import { Home } from './views/Home';
import { Memory } from './views/Memory';
import { Objects } from './views/Objects';
import { Report } from './views/Report';
import { Threads } from './views/Threads';
import { Placeholder } from './views/Placeholder';

// React Flow + ELK are most of the bundle; load them when the Locks view is first opened.
const Locks = lazy(() => import('./views/Locks').then((m) => ({ default: m.Locks })));

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ContextMenuProvider>
        <Shell />
      </ContextMenuProvider>
    </QueryClientProvider>
  );
}

/** Views with their own docked inspector column; elsewhere it slides over the right edge. */
const docksInspector: View[] = ['diagnosis', 'threads'];

function Shell() {
  const { view } = useRoute();
  const dump = useApp((s) => s.dump);
  const inspectorOpen = useApp((s) => s.inspector.open);
  useGlobalKeys();

  useEffect(() => {
    rpc.call('app.info', {}).then((info) => {
      if (info.startupDumpPath) void useApp.getState().openDump(info.startupDumpPath);
    });
  }, []);

  // Dump-bound views make no sense without a dump: send the user home.
  useEffect(() => {
    if (!dump && !['home', 'analyzing', 'compare'].includes(view) && !useApp.getState().opening) navigate('home');
  }, [dump, view]);

  return (
    <div className="win">
      <Titlebar />
      <div className="body">
        <Rail />
        <main className="main" id="main">
          <h1 className="sr-only">Dump Debugger · {view}</h1>
          <CurrentView view={view} />
          {inspectorOpen && !docksInspector.includes(view) && <Inspector docked />}
          <Palette />
          <Help />
        </main>
      </div>
      <StatusBar />
    </div>
  );
}

function CurrentView({ view }: { view: View }) {
  switch (view) {
    case 'home':
      return <Home />;
    case 'analyzing':
      return <Analyzing />;
    case 'diagnosis':
      return <Diagnosis />;
    case 'threads':
      return <Threads />;
    case 'locks':
      return (
        <Suspense fallback={<div className="view on" />}>
          <Locks />
        </Suspense>
      );
    case 'memory':
      return <Memory />;
    case 'objects':
      return <Objects />;
    case 'report':
      return <Report />;
    case 'compare':
      return <Compare />;
    default:
      return <Placeholder view={view} />;
  }
}

const chords: Record<string, View> = {
  h: 'home',
  d: 'diagnosis',
  t: 'threads',
  l: 'locks',
  m: 'memory',
  o: 'objects',
  c: 'compare',
  r: 'report',
};

/** Keyboard parity (design system): Ctrl+K, G then a view letter, P pins, ? lists everything. */
function useGlobalKeys() {
  const pins = usePins();
  useEffect(() => {
    let pendingG = 0;
    const onKey = (e: KeyboardEvent) => {
      const s = useApp.getState();
      const typing = e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        s.setPalette(!s.paletteOpen);
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'o') {
        e.preventDefault();
        void rpc.call('dump.pick', {}).then((p) => {
          if (p.path) void s.openDump(p.path);
        });
        return;
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'e' && s.dump) {
        e.preventDefault();
        navigate('report');
        return;
      }
      if (e.key === 'Escape') {
        if (s.paletteOpen) s.setPalette(false);
        else if (s.helpOpen) s.setHelp(false);
        else if (s.inspector.open) s.closeInspector();
        return;
      }
      if (typing || e.ctrlKey || e.altKey || e.metaKey) return;
      if (e.key === '?') {
        s.setHelp(!s.helpOpen);
        return;
      }
      if (e.key.toLowerCase() === 'g') {
        pendingG = Date.now();
        return;
      }
      if (Date.now() - pendingG < 1200 && chords[e.key.toLowerCase()]) {
        pendingG = 0;
        navigate(chords[e.key.toLowerCase()]);
        return;
      }
      if (e.key.toLowerCase() === 'p' && s.dump) {
        const current = s.inspector.open ? s.inspector.history[s.inspector.index] : undefined;
        if (current) {
          if (pins.isPinned(current)) void pins.unpin(current);
          else
            void pins.pin(
              refFor(
                current,
                s.diagnosis?.findings.flatMap((f) => f.evidence),
              ),
            );
        }
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [pins]);
}
