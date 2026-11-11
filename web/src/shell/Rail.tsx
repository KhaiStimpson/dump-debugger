import { icons } from '../components/Icons';
import { navigate, useRoute, type View } from '../router';
import { useApp } from '../state/store';

const items: { view: View; label: string; key: string }[] = [
  { view: 'home', label: 'Home', key: 'H' },
  { view: 'diagnosis', label: 'Diagnosis', key: 'D' },
  { view: 'threads', label: 'Threads', key: 'T' },
  { view: 'locks', label: 'Locks', key: 'L' },
  { view: 'memory', label: 'Memory', key: 'M' },
  { view: 'objects', label: 'Objects', key: 'O' },
  { view: 'compare', label: 'Compare', key: 'C' },
];

/** Fixed left rail of views. Views that need an open dump are disabled until one is. */
export function Rail() {
  const { view } = useRoute();
  const dump = useApp((s) => s.dump);
  const critical = useApp((s) => s.diagnosis?.verdict.severity === 'critical');
  const active = view === 'analyzing' ? 'home' : view;

  const button = (v: View, label: string, key: string) => {
    const needsDump = v !== 'home' && v !== 'compare';
    return (
      <button
        key={v}
        className={active === v ? 'on' : ''}
        title={`${label}  (G ${key})`}
        aria-label={label}
        aria-current={active === v ? 'page' : undefined}
        disabled={needsDump && !dump}
        onClick={() => navigate(v)}
      >
        {icons[v as keyof typeof icons]}
        {v === 'diagnosis' && critical && <span className="badge" aria-label="critical finding" />}
      </button>
    );
  };

  return (
    <nav className="rail" aria-label="Views">
      {items.map((i) => button(i.view, i.label, i.key))}
      <div className="sp" />
      {button('report', 'Report', 'R')}
    </nav>
  );
}
