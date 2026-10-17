import { describe, expect, it } from 'vitest';
import type { ConnectorElement, ShapeElement } from '../src/contracts/generated';
import { createTestEngine, element, exactElementHashes, hashesExcept } from './support/engine';

describe('incremental commit and rollback', () => {
  it('one_logo_patch_changes_one_object_and_leaves_19_byte_identical', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const before = store.snapshot();
    const r = await engine.execute(request([{ op: 'set', target: ids.image, patch: { assetId: 'asset:logo-v2' } }]), 'mcp');
    expect(r.ok && r.value.changed).toEqual([ids.image]);
    expect(store.snapshot().revision).toBe(before.revision + 1);
    expect(hashesExcept(store.snapshot(), ids.image)).toEqual(hashesExcept(before, ids.image));
    expect(Object.keys(exactElementHashes(store.snapshot()))).toHaveLength(20);
    // Unchanged elements are the very same immutable objects, not copies.
    expect(element(store.snapshot(), ids.shape)).toBe(element(before, ids.shape));
  });

  it('late_invalid_operation_rolls_back_all', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const before = store.snapshot();
    const r = await engine.execute(request([
      { op: 'move', target: ids.shape, delta: { xPt: 10, yPt: 0 } },
      { op: 'set', target: ids.image, patch: { assetId: 'asset:logo-v2' } },
      { op: 'set', target: ids.image, patch: { assetId: 'asset:does-not-exist' } },
    ]), 'mcp');
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error.code).toBe('invalid_request');
    expect(store.snapshot().revision).toBe(before.revision);
    expect(exactElementHashes(store.snapshot())).toEqual(exactElementHashes(before));
    expect(store.snapshot().document).toBe(before.document);
  });

  it('nested_stroke_patch_preserves_other_leaves', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const before = element(store.snapshot(), ids.shape) as ShapeElement;
    const r = await engine.execute(request([
      { op: 'set', target: ids.shape, patch: { style: { stroke: { kind: 'solid', colour: '#333333', opacity: 1 } } } },
    ]), 'mcp');
    expect(r.ok).toBe(true);
    const after = element(store.snapshot(), ids.shape) as ShapeElement;
    expect(after.style.stroke).toEqual({ kind: 'solid', colour: '#333333', opacity: 1 });
    expect({ ...after.style, stroke: before.style.stroke }).toEqual(before.style); // every other style leaf untouched
    expect(after.text).toEqual(before.text);
    expect(after.bounds).toEqual(before.bounds);
    expect(after.metadata).toBe(before.metadata);
  });

  it('create_then_reference', async () => {
    const { engine, store, request } = createTestEngine();
    const r = await engine.execute(request([
      { op: 'createShape', alias: 'a', bounds: { x: 10, y: 10, width: 50, height: 30 }, text: { value: 'A' } },
      { op: 'createShape', alias: 'b', bounds: { x: 200, y: 10, width: 50, height: 30 } },
      { op: 'createConnector', from: { elementId: 'a', glue: 'dynamic' }, to: { elementId: 'b', port: 'west', glue: 'static' }, route: 'orthogonal' },
      { op: 'move', target: 'a', delta: { xPt: 5, yPt: 5 } },
    ]), 'mcp');
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.value.created).toHaveLength(3);
    const els = store.snapshot().document.pages[0]!.elements;
    const a = els.find(e => e.alias === 'a')!;
    const b = els.find(e => e.alias === 'b')!;
    const c = els.find(e => e.kind === 'connector') as ConnectorElement;
    expect(c.from.elementId).toBe(a.id); // alias was resolved to a UUID before commit
    expect(c.to.elementId).toBe(b.id);
    expect(a.bounds.x).toBe(15);
    expect(a.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(new Set(els.map(e => e.zIndex)).size).toBe(els.length); // distinct stacking order
  });

  it('no_op_has_no_history', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const before = store.snapshot();
    const r = await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 0, yPt: 0 } }]), 'mcp');
    expect(r.ok && r.value.noChange).toBe(true);
    expect(r.ok && r.value.revision).toBe(before.revision);
    expect(store.snapshot().document).toBe(before.document);
    const changes = await engine.getChanges(engine.scope(), 0);
    expect(changes.ok && changes.value.transactions).toEqual([]);
    const undo = await engine.undo({ ...engine.scope(), transactionId: '00000000-0000-4000-8000-000000000f01', baseRevision: engine.revision });
    expect(undo.ok).toBe(false);
  });

  it('ambiguous_name_returns_candidates_and_changes_nothing', async () => {
    const { engine, store, request, ids } = createTestEngine();
    await engine.execute(request([{ op: 'set', target: ids.shapes[1]!, patch: { name: 'Shape 0' } }]), 'mcp');
    const before = store.snapshot();
    const r = await engine.execute(request([{ op: 'set', target: 'Shape 0', patch: { hidden: true } }]), 'mcp');
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('ambiguous_target');
      expect((r.error.details?.candidates as string[]).sort()).toEqual([ids.shapes[0]!, ids.shapes[1]!].sort());
    }
    expect(store.snapshot().revision).toBe(before.revision);
  });

  it('resolves uuid first, then alias, then exact name', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const r = await engine.execute(request([
      { op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } },
      { op: 'move', target: 's1', delta: { xPt: 1, yPt: 0 } },
      { op: 'move', target: 'Shape 2', delta: { xPt: 1, yPt: 0 } },
    ]), 'gui');
    expect(r.ok && r.value.changed.sort()).toEqual([ids.shapes[0]!, ids.shapes[1]!, ids.shapes[2]!].sort());
    expect(element(store.snapshot(), ids.shapes[3]!)).toBeDefined();
  });

  it('locked_target_rejects_geometry_but_allows_unlock', async () => {
    const { engine, store, request, ids } = createTestEngine();
    expect((await engine.execute(request([{ op: 'set', target: ids.shape, patch: { locked: true } }]), 'gui')).ok).toBe(true);
    const move = await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 5, yPt: 0 } }]), 'mcp');
    expect(!move.ok && move.error.code).toBe('locked_target');
    const restyle = await engine.execute(request([{ op: 'set', target: ids.shape, patch: { name: 'x' } }]), 'mcp');
    expect(!restyle.ok && restyle.error.code).toBe('locked_target');
    expect((await engine.execute(request([{ op: 'set', target: ids.shape, patch: { locked: false } }]), 'gui')).ok).toBe(true);
    expect((await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 5, yPt: 0 } }]), 'mcp')).ok).toBe(true);
    expect(element(store.snapshot(), ids.shape).bounds.x).toBe(105);
  });

  it('locked_layer_locks_its_members', async () => {
    const { engine, request, ids } = createTestEngine();
    const doc = structuredClone(engine.store.snapshot().document);
    doc.pages[0]!.layers[0]!.locked = true;
    const locked = createTestEngine({ document: doc });
    const r = await locked.engine.execute(locked.request([{ op: 'move', target: ids.shape, delta: { xPt: 5, yPt: 0 } }]), 'mcp');
    expect(!r.ok && r.error.code).toBe('locked_target');
    void request;
  });

  it('deleting_a_connected_shape_is_a_dependency_conflict_unless_connectors_go_too', async () => {
    const { engine, store, request, ids } = createTestEngine();
    await engine.execute(request([{ op: 'createConnector', alias: 'link', from: { elementId: ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: ids.shapes[1]!, glue: 'dynamic' } }]), 'gui');
    const blocked = await engine.execute(request([{ op: 'delete', target: ids.shapes[0]! }]), 'mcp');
    expect(!blocked.ok && blocked.error.code).toBe('dependency_conflict');
    if (!blocked.ok) expect((blocked.error.details?.connectorIds as string[]).length).toBe(1);
    const both = await engine.execute(request([{ op: 'delete', target: ids.shapes[0]!, withConnectors: true }]), 'mcp');
    expect(both.ok && both.value.deleted).toHaveLength(2);
    expect(store.snapshot().document.pages[0]!.elements.some(e => e.kind === 'connector')).toBe(false);
  });

  it('resize_rotate_and_text_patch', async () => {
    const { engine, store, request, ids } = createTestEngine();
    const r = await engine.execute(request([
      { op: 'resize', target: ids.shape, bounds: { width: 200 } },
      { op: 'rotate', target: ids.shape, angleDeg: 33 },
      { op: 'rotate', target: ids.shape, deltaDeg: 10 },
      { op: 'set', target: ids.shape, patch: { text: { value: 'Renamed', bold: true } } },
    ]), 'gui');
    expect(r.ok).toBe(true);
    const s = element(store.snapshot(), ids.shape) as ShapeElement;
    expect(s.bounds).toEqual({ x: 100, y: 40, width: 200, height: 60 });
    expect(s.rotationDeg).toBe(43);
    expect(s.text).toMatchObject({ value: 'Renamed', bold: true, fontFamily: 'Segoe UI' });
    const bad = await engine.execute(request([{ op: 'resize', target: ids.shape, bounds: { width: -5 } }]), 'gui');
    expect(!bad.ok && bad.error.code).toBe('invalid_request');
  });

  it('result_reports_visually_affected_connectors_without_rewriting_them', async () => {
    const { engine, store, request, ids } = createTestEngine();
    await engine.execute(request([{ op: 'createConnector', alias: 'link', from: { elementId: ids.shapes[0]!, glue: 'dynamic' }, to: { elementId: ids.shapes[1]!, glue: 'dynamic' } }]), 'gui');
    const connectorHash = exactElementHashes(store.snapshot());
    const r = await engine.execute(request([{ op: 'move', target: ids.shapes[0]!, delta: { xPt: 30, yPt: 0 } }]), 'gui');
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const connector = store.snapshot().document.pages[0]!.elements.find(e => e.kind === 'connector')!;
    expect(r.value.changed).toEqual([ids.shapes[0]!]);
    expect(r.value.visuallyAffectedIds).toContain(connector.id);
    expect(exactElementHashes(store.snapshot())[connector.id]).toBe(connectorHash[connector.id]); // canonical connector untouched
  });

  it('registers_an_asset_and_references_it_in_the_same_batch', async () => {
    const { engine, store, request } = createTestEngine();
    const asset = { id: 'asset:new', name: 'New', mimeType: 'image/png' as const, sha256: 'c'.repeat(64), tags: [] };
    const r = await engine.execute(request([
      { op: 'registerAsset', asset },
      { op: 'createImage', alias: 'pic', bounds: { x: 0, y: 0, width: 30, height: 30 }, assetId: 'asset:new' },
    ]), 'mcp');
    expect(r.ok && r.value.changedAssetIds).toEqual(['asset:new']);
    expect(store.snapshot().document.assets.map(a => a.id)).toContain('asset:new');
  });
});
