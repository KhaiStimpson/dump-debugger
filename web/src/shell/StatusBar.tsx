import { seconds } from '../format';
import { useApp } from '../state/store';

/** Worker, DAC, workspace and analysis timing: the facts an expert wants to glance at. */
export function StatusBar() {
  const worker = useApp((s) => s.worker);
  const dump = useApp((s) => s.dump);
  const diagnosis = useApp((s) => s.diagnosis);
  const running = diagnosis?.stages.find((s) => s.status === 'running');

  return (
    <div className="statusbar" role="status">
      {worker && worker.state !== 'exited' ? (
        <span>
          <span className="dot" />
          worker {worker.architecture.toLowerCase()} · pid {worker.processId}
        </span>
      ) : (
        <span>
          <span className="dot" style={{ background: 'var(--fg-3)' }} />
          no worker
        </span>
      )}
      {diagnosis?.dump.dacSource && <span>DAC {diagnosis.dump.dacSource}</span>}
      {dump && <span title={dump.workspacePath}>workspace: {dump.workspacePath.split(/[\\/]/).pop()}/</span>}
      <span style={{ marginLeft: 'auto' }}>
        {diagnosis
          ? diagnosis.fromCache
            ? 'analysis restored from workspace'
            : diagnosis.complete
              ? `analysis ${seconds(diagnosis.seconds)} · all analyzers done`
              : `analyzing · ${running?.label.toLowerCase() ?? '…'}${running?.fraction != null ? ` ${Math.round(running.fraction * 100)}%` : ''}`
          : ''}
      </span>
    </div>
  );
}
