import { useApp } from '../state/store';

const keys: [string, string][] = [
  ['Ctrl K', 'Search requests, threads, locks, types, commands'],
  ['Ctrl O', 'Open a dump'],
  ['Ctrl E', 'Export incident report'],
  ['G then H / D / T / L / M / O / C / R', 'Go to Home, Diagnosis, Threads, Locks, Memory, Objects, Compare, Report'],
  ['J / K', 'Next / previous row'],
  ['Enter', 'Open the selected row'],
  ['P', 'Pin or unpin what the inspector is showing'],
  ['Alt ← / Alt →', 'Inspector back / forward'],
  ['Esc', 'Close the palette, this overlay, or the inspector'],
  ['?', 'Show this list'],
];

/** The "?" overlay: every keyboard shortcut in one place. */
export function Help() {
  const open = useApp((s) => s.helpOpen);
  const setHelp = useApp((s) => s.setHelp);
  if (!open) return null;
  return (
    <div className="palette-scrim" onClick={() => setHelp(false)}>
      <div
        className="palette on help"
        role="dialog"
        aria-label="Keyboard shortcuts"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="pane-h">
          <h3>Keyboard shortcuts</h3>
          <div className="r">
            <button className="btn ghost" onClick={() => setHelp(false)} aria-label="Close">
              ✕
            </button>
          </div>
        </div>
        <dl className="kv keys">
          {keys.map(([k, d]) => (
            <div key={k} style={{ display: 'contents' }}>
              <dt>
                {k.split(' ').map((p, i) =>
                  ['then', '/', '←', '→'].includes(p) ? (
                    <span key={i} style={{ margin: '0 3px', color: 'var(--fg-3)' }}>
                      {p}
                    </span>
                  ) : (
                    <span key={i} className="kbd" style={{ marginRight: 3 }}>
                      {p}
                    </span>
                  ),
                )}
              </dt>
              <dd>{d}</dd>
            </div>
          ))}
        </dl>
      </div>
    </div>
  );
}
