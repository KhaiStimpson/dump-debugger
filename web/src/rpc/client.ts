import type { RpcMethod, RpcMethods, RpcNotification, RpcNotifications } from '../contracts.gen';
import { finishCall, recordEvent, startCall } from '../debug/diagnostics';

/**
 * Typed JSON-RPC client over the WebView2 bridge. Method names, params and results are all
 * checked against the generated `RpcMethods` map, so a contract change in DumpDebugger.Core
 * that isn't reflected here fails `tsc`.
 */

interface WebViewBridge {
  postMessage(message: string): void;
  postMessageWithAdditionalObjects?(message: string, objects: unknown[]): void;
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

interface RpcErrorShape {
  code: number;
  message: string;
  data?: string | null;
}

interface RpcEnvelope {
  id?: string;
  method?: string;
  params?: unknown;
  result?: unknown;
  error?: RpcErrorShape;
}

export class RpcError extends Error {
  readonly code: number;
  readonly data?: string | null;

  constructor(e: RpcErrorShape) {
    super(e.message);
    this.code = e.code;
    this.data = e.data;
  }

  get cancelled(): boolean {
    return this.code === -32800;
  }
}

export interface CallOptions {
  signal?: AbortSignal;
  /** Files dropped onto the page; the host receives their real paths. */
  files?: File[];
}

type Handler = (payload: unknown) => void;

export class RpcClient {
  private nextId = 1;
  private readonly pending = new Map<
    string,
    { resolve: (v: unknown) => void; reject: (e: unknown) => void; cleanup: () => void }
  >();
  private readonly handlers = new Map<string, Set<Handler>>();
  private readonly bridge: WebViewBridge | undefined;
  private lastAnalysisStage = '';

  constructor(bridge: WebViewBridge | undefined) {
    this.bridge = bridge;
    bridge?.addEventListener('message', (e) => this.receive(e.data));
  }

  get connected(): boolean {
    return this.bridge !== undefined;
  }

  call<M extends RpcMethod>(
    method: M,
    params: RpcMethods[M]['params'],
    options: CallOptions = {},
  ): Promise<RpcMethods[M]['result']> {
    const bridge = this.bridge;
    if (!bridge) {
      recordEvent('rpc', method, 'WebView2 bridge is not connected', true);
      return Promise.reject(new RpcError({ code: -32002, message: 'Not running inside the Dump Debugger host.' }));
    }

    const id = `w${this.nextId++}`;
    const message = JSON.stringify({ id, method, params });
    return new Promise((resolve, reject) => {
      if (options.signal?.aborted) {
        reject(new RpcError({ code: -32800, message: 'Request cancelled.' }));
        return;
      }

      const abort = () => {
        if (this.pending.delete(id)) {
          finishCall(id, 'cancelled');
          try {
            bridge.postMessage(JSON.stringify({ method: '$/cancel', params: { id } }));
          } catch (error) {
            recordEvent('rpc', '$/cancel', `send failed: ${String(error)}`, true);
          } finally {
            reject(new RpcError({ code: -32800, message: 'Request cancelled.' }));
          }
        }
      };
      this.pending.set(id, {
        resolve: resolve as (v: unknown) => void,
        reject,
        cleanup: () => options.signal?.removeEventListener('abort', abort),
      });
      startCall(id, method);
      options.signal?.addEventListener('abort', abort, { once: true });

      try {
        if (options.files?.length && bridge.postMessageWithAdditionalObjects) {
          bridge.postMessageWithAdditionalObjects(message, options.files);
        } else {
          bridge.postMessage(message);
        }
      } catch (error) {
        this.pending.get(id)?.cleanup();
        this.pending.delete(id);
        finishCall(id, `send failed: ${String(error)}`, true);
        reject(error);
      }
    });
  }

  on<N extends RpcNotification>(name: N, handler: (payload: RpcNotifications[N]) => void): () => void {
    let set = this.handlers.get(name);
    if (!set) {
      set = new Set();
      this.handlers.set(name, set);
    }

    set.add(handler as Handler);
    return () => set.delete(handler as Handler);
  }

  private receive(data: unknown): void {
    let msg: RpcEnvelope;
    try {
      msg = (typeof data === 'string' ? JSON.parse(data) : data) as RpcEnvelope;
    } catch {
      return;
    }

    if (msg.method && msg.id === undefined) {
      if (msg.params && typeof msg.params === 'object') {
        const p = msg.params as Record<string, unknown>;
        if (msg.method === 'debug.event') {
          recordEvent(String(p.source ?? 'host'), String(p.stage ?? 'event'), String(p.detail ?? ''), p.error === true);
        } else if (msg.method === 'analysis.failed') {
          recordEvent('analysis', 'failed', String(p.message ?? ''), true);
        } else if (msg.method === 'dump.progress') {
          recordEvent('host', String(p.stage ?? 'progress'), String(p.detail ?? ''));
        } else if (msg.method === 'worker.status') {
          recordEvent(
            'worker',
            String(p.state),
            `${p.architecture} · pid ${p.processId ?? '—'} ${p.message ?? ''}`,
            p.state === 'exited' && !!p.message,
          );
        } else if (msg.method === 'analysis.updated') {
          const d = msg.params as RpcNotifications['analysis.updated'];
          const summary = d.complete
            ? 'Complete'
            : d.stages
                .filter((s) => s.status !== 'pending')
                .map((s) => `${s.id}: ${s.status}`)
                .join(', ');
          if (summary !== this.lastAnalysisStage) {
            recordEvent('analysis', 'stages', summary);
            this.lastAnalysisStage = summary;
          }
        }
      }
      this.handlers.get(msg.method)?.forEach((h) => h(msg.params));
      return;
    }

    if (msg.id !== undefined) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      p.cleanup();
      finishCall(msg.id, msg.error ? `${msg.error.code}: ${msg.error.message}` : 'completed', !!msg.error);
      if (msg.error) p.reject(new RpcError(msg.error));
      else p.resolve(msg.result ?? null);
    }
  }
}

export const rpc = new RpcClient(typeof window !== 'undefined' ? window.chrome?.webview : undefined);
