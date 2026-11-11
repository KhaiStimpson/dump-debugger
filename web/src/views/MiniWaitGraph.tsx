import type { LockCycle, WaitGraph } from '../contracts.gen';
import { useApp } from '../state/store';
import { colors } from '../styles/tokens';

/**
 * "The deadlock at a glance": cycle threads on top, the locks they hold underneath, and red
 * dashed "waits for" edges crossing between them, as in the mockup. Nodes open the inspector.
 */
export function MiniWaitGraph({ graph, cycle }: { graph: WaitGraph; cycle: LockCycle }) {
  const inspect = useApp((s) => s.inspect);
  const n = cycle.threads.length;
  const w = 320;
  const boxW = 96;
  const gap = n > 1 ? (w - 28 - boxW) / (n - 1) : 0;
  const x = (i: number) => 14 + i * gap;
  const node = (id: string) => graph.nodes.find((x) => x.id === id);

  // Each thread's held lock sits under it.
  const held = cycle.threads.map((t) => {
    const owns = graph.edges.find((e) => e.from === `thread/${t}` && e.kind === 'owns' && cycle.locks.includes(e.to));
    return owns?.to;
  });
  const wants = cycle.threads.map(
    (t) => graph.edges.find((e) => e.from === `thread/${t}` && e.kind === 'waitsFor')?.to,
  );

  return (
    <svg
      className="mini-graph"
      viewBox="0 0 320 170"
      width="100%"
      role="img"
      aria-label="Wait-for graph of the deadlock"
    >
      <g fill="none" strokeWidth="1.5">
        {cycle.threads.map((_, i) => (
          <path key={`o${i}`} d={`M${x(i) + 48} 112V60`} stroke={colors.fg1} />
        ))}
        {cycle.threads.map((_, i) => {
          const j = held.indexOf(wants[i]);
          if (j < 0) return null;
          const fromX = i < j ? x(i) + boxW : x(i);
          const toX = i < j ? x(j) : x(j) + boxW;
          const mid = (fromX + toX) / 2;
          return (
            <path
              key={`w${i}`}
              d={`M${fromX} ${i < j ? 50 : 50} C${mid} 70 ${mid} 110 ${toX} ${i < j ? 125 : 125}`}
              stroke={colors.crit}
              strokeDasharray="5 4"
            />
          );
        })}
      </g>
      <g fontFamily="Cascadia Code,Consolas,monospace" fontSize="10">
        {cycle.threads.map((t, i) => {
          const sub = node(`thread/${t}`)?.sub ?? node(`thread/${t}`)?.detail ?? '';
          return (
            <g key={t} style={{ cursor: 'pointer' }} onClick={() => inspect(`thread/${t}`)}>
              <rect x={x(i)} y={20} width={boxW} height={40} rx={6} fill={colors.critDim} stroke={colors.crit} />
              <text x={x(i) + 10} y={37} fill={colors.fg0}>
                ◆ #{t}
              </text>
              <text x={x(i) + 10} y={51} fill={colors.fg2}>
                {clip(sub.split(' ').pop() ?? sub, 13)}
              </text>
            </g>
          );
        })}
        {held.map((l, i) => {
          const lk = graph.locks.find((x) => x.uri === l);
          if (!lk) return null;
          const [owner, field] = splitName(lk.name);
          return (
            <g key={l} style={{ cursor: 'pointer' }} onClick={() => inspect(lk.uri)}>
              <rect x={x(i)} y={112} width={boxW} height={40} rx={6} fill={colors.bg3} stroke={colors.line2} />
              <text x={x(i) + 10} y={129} fill={colors.fg0}>
                ▲ {clip(field, 11)}
              </text>
              <text x={x(i) + 10} y={143} fill={colors.fg2}>
                {clip(owner, 13)}
              </text>
            </g>
          );
        })}
      </g>
    </svg>
  );
}

function splitName(name: string): [string, string] {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? [name.slice(0, dot), name.slice(dot + 1)] : ['', name];
}

function clip(s: string, n: number) {
  return s.length > n ? s.slice(0, n - 1) + '…' : s;
}
