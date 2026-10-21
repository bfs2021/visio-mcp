import type { End, V } from './orthogonalRoute';

/**
 * Obstacle-aware orthogonal routing (R08b). Pure geometry in page points, y down.
 *
 * Policy:
 * - The route from orthogonalRoute.ts is kept when it is clear. Only a blocked route is searched again.
 * - Obstacles are inflated by CLEARANCE. The two end shapes are obstacles too, except for the stub that leaves or enters them.
 * - Explicit waypoints are never rerouted: the caller does not call this when the author supplied any.
 * - Routes are made one after another; a later route pays a penalty for running along an earlier one, and the search can use a
 *   track SPACING beside each obstacle edge, so parallel connectors fan out instead of overprinting.
 * - If no clear route exists, or the search budget runs out, the result is the unchanged base route with `blocked: true`. That
 *   outcome is deterministic for a given input, and the layout check already reports the crossing.
 */
export interface Rect { x: number; y: number; width: number; height: number }
export interface Segment { a: V; b: V }
export interface AvoidOptions { clearance?: number; spacing?: number; stub?: number; budget?: number }
export interface AvoidResult { route: V[]; blocked: boolean; rerouted: boolean }

export const CLEARANCE = 8;
export const SPACING = 6;
export const STUB = 12;
export const BUDGET = 80_000;
const TURN = 24;
const SHARED = 1; // extra cost per point of overlap with an earlier route
const EPS = 1e-6;

export const inflate = (r: Rect, d: number): Rect => ({ x: r.x - d, y: r.y - d, width: r.width + 2 * d, height: r.height + 2 * d });

/** Whether an axis-aligned segment passes through the open interior of a rectangle. */
function crosses(a: V, b: V, r: Rect): boolean {
  const x1 = Math.min(a.x, b.x), x2 = Math.max(a.x, b.x), y1 = Math.min(a.y, b.y), y2 = Math.max(a.y, b.y);
  return x2 > r.x + EPS && x1 < r.x + r.width - EPS && y2 > r.y + EPS && y1 < r.y + r.height - EPS
    && (Math.abs(a.x - b.x) < EPS ? a.x > r.x + EPS && a.x < r.x + r.width - EPS : a.y > r.y + EPS && a.y < r.y + r.height - EPS);
}

/** Total length of a route that runs along (collinear with and overlapping) earlier segments. */
function sharedLength(a: V, b: V, occupied: readonly Segment[]): number {
  let n = 0;
  const horizontal = Math.abs(a.y - b.y) < EPS;
  for (const s of occupied) {
    if (horizontal) {
      if (Math.abs(s.a.y - a.y) > 0.5 || Math.abs(s.b.y - a.y) > 0.5) continue;
      n += Math.max(0, Math.min(Math.max(a.x, b.x), Math.max(s.a.x, s.b.x)) - Math.max(Math.min(a.x, b.x), Math.min(s.a.x, s.b.x)));
    } else {
      if (Math.abs(s.a.x - a.x) > 0.5 || Math.abs(s.b.x - a.x) > 0.5) continue;
      n += Math.max(0, Math.min(Math.max(a.y, b.y), Math.max(s.a.y, s.b.y)) - Math.max(Math.min(a.y, b.y), Math.min(s.a.y, s.b.y)));
    }
  }
  return n;
}

/** The segments of a polyline, for recording as occupied once a connector is routed. */
export const segmentsOf = (route: readonly V[]): Segment[] => route.slice(1).map((p, i) => ({ a: route[i]!, b: p }));

export function isClear(route: readonly V[], others: readonly Rect[], ends: readonly Rect[], pad: number): boolean {
  const inflatedOthers = others.map(r => inflate(r, pad)), inflatedEnds = ends.map(r => inflate(r, pad));
  for (let i = 1; i < route.length; i++) {
    const a = route[i - 1]!, b = route[i]!;
    if (inflatedOthers.some(r => crosses(a, b, r))) return false;
    // the first and last segments touch their own shape; only the middle ones must stay off the end shapes
    if (i > 1 && i < route.length - 1 && inflatedEnds.some(r => crosses(a, b, r))) return false;
  }
  return true;
}

/** Indices of the segments of `route` that pass through an obstacle inflated by `pad`; the first and last may touch their own shapes. */
export function blockedSegments(route: readonly V[], others: readonly Rect[], ends: readonly Rect[], pad: number): number[] {
  const inflatedOthers = others.map(r => inflate(r, pad)), inflatedEnds = ends.map(r => inflate(r, pad));
  const out: number[] = [];
  for (let i = 1; i < route.length; i++) {
    const a = route[i - 1]!, b = route[i]!;
    if (inflatedOthers.some(r => crosses(a, b, r)) || (i > 1 && i < route.length - 1 && inflatedEnds.some(r => crosses(a, b, r)))) out.push(i - 1);
  }
  return out;
}

const DIRS: V[] = [{ x: 1, y: 0 }, { x: 0, y: 1 }, { x: -1, y: 0 }, { x: 0, y: -1 }];

class Heap {
  private h: Array<{ f: number; seq: number; id: number }> = [];
  private seq = 0;
  get size(): number { return this.h.length; }
  push(f: number, id: number): void {
    const n = { f, seq: this.seq++, id }; const h = this.h; h.push(n);
    let i = h.length - 1;
    while (i > 0) { const p = (i - 1) >> 1; if (this.less(h[i]!, h[p]!)) { [h[i], h[p]] = [h[p]!, h[i]!]; i = p; } else break; }
  }
  pop(): { f: number; id: number } {
    const h = this.h, top = h[0]!, last = h.pop()!;
    if (h.length) {
      h[0] = last; let i = 0;
      for (;;) {
        const l = 2 * i + 1, r = l + 1; let m = i;
        if (l < h.length && this.less(h[l]!, h[m]!)) m = l;
        if (r < h.length && this.less(h[r]!, h[m]!)) m = r;
        if (m === i) break;
        [h[i], h[m]] = [h[m]!, h[i]!]; i = m;
      }
    }
    return top;
  }
  private less(a: { f: number; seq: number }, b: { f: number; seq: number }): boolean { return a.f < b.f || (a.f === b.f && a.seq < b.seq); }
}

/** Searches the grid of obstacle-edge lines for the cheapest orthogonal path from `start` (leaving along `startDir`) to `goal`. */
export function search(start: V, startDir: V, goal: V, goalDir: V, blockers: readonly Rect[], occupied: readonly Segment[], spacing: number, budget: number): V[] | undefined {
  const xs = new Set<number>([start.x, goal.x]), ys = new Set<number>([start.y, goal.y]);
  for (const r of blockers) {
    for (const x of [r.x, r.x + r.width]) { xs.add(x); xs.add(x < r.x + r.width / 2 ? x - spacing : x + spacing); }
    for (const y of [r.y, r.y + r.height]) { ys.add(y); ys.add(y < r.y + r.height / 2 ? y - spacing : y + spacing); }
  }
  const lo = { x: Math.min(...xs) - 4 * spacing, y: Math.min(...ys) - 4 * spacing }, hi = { x: Math.max(...xs) + 4 * spacing, y: Math.max(...ys) + 4 * spacing };
  xs.add(lo.x); xs.add(hi.x); ys.add(lo.y); ys.add(hi.y);
  const X = [...xs].sort((a, b) => a - b), Y = [...ys].sort((a, b) => a - b);
  const nx = X.length, ny = Y.length;
  const ix = (v: number) => X.findIndex(x => Math.abs(x - v) < EPS), iy = (v: number) => Y.findIndex(y => Math.abs(y - v) < EPS);
  const sx = ix(start.x), sy = iy(start.y), gx = ix(goal.x), gy = iy(goal.y);
  const state = (x: number, y: number, d: number) => (y * nx + x) * 4 + d;
  const blockedNode = (x: number, y: number): boolean => blockers.some(r => X[x]! > r.x + EPS && X[x]! < r.x + r.width - EPS && Y[y]! > r.y + EPS && Y[y]! < r.y + r.height - EPS);
  const dist = new Float64Array(nx * ny * 4).fill(Infinity), prev = new Int32Array(nx * ny * 4).fill(-1);
  const d0 = DIRS.findIndex(d => d.x === startDir.x && d.y === startDir.y);
  const heap = new Heap();
  const h = (x: number, y: number) => Math.abs(X[x]! - goal.x) + Math.abs(Y[y]! - goal.y);
  dist[state(sx, sy, d0)] = 0; heap.push(h(sx, sy), state(sx, sy, d0));
  const arrive = DIRS.findIndex(d => d.x === -goalDir.x && d.y === -goalDir.y);
  let steps = 0, best = -1;
  while (heap.size) {
    const { id } = heap.pop();
    const d = id % 4, cell = (id - d) / 4, x = cell % nx, y = (cell - x) / nx;
    if (x === gx && y === gy) { best = id; break; }
    if (++steps > budget) return undefined;
    const g = dist[id]!;
    for (let nd = 0; nd < 4; nd++) {
      if (nd === (d + 2) % 4) continue; // no U-turn on the spot
      const tx = x + DIRS[nd]!.x, ty = y + DIRS[nd]!.y;
      if (tx < 0 || ty < 0 || tx >= nx || ty >= ny) continue;
      const a = { x: X[x]!, y: Y[y]! }, b = { x: X[tx]!, y: Y[ty]! };
      if (blockedNode(tx, ty) || blockers.some(r => crosses(a, b, r))) continue;
      const len = Math.abs(b.x - a.x) + Math.abs(b.y - a.y);
      const cost = g + len + (nd === d ? 0 : TURN) + SHARED * sharedLength(a, b, occupied) + (tx === gx && ty === gy && nd !== arrive ? TURN : 0);
      const ns = state(tx, ty, nd);
      if (cost < dist[ns]!) { dist[ns] = cost; prev[ns] = id; heap.push(cost + h(tx, ty), ns); }
    }
  }
  if (best < 0) return undefined;
  const pts: V[] = [];
  for (let s = best; s >= 0; s = prev[s]!) { const cell = (s - (s % 4)) / 4; pts.push({ x: X[cell % nx]!, y: Y[(cell - (cell % nx)) / nx]! }); }
  return pts.reverse();
}

/**
 * Keeps `base` when it is clear, otherwise looks for a clear orthogonal route between the same two ends.
 * `others` are the obstacles; `ends` are the two connected shapes (rotated bounds).
 */
export function avoidObstacles(a: End, b: End, base: readonly V[], others: readonly Rect[], ends: readonly Rect[], occupied: readonly Segment[], opts: AvoidOptions = {}): AvoidResult {
  const clearance = opts.clearance ?? CLEARANCE, spacing = opts.spacing ?? SPACING, stub = opts.stub ?? STUB;
  if (isClear(base, others, ends, 2)) {
    let overlaps = 0;
    for (let i = 1; i < base.length; i++) overlaps += sharedLength(base[i - 1]!, base[i]!, occupied);
    if (overlaps === 0) return { route: [...base], blocked: false, rerouted: false };
  }
  const A1 = { x: a.anchor.x + a.dir.x * stub, y: a.anchor.y + a.dir.y * stub }, B1 = { x: b.anchor.x + b.dir.x * stub, y: b.anchor.y + b.dir.y * stub };
  const blockers = [...others, ...ends].map(r => inflate(r, clearance));
  const path = search(A1, a.dir, B1, b.dir, blockers, occupied, spacing, opts.budget ?? BUDGET);
  if (!path) return { route: [...base], blocked: !isClear(base, others, ends, 2), rerouted: false };
  const out: V[] = [a.anchor, ...path, b.anchor];
  const tidy: V[] = [];
  for (const p of out) if (!tidy.length || Math.abs(tidy[tidy.length - 1]!.x - p.x) > EPS || Math.abs(tidy[tidy.length - 1]!.y - p.y) > EPS) tidy.push(p);
  for (let i = tidy.length - 2; i > 0; i--) {
    const p = tidy[i - 1]!, q = tidy[i]!, r = tidy[i + 1]!;
    if ((Math.abs(p.x - q.x) < EPS && Math.abs(q.x - r.x) < EPS) || (Math.abs(p.y - q.y) < EPS && Math.abs(q.y - r.y) < EPS)) tidy.splice(i, 1);
  }
  return { route: tidy, blocked: false, rerouted: true };
}
