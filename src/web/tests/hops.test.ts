import { describe, expect, it } from 'vitest';
import { Point } from '@maxgraph/core';
import { compareHopOrder, hopCrossings } from '../src/canvas/hopShape';

const P = (x: number, y: number) => new Point(x, y);

describe('where a connector hops', () => {
  const horizontal = [P(0, 100), P(200, 100)];

  it('finds a plain crossing at its place along the segment', () => {
    const [segment] = hopCrossings(horizontal, [[P(80, 0), P(80, 200)]], 4);
    expect(segment).toHaveLength(1);
    expect(segment![0]!.point.x).toBeCloseTo(80);
    expect(segment![0]!.at).toBeCloseTo(0.4);
  });

  it('puts several along one segment in order, and drops one too close to the last to fit', () => {
    const [segment] = hopCrossings(horizontal, [[P(150, 0), P(150, 200)], [P(50, 0), P(50, 200)], [P(54, 0), P(54, 200)]], 4);
    expect(segment!.map(h => Math.round(h.point.x))).toEqual([50, 150]);
  });

  it('does not hop at a corner or at the end of either line (a join or a bend is not a crossing)', () => {
    expect(hopCrossings(horizontal, [[P(3, 0), P(3, 200)]], 4)[0]).toEqual([]);        // right at this line's end
    expect(hopCrossings(horizontal, [[P(100, 100), P(100, 200)]], 4)[0]).toEqual([]);  // the other line starts on this one: a T
    expect(hopCrossings(horizontal, [[P(100, 98), P(100, 200)]], 4)[0]).toEqual([]);
  });

  it('ignores parallel lines and lines that miss', () => {
    expect(hopCrossings(horizontal, [[P(0, 110), P(200, 110)], [P(80, 0), P(80, 50)]], 4)[0]).toEqual([]);
  });

  it('works segment by segment on a bent route', () => {
    const bent = [P(0, 0), P(100, 0), P(100, 100)];
    const out = hopCrossings(bent, [[P(50, -50), P(50, 50)], [P(50, 60), P(150, 60)]], 4);
    expect(out[0]).toHaveLength(1);
    expect(out[1]).toHaveLength(1);
    expect(out[1]![0]!.point.y).toBeCloseTo(60);
  });
});

describe('which connector owns a hop', () => {
  it('compares_level_before_z_index_and_keeps_the_values_separate', () => {
    expect(compareHopOrder({ hopLevel: 2, hopOrder: 1 }, { hopLevel: 1, hopOrder: Number.MAX_SAFE_INTEGER })).toBeGreaterThan(0);
    expect(compareHopOrder({ hopLevel: 2, hopOrder: 5 }, { hopLevel: 2, hopOrder: 6 })).toBeLessThan(0);
    expect(compareHopOrder({ hopLevel: 2, hopOrder: Number.MAX_SAFE_INTEGER }, { hopLevel: 2, hopOrder: Number.MAX_SAFE_INTEGER - 1 })).toBeGreaterThan(0);
  });
});
