import { useState } from 'react';
import { icons } from '../components/Icons';
import { Sev } from '../components/Sev';
import { ago, bytes } from '../format';
import { rpc } from '../rpc/client';
import { navigate } from '../router';
import { useRpc } from '../state/queries';
import { useApp } from '../state/store';

/** What am I opening? Drop zone, recents with their last verdict, sample generator, compare. */
export function Home() {
  const openDump = useApp((s) => s.openDump);
  const error = useApp((s) => s.error);
  const clearError = useApp((s) => s.clearError);
  const recents = useRpc('recents.list', {});
  const samples = useRpc('samples.list', {});
  const [dragging, setDragging] = useState(false);
  const [sampleMenu, setSampleMenu] = useState(false);
  const [generating, setGenerating] = useState<string | null>(null);

  const browse = async () => {
    const picked = await rpc.call('dump.pick', {});
    if (picked.path) void openDump(picked.path);
  };

  const generate = async (scenario: string) => {
    setSampleMenu(false);
    setGenerating(scenario);
    try {
      const sample = await rpc.call('samples.generate', { scenario });
      await openDump(sample.path);
    } catch (e) {
      useApp.setState({ error: String(e instanceof Error ? e.message : e) });
    } finally {
      setGenerating(null);
    }
  };

  return (
    <div className="view on home">
      <div className="home-in">
        <h2 style={{ fontSize: 26 }}>Open a dump</h2>
        <p style={{ color: 'var(--fg-2)', margin: '6px 0 20px' }}>
          Hang, crash or memory dump from .NET Framework 4.8, .NET 6 or .NET 8+ (x86 or x64).
        </p>
        {error && (
          <div className="callout bad-callout" role="alert">
            {error}{' '}
            <button className="btn ghost" onClick={clearError}>
              Dismiss
            </button>
          </div>
        )}
        <div
          className={`drop ${dragging ? 'over' : ''}`}
          onDragOver={(e) => {
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={(e) => {
            e.preventDefault();
            setDragging(false);
            const files = [...e.dataTransfer.files];
            if (files.length) void openDump('', files);
          }}
        >
          {icons.drop}
          <div style={{ color: 'var(--fg-0)', fontSize: 15, marginTop: 10 }}>
            Drop a <code>.dmp</code> here
          </div>
          <div style={{ marginTop: 14, display: 'flex', gap: 8, justifyContent: 'center', position: 'relative' }}>
            <button className="btn pri" onClick={browse}>
              Browse…
            </button>
            <button className="btn" onClick={() => navigate('compare')}>
              Compare two dumps
            </button>
            <button
              className="btn ghost"
              onClick={() => setSampleMenu((v) => !v)}
              disabled={!!generating}
              aria-expanded={sampleMenu}
            >
              {generating ? `Generating ${generating} sample…` : 'Generate a sample'}
            </button>
            {sampleMenu && (
              <div className="menu" role="menu">
                {samples.data?.map((s) => (
                  <button key={s.id} role="menuitem" onClick={() => generate(s.id)}>
                    <b>{s.title}</b>
                    <span>{s.description}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="recent">
          <div className="lbl" style={{ padding: '0 14px 8px' }}>
            Recent
          </div>
          {recents.data?.length === 0 && (
            <div className="rrow empty">Dumps you open show up here with their last verdict.</div>
          )}
          {recents.data?.map((r) => (
            <div
              key={r.path}
              className={`rrow ${r.exists ? '' : 'missing'}`}
              role="button"
              tabIndex={0}
              onClick={() => r.exists && openDump(r.path)}
              onKeyDown={(e) => e.key === 'Enter' && r.exists && openDump(r.path)}
              title={r.exists ? `${r.path} · ${bytes(r.sizeBytes)}` : `${r.path} (file not found)`}
            >
              <div>
                <div className="n">{r.fileName}</div>
                <div className="s">{r.folder}</div>
              </div>
              {r.isSample && r.verdictSeverity == null ? (
                <Sev severity="ok" label="Sample" />
              ) : r.verdictSeverity ? (
                <Sev severity={r.verdictSeverity} label={r.verdictLabel ?? undefined} />
              ) : (
                <span className="s">—</span>
              )}
              <span className="s" title={bytes(r.sizeBytes)}>
                {r.runtime ?? '?'} · {r.architecture}
              </span>
              <span className="s">{ago(r.openedAt)}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
