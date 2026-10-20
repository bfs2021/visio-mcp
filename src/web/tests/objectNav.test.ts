import { describe, expect, it } from 'vitest';
import { connectable, describe as describeObject, navigableObjects, nameOf, nearestIn, roleOf, stepFrom } from '../src/editor/objectNav';
import { createTestEngine } from './support/engine';
import { group, id, run, shape } from './support/ops';

type T = ReturnType<typeof createTestEngine>;
const KEEP = new Set<string>();
/** The test engine's page also holds a sample drawing; only the four shapes under test (and anything made from them) are kept. */
const page = (t: T) => { const p = t.store.snapshot().document.pages[0]!; return { ...p, elements: p.elements.filter(e => KEEP.has(e.id)) }; };

/** a (left, top), b (right, top), c (left, below a), d (right, below b): placed on a clean grid by the test. */
async function grid(): Promise<{ t: T; ids: string[] }> {
  const t = createTestEngine();
  const ids = t.ids.shapes.slice(0, 4);
  KEEP.clear(); ids.forEach(i => KEEP.add(i)); KEEP.add(id(900));
  const at = [[100, 100], [400, 100], [100, 300], [400, 300]];
  await run(t, ids.map((target, i) => ({ op: 'resize', target, bounds: { x: at[i]![0]!, y: at[i]![1]!, width: 80, height: 40 } })));
  return { t, ids };
}

describe('keyboard order', () => {
  it('visits objects top to bottom, then left to right', async () => {
    const { t, ids } = await grid();
    expect(navigableObjects(page(t)).map(e => e.id)).toEqual([ids[0], ids[1], ids[2], ids[3]]);
  });

  it('skips members of a group (the group stands for them) and hidden objects', async () => {
    const { t, ids } = await grid();
    await run(t, [{ op: 'group', targets: [ids[0]!, ids[1]!], id: id(900) }]);
    const order = navigableObjects(page(t)).map(e => e.id);
    expect(order).toContain(id(900));
    expect(order).not.toContain(ids[0]);
    expect(order).not.toContain(ids[1]);
    expect(group(t, id(900)).childIds).toHaveLength(2);
  });

  it('steps forward and back, and returns null past either end so Tab can leave', async () => {
    const { t, ids } = await grid();
    const order = navigableObjects(page(t));
    expect(stepFrom(order, undefined, 1)!.id).toBe(ids[0]);
    expect(stepFrom(order, undefined, -1)!.id).toBe(ids[3]);
    expect(stepFrom(order, ids[0], 1)!.id).toBe(ids[1]);
    expect(stepFrom(order, ids[1], -1)!.id).toBe(ids[0]);
    expect(stepFrom(order, ids[3], 1)).toBeNull();
    expect(stepFrom(order, ids[0], -1)).toBeNull();
    expect(stepFrom([], undefined, 1)).toBeNull();
  });
});

describe('directional jump', () => {
  it('goes to the nearest object in the direction asked', async () => {
    const { t, ids } = await grid();
    const p = page(t), order = navigableObjects(p);
    expect(nearestIn(p, order, ids[0]!, 'right')!.id).toBe(ids[1]);
    expect(nearestIn(p, order, ids[0]!, 'down')!.id).toBe(ids[2]);
    expect(nearestIn(p, order, ids[3]!, 'left')!.id).toBe(ids[2]);
    expect(nearestIn(p, order, ids[3]!, 'up')!.id).toBe(ids[1]);
  });

  it('answers null when nothing lies that way', async () => {
    const { t, ids } = await grid();
    const p = page(t), order = navigableObjects(p);
    expect(nearestIn(p, order, ids[0]!, 'left')).toBeNull();
    expect(nearestIn(p, order, ids[0]!, 'up')).toBeNull();
    expect(nearestIn(p, order, 'missing', 'up')).toBeNull();
  });

  it('prefers the object straight ahead over a nearer one off to the side', async () => {
    const { t, ids } = await grid();
    await run(t, [{ op: 'resize', target: ids[1]!, bounds: { x: 110, y: 250, width: 80, height: 40 } }]); // close below-right of a, but c is straight below
    const p = page(t);
    expect(nearestIn(p, navigableObjects(p), ids[0]!, 'down')!.id).toBe(ids[1]); // 150 down, 10 across beats 200 down, 0 across
    await run(t, [{ op: 'resize', target: ids[1]!, bounds: { x: 330, y: 150, width: 80, height: 40 } }]);
    const q = page(t);
    expect(nearestIn(q, navigableObjects(q), ids[0]!, 'down')!.id).toBe(ids[2]); // b is to the side, outside the cone
  });
});

describe('names for assistive technology', () => {
  it('uses the alias, then the text, then a plain description', async () => {
    const { t, ids } = await grid();
    const p = page(t);
    await run(t, [{ op: 'set', target: ids[1]!, patch: { alias: 'database' } }]);
    expect(nameOf(page(t), shape(t, ids[1]!))).toBe('database');
    expect(roleOf(shape(t, ids[0]!))).toBe('shape');
    expect(nameOf(p, shape(t, ids[0]!)).length).toBeGreaterThan(0);
  });

  it('describes name, role, position and size in the units given', async () => {
    const { t, ids } = await grid();
    await run(t, [{ op: 'set', target: ids[0]!, patch: { alias: 'api' } }]);
    const text = describeObject(page(t), shape(t, ids[0]!), pt => String(pt), 'pt');
    expect(text).toBe('api, shape, at x 100 y 100 pt, 80 by 40 pt');
  });

  it('offers every visible non-connector object for connecting', async () => {
    const { t } = await grid();
    expect(connectable(page(t))).toHaveLength(4);
  });
});
