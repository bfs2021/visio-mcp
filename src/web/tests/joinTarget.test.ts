import { describe, expect, it } from 'vitest';
import { findJoinTarget } from '../src/canvas/joinTarget';

const bus = { id: 'bus', route: [{ x: 0, y: 100 }, { x: 200, y: 100 }] };
const other = { id: 'other', route: [{ x: 0, y: 130 }, { x: 200, y: 130 }] };

describe('where a dropped connector end joins', () => {
  it('finds the line it was dropped on, and how far along it', () => {
    expect(findJoinTarget([bus, other], 'tap', { x: 50, y: 103 }, 8)).toEqual({ connectorId: 'bus', position: 0.25 });
  });
  it('picks the nearer of two lines', () => {
    expect(findJoinTarget([bus, other], 'tap', { x: 50, y: 118 }, 20)!.connectorId).toBe('other');
  });
  it('does nothing when no line is close enough, or the only one close is itself', () => {
    expect(findJoinTarget([bus, other], 'tap', { x: 50, y: 115 }, 8)).toBeNull();
    expect(findJoinTarget([bus], 'bus', { x: 50, y: 100 }, 8)).toBeNull();
  });
  it('leaves the ends of a line to shapes: a drop at either end is not a join', () => {
    expect(findJoinTarget([bus], 'tap', { x: 1, y: 100 }, 8)).toBeNull();
    expect(findJoinTarget([bus], 'tap', { x: 199, y: 100 }, 8)).toBeNull();
  });
  it('skips a line that would make a loop', () => {
    expect(findJoinTarget([bus], 'tap', { x: 50, y: 100 }, 8, id => id === 'bus')).toBeNull();
  });
  it('follows a bent line to the right segment', () => {
    const bent = { id: 'bent', route: [{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }] };
    expect(findJoinTarget([bent], 'tap', { x: 102, y: 75 }, 8)).toEqual({ connectorId: 'bent', position: 0.875 });
  });
});
