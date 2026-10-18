import { describe, expect, it } from 'vitest';
import { inspectLayout } from '../src/layout/inspectLayout';
import { createTestEngine, exactElementHashes } from './support/engine';
import { id, run } from './support/ops';
import type { Operation } from '../src/contracts/generated';

const ctx = { measureWidth: (s: string) => s.length * 6 };
const shape = (n: number, x: number, y: number, extra = {}): Operation => ({ op: 'createShape', id: id(n), bounds: { x, y, width: 80, height: 40 }, ...extra });
async function scene(ops: Operation[]) {
  const doc = structuredClone(createTestEngine().store.snapshot().document); doc.pages[0]!.elements = [];
  const t = createTestEngine({ document: doc }); await run(t, ops); return t;
}
const hints = (t: Awaited<ReturnType<typeof scene>>) => inspectLayout(t.store.snapshot(), ctx).issues.filter(i => i.code === 'NEAR_ALIGNMENT');

describe('reference layout diagnostics', () => {
  it.each([
    { axis: 'y', ops: [shape(1, 40, 100), shape(2, 180, 105)], delta: 5 },
    { axis: 'x', ops: [shape(1, 100, 40), shape(2, 106, 150)], delta: 6 },
  ])('reports a measured $axis near-miss without editing anything', async ({ axis, ops, delta }) => {
    const t = await scene(ops), before = exactElementHashes(t.store.snapshot());
    expect(hints(t)).toHaveLength(1);
    expect(hints(t)[0]).toMatchObject({ severity: 'info', elementIds: [id(1), id(2)], evidence: { axis, deltaPt: delta, heuristic: true } });
    expect(inspectLayout(t.store.snapshot(), ctx).notes.join(' ')).toMatch(/alignment.*heuristic/i);
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
  });
  it('does not flag exact alignment, distant rows, overlap or dissimilar sizes', async () => {
    for (const ops of [
      [shape(1, 40, 100), shape(2, 180, 100)],
      [shape(1, 40, 100), shape(2, 180, 130)],
      [shape(1, 40, 100), shape(2, 45, 105)],
      [shape(1, 40, 100), shape(2, 180, 105, { bounds: { x: 180, y: 105, width: 200, height: 80 } })],
    ]) expect(hints(await scene(ops))).toEqual([]);
  });
  it('excludes hidden, rotated, grouped and custom-vector objects', async () => {
    for (const op of [{ op: 'set', target: id(2), patch: { hidden: true } }, { op: 'rotate', target: id(2), angleDeg: 20 }] as Operation[]) {
      const t = await scene([shape(1, 40, 100), shape(2, 180, 105), op]);
      expect(hints(t)).toEqual([]);
    }
    const grouped = await scene([shape(1, 40, 100), shape(2, 180, 105), { op: 'group', id: id(3), targets: [id(1), id(2)] }]);
    expect(hints(grouped)).toEqual([]);
    const vectors = await scene([shape(1, 40, 100, { geometry: { preset: 'rectangle', svgPath: 'M0 0 L100 100' } }), shape(2, 180, 105)]);
    expect(hints(vectors)).toEqual([]);
  });
  it('excludes unpainted shapes and fully transparent images', async () => {
    const t = await scene([shape(1, 40, 100), shape(2, 180, 105)]);
    const snapshot = structuredClone(t.store.snapshot());
    for (const e of snapshot.document.pages[0]!.elements) if (e.kind === 'shape') { e.style.fill = { kind: 'none' }; e.style.stroke = { kind: 'none' }; }
    expect(inspectLayout(snapshot, ctx).issues.filter(i => i.code === 'NEAR_ALIGNMENT')).toEqual([]);
    snapshot.document.pages[0]!.elements = snapshot.document.pages[0]!.elements.map(e => ({ ...e, kind: 'image', assetId: id(99), opacity: 0, fit: 'contain' } as unknown as typeof e));
    expect(inspectLayout(snapshot, ctx).issues.filter(i => i.code === 'NEAR_ALIGNMENT')).toEqual([]);
  });
  it('bounds comparison work and reports truncation rather than silently implying a full analysis', async () => {
    const t = await scene([shape(1, 40, 100)]), snapshot = structuredClone(t.store.snapshot());
    const template = snapshot.document.pages[0]!.elements[0]!;
    snapshot.document.pages[0]!.elements = Array.from({ length: 350 }, (_, n) => ({ ...structuredClone(template), id: id(n + 100), bounds: { x: n * 1000, y: n * 1000, width: 80, height: 40 } }));
    const report = inspectLayout(snapshot, ctx);
    expect(report.notes.join(' ')).toMatch(/50,000 pairs/);
    expect(report.issues.filter(i => i.code === 'NEAR_ALIGNMENT')).toEqual([]);
  });
  it('treats visible thin rules as painted geometry but retains tiny and unpainted warnings', async () => {
    const t = await scene([
      shape(1, 40, 40, { bounds: { x: 40, y: 40, width: 300, height: 0.8 }, style: { fill: { kind: 'solid', colour: '#E2E8F0', opacity: 1 }, stroke: { kind: 'none' } } }),
      shape(2, 40, 80, { bounds: { x: 40, y: 80, width: 300, height: 0.8 }, style: { fill: { kind: 'none' }, stroke: { kind: 'none' } } }),
      shape(3, 40, 120, { bounds: { x: 40, y: 120, width: 0.001, height: 40 } }),
      shape(4, 40, 180, { bounds: { x: 40, y: 180, width: 0.2, height: 40 } }),
    ]);
    const zero = inspectLayout(t.store.snapshot(), ctx).issues.filter(i => i.code === 'ZERO_SIZE');
    expect(zero.map(i => i.elementIds[0])).toEqual([id(2), id(3), id(4)]);
    expect(zero.find(i => i.elementIds[0] === id(3))?.severity).toBe('error');
    expect(hints(t)).toEqual([]);
  });
});
