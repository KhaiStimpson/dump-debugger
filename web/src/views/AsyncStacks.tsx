import { useState } from 'react';
import type { AsyncStackGroup } from '../contracts.gen';
import { Chip } from '../components/Chip';
import { count } from '../format';
import { uri } from '../entities';
import { useRpc } from '../state/queries';

/**
 * Async "logical stacks": operations that are awaiting, so they're on no thread at all. Each
 * row is a group with the same chain of async methods and the same thing awaited; the panel
 * shows the chain innermost-first.
 */
export function AsyncStacks({ header }: { header: React.ReactNode }) {
  const { data, isLoading } = useRpc('async.stacks', {});
  const [selected, setSelected] = useState(0);
  const groups = data?.groups ?? [];
  const current = groups[selected];

  return (
    <>
      <div className="threads-main">
        {header}
        <div className="thead lbl">
          <span>#</span>
          <span>Your code (first app frame)</span>
          <span>Awaiting</span>
          <span>State</span>
        </div>
        <div className="scroll" role="listbox" aria-label="Async stack groups">
          {isLoading && <div className="empty-row">Walking async state machines…</div>}
          {data && groups.length === 0 && <div className="empty-row">No pending async operations.</div>}
          {groups.map((g, i) => (
            <div
              key={i}
              className={`trow ${i === selected ? 'sel' : ''}`}
              role="option"
              aria-selected={i === selected}
              onClick={() => setSelected(i)}
            >
              <span className="cnt">{count(g.count)}</span>
              <span
                className="frame"
                style={g.firstAppFrame ? undefined : { color: 'var(--fg-3)' }}
                title={g.frames.join(' ← ')}
              >
                {g.firstAppFrame ?? g.frames[0]}
              </span>
              <span className="op" title={g.awaiting}>
                awaiting {g.awaiting}
              </span>
              <span className="state st-wait">Suspended</span>
            </div>
          ))}
        </div>
      </div>
      {current ? <AsyncChain group={current} pending={data?.pending ?? 0} /> : <aside className="insp" />}
    </>
  );
}

function AsyncChain({ group, pending }: { group: AsyncStackGroup; pending: number }) {
  return (
    <aside className="insp" aria-label="Async logical stack">
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 16 }}>{count(group.count)} async operations</h3>
          <span className="state st-wait">Suspended</span>
        </div>
        <dl className="kv">
          <dt>Awaiting</dt>
          <dd>{group.awaiting}</dd>
          <dt>Your code</dt>
          <dd className="mono">{group.firstAppFrame ?? '—'}</dd>
          <dt>In this dump</dt>
          <dd>{count(pending)} pending async operations in total</dd>
        </dl>
      </div>
      <div className="ib">
        <span className="lbl">Logical stack (innermost first)</span>
        <div className="stack">
          {group.frames.map((f, i) => (
            <div key={i} className={f === group.firstAppFrame ? 'app' : i === 0 ? 'hot' : 'fw'}>
              {i === 0 ? `${f}  ← awaiting ${group.awaiting}` : f}
            </div>
          ))}
        </div>
        <p className="conf" style={{ marginTop: 10 }}>
          These operations aren't on any thread: each is a state machine on the heap, resumed when what it awaits
          completes.
        </p>
      </div>
      {group.samples.length > 0 && (
        <div className="ib">
          <span className="lbl">Examples</span>
          <div className="ev">
            {group.samples.map((a) => (
              <Chip
                key={a}
                entity={{ uri: uri.object(a), kind: 'object', label: `0x${a.toString(16)}`, detail: group.frames[0] }}
              />
            ))}
          </div>
        </div>
      )}
    </aside>
  );
}
