import { useEffect, useState } from 'react';
import type { DumpMetadata, ThreadInfo } from './contracts.gen';
import { rpc } from './rpc/client';

export function App() {
  const [meta, setMeta] = useState<DumpMetadata | null>(null);
  const [threads, setThreads] = useState<ThreadInfo[]>([]);
  const [status, setStatus] = useState('Ready');

  async function open(path: string) {
    setStatus(`Opening ${path}…`);
    try {
      const m = await rpc.call('dump.open', { path });
      setMeta(m);
      setThreads(await rpc.call('threads.list', {}));
      setStatus('Loaded');
    } catch (e) {
      setStatus(String(e));
    }
  }

  useEffect(() => {
    rpc.call('app.info', {}).then((info) => {
      if (info.startupDumpPath) void open(info.startupDumpPath);
    });
  }, []);

  return (
    <div className="win">
      <div className="titlebar">
        <div className="logo" />
        <span className="dump">{meta ? meta.dumpPath.split(/[\\/]/).pop() : 'Dump Debugger'}</span>
        {meta && (
          <span className="meta">
            <span className="pill">{meta.runtimes[0]?.version}</span>
            <span className="pill">{meta.architecture}</span>
            <span className="pill">{meta.threadCount} threads</span>
          </span>
        )}
        <span style={{ marginLeft: 'auto' }} />
        <span className="wc" />
      </div>
      <div className="scroll" style={{ padding: 20 }}>
        <button
          className="btn pri"
          onClick={async () => {
            const picked = await rpc.call('dump.pick', {});
            if (picked.path) void open(picked.path);
          }}
        >
          Open dump
        </button>
        <table style={{ width: '100%', marginTop: 16, fontSize: 13, borderCollapse: 'collapse' }}>
          <tbody>
            {threads.map((t) => (
              <tr key={t.managedThreadId} style={{ borderBottom: '1px solid var(--line)' }}>
                <td className="mono">#{t.managedThreadId}</td>
                <td className="mono">{t.osThreadId.toString(16)}</td>
                <td className="mono" style={{ color: 'var(--fg-0)' }}>
                  {t.frames[0] ? `${t.frames[0].typeName ?? ''}.${t.frames[0].methodName}` : '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="statusbar">
        <span>
          <span className="dot" />
          {status}
        </span>
      </div>
    </div>
  );
}
