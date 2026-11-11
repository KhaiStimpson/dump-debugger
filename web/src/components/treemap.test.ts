import { describe, expect, it } from 'vitest';
import { squarify } from './treemap';

describe('squarify', () => {
  it('fills the bounds exactly with areas proportional to size', () => {
    const sizes = [600, 300, 200, 100, 50];
    const rects = squarify(sizes, { x: 0, y: 0, w: 400, h: 250 });
    const total = sizes.reduce((a, b) => a + b, 0);
    const area = rects.reduce((a, r) => a + r.w * r.h, 0);
    expect(area).toBeCloseTo(400 * 250, 3);
    rects.forEach((r, i) => expect((r.w * r.h) / (400 * 250)).toBeCloseTo(sizes[i] / total, 3));
  });

  it('keeps every cell inside the bounds', () => {
    const rects = squarify([5, 4, 3, 2, 1, 1, 1], { x: 10, y: 20, w: 300, h: 120 });
    for (const r of rects) {
      expect(r.x).toBeGreaterThanOrEqual(10 - 1e-9);
      expect(r.y).toBeGreaterThanOrEqual(20 - 1e-9);
      expect(r.x + r.w).toBeLessThanOrEqual(310 + 1e-9);
      expect(r.y + r.h).toBeLessThanOrEqual(140 + 1e-9);
    }
  });
});
