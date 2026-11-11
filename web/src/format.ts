export function bytes(n: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n;
  let u = 0;
  while (v >= 1024 && u < units.length - 1) {
    v /= 1024;
    u++;
  }
  if (u === 0) return `${n} B`;
  return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${units[u]}`;
}

export function count(n: number): string {
  return n.toLocaleString('en-US');
}

export function seconds(s: number | null | undefined): string {
  if (s == null) return '—';
  return s < 10 ? `${s.toFixed(1)}s` : `${Math.round(s)}s`;
}

export function ago(iso: string): string {
  const then = new Date(iso).getTime();
  const diff = Date.now() - then;
  const min = Math.round(diff / 60000);
  if (min < 1) return 'just now';
  if (min < 60) return `${min} min ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  if (d === 1) return 'yesterday';
  if (d < 7) return `${d} days ago`;
  return new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}

export function fileName(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export function cap(s: string): string {
  return s.length ? s[0].toUpperCase() + s.slice(1) : s;
}
