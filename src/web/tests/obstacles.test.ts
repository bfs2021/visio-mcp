import { describe, expect, it } from 'vitest';
import type { Element } from '../src/contracts/generated';
import { selectObstacles, visualRect } from '../src/layout/obstacles';

const solid = { kind: 'solid', colour: '#fff', opacity: 1 } as const;
const shape = (id: string, x: number, y: number, w: number, h: number, extra: object = {}): Element => ({
  id, kind: 'shape', layerIds: [], zIndex: 1, locked: false, hidden: false, bounds: { x, y, width: w, height: h }, rotationDeg: 0, metadata: {}, geometry: { preset: 'rectangle' },
  style: { fill: solid, stroke: solid, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' }, ...extra,
}) as Element;
const pick = (els: Element[], ends: string[], endRects = [{ x: 0, y: 0, width: 10, height: 10 }], free: Array<{ x: number; y: number }> = []) =>
  selectObstacles(els, [], new Set(ends), endRects, free, 1_000_000);

describe('obstacle selection', () => {
  it('excludes legacy end shapes, text boxes, invisible shapes and hidden elements', () => {
    const els = [
      shape('a', 0, 0, 10, 10), shape('t', 100, 100, 20, 20, { kind: 'text' }), shape('ghost', 200, 200, 20, 20, { style: { fill: { kind: 'none' }, stroke: { kind: 'none' }, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' } }),
      shape('hid', 300, 300, 20, 20, { hidden: true }), shape('wall', 400, 400, 20, 20),
    ];
    expect(pick(els, ['a'])).toEqual([{ id: 'wall', rect: { x: 400, y: 400, width: 20, height: 20 }, policy: 'forbidden', clearancePt: 8, source: 'element' }]);
  });

  it('treats a shape holding an end shape, a free end, or another shape as a legacy container', () => {
    const els = [shape('frame', -50, -50, 200, 200), shape('lane', 300, 0, 200, 100), shape('inner', 320, 20, 20, 20), shape('free', 600, 0, 50, 50)];
    const records = pick(els, [], undefined, [{ x: 620, y: 20 }]);
    expect(records.map(r => r.id)).toEqual(['inner']);
  });

  it('treats a very large legacy shape as a backdrop', () => {
    expect(pick([shape('bg', 0, 100, 900, 900)], [])).toEqual([]);
  });

  it('uses the rotated box', () => {
    const r = visualRect({ bounds: { x: 0, y: 0, width: 100, height: 20 }, rotationDeg: 90 });
    expect([r.x, r.y, r.width, r.height].map(Math.round)).toEqual([40, -40, 20, 100]);
  });
});