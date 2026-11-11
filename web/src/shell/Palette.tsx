import { useEffect, useMemo, useRef, useState } from 'react';
import type { Severity } from '../contracts.gen';
import { Sev } from '../components/Sev';
import { uri } from '../entities';
import { navigate, type View } from '../router';
import { useRpc } from '../state/queries';
import { useApp } from '../state/store';

interface Item {
  group: string;
  icon: string;
  iconColor?: string;
  label: string;
  meta?: string;
  sev?: { severity: Severity; label: string };
  keys?: string;
  run(): void;
}

/** Ctrl+K: jump to anything by name, address, type or route. */
export function Palette() {
  const open = useApp((s) => s.paletteOpen);
  const setOpen = useApp((s) => s.setPalette);
  if (!open) return null;
  return <PaletteBody close={() => setOpen(false)} />;
}

function PaletteBody({ close }: { close(): void }) {
  const [q, setQ] = useState('');
  const [sel, setSel] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const dump = useApp((s) => s.dump);
  const diagnosis = useApp((s) => s.diagnosis);
  const inspect = useApp((s) => s.inspect);
  const openDump = useApp((s) => s.openDump);
  const closeDump = useApp((s) => s.closeDump);
  const threads = useRpc('threads.groups', { groupBy: 'flat', state: 'all', search: null }, !!dump);
  const graph = useRpc('locks.graph', { cyclesOnly: false }, !!dump);
  const types = useRpc(
    'heap.types',
    { groupBy: 'type', filter: null, offset: 0, limit: 400, sort: 'size' },
    !!dump && !!diagnosis?.complete,
  );

  useEffect(() => input.current?.focus(), []);

  const items = useMemo(() => {
    const all: Item[] = [];
    const go = (v: View, arg?: string) => () => navigate(v, arg);

    const requests = new Map<string, number>();
    threads.data?.groups.forEach((g) => g.request && requests.set(g.request, (requests.get(g.request) ?? 0) + 1));
    requests.forEach((n, route) =>
      all.push({
        group: 'Requests',
        icon: '⇢',
        iconColor: 'var(--info)',
        label: route,
        meta: `${n} thread${n === 1 ? '' : 's'}`,
        run: () => inspect(uri.request(route)),
      }),
    );

    threads.data?.groups.forEach((g) => {
      const id = g.threadIds[0];
      all.push({
        group: 'Threads',
        icon: '◆',
        label: `#${id} ${g.where}`,
        meta: g.request ?? undefined,
        sev:
          g.state === 'blocked' ? { severity: g.inDeadlock ? 'critical' : 'warning', label: g.stateLabel } : undefined,
        run: () => inspect(uri.thread(id)),
      });
    });

    graph.data?.locks.forEach((l) =>
      all.push({
        group: 'Locks',
        icon: '▲',
        label: l.name,
        meta: `0x${l.address.toString(16)} · ${l.waiters.length} waiting`,
        sev: l.inCycle ? { severity: 'critical', label: 'Cycle' } : undefined,
        run: () => inspect(l.uri),
      }),
    );

    types.data?.items.forEach((t) =>
      all.push({
        group: 'Types',
        icon: '▦',
        label: t.name,
        meta: `${t.count.toLocaleString('en-US')} instances`,
        run: () => navigate('memory', t.name),
      }),
    );

    const address = q.trim().match(/^(?:0x)?([0-9a-f]{6,16})$/i);
    if (dump && address) {
      all.push({
        group: 'Objects',
        icon: '●',
        label: `Go to object 0x${address[1]}`,
        run: () => navigate('objects', `0x${address[1]}`),
      });
    }

    diagnosis?.findings.forEach((f) =>
      all.push({
        group: 'Findings',
        icon: '!',
        label: f.title.replace(/`/g, ''),
        sev: { severity: f.severity, label: f.severity },
        run: () => inspect(uri.finding(f.id)),
      }),
    );

    const cmd = (label: string, run: () => void, keys?: string) =>
      all.push({ group: 'Commands', icon: '⌘', label, run, keys });
    cmd(
      'Open dump…',
      async () => {
        const { rpc } = await import('../rpc/client');
        const picked = await rpc.call('dump.pick', {});
        if (picked.path) void openDump(picked.path);
      },
      'Ctrl O',
    );
    if (dump) {
      cmd('Go to Diagnosis', go('diagnosis'), 'G D');
      cmd('Go to Threads', go('threads'), 'G T');
      cmd('Go to Locks', go('locks'), 'G L');
      cmd('Go to Memory', go('memory'), 'G M');
      cmd('Go to Objects', go('objects'), 'G O');
      cmd('Export incident report', go('report'), 'Ctrl E');
      cmd('Close dump', () => void closeDump());
    }
    cmd('Compare two dumps', go('compare'), 'G C');
    cmd('Home', go('home'), 'G H');
    cmd('Keyboard shortcuts', () => useApp.getState().setHelp(true), '?');
    return all;
  }, [threads.data, graph.data, types.data, q, diagnosis, dump, inspect, openDump, closeDump]);

  const filtered = useMemo(() => {
    const terms = q.toLowerCase().split(/\s+/).filter(Boolean);
    const scored = items
      .map((it) => {
        const hay = `${it.label} ${it.meta ?? ''}`.toLowerCase();
        if (!terms.every((t) => hay.includes(t.replace(/^#/, '#')))) return null;
        const score = terms.length ? terms.reduce((s, t) => s + hay.indexOf(t), 0) : 0;
        return { it, score };
      })
      .filter((x): x is { it: Item; score: number } => x !== null);
    const byGroup = new Map<string, Item[]>();
    for (const { it } of scored.sort((a, b) => a.score - b.score)) {
      const list = byGroup.get(it.group) ?? [];
      if (list.length < (q ? 6 : 4)) list.push(it);
      byGroup.set(it.group, list);
    }
    const order = ['Objects', 'Requests', 'Threads', 'Locks', 'Types', 'Findings', 'Commands'];
    return order.flatMap((g) => byGroup.get(g) ?? []);
  }, [items, q]);

  const choose = (it: Item | undefined) => {
    if (!it) return;
    close();
    it.run();
  };

  let lastGroup = '';
  return (
    <div className="palette-scrim" onClick={close}>
      <div className="palette on" role="dialog" aria-label="Command palette" onClick={(e) => e.stopPropagation()}>
        <input
          ref={input}
          value={q}
          spellCheck={false}
          placeholder="Search requests, threads, locks, types, commands…"
          aria-label="Search"
          onChange={(e) => {
            setQ(e.target.value);
            setSel(0);
          }}
          onKeyDown={(e) => {
            if (e.key === 'ArrowDown') {
              e.preventDefault();
              setSel((s) => Math.min(s + 1, filtered.length - 1));
            } else if (e.key === 'ArrowUp') {
              e.preventDefault();
              setSel((s) => Math.max(s - 1, 0));
            } else if (e.key === 'Enter') {
              choose(filtered[sel]);
            } else if (e.key === 'Escape') {
              close();
            }
          }}
        />
        <div className="pg" role="listbox" aria-label="Results">
          {filtered.length === 0 && <div className="gl">No matches</div>}
          {filtered.map((it, i) => {
            const header = it.group !== lastGroup ? <div className="gl">{it.group}</div> : null;
            lastGroup = it.group;
            return (
              <div key={`${it.group}-${i}`}>
                {header}
                <div
                  className={`pi ${i === sel ? 'on' : ''}`}
                  role="option"
                  aria-selected={i === sel}
                  onMouseEnter={() => setSel(i)}
                  onClick={() => choose(it)}
                >
                  <span style={{ color: it.iconColor }}>{it.icon}</span>
                  <span className={it.group === 'Commands' || it.group === 'Findings' ? '' : 'mono'}>
                    {it.label} {it.meta && <span style={{ color: 'var(--fg-2)' }}>· {it.meta}</span>}
                  </span>
                  {it.sev ? (
                    <Sev severity={it.sev.severity} label={it.sev.label} />
                  ) : it.keys ? (
                    <span className="kbd">{it.keys}</span>
                  ) : i === sel ? (
                    <span className="kbd">↵</span>
                  ) : (
                    <span />
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
