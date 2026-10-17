import { describe, expect, it } from 'vitest';
import type { RequestEnvelope } from '../src/contracts/generated';
import { createEngineHandler } from '../src/bridge/engineHandlers';
import { listAssets, readObjects, readSummary } from '../src/bridge/readTools';
import { createTestEngine, exactElementHashes } from './support/engine';
import { id, run } from './support/ops';

describe('read tools', () => {
  it('summary is compact by default and opt-in for style, hidden elements and geometry', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'set', target: 's3', patch: { hidden: true } },
      { op: 'createConnector', id: id(950), from: { elementId: 's0', port: 'east', glue: 'static' }, to: { elementId: 's1', glue: 'dynamic' }, label: { value: 'flows' } },
    ]);
    const snap = t.store.snapshot();
    const before = exactElementHashes(snap);
    const r = readSummary(snap);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const s = r.value as { revision: number; assetIds: string[]; pages: Array<{ elements: Array<Record<string, unknown>>; omittedHidden: number; layers: Array<{ elementCount: number }> }> };
    expect(s.revision).toBe(snap.revision);
    expect(s.assetIds).toEqual(['asset:logo']);
    const els = s.pages[0]!.elements;
    expect(els).toHaveLength(20); // 21 elements, one hidden
    expect(s.pages[0]!.omittedHidden).toBe(1);
    expect(els.find(e => e.alias === 's0')).toMatchObject({ kind: 'shape', bounds: { x: 100 }, text: 'Shape 0' });
    expect(els.find(e => e.alias === 's0')!.style).toBeUndefined();
    expect(els.find(e => e.id === id(950))).toMatchObject({ kind: 'connector', text: 'flows', from: { elementId: expect.any(String), port: 'east', glue: 'static' } });
    const full = readSummary(snap, { includeStyle: true, includeGeometry: false, includeHidden: true });
    const e0 = (full.ok ? (full.value as typeof s).pages[0]!.elements.find(e => e.alias === 's0') : undefined) as Record<string, unknown>;
    expect(e0.style).toBeDefined();
    expect(e0.bounds).toBeUndefined();
    expect((full.ok && (full.value as typeof s).pages[0]!.elements.length)).toBe(21);
    expect(readSummary(snap, { pageId: id(77) }).ok).toBe(false);
    expect(exactElementHashes(snap)).toEqual(before); // a read changes nothing
  });

  it('get_objects returns complete canonical elements by id or alias and reports what it cannot find', async () => {
    const t = createTestEngine();
    const snap = t.store.snapshot();
    const r = readObjects(snap, [t.ids.shapes[1]!, 's2']);
    expect(r.ok && r.value.map(e => e.id)).toEqual([t.ids.shapes[1], t.ids.shapes[2]]);
    expect(r.ok && (r.value[0] as { style: unknown }).style).toBeDefined(); // everything, not a summary
    const missing = readObjects(snap, ['nothing']);
    expect(!missing.ok && missing.error.code).toBe('not_found');
  });

  it('list_assets filters by text and tags and counts references', async () => {
    const t = createTestEngine();
    const snap = t.store.snapshot();
    expect(listAssets(snap).map(a => [a.id, a.references])).toEqual([['asset:logo', 1], ['asset:logo-v2', 0]]);
    expect(listAssets(snap, 'V2').map(a => a.id)).toEqual(['asset:logo-v2']);
    expect(listAssets(snap, undefined, ['logo'])).toHaveLength(2);
    expect(listAssets(snap, undefined, ['nope'])).toHaveLength(0);
  });
});

describe('request handler: tool methods', () => {
  const call = async (t: ReturnType<typeof createTestEngine>, method: string, params: Record<string, unknown>) =>
    createEngineHandler(t.engine)({ protocolVersion: 1, kind: 'request', requestId: 'q', method, ...t.scope(), params } as RequestEnvelope);

  it('doc.executeScript returns the transaction and the operations the script became', async () => {
    const t = createTestEngine();
    const r = await call(t, 'doc.executeScript', { transactionId: id(7001), baseRevision: 0, script: 'add shape id=n x=1mm y=1mm w=10mm h=5mm text="Hi"' });
    expect(r.error).toBeUndefined();
    const res = r.result as { transaction: { revision: number; created: string[] }; operations: Array<{ op: string }> };
    expect(res.transaction.revision).toBe(1);
    expect(res.operations.map(o => o.op)).toEqual(['createShape']);
    const retry = await call(t, 'doc.executeScript', { transactionId: id(7001), baseRevision: 0, script: 'add shape id=n x=1mm y=1mm w=10mm h=5mm text="Hi"' });
    expect((retry.result as typeof res).transaction).toEqual(res.transaction); // the same result, once
    expect(t.engine.revision).toBe(1);
  });

  it('a script error is an invalid_request with its line', async () => {
    const t = createTestEngine();
    const r = await call(t, 'doc.executeScript', { transactionId: id(7002), baseRevision: 0, script: 'move s0 dx=1mm dy=1mm\nmove s0 dx=oops' });
    expect(r.error?.code).toBe('invalid_request');
    expect(r.error?.details?.line).toBe(2);
    expect(t.engine.revision).toBe(0);
  });

  it('export is not ready yet, and render is not ready without a drawing surface; layout inspection works anywhere', async () => {
    const t = createTestEngine();
    for (const m of ['doc.render', 'doc.export']) expect((await call(t, m, { scope: 'page' })).error?.code).toBe('not_ready');
    const layout = await call(t, 'doc.inspectLayout', {});
    expect(layout.error).toBeUndefined();
    expect((layout.result as { issues: unknown[]; notes: string[] }).notes.length).toBeGreaterThan(0);
  });

  it('summary, objects, assets and changes answer from the committed store', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'move', target: 's0', delta: { xPt: 3, yPt: 0 } }]);
    expect(((await call(t, 'doc.summary', {})).result as { revision: number }).revision).toBe(1);
    expect(((await call(t, 'doc.getObjects', { ids: ['s0'] })).result as { objects: Array<{ bounds: { x: number } }> }).objects[0]!.bounds.x).toBe(103);
    expect(((await call(t, 'assets.list', { query: 'logo' })).result as { assets: unknown[] }).assets).toHaveLength(2);
    const ch = (await call(t, 'doc.changes', { sinceRevision: 0 })).result as { transactions: Array<{ changed: string[] }> };
    expect(ch.transactions).toHaveLength(1);
    expect((await call(t, 'doc.getObjects', { ids: [] })).error?.code).toBe('invalid_request');
  });
});
