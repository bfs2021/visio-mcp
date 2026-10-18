import { describe, expect, it } from 'vitest';
import { endFor, routeOrthogonal, type Box, type End, type Terminal } from '../src/layout/orthogonalRoute';

const box = (x: number, y: number, width = 100, height = 60): Terminal => ({ box: { x, y, width, height } as Box, rotationDeg: 0 });
const staticEnd = (t: Terminal, port: string, towards = { x: 0, y: 0 }) => endFor({ elementId: 'x', port, glue: 'static' }, t, towards);
const axisAligned = (r: Array<{ x: number; y: number }>) => r.every((p, i) => i === 0 || Math.abs(p.x - r[i - 1]!.x) < 1e-6 || Math.abs(p.y - r[i - 1]!.y) < 1e-6);
const leaves = (r: Array<{ x: number; y: number }>, e: End) => (r[1]!.x - r[0]!.x) * e.dir.x + (r[1]!.y - r[0]!.y) * e.dir.y > 0;
const arrives = (r: Array<{ x: number; y: number }>, e: End) => { const n = r.length; return (r[n - 2]!.x - r[n - 1]!.x) * e.dir.x + (r[n - 2]!.y - r[n - 1]!.y) * e.dir.y > 0; };

describe('orthogonal routing', () => {
  it('goes straight between aligned facing ports', () => {
    const a = staticEnd(box(0, 100), 'east'), b = staticEnd(box(300, 100), 'west');
    expect(routeOrthogonal(a, b, [])).toEqual([{ x: 100, y: 130 }, { x: 300, y: 130 }]);
  });

  it('uses a Z with the middle bend between offset facing ports', () => {
    const a = staticEnd(box(0, 100), 'east'), b = staticEnd(box(300, 200), 'west');
    const r = routeOrthogonal(a, b, []);
    expect(r).toEqual([{ x: 100, y: 130 }, { x: 200, y: 130 }, { x: 200, y: 230 }, { x: 300, y: 230 }]);
  });

  it('uses one elbow from an east port into a north port and arrives going down', () => {
    const a = staticEnd(box(0, 100), 'east'), b = staticEnd(box(300, 250), 'north');
    const r = routeOrthogonal(a, b, []);
    expect(r).toEqual([{ x: 100, y: 130 }, { x: 350, y: 130 }, { x: 350, y: 250 }]);
    expect(leaves(r, a) && arrives(r, b)).toBe(true);
  });

  it('goes round when both ends leave the same way', () => {
    const a = staticEnd(box(0, 100), 'east'), b = staticEnd(box(40, 250), 'east');
    const r = routeOrthogonal(a, b, []);
    expect(axisAligned(r)).toBe(true);
    expect(Math.max(...r.map(p => p.x))).toBeGreaterThan(140); // beyond both shapes
    expect(leaves(r, a) && arrives(r, b)).toBe(true);
  });

  it('enters a port from the right side even when the target lies behind the source', () => {
    const a = staticEnd(box(300, 100), 'east'), b = staticEnd(box(0, 250), 'west');
    const r = routeOrthogonal(a, b, []);
    expect(axisAligned(r)).toBe(true);
    expect(leaves(r, a) && arrives(r, b)).toBe(true);
  });

  it('passes through every waypoint in order, with elbows between them', () => {
    const a = staticEnd(box(0, 100), 'west'), b = staticEnd(box(400, 400), 'north');
    const wp = [{ x: -40, y: 130 }, { x: -40, y: 300 }, { x: 450, y: 300 }];
    const r = routeOrthogonal(a, b, wp);
    expect(axisAligned(r)).toBe(true);
    let at = 0;
    for (const p of r) if (p.x === wp[at]?.x && p.y === wp[at]?.y) at++;
    expect(at).toBe(3);
    expect(leaves(r, a) && arrives(r, b)).toBe(true);
  });

  it('adds elbows to a waypoint that is not on the axis', () => {
    const a = staticEnd(box(0, 100), 'east'), b = staticEnd(box(300, 100), 'west');
    const r = routeOrthogonal(a, b, [{ x: 200, y: 20 }]);
    expect(axisAligned(r)).toBe(true);
    expect(r.some(p => p.x === 200 && p.y === 20)).toBe(true);
  });

  it('turns the end axis with the shape rotation', () => {
    const t: Terminal = { box: { x: 0, y: 0, width: 100, height: 60 }, rotationDeg: 90 };
    expect(staticEnd(t, 'east').dir).toEqual({ x: 0, y: 1 });
    expect(staticEnd(t, 'north').dir).toEqual({ x: 1, y: 0 });
  });

  it('a dynamic end faces the other end and floats', () => {
    const e = endFor({ elementId: 'x', glue: 'dynamic' }, box(0, 100), { x: 500, y: 130 });
    expect(e).toMatchObject({ dir: { x: 1, y: 0 }, floating: true, anchor: { x: 100, y: 130 } });
    const below = endFor({ elementId: 'x', glue: 'dynamic' }, box(0, 100), { x: 50, y: 500 });
    expect(below.dir).toEqual({ x: 0, y: 1 });
  });

  it('never produces a diagonal segment', () => {
    const sides = ['north', 'south', 'east', 'west'];
    for (const pa of sides) for (const pb of sides) for (const [bx, by] of [[300, 200], [-300, 200], [300, -200], [20, 200], [0, 100], [300, 100]] as const) {
      const r = routeOrthogonal(staticEnd(box(0, 100), pa), staticEnd(box(bx, by), pb), []);
      expect(axisAligned(r), `${pa}->${pb} to ${bx},${by}`).toBe(true);
    }
  });
});
