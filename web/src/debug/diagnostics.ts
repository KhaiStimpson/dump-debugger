import { create } from 'zustand';

export interface DebugEvent {
  sequence: number;
  at: number;
  source: string;
  stage: string;
  detail: string;
  error: boolean;
}

export interface PendingCall {
  id: string;
  method: string;
  started: number;
}

interface Diagnostics {
  visible: boolean;
  events: DebugEvent[];
  pending: PendingCall[];
  toggle(): void;
  clear(): void;
}

export const eventLimit = 300;
let sequence = 0;
export const useDiagnostics = create<Diagnostics>((set) => ({
  visible: false,
  events: [],
  pending: [],
  toggle: () => set((s) => ({ visible: !s.visible })),
  clear: () => set({ events: [] }),
}));

// Never retain RPC arguments, results, memory objects, or heap pages. This buffer stays local.
export function recordEvent(source: string, stage: string, detail = '', error = false) {
  const event: DebugEvent = {
    sequence: ++sequence,
    at: Date.now(),
    source,
    stage,
    detail: detail.slice(0, 4000),
    error,
  };
  useDiagnostics.setState((s) => ({ events: [...s.events.slice(-(eventLimit - 1)), event] }));
}

export function startCall(id: string, method: string) {
  useDiagnostics.setState((s) => ({ pending: [...s.pending, { id, method, started: Date.now() }] }));
  recordEvent('rpc', method, `${id} started`);
}

export function finishCall(id: string, outcome: string, error = false) {
  const call = useDiagnostics.getState().pending.find((p) => p.id === id);
  if (!call) return;
  useDiagnostics.setState((s) => ({ pending: s.pending.filter((p) => p.id !== id) }));
  recordEvent('rpc', call.method, `${id} · ${Date.now() - call.started} ms · ${outcome}`, error);
}
