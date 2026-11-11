import { useMemo, useState } from 'react';
import type { Finding, Symptom } from '../contracts.gen';
import { Chip } from '../components/Chip';
import { plain, Rich } from '../components/Rich';
import { Conf, Sev, toneColor } from '../components/Sev';
import { refFor, uri } from '../entities';
import { Inspector, useWaitGraph } from '../inspector/Inspector';
import { navigate } from '../router';
import { usePins } from '../state/pins';
import { useApp } from '../state/store';
import { MiniWaitGraph } from './MiniWaitGraph';

const symptoms: { id: Symptom | 'all'; label: string }[] = [
  { id: 'all', label: 'Not sure' },
  { id: 'hang', label: 'Hang / timeouts' },
  { id: 'highMemory', label: 'High memory' },
  { id: 'crash', label: 'Crash' },
  { id: 'slowCpu', label: 'Slow / high CPU' },
];

/** What's wrong, and what do I do? Verdict → evidence → raw, top to bottom (P1). */
export function Diagnosis() {
  const diagnosis = useApp((s) => s.diagnosis);
  const inspectorOpen = useApp((s) => s.inspector.open);
  const [symptom, setSymptom] = useState<Symptom | 'all'>('all');
  const [open, setOpen] = useState<Set<string> | null>(null);

  const ranked = useMemo(() => {
    const list = diagnosis?.findings ?? [];
    if (symptom === 'all') return list.map((f) => ({ f, match: true }));
    return [...list]
      .map((f) => ({ f, match: f.symptoms.includes(symptom) }))
      .sort((a, b) => Number(b.match) - Number(a.match) || b.f.score - a.f.score);
  }, [diagnosis, symptom]);

  if (!diagnosis) {
    return (
      <div className="view on" style={{ placeItems: 'center' }}>
        <p style={{ color: 'var(--fg-3)' }}>Waiting for the first analysis results…</p>
      </div>
    );
  }

  const openSet = open ?? new Set(ranked[0] ? [ranked[0].f.id] : []);
  const toggle = (id: string) => {
    const next = new Set(openSet);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setOpen(next);
  };
  const v = diagnosis.verdict;

  return (
    <div className="view on diag">
      <div className="scroll">
        <div className={`verdict v-${v.severity}`}>
          <Sev severity={v.severity} confidence={v.confidence} />
          <h2>{v.headline}</h2>
          <p>
            <Rich text={v.body} entities={v.entities} />
          </p>
          {!diagnosis.complete && (
            <p className="conf" style={{ marginTop: 8 }}>
              Deeper analyzers are still running; findings may be added.
            </p>
          )}
        </div>
        <div className="vitals">
          {diagnosis.vitals.map((vt) => (
            <div className="vital" key={vt.key}>
              <div className="lbl">{vt.label}</div>
              <div className="v">{vt.value}</div>
              <div className="d">{vt.detail}</div>
              <div className="bar">
                <i
                  style={{
                    width: `${Math.round((vt.fraction ?? 0) * 100)}%`,
                    background: toneColor[vt.tone === 'neutral' ? 'accent' : vt.tone],
                  }}
                />
              </div>
            </div>
          ))}
        </div>
        <div className="symp" role="radiogroup" aria-label="What were you seeing?">
          <span className="lbl" style={{ marginRight: 4 }}>
            What were you seeing?
          </span>
          {symptoms.map((s) => (
            <button
              key={s.id}
              role="radio"
              aria-checked={symptom === s.id}
              className={symptom === s.id ? 'on' : ''}
              onClick={() => {
                setSymptom(s.id);
                const first =
                  s.id === 'all'
                    ? diagnosis.findings[0]
                    : diagnosis.findings
                        .filter((f) => f.symptoms.includes(s.id as Symptom))
                        .sort((a, b) => b.score - a.score)[0];
                setOpen(new Set(first ? [first.id] : []));
              }}
            >
              {s.label}
            </button>
          ))}
        </div>
        <div className="finds">
          {ranked.length === 0 && (
            <div className="find">
              <div className="fh" style={{ cursor: 'default' }}>
                <Sev severity="ok" />
                <div>
                  <h4>No findings</h4>
                  <div className="why">None of the analyzers found anything worth reporting in this dump.</div>
                </div>
                <span />
              </div>
            </div>
          )}
          {ranked.map(({ f, match }) => (
            <FindingCard key={f.id} f={f} open={openSet.has(f.id)} dim={!match} onToggle={() => toggle(f.id)} />
          ))}
        </div>
      </div>
      {inspectorOpen ? <Inspector docked /> : <Side />}
    </div>
  );
}

function FindingCard({ f, open, dim, onToggle }: { f: Finding; open: boolean; dim: boolean; onToggle(): void }) {
  const pins = usePins();
  const entity = { uri: uri.finding(f.id), kind: 'finding' as const, label: f.title.replace(/`/g, ''), detail: null };
  const pinned = pins.isPinned(entity.uri);
  const copy = () => {
    const text = [
      plain(f.title, f.evidence).replace(/`/g, ''),
      '',
      plain(f.whatItMeans, f.evidence),
      '',
      'What to do next:',
      ...f.nextSteps.map((s, i) => `${i + 1}. ${plain(s, f.evidence)}`),
    ].join('\n');
    void navigator.clipboard?.writeText(text);
  };

  return (
    <div className={`find ${open ? 'open' : ''}`} style={{ opacity: dim ? 0.45 : 1 }}>
      <div
        className="fh"
        role="button"
        tabIndex={0}
        aria-expanded={open}
        onClick={onToggle}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onToggle()}
      >
        <Sev severity={f.severity} />
        <div>
          <h4>
            <Rich text={f.title} entities={f.evidence} />
          </h4>
          <div className="why">
            <Rich text={f.why} entities={f.evidence} />
          </div>
        </div>
        <Conf confidence={f.confidence} />
      </div>
      {open && (
        <div className="fb">
          <div className="cols">
            <div>
              <div className="lbl">What this means</div>
              <p style={{ margin: '6px 0 0' }}>
                <Rich text={f.whatItMeans} entities={f.evidence} />
              </p>
              <div className="lbl" style={{ marginTop: 14 }}>
                Evidence
              </div>
              <div className="ev">
                {f.evidence.map((e) => (
                  <Chip key={e.uri} entity={e} suffix={e.kind === 'thread' && e.detail ? ` ${e.detail}` : undefined} />
                ))}
              </div>
            </div>
            <div>
              <div className="lbl">What to do next</div>
              <ol>
                {f.nextSteps.map((s, i) => (
                  <li key={i}>
                    <Rich text={s} entities={f.evidence} />
                  </li>
                ))}
              </ol>
              <div style={{ marginTop: 12, display: 'flex', gap: 8 }}>
                {f.visual === 'waitGraph' && (
                  <button className="btn" onClick={() => navigate('locks')}>
                    Show graph
                  </button>
                )}
                {f.visual === 'heap' && (
                  <button className="btn" onClick={() => navigate('memory')}>
                    Show memory
                  </button>
                )}
                <button
                  className={`btn ${pinned ? 'pinned' : ''}`}
                  onClick={() => (pinned ? pins.unpin(entity.uri) : pins.pin(entity))}
                  aria-pressed={pinned}
                >
                  📌 {pinned ? 'Pinned' : 'Pin'}
                </button>
                <button className="btn ghost" onClick={copy}>
                  Copy
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function Side() {
  const diagnosis = useApp((s) => s.diagnosis)!;
  const pins = usePins();
  const inspect = useApp((s) => s.inspect);
  const graph = useWaitGraph(false);
  const cycle = graph.data?.cycles[0];

  return (
    <aside className="side">
      {cycle && graph.data && (
        <div className="blk">
          <div className="lbl">The deadlock at a glance</div>
          <MiniWaitGraph graph={graph.data} cycle={cycle} />
          <div style={{ fontSize: 12, color: 'var(--fg-3)', marginTop: 6 }}>
            — owns · <span style={{ color: 'var(--crit)' }}>- - waits for</span>
          </div>
        </div>
      )}
      <div className="blk">
        <div className="lbl">Busiest requests</div>
        {diagnosis.busiestRequests.length ? (
          <div className="kv" style={{ gridTemplateColumns: '1fr auto' }}>
            {diagnosis.busiestRequests.map((r) => (
              <FragmentRow key={r.route} route={r.route} n={r.threads} onClick={() => inspect(uri.request(r.route))} />
            ))}
          </div>
        ) : (
          <p className="conf" style={{ margin: '6px 0 0' }}>
            No HTTP requests found on thread stacks.
          </p>
        )}
      </div>
      <div className="blk">
        <div className="lbl">Pinned ({pins.pins.length})</div>
        {pins.pins.length === 0 && (
          <p className="conf" style={{ margin: '6px 0 0' }}>
            Pin threads, locks, objects or findings to collect them for the report. Right-click any chip, or press{' '}
            <span className="kbd">P</span>.
          </p>
        )}
        {pins.pins.map((p) => (
          <div className="pin" key={p.uri}>
            <Chip entity={refFor(p.uri, [{ uri: p.uri, kind: p.kind, label: p.label, detail: null }])} />
            <span className="note">{p.note || <i style={{ color: 'var(--fg-3)' }}>No note</i>}</span>
          </div>
        ))}
      </div>
    </aside>
  );
}

function FragmentRow({ route, n, onClick }: { route: string; n: number; onClick(): void }) {
  return (
    <>
      <button className="linklike mono" onClick={onClick}>
        {route}
      </button>
      <b style={{ color: 'var(--fg-0)' }}>{n}</b>
    </>
  );
}
