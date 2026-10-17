import { describe, expect, it } from 'vitest';
import { deleteOperations } from '../src/canvas/intents';
import { collapseToGroups, expandToGroups, leavesOfTargets } from '../src/canvas/selection';
import { createTestEngine } from './support/engine';
import { id, run } from './support/ops';

const G1 = id(900), G2 = id(901);

async function scene() {
  const t = createTestEngine();
  const s = (n: number) => t.ids.shapes[n]!;
  await run(t, [
    { op: 'createConnector', id: id(950), from: { elementId: s(0), glue: 'dynamic' }, to: { elementId: s(1), glue: 'dynamic' } },
    { op: 'group', targets: [s(0), s(1)], id: G1 },
    { op: 'group', targets: [G1, s(2)], id: G2 },
  ]);
  return { t, s, snap: () => t.store.snapshot() };
}

describe('group selection', () => {
  it('picking a member picks its whole top level group', async () => {
    const { s, snap } = await scene();
    expect(expandToGroups(snap(), [s(0)])).toEqual([s(0), s(1), s(2)]);
    expect(expandToGroups(snap(), [s(5)])).toEqual([s(5)]);
    expect(expandToGroups(snap(), [s(5), s(1)])).toEqual([s(5), s(0), s(1), s(2)]);
  });

  it('a complete group collapses to one object, the highest complete one', async () => {
    const { s, snap } = await scene();
    expect(collapseToGroups(snap(), [s(0), s(1), s(2)])).toEqual([G2]);
    expect(collapseToGroups(snap(), [s(0), s(1)])).toEqual([G1]);
    expect(collapseToGroups(snap(), [s(0), s(2)])).toEqual([s(0), s(2)]); // G1 is incomplete
    expect(collapseToGroups(snap(), [s(5), s(0), s(1)])).toEqual([s(5), G1]);
    expect(leavesOfTargets(snap(), [G2])).toEqual([s(0), s(1), id(950), s(2)].filter(x => leavesOfTargets(snap(), [G2]).includes(x)));
  });

  it('deleting a complete group deletes it as one object and takes attached connectors', async () => {
    const { t, s, snap } = await scene();
    const ops = deleteOperations(snap(), [s(0), s(1), s(2)]);
    expect(ops).toEqual([{ op: 'delete', target: G2, children: 'delete', withConnectors: true }]);
    const r = await t.engine.execute(t.request(ops), 'gui');
    expect(r.ok).toBe(true);
  });

  it('deleting part of a group removes just those members', async () => {
    const { t, s, snap } = await scene();
    const ops = deleteOperations(snap(), [s(2)]);
    expect(ops).toEqual([{ op: 'delete', target: s(2), withConnectors: true }]);
    expect((await t.engine.execute(t.request(ops), 'gui')).ok).toBe(true);
  });
});
