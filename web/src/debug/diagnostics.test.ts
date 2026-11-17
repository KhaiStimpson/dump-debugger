import { beforeEach, describe, expect, it, vi } from 'vitest';
import { eventLimit, recordEvent, useDiagnostics } from './diagnostics';
import { RpcClient } from '../rpc/client';

function bridge() {
  let receive: (e: { data: unknown }) => void = () => {};
  const postMessage = vi.fn();
  const client = new RpcClient({
    postMessage,
    addEventListener: (_, listener) => {
      receive = listener;
    },
  });
  return { client, postMessage, reply: (data: unknown) => receive({ data }) };
}

describe('local diagnostics', () => {
  beforeEach(() => useDiagnostics.setState({ events: [], pending: [], visible: false }));

  it('bounds retained events and detail length, even while the panel is hidden', () => {
    for (let i = 0; i < eventLimit + 20; i++) recordEvent('test', String(i), 'x'.repeat(5000));
    const state = useDiagnostics.getState();
    expect(state.visible).toBe(false);
    expect(state.events).toHaveLength(eventLimit);
    expect(state.events[0].stage).toBe('20');
    expect(state.events[0].detail).toHaveLength(4000);
  });

  it('tracks a live request and its failure without retaining request parameters', async () => {
    const { client, reply } = bridge();
    const result = client.call('dump.open', { path: 'PRIVATE-DUMP-PATH', allowSymbolServer: false });
    expect(useDiagnostics.getState().pending[0].method).toBe('dump.open');
    expect(JSON.stringify(useDiagnostics.getState())).not.toContain('PRIVATE-DUMP-PATH');
    reply({ id: 'w1', error: { code: -32603, message: 'Matching DAC missing' } });
    await expect(result).rejects.toThrow('Matching DAC missing');
    expect(useDiagnostics.getState().pending).toHaveLength(0);
    expect(useDiagnostics.getState().events.at(-1)?.error).toBe(true);
  });

  it('clears history while preserving active calls and records their eventual completion', async () => {
    const { client, reply } = bridge();
    const result = client.call('analysis.get', {});
    useDiagnostics.getState().clear();
    expect(useDiagnostics.getState().events).toHaveLength(0);
    expect(useDiagnostics.getState().pending).toHaveLength(1);
    reply({ id: 'w1', result: { diagnosis: null, running: false } });
    await result;
    expect(useDiagnostics.getState().events.at(-1)?.detail).toContain('completed');
    expect(useDiagnostics.getState().pending).toHaveLength(0);
  });

  it('removes cancelled calls and ignores late replies', async () => {
    const { client, reply, postMessage } = bridge();
    const abort = new AbortController();
    const result = client.call('analysis.get', {}, { signal: abort.signal });
    abort.abort();
    await expect(result).rejects.toThrow('Request cancelled');
    expect(useDiagnostics.getState().pending).toHaveLength(0);
    const count = useDiagnostics.getState().events.length;
    reply({ id: 'w1', result: {} });
    expect(useDiagnostics.getState().events).toHaveLength(count);
    expect(postMessage.mock.calls.at(-1)?.[0]).toContain('$/cancel');
  });

  it('reports synchronous bridge failures without leaving a stuck pending call', async () => {
    const { client, postMessage } = bridge();
    postMessage.mockImplementation(() => {
      throw new Error('WebView disconnected');
    });
    await expect(client.call('analysis.get', {})).rejects.toThrow('WebView disconnected');
    expect(useDiagnostics.getState().pending).toHaveLength(0);
    expect(useDiagnostics.getState().events.at(-1)?.detail).toContain('send failed');
  });

  it('receives worker DAC diagnostics before the open request finishes', async () => {
    const { client, reply } = bridge();
    const result = client.call('dump.open', { path: 'dump', allowSymbolServer: false });
    reply({
      method: 'debug.event',
      params: { source: 'worker', stage: 'Resolving DAC', detail: 'Core 10.0.13', error: false },
    });
    expect(useDiagnostics.getState().pending).toHaveLength(1);
    expect(useDiagnostics.getState().events.at(-1)?.stage).toBe('Resolving DAC');
    reply({ id: 'w1', result: {} });
    await result;
  });
});
