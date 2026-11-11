import { useEffect, useRef } from 'react';
import { icons } from '../components/Icons';
import { bytes, fileName } from '../format';
import { rpc } from '../rpc/client';
import { useRoute } from '../router';
import { useApp } from '../state/store';

/**
 * The window titlebar, drawn by the page (the host extends content into the titlebar and
 * paints only the caption buttons, top-right). The strip is draggable; the rects of anything
 * interactive in it are reported to the host as passthrough regions.
 */
export function Titlebar() {
  const dump = useApp((s) => s.dump);
  const diagnosis = useApp((s) => s.diagnosis);
  const setPalette = useApp((s) => s.setPalette);
  const { view } = useRoute();
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const report = () => {
      const rects = [...(ref.current?.querySelectorAll<HTMLElement>('[data-interactive]') ?? [])].map((el) => {
        const r = el.getBoundingClientRect();
        return { x: r.left, y: r.top, width: r.width, height: r.height };
      });
      void rpc.call('window.setTitlebar', { passthrough: rects }).catch(() => {});
    };
    report();
    const ro = new ResizeObserver(report);
    if (ref.current) ro.observe(ref.current);
    window.addEventListener('resize', report);
    return () => {
      ro.disconnect();
      window.removeEventListener('resize', report);
    };
  }, [dump]);

  const atHome = view === 'home' || (view === 'analyzing' && !dump);
  const name =
    view === 'compare' ? 'Compare · 2 dumps' : atHome || !dump ? 'Dump Debugger' : fileName(dump.metadata.dumpPath);
  const md = dump?.metadata;

  return (
    <div className="titlebar" ref={ref}>
      <div className="logo" />
      <span className="dump">{name}</span>
      {md && !atHome && view !== 'compare' && (
        <span className="meta">
          <span className="pill">{diagnosis?.dump.runtime ?? md.runtimes[0]?.productVersion}</span>
          <span className="pill">{md.architecture}</span>
          <span className="pill">{bytes(md.fileSizeBytes)}</span>
          <span className="pill">{diagnosis?.dump.threadCount ?? md.threadCount} threads</span>
        </span>
      )}
      <button className="search" data-interactive onClick={() => setPalette(true)} aria-label="Search (Ctrl+K)">
        {icons.search}
        Search threads, types, objects…
        <span style={{ marginLeft: 'auto' }}>
          <span className="kbd">Ctrl</span> <span className="kbd">K</span>
        </span>
      </button>
      <span className="wc" />
    </div>
  );
}
