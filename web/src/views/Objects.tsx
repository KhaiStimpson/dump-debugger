import { useState } from 'react';
import type { EntityRef, FieldValue, RootPath, RootPathMode } from '../contracts.gen';
import { Rich } from '../components/Rich';
import { Sev } from '../components/Sev';
import { shortType } from '../entities';
import { bytes, count, seconds } from '../format';
import { NoteEditor, PinButtons } from '../inspector/Inspector';
import { navigate, useRoute } from '../router';
import { useRpc } from '../state/queries';

/** What is this thing, and why is it alive? Fields on the left, the path to a GC root on the right. */
export function Objects() {
  const { arg } = useRoute();
  const address = arg ? parseInt(arg.replace(/^0x/, ''), 16) : undefined;
  if (address == null || Number.isNaN(address)) return <PickObject />;
  return (
    <div className="view on objects" key={address}>
      <ObjectPane address={address} />
      <RootPane address={address} />
    </div>
  );
}

const go = (address: number) => navigate('objects', `0x${address.toString(16)}`);

function ObjectPane({ address }: { address: number }) {
  const { data: o, error, isLoading } = useRpc('objects.inspect', { address, elementOffset: 0, elementLimit: 100 });
  const shortest = useRpc('objects.roots', { address, mode: 'shortest', maxPaths: 1 });
  const referrers = useRpc('objects.referrers', { address, offset: 0, limit: 50 });

  if (isLoading) return <div className="scroll pad">Reading object 0x{address.toString(16)}…</div>;
  if (error || !o)
    return (
      <div className="scroll pad">
        Couldn't read 0x{address.toString(16)}: {String((error as Error)?.message ?? error)}
      </div>
    );

  const entity: EntityRef = { uri: o.uri, kind: 'object', label: o.identity, detail: o.typeName };
  const crumbs = shortest.data?.paths[0]?.steps.slice(0, -1) ?? [];
  const root = shortest.data?.paths[0]?.root;

  return (
    <div className="scroll obj-left">
      <div className="pane-h">
        <h2 className="obj-title" title={o.identity}>
          {o.identity}
        </h2>
        <span className="pill">
          {shortType(o.typeName)} · {bytes(o.size)} · {o.generation}
        </span>
        <div className="r">
          <PinButtons entity={entity} />
        </div>
      </div>
      {root && (
        <div className="crumbs" aria-label="Path from root">
          <span>{root.label}</span>
          {crumbs.slice(1).map((s) => (
            <span key={s.object.uri}>
              {' › '}
              <button className="linklike" onClick={() => go(addr(s.object.uri))}>
                {s.object.label}
              </button>
            </span>
          ))}
          <span className="cur"> › {shortType(o.typeName)}</span>
        </div>
      )}
      <NoteEditor entity={entity} />
      <div className="mono addr">
        0x{o.address.toString(16)} · {o.typeName}
      </div>
      {o.stringValue != null && (
        <div className="strval">
          <div className="lbl">Value ({count(o.length ?? 0)} chars)</div>
          <pre>{o.stringValue}</pre>
        </div>
      )}
      {o.fields.length > 0 && (
        <div className="fields">
          <div className="fr lbl" style={{ fontFamily: 'var(--sans)' }}>
            <span />
            <span>Field</span>
            <span>Type</span>
            <span>Value</span>
          </div>
          {o.fields.map((f) => (
            <FieldRow key={f.name} f={f} depth={0} />
          ))}
        </div>
      )}
      {o.preview && (
        <div className="fields">
          <div className="fr">
            <span />
            <span className="nm" style={{ color: 'var(--fg-2)' }}>
              [0..15]
            </span>
            <span className="ty">preview</span>
            <span className="val">{o.preview}</span>
          </div>
        </div>
      )}
      {o.elements && o.elements.length > 0 && <Elements address={address} length={o.length ?? 0} first={o.elements} />}
      <div className="pane-h" style={{ borderTop: '1px solid var(--line)', marginTop: 10 }}>
        <h2 style={{ fontSize: 13 }}>Referenced by ({referrers.data?.total ?? '…'})</h2>
      </div>
      <div className="fields">
        {referrers.data?.items.map((r) => (
          <div className="fr" key={r.object.uri}>
            <span />
            <span className="nm">{r.via}</span>
            <span className="ty">{r.typeName}</span>
            <span className="val">
              <button className="chip o" onClick={() => go(addr(r.object.uri))}>
                {r.object.label}
              </button>
            </span>
          </div>
        ))}
        {referrers.data?.total === 0 && (
          <div className="fr empty">Nothing on the heap references this object (it may be held only by a root).</div>
        )}
      </div>
    </div>
  );
}

function FieldRow({ f, depth }: { f: FieldValue; depth: number }) {
  const [open, setOpen] = useState(false);
  const expandable = (f.kind === 'reference' && f.address != null) || (f.kind === 'struct' && !!f.children?.length);
  const valClass =
    f.kind === 'string' ? 'val s' : f.kind === 'number' || f.kind === 'dateTime' || f.kind === 'bool' ? 'val n' : 'val';
  return (
    <>
      <div className="fr">
        <span
          className="tw"
          role={expandable ? 'button' : undefined}
          tabIndex={expandable ? 0 : undefined}
          onClick={() => expandable && setOpen(!open)}
          onKeyDown={(e) => e.key === 'Enter' && expandable && setOpen(!open)}
          aria-expanded={expandable ? open : undefined}
        >
          {expandable ? (open ? '▾' : '▸') : ''}
        </span>
        <span className="nm" style={{ paddingLeft: depth * 16 }}>
          {f.name}
        </span>
        <span className="ty">{f.typeName}</span>
        <span className={valClass} style={f.kind === 'null' && f.note ? { color: 'var(--crit)' } : undefined}>
          {f.kind === 'reference' && f.address != null ? (
            <>
              <span>{f.display}</span>{' '}
              <button className="chip o" onClick={() => go(f.address!)} title="Open this object">
                0x{f.address.toString(16)}
              </button>
            </>
          ) : (
            f.display
          )}
          {f.note && <span style={{ color: 'var(--crit)' }}> — {f.note}</span>}
        </span>
      </div>
      {open && f.kind === 'struct' && f.children?.map((c) => <FieldRow key={c.name} f={c} depth={depth + 1} />)}
      {open && f.kind === 'reference' && f.address != null && <NestedFields address={f.address} depth={depth + 1} />}
    </>
  );
}

function NestedFields({ address, depth }: { address: number; depth: number }) {
  const { data } = useRpc('objects.inspect', { address, elementOffset: 0, elementLimit: 20 });
  if (!data)
    return (
      <div className="fr">
        <span />
        <span className="nm" style={{ paddingLeft: depth * 16, color: 'var(--fg-3)' }}>
          …
        </span>
      </div>
    );
  const rows = data.fields.length ? data.fields : (data.elements ?? []);
  if (data.stringValue != null)
    return (
      <div className="fr">
        <span />
        <span className="nm" style={{ paddingLeft: depth * 16, color: 'var(--fg-2)' }}>
          value
        </span>
        <span className="ty">string</span>
        <span className="val s">"{data.stringValue}"</span>
      </div>
    );
  return (
    <>
      {rows.slice(0, 40).map((c) => (
        <FieldRow key={c.name} f={c} depth={depth} />
      ))}
    </>
  );
}

function Elements({ address, length, first }: { address: number; length: number; first: FieldValue[] }) {
  const [pages, setPages] = useState(1);
  return (
    <div className="fields">
      <div className="fr lbl" style={{ fontFamily: 'var(--sans)' }}>
        <span />
        <span>Element</span>
        <span>Type</span>
        <span>Value ({count(length)} total)</span>
      </div>
      {first.map((f) => (
        <FieldRow key={f.name} f={f} depth={0} />
      ))}
      {Array.from({ length: pages - 1 }, (_, i) => (
        <ElementPage key={i} address={address} offset={(i + 1) * 100} />
      ))}
      {pages * 100 < length && (
        <div className="fr">
          <span />
          <button className="linklike" style={{ fontFamily: 'var(--sans)' }} onClick={() => setPages(pages + 1)}>
            Show 100 more
          </button>
        </div>
      )}
    </div>
  );
}

function ElementPage({ address, offset }: { address: number; offset: number }) {
  const { data } = useRpc('objects.inspect', { address, elementOffset: offset, elementLimit: 100 });
  return (
    <>
      {data?.elements?.map((f) => (
        <FieldRow key={f.name} f={f} depth={0} />
      ))}
    </>
  );
}

function RootPane({ address }: { address: number }) {
  const [mode, setMode] = useState<RootPathMode>('shortest');
  const { data, isLoading, error } = useRpc('objects.roots', { address, mode, maxPaths: mode === 'all' ? 10 : 1 });
  const all = useRpc('objects.roots', { address, mode: 'all', maxPaths: 10 }, mode === 'all' || !!data);

  return (
    <div className="scroll">
      <div className="pane-h">
        <h2>Why is it alive?</h2>
        <span className="pill">{mode === 'shortest' ? 'shortest path to a GC root' : 'all paths'}</span>
        <div className="r">
          <div className="seg" role="radiogroup" aria-label="Paths">
            <button
              role="radio"
              aria-checked={mode === 'shortest'}
              className={mode === 'shortest' ? 'on' : ''}
              onClick={() => setMode('shortest')}
            >
              Shortest
            </button>
            <button
              role="radio"
              aria-checked={mode === 'all'}
              className={mode === 'all' ? 'on' : ''}
              onClick={() => setMode('all')}
            >
              All paths{all.data ? ` (${all.data.paths.length}${all.data.truncated ? '+' : ''})` : ''}
            </button>
          </div>
        </div>
      </div>
      <div className="root-path">
        {isLoading && <p className="conf">Searching back from the object to the GC roots…</p>}
        {error && <p className="conf">{String((error as Error).message ?? error)}</p>}
        {data?.unrooted && (
          <div className="rp-node">
            <Sev severity="ok" label="Unrooted" />
            <span className="ty">Garbage awaiting collection</span>
          </div>
        )}
        {data?.paths.map((p, i) => (
          <Path key={i} path={p} first={i === 0} />
        ))}
        {data && (
          <div className="callout" style={{ marginTop: 22 }}>
            <Rich text={data.explanation} />
            <div className="conf" style={{ marginTop: 6 }}>
              Found in {seconds(data.seconds)}
              {data.truncated ? ' · search limit reached' : ''}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function Path({ path, first }: { path: RootPath; first: boolean }) {
  return (
    <div className="rp" style={first ? undefined : { marginTop: 26 }}>
      <div className="rp-node root">
        <Sev severity="warning" label="Root" />
        <span className="ty">{path.root.label}</span>
        <span className="f">{path.root.detail.replace(/`/g, '')}</span>
      </div>
      {path.steps.map((s, i) => {
        const last = i === path.steps.length - 1;
        return (
          <div key={s.object.uri}>
            <div className="rp-edge">→ {i === 0 ? 'value' : path.steps[i - 1].via}</div>
            <div
              className={`rp-node ${last ? 'target' : ''}`}
              role="button"
              tabIndex={0}
              onClick={() => !last && go(addr(s.object.uri))}
              onKeyDown={(e) => e.key === 'Enter' && !last && go(addr(s.object.uri))}
              style={last ? undefined : { cursor: 'pointer' }}
            >
              {last && <Sev severity="ok" label="This" />}
              <span className="ty">{s.object.label}</span>
              <span className="f">
                {shortType(s.typeName)}
                {s.note ? ` · ${s.note}` : ''}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function PickObject() {
  const { data } = useRpc('heap.largeObjects', {});
  return (
    <div className="view on" style={{ placeItems: 'center' }}>
      <div style={{ width: 640 }}>
        <h2 style={{ fontSize: 22 }}>Pick an object</h2>
        <p style={{ color: 'var(--fg-2)' }}>
          Open any object chip, an instance from Memory, or one of the largest objects below. Search by address with{' '}
          <span className="kbd">Ctrl</span> <span className="kbd">K</span>.
        </p>
        <div className="card-list">
          {data?.slice(0, 8).map((o) => (
            <div
              key={o.uri}
              className="lobj"
              role="button"
              tabIndex={0}
              onClick={() => go(o.address)}
              onKeyDown={(e) => e.key === 'Enter' && go(o.address)}
            >
              <div className="id">{o.identity}</div>
              <div className="sub">
                {shortType(o.typeName)} · {bytes(o.size)} · {o.generation}
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function addr(u: string): number {
  return parseInt(u.slice(u.indexOf('/') + 3), 16);
}
