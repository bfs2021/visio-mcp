import { describe, expect, it } from 'vitest';
import { mapIngressDropPoint } from '../src/workspace/ingressGeometry';

const viewport = { scale: 2, scrollLeft: 40, scrollTop: 60, marginPt: 10, pageWidthPt: 600, pageHeightPt: 800 };

describe('native file drop geometry', () => {
  it('translates canvas client coordinates through the captured viewport exactly once', () => {
    expect(mapIngressDropPoint({ x: 150, y: 250 }, { left: 100, top: 200, right: 500, bottom: 600 }, viewport)).toEqual({ x: 35, y: 45 });
  });

  it('uses the page centre when the native drop was outside the canvas', () => {
    expect(mapIngressDropPoint({ x: 20, y: 20 }, { left: 100, top: 200, right: 500, bottom: 600 }, viewport)).toEqual({ x: 300, y: 400 });
  });

  it('rejects a non-finite point or invalid viewport scale', () => {
    expect(mapIngressDropPoint({ x: Number.NaN, y: 2 }, { left: 0, top: 0, right: 10, bottom: 10 }, viewport)).toBeNull();
    expect(mapIngressDropPoint({ x: 2, y: 2 }, { left: 0, top: 0, right: 10, bottom: 10 }, { ...viewport, scale: 0 })).toBeNull();
  });
});
