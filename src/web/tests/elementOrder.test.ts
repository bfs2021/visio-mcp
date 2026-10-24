import { describe, expect, it } from 'vitest';
import { compareElementOrder, effectiveLevel } from '../src/model/elementOrder';
import type { Element } from '../src/contracts/generated';

const shape = (id: string, zIndex: number, level?: number): Element => ({
  id, kind: 'shape', layerIds: [], zIndex, ...(level === undefined ? {} : { level }), locked: false, hidden: false,
  bounds: { x: 0, y: 0, width: 10, height: 10 }, rotationDeg: 0, metadata: {}, geometry: { preset: 'rectangle' },
  style: { fill: { kind: 'none' }, stroke: { kind: 'none' }, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' },
});

describe('effective element order', () => {
  it('defaults_missing_levels_to_zero_and_orders_level_before_z_index', () => {
    const base = shape('base', 100);
    const lower = shape('lower', 1000, -1);
    const upper = shape('upper', 1, 1);
    expect(effectiveLevel(base)).toBe(0);
    expect([upper, base, lower].sort(compareElementOrder)).toEqual([lower, base, upper]);
  });

  it('compares_large_z_indices_without_packing_level_and_z_index', () => {
    const later = shape('later', Number.MAX_SAFE_INTEGER, 2);
    const earlier = shape('earlier', Number.MAX_SAFE_INTEGER - 1, 2);
    expect(compareElementOrder(earlier, later)).toBeLessThan(0);
    expect(compareElementOrder(shape('lower', Number.MAX_SAFE_INTEGER, 1), later)).toBeLessThan(0);
  });
});
