import { useVirtualizer } from '@tanstack/react-virtual';
import { useMemo, useRef, useState } from 'react';
import type { HeapGrouping, HeapSort, TreemapCell } from '../contracts.gen';
import { squarify } from '../components/treemap';
import { shortType } from '../entities';
import { bytes, count } from '../format';
import { navigate, useRoute } from '../router';
import { useRpc } from '../state/queries';

const groupings: { id: HeapGrouping; label: string }[] = [
  { id: 'type', label: 'By type' },
  { id: 'generation', label: 'By generation' },
  { id: 'namespace', label: 'By namespace' },
];

// Teal ramp from the mockup treemap, largest cell darkest-saturated.
const shades = ['#0f766e', '#115e59', '#134e4a', '#1e3a3a', '#1a3436', '#1c3133', '#1d2e31', '#1e2c30', '#1f2f33'];
const ROW = 37;

/** What's eating memory? Treemap and type table from the worker's aggregates; never raw objects. */
export function Memory() {
  const { arg: selected } = useRoute();
  const [groupBy, setGroupBy] = useState<HeapGrouping>('type');
  const [sort, setSort] = useState<HeapSort>('size');
  const [filter, setFilter] = useState('');
  const summary = useRpc('heap.summary', {});
  const types = useRpc('heap.types', { groupBy, filter: filter.trim() || null, offset: 0, limit: 1000, sort });
  const parent = useRef<HTMLDivElement>(null);
  const rows = types.data?.items ?? [];
  const max = Math.max(1, ...rows.map((r) => (sort === 'count' ? r.count : r.size)));

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parent.current,
    estimateSize: () => ROW,
    overscan: 10,
  });
  const ov = summary.data?.overview;
  const select = (name: string) => groupBy === 'type' && navigate('memory', name === selected ? undefined : name);

  return (
    <div className="view on memory">
      <div className="mem-main">
        <div className="pane-h">
          <h3>Managed heap</h3>
          {ov && (
            <span className="pill">
              {bytes(ov.totalBytes)} · {count(ov.objectCount)} objects
            </span>
          )}
          {summary.data && summary.data.freeBytes > 0 && (
            <span className="pill" title="Free space between objects (fragmentation)">
              {bytes(summary.data.freeBytes)} free
            </span>
          )}
          <div className="r">
            <input
              className="filter"
              placeholder="Filter types"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              aria-label="Filter types"
            />
            <div className="seg" role="radiogroup" aria-label="Grouping">
              {groupings.map((g) => (
                <button
                  key={g.id}
                  role="radio"
                  aria-checked={groupBy === g.id}
                  className={groupBy === g.id ? 'on' : ''}
                  onClick={() => setGroupBy(g.id)}
                >
                  {g.label}
                </button>
              ))}
            </div>
          </div>
        </div>
        {summary.data && <Treemap cells={summary.data.treemap} selected={selected} onSelect={select} />}
        <div className="bar-row lbl head">
          <button className="linklike" onClick={() => setSort('name')}>
            {groupBy === 'type' ? 'Type' : groupBy === 'namespace' ? 'Namespace' : 'Generation'}{' '}
            {sort === 'name' && '↓'}
          </button>
          <button className="linklike r" onClick={() => setSort('count')}>
            Count {sort === 'count' && '↓'}
          </button>
          <button className="linklike r" onClick={() => setSort('size')}>
            Size {sort === 'size' && '↓'}
          </button>
          <span>Share</span>
        </div>
        <div className="scroll" ref={parent} role="listbox" aria-label="Types">
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((v) => {
              const r = rows[v.index];
              return (
                <div
                  key={r.name}
                  className={`bar-row ${r.name === selected ? 'sel' : ''} ${groupBy === 'type' ? 'clickable' : ''}`}
                  role="option"
                  aria-selected={r.name === selected}
                  style={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    right: 0,
                    height: ROW,
                    transform: `translateY(${v.start}px)`,
                  }}
                  onClick={() => select(r.name)}
                  title={r.name}
                >
                  <span className="ty" style={r.isApp ? { color: 'var(--fg-0)' } : { color: 'var(--fg-1)' }}>
                    {r.name}
                  </span>
                  <span className="r">{count(r.count)}</span>
                  <span className="r">{bytes(r.size)}</span>
                  <div className="hbar">
                    <i style={{ width: `${Math.max(0.5, ((sort === 'count' ? r.count : r.size) / max) * 100)}%` }} />
                  </div>
                </div>
              );
            })}
          </div>
          {types.data && rows.length === 0 && <div className="empty-row">No types match.</div>}
        </div>
      </div>
      {selected ? <Instances typeName={selected} /> : <LargestObjects />}
    </div>
  );
}

function Treemap({
  cells,
  selected,
  onSelect,
}: {
  cells: TreemapCell[];
  selected?: string;
  onSelect(name: string): void;
}) {
  const W = 1000;
  const H = 260;
  const rects = useMemo(
    () =>
      squarify(
        cells.map((c) => c.size),
        { x: 0, y: 0, w: W, h: H },
      ),
    [cells],
  );
  return (
    <div className="treemap-box" role="img" aria-label="Heap treemap by type">
      {cells.map((c, i) => {
        const r = rects[i];
        if (r.w < 1 || r.h < 1) return null;
        return (
          <button
            key={c.name}
            className={`tm ${c.name === selected ? 'on' : ''}`}
            style={{
              left: `${(r.x / W) * 100}%`,
              top: `${(r.y / H) * 100}%`,
              width: `${(r.w / W) * 100}%`,
              height: `${(r.h / H) * 100}%`,
              background: c.isOther ? '#1f2f33' : (shades[i] ?? shades[shades.length - 1]),
            }}
            onClick={() => !c.isOther && onSelect(c.name)}
            title={`${c.name} · ${c.label} · ${Math.round(c.share * 100)}%`}
            disabled={c.isOther}
          >
            <b>{c.isOther ? c.name : shortType(c.name)}</b>
            <span>
              {c.label}
              {c.share >= 0.1 ? ` · ${Math.round(c.share * 100)}%` : ''}
            </span>
          </button>
        );
      })}
    </div>
  );
}

function LargestObjects() {
  const { data, isLoading } = useRpc('heap.largeObjects', {});
  return (
    <aside className="insp">
      <div className="ih">
        <div className="lbl">Largest objects, identified</div>
        <p style={{ margin: '6px 0 0', color: 'var(--fg-2)', fontSize: 12.5 }}>
          Named by reading their own fields and their owner.
        </p>
      </div>
      {isLoading && <div className="ib conf">Indexing references…</div>}
      {data?.length === 0 && <div className="ib conf">No objects over 85 KB (nothing on the Large Object Heap).</div>}
      {data?.map((o) => (
        <div
          key={o.uri}
          className="lobj"
          role="button"
          tabIndex={0}
          onClick={() => navigate('objects', `0x${o.address.toString(16)}`)}
          onKeyDown={(e) => e.key === 'Enter' && navigate('objects', `0x${o.address.toString(16)}`)}
        >
          <div className="id">{o.identity}</div>
          <div className="sub">
            {shortType(o.typeName)} · {bytes(o.size)} · {o.generation}
            {o.pinned ? ' · pinned' : ''} · 0x{o.address.toString(16)}
            {o.detail ? ` · ${o.detail}` : ''}
          </div>
        </div>
      ))}
    </aside>
  );
}

function Instances({ typeName }: { typeName: string }) {
  const { data, isLoading } = useRpc('heap.instances', { typeName, offset: 0, limit: 100 });
  return (
    <aside className="insp">
      <div className="ih">
        <button
          className="linklike"
          style={{ fontFamily: 'var(--sans)', color: 'var(--fg-3)', fontSize: 12 }}
          onClick={() => navigate('memory')}
        >
          ← Largest objects
        </button>
        <div className="lbl" style={{ marginTop: 8 }}>
          Instances, largest first
        </div>
        <div className="mono" style={{ color: 'var(--fg-0)', marginTop: 4, wordBreak: 'break-all' }}>
          {typeName}
        </div>
        {data && (
          <p className="conf" style={{ margin: '4px 0 0' }}>
            {count(data.total)} instance{data.total === 1 ? '' : 's'}
            {data.total > data.items.length ? ` · showing ${data.items.length}` : ''}
          </p>
        )}
      </div>
      {isLoading && <div className="ib conf">Indexing references…</div>}
      {data?.items.map((o) => (
        <div
          key={o.uri}
          className="lobj"
          role="button"
          tabIndex={0}
          onClick={() => navigate('objects', `0x${o.address.toString(16)}`)}
          onKeyDown={(e) => e.key === 'Enter' && navigate('objects', `0x${o.address.toString(16)}`)}
        >
          <div className="id">{o.identity}</div>
          <div className="sub">
            {bytes(o.size)} · {o.generation} · 0x{o.address.toString(16)}
          </div>
        </div>
      ))}
    </aside>
  );
}
