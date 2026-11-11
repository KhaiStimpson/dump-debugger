export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Squarified treemap layout (Bruls, Huizing & van Wijk): lays sizes out in rows that keep
 * cells as close to square as possible, so labels stay readable. Input order is preserved in
 * the output; sizes should be sorted descending for best results.
 */
export function squarify(sizes: number[], bounds: Rect): Rect[] {
  const total = sizes.reduce((a, b) => a + b, 0);
  if (total <= 0 || sizes.length === 0) return sizes.map(() => ({ x: bounds.x, y: bounds.y, w: 0, h: 0 }));
  const scale = (bounds.w * bounds.h) / total;
  const areas = sizes.map((s) => s * scale);
  const out: Rect[] = new Array(sizes.length);

  let { x, y, w, h } = bounds;
  let i = 0;
  while (i < areas.length) {
    const short = Math.min(w, h);
    let row = [i];
    let best = worst([areas[i]], short);
    while (i + row.length < areas.length) {
      const candidate = [...row, i + row.length];
      const ratio = worst(
        candidate.map((k) => areas[k]),
        short,
      );
      if (ratio > best) break;
      row = candidate;
      best = ratio;
    }

    const rowArea = row.reduce((a, k) => a + areas[k], 0);
    const thickness = rowArea / short;
    let offset = 0;
    for (const k of row) {
      const length = areas[k] / thickness;
      out[k] = w >= h ? { x, y: y + offset, w: thickness, h: length } : { x: x + offset, y, w: length, h: thickness };
      offset += length;
    }
    if (w >= h) {
      x += thickness;
      w -= thickness;
    } else {
      y += thickness;
      h -= thickness;
    }
    i += row.length;
  }
  return out;
}

function worst(row: number[], side: number): number {
  const sum = row.reduce((a, b) => a + b, 0);
  const max = Math.max(...row);
  const min = Math.min(...row);
  const s2 = side * side;
  return Math.max((s2 * max) / (sum * sum), (sum * sum) / (s2 * min));
}
