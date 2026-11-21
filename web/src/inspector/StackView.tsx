import { useState } from 'react';
import type { SourceSnippet, StackFrame } from '../contracts.gen';
import { useRpc } from '../state/queries';

/**
 * A thread's stack, app-first: the frame it's blocked in (highlighted), your code, and runs
 * of framework frames folded into "▸ N framework frames". "All" shows every frame, including
 * runtime helpers, for the expert who wants the raw view. A frame with a file and line opens
 * the source around it.
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

/** "Ledger.cs:42" when the PDB was found, else the IL offset for app frames. */
export const frameLocation = (f: StackFrame) =>
  f.sourceFile
    ? `${f.sourceFile}${f.line != null ? `:${f.line}` : ''}`
    : f.ilOffset != null && f.kind === 'app'
      ? `IL +0x${f.ilOffset.toString(16)}`
      : '';

function FrameRow({ f, hot }: { f: StackFrame; hot?: boolean }) {
  const [open, setOpen] = useState(false);
  const cls = hot ? 'hot' : f.kind === 'app' ? 'app' : 'fw';
  const where = frameLocation(f);
  const hasSource = !!f.sourcePath && f.line != null;
  return (
    <>
      <div className={cls} title={f.fullName ?? f.display}>
        {f.display}
        {hot && <span className="wait">waiting here</span>}
        {where &&
          (hasSource ? (
            <button
              className="src src-link"
              onClick={() => setOpen(!open)}
              aria-expanded={open}
              title={open ? 'Hide the source' : `Show the source around line ${f.line}`}
            >
              {where}
            </button>
          ) : (
            <span className="src">{where}</span>
          ))}
      </div>
      {open && hasSource && <Source path={f.sourcePath!} line={f.line!} />}
    </>
  );
}

function Source({ path, line }: { path: string; line: number }) {
  const { data, error, isLoading } = useRpc('source.snippet', { path, line, context: 6 });
  if (isLoading) return <div className="snippet-note">Reading source…</div>;
  if (error || !data) return <div className="snippet-note">{String((error as Error)?.message ?? 'No source.')}</div>;
  return <SourceLines snippet={data} />;
}

export const originLabel: Record<SourceSnippet['origin'], string> = {
  none: '',
  embedded: 'embedded in the PDB',
  disk: 'from disk',
  sourceFolder: 'from a source folder',
};

export function SourceLines({ snippet }: { snippet: SourceSnippet }) {
  if (snippet.unavailable) return <div className="snippet-note">{snippet.unavailable}</div>;
  const width = String(snippet.lines[snippet.lines.length - 1]?.number ?? 0).length;
  return (
    <div className="snippet" role="group" aria-label={`Source around line ${snippet.line}`}>
      <div className="snippet-h" title={snippet.resolvedPath ?? snippet.path}>
        {(snippet.resolvedPath ?? snippet.path).split(/[\\/]/).pop()} · {originLabel[snippet.origin]}
        {snippet.modified && <span className="snippet-warn"> · changed since this build, so the line may be off</span>}
      </div>
      <pre>
        {snippet.lines.map((l) => (
          <span key={l.number} className={l.number === snippet.line ? 'cur' : undefined}>
            <i>{String(l.number).padStart(width)}</i>
            {l.text || ' '}
            {'\n'}
          </span>
        ))}
      </pre>
    </div>
  );
}
