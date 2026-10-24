import { describe, expect, it } from 'vitest';
import { planArrange } from '../src/commands/arrange';
import { createTestEngine, exactElementHashes, hashesExcept } from './support/engine';
import { byId, group, id, refused, run, shape } from './support/ops';

type T = ReturnType<typeof createTestEngine>;
const s = (t: T, n: number) => t.ids.shapes[n]!;
const G1 = id(900);

/** s0 100..200, s1 130..180 (narrow), s2 400..500, s3 900..1000, all at y 40 with height 60. */
async function row(): Promise<T> {
  const t = createTestEngine();
  await run(t, [
    { op: 'resize', target: s(t, 0), bounds: { x: 0 } },
    { op: 'resize', target: s(t, 1), bounds: { x: 130, width: 50 } },
    { op: 'resize', target: s(t, 2), bounds: { x: 400 } },
    { op: 'resize', target: s(t, 3), bounds: { x: 900, y: 40 } },
  ]);
  return t;
}

describe('align', () => {
  it('align_changes_only_selected_geometry', async () => {
    const t = createTestEngine();
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'align', targets: [s(t, 1), s(t, 2), s(t, 3)], edge: 'left' }]);
    // s1 already sits on the shared edge (220), so only the other two change
    expect(r.changed.sort()).toEqual([s(t, 2), s(t, 3)].sort());
    expect(shape(t, s(t, 2)).bounds).toEqual({ x: 220, y: 40, width: 100, height: 60 });
    expect(shape(t, s(t, 3)).bounds).toEqual({ x: 220, y: 40, width: 100, height: 60 });
    expect(hashesExcept(t.store.snapshot(), s(t, 2), s(t, 3))).toEqual(hashesExcept(before, s(t, 2), s(t, 3)));
  });

  it('every_edge_and_centre_line', async () => {
    const t = await row();
    await run(t, [{ op: 'align', targets: [s(t, 0), s(t, 1), s(t, 2)], edge: 'right' }]);
    expect([s(t, 0), s(t, 1), s(t, 2)].map(i => shape(t, i).bounds.x + shape(t, i).bounds.width)).toEqual([500, 500, 500]);
    await run(t, [{ op: 'align', targets: [s(t, 0), s(t, 1), s(t, 2)], edge: 'center' }]);
    // union of 450..500 and 400..500 and 400..500 is 400..500 after the right alignment, so its centre is 450
    expect([s(t, 0), s(t, 1), s(t, 2)].map(i => shape(t, i).bounds.x + shape(t, i).bounds.width / 2)).toEqual([450, 450, 450]);
    await run(t, [{ op: 'resize', target: s(t, 1), bounds: { y: 100 } }, { op: 'align', targets: [s(t, 0), s(t, 1)], edge: 'top' }]);
    expect(shape(t, s(t, 1)).bounds.y).toBe(40);
    await run(t, [{ op: 'align', targets: [s(t, 0), s(t, 5)], edge: 'bottom' }]);
    expect(shape(t, s(t, 0)).bounds.y).toBe(130);
    await run(t, [{ op: 'resize', target: s(t, 4), bounds: { y: 400, height: 20 } }, { op: 'align', targets: [s(t, 0), s(t, 4)], edge: 'middle' }]);
    const mid = (i: string) => shape(t, i).bounds.y + shape(t, i).bounds.height / 2;
    expect(mid(s(t, 0))).toBe(mid(s(t, 4)));
  });

  it('uses_the_visual_bounds_of_rotated_elements', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'rotate', target: s(t, 1), angleDeg: 90 }]);
    // s1 turned a quarter covers y 20..120, so top-aligning with s0 (y 40) lifts s0 to 20 and leaves s1 alone
    const r = await run(t, [{ op: 'align', targets: [s(t, 0), s(t, 1)], edge: 'top' }]);
    expect(r.changed).toEqual([s(t, 0)]);
    expect(shape(t, s(t, 0)).bounds.y).toBe(20);
  });

  it('a_group_is_one_target_and_moves_its_descendants', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
    const r = await run(t, [{ op: 'align', targets: [G1, s(t, 2)], edge: 'right' }]);
    // the group (100..320) moves right so its edge meets s2 (340..440): dx = 120
    expect(group(t, G1).bounds).toMatchObject({ x: 220, width: 220 });
    expect(shape(t, s(t, 0)).bounds.x).toBe(220);
    expect(shape(t, s(t, 1)).bounds.x).toBe(340);
    expect(r.changed).toEqual(expect.arrayContaining([G1, s(t, 0), s(t, 1)]));
    expect(r.changed).not.toContain(s(t, 2));
  });
});

describe('level changes', () => {
  it('setting_an_absent_level_to_zero_is_a_noop', async () => {
    const t = createTestEngine();
    const target = s(t, 0);
    const before = t.store.snapshot();
    const result = await run(t, [{ op: 'set', target, patch: { level: 0 } }]);
    expect(result.changed).toEqual([]);
    expect(result.revision).toBe(before.revision);
    expect(byId(t, target)).not.toHaveProperty('level');
    expect(exactElementHashes(t.store.snapshot())).toEqual(exactElementHashes(before));
  });

  it('create_and_set_store_only_nonzero_levels', async () => {
    const t = createTestEngine();
    const created = await run(t, [{ op: 'createShape', id: G1, level: 3, bounds: { x: 5, y: 5, width: 20, height: 20 } }]);
    expect(byId(t, G1)).toMatchObject({ level: 3 });
    await run(t, [{ op: 'set', target: G1, patch: { level: 0 } }]);
    expect(byId(t, G1)).not.toHaveProperty('level');
    expect(created.created).toEqual([G1]);
  });

  it('moving_to_a_new_level_uses_global_z_top_and_reorders_only_within_that_level', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'set', target: s(t, 0), patch: { level: 2 } },
      { op: 'set', target: s(t, 1), patch: { level: 2 } },
      { op: 'set', target: s(t, 2), patch: { level: 1 } },
    ]);
    const before = t.store.snapshot().document.pages[0]!.elements.filter(e => e.kind !== 'group');
    await run(t, [{ op: 'reorder', targets: [s(t, 0)], to: 'front' }]);
    const after = t.store.snapshot().document.pages[0]!.elements.filter(e => e.kind !== 'group');
    const picked = after.find(e => e.id === s(t, 0))!;
    expect(picked.zIndex).toBe(Math.max(...before.map(e => e.zIndex)) + 1);
    expect(after.map(e => e.zIndex).sort((a, b) => a - b)).toEqual([...new Set(after.map(e => e.zIndex))].sort((a, b) => a - b));
    expect(byId(t, s(t, 1)).level).toBe(2);
    expect(byId(t, s(t, 2)).zIndex).toBe(before.find(e => e.id === s(t, 2))!.zIndex);
  });
});

describe('distribute and gap', () => {
  it('distribute_preserves_ends', async () => {
    const t = await row();
    const first = shape(t, s(t, 0)), last = shape(t, s(t, 3));
    const r = await run(t, [{ op: 'distribute', targets: [s(t, 3), s(t, 1), s(t, 0), s(t, 2)], axis: 'horizontal' }]);
    expect(shape(t, s(t, 0))).toBe(first);
    expect(shape(t, s(t, 3))).toBe(last);
    expect(r.changed.sort()).toEqual([s(t, 1), s(t, 2)].sort());
    // widths 100+50+100+100 inside 0..1000 leave 650 for three equal gaps
    const gap = 650 / 3;
    expect(shape(t, s(t, 1)).bounds.x).toBeCloseTo(100 + gap, 9);
    expect(shape(t, s(t, 2)).bounds.x).toBeCloseTo(100 + gap + 50 + gap, 9);
  });

  it('vertical_distribution_and_the_three_target_minimum', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'distribute', targets: [s(t, 0), s(t, 5), s(t, 10)], axis: 'vertical' }]);
    expect(shape(t, s(t, 5)).bounds.y).toBe(130); // already evenly spaced: 40, 130, 220
    const e = await refused(t, [{ op: 'distribute', targets: [s(t, 0), s(t, 5)], axis: 'vertical' }]);
    expect(e.code).toBe('invalid_request');
  });

  it('gap_preserves_first', async () => {
    const t = await row();
    const first = shape(t, s(t, 0));
    await run(t, [{ op: 'setGap', targets: [s(t, 2), s(t, 0), s(t, 1)], axis: 'horizontal', gapPt: 20 }]);
    expect(shape(t, s(t, 0))).toBe(first);
    expect(shape(t, s(t, 1)).bounds.x).toBe(120); // 0 + 100 + 20
    expect(shape(t, s(t, 2)).bounds.x).toBe(190); // 120 + 50 + 20
  });

  it('equal_positions_keep_input_order', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'setGap', targets: [s(t, 1), s(t, 6)], axis: 'vertical', gapPt: 10 }]); // s1 and s6 share x but differ in y
    expect(shape(t, s(t, 1)).bounds.y).toBe(40);
    expect(shape(t, s(t, 6)).bounds.y).toBe(110);
    const tie = createTestEngine();
    await run(tie, [{ op: 'resize', target: s(tie, 6), bounds: { y: 40 } }]); // now s1 and s6 tie on y
    await run(tie, [{ op: 'setGap', targets: [s(tie, 6), s(tie, 1)], axis: 'vertical', gapPt: 10 }]);
    expect(shape(tie, s(tie, 6)).bounds.y).toBe(40); // first in input order stays
    expect(shape(tie, s(tie, 1)).bounds.y).toBe(110);
  });

  it('planArrange_returns_plain_moves_with_resolved_ids', () => {
    const t = createTestEngine();
    const r = planArrange(t.store.snapshot(), { op: 'align', targets: ['s1', 's2'], edge: 'left' });
    expect(r.ok && r.value).toEqual([{ op: 'move', target: s(t, 2), delta: { xPt: -120, yPt: 0 } }]);
  });
});

describe('arrange refusals', () => {
  it('mixed_page_rejects', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: id(800), name: 'Second' }]);
    await run(t, [{ op: 'createShape', id: id(801), pageId: id(800), bounds: { x: 10, y: 10, width: 40, height: 40 } }]);
    const e = await refused(t, [{ op: 'align', targets: [s(t, 0), id(801)], edge: 'left' }]);
    expect(e.code).toBe('invalid_request');
    expect(e.details?.reason).toBe('mixed_pages');
  });

  it('locked_targets_and_endpoint_driven_connectors_are_refused', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'set', target: s(t, 2), patch: { locked: true } },
      { op: 'createConnector', id: id(950), from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 1), glue: 'dynamic' } },
    ]);
    expect((await refused(t, [{ op: 'align', targets: [s(t, 1), s(t, 2)], edge: 'left' }])).code).toBe('locked_target');
    expect((await refused(t, [{ op: 'align', targets: [s(t, 1), id(950)], edge: 'left' }])).code).toBe('invalid_request');
  });
});

describe('z order', () => {
  const z = (t: T, i: string) => byId(t, i).zIndex;

  it('reorder_reports_intervening_ids', async () => {
    const t = createTestEngine();
    // the seed stacks image (1), s0 (2), s1 (3), ...
    const up = await run(t, [{ op: 'reorder', targets: [s(t, 0)], to: 'forward' }]);
    expect(up.changed.sort()).toEqual([s(t, 0), s(t, 1)].sort());
    expect([z(t, s(t, 0)), z(t, s(t, 1))]).toEqual([3, 2]);
    const down = await run(t, [{ op: 'reorder', targets: [s(t, 2)], to: 'backward' }]);
    expect(down.changed.sort()).toEqual([s(t, 0), s(t, 2)].sort()); // s0 now sits at 3, directly under s2
  });

  it('front_and_back_move_only_the_targets', async () => {
    const t = createTestEngine();
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'reorder', targets: [s(t, 3), s(t, 1)], to: 'front' }]);
    expect(r.changed.sort()).toEqual([s(t, 1), s(t, 3)].sort());
    expect(z(t, s(t, 1))).toBeLessThan(z(t, s(t, 3))); // relative order of the targets is kept
    expect(z(t, s(t, 1))).toBeGreaterThan(20);
    expect(hashesExcept(t.store.snapshot(), s(t, 1), s(t, 3))).toEqual(hashesExcept(before, s(t, 1), s(t, 3)));
    const back = await run(t, [{ op: 'reorder', targets: [s(t, 5)], to: 'back' }]);
    expect(back.changed).toEqual([s(t, 5)]);
    expect(z(t, s(t, 5))).toBeLessThan(z(t, t.ids.image));
  });

  it('already_at_the_front_is_a_no_op', async () => {
    const t = createTestEngine();
    const r = await run(t, [{ op: 'reorder', targets: [s(t, 18)], to: 'front' }]);
    expect(r.noChange).toBe(true);
  });

  it('a_group_moves_as_a_block_and_keeps_its_internal_order', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 2)], id: G1 }]);
    await run(t, [{ op: 'reorder', targets: [G1], to: 'front' }]);
    expect(z(t, s(t, 0))).toBeLessThan(z(t, s(t, 2)));
    expect(z(t, s(t, 2))).toBeGreaterThan(z(t, s(t, 18)));
    expect(z(t, G1)).toBe(Math.max(z(t, s(t, 0)), z(t, s(t, 2))));
  });

  it('undo_restores_every_value', async () => {
    const t = createTestEngine();
    const before = exactElementHashes(t.store.snapshot());
    const r = await run(t, [{ op: 'reorder', targets: [s(t, 0)], to: 'forward' }]);
    await t.engine.undo({ ...t.scope(), transactionId: id(7001), baseRevision: r.revision });
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
  });
});
