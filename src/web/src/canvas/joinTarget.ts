import type { Point } from '../contracts/generated';
import { nearestOnRoute, pointOnRoute } from '../commands/pathAnchor';

export interface DrawnRoute { id: string; route: readonly Point[] }

/**
 * The connector, and the place along it, that a dropped connector end should join: the drawn route nearest to `at`, if it comes within `tolerancePt`.
 * Near either end of a route does not count: that is where a connector meets a shape, not where a branch joins a line. `loops(id)` says a
 * connector cannot be joined because it already joins (directly or through others) the one being edited.
 */
export function findJoinTarget(routes: readonly DrawnRoute[], selfId: string, at: Point, tolerancePt: number, loops: (id: string) => boolean = () => false): { connectorId: string; position: number } | null {
  let best: { connectorId: string; position: number; d: number } | null = null;
  for (const { id, route } of routes) {
    if (id === selfId || route.length < 2 || loops(id)) continue;
    const near = nearestOnRoute(route, at);
    if (!near || near.position <= 0.02 || near.position >= 0.98) continue;
    const on = pointOnRoute(route, near.position, 0);
    if (!on) continue;
    const d = Math.hypot(on.point.x - at.x, on.point.y - at.y);
    if (d <= tolerancePt && (!best || d < best.d)) best = { connectorId: id, position: near.position, d };
  }
  return best && { connectorId: best.connectorId, position: best.position };
}
