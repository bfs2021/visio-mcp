import { ConnectorShape, Point, type AbstractCanvas2D, type CellState } from '@maxgraph/core';

/** R16: style keys the adapter sets on a connector that hops. Sizes are in points; the shape scales them with the view. */
export interface HopStyle { hopSize?: number; hopOrder?: number; hopIgnore?: string }

const EPS = 1e-6;

interface Crossing { at: number; point: Point }

/** Where segment a-b is crossed by segment c-d, as a fraction along a-b; null if they do not cross (or are parallel). */
function crossAt(a: Point, b: Point, c: Point, d: Point): { t: number; u: number } | null {
  const r = { x: b.x - a.x, y: b.y - a.y }, s = { x: d.x - c.x, y: d.y - c.y };
  const den = r.x * s.y - r.y * s.x;
  if (Math.abs(den) < EPS) return null;
  const t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / den;
  const u = ((c.x - a.x) * r.y - (c.y - a.y) * r.x) / den;
  return t > 0 && t < 1 && u > 0 && u < 1 ? { t, u } : null;
}

/**
 * Where a route (in view pixels) crosses the other connectors, as arcs to draw: for each segment, the crossings that are clear of its corners
 * and of each other. Pure, so the same function serves the editor, the tests and anything that needs to know where the hops are.
 */
export function hopCrossings(route: readonly Point[], others: ReadonlyArray<readonly Point[]>, radius: number): Crossing[][] {
  return route.slice(1).map((end, i) => {
    const a = route[i]!, b = end, len = Math.hypot(b.x - a.x, b.y - a.y);
    if (len < radius * 4) return [];
    const found: Crossing[] = [];
    for (const other of others) {
      for (let j = 1; j < other.length; j++) {
        const hit = crossAt(a, b, other[j - 1]!, other[j]!);
        if (!hit) continue;
        const along = hit.t * len, otherLen = Math.hypot(other[j]!.x - other[j - 1]!.x, other[j]!.y - other[j - 1]!.y);
        if (along < radius * 1.5 || len - along < radius * 1.5) continue;                          // too near a corner or an end to fit a hop
        if (hit.u * otherLen < radius || (1 - hit.u) * otherLen < radius) continue;      // meets the other line at its end: a join or a corner, not a crossing
        found.push({ at: hit.t, point: new Point(a.x + (b.x - a.x) * hit.t, a.y + (b.y - a.y) * hit.t) });
      }
    }
    found.sort((p, q) => p.at - q.at);
    const spaced: Crossing[] = [];
    for (const f of found) if (!spaced.length || (f.at - spaced[spaced.length - 1]!.at) * len >= radius * 2.5) spaced.push(f);
    return spaced;
  });
}

/** A connector that draws a small arc where it crosses another connector. Which connectors it ignores and how big the arc is ride in its style. */
export class HopConnectorShape extends ConnectorShape {
  paintLine(c: AbstractCanvas2D, pts: Point[], _rounded: boolean): void {
    const state = (this as unknown as { state?: CellState }).state;
    const style = (this.style ?? {}) as HopStyle;
    const view = state?.view;
    const radius = (style.hopSize ?? 0) * (view?.scale ?? 1);
    const others: Point[][] = [];
    if (state && view && radius > 0) {
      const ignore = new Set((style.hopIgnore ?? '').split(',').filter(Boolean));
      for (const [cell, other] of view.getStates()) {
        if (cell === state.cell || !other.cell.isEdge() || !other.cell.isVisible()) continue;
        if (ignore.has(other.cell.getId() ?? '')) continue;
        const o = (other.style ?? {}) as HopStyle;
        if (o.hopSize !== undefined && (o.hopOrder ?? 0) > (style.hopOrder ?? 0)) continue;       // two hoppers at one crossing: the one on top hops, the other stays straight
        const route = (other.absolutePoints ?? []).filter((p): p is Point => !!p);
        if (route.length > 1) others.push(route);
      }
    }
    const hops = radius > 0 ? hopCrossings(pts, others, radius) : [];
    c.begin();
    c.moveTo(pts[0]!.x, pts[0]!.y);
    for (let i = 1; i < pts.length; i++) {
      const a = pts[i - 1]!, b = pts[i]!, len = Math.hypot(b.x - a.x, b.y - a.y) || 1;
      const t = { x: (b.x - a.x) / len, y: (b.y - a.y) / len }, n = { x: t.y, y: -t.x }; // n: to the left of travel on screen
      for (const h of hops[i - 1] ?? []) {
        const p0 = { x: h.point.x - t.x * radius, y: h.point.y - t.y * radius }, p3 = { x: h.point.x + t.x * radius, y: h.point.y + t.y * radius };
        const lift = radius * (4 / 3);
        c.lineTo(p0.x, p0.y);
        c.curveTo(p0.x + n.x * lift, p0.y + n.y * lift, p3.x + n.x * lift, p3.y + n.y * lift, p3.x, p3.y);
      }
      c.lineTo(b.x, b.y);
    }
    c.stroke();
  }
}
