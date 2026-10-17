import { describe, expect, it } from 'vitest';
import type { Projection } from '../src/contracts/generated';
import { planRender } from '../src/render/SnapshotRenderer';
import { createTestEngine } from './support/engine';
import { id, run } from './support/ops';

const plan = (t: ReturnType<typeof createTestEngine>, p: Parameters<typeof planRender>[2], projection?: Projection) => planRender(t.store.snapshot(), projection, p);

describe('render planning', () => {
  it('a page render fits the page inside 1600 x 1200 by default and keeps its aspect ratio', () => {
    const t = createTestEngine();
    const r = plan(t, { scope: 'page' });
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.value.dims.crop).toEqual({ x: 0, y: 0, width: 841.8897637795, height: 595.2755905512 });
    expect(r.value.dims.widthPx).toBe(1600);
    expect(r.value.dims.heightPx).toBe(Math.round(1600 * 595.2755905512 / 841.8897637795));
    expect(r.value.format).toBe('png');
    expect(r.value.mode).toBe('clean');
  });

  it('a region needs exactly one of bounds or element ids, and its padding counts', () => {
    const t = createTestEngine();
    expect(plan(t, { scope: 'region' }).ok).toBe(false);
    expect(plan(t, { scope: 'region', bounds: { x: 0, y: 0, width: 10, height: 10 }, elementIds: ['s0'] }).ok).toBe(false);
    expect(plan(t, { scope: 'page', elementIds: ['s0'] }).ok).toBe(false);
    const r = plan(t, { scope: 'region', bounds: { x: 100, y: 50, width: 200, height: 100 }, paddingPt: 20 });
    expect(r.ok && r.value.dims.crop).toEqual({ x: 80, y: 30, width: 240, height: 140 });
    expect(plan(t, { scope: 'region', bounds: { x: 0, y: 0, width: 0, height: 10 } }).ok).toBe(false);
  });

  it('framing elements includes the visual bounds of rotated members, groups and routed connectors', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'rotate', target: 's1', angleDeg: 90 },
      { op: 'group', targets: ['s0', 's1'], id: id(900), alias: 'pair' },
      { op: 'createConnector', id: id(950), from: { elementId: 's0', glue: 'dynamic' }, to: { elementId: 's5', glue: 'dynamic' } },
    ]);
    // s1 is 100 x 60 about (270,70); turned a quarter it covers y 20..120, so the group runs y 20..120 and x 100..320
    const g = plan(t, { scope: 'region', elementIds: ['pair'], paddingPt: 0 });
    expect(g.ok && g.value.dims.crop).toEqual({ x: 100, y: 20, width: 200, height: 100 });
    const routed: Projection = {
      documentId: t.documentId, sessionId: t.sessionId, revision: 2, groupVisualBounds: { [id(900)]: { x: 90, y: 10, width: 260, height: 130 } },
      connectors: { [id(950)]: { routePoints: [], visualBounds: { x: 150, y: 70, width: 400, height: 300 } } },
    };
    const c = plan(t, { scope: 'region', elementIds: [id(950)], paddingPt: 0 }, routed);
    expect(c.ok && c.value.dims.crop).toEqual({ x: 150, y: 70, width: 400, height: 300 }); // the route as drawn, not the stale fallback box
    const withGroup = plan(t, { scope: 'region', elementIds: ['pair'], paddingPt: 0 }, routed);
    expect(withGroup.ok && withGroup.value.dims.crop).toEqual({ x: 90, y: 10, width: 260, height: 130 }); // a group's frame can include routing
    const both = plan(t, { scope: 'region', elementIds: ['s0', 's9'], paddingPt: 0 });
    expect(both.ok && both.value.dims.crop).toEqual({ x: 100, y: 40, width: 580 + 100 - 100, height: 90 + 60 }); // s0 at (100,40), s9 at (580,130): 100..680 by 40..190
  });

  it('unknown or ambiguous elements are errors, not guesses', () => {
    const t = createTestEngine();
    expect(plan(t, { scope: 'region', elementIds: ['nothing'] }).ok).toBe(false);
  });

  it('16 MP and bad parameters are rejected', () => {
    const t = createTestEngine();
    const big = plan(t, { scope: 'page', maxWidth: 8192, maxHeight: 8192 });
    expect(!big.ok && big.error.code).toBe('limit_exceeded');
    expect(plan(t, { scope: 'page', maxWidth: 0 }).ok).toBe(false);
    expect(plan(t, { scope: 'page', maxWidth: 9000 }).ok).toBe(false);
    expect(plan(t, { scope: 'page', format: 'gif' as never }).ok).toBe(false);
    expect(plan(t, { scope: 'page', mode: 'x' as never }).ok).toBe(false);
    expect(plan(t, { scope: 'page', pageId: id(77) }).ok).toBe(false);
    const small = plan(t, { scope: 'page', maxWidth: 4000, maxHeight: 4000 });
    expect(small.ok && small.value.dims.widthPx * small.value.dims.heightPx).toBeLessThanOrEqual(16_000_000);
  });

  it('a tiny region is enlarged but not beyond a sensible scale', () => {
    const t = createTestEngine();
    const r = plan(t, { scope: 'region', bounds: { x: 100, y: 100, width: 10, height: 10 }, paddingPt: 0 });
    expect(r.ok && r.value.dims.widthPx).toBe(80); // 8 pixels per point at most
  });
});
