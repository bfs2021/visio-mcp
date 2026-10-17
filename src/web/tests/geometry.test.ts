import { describe, expect, it } from 'vitest';
import type { ConnectorElement, Operation } from '../src/contracts/generated';
import { canonicalSerialize } from '../src/model/canonical';
import { createTestEngine, exactElementHashes, hashesExcept } from './support/engine';
import { byId, group, id, refused, run, shape } from './support/ops';

const G1 = id(900);
const s = (t: ReturnType<typeof createTestEngine>, n: number) => t.ids.shapes[n]!;

async function grouped() {
  const t = createTestEngine();
  await run(t, [{ op: 'group', targets: [s(t, 0), s(t, 1)], id: G1 }]);
  return t;
}

describe('group geometry', () => {
  it('moving_a_group_translates_members_free_points_and_waypoints_but_not_glue', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'createConnector', id: id(950), from: { glue: 'none', point: { x: 10, y: 10 } }, to: { elementId: s(t, 0), glue: 'dynamic' }, waypoints: [{ x: 50, y: 10 }] },
      { op: 'group', targets: [s(t, 0), s(t, 1), id(950)], id: G1 },
    ]);
    const r = await run(t, [{ op: 'move', target: G1, delta: { xPt: 10, yPt: 20 } }]);
    expect(r.changed).toEqual(expect.arrayContaining([s(t, 0), s(t, 1), id(950), G1]));
    expect(shape(t, s(t, 0)).bounds).toMatchObject({ x: 110, y: 60 });
    const c = byId<ConnectorElement>(t, id(950));
    expect(c.from.point).toEqual({ x: 20, y: 30 });
    expect(c.waypoints).toEqual([{ x: 60, y: 30 }]);
    expect(c.to.elementId).toBe(s(t, 0)); // glue is a reference, not a position
  });

  it('fixed_pivot_rotate_and_inverse', async () => {
    const t = await grouped();
    const before = exactElementHashes(t.store.snapshot());
    const turn = await run(t, [{ op: 'rotate', target: G1, deltaDeg: 90, pivot: { xPt: 0, yPt: 0 } }]);
    // centre (150,70) turns about the origin to (-70,150); (270,70) to (-70,270)
    expect(shape(t, s(t, 0)).bounds).toEqual({ x: -120, y: 120, width: 100, height: 60 });
    expect(shape(t, s(t, 0)).rotationDeg).toBe(90);
    expect(group(t, G1).bounds).toEqual({ x: -100, y: 100, width: 60, height: 220 });
    expect(turn.changed).toEqual(expect.arrayContaining([s(t, 0), s(t, 1), G1]));
    await run(t, [{ op: 'rotate', target: G1, deltaDeg: -90, pivot: { xPt: 0, yPt: 0 } }]);
    expect(canonicalSerialize(shape(t, s(t, 0)))).toBe(before[s(t, 0)]);
    expect(group(t, G1).bounds).toEqual({ x: 100, y: 40, width: 220, height: 60 });
    // and the stored inverse of the second turn restores the first
    const un = await t.engine.undo({ ...t.scope(), transactionId: id(7001), baseRevision: t.engine.revision });
    expect(un.ok).toBe(true);
    expect(group(t, G1).bounds).toEqual({ x: -100, y: 100, width: 60, height: 220 });
  });

  it('rotationDeg_zero', async () => {
    const t = await grouped();
    await run(t, [{ op: 'rotate', target: G1, deltaDeg: 33 }]);
    expect(group(t, G1).rotationDeg).toBe(0);
    expect(shape(t, s(t, 0)).rotationDeg).toBe(33);
    expect(shape(t, s(t, 1)).rotationDeg).toBe(33);
  });

  it('reject_group_angleDeg', async () => {
    const t = await grouped();
    const e = await refused(t, [{ op: 'rotate', target: G1, angleDeg: 45 }]);
    expect(e.code).toBe('invalid_request');
    expect(e.details?.reason).toBe('group_rotation_requires_delta');
    const p = await refused(t, [{ op: 'rotate', target: s(t, 2), deltaDeg: 10, pivot: { xPt: 1, yPt: 1 } }]);
    expect(p.code).toBe('invalid_request');
  });

  it('external_reroute_keeps_group_hash', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'createConnector', id: id(950), from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 7), glue: 'dynamic' } },
      { op: 'group', targets: [s(t, 0), s(t, 1), id(950)], id: G1 },
    ]);
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'move', target: s(t, 7), delta: { xPt: 200, yPt: 150 } }]);
    expect(r.changed).toEqual([s(t, 7)]);
    expect(r.visuallyAffectedIds).toContain(id(950)); // the route changes on screen...
    const after = exactElementHashes(t.store.snapshot());
    expect(after[G1]).toBe(exactElementHashes(before)[G1]); // ...but nothing canonical about the group does
    expect(after[id(950)]).toBe(exactElementHashes(before)[id(950)]);
  });

  it('uniform_scale_scales_geometry_not_fonts_or_strokes', async () => {
    const t = await grouped();
    await run(t, [{ op: 'rotate', target: s(t, 1), angleDeg: 30 }]);
    const before = shape(t, s(t, 0));
    const gb = group(t, G1).bounds;
    await run(t, [{ op: 'resize', target: G1, bounds: { width: gb.width * 2, height: gb.height * 2 } }]);
    const after = shape(t, s(t, 0));
    expect(after.bounds.width).toBeCloseTo(before.bounds.width * 2, 9);
    expect(after.bounds.height).toBeCloseTo(before.bounds.height * 2, 9);
    expect(after.text!.fontSizePt).toBe(before.text!.fontSizePt);
    expect(after.style.strokeWidthPt).toBe(before.style.strokeWidthPt);
    expect(shape(t, s(t, 1)).rotationDeg).toBe(30);
    expect(group(t, G1).bounds.width).toBeCloseTo(gb.width * 2, 6);
  });

  it('reject_nonuniform_scale_rotated_leaf', async () => {
    const t = await grouped();
    await run(t, [{ op: 'rotate', target: s(t, 0), angleDeg: 30 }]);
    const gb = group(t, G1).bounds;
    const e = await refused(t, [{ op: 'resize', target: G1, bounds: { width: gb.width * 2 } }]);
    expect(e.code).toBe('invalid_request');
    expect(e.details?.reason).toBe('nonuniform_group_scale');
  });

  it('nonuniform_scale_swaps_axes_for_quarter_turned_members', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'rotate', target: s(t, 0), angleDeg: 90 }, { op: 'group', targets: [s(t, 0)], id: G1 }]);
    expect(group(t, G1).bounds).toEqual({ x: 120, y: 20, width: 60, height: 100 });
    await run(t, [{ op: 'resize', target: G1, bounds: { width: 120 } }]);
    // horizontal factor 2 acts on the member's own height because its axes are swapped
    expect(shape(t, s(t, 0)).bounds).toEqual({ x: 130, y: 10, width: 100, height: 120 });
    expect(group(t, G1).bounds).toEqual({ x: 120, y: 20, width: 120, height: 100 });
  });

  it('a_locked_member_blocks_group_geometry', async () => {
    const t = await grouped();
    await run(t, [{ op: 'set', target: s(t, 1), patch: { locked: true } }]);
    const attempts: Operation[] = [
      { op: 'move', target: G1, delta: { xPt: 1, yPt: 1 } },
      { op: 'rotate', target: G1, deltaDeg: 5 },
      { op: 'resize', target: G1, bounds: { width: 400, height: 120 } },
    ];
    for (const op of attempts) expect((await refused(t, [op])).code).toBe('locked_target');
  });

  it('unrelated_objects_keep_their_exact_hashes_through_group_transforms', async () => {
    const t = await grouped();
    const before = t.store.snapshot();
    await run(t, [{ op: 'move', target: G1, delta: { xPt: 3, yPt: 4 } }, { op: 'rotate', target: G1, deltaDeg: 15 }]);
    expect(hashesExcept(t.store.snapshot(), s(t, 0), s(t, 1), G1)).toEqual(hashesExcept(before, s(t, 0), s(t, 1), G1));
  });
});
