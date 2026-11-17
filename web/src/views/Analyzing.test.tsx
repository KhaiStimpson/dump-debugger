import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { useApp } from '../state/store';
import { Analyzing } from './Analyzing';

type State = ReturnType<typeof useApp.getState>;
let state: Partial<State> = {};
vi.mock('../state/store', () => ({ useApp: (selector: (s: Partial<State>) => unknown) => selector(state) }));
vi.mock('../router', () => ({ navigate: vi.fn() }));

describe('Analyzing failures', () => {
  beforeEach(() => {
    state = {};
  });

  it('shows the DAC error and stops showing progress when analysis cannot start', () => {
    state.dump = {
      metadata: { dumpPath: 'w3wp.dmp', runtimes: [] },
      analysisError: 'Could not find matching DAC: 10.0.13 x86',
    } as unknown as State['dump'];
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain('role="alert"');
    expect(html).toContain('Could not find matching DAC: 10.0.13 x86');
    expect(html).toContain('Close dump');
    expect(html).not.toContain('role="progressbar"');
    expect(html).not.toContain('See early findings');
  });

  it('keeps RPC loading errors on the analysis screen', () => {
    state.error = 'Worker could not load the dump';
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain(state.error);
    expect(html).not.toContain('role="progressbar"');
  });

  it('shows progress while the worker is still opening the dump', () => {
    state.opening = 'w3wp.dmp';
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain('role="progressbar"');
    expect(html).not.toContain('role="alert"');
  });
});
