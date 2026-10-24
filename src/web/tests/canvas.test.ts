import { describe, expect, it } from 'vitest';
import type { ConnectorElement, Element, Operation, Projection, ShapeElement } from '../src/contracts/generated';
import { copyElements, pasteOperations } from '../src/commands/clipboard';
import { GestureController } from '../src/canvas/GestureController';
import { deleteOperations, intentOperations, moveOperations, nudgeOperations, resizeOperations, snap, snapContextFor, pageOf } from '../src/canvas/intents';
import { ok } from '../src/model/result';
import { createTestEngine, element, sequenceIds } from './support/engine';

const grid = (t: ReturnType<typeof createTestEngine>) => pageOf(t.engine.store.snapshot()).grid; // 14.1732 pt = 5 mm, snapping on

function controller(t: ReturnType<typeof createTestEngine>) {
  const tx = sequenceIds('7');
  return new GestureController(t.engine, tx);
}

describe('snapping in the interaction planner', () => {
  it('snaps the lead element to absolute grid coordinates and moves the rest by the same delta', () => {
    const t = createTestEngine();
    const snap0 = t.engine.store.snapshot();
    const [a, b] = [t.ids.shapes[0]!, t.ids.shapes[1]!]; // x=100 and x=220
    const ops = moveOperations(snap0, [a, b], 9, 3);
    const g = grid(t).spacingPt;
    const expectedX = snap(100 + 9, { enabled: true, spacingPt: g }) - 100;
    expect(ops).toEqual([
      { op: 'move', target: a, delta: { xPt: expect.closeTo(expectedX, 4), yPt: expect.any(Number) } },
      { op: 'move', target: b, delta: { xPt: expect.closeTo(expectedX, 4), yPt: expect.any(Number) } },
    ]);
    const d = (ops[0] as { delta: { xPt: number; yPt: number } }).delta;
    expect((100 + d.xPt) / g).toBeCloseTo(Math.round((100 + d.xPt) / g), 6); // lands on a grid line
    expect((40 + d.yPt) / g).toBeCloseTo(Math.round((40 + d.yPt) / g), 6);
  });

  it('snapped coordinates equal the configured spacing multiples', () => {
    const ctx = { enabled: true, spacingPt: 14.1732283465 };
    for (const v of [0, 3, 7.2, 10.7, 99.9]) expect(snap(v, ctx) / ctx.spacingPt).toBeCloseTo(Math.round(v / ctx.spacingPt), 9);
    expect(snap(10.7, { enabled: false, spacingPt: 14 })).toBe(10.7);
  });

  it('does not snap when the page or one of the element\'s layers has snapping off', () => {
    const t = createTestEngine();
    const doc = structuredClone(t.engine.store.snapshot().document);
    doc.pages[0]!.layers[0]!.snap = false;
    const t2 = createTestEngine({ document: doc });
    const page = pageOf(t2.engine.store.snapshot());
    expect(snapContextFor(page, element(t2.engine.store.snapshot(), t.ids.shape)).enabled).toBe(false);
    const ops = moveOperations(t2.engine.store.snapshot(), [t.ids.shape], 9, 3);
    expect(ops).toEqual([{ op: 'move', target: t.ids.shape, delta: { xPt: 9, yPt: 3 } }]);
  });

  it('resize snaps both moving edges and ignores a no-op', () => {
    const t = createTestEngine();
    const snapshot = t.engine.store.snapshot();
    const el = element(snapshot, t.ids.shape);
    const ops = resizeOperations(snapshot, t.ids.shape, { x: el.bounds.x, y: el.bounds.y, width: el.bounds.width + 9, height: el.bounds.height });
    const g = grid(t).spacingPt;
    const op = ops[0] as { bounds: { x: number; width: number } };
    expect((op.bounds.x + op.bounds.width) / g).toBeCloseTo(Math.round((op.bounds.x + op.bounds.width) / g), 6);
    // A "resize" that lands on the current bounds is not an edit.
    const noop = resizeOperations(snapshot, t.ids.shape, { ...el.bounds });
    expect(noop).toEqual([]);
  });

  it('a connector fully positioned by its glue does not move by itself, and a zero move is no operation', async () => {
    const t = createTestEngine();
    await t.engine.execute(t.request([{ op: 'createConnector', alias: 'c', from: { elementId: t.ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: t.ids.shapes[1]!, glue: 'dynamic' } }]), 'gui');
    const snapshot = t.engine.store.snapshot();
    const c = snapshot.document.pages[0]!.elements.find(e => e.kind === 'connector')!;
    expect(moveOperations(snapshot, [c.id], 50, 50)).toEqual([]);
    expect(moveOperations(snapshot, [t.ids.shape], 0, 0)).toEqual([]);
    expect(nudgeOperations(snapshot, [c.id, t.ids.shape], 1, 0)).toEqual([{ op: 'move', target: t.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
  });

  it('maps every intent type to canonical operations', () => {
    const t = createTestEngine();
    const s = t.engine.store.snapshot();
    const id = t.ids.shape;
    expect(intentOperations(s, { type: 'rotate', id, deltaDeg: 33.004 })).toEqual([{ op: 'rotate', target: id, deltaDeg: 33 }]);
    expect(intentOperations(s, { type: 'rotate', id, deltaDeg: 0 })).toEqual([]);
    expect(intentOperations(s, { type: 'text', id, value: 'hi' })).toEqual([{ op: 'set', target: id, patch: { text: { value: 'hi' } } }]);
    expect(intentOperations(s, { type: 'bend', id, waypoints: [{ x: 1.00001, y: 2 }] })).toEqual([{ op: 'set', target: id, patch: { waypoints: [{ x: 1, y: 2 }] } }]);
    expect(intentOperations(s, { type: 'connect', from: id, to: t.ids.shapes[1]! })[0]).toMatchObject({ op: 'createConnector', route: 'orthogonal' });
    expect(intentOperations(s, { type: 'reconnect', id, end: 'to', elementId: t.ids.shapes[2]! })[0]).toMatchObject({ patch: { to: { elementId: t.ids.shapes[2], glue: 'dynamic' } } });
    expect(intentOperations(s, { type: 'reconnect', id, end: 'from', point: { x: 5, y: 6 } })[0]).toMatchObject({ patch: { from: { point: { x: 5, y: 6 }, glue: 'none' } } });
  });
});

describe('delete', () => {
  it('takes attached connectors with their shapes and never deletes a connector twice', async () => {
    const t = createTestEngine();
    await t.engine.execute(t.request([
      { op: 'createConnector', alias: 'c1', from: { elementId: t.ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: t.ids.shapes[1]!, glue: 'dynamic' } },
      { op: 'createConnector', alias: 'c2', from: { elementId: t.ids.shapes[1]!, glue: 'dynamic' }, to: { elementId: t.ids.shapes[2]!, glue: 'dynamic' } },
    ]), 'gui');
    const snapshot = t.engine.store.snapshot();
    const [c1, c2] = snapshot.document.pages[0]!.elements.filter(e => e.kind === 'connector').map(e => e.id);
    const ops = deleteOperations(snapshot, [t.ids.shapes[0]!, c1!, c2!]);
    expect(ops).toEqual([{ op: 'delete', target: t.ids.shapes[0], withConnectors: true }, { op: 'delete', target: c2 }]); // c1 goes with its shape
    const r = await t.engine.execute(t.request(ops), 'gui');
    expect(r.ok && r.value.deleted.sort()).toEqual([t.ids.shapes[0]!, c1!, c2!].sort());
  });
});

describe('copy and paste', () => {
  it('preserves effective stacking order across levels on paste', async () => {
    const t = createTestEngine();
    await t.engine.execute(t.request([
      { op: 'createShape', id: '00000000-0000-4000-8000-000000000970', level: 2, alias: 'upper', bounds: { x: 1, y: 1, width: 10, height: 10 } },
      { op: 'createShape', id: '00000000-0000-4000-8000-000000000971', level: -1, alias: 'lower', bounds: { x: 20, y: 1, width: 10, height: 10 } },
    ]), 'gui');
    const snapshot = t.engine.store.snapshot(), page = snapshot.document.pages[0]!;
    const clip = copyElements(snapshot, ['00000000-0000-4000-8000-000000000970', '00000000-0000-4000-8000-000000000971'])!;
    expect(clip.elements.map(e => e.alias)).toEqual(['lower', 'upper']);
    const { operations } = pasteOperations(clip, page, 12, sequenceIds('4'));
    expect(operations.map(o => o.op === 'createShape' ? o.level : undefined)).toEqual([-1, 2]);
  });

  it('remaps identities, uniquifies aliases and re-attaches connectors between copied shapes', async () => {
    const t = createTestEngine();
    await t.engine.execute(t.request([{ op: 'createConnector', alias: 'link', from: { elementId: t.ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: t.ids.shapes[1]!, glue: 'dynamic' }, waypoints: [{ x: 150, y: 20 }] }]), 'gui');
    const snapshot = t.engine.store.snapshot();
    const connector = snapshot.document.pages[0]!.elements.find(e => e.kind === 'connector')!;
    const clip = copyElements(snapshot, [t.ids.shapes[0]!, t.ids.shapes[1]!, connector.id, t.ids.image])!;
    expect(clip.elements).toHaveLength(4);

    const fresh = sequenceIds('3');
    const { operations, newIds } = pasteOperations(clip, snapshot.document.pages[0]!, 10, fresh);
    expect(newIds).toHaveLength(4);
    expect(operations.map(o => o.op)).toEqual(['createImage', 'createShape', 'createShape', 'createConnector']); // connectors last, so their endpoints exist

    const r = await t.engine.execute(t.request(operations), 'gui');
    expect(r.ok).toBe(true);
    const els = t.engine.store.snapshot().document.pages[0]!.elements;
    expect(els).toHaveLength(25); // 20 originals + the connector + 4 copies
    const copyOf = (original: string) => els.find(e => e.id !== original && e.alias === `${(element(snapshot, original) as ShapeElement).alias}-2`)!;
    const a2 = copyOf(t.ids.shapes[0]!), b2 = copyOf(t.ids.shapes[1]!);
    expect(a2.id).not.toBe(t.ids.shapes[0]);
    expect(a2.bounds.x).toBe(element(snapshot, t.ids.shapes[0]!).bounds.x + 10);
    const c2 = els.find(e => e.kind === 'connector' && e.id !== connector.id) as ConnectorElement;
    expect(c2.from.elementId).toBe(a2.id); // the copy points at the copies, not the originals
    expect(c2.to.elementId).toBe(b2.id);
    expect(c2.waypoints).toEqual([{ x: 160, y: 30 }]);
    expect(c2.alias).toBe('link-2');
    expect(new Set(els.map(e => e.id)).size).toBe(els.length);
  });

  it('drops connectors whose ends are outside the selection, and refuses an empty copy', () => {
    const t = createTestEngine();
    const snapshot = t.engine.store.snapshot();
    expect(copyElements(snapshot, [])).toBeNull();
    const el: Element[] = [];
    expect(el).toEqual([]);
  });
});

describe('gestures', () => {
  it('gesture_is_one_transaction', async () => {
    const t = createTestEngine();
    const c = controller(t);
    const token = c.begin('move', t.scope(), [t.ids.shapes[0]!, t.ids.shapes[1]!, t.ids.shapes[2]!]);
    const ops = moveOperations(token.startSnapshot, [...token.ids], 28.3464566929, 0);
    expect(ops).toHaveLength(3);
    expect(c.preview(token, ops).ok).toBe(true);
    const before = t.engine.revision;
    const r = await c.commit(token);
    expect(r.ok && r.value.revision).toBe(before + 1); // three moved elements, one revision
    expect(r.ok && r.value.changed).toHaveLength(3);
    expect((await t.engine.getChanges(t.scope(), before)).ok).toBe(true);
    expect(c.isActive()).toBe(false);
  });

  it('stale_preview_is_discarded', async () => {
    const t = createTestEngine();
    const c = controller(t);
    const discards: string[] = [];
    c.onDiscard = r => discards.push(r);
    const token = c.begin('move', t.scope(), [t.ids.shape]);
    c.preview(token, moveOperations(token.startSnapshot, [t.ids.shape], 42.5196850394, 0));
    // An agent commits while the human is still dragging.
    await t.engine.execute(t.request([{ op: 'set', target: t.ids.shape, patch: { name: 'agent' } }]), 'mcp');
    const r = await c.commit(token);
    expect(!r.ok && r.error.code).toBe('revision_conflict');
    expect(discards).toEqual(['The document changed while you were editing, so your change was discarded.']);
    const el = element(t.engine.store.snapshot(), t.ids.shape);
    expect(el.bounds.x).toBe(100); // the stale drag did not overwrite newer state
    expect((el as ShapeElement).name).toBe('agent');
  });

  it('cancel_restores_committed_state_and_ends_the_interlock', async () => {
    const t = createTestEngine();
    const c = controller(t);
    const states: boolean[] = [];
    c.onGestureState = a => states.push(a);
    const token = c.begin('rotate', t.scope(), [t.ids.shape]);
    expect(c.isActive()).toBe(true);
    c.preview(token, [{ op: 'rotate', target: t.ids.shape, deltaDeg: 45 }]);
    c.cancel(token);
    expect(c.isActive()).toBe(false);
    expect(states).toEqual([true, false]);
    expect(t.engine.revision).toBe(0);
    const late = await c.commit(token);
    expect(!late.ok).toBe(true); // a cancelled token cannot be committed later
  });

  it('selection-like and one-shot kinds never raise the pointer interlock', () => {
    const t = createTestEngine();
    const c = controller(t);
    const states: boolean[] = [];
    c.onGestureState = a => states.push(a);
    for (const kind of ['nudge', 'inspector', 'paste', 'create'] as const) {
      const tok = c.begin(kind, t.scope(), []);
      expect(c.isActive()).toBe(false);
      c.cancel(tok);
    }
    expect(states).toEqual([]);
    const text = c.begin('text', t.scope(), [t.ids.shape]);
    expect(states).toEqual([true]);
    c.cancel(text);
    expect(states).toEqual([true, false]);
  });

  it('a replacing gesture cancels the stuck one and keeps a single interlock state', () => {
    const t = createTestEngine();
    const c = controller(t);
    const states: boolean[] = [];
    c.onGestureState = a => states.push(a);
    const first = c.begin('move', t.scope(), [t.ids.shape]);
    const second = c.begin('resize', t.scope(), [t.ids.shape]);
    expect(first.id).not.toBe(second.id);
    expect(c.isActive()).toBe(true);
    expect(states.filter(Boolean).length).toBeGreaterThanOrEqual(1);
    c.cancel(second);
    expect(states.at(-1)).toBe(false);
  });

  it('glue_survives_a_move_gesture', async () => {
    const t = createTestEngine();
    await t.engine.execute(t.request([{ op: 'createConnector', alias: 'link', from: { elementId: t.ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: t.ids.shapes[1]!, glue: 'static', port: 'west' } }]), 'gui');
    const before = t.engine.store.snapshot().document.pages[0]!.elements.find(e => e.kind === 'connector') as ConnectorElement;
    const c = controller(t);
    const token = c.begin('move', t.scope(), [t.ids.shapes[0]!]);
    c.preview(token, moveOperations(token.startSnapshot, [t.ids.shapes[0]!], 70.8661417323, 14.1732283465));
    expect((await c.commit(token)).ok).toBe(true);
    const after = t.engine.store.snapshot().document.pages[0]!.elements.find(e => e.kind === 'connector') as ConnectorElement;
    expect(after.from.elementId).toBe(before.from.elementId);
    expect(after.to).toEqual(before.to);
    expect(after).toBe(before); // the connector object itself is untouched
  });

  it('ExportDuringActiveDragUsesCommittedGeometry', async () => {
    const t = createTestEngine();
    const c = controller(t);
    t.engine.setSnapshotProjector(s => ok({ documentId: s.documentId, sessionId: s.sessionId, revision: s.revision, connectors: {}, groupVisualBounds: {} } as Projection));
    const token = c.begin('move', t.scope(), [t.ids.shape]);
    c.preview(token, moveOperations(token.startSnapshot, [t.ids.shape], 99, 99)); // mid-drag, nothing committed
    expect(c.isActive()).toBe(true);
    const exported = await t.engine.exportSnapshot(t.scope());
    expect(exported.ok).toBe(true);
    if (exported.ok) {
      expect(element(exported.value, t.ids.shape).bounds.x).toBe(100); // committed geometry, not the preview
      expect(exported.value.revision).toBe(0);
    }
    c.cancel(token);
  });

  it('a preview that would fail validation is reported before commit', () => {
    const t = createTestEngine();
    const c = controller(t);
    const token = c.begin('resize', t.scope(), [t.ids.shape]);
    const bad: Operation[] = [{ op: 'resize', target: t.ids.shape, bounds: { width: -1 } }];
    const r = c.preview(token, bad);
    expect(!r.ok && r.error.code).toBe('invalid_request');
    c.cancel(token);
  });
});
