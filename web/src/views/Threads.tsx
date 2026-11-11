import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { ThreadGroup, ThreadGrouping, ThreadStateFilter } from '../contracts.gen';
import { StatePill } from '../components/Sev';
import { threadIdOf, uri } from '../entities';
import { Inspector } from '../inspector/Inspector';
import { useRpc } from '../state/queries';
import { AsyncStacks } from './AsyncStacks';
import { useApp } from '../state/store';

const groupings: { id: ThreadGrouping; label: string }[] = [
  { id: 'stack', label: 'By stack' },
  { id: 'request', label: 'By request' },
  { id: 'flat', label: 'Flat' },
];

const filters: { id: ThreadStateFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'blocked', label: 'Blocked' },
  { id: 'waiting', label: 'Waiting' },
  { id: 'running', label: 'Running' },
  { id: 'idle', label: 'Idle' },
];

const ROW = 28;

/**
 * What was every thread doing? Groups keyed on the first application frame, so 200 threads
 * blocked in one place read as one row. The list is virtualized (the worker returns groups,
 * the page renders only what's on screen).
 */
export function Threads() {
  const [groupBy, setGroupBy] = useState<ThreadGrouping>('stack');
  const [mode, setMode] = useState<'threads' | 'async'>('threads');
  const [state, setState] = useState<ThreadStateFilter>('all');
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  const inspect = useApp((s) => s.inspect);
  const inspector = useApp((s) => s.inspector);
  const selectedId = inspector.open ? threadIdOf(inspector.history[inspector.index] ?? '') : undefined;
  const parent = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(search.trim()), 150);
    return () => clearTimeout(t);
  }, [search]);

  const { data } = useRpc('threads.groups', { groupBy, state, search: debounced || null });
  const groups = useMemo(() => data?.groups ?? [], [data]);
  const counts = new Map(data?.counts.map((c) => [c.state, c.count]));

  const virtualizer = useVirtualizer({
    count: groups.length,
    getScrollElement: () => parent.current,
    estimateSize: () => ROW,
    overscan: 12,
  });

  const selectedIndex = groups.findIndex((g) => selectedId != null && g.threadIds.includes(selectedId));
  const select = (g: ThreadGroup | undefined) => g && inspect(uri.thread(g.threadIds[0]));

  // Open the most interesting group by default so the inspector is never empty on arrival.
  useEffect(() => {
    if (!inspector.open && groups.length) select(groups[0]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [groups.length > 0]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.ctrlKey || e.altKey || e.metaKey) return;
      const k = e.key.toLowerCase();
      if (k !== 'j' && k !== 'k' && e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
      e.preventDefault();
      const down = k === 'j' || e.key === 'ArrowDown';
      const next = Math.min(groups.length - 1, Math.max(0, (selectedIndex < 0 ? -1 : selectedIndex) + (down ? 1 : -1)));
      select(groups[next]);
      virtualizer.scrollToIndex(next, { align: 'auto' });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

  const threadsShown = groups.reduce((n, g) => n + g.count, 0);
  const header = (
    <div className="pane-h">
      <h2>Threads</h2>
      <span className="pill">
        {threadsShown} thread{threadsShown === 1 ? '' : 's'} · {groups.length} {groupBy === 'flat' ? 'rows' : 'groups'}
      </span>
      <input
        className="filter"
        placeholder="Filter by frame, request or #id"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        aria-label="Filter threads"
      />
      <div className="r">
        <div className="seg" role="radiogroup" aria-label="Grouping">
          {groupings.map((g) => (
            <button
              key={g.id}
              role="radio"
              aria-checked={mode === 'threads' && groupBy === g.id}
              className={mode === 'threads' && groupBy === g.id ? 'on' : ''}
              onClick={() => {
                setMode('threads');
                setGroupBy(g.id);
              }}
            >
              {g.label}
            </button>
          ))}
          <button
            role="radio"
            aria-checked={mode === 'async'}
            className={mode === 'async' ? 'on' : ''}
            onClick={() => setMode('async')}
          >
            Async stacks
          </button>
        </div>
        <div className="seg" role="radiogroup" aria-label="State filter">
          {filters.map((f) => (
            <button
              key={f.id}
              role="radio"
              aria-checked={state === f.id}
              className={state === f.id ? 'on' : ''}
              onClick={() => setState(f.id)}
            >
              {f.label} <span className="n">{counts.get(f.id) ?? ''}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );

  if (mode === 'async') {
    return (
      <div className="view on threads">
        <AsyncStacks header={header} />
      </div>
    );
  }

  return (
    <div className="view on threads">
      <div className="threads-main">
        {header}
        <div className="thead">
          <span>{groupBy === 'flat' ? 'Thread' : 'Count'}</span>
          <span>Where (first app frame)</span>
          <span>Request</span>
          <span>State</span>
        </div>
        <div className="scroll" ref={parent} role="listbox" aria-label="Thread groups" tabIndex={0}>
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((v) => {
              const g = groups[v.index];
              const sel = v.index === selectedIndex;
              return (
                <div
                  key={g.key}
                  className={`trow ${sel ? 'sel' : ''}`}
                  role="option"
                  aria-selected={sel}
                  style={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    right: 0,
                    height: ROW,
                    transform: `translateY(${v.start}px)`,
                  }}
                  onClick={() => select(g)}
                >
                  <span className="cnt">{groupBy === 'flat' ? `#${g.threadIds[0]}` : g.count}</span>
                  <span className="frame" style={g.whereIsApp ? undefined : { color: 'var(--fg-3)' }} title={g.where}>
                    {g.where}
                  </span>
                  <span className="op" title={g.request ?? undefined}>
                    {g.request ? <Request route={g.request} /> : ''}
                  </span>
                  <StatePill state={g.state} label={g.stateLabel} />
                </div>
              );
            })}
          </div>
          {data && groups.length === 0 && <div className="empty-row">No threads match.</div>}
        </div>
      </div>
      <Inspector docked />
      {!inspector.open && <aside className="insp" />}
    </div>
  );
}

function Request({ route }: { route: string }) {
  const [verb, ...rest] = route.split(' ');
  if (!/^[A-Z]+$/.test(verb)) return <>{route}</>;
  return (
    <>
      <b>{verb}</b> {rest.join(' ')}
    </>
  );
}
