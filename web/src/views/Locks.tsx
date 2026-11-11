import {
  Background,
  BackgroundVariant,
  Handle,
  MarkerType,
  Position,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Edge,
  type Node,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import ELK from 'elkjs/lib/elk.bundled.js';
import { useEffect, useMemo, useState } from 'react';
import type { WaitGraph, WaitGraphNode } from '../contracts.gen';
import { Sev } from '../components/Sev';
import { uri } from '../entities';
import { PinButtons, useWaitGraph } from '../inspector/Inspector';
import { navigate, useRoute } from '../router';
import { useApp } from '../state/store';
import { colors } from '../styles/tokens';

const elk = new ELK();
const size = {
  thread: { width: 200, height: 76 },
  lock: { width: 210, height: 70 },
  cluster: { width: 190, height: 56 },
};

type GraphNodeData = { node: WaitGraphNode; highlighted: boolean };

/** Who's waiting on whom? Threads ◆ and locks ▲, laid out by ELK, with cycles in red. */
export function Locks() {
  return (
    <ReactFlowProvider>
      <LocksInner />
    </ReactFlowProvider>
  );
}

function LocksInner() {
  const all = useWaitGraph(false);
  const [mode, setMode] = useState<'cycles' | 'all' | null>(null);
  const hasCycles = (all.data?.cycles.length ?? 0) > 0;
  const effective = mode ?? (hasCycles ? 'cycles' : 'all');
  const graphQuery = useWaitGraph(effective === 'cycles');
  const graph = graphQuery.data;
  const { arg } = useRoute();
  const inspect = useApp((s) => s.inspect);
  const [laid, setLaid] = useState<{ nodes: Node<GraphNodeData>[]; edges: Edge[] } | null>(null);
  const flow = useReactFlow();

  useEffect(() => {
    if (!graph) return;
    let cancelled = false;
    void layout(graph, arg).then((r) => {
      if (cancelled) return;
      setLaid(r);
      requestAnimationFrame(() => {
        // Always show the whole graph; a node named in the route is highlighted, not zoomed to.
        void flow.fitView({ padding: 0.25, maxZoom: 1.1, duration: 0 });
      });
    });
    return () => {
      cancelled = true;
    };
  }, [graph, arg, flow]);

  const nodeTypes = useMemo(() => ({ thread: ThreadNode, lock: LockNode, cluster: ClusterNode }), []);
  const contended = all.data?.locks.filter((l) => l.waiters.length > 0).length ?? 0;
  const cycle = graph?.cycles[0];

  return (
    <div className="view on locks">
      <div className="pane-h">
        <h2>Locks &amp; waits</h2>
        <span className="pill">
          {contended} contended lock{contended === 1 ? '' : 's'} · {all.data?.cycles.length ?? 0} cycle
          {all.data?.cycles.length === 1 ? '' : 's'}
        </span>
        <div className="r">
          <div className="seg" role="radiogroup" aria-label="Graph scope">
            <button
              role="radio"
              aria-checked={effective === 'cycles'}
              className={effective === 'cycles' ? 'on' : ''}
              onClick={() => setMode('cycles')}
              disabled={!hasCycles}
            >
              Cycles only
            </button>
            <button
              role="radio"
              aria-checked={effective === 'all'}
              className={effective === 'all' ? 'on' : ''}
              onClick={() => setMode('all')}
            >
              All contention
            </button>
          </div>
          <button
            className="btn"
            onClick={() => void flow.fitView({ padding: 0.25, maxZoom: 1.1, duration: reducedMotion() ? 0 : 300 })}
          >
            Fit
          </button>
        </div>
      </div>
      <div className="graph-wrap">
        {graph && graph.nodes.length === 0 ? (
          <div className="graph-empty">
            <Sev severity="ok" label="No contention" />
            <p>
              No thread is waiting on a lock another thread holds. If the app was hung, look at Threads for task waits
              and I/O.
            </p>
          </div>
        ) : (
          <ReactFlow
            className={cycle ? 'with-card' : ''}
            nodes={laid?.nodes ?? []}
            edges={laid?.edges ?? []}
            nodeTypes={nodeTypes}
            onNodeClick={(_, n) => {
              const node = (n.data as GraphNodeData).node;
              inspect(node.kind === 'thread' && node.count > 1 ? uri.thread(node.threadIds[0]) : node.id);
            }}
            nodesDraggable
            nodesConnectable={false}
            elementsSelectable
            proOptions={{ hideAttribution: true }}
            minZoom={0.2}
            maxZoom={2}
            colorMode="dark"
          >
            <Background variant={BackgroundVariant.Dots} gap={22} size={1} color="#ffffff14" bgColor={colors.bg0} />
          </ReactFlow>
        )}
        {cycle && graph && <ExplainCard graph={graph} cycleIndex={0} />}
        <div className="legend">
          <span>
            <i style={{ borderColor: colors.fg1 }} />
            owns
          </span>
          <span>
            <i style={{ borderColor: colors.crit, borderTopStyle: 'dashed' }} />
            waits for (in cycle)
          </span>
          <span>
            <i style={{ borderColor: colors.warn, borderTopStyle: 'dashed' }} />
            waits for
          </span>
          <span>
            <i style={{ borderColor: colors.fg3, borderTopStyle: 'dotted' }} />
            queued behind
          </span>
        </div>
      </div>
    </div>
  );
}

function ExplainCard({ graph, cycleIndex }: { graph: WaitGraph; cycleIndex: number }) {
  const cycle = graph.cycles[cycleIndex];
  const findingId = useApp((s) => s.diagnosis?.findings.find((f) => f.analyzer === 'deadlock')?.id);
  const inspect = useApp((s) => s.inspect);
  const pinEntity = {
    uri: findingId ? uri.finding(findingId) : cycle.locks[0],
    kind: findingId ? ('finding' as const) : ('lock' as const),
    label: 'Lock-order inversion',
    detail: null,
  };
  return (
    <div className="explain">
      <Sev severity="critical" label="Cycle" />
      <h3 style={{ margin: '10px 0 6px', fontSize: 15 }}>
        {cycle.threads.length === 2 ? 'Lock-order inversion' : `${cycle.threads.length}-thread lock cycle`}
      </h3>
      <p style={{ margin: 0, color: 'var(--fg-2)', fontSize: 13 }}>
        {cycle.threads.map((t) => {
          const held = graph.edges.find(
            (e) => e.from === uri.thread(t) && e.kind === 'owns' && cycle.locks.includes(e.to),
          )?.to;
          const wanted = graph.edges.find((e) => e.from === uri.thread(t) && e.kind === 'waitsFor')?.to;
          return (
            <span key={t}>
              #{t} took{' '}
              <b>
                {field(graph, held)} → {field(graph, wanted)}
              </b>
              .{' '}
            </span>
          );
        })}
        Each now holds what the other needs.
      </p>
      <div className="lbl" style={{ marginTop: 12 }}>
        Acquired at
      </div>
      <div className="mono" style={{ color: 'var(--fg-0)', marginTop: 4 }}>
        {cycle.acquiredAt.filter(Boolean).map((a) => (
          <div key={a}>{a}</div>
        ))}
      </div>
      <div style={{ marginTop: 12, display: 'flex', gap: 8 }}>
        <PinButtons entity={pinEntity} />
        <button
          className="btn ghost"
          onClick={() => {
            navigate('diagnosis');
            if (findingId) inspect(uri.finding(findingId));
          }}
        >
          Back to finding
        </button>
      </div>
    </div>
  );
}

async function layout(graph: WaitGraph, highlight?: string) {
  const kindOfNode = (n: WaitGraphNode) =>
    n.kind === 'lock' ? 'lock' : n.count > 1 || n.id.startsWith('cluster/') ? 'cluster' : 'thread';
  const result = await elk.layout({
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': 'DOWN',
      'elk.spacing.nodeNode': '70',
      'elk.layered.spacing.nodeNodeBetweenLayers': '110',
      'elk.layered.nodePlacement.strategy': 'BRANDES_KOEPF',
    },
    children: graph.nodes.map((n) => ({ id: n.id, ...size[kindOfNode(n)] })),
    // Queued clusters lay out below their lock (edge reversed for layout only).
    edges: graph.edges.map((e, i) =>
      e.kind === 'queuedBehind'
        ? { id: `e${i}`, sources: [e.to], targets: [e.from] }
        : { id: `e${i}`, sources: [e.from], targets: [e.to] },
    ),
  });

  const nodes: Node<GraphNodeData>[] = graph.nodes.map((n) => {
    const p = result.children!.find((c) => c.id === n.id)!;
    return {
      id: n.id,
      type: kindOfNode(n),
      position: { x: p.x!, y: p.y! },
      data: { node: n, highlighted: n.id === highlight },
    };
  });

  const edges: Edge[] = graph.edges.map((e, i) => {
    const style =
      e.kind === 'owns'
        ? { stroke: colors.fg1, strokeWidth: 2 }
        : e.kind === 'waitsFor'
          ? { stroke: e.inCycle ? colors.crit : colors.warn, strokeWidth: 2, strokeDasharray: '7 5' }
          : { stroke: colors.fg3, strokeWidth: 2, strokeDasharray: '3 4' };
    return {
      id: `e${i}`,
      source: e.from,
      target: e.to,
      targetHandle: e.kind === 'queuedBehind' ? 'below' : 'above',
      sourceHandle: e.kind === 'queuedBehind' ? 'top' : 'bottom',
      type: 'default',
      style,
      markerEnd: { type: MarkerType.ArrowClosed, color: style.stroke, width: 16, height: 16 },
      ariaLabel: `${e.from} ${e.kind} ${e.to}`,
    };
  });
  return { nodes, edges };
}

function ThreadNode({ data }: NodeProps<Node<GraphNodeData>>) {
  const n = data.node;
  return (
    <div className={`gnode thread ${n.inCycle ? 'cycle' : ''} ${data.highlighted ? 'hl' : ''}`} style={size.thread}>
      <div className="t1">◆ {n.label}</div>
      {n.sub && <div className="t2 req">{n.sub}</div>}
      {n.detail && <div className="t3">{n.detail}</div>}
      <Handle type="source" id="bottom" position={Position.Bottom} />
    </div>
  );
}

function LockNode({ data }: NodeProps<Node<GraphNodeData>>) {
  const n = data.node;
  return (
    <div className={`gnode lock ${n.inCycle ? 'cycle' : ''} ${data.highlighted ? 'hl' : ''}`} style={size.lock}>
      <Handle type="target" id="above" position={Position.Top} />
      <div className="t1">▲ {n.label}</div>
      {n.sub && <div className="t3">{n.sub}</div>}
      <Handle type="target" id="below" position={Position.Bottom} />
    </div>
  );
}

function ClusterNode({ data }: NodeProps<Node<GraphNodeData>>) {
  const n = data.node;
  return (
    <div className={`gnode cluster ${data.highlighted ? 'hl' : ''}`} style={size.cluster}>
      <Handle type="source" id="top" position={Position.Top} />
      <div className="t1">◆ {n.label}</div>
      {n.sub && <div className="t3">{n.sub}</div>}
    </div>
  );
}

/** "OrderLedger._gate" → "_gate", for the compact cycle sentence. */
function field(graph: WaitGraph, lockUri: string | undefined) {
  const name = graph.locks.find((l) => l.uri === lockUri)?.name ?? '?';
  return name.slice(name.lastIndexOf('.') + 1);
}

/** Respect the OS setting: the graph snaps instead of animating. */
function reducedMotion() {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
}
