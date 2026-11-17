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

  it('labels the dump with the primary runtime, not the first one in the dump', () => {
    state.dump = {
      metadata: {
        dumpPath: 'w3wp.dmp',
        fileSizeBytes: 1,
        architecture: 'x86',
        runtimes: [
          { family: 'netFramework', productVersion: '4.8', isPrimary: false },
          { family: 'netCore', productVersion: '10.0.13', isPrimary: true },
        ],
      },
    } as unknown as State['dump'];
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain('10.0.13 x86');
    expect(html).not.toContain('4.8 x86');
  });

  it('ends progress when analysis fails after it started', () => {
    state.dump = { metadata: { dumpPath: 'w3wp.dmp', runtimes: [] } } as unknown as State['dump'];
    state.diagnosis = {
      stages: [{ id: 'threads', label: 'Reading threads', status: 'running' }],
    } as unknown as State['diagnosis'];
    state.error = 'Worker exited unexpectedly (exit code -532462766).';
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain('Analysis failed');
    expect(html).toContain('exit code -532462766');
    expect(html).not.toContain('role="progressbar"');
  });

  it('shows progress while the worker is still opening the dump', () => {
    state.opening = 'w3wp.dmp';
    const html = renderToStaticMarkup(<Analyzing />);
    expect(html).toContain('role="progressbar"');
    expect(html).not.toContain('role="alert"');
  });
});
