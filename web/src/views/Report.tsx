import { useMemo, useState } from 'react';
import { Chip } from '../components/Chip';
import { refFor } from '../entities';
import { buildReport, defaultSections, type ReportSections } from '../report/report';
import { rpc } from '../rpc/client';
import { usePins } from '../state/pins';
import { useRpc } from '../state/queries';
import { useApp } from '../state/store';

const toggles: { key: keyof ReportSections; label: string }[] = [
  { key: 'verdict', label: 'Verdict & vitals' },
  { key: 'findings', label: 'Findings (warning+)' },
  { key: 'infoFindings', label: 'Info findings too' },
  { key: 'pins', label: 'Pins & notes' },
  { key: 'threads', label: 'Full thread groups' },
  { key: 'heap', label: 'Top 20 heap types' },
];

/** How do I hand this off? Pins and notes on the left, the exact export previewed on the right. */
export function Report() {
  const diagnosis = useApp((s) => s.diagnosis);
  const pins = usePins();
  const [sections, setSections] = useState<ReportSections>(defaultSections);
  const [status, setStatus] = useState<string | null>(null);
  const threads = useRpc('threads.groups', { groupBy: 'stack', state: 'all', search: null }, sections.threads);
  const heap = useRpc(
    'heap.types',
    { groupBy: 'type', filter: null, offset: 0, limit: 20, sort: 'size' },
    sections.heap,
  );
  const info = useRpc('app.info', {});

  const report = useMemo(
    () =>
      diagnosis
        ? buildReport(
            {
              diagnosis,
              pins: pins.pins,
              threadGroups: threads.data?.groups,
              heapTypes: heap.data?.items,
              generatedAt: new Date(),
              appVersion: info.data?.version,
            },
            sections,
          )
        : null,
    [diagnosis, pins.pins, threads.data, heap.data, sections, info.data],
  );

  if (!diagnosis || !report) {
    return (
      <div className="view on" style={{ placeItems: 'center' }}>
        <p style={{ color: 'var(--fg-3)' }}>The report is available once analysis has produced a diagnosis.</p>
      </div>
    );
  }

  const save = async (format: 'markdown' | 'html') => {
    setStatus(null);
    const saved = await rpc.call('report.save', {
      format,
      fileName: report.fileName,
      content: format === 'html' ? report.html : report.markdown,
    });
    if (saved.path) setStatus(`Saved ${saved.path}`);
  };

  return (
    <div className="view on report">
      <h2 className="sr-only">Incident report</h2>
      <aside className="side" style={{ borderLeft: 0, borderRight: '1px solid var(--line)' }}>
        <div className="blk">
          <div className="lbl">Include</div>
          {toggles.map((t) => (
            <label key={t.key} className="check">
              <input
                type="checkbox"
                checked={sections[t.key]}
                onChange={(e) => setSections({ ...sections, [t.key]: e.target.checked })}
              />{' '}
              {t.label}
            </label>
          ))}
        </div>
        <div className="blk">
          <div className="lbl">Pins &amp; notes</div>
          {pins.pins.length === 0 && (
            <p className="conf" style={{ margin: '6px 0 0' }}>
              Nothing pinned yet. Pin threads, locks, objects or findings (right-click a chip, or press{' '}
              <span className="kbd">P</span> in the inspector) and add a note: they show up here and in the report.
            </p>
          )}
          {pins.pins.map((p) => {
            const entity = refFor(p.uri, [{ uri: p.uri, kind: p.kind, label: p.label, detail: null }]);
            return (
              <div className="pin col" key={p.uri}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <Chip entity={entity} />
                  <button
                    className="btn ghost"
                    style={{ marginLeft: 'auto', padding: '1px 6px' }}
                    onClick={() => pins.unpin(p.uri)}
                    aria-label={`Unpin ${p.label}`}
                  >
                    ✕
                  </button>
                </div>
                <textarea
                  className="note-input"
                  defaultValue={p.note}
                  placeholder="Add a note…"
                  onBlur={(e) => e.target.value !== p.note && pins.setNote(entity, e.target.value)}
                  aria-label={`Note for ${p.label}`}
                />
              </div>
            );
          })}
        </div>
        <div className="blk" style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <button className="btn pri" onClick={() => save('markdown')}>
            Export Markdown
          </button>
          <button className="btn" onClick={() => save('html')}>
            HTML
          </button>
          <button
            className="btn ghost"
            onClick={() => {
              void navigator.clipboard?.writeText(report.markdown);
              setStatus('Markdown copied to the clipboard');
            }}
          >
            Copy
          </button>
          {status && (
            <p className="conf" style={{ margin: '4px 0 0', wordBreak: 'break-all' }} role="status">
              {status}
            </p>
          )}
        </div>
      </aside>
      <div className="report-preview">
        <iframe title="Report preview" srcDoc={report.html} sandbox="" />
      </div>
    </div>
  );
}
