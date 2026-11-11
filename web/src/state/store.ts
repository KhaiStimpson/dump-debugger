import { create } from 'zustand';
import type { Diagnosis, DumpOpened, Pin, ProgressNotification, WorkerStatus } from '../contracts.gen';
import { RpcError, rpc } from '../rpc/client';
import { navigate } from '../router';
import { queryClient } from './queries';

interface InspectorState {
  history: string[];
  index: number;
  open: boolean;
}

interface AppState {
  dump: DumpOpened | null;
  diagnosis: Diagnosis | null;
  worker: WorkerStatus | null;
  progress: ProgressNotification | null;
  opening: string | null;
  error: string | null;
  pins: Pin[];
  inspector: InspectorState;
  paletteOpen: boolean;
  helpOpen: boolean;

  openDump(path: string, files?: File[]): Promise<void>;
  closeDump(): Promise<void>;
  inspect(uri: string): void;
  back(): void;
  forward(): void;
  closeInspector(): void;
  setPalette(open: boolean): void;
  setHelp(open: boolean): void;
  setPins(pins: Pin[]): void;
  clearError(): void;
}

export const useApp = create<AppState>((set, get) => ({
  dump: null,
  diagnosis: null,
  worker: null,
  progress: null,
  opening: null,
  error: null,
  pins: [],
  inspector: { history: [], index: -1, open: false },
  paletteOpen: false,
  helpOpen: false,

  async openDump(path, files) {
    set({ opening: path || files?.[0]?.name || 'dump', error: null, diagnosis: null, progress: null });
    navigate('analyzing');
    try {
      const opened = await rpc.call('dump.open', { path }, { files });
      queryClient.clear();
      set({ dump: opened, opening: null, inspector: { history: [], index: -1, open: false } });
      const state = await rpc.call('analysis.get', {});
      if (state.diagnosis) set({ diagnosis: state.diagnosis });
      if (opened.cachedAnalysis) navigate('diagnosis');
      rpc.call('pins.list', {}).then((pins) => set({ pins }));
    } catch (e) {
      set({ opening: null, error: e instanceof RpcError ? e.message : String(e) });
      navigate('home');
    }
  },

  async closeDump() {
    await rpc.call('analysis.cancel', {}).catch(() => {});
    await rpc.call('dump.close', {}).catch(() => {});
    queryClient.clear();
    set({ dump: null, diagnosis: null, pins: [], inspector: { history: [], index: -1, open: false } });
    navigate('home');
  },

  inspect(uri) {
    const { history, index } = get().inspector;
    if (history[index] === uri) {
      set({ inspector: { history, index, open: true } });
      return;
    }
    const next = [...history.slice(0, index + 1), uri].slice(-50);
    set({ inspector: { history: next, index: next.length - 1, open: true } });
  },
  back() {
    const i = get().inspector;
    if (i.index > 0) set({ inspector: { ...i, index: i.index - 1, open: true } });
  },
  forward() {
    const i = get().inspector;
    if (i.index < i.history.length - 1) set({ inspector: { ...i, index: i.index + 1, open: true } });
  },
  closeInspector() {
    set({ inspector: { ...get().inspector, open: false } });
  },
  setPalette(open) {
    set({ paletteOpen: open });
  },
  setHelp(open) {
    set({ helpOpen: open });
  },
  setPins(pins) {
    set({ pins });
  },
  clearError() {
    set({ error: null });
  },
}));

export function currentInspectorUri(): string | undefined {
  const { history, index, open } = useApp.getState().inspector;
  return open ? history[index] : undefined;
}

// Server-pushed state.
rpc.on('analysis.updated', (d) => {
  const wasComplete = useApp.getState().diagnosis?.complete;
  useApp.setState({ diagnosis: d });
  if (d.complete && !wasComplete) {
    queryClient.invalidateQueries();
    if (location.hash.startsWith('#/analyzing')) navigate('diagnosis');
  }
});
rpc.on('worker.status', (w) => useApp.setState({ worker: w }));
rpc.on('dump.progress', (p) => useApp.setState({ progress: p }));
