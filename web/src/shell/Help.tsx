import { useEffect, useRef } from 'react';
import { glossary } from '../glossary';
import { useApp } from '../state/store';

const keys: [string, string][] = [
  ['Ctrl K', 'Search requests, threads, locks, types, objects, commands'],
  ['Ctrl O', 'Open a dump'],
  ['Ctrl E', 'Export incident report'],
  ['G then H / D / T / L / M / O / C / R', 'Go to Home, Diagnosis, Threads, Locks, Memory, Objects, Compare, Report'],
  ['J / K', 'Next / previous row or finding'],
  ['Enter', 'Open or expand the selected row'],
  ['P', 'Pin or unpin what the inspector is showing'],
  ['Alt ← / Alt →', 'Inspector back / forward'],
  ['Esc', 'Close the palette, this overlay, or the inspector'],
  ['?', 'Show this list'],
];

/** The "?" overlay: every keyboard shortcut, and the glossary behind the dotted-underlined terms. */
export function Help() {
  const open = useApp((s) => s.helpOpen);
  const tab = useApp((s) => s.helpTab);
  const focus = useApp((s) => s.glossaryFocus);
  const setHelp = useApp((s) => s.setHelp);
  const focused = useRef<HTMLDivElement>(null);

  useEffect(() => focused.current?.scrollIntoView({ block: 'center' }), [focus, tab]);
  if (!open) return null;

  return (
    <div className="palette-scrim" onClick={() => setHelp(false)}>
      <div
        className="palette on help"
        role="dialog"
        aria-modal="true"
        aria-label="Help"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="pane-h">
          <div className="seg" role="tablist" aria-label="Help sections">
            <button
              role="tab"
              aria-selected={tab === 'keys'}
              className={tab === 'keys' ? 'on' : ''}
              onClick={() => useApp.setState({ helpTab: 'keys' })}
            >
              Keyboard shortcuts
            </button>
            <button
              role="tab"
              aria-selected={tab === 'glossary'}
              className={tab === 'glossary' ? 'on' : ''}
              onClick={() => useApp.setState({ helpTab: 'glossary' })}
            >
              Glossary
            </button>
          </div>
          <div className="r">
            <button className="btn ghost" onClick={() => setHelp(false)} aria-label="Close help">
              ✕
            </button>
          </div>
        </div>
        {tab === 'keys' ? (
          <div role="tabpanel" aria-label="Keyboard shortcuts">
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
        ) : (
          <div role="tabpanel" aria-label="Glossary">
            <dl className="glossary">
              {Object.entries(glossary)
                .sort(([a], [b]) => a.localeCompare(b))
                .map(([term, def]) => (
                  <div key={term} ref={term === focus ? focused : undefined} className={term === focus ? 'focus' : ''}>
                    <dt>{term}</dt>
                    <dd>{def}</dd>
                  </div>
                ))}
            </dl>
          </div>
        )}
      </div>
    </div>
  );
}
