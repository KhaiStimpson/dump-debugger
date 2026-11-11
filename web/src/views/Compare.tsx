import { useState } from 'react';
import { create } from 'zustand';
import type { CompareCandidate, CompareResult, CompareSide, FindingChange } from '../contracts.gen';
import { Rich } from '../components/Rich';
import { Sev, toneColor } from '../components/Sev';
import { ago, bytes, count } from '../format';
import { RpcError, rpc } from '../rpc/client';
import { navigate } from '../router';
import { useRpc } from '../state/queries';
import { useApp } from '../state/store';

interface CompareState {
  baseline?: string;
  current?: string;
  result?: CompareResult;
  running: boolean;
  error?: string;
}

const useCompare = create<CompareState>(() => ({ running: false }));

/** What changed between dump A and B? Computed from cached snapshots, so A is never reopened. */
export function Compare() {
  const { result } = useCompare();
  return result ? <CompareResultView result={result} /> : <Picker />;
}

function Picker() {
  const dump = useApp((s) => s.dump);
  const progress = useApp((s) => s.progress);
  const { baseline, current, running, error } = useCompare();
  const candidates = useRpc('compare.candidates', {});
  const list = (candidates.data ?? []).filter((c) => c.exists);
  const openPath = dump?.metadata.dumpPath;
  const b = current ?? openPath;

  const run = async () => {
    if (!baseline) return;
    useCompare.setState({ running: true, error: undefined });
    try {
      const result = await rpc.call('compare.run', { baselinePath: baseline, currentPath: b ?? null });
      useCompare.setState({ result, running: false });
    } catch (e) {
      useCompare.setState({ running: false, error: e instanceof RpcError ? e.message : String(e) });
    }
  };

  return (
    <div className="view on" style={{ overflow: 'auto' }}>
      <div className="cmp-pick">
        <h2 style={{ fontSize: 22 }}>Compare two dumps</h2>
        <p style={{ color: 'var(--fg-2)', margin: '6px 0 18px' }}>
          Pick a baseline (A) taken when things were fine, and the current dump (B). Dumps you've opened before compare
          instantly from their cached snapshots; others are analyzed once.
        </p>
        <div className="cmp-cols">
          <CandidateList
            title="Baseline · A"
            list={list}
            selected={baseline}
            disabled={b}
            onPick={(p) => useCompare.setState({ baseline: p })}
          />
          <CandidateList
            title="Current · B"
            list={list}
            selected={b}
            disabled={baseline}
            openPath={openPath}
            onPick={(p) => useCompare.setState({ current: p })}
          />
        </div>
        {list.length < 2 && (
          <p className="conf">
            Open at least two dumps (or generate the "Memory: cached reports" sample twice, a while apart) to compare
            them.
          </p>
        )}
        {error && (
          <div className="callout bad-callout" role="alert">
            {error}
          </div>
        )}
        <div style={{ display: 'flex', gap: 10, alignItems: 'center', marginTop: 16 }}>
          <button className="btn pri" disabled={!baseline || !b || running} onClick={run}>
            {running ? 'Comparing…' : 'Compare'}
          </button>
          {running && progress && <span className="conf">{progress.stage}</span>}
        </div>
      </div>
    </div>
  );
}

function CandidateList(props: {
  title: string;
  list: CompareCandidate[];
  selected?: string;
  disabled?: string;
  openPath?: string;
  onPick(path: string): void;
}) {
  return (
    <div>
      <div className="lbl" style={{ marginBottom: 8 }}>
        {props.title}
      </div>
      <div className="card-list" role="radiogroup" aria-label={props.title}>
        {props.list.map((c) => {
          const on = c.path === props.selected;
          const off = c.path === props.disabled;
          return (
            <div
              key={c.path}
              className={`cand ${on ? 'on' : ''} ${off ? 'off' : ''}`}
              role="radio"
              aria-checked={on}
              aria-disabled={off}
              tabIndex={off ? -1 : 0}
              onClick={() => !off && props.onPick(c.path)}
              onKeyDown={(e) => e.key === 'Enter' && !off && props.onPick(c.path)}
            >
              <div>
                <div className="n">
                  {c.fileName}
                  {c.path === props.openPath && (
                    <span className="pill" style={{ marginLeft: 8 }}>
                      open
                    </span>
                  )}
                </div>
                <div className="s">
                  {c.runtime ?? ''} · opened {ago(c.openedAt)} ·{' '}
                  {c.hasSnapshot ? 'snapshot cached' : 'will be analyzed once'}
                </div>
              </div>
              {c.verdictSeverity && <Sev severity={c.verdictSeverity} label={c.verdictLabel ?? undefined} />}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function Card({ side, label, accent }: { side: CompareSide; label: string; accent?: boolean }) {
  return (
    <div className="dumpcard" style={accent ? { borderColor: 'var(--accent)' } : undefined}>
      <div className="lbl">{label}</div>
      <div className="n">{side.fileName}</div>
      <div className="s">
        heap {bytes(side.heapBytes)} · {side.threads} threads · {side.critical} critical
        {side.capturedAt ? ` · ${new Date(side.capturedAt).toISOString().slice(0, 16).replace('T', ' ')} UTC` : ''}
      </div>
    </div>
  );
}

const changeLabel: Record<FindingChange, string> = { new: 'New', resolved: 'Resolved', persisting: 'Still there' };

function CompareResultView({ result }: { result: CompareResult }) {
  const openDump = useApp((s) => s.openDump);
  const [allTypes, setAllTypes] = useState(false);
  const types = allTypes ? result.types : result.types.slice(0, 25);
  const maxDelta = Math.max(1, ...result.types.map((t) => Math.abs(t.sizeDelta)));

  const followInB = async (address?: number) => {
    if (!result.b.isOpen) {
      await openDump(result.b.path);
    }
    if (address != null) navigate('objects', `0x${address.toString(16)}`);
  };

  return (
    <div className="view on compare">
      <div className="cmp-h">
        <Card side={result.a} label="Baseline · A" />
        <div style={{ textAlign: 'center', color: 'var(--fg-3)', fontSize: 20 }}>→</div>
        <Card side={result.b} label="Current · B" accent />
      </div>
      <div className="cmp-sum">
        {result.summary.map((v) => (
          <div className="vital" key={v.key}>
            <div className="lbl">{v.label}</div>
            <div className="v" style={{ color: v.tone === 'neutral' ? undefined : toneColor[v.tone] }}>
              {v.value}
            </div>
            <div className="d">{v.detail}</div>
          </div>
        ))}
      </div>
      <div className="scroll">
        {result.leakSuspects.map((s) => (
          <div className="callout" style={{ margin: '16px 20px' }} key={s.typeName}>
            <b>Leak suspect:</b> <Rich text={s.explanation} />{' '}
            {s.sampleAddressesInB.length > 0 && (
              <button
                className="linklike"
                style={{ color: 'var(--accent)', fontFamily: 'var(--sans)' }}
                onClick={() => followInB(s.sampleAddressesInB[0])}
              >
                {result.b.isOpen ? 'Open retention path →' : 'Open B and follow the retention path →'}
              </button>
            )}
          </div>
        ))}
        <div className="delta-row lbl">
          <span>Type</span>
          <span className="r">Count A</span>
          <span className="r">Count B</span>
          <span className="r">Δ size</span>
          <span>Growth</span>
        </div>
        {types.map((t) => (
          <div
            className={`delta-row ${result.b.isOpen ? 'clickable' : ''}`}
            key={t.typeName}
            onClick={() => result.b.isOpen && navigate('memory', t.typeName)}
            title={result.b.isOpen ? `Show ${t.typeName} in Memory (B)` : t.typeName}
          >
            <span
              className="mono"
              style={{ color: 'var(--fg-0)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
            >
              {t.typeName}
            </span>
            <span className="r">{count(t.countA)}</span>
            <span className="r">{count(t.countB)}</span>
            <span className={`r ${t.sizeDelta > 0 ? 'up' : t.sizeDelta < 0 ? 'dn' : ''}`}>
              {t.sizeDelta > 0 ? '+' : t.sizeDelta < 0 ? '−' : '±'}
              {bytes(Math.abs(t.sizeDelta))}
            </span>
            <div className="dbar">
              <i
                style={{
                  width: `${Math.max(1, (Math.abs(t.sizeDelta) / maxDelta) * 100)}%`,
                  background:
                    t.sizeDelta > 0
                      ? t.growth != null && t.growth >= 3
                        ? 'var(--crit)'
                        : 'var(--warn)'
                      : t.sizeDelta < 0
                        ? 'var(--ok)'
                        : 'var(--fg-3)',
                }}
              />
            </div>
          </div>
        ))}
        {result.types.length > 25 && (
          <div style={{ padding: '10px 20px' }}>
            <button className="btn ghost" onClick={() => setAllTypes(!allTypes)}>
              {allTypes ? 'Show top 25' : `Show all ${result.types.length} changed types`}
            </button>
          </div>
        )}
        {result.threadGroups.length > 0 && (
          <>
            <div className="pane-h" style={{ borderTop: '1px solid var(--line)' }}>
              <h2 style={{ fontSize: 13 }}>Thread groups that changed</h2>
            </div>
            {result.threadGroups.map((g, i) => (
              <div className="delta-row" key={i}>
                <span
                  className="mono"
                  style={{ color: 'var(--fg-0)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                >
                  {g.where}
                </span>
                <span className="r">{g.countA}</span>
                <span className="r">{g.countB}</span>
                <span className={`r ${g.countB > g.countA ? 'up' : 'dn'}`}>
                  {g.countB > g.countA ? `+${g.countB - g.countA}` : g.countB - g.countA}
                </span>
                <span className="conf" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {g.stateLabel}
                  {g.request ? ` · ${g.request}` : ''}
                </span>
              </div>
            ))}
          </>
        )}
        <div className="pane-h" style={{ borderTop: '1px solid var(--line)' }}>
          <h2 style={{ fontSize: 13 }}>Findings</h2>
          <div className="r">
            <button className="btn ghost" onClick={() => useCompare.setState({ result: undefined })}>
              Change dumps
            </button>
          </div>
        </div>
        <div style={{ padding: '6px 20px 30px' }}>
          {result.findings.length === 0 && <p className="conf">Neither dump has findings.</p>}
          {result.findings.map((f, i) => (
            <div key={i} className="fdiff">
              <span className={`pill chg-${f.change}`}>{changeLabel[f.change]}</span>
              <Sev severity={f.severity} />
              <span>
                <Rich text={f.title} />
              </span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
