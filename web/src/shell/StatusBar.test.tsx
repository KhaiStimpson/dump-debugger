import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { useApp } from '../state/store';
import { StatusBar } from './StatusBar';

type State = ReturnType<typeof useApp.getState>;
let state: Partial<State> = {};
vi.mock('../state/store', () => ({ useApp: (selector: (s: Partial<State>) => unknown) => selector(state) }));
vi.mock('../debug/diagnostics', () => ({ useDiagnostics: { getState: () => ({ toggle: vi.fn() }) } }));

const running = {
  complete: false,
  fromCache: false,
  seconds: 0,
  dump: { dacSource: 'local cache' },
  stages: [{ id: 'heap', label: 'Walking the heap', status: 'running', fraction: 0.4 }],
} as unknown as State['diagnosis'];

describe('StatusBar analysis status', () => {
  beforeEach(() => {
    state = { dump: { workspacePath: 'C:/d/w3wp.dmp.dumpdebugger' } as State['dump'], diagnosis: running };
  });

  it('shows the running stage while analysis streams', () => {
    expect(renderToStaticMarkup(<StatusBar />)).toContain('Analyzing: walking the heap 40%');
  });

  it('stops claiming to analyze once analysis failed or the worker exited', () => {
    state.error = 'Worker exited unexpectedly (exit code 1).';
    const html = renderToStaticMarkup(<StatusBar />);
    expect(html).toContain('Analysis failed');
    expect(html).not.toContain('Analyzing:');
  });

  it('shows the DAC provenance of the primary runtime', () => {
    expect(renderToStaticMarkup(<StatusBar />)).toContain('DAC local cache');
  });
});
