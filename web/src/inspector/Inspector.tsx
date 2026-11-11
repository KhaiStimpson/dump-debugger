import { useEffect } from 'react';
import type { EntityRef, Finding, WaitGraph } from '../contracts.gen';
import { Chip } from '../components/Chip';
import { Rich } from '../components/Rich';
import { Conf, Sev, StatePill } from '../components/Sev';
import { addressOf, fallbackLabel, keyOf, kindOf, refFor, shortType, threadIdOf, uri } from '../entities';
import { navigate } from '../router';
import { usePins } from '../state/pins';
import { useRpc } from '../state/queries';
import { useApp } from '../state/store';
import { StackView } from './StackView';

/**
 * The shared right-hand inspector. Any chip anywhere opens its entity here instead of
 * navigating away; Alt+←/→ walk the history.
 */
export function Inspector({ docked = false }: { docked?: boolean }) {
  const { history, index, open } = useApp((s) => s.inspector);
  const back = useApp((s) => s.back);
  const forward = useApp((s) => s.forward);
  const close = useApp((s) => s.closeInspector);
  const current = history[index];

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.altKey && e.key === 'ArrowLeft') back();
      if (e.altKey && e.key === 'ArrowRight') forward();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [back, forward]);

  if (!open || !current) return null;

  return (
    <aside className={`insp ${docked ? '' : 'overlay'}`} aria-label="Inspector">
      <div className="insp-nav">
        <button
          className="btn ghost"
          onClick={back}
          disabled={index <= 0}
          aria-label="Back (Alt+Left)"
          title="Back (Alt+←)"
        >
          ←
        </button>
        <button
          className="btn ghost"
          onClick={forward}
          disabled={index >= history.length - 1}
          aria-label="Forward (Alt+Right)"
          title="Forward (Alt+→)"
        >
          →
        </button>
        <span className="crumb">{fallbackLabel(current)}</span>
        <button className="btn ghost" style={{ marginLeft: 'auto' }} onClick={close} aria-label="Close inspector">
          ✕
        </button>
      </div>
      <EntityBody key={current} entityUri={current} />
    </aside>
  );
}

function EntityBody({ entityUri }: { entityUri: string }) {
  switch (kindOf(entityUri)) {
    case 'thread':
      return <ThreadBody id={threadIdOf(entityUri)!} />;
    case 'lock':
      return <LockBody entityUri={entityUri} />;
    case 'request':
      return <RequestBody route={keyOf(entityUri)} />;
    case 'finding':
      return <FindingBody id={keyOf(entityUri)} />;
    case 'type':
      return <TypeBody name={keyOf(entityUri)} />;
    default:
      return <ObjectBody entityUri={entityUri} />;
  }
}

export function PinButtons({ entity }: { entity: EntityRef }) {
  const pins = usePins();
  const pinned = pins.isPinned(entity.uri);
  return (
    <span style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
      <button
        className={`btn ${pinned ? 'pinned' : ''}`}
        onClick={() => (pinned ? pins.unpin(entity.uri) : pins.pin(entity))}
        title={pinned ? 'Unpin (P)' : 'Pin (P)'}
        aria-pressed={pinned}
      >
        📌{pinned ? ' Pinned' : ''}
      </button>
      <button className="btn ghost" onClick={() => navigator.clipboard?.writeText(entity.label)}>
        Copy
      </button>
    </span>
  );
}

export function NoteEditor({ entity }: { entity: EntityRef }) {
  const pins = usePins();
  if (!pins.isPinned(entity.uri)) return null;
  return (
    <div className="ib">
      <span className="lbl">Note</span>
      <textarea
        className="note-input"
        defaultValue={pins.noteFor(entity.uri)}
        placeholder="What you found, who to ask, what to check next…"
        onBlur={(e) => pins.setNote(entity, e.target.value)}
      />
    </div>
  );
}

function ThreadBody({ id }: { id: number }) {
  const { data: t, isLoading, error } = useRpc('threads.get', { id });
  if (isLoading) return <div className="ih">Loading thread #{id}…</div>;
  if (error || !t)
    return (
      <div className="ih">
        Couldn't read thread #{id}: {String(error)}
      </div>
    );
  const s = t.thread;
  const entity: EntityRef = { uri: uri.thread(id), kind: 'thread', label: `#${id}`, detail: s.request };
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 16 }}>Thread #{id}</h3>
          <StatePill state={s.state} label={s.stateLabel} />
          <PinButtons entity={entity} />
        </div>
        <dl className="kv">
          <dt>Serving</dt>
          <dd className="mono" style={{ color: s.request ? 'var(--info)' : 'var(--fg-3)' }}>
            {s.request ?? '—'}
          </dd>
          <dt>Where</dt>
          <dd className="mono">{s.where ?? '—'}</dd>
          <dt>Holds</dt>
          <dd className="ev" style={{ marginTop: 0 }}>
            {t.holds.length ? t.holds.map((h) => <Chip key={h.uri} entity={h} />) : '—'}
          </dd>
          <dt>Waiting for</dt>
          <dd>
            {t.waitingFor ? (
              <span className="ev" style={{ marginTop: 0, alignItems: 'center' }}>
                <Chip entity={t.waitingFor} />
                {t.waitingForOwner && (
                  <>
                    owned by <Chip entity={t.waitingForOwner} />
                  </>
                )}
              </span>
            ) : (
              '—'
            )}
          </dd>
          <dt>Thread</dt>
          <dd className="mono" style={{ color: 'var(--fg-2)' }}>
            managed {id} · OS 0x{s.osId.toString(16)}
            {s.exception ? ` · ${s.exception}` : ''}
          </dd>
        </dl>
      </div>
      <NoteEditor entity={entity} />
      <div className="ib">
        <StackView frames={t.frames} />
      </div>
      {t.stackObjects.length > 0 && (
        <div className="ib">
          <span className="lbl">Objects on stack</span>
          <div className="ev">
            {t.stackObjects.map((o) => (
              <Chip key={o.uri} entity={o} />
            ))}
          </div>
        </div>
      )}
      {t.sameStack.length > 0 && (
        <div className="ib">
          <span className="lbl">Same stack ({t.sameStack.length})</span>
          <div className="ev">
            {t.sameStack.slice(0, 24).map((x) => (
              <Chip key={x} entity={{ uri: uri.thread(x), kind: 'thread', label: `#${x}`, detail: null }} />
            ))}
          </div>
        </div>
      )}
    </>
  );
}

export function useWaitGraph(cyclesOnly = false) {
  return useRpc('locks.graph', { cyclesOnly });
}

function LockBody({ entityUri }: { entityUri: string }) {
  const { data } = useWaitGraph(false);
  const lock = data?.locks.find((l) => l.uri === entityUri);
  if (!data) return <div className="ih">Loading…</div>;
  if (!lock) return <div className="ih">This lock isn't held or contended in the dump.</div>;
  const cycle = data.cycles.find((c) => c.locks.includes(entityUri));
  const entity: EntityRef = { uri: lock.uri, kind: 'lock', label: lock.name, detail: null };
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 16 }}>▲ {lock.name}</h3>
          {lock.inCycle && <Sev severity="critical" label="In cycle" />}
          <PinButtons entity={entity} />
        </div>
        <dl className="kv">
          <dt>Owner</dt>
          <dd>{lock.ownerThread != null ? <Chip entity={refThread(lock.ownerThread, data)} /> : '—'}</dd>
          <dt>Acquired at</dt>
          <dd className="mono">{lock.acquiredAt ?? '—'}</dd>
          <dt>Object</dt>
          <dd>
            <Chip
              entity={{
                uri: uri.object(lock.address),
                kind: 'object',
                label: shortType(lock.typeName),
                detail: `0x${lock.address.toString(16)}`,
              }}
            />
          </dd>
          <dt>Recursion</dt>
          <dd className="mono">{lock.recursion}</dd>
        </dl>
      </div>
      <NoteEditor entity={entity} />
      <div className="ib">
        <span className="lbl">Waiting ({lock.waiters.length})</span>
        <div className="ev">
          {lock.waiters.length ? (
            lock.waiters.map((w) => <Chip key={w} entity={refThread(w, data)} />)
          ) : (
            <span className="conf">Nobody is waiting.</span>
          )}
        </div>
      </div>
      {cycle && (
        <div className="ib">
          <span className="lbl">Cycle</span>
          <p style={{ margin: '6px 0 0', fontSize: 13 }}>
            <Rich text={cycle.explanation} entities={lockRefs(data)} />
          </p>
        </div>
      )}
      <div className="ib">
        <button className="btn" onClick={() => navigate('locks')}>
          Show in graph
        </button>
      </div>
    </>
  );
}

function refThread(id: number, graph: WaitGraph): EntityRef {
  const node = graph.nodes.find((n) => n.id === uri.thread(id));
  return { uri: uri.thread(id), kind: 'thread', label: `#${id}`, detail: node?.sub ?? null };
}

export function lockRefs(graph: WaitGraph): EntityRef[] {
  return graph.locks.map((l) => ({ uri: l.uri, kind: 'lock', label: l.name, detail: null }));
}

function RequestBody({ route }: { route: string }) {
  const path = route.split(' ').slice(1).join(' ');
  const { data } = useRpc('threads.groups', {
    groupBy: 'flat',
    state: 'all',
    search: path.replace(/\{id\}.*/, '') || path,
  });
  const threads = data?.groups.filter((g) => g.request === route) ?? [];
  const entity: EntityRef = { uri: uri.request(route), kind: 'request', label: route, detail: null };
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 15 }} className="mono">
            {route}
          </h3>
          <PinButtons entity={entity} />
        </div>
        <p className="conf" style={{ margin: '6px 0 0' }}>
          {threads.length} thread{threads.length === 1 ? '' : 's'} serving this request when the dump was taken.
        </p>
      </div>
      <NoteEditor entity={entity} />
      <div className="ib">
        {threads.map((g) => (
          <div key={g.key} style={{ display: 'flex', gap: 8, alignItems: 'center', padding: '4px 0' }}>
            <Chip
              entity={{ uri: uri.thread(g.threadIds[0]), kind: 'thread', label: `#${g.threadIds[0]}`, detail: null }}
            />
            <StatePill state={g.state} label={g.stateLabel} />
            <span
              className="mono"
              style={{
                color: 'var(--fg-2)',
                fontSize: 12,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              {g.where}
            </span>
          </div>
        ))}
      </div>
    </>
  );
}

function FindingBody({ id }: { id: string }) {
  const finding = useApp((s) => s.diagnosis?.findings.find((f) => f.id === id));
  if (!finding) return <div className="ih">Finding not found.</div>;
  return <FindingDetail f={finding} />;
}

export function FindingDetail({ f }: { f: Finding }) {
  const entity: EntityRef = { uri: uri.finding(f.id), kind: 'finding', label: f.title.replace(/`/g, ''), detail: null };
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <Sev severity={f.severity} />
          <Conf confidence={f.confidence} />
          <PinButtons entity={entity} />
        </div>
        <h3 style={{ fontSize: 15, marginTop: 10 }}>
          <Rich text={f.title} entities={f.evidence} />
        </h3>
        <p className="conf" style={{ margin: '4px 0 0' }}>
          <Rich text={f.why} entities={f.evidence} />
        </p>
      </div>
      <div className="ib">
        <span className="lbl">What this means</span>
        <p style={{ margin: '6px 0 0', fontSize: 13 }}>
          <Rich text={f.whatItMeans} entities={f.evidence} />
        </p>
      </div>
      <div className="ib">
        <span className="lbl">What to do next</span>
        <ol style={{ margin: '6px 0 0', paddingLeft: 18, fontSize: 13 }}>
          {f.nextSteps.map((s, i) => (
            <li key={i}>
              <Rich text={s} entities={f.evidence} />
            </li>
          ))}
        </ol>
      </div>
      <div className="ib">
        <span className="lbl">Evidence</span>
        <div className="ev">
          {f.evidence.map((e) => (
            <Chip key={e.uri} entity={e} />
          ))}
        </div>
      </div>
    </>
  );
}

function TypeBody({ name }: { name: string }) {
  const entity = refFor(uri.type(name));
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 15 }} className="mono">
            ◆ {shortType(name)}
          </h3>
          <PinButtons entity={entity} />
        </div>
        <p className="mono" style={{ color: 'var(--fg-3)', fontSize: 12, margin: '6px 0 0', wordBreak: 'break-all' }}>
          {name}
        </p>
      </div>
      <div className="ib">
        <button className="btn" onClick={() => navigate('memory', name)}>
          Show instances in Memory
        </button>
      </div>
    </>
  );
}

function ObjectBody({ entityUri }: { entityUri: string }) {
  const address = addressOf(entityUri);
  const entity = refFor(entityUri);
  return (
    <>
      <div className="ih">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <h3 style={{ fontSize: 15 }}>● Object</h3>
          <PinButtons entity={entity} />
        </div>
        <p className="mono" style={{ color: 'var(--fg-2)', margin: '6px 0 0' }}>
          0x{address?.toString(16)}
        </p>
      </div>
      <div className="ib">
        <button className="btn pri" onClick={() => navigate('objects', `0x${address?.toString(16)}`)}>
          Open in object explorer
        </button>
      </div>
    </>
  );
}
