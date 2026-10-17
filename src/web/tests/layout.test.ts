import { describe, expect, it } from 'vitest';
import type { Projection } from '../src/contracts/generated';
import { inspectLayout, type LayoutContext } from '../src/layout/inspectLayout';
import { createTestEngine, exactElementHashes } from './support/engine';
import { id, run } from './support/ops';

const ctx = (extra: Partial<LayoutContext> = {}): LayoutContext => ({ measureWidth: (t, f) => t.length * f.sizePt * 0.5, ...extra });
const box = (x: number, y: number, w = 60, h = 40) => ({ x, y, width: w, height: h });

/** An empty page, so only what a test creates is judged. */
async function scene(ops: Parameters<typeof run>[1]) {
  const doc = structuredClone(createTestEngine().store.snapshot().document);
  doc.pages[0]!.elements = [];
  const fresh = createTestEngine({ document: doc });
  await run(fresh, ops);
  return fresh;
}
const found = (t: Awaited<ReturnType<typeof scene>>, c: LayoutContext = ctx()) => inspectLayout(t.store.snapshot(), c).issues;

describe('inspect layout', () => {
  it('reports elements outside the page, partly or entirely, with their bounds', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), bounds: box(800, 100) },
      { op: 'createShape', id: id(2), bounds: box(-500, -500) },
      { op: 'createShape', id: id(3), bounds: box(100, 100) },
    ]);
    const issues = found(t).filter(i => i.code === 'OUTSIDE_PAGE');
    expect(issues.map(i => [i.elementIds[0], i.severity])).toEqual([[id(1), 'warning'], [id(2), 'error']]);
    expect(issues[0]!.bounds).toEqual(box(800, 100));
    expect((issues[0]!.evidence.overhangPt as { right: number }).right).toBeCloseTo(800 + 60 - 841.8897637795, 6);
    expect(found(t).some(i => i.elementIds.includes(id(3)))).toBe(false);
  });

  it('text that does not fit is reported with the sizes it was judged by', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), bounds: box(100, 100, 80, 20), text: { value: 'a long label that cannot fit on one short line', fontSizePt: 12 } },
      { op: 'createShape', id: id(2), bounds: box(100, 200, 200, 60), text: { value: 'fits', fontSizePt: 12 } },
      { op: 'createShape', id: id(3), bounds: box(100, 300, 40, 40), text: { value: 'Unbreakable', fontSizePt: 12 } },
    ]);
    const overflow = found(t).filter(i => i.code === 'TEXT_OVERFLOW');
    expect(overflow.map(i => i.elementIds[0])).toEqual([id(1), id(3)]);
    expect(overflow[0]!.evidence).toMatchObject({ measured: 'estimated', lines: expect.any(Number), availableHeightPt: 12 });
    expect(overflow[0]!.evidence.neededHeightPt as number).toBeGreaterThan(12);
    expect(overflow[1]!.evidence.neededWidthPt as number).toBeGreaterThan(overflow[1]!.evidence.availableWidthPt as number); // one long word
  });

  it('unglued connector ends are reported', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), alias: 'a', bounds: box(100, 100) },
      { op: 'createConnector', id: id(10), from: { elementId: 'a', glue: 'dynamic' }, to: { glue: 'none', point: { x: 300, y: 300 } } },
    ]);
    const issues = found(t).filter(i => i.code === 'UNGLUED_CONNECTOR_END');
    expect(issues).toHaveLength(1);
    expect(issues[0]!.evidence.end).toBe('to');
    expect(issues[0]!.elementIds).toEqual([id(10)]);
  });

  it('a connector through an unrelated shape is reported, but not through its own ends or their groups', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), alias: 'a', bounds: box(50, 100) },
      { op: 'createShape', id: id(2), alias: 'b', bounds: box(450, 100) },
      { op: 'createShape', id: id(3), alias: 'wall', bounds: box(250, 90, 40, 60) },
      { op: 'createConnector', id: id(10), from: { elementId: 'a', glue: 'dynamic' }, to: { elementId: 'b', glue: 'dynamic' } },
      { op: 'createShape', id: id(4), alias: 'c', bounds: box(50, 300) },
      { op: 'createShape', id: id(5), alias: 'd', bounds: box(450, 300) },
      { op: 'createConnector', id: id(11), from: { elementId: 'c', glue: 'dynamic' }, to: { elementId: 'd', glue: 'dynamic' } },
      { op: 'group', targets: ['c', 'd'], id: id(20) },
    ]);
    const crossings = found(t).filter(i => i.code === 'CONNECTOR_CROSSES_SHAPE');
    expect(crossings).toHaveLength(1);
    expect(crossings[0]!.elementIds).toEqual([id(10), id(3)]);
    expect(crossings[0]!.evidence.crossedElementIds).toEqual([id(3)]);
  });

  it('a resolved route is judged instead of the straight line', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), alias: 'a', bounds: box(50, 100) },
      { op: 'createShape', id: id(2), alias: 'b', bounds: box(450, 100) },
      { op: 'createShape', id: id(3), alias: 'wall', bounds: box(250, 90, 40, 60) },
      { op: 'createConnector', id: id(10), from: { elementId: 'a', glue: 'dynamic' }, to: { elementId: 'b', glue: 'dynamic' } },
    ]);
    const around: Projection = {
      documentId: t.documentId, sessionId: t.sessionId, revision: 1, groupVisualBounds: {},
      connectors: { [id(10)]: { routePoints: [{ x: 110, y: 120 }, { x: 110, y: 20 }, { x: 480, y: 20 }, { x: 480, y: 100 }], visualBounds: box(110, 20, 370, 100) } },
    };
    expect(found(t, ctx({ projection: around })).some(i => i.code === 'CONNECTOR_CROSSES_SHAPE')).toBe(false);
    expect(found(t).some(i => i.code === 'CONNECTOR_CROSSES_SHAPE')).toBe(true);
  });

  it('overlap ignores containment and members of one group', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), bounds: box(100, 100, 200, 120) },
      { op: 'createShape', id: id(2), bounds: box(120, 120, 60, 40) }, // inside the first: a container
      { op: 'createShape', id: id(3), bounds: box(250, 150, 100, 80) }, // overlaps the first
      { op: 'createShape', id: id(4), bounds: box(500, 100) }, { op: 'createShape', id: id(5), bounds: box(530, 110) },
      { op: 'group', targets: [id(4), id(5)], id: id(20) },
    ]);
    expect(found(t).filter(i => i.code === 'OVERLAP').map(i => i.elementIds)).toEqual([[id(1), id(3)]]);
  });

  it('picture distortion needs the natural size and compares aspect ratios', async () => {
    const t = await scene([
      { op: 'createImage', id: id(1), assetId: 'asset:logo', bounds: box(100, 100, 100, 100), fit: 'stretch', preserveAspectRatio: false },
      { op: 'createImage', id: id(2), assetId: 'asset:logo', bounds: box(300, 100, 100, 50), fit: 'contain', preserveAspectRatio: true },
    ]);
    expect(found(t).some(i => i.code === 'IMAGE_DISTORTION')).toBe(false); // size unknown: not judged
    const issues = found(t, ctx({ assetSize: () => ({ width: 200, height: 100 }) })).filter(i => i.code === 'IMAGE_DISTORTION');
    expect(issues.map(i => i.elementIds[0])).toEqual([id(1)]);
    expect(issues[0]!.evidence).toMatchObject({ naturalAspect: 2, shownAspect: 1 });
  });

  it('low contrast text, zero size and quiet hidden-object notes', async () => {
    const t = await scene([
      { op: 'createShape', id: id(1), bounds: box(100, 100, 100, 40), style: { fill: { kind: 'solid', colour: '#ffffff', opacity: 1 } }, text: { value: 'faint', colour: '#eeeeee' } },
      { op: 'createShape', id: id(2), bounds: box(300, 100, 0.2, 40) },
      { op: 'createShape', id: id(3), bounds: box(400, 100) }, { op: 'set', target: id(3), patch: { hidden: true } },
    ]);
    const issues = found(t);
    expect(issues.find(i => i.code === 'LOW_CONTRAST')!.evidence.contrastRatio as number).toBeLessThan(3);
    expect(issues.find(i => i.code === 'ZERO_SIZE')!.elementIds).toEqual([id(2)]);
    expect(issues.find(i => i.code === 'HIDDEN_OBJECT')).toMatchObject({ severity: 'info', elementIds: [id(3)] });
  });

  it('every issue names elements, a page and bounds, and inspection changes nothing', async () => {
    const t = await scene([{ op: 'createShape', id: id(1), bounds: box(800, 100) }, { op: 'createShape', id: id(2), bounds: box(780, 110) }]);
    const before = exactElementHashes(t.store.snapshot());
    const snap = t.store.snapshot();
    const report = inspectLayout(snap, ctx());
    expect(report.issues.length).toBeGreaterThan(0);
    for (const i of report.issues) {
      expect(i.elementIds.length).toBeGreaterThan(0);
      expect(i.pageId).toBe(snap.document.pages[0]!.id);
      expect(Number.isFinite(i.bounds.width)).toBe(true);
    }
    expect(report.notes.join(' ')).toMatch(/estimated/);
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
    expect(t.store.snapshot().document).toBe(snap.document);
  });
});
