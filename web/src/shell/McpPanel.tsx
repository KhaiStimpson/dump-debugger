import { useEffect, useRef, useState } from 'react';
import type { McpSettingsParams, McpStatus } from '../contracts.gen';
import { rpc } from '../rpc/client';

/** The one-line command that registers the server with Claude Code. */
export const claudeCommand = (s: Pick<McpStatus, 'url' | 'token'>) =>
  `claude mcp add --transport http dump-debugger ${s.url} --header "Authorization: Bearer ${s.token}"`;

/** The `mcpServers` entry most other MCP clients accept. */
export const jsonConfig = (s: Pick<McpStatus, 'url' | 'token'>) =>
  JSON.stringify(
    { mcpServers: { 'dump-debugger': { type: 'http', url: s.url, headers: { Authorization: `Bearer ${s.token}` } } } },
    null,
    2,
  );

/** What the status bar button says. */
export const mcpLabel = (s: McpStatus | null) =>
  !s || !s.enabled ? 'MCP: off' : s.running ? 'MCP: on' : 'MCP: not running';

const hideToken = (text: string, token: string) => (token ? text.replaceAll(token, '•'.repeat(12)) : text);

type Snippet = 'claude' | 'json';

/** The status bar button and the panel it opens: turn the MCP server on, and copy what a client needs to connect. */
export function McpButton() {
  const [status, setStatus] = useState<McpStatus | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!rpc.connected) return;
    void rpc
      .call('mcp.get', {})
      .then(setStatus)
      .catch(() => {});
  }, []);

  if (status && !status.available) return null;

  return (
    <>
      <button
        className="btn ghost"
        onClick={() => setOpen(true)}
        aria-haspopup="dialog"
        title="Let an AI agent analyze dumps through this app (MCP server)"
      >
        {mcpLabel(status)}
      </button>
      {open && status && <McpPanel status={status} onStatus={setStatus} onClose={() => setOpen(false)} />}
    </>
  );
}

function McpPanel({
  status,
  onStatus,
  onClose,
}: {
  status: McpStatus;
  onStatus: (s: McpStatus) => void;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [port, setPort] = useState(String(status.port));
  const [snippet, setSnippet] = useState<Snippet>('claude');
  const [reveal, setReveal] = useState(false);
  const [copied, setCopied] = useState(false);
  const [confirmRotate, setConfirmRotate] = useState(false);
  const dialog = useRef<HTMLDivElement>(null);

  useEffect(() => {
    dialog.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [onClose]);

  const run = async (work: () => Promise<McpStatus>) => {
    setBusy(true);
    setFailure(null);
    try {
      const next = await work();
      onStatus(next);
      setPort(String(next.port));
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const change = (settings: Partial<McpSettingsParams>) =>
    run(() => rpc.call('mcp.set', { enabled: null, port: null, allowDacDownload: null, ...settings }));

  const portNumber = Number(port);
  const portValid = Number.isInteger(portNumber) && portNumber >= 1024 && portNumber <= 65535;
  const text = snippet === 'claude' ? claudeCommand(status) : jsonConfig(status);
  const problem = failure ?? status.error;

  const copy = () => {
    void navigator.clipboard?.writeText(text);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
  };

  return (
    <div className="palette-scrim" onClick={onClose}>
      <div
        ref={dialog}
        className="palette on mcp"
        role="dialog"
        aria-modal="true"
        aria-labelledby="mcp-title"
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="pane-h">
          <h3 id="mcp-title">MCP server</h3>
          <div className="r">
            <button className="btn ghost" onClick={onClose} aria-label="Close MCP settings">
              ✕
            </button>
          </div>
        </div>

        <div className="mcp-body">
          <p className="mcp-lede">
            Lets an AI agent such as Claude Code analyze dumps through this app: the one you have open, and others it
            opens by path. Agents can only read. The server is reachable from this computer only, and only with the
            token below.
          </p>

          <label className="check">
            <input
              type="checkbox"
              role="switch"
              checked={status.enabled}
              disabled={busy}
              onChange={(e) => void change({ enabled: e.target.checked })}
            />
            <span>Run the MCP server</span>
          </label>

          <p className={`mcp-state ${problem ? 'bad' : status.running ? 'good' : ''}`} role="status" aria-live="polite">
            {problem
              ? problem
              : status.running
                ? `Running at ${status.url}`
                : 'Off. Nothing is listening, and it stays off until you turn it on.'}
          </p>

          <div className="mcp-row">
            <label htmlFor="mcp-port">Port</label>
            <input
              id="mcp-port"
              inputMode="numeric"
              value={port}
              onChange={(e) => setPort(e.target.value.replace(/\D/g, '').slice(0, 5))}
              aria-invalid={!portValid}
              aria-describedby="mcp-port-hint"
            />
            <button
              className="btn"
              disabled={busy || !portValid || portNumber === status.port}
              onClick={() => void change({ port: portNumber })}
            >
              Change port
            </button>
            <span id="mcp-port-hint" className="mcp-hint">
              {portValid ? 'Client configurations include the port.' : '1024 to 65535'}
            </span>
          </div>

          <h4>Connect a client</h4>
          <div className="mcp-row">
            <div className="seg" role="tablist" aria-label="Client configuration format">
              <button
                role="tab"
                aria-selected={snippet === 'claude'}
                className={snippet === 'claude' ? 'on' : ''}
                onClick={() => setSnippet('claude')}
              >
                Claude Code
              </button>
              <button
                role="tab"
                aria-selected={snippet === 'json'}
                className={snippet === 'json' ? 'on' : ''}
                onClick={() => setSnippet('json')}
              >
                JSON config
              </button>
            </div>
            <span className="mcp-hint">
              {snippet === 'claude' ? 'Run this in a terminal.' : 'Add this to the client’s MCP settings.'}
            </span>
          </div>
          <pre className="mcp-snippet" role="tabpanel" aria-label="Client configuration">
            {reveal ? text : hideToken(text, status.token)}
          </pre>
          <div className="mcp-row">
            <button className="btn pri" onClick={copy}>
              {copied ? 'Copied' : 'Copy'}
            </button>
            <button className="btn" onClick={() => setReveal(!reveal)} aria-pressed={reveal}>
              {reveal ? 'Hide token' : 'Show token'}
            </button>
            <span className="mcp-hint">Copy includes the token. Treat it like a password.</span>
          </div>

          <h4>Options</h4>
          <label className="check">
            <input
              type="checkbox"
              checked={status.allowDacDownload}
              disabled={busy}
              onChange={(e) => void change({ allowDacDownload: e.target.checked })}
            />
            <span>Allow DAC downloads for dumps agents open</span>
          </label>
          <p className="mcp-hint mcp-indent">
            When a dump’s runtime has no matching debugging DLL on this computer, fetch it from the Microsoft symbol
            server without asking. Runtime identifiers are sent; the dump is not. The dump you open yourself still asks.
          </p>

          <div className="mcp-row">
            {confirmRotate ? (
              <>
                <span>Clients using the current token will stop working.</span>
                <button
                  className="btn"
                  disabled={busy}
                  onClick={() => {
                    setConfirmRotate(false);
                    void run(() => rpc.call('mcp.regenerateToken', {}));
                  }}
                >
                  Regenerate
                </button>
                <button className="btn ghost" onClick={() => setConfirmRotate(false)}>
                  Cancel
                </button>
              </>
            ) : (
              <button className="btn" disabled={busy} onClick={() => setConfirmRotate(true)}>
                Regenerate token…
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
