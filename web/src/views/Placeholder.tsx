import type { View } from '../router';

/** Stand-in for views not built yet in this phase. */
export function Placeholder({ view }: { view: View }) {
  return (
    <div className="view on" style={{ placeItems: 'center' }}>
      <p style={{ color: 'var(--fg-3)' }}>The {view} view isn't available yet.</p>
    </div>
  );
}
