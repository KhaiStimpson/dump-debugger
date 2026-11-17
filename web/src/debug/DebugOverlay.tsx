import { useEffect, useState } from 'react';
import { useApp } from '../state/store';
import { rpc } from '../rpc/client';
import { useDiagnostics } from './diagnostics';
import './debug.css';

/** Non-modal: keep interacting with the app while watching calls and worker events. */
export function DebugOverlay() {
  const visible = useDiagnostics((s) => s.visible);
  return visible ? <LiveDiagnostics /> : null;
}

function LiveDiagnostics() {
  const { events, pending, toggle, clear } = useDiagnostics();
  const worker = useApp((s) => s.worker);
  const dump = useApp((s) => s.dump);
  const progress = useApp((s) => s.progress);
  const error = useApp((s) => s.error) ?? dump?.analysisError;
  const diagnosis = useApp((s) => s.diagnosis);
  const [now, setNow] = useState(Date.now);
  const [errorsOnly, setErrorsOnly] = useState(false);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 500);
    return () => window.clearInterval(timer);
  }, []);

  const stage = diagnosis?.stages.find((s) => s.status === 'running');
  const save = () => {
    const blob = new Blob(
      [
        JSON.stringify(
          {
            savedAt: new Date().toISOString(),
            worker,
            metadata: dump?.metadata,
            progress,
            analysisError: error,
            stages: diagnosis?.stages,
            pending,
            events,
          },
          null,
          2,
        ),
      ],
      { type: 'application/json' },
    );
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `dump-debugger-diagnostics-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    link.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  };

  return (
    <section className="debug-overlay" aria-label="Live diagnostics">
      <header>
        <div>
          <span className="debug-live" /> Live diagnostics <small>Ctrl+Shift+D</small>
        </div>
        <button className="btn ghost" onClick={toggle} aria-label="Close diagnostics">
          ✕
        </button>
      </header>
      <div className="debug-facts">
        <div>
          <b>Bridge</b>
          <span>{rpc.connected ? 'Connected' : 'Disconnected'}</span>
        </div>
        <div>
          <b>Worker</b>
          <span>
            {worker ? `${worker.architecture} · ${worker.state} · pid ${worker.processId ?? '—'}` : 'Not started'}
          </span>
        </div>
        <div>
          <b>Stage</b>
          <span>
            {error ? 'Failed' : diagnosis?.complete ? 'Complete' : (stage?.label ?? progress?.stage ?? 'Idle')}
          </span>
        </div>
        {dump?.metadata.runtimes.map((runtime, i) => (
          <div key={i}>
            <b>
              {runtime.family} {runtime.productVersion}
            </b>
            <span className={runtime.isManagedAnalysisAvailable ? '' : 'debug-error'}>
              {runtime.isManagedAnalysisAvailable ? 'Runtime loaded' : 'Runtime unavailable'} · build {runtime.version}
              {runtime.dacError && <small>{runtime.dacError}</small>}
            </span>
          </div>
        ))}
      </div>
      {error && <div className="debug-failure">{error}</div>}
      <div className="debug-pending">
        <b>{pending.length} pending calls</b>
        {pending.map((call) => (
          <div key={call.id}>
            <code>
              {call.id} {call.method}
            </code>
            <span>{Math.max(0, (now - call.started) / 1000).toFixed(1)} s</span>
          </div>
        ))}
      </div>
      <div className="debug-tools">
        <label>
          <input type="checkbox" checked={errorsOnly} onChange={(e) => setErrorsOnly(e.target.checked)} /> Errors only
        </label>
        <span>{events.length}/300 events</span>
        <button className="btn ghost" onClick={clear}>
          Clear
        </button>
        <button className="btn" onClick={save}>
          Save log
        </button>
      </div>
      <ol className="debug-events" aria-label="Diagnostic events">
        {[...events]
          .reverse()
          .filter((event) => !errorsOnly || event.error)
          .map((event) => (
            <li key={event.sequence} className={event.error ? 'debug-error' : ''}>
              <time>{new Date(event.at).toLocaleTimeString([], { hour12: false })}</time>
              <div>
                <b>
                  {event.source} / {event.stage}
                </b>
                <span>{event.detail}</span>
              </div>
            </li>
          ))}
        {events.length === 0 && <li>Waiting for activity. Open a dump to follow its loading stages.</li>}
      </ol>
      <footer>Local session only. Saved logs may include file paths and error messages.</footer>
    </section>
  );
}
