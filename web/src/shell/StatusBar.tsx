import { seconds } from '../format';
import { useApp } from '../state/store';
import { useDiagnostics } from '../debug/diagnostics';

/** Worker, DAC, workspace and analysis timing: the facts an expert wants to glance at. */
export function StatusBar() {
  const worker = useApp((s) => s.worker);
  const dump = useApp((s) => s.dump);
  const diagnosis = useApp((s) => s.diagnosis);
  const error = useApp((s) => s.error);
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
      {worker && worker.state !== 'exited' ? (
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
