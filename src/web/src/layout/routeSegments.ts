import type { V as Point } from './orthogonalRoute';

/** One straight run of an orthogonal route. `index` is the run's position: it joins points[index] to points[index + 1]. */
export interface RouteSegment {
  index: number;
  axis: 'h' | 'v';
  /** Position on the perpendicular axis: y for a horizontal run, x for a vertical one. */
  coord: number;
  from: Point;
  to: Point;
  length: number;
  /** False for the runs attached to a port and for a route too short to have an inner run. */
  movable: boolean;
  /** Why a run cannot be dragged, in words for the person using it. */
  fixedReason?: string;
}

const EPS = 0.01;

export function segmentsOf(points: readonly Point[]): RouteSegment[] {
  const out: RouteSegment[] = [];
  for (let i = 0; i + 1 < points.length; i++) {
    const a = points[i]!, b = points[i + 1]!;
    const horizontal = Math.abs(a.y - b.y) <= EPS, vertical = Math.abs(a.x - b.x) <= EPS;
    if (!horizontal && !vertical) continue; // a diagonal run is not part of an orthogonal route
    const axis = horizontal ? 'h' : 'v';
    const inner = i > 0 && i + 2 < points.length;
    out.push({
      index: i, axis, coord: axis === 'h' ? a.y : a.x, from: a, to: b, length: Math.abs(axis === 'h' ? b.x - a.x : b.y - a.y),
      movable: inner,
      ...(inner ? {} : { fixedReason: points.length <= 2 ? 'a straight connector has no inner run to move: move a shape, or add a bend first' : 'this run leaves a port: change the side or move the shape instead' }),
    });
  }
  return out;
}

/** Removes repeated points and points that lie on a straight line between their neighbours. Both ends always stay. */
export function simplifyRoute(points: readonly Point[]): Point[] {
  const out: Point[] = [];
  for (const p of points) {
    const last = out[out.length - 1];
    if (last && Math.abs(last.x - p.x) <= EPS && Math.abs(last.y - p.y) <= EPS) continue;
    out.push({ x: p.x, y: p.y });
  }
  const keep: Point[] = [];
  for (let i = 0; i < out.length; i++) {
    const p = out[i]!;
    if (i === 0 || i === out.length - 1) { keep.push(p); continue; }
    const a = keep[keep.length - 1]!, b = out[i + 1]!;
    const flatX = Math.abs(a.x - p.x) <= EPS && Math.abs(p.x - b.x) <= EPS;
    const flatY = Math.abs(a.y - p.y) <= EPS && Math.abs(p.y - b.y) <= EPS;
    if (!flatX && !flatY) keep.push(p);
  }
  return keep;
}

export type MoveResult = { ok: true; points: Point[] } | { ok: false; code: 'segment_fixed' | 'segment_missing' | 'no_movement'; message: string };

/**
 * Moves one inner run to `target` on its perpendicular axis. The neighbouring runs lengthen or shorten; remote runs and both ends
 * stay put. A run dragged level with a parallel neighbour merges into it, leaving no redundant bends.
 */
export function moveSegment(points: readonly Point[], index: number, target: number): MoveResult {
  const seg = segmentsOf(points).find(s => s.index === index);
  if (!seg) return { ok: false, code: 'segment_missing', message: `the route has no run ${index}` };
  if (!seg.movable) return { ok: false, code: 'segment_fixed', message: seg.fixedReason! };
  if (Math.abs(target - seg.coord) <= EPS) return { ok: false, code: 'no_movement', message: 'the run is already there' };
  const next = points.map(p => ({ x: p.x, y: p.y }));
  for (const i of [index, index + 1]) {
    if (seg.axis === 'h') next[i]!.y = target;
    else next[i]!.x = target;
  }
  return { ok: true, points: simplifyRoute(next) };
}
