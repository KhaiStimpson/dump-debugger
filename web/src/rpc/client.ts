import type { RpcMethod, RpcMethods, RpcNotification, RpcNotifications } from '../contracts.gen';

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
  private readonly pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
  private readonly handlers = new Map<string, Set<Handler>>();
  private readonly bridge: WebViewBridge | undefined;

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
      return Promise.reject(new RpcError({ code: -32002, message: 'Not running inside the Dump Debugger host.' }));
    }

    const id = `w${this.nextId++}`;
    const message = JSON.stringify({ id, method, params });
    return new Promise((resolve, reject) => {
      if (options.signal?.aborted) {
        reject(new RpcError({ code: -32800, message: 'Request cancelled.' }));
        return;
      }

      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
      options.signal?.addEventListener(
        'abort',
        () => {
          if (this.pending.delete(id)) {
            bridge.postMessage(JSON.stringify({ method: '$/cancel', params: { id } }));
            reject(new RpcError({ code: -32800, message: 'Request cancelled.' }));
          }
        },
        { once: true },
      );

      if (options.files?.length && bridge.postMessageWithAdditionalObjects) {
        bridge.postMessageWithAdditionalObjects(message, options.files);
      } else {
        bridge.postMessage(message);
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
      this.handlers.get(msg.method)?.forEach((h) => h(msg.params));
      return;
    }

    if (msg.id !== undefined) {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(new RpcError(msg.error));
      else p.resolve(msg.result ?? null);
    }
  }
}

export const rpc = new RpcClient(typeof window !== 'undefined' ? window.chrome?.webview : undefined);
