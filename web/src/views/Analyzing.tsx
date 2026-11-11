import { bytes, fileName, seconds } from '../format';
import { navigate } from '../router';
import { useApp } from '../state/store';
import { toneColor } from '../components/Sev';

/** Named stages, never a bare spinner (P6). Threads are explorable as soon as their stage is done. */
export function Analyzing() {
  const dump = useApp((s) => s.dump);
  const opening = useApp((s) => s.opening);
  const progress = useApp((s) => s.progress);
  const diagnosis = useApp((s) => s.diagnosis);
  const closeDump = useApp((s) => s.closeDump);

  const stages = diagnosis?.stages ?? [];
  const done = stages.filter((s) => s.status === 'done').length;
  const running = stages.find((s) => s.status === 'running');
  const fraction = stages.length ? (done + (running?.fraction ?? 0)) / stages.length : 0.03;
  const md = dump?.metadata;
  const threadsReady = stages.some((s) => s.id === 'threads' && s.status === 'done');

  return (
    <div className="view on anz">
      <div className="steps">
        <div className="lbl">Analyzing {md ? fileName(md.dumpPath) : opening ? fileName(opening) : ''}</div>
        <h2 style={{ fontSize: 22, marginTop: 8 }}>
          {md
            ? `Reading ${bytes(md.fileSizeBytes)} · ${diagnosis?.dump.runtime ?? md.runtimes[0]?.productVersion ?? ''} ${md.architecture}`
            : (progress?.stage ?? 'Starting worker…')}
        </h2>
        <div
          className="prog"
          role="progressbar"
          aria-valuenow={Math.round(fraction * 100)}
          aria-valuemin={0}
          aria-valuemax={100}
        >
          <i style={{ width: `${Math.max(3, fraction * 100)}%` }} />
        </div>
        <div style={{ fontSize: 12, color: 'var(--fg-3)', marginBottom: 14 }}>
          {threadsReady
            ? 'You can start exploring threads now. Heap findings will appear as they finish.'
            : (progress?.detail ?? 'Matching the dump to a worker of the same bitness and loading the runtime.')}
        </div>
        {stages.length === 0 && (
          <div className="step run">
            <span className="ic" />
            <span>{progress?.stage ?? 'Opening dump'}</span>
            <span className="t">—</span>
          </div>
        )}
        {stages.map((s) => (
          <div key={s.id} className={`step ${s.status === 'done' ? 'done' : s.status === 'running' ? 'run' : ''}`}>
            <span className="ic" />
            <span style={{ color: s.status === 'pending' ? 'var(--fg-3)' : undefined }}>
              {s.label}
              {s.detail && (
                <>
                  {' · '}
                  {s.tone === 'neutral' ? (
                    <span style={{ color: 'var(--fg-3)' }}>{s.detail}</span>
                  ) : (
                    <b style={{ color: toneColor[s.tone] }}>{s.detail}</b>
                  )}
                </>
              )}
            </span>
            <span className="t">{s.status === 'pending' ? '—' : seconds(s.seconds)}</span>
          </div>
        ))}
        <div style={{ marginTop: 18, display: 'flex', gap: 8 }}>
          <button className="btn pri" disabled={!threadsReady} onClick={() => navigate('diagnosis')}>
            See early findings
          </button>
          <button className="btn ghost" onClick={() => void closeDump()}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
