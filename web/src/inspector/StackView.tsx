import { useState } from 'react';
import type { StackFrame } from '../contracts.gen';

/**
 * A thread's stack, app-first: the frame it's blocked in (highlighted), your code, and runs
 * of framework frames folded into "▸ N framework frames". "All" shows every frame, including
 * runtime helpers, for the expert who wants the raw view.
 */
export function StackView({ frames }: { frames: StackFrame[] }) {
  const [mode, setMode] = useState<'app' | 'all'>('app');
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  const managed = frames.filter((f) => f.kind !== 'runtime');
  const hotIndex = managed.findIndex((f) => f.isWait);
  const rows: React.ReactNode[] = [];

  if (mode === 'all') {
    frames.forEach((f, i) =>
      rows.push(<FrameRow key={i} f={f} hot={f.isWait && i === frames.indexOf(managed[hotIndex])} />),
    );
  } else {
    let run: { start: number; items: StackFrame[] } | null = null;
    const flush = () => {
      if (!run) return;
      const r = run;
      if (expanded.has(r.start)) {
        r.items.forEach((f, j) => rows.push(<FrameRow key={`${r.start}-${j}`} f={f} />));
      } else {
        rows.push(
          <div
            key={`fold-${r.start}`}
            className="fold"
            role="button"
            tabIndex={0}
            onClick={() => setExpanded(new Set([...expanded, r.start]))}
            onKeyDown={(e) => e.key === 'Enter' && setExpanded(new Set([...expanded, r.start]))}
          >
            ▸ {r.items.length} framework frame{r.items.length === 1 ? '' : 's'}
          </div>,
        );
      }
      run = null;
    };

    managed.forEach((f, i) => {
      if (i === hotIndex) {
        flush();
        rows.push(<FrameRow key={i} f={f} hot />);
      } else if (f.kind === 'app') {
        flush();
        rows.push(<FrameRow key={i} f={f} />);
      } else if (i < hotIndex) {
        // Framework frames above the blocking call add nothing for the app view.
      } else {
        run ??= { start: i, items: [] };
        run.items.push(f);
      }
    });
    flush();
  }

  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center' }}>
        <span className="lbl">Stack</span>
        <span style={{ marginLeft: 'auto' }} className="seg">
          <button className={mode === 'app' ? 'on' : ''} onClick={() => setMode('app')}>
            App frames
          </button>
          <button className={mode === 'all' ? 'on' : ''} onClick={() => setMode('all')}>
            All
          </button>
        </span>
      </div>
      <div className="stack">{rows.length ? rows : <div className="fw">No managed frames</div>}</div>
    </>
  );
}

function FrameRow({ f, hot }: { f: StackFrame; hot?: boolean }) {
  const cls = hot ? 'hot' : f.kind === 'app' ? 'app' : 'fw';
  const where = f.sourceFile
    ? `${f.sourceFile}:${f.line}`
    : f.ilOffset != null && f.kind === 'app'
      ? `IL +0x${f.ilOffset.toString(16)}`
      : '';
  return (
    <div className={cls} title={f.fullName ?? f.display}>
      {f.display}
      {hot && <span style={{ opacity: 0.6 }}> ← waiting</span>}
      {where && <span className="src">{where}</span>}
    </div>
  );
}
