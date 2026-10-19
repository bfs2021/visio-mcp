import type { Point } from '../contracts/generated';

/** Where along a drawn route a position falls, and which way the route runs there. */
export interface OnRoute { point: Point; tangent: Point }

function lengths(route: readonly Point[]): { segs: number[]; total: number } {
  const segs = route.slice(1).map((p, i) => Math.hypot(p.x - route[i]!.x, p.y - route[i]!.y));
  return { segs, total: segs.reduce((a, b) => a + b, 0) };
}

/** The unit step to the right of a direction of travel on screen (y grows downwards): east travel has south on its right. */
const rightOf = (t: Point): Point => ({ x: -t.y, y: t.x });

/**
 * The point `position` (0 to 1 of the route's length) along `route`, moved `offsetPt` to the right of the direction of travel.
 * A route with no length is a single point.
 */
export function pointOnRoute(route: readonly Point[], position: number, offsetPt: number): OnRoute | null {
  if (route.length === 0) return null;
  const { segs, total } = lengths(route);
  if (route.length === 1 || total === 0) return { point: { ...route[0]! }, tangent: { x: 1, y: 0 } };
  let remaining = Math.min(Math.max(position, 0), 1) * total;
  let i = 0;
  while (i < segs.length - 1 && remaining > segs[i]!) { remaining -= segs[i]!; i++; }
  const a = route[i]!, b = route[i + 1]!, len = segs[i]! || 1;
  const t = { x: (b.x - a.x) / len, y: (b.y - a.y) / len };
  const along = Math.min(remaining, len) / len;
  const n = rightOf(t);
  return { point: { x: a.x + (b.x - a.x) * along + n.x * offsetPt, y: a.y + (b.y - a.y) * along + n.y * offsetPt }, tangent: t };
}

/** The place on `route` closest to `p`: its position as a fraction of the length, and how far to the right of the route it is. */
export function nearestOnRoute(route: readonly Point[], p: Point): { position: number; offsetPt: number } | null {
  if (route.length === 0) return null;
  const { segs, total } = lengths(route);
  if (route.length === 1 || total === 0) return { position: 0, offsetPt: 0 };
  let best = { d: Infinity, at: 0, side: 0 }, before = 0;
  for (let i = 0; i < segs.length; i++) {
    const a = route[i]!, b = route[i + 1]!, len = segs[i]!;
    if (len > 0) {
      const t = { x: (b.x - a.x) / len, y: (b.y - a.y) / len };
      const s = Math.min(Math.max((p.x - a.x) * t.x + (p.y - a.y) * t.y, 0), len);
      const q = { x: a.x + t.x * s, y: a.y + t.y * s };
      const d = Math.hypot(p.x - q.x, p.y - q.y);
      if (d < best.d - 1e-9) {
        const n = rightOf(t);
        best = { d, at: before + s, side: (p.x - q.x) * n.x + (p.y - q.y) * n.y };
      }
    }
    before += len;
  }
  return { position: Math.round((best.at / total) * 10000) / 10000, offsetPt: Math.round(best.side * 100) / 100 };
}
