import { describe, expect, it } from 'vitest';
import { snapRun } from '../src/layout/segmentSnap';

const cands = [{ value: 100, kind: 'grid' as const }, { value: 104, kind: 'track' as const }, { value: 200, kind: 'centre' as const }];

describe('run snapping (R21)', () => {
  it('snaps to the nearest candidate within reach and not beyond it', () => {
    expect(snapRun(102.5, cands, 8)).toEqual({ value: 104, kind: 'track' });
    expect(snapRun(150, cands, 8)).toEqual({ value: 150 });
  });

  it('holds a snap until the pointer is 1.5 times the reach away, so it does not oscillate at the threshold', () => {
    const held = snapRun(104, cands, 8);
    expect(snapRun(113, cands, 8, held)).toEqual(held); // 9 away: beyond reach but inside the release distance
    expect(snapRun(117, cands, 8, held)).toEqual({ value: 117 }); // 13 away: let go
    // and it does not grab again on the way back until it is inside reach
    expect(snapRun(113, cands, 8)).toEqual({ value: 113 });
  });
});
