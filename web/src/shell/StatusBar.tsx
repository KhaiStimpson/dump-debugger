import { useEffect, useRef, useState } from 'react';
import { seconds } from '../format';
import { useApp } from '../state/store';
import { useDiagnostics } from '../debug/diagnostics';
import { systemTheme, themeLabels, useTheme, type ThemePreference } from '../state/theme';
import { McpButton } from './McpPanel';
import { SymbolsButton } from './SymbolsPanel';

/** Worker, DAC, workspace and analysis timing: the facts an expert wants to glance at. */
export function StatusBar() {
  const worker = useApp((s) => s.worker);
  const dump = useApp((s) => s.dump);
  const diagnosis = useApp((s) => s.diagnosis);
  const error = useApp((s) => s.error);
  const reopenDump = useApp((s) => s.reopenDump);
  const workerLost = !!dump && worker?.state === 'exited' && !!worker.message;
  const running = diagnosis?.stages.find((s) => s.status === 'running');

  return (
    <footer className="statusbar">
      <button
        className="btn ghost"
        onClick={() => useDiagnostics.getState().toggle()}
        title="Live diagnostics (Ctrl+Shift+D)"
      >
        Debug
      </button>
      <ThemeMenu />
      <McpButton />
      <SymbolsButton />
      {workerLost ? (
        <span role="alert" className="worker-lost" title={worker?.message ?? undefined}>
          Worker stopped{' '}
          <button className="btn" onClick={() => void reopenDump()}>
            Reopen dump
          </button>
        </span>
      ) : worker && worker.state !== 'exited' ? (
        <span>
          Worker {worker.architecture.toLowerCase()}, pid {worker.processId}
        </span>
      ) : (
        <span>No worker</span>
      )}
      {diagnosis?.dump.dacSource && <span>DAC {diagnosis.dump.dacSource}</span>}
      {dump && <span title={dump.workspacePath}>Workspace {dump.workspacePath.split(/[\\/]/).pop()}</span>}
      <span style={{ marginLeft: 'auto' }} role="status" aria-live="polite">
        {dump?.analysisError
          ? 'Analysis unavailable'
          : error && dump && !diagnosis?.complete
            ? 'Analysis failed'
            : diagnosis
              ? diagnosis.fromCache
                ? 'Analysis restored from workspace'
                : diagnosis.complete
                  ? `Analysis took ${seconds(diagnosis.seconds)}`
                  : `Analyzing: ${running?.label.toLowerCase() ?? '…'}${running?.fraction != null ? ` ${Math.round(running.fraction * 100)}%` : ''}`
              : ''}
      </span>
    </footer>
  );
}

/** System / Light / Dark, opened upwards from the status bar. */
function ThemeMenu() {
  const preference = useTheme((s) => s.preference);
  const setPreference = useTheme((s) => s.setPreference);
  const [at, setAt] = useState<{ left: number; bottom: number } | null>(null);
  const button = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!at) return;
    const close = (e: Event) => {
      if (e instanceof KeyboardEvent && e.key !== 'Escape') return;
      if (e.type === 'pointerdown' && button.current?.contains(e.target as Node)) return;
      setAt(null);
    };
    window.addEventListener('pointerdown', close);
    window.addEventListener('keydown', close);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('keydown', close);
      window.removeEventListener('blur', close);
    };
  }, [at]);

  const toggle = () => {
    const r = button.current?.getBoundingClientRect();
    setAt(at || !r ? null : { left: r.left, bottom: window.innerHeight - r.top + 4 });
  };
  const choose = (p: ThemePreference) => {
    setPreference(p);
    setAt(null);
    button.current?.focus();
  };

  return (
    <>
      <button
        ref={button}
        className="btn ghost"
        onClick={toggle}
        aria-haspopup="menu"
        aria-expanded={!!at}
        title="Colour theme"
      >
        Theme: {themeLabels[preference]}
      </button>
      {at && (
        <div className="ctxmenu" style={{ left: at.left, bottom: at.bottom }} role="menu" aria-label="Colour theme">
          {(['system', 'light', 'dark'] as const).map((p) => (
            <button
              key={p}
              role="menuitemradio"
              aria-checked={preference === p}
              onPointerDown={(e) => e.stopPropagation()}
              onClick={() => choose(p)}
              autoFocus={preference === p}
            >
              {themeLabels[p]}
              <span className="menu-note">
                {p === 'system' ? `Windows: ${themeLabels[systemTheme()]}` : ''}
                {preference === p ? ' ✓' : ''}
              </span>
            </button>
          ))}
        </div>
      )}
    </>
  );
}
