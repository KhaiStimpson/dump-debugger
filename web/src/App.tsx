import { useEffect, useState } from 'react';
import type { Diagnosis } from './contracts.gen';
import { rpc } from './rpc/client';

// Interim R1 view: proves the streamed diagnosis reaches the page. Replaced by the R2 shell.
export function App() {
  const [diagnosis, setDiagnosis] = useState<Diagnosis | null>(null);

  useEffect(() => {
    const off = rpc.on('analysis.updated', setDiagnosis);
    rpc.call('app.info', {}).then((info) => {
      if (info.startupDumpPath) void rpc.call('dump.open', { path: info.startupDumpPath });
    });
    return off;
  }, []);

  return (
    <div className="win">
      <div className="titlebar">
        <div className="logo" />
        <span className="dump">{diagnosis?.dump.fileName ?? 'Dump Debugger'}</span>
        <span style={{ marginLeft: 'auto' }} />
        <span className="wc" />
      </div>
      <div className="scroll" style={{ padding: 28 }}>
        {diagnosis && (
          <>
            <span className={`sev ${diagnosis.verdict.severity === 'critical' ? 'crit' : 'info'}`}>
              {diagnosis.verdict.severity}
            </span>
            <h2 style={{ marginTop: 10 }}>{diagnosis.verdict.headline}</h2>
            {diagnosis.findings.map((f) => (
              <p key={f.id}>{f.title}</p>
            ))}
          </>
        )}
      </div>
      <div className="statusbar">{diagnosis ? `analysis ${diagnosis.seconds.toFixed(1)}s` : 'Ready'}</div>
    </div>
  );
}
