import { describe, expect, it } from 'vitest';
import type { ConnectorElement, Operation } from '../src/contracts/generated';
import { resolveTarget } from '../src/commands/resolveTarget';
import { createTestEngine, exactElementHashes, hashesExcept } from './support/engine';
import { byId, els, group, has, id, refused, run, shape } from './support/ops';

type T = ReturnType<typeof createTestEngine>;
const s = (t: T, n: number) => t.ids.shapes[n]!;
const PAGE2 = id(800);

describe('target resolution', () => {
  it('uuid_then_alias_then_exact_name', async () => {
    const t = createTestEngine();
    // s1 is also the display name of another element: the alias must win, the UUID must beat both
    await run(t, [{ op: 'set', target: s(t, 5), patch: { name: 's1' } }]);
    const snap = t.store.snapshot();
    expect(resolveTarget(snap, undefined, s(t, 3))).toEqual({ ok: true, value: s(t, 3) });
    expect(resolveTarget(snap, undefined, 's1')).toEqual({ ok: true, value: s(t, 1) });
    expect(resolveTarget(snap, undefined, 'Shape 7')).toEqual({ ok: true, value: s(t, 7) });
  });

  it('ambiguous_name_has_candidates', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'set', target: s(t, 0), patch: { name: 'Twin' } }, { op: 'set', target: s(t, 1), patch: { name: 'Twin' } }]);
    const r = resolveTarget(t.store.snapshot(), undefined, 'Twin');
    expect(r.ok).toBe(false);
    if (r.ok) return;
    expect(r.error.code).toBe('ambiguous_target');
    expect((r.error.details?.candidates as string[]).sort()).toEqual([s(t, 0), s(t, 1)].sort());
    const e = await refused(t, [{ op: 'move', target: 'Twin', delta: { xPt: 1, yPt: 1 } }]);
    expect(e.code).toBe('ambiguous_target');
  });

  it('page_aliases_do_not_collide', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }]);
    await run(t, [{ op: 'createShape', id: id(801), alias: 's0', pageId: PAGE2, bounds: { x: 0, y: 0, width: 10, height: 10 } }]);
    const snap = t.store.snapshot();
    expect(resolveTarget(snap, PAGE2, 's0')).toEqual({ ok: true, value: id(801) });
    expect(resolveTarget(snap, t.ids.page, 's0')).toEqual({ ok: true, value: s(t, 0) });
    expect(resolveTarget(snap, undefined, 's0').ok).toBe(false); // two pages: a bare alias has no scope
    await run(t, [{ op: 'move', target: 's0', delta: { xPt: 5, yPt: 0 } }], PAGE2);
    expect(byId(t, id(801)).bounds.x).toBe(5);
    expect(shape(t, s(t, 0)).bounds.x).toBe(100);
    // but the same alias twice on one page is refused
    const e = await refused(t, [{ op: 'createShape', alias: 's0', pageId: PAGE2, bounds: { x: 0, y: 0, width: 10, height: 10 } }]);
    expect(e.code).toBe('invalid_request');
  });

  it('kind_and_id_immutable', async () => {
    const t = createTestEngine();
    const before = exactElementHashes(t.store.snapshot());
    for (const patch of [{ kind: 'text' }, { id: id(1) }, { bounds: { x: 1, y: 1, width: 1, height: 1 } }, { rotationDeg: 5 }, { zIndex: 4 }]) {
      const r = await t.engine.execute(t.request([{ op: 'set', target: s(t, 0), patch: patch as never }]), 'mcp');
      expect(r.ok).toBe(false);
      if (!r.ok) expect(r.error.code).toBe('invalid_request');
    }
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
  });

  it('group_bounds_and_rotation_cannot_be_patched', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: id(900) }]);
    for (const patch of [{ bounds: { x: 1, y: 1, width: 1, height: 1 } }, { rotationDeg: 5 }, { style: { strokeWidthPt: 2 } }]) {
      const r = await t.engine.execute(t.request([{ op: 'set', target: id(900), patch: patch as never }]), 'mcp');
      expect(r.ok).toBe(false);
    }
    expect(group(t, id(900)).rotationDeg).toBe(0);
  });
});

describe('locks and dependencies', () => {
  it('locked_target_rejects', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'set', target: s(t, 0), patch: { locked: true } }]);
    const attempts: Operation[] = [
      { op: 'move', target: s(t, 0), delta: { xPt: 1, yPt: 1 } },
      { op: 'resize', target: s(t, 0), bounds: { width: 10 } },
      { op: 'rotate', target: s(t, 0), angleDeg: 4 },
      { op: 'delete', target: s(t, 0) },
      { op: 'set', target: s(t, 0), patch: { name: 'x' } },
      { op: 'assignLayers', targets: [s(t, 0)], layerIds: [], mode: 'set' },
      { op: 'reorder', targets: [s(t, 0)], to: 'front' },
    ];
    for (const op of attempts) expect((await refused(t, [op])).code).toBe('locked_target');
    // unlocking is the one patch a lock allows
    await run(t, [{ op: 'set', target: s(t, 0), patch: { locked: false } }]);
  });

  it('a_locked_layer_locks_its_members', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'setLayer', layerId: t.ids.layer, patch: { locked: true } }]);
    expect((await refused(t, [{ op: 'move', target: s(t, 0), delta: { xPt: 1, yPt: 1 } }])).code).toBe('locked_target');
    await run(t, [{ op: 'setLayer', layerId: t.ids.layer, patch: { locked: false } }]);
    await run(t, [{ op: 'move', target: s(t, 0), delta: { xPt: 1, yPt: 1 } }]);
  });

  it('delete_glued_target_requires_detach_or_delete', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'createConnector', id: id(950), from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 1), glue: 'dynamic' } }]);
    const e = await refused(t, [{ op: 'delete', target: s(t, 0) }]);
    expect(e.code).toBe('dependency_conflict');
    expect(e.details?.connectorIds).toEqual([id(950)]);
    // detaching in the same batch is explicit enough
    const r = await run(t, [
      { op: 'set', target: id(950), patch: { from: { glue: 'none', point: { x: 5, y: 5 } } } },
      { op: 'delete', target: s(t, 0) },
    ]);
    expect(r.deleted).toEqual([s(t, 0)]);
    expect(r.changed).toEqual([id(950)]);
    const c = byId<ConnectorElement>(t, id(950));
    expect(c.from).toEqual({ glue: 'none', point: { x: 5, y: 5 } });
    // or both go together
    const both = await run(t, [{ op: 'delete', target: s(t, 1), withConnectors: true }]);
    expect(both.deleted.sort()).toEqual([s(t, 1), id(950)].sort());
  });
});

describe('duplicate', () => {
  it('duplicate_remaps_internal_links', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'createConnector', id: id(950), alias: 'link', from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 1), port: 'east', glue: 'static' }, waypoints: [{ x: 150, y: 20 }] }]);
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'duplicate', targets: [s(t, 0), s(t, 1), id(950)], offset: { xPt: 0, yPt: 200 } }]);
    expect(r.created).toHaveLength(3);
    expect(r.changed).toEqual([]);
    expect(hashesExcept(t.store.snapshot(), ...r.created)).toEqual(exactElementHashes(before)); // originals untouched
    const copies = r.created.map(c => byId(t, c));
    const [a, b] = copies.filter(c => c.kind === 'shape') as ReturnType<typeof shape>[];
    const link = copies.find(c => c.kind === 'connector') as ConnectorElement;
    expect(a!.bounds).toEqual({ x: 100, y: 240, width: 100, height: 60 });
    expect(a!.alias).toBeUndefined(); // aliases are page-unique, so the copy has none unless asked
    expect(a!.name).toBe('Shape 0');
    expect(link.from.elementId).toBe(a!.id);
    expect(link.to).toEqual({ elementId: b!.id, port: 'east', glue: 'static' });
    expect(link.waypoints).toEqual([{ x: 150, y: 220 }]);
    expect(link.alias).toBeUndefined();
    expect(a!.zIndex).toBeGreaterThan(20);
  });

  it('copies_get_the_requested_alias_only_for_a_single_target', async () => {
    const t = createTestEngine();
    const r = await run(t, [{ op: 'duplicate', targets: [s(t, 0)], as: 'twin' }]);
    expect(byId(t, r.created[0]!).alias).toBe('twin');
    expect(byId(t, r.created[0]!).bounds).toEqual(shape(t, s(t, 0)).bounds);
    const e = await refused(t, [{ op: 'duplicate', targets: [s(t, 1), s(t, 2)], as: 'both' }]);
    expect(e.code).toBe('invalid_request');
    const clash = await refused(t, [{ op: 'duplicate', targets: [s(t, 1)], as: 's2' }]);
    expect(clash.code).toBe('invalid_request');
  });

  it('keepAliases_makes_copies_unique_by_suffix', async () => {
    const t = createTestEngine();
    const first = await run(t, [{ op: 'duplicate', targets: [s(t, 0), s(t, 1)], keepAliases: true }]);
    expect(first.created.map(c => byId(t, c).alias).sort()).toEqual(['s0-2', 's1-2']);
    const second = await run(t, [{ op: 'duplicate', targets: [s(t, 0)], keepAliases: true }]);
    expect(byId(t, second.created[0]!).alias).toBe('s0-3');
  });

  it('a_connector_to_outside_elements_is_not_copied_unless_links_are_kept', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'createConnector', id: id(950), from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 1), glue: 'dynamic' } }]);
    const e = await refused(t, [{ op: 'duplicate', targets: [id(950)] }]);
    expect(e.details?.reason).toBe('connector_has_external_ends');
    const r = await run(t, [{ op: 'duplicate', targets: [id(950)], keepExternalLinks: true }]);
    const copy = byId<ConnectorElement>(t, r.created[0]!);
    expect([copy.from.elementId, copy.to.elementId]).toEqual([s(t, 0), s(t, 1)]);
  });

  it('duplicating_a_group_copies_its_subtree', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: id(900) }]);
    const r = await run(t, [{ op: 'duplicate', targets: [id(900)], offset: { xPt: 0, yPt: 300 } }]);
    expect(r.created).toHaveLength(3);
    const g = r.created.map(c => byId(t, c)).find(c => c.kind === 'group')!;
    expect(g.kind === 'group' && g.childIds.every(c => r.created.includes(c))).toBe(true);
    expect(g.bounds).toEqual({ x: 100, y: 340, width: 220, height: 60 });
    expect(group(t, id(900)).childIds).toEqual([s(t, 0), s(t, 1)]);
    expect(els(t).filter(e => e.kind === 'group')).toHaveLength(2);
  });

  it('duplicate_ids_cannot_commit', async () => {
    const t = createTestEngine();
    const e = await refused(t, [{ op: 'createShape', id: s(t, 0), bounds: { x: 0, y: 0, width: 5, height: 5 } }]);
    expect(e.code).toBe('invalid_request');
    expect(has(t, s(t, 0))).toBe(true);
  });
});
