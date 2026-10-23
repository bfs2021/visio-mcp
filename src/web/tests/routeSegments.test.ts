import { describe, expect, it } from 'vitest';
import type { ConnectorElement, Element, Operation, TransactionResult } from '../src/contracts/generated';
import { newDocument } from '../src/model/newDocument';
import { moveSegment, segmentsOf, simplifyRoute } from '../src/layout/routeSegments';
import { routeDefects } from '../src/layout/routePlanner';
import { canonicalSerialize } from '../src/model/canonical';
import { createTestEngine } from './support/engine';

const P = (x: number, y: number) => ({ x, y });
const Z = [P(0, 0), P(50, 0), P(50, 40), P(100, 40)];

describe('route segments (R21)', () => {
  it('lists the runs and marks the port runs as fixed', () => {
    const segs = segmentsOf(Z);
    expect(segs.map(s => [s.index, s.axis, s.coord, s.movable])).toEqual([[0, 'h', 0, false], [1, 'v', 50, true], [2, 'h', 40, false]]);
    expect(segs[0]!.fixedReason).toMatch(/port/);
    expect(segmentsOf([P(0, 0), P(100, 0)])[0]!.fixedReason).toMatch(/straight/);
  });

  it('slides an inner run and lengthens its neighbours', () => {
    const r = moveSegment(Z, 1, 70);
    expect(r).toEqual({ ok: true, points: [P(0, 0), P(70, 0), P(70, 40), P(100, 40)] });
  });

  it('merges a run dragged level with its neighbour and leaves no redundant bends', () => {
    const six = [P(0, 0), P(40, 0), P(40, 30), P(80, 30), P(80, 60), P(120, 60)];
    // run 3 (x=80, vertical is index 3) moved onto x=40 makes run 2 vanish: the two vertical runs join into one
    const r = moveSegment(six, 3, 40);
    expect(r.ok && r.points).toEqual([P(0, 0), P(40, 0), P(40, 60), P(120, 60)]);
    if (r.ok) expect(routeDefects(r.points)).toEqual([]);
  });

  it('refuses a port run, an unknown run and a no-op', () => {
    expect(moveSegment(Z, 0, 10)).toMatchObject({ ok: false, code: 'segment_fixed' });
    expect(moveSegment(Z, 9, 10)).toMatchObject({ ok: false, code: 'segment_missing' });
    expect(moveSegment(Z, 1, 50)).toMatchObject({ ok: false, code: 'no_movement' });
  });

  it('simplify keeps both ends and drops collinear points', () => {
    expect(simplifyRoute([P(0, 0), P(10, 0), P(10, 0), P(20, 0), P(20, 5)])).toEqual([P(0, 0), P(20, 0), P(20, 5)]);
  });
});

const box = (x: number, y: number) => ({ x, y, width: 100, height: 60 });
async function setup() {
  const t = createTestEngine({ document: newDocument() });
  const page = t.store.snapshot().document.pages[0]!.id;
  const run = async (ops: object[]) => t.engine.execute(t.request(ops.map(o => ((o as { op: string }).op.startsWith('create') ? { pageId: page, ...o } : o) as Operation)), 'mcp');
  const ok = async (ops: object[]): Promise<TransactionResult> => {
    const r = await run(ops);
    if (!r.ok) throw new Error(`${r.error.code}: ${r.error.message}`);
    return r.value;
  };
  await ok([
    { op: 'createShape', alias: 'a', bounds: box(0, 100) },
    { op: 'createShape', alias: 'd', bounds: box(700, 400) },
    { op: 'createConnector', alias: 'z', from: { elementId: 'a', port: 'east', glue: 'static' }, to: { elementId: 'd', port: 'west', glue: 'static' }, route: 'orthogonal' },
  ]);
  const doc = () => t.store.snapshot().document;
  const conn = () => doc().pages[0]!.elements.find(e => e.alias === 'z') as ConnectorElement;
  return { t, run, ok, doc, conn };
}

describe('moveSegment operation (R21)', () => {
  it('moves the middle run, makes the connector manual and keeps the ends glued', async () => {
    const s = await setup();
    const was = s.conn().routing!.advisory!;
    const run = was[1]!.x;
    const result = await s.ok([{ op: 'moveSegment', target: 'z', segment: 1, by: 30 }]);
    expect(result.changed).toContain(s.conn().id);
    const c = s.conn();
    expect(c.waypoints.length).toBeGreaterThan(0);
    expect(c.routing).toBeUndefined();
    expect(c.waypoints.every(w => w.x === run + 30 || w.y === was[0]!.y || w.y === was.at(-1)!.y)).toBe(true);
    expect(c.from.glue).toBe('static');
  });

  it('to= and by= agree, and one undo restores the original', async () => {
    const s = await setup();
    const before = canonicalSerialize(s.doc().pages);
    const run = s.conn().routing!.advisory![1]!.x;
    await s.ok([{ op: 'moveSegment', target: 'z', segment: 1, to: run + 25 }]);
    const after = canonicalSerialize(s.doc().pages);
    const undo = await s.t.engine.undo({ ...s.t.scope(), transactionId: '00000000-0000-4000-8000-0000000000ab', baseRevision: s.t.engine.revision });
    expect(undo.ok).toBe(true);
    expect(canonicalSerialize(s.doc().pages)).toBe(before);
    await s.ok([{ op: 'moveSegment', target: 'z', segment: 1, by: 25 }]);
    expect(canonicalSerialize(s.doc().pages)).toBe(after); // by= and to= land on the same geometry
  });

  it('refuses a port run with segment_fixed and changes nothing', async () => {
    const s = await setup();
    const before = canonicalSerialize(s.doc().pages);
    const r = await s.run([{ op: 'moveSegment', target: 'z', segment: 0, by: 10 }]);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(JSON.stringify(r.error)).toMatch(/segment_fixed/);
    expect(canonicalSerialize(s.doc().pages)).toBe(before);
  });

  it('keeps a frozen connector frozen with the moved route', async () => {
    const s = await setup();
    await s.ok([{ op: 'set', target: 'z', patch: { routing: { mode: 'frozen' } } }]);
    const run = s.conn().routing!.advisory![1]!.x;
    await s.ok([{ op: 'moveSegment', target: 'z', segment: 1, by: 40 }]);
    const c = s.conn();
    expect(c.routing?.mode).toBe('frozen');
    expect(c.waypoints).toEqual([]);
    expect(c.routing!.advisory![1]!.x).toBe(run + 40);
  });
});

describe('jogs (R21)', () => {
  it('a straight connector dragged out of line gets a jog, and bringing the shape back removes it with no stale vertices', async () => {
    const t = createTestEngine({ document: newDocument() });
    const page = t.store.snapshot().document.pages[0]!.id;
    const ok = async (ops: object[]) => {
      const r = await t.engine.execute(t.request(ops.map(o => ((o as { op: string }).op.startsWith('create') ? { pageId: page, ...o } : o) as Operation)), 'mcp');
      if (!r.ok) throw new Error(r.error.message);
    };
    await ok([
      { op: 'createShape', alias: 'a', bounds: box(0, 100) },
      { op: 'createShape', alias: 'b', bounds: box(500, 100) },
      { op: 'createConnector', alias: 'w', from: { elementId: 'a', port: 'east', glue: 'static' }, to: { elementId: 'b', port: 'west', glue: 'static' }, route: 'orthogonal' },
    ]);
    const route = () => (t.store.snapshot().document.pages[0]!.elements.find(e => e.alias === 'w') as ConnectorElement).routing!.advisory!;
    expect(route()).toHaveLength(2);
    await ok([{ op: 'move', target: 'b', delta: { xPt: 0, yPt: 40 } }]);
    expect(route()).toHaveLength(4); // the minimum jog: one step
    expect(routeDefects(route())).toEqual([]);
    await ok([{ op: 'move', target: 'b', delta: { xPt: 0, yPt: -40 } }]);
    expect(route()).toHaveLength(2);
  });
});

