import { describe, expect, it } from 'vitest';
import { deriveGroupBounds } from '../src/commands/groups';
import { createTestEngine, exactElementHashes, hashesExcept } from './support/engine';
import { byId, group, has, id, refused, run } from './support/ops';

const G1 = id(900), G2 = id(901);
const s = (t: ReturnType<typeof createTestEngine>, n: number) => t.ids.shapes[n]!;

describe('groups', () => {
  it('group_bounds_are_the_tight_union_with_zero_rotation', async () => {
    const t = createTestEngine();
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1, alias: 'g' }]);
    expect(r.created).toEqual([G1]);
    expect(r.changed).toEqual([]); // members are not rewritten: membership lives on the group
    const g = group(t, G1);
    expect(g.bounds).toEqual({ x: 100, y: 40, width: 220, height: 60 });
    expect(g.rotationDeg).toBe(0);
    expect(g.childIds).toEqual([s(t, 0), s(t, 1)]);
    expect(hashesExcept(t.store.snapshot(), G1)).toEqual(exactElementHashes(before));
  });

  it('derived_bounds_use_rotated_extents_and_ignore_routes', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'rotate', target: s(t, 1), angleDeg: 90 },
      { op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 },
    ]);
    // s1 is 100x60 about (270,70); turned a quarter it covers x 240..300, y 20..120.
    expect(group(t, G1).bounds).toEqual({ x: 100, y: 20, width: 200, height: 100 });
    expect(deriveGroupBounds(t.store.snapshot().document, G1)).toEqual(group(t, G1).bounds);
  });

  it('descendant_edit_reports_ancestors', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }, { op: 'group', targets: [G1, s(t, 2)], id: G2 }]);
    const r = await run(t, [{ op: 'resize', target: s(t, 0), bounds: { x: 50 } }]);
    expect(r.changed).toEqual(expect.arrayContaining([s(t, 0), G1, G2]));
    expect(group(t, G1).bounds).toEqual({ x: 50, y: 40, width: 270, height: 60 });
    const inside = await run(t, [{ op: 'resize', target: s(t, 0), bounds: { width: 120 } }]);
    expect(inside.changed).toEqual([s(t, 0)]); // the union did not change, so no ancestor did
    const wide = await run(t, [{ op: 'resize', target: s(t, 2), bounds: { width: 300 } }]);
    expect(wide.changed).toEqual(expect.arrayContaining([s(t, 2), G2]));
    expect(wide.changed).not.toContain(G1); // G1 holds only s0 and s1
    expect(group(t, G2).bounds.width).toBe(590); // 50 .. 640
  });

  it('reject_empty_group', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    // removing one member shrinks the group and reports it
    const r = await run(t, [{ op: 'delete', target: s(t, 0) }]);
    expect(r.deleted).toEqual([s(t, 0)]);
    expect(r.changed).toEqual([G1]);
    expect(group(t, G1).childIds).toEqual([s(t, 1)]);
    // removing the last one without saying what happens to the group is refused
    const e = await refused(t, [{ op: 'delete', target: s(t, 1) }]);
    expect(e.code).toBe('invalid_request');
    expect(e.details?.reason).toBe('empty_group');
    // naming the outcome makes the same removal legal
    await run(t, [{ op: 'ungroup', target: G1 }, { op: 'delete', target: s(t, 1) }]);
    expect(has(t, G1)).toBe(false);
  });

  it('ancestor_and_descendant_selection_conflicts', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    const a = await refused(t, [{ op: 'group', targets: [G1, s(t, 0)] }]);
    expect(a.details?.reason).toBe('ancestor_and_descendant_selection');
    const b = await refused(t, [{ op: 'align', targets: [G1, s(t, 1)], edge: 'left' }]);
    expect(b.details?.reason).toBe('ancestor_and_descendant_selection');
    const c = await refused(t, [{ op: 'group', targets: [s(t, 1), s(t, 2)] }]);
    expect(c.details?.reason).toBe('already_grouped');
  });

  it('ungroup_keeps_members_identical_and_reattaches_them_to_the_parent', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }, { op: 'group', targets: [G1, s(t, 2)], id: G2 }]);
    const members = { a: byId(t, s(t, 0)), b: byId(t, s(t, 1)), c: byId(t, s(t, 2)) };
    const r = await run(t, [{ op: 'ungroup', target: G1 }]);
    expect(r.deleted).toEqual([G1]);
    expect(r.changed).toEqual([G2]);
    expect(group(t, G2).childIds).toEqual([s(t, 0), s(t, 1), s(t, 2)]);
    expect(byId(t, s(t, 0))).toBe(members.a);
    expect(byId(t, s(t, 1))).toBe(members.b);
    expect(byId(t, s(t, 2))).toBe(members.c);
  });

  it('grouping_undoes_to_the_exact_prior_state', async () => {
    const t = createTestEngine();
    const before = exactElementHashes(t.store.snapshot());
    const r = await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    const u = await t.engine.undo({ ...t.scope(), transactionId: id(7001), baseRevision: r.revision });
    expect(u.ok).toBe(true);
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
    const again = await t.engine.redo({ ...t.scope(), transactionId: id(7002), baseRevision: t.engine.revision });
    expect(again.ok).toBe(true);
    expect(group(t, G1).childIds).toHaveLength(2);
  });

  it('deleting_a_group_requires_an_explicit_subtree_choice', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    const e = await refused(t, [{ op: 'delete', target: G1 }]);
    expect(e.details?.reason).toBe('group_delete_requires_choice');
    const kept = await run(t, [{ op: 'delete', target: G1, children: 'ungroup' }]);
    expect(kept.deleted).toEqual([G1]);
    expect(has(t, s(t, 0))).toBe(true);
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    const gone = await run(t, [{ op: 'delete', target: G1, children: 'delete' }]);
    expect([...gone.deleted].sort()).toEqual([G1, s(t, 0), s(t, 1)].sort());
  });

  it('deleting_a_subtree_with_attached_connectors_needs_them_gone_too', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 },
      { op: 'createConnector', alias: 'c', from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 2), glue: 'dynamic' } },
    ]);
    const e = await refused(t, [{ op: 'delete', target: G1, children: 'delete' }]);
    expect(e.code).toBe('dependency_conflict');
    const r = await run(t, [{ op: 'delete', target: G1, children: 'delete', withConnectors: true }]);
    expect(r.deleted).toHaveLength(4);
  });

  it('a_locked_member_blocks_group_moves', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }, { op: 'set', target: s(t, 0), patch: { locked: true } }]);
    const e = await refused(t, [{ op: 'move', target: G1, delta: { xPt: 5, yPt: 0 } }]);
    expect(e.code).toBe('locked_target');
  });
});
