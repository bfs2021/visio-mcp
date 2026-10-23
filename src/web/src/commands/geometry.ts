import type { Bounds, ConnectorElement, Element, Endpoint, Point } from '../contracts/generated';

/** Trigonometry for right-angle rotations is exact, so axis-aligned geometry never picks up rounding noise. */
function sinCos(deg: number): { sin: number; cos: number } {
  const d = ((deg % 360) + 360) % 360;
  if (d === 0) return { sin: 0, cos: 1 };
  if (d === 90) return { sin: 1, cos: 0 };
  if (d === 180) return { sin: 0, cos: -1 };
  if (d === 270) return { sin: -1, cos: 0 };
  const r = (d * Math.PI) / 180;
  return { sin: Math.sin(r), cos: Math.cos(r) };
}

export const isRightAngle = (deg: number): boolean => Number.isInteger(deg / 90);

export const centreOf = (b: Bounds): Point => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 });

/** Rotates clockwise on screen (y down) about `pivot`. */
export function rotatePoint(p: Point, pivot: Point, deg: number): Point {
  const { sin, cos } = sinCos(deg);
  const dx = p.x - pivot.x, dy = p.y - pivot.y;
  return { x: pivot.x + dx * cos - dy * sin, y: pivot.y + dx * sin + dy * cos };
}

/** The axis-aligned box that encloses a rectangle rotated about its own centre. */
export function rotatedExtent(b: Bounds, rotationDeg: number): Bounds {
  if (rotationDeg === 0) return { ...b };
  const c = centreOf(b);
  const corners = [
    { x: b.x, y: b.y }, { x: b.x + b.width, y: b.y }, { x: b.x + b.width, y: b.y + b.height }, { x: b.x, y: b.y + b.height },
  ].map(p => rotatePoint(p, c, rotationDeg));
  return pointsExtent(corners);
}

export function pointsExtent(points: readonly Point[]): Bounds {
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const p of points) {
    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
    if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
  }
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
}

export function unionBounds(list: readonly Bounds[]): Bounds {
  if (!list.length) throw new RangeError('cannot take the union of nothing');
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const b of list) {
    minX = Math.min(minX, b.x); minY = Math.min(minY, b.y);
    maxX = Math.max(maxX, b.x + b.width); maxY = Math.max(maxY, b.y + b.height);
  }
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
}

/** Canonical extent of a connector for grouping: its authored fallback box, its waypoints and its free endpoints. Never an auto-route. */
export function connectorExtent(c: ConnectorElement): Bounds {
  const pts: Point[] = [...c.waypoints];
  for (const ep of [c.from, c.to]) if (ep.glue === 'none' && ep.point) pts.push(ep.point);
  return pts.length ? unionBounds([c.bounds, pointsExtent(pts)]) : { ...c.bounds };
}

/** Visual bounds used by arrange: rotated rectangle for ordinary elements, fallback extent for connectors, stored bounds for groups. */
export function visualBounds(el: Element): Bounds {
  if (el.kind === 'connector') return connectorExtent(el);
  if (el.kind === 'group') return { ...el.bounds };
  return rotatedExtent(el.bounds, el.rotationDeg);
}

// ---------------------------------------------------------------------------- point-wise transforms

export type PointMap = (p: Point) => Point;

/** Applies `f` to a connector's free endpoints and waypoints. Glued endpoints keep their references. The caller recomputes bounds. */
export function mapConnectorPoints(c: ConnectorElement, f: PointMap): ConnectorElement {
  const mapEnd = (ep: Endpoint): Endpoint => (ep.glue === 'none' && ep.point ? { ...ep, point: f(ep.point) } : ep);
  const next: ConnectorElement = { ...c, from: mapEnd(c.from), to: mapEnd(c.to), waypoints: c.waypoints.map(f) };
  if (c.routing) next.routing = mapRouting(c.routing, f);
  return next;
}

/**
 * The stored route and pinned runs of a connector carried through a point map. A pinned run keeps its meaning only if the map keeps
 * the axes (a move or a scale); a turn drops the pins and leaves the route to be planned again.
 */
export function mapRouting(routing: NonNullable<ConnectorElement['routing']>, f: PointMap): NonNullable<ConnectorElement['routing']> {
  const out = { ...routing };
  if (routing.advisory) out.advisory = routing.advisory.map(f);
  if (routing.constraints) {
    const o = f({ x: 0, y: 0 }), ex = f({ x: 1, y: 0 }), ey = f({ x: 0, y: 1 });
    const keepsAxes = Math.abs(ex.y - o.y) < 1e-9 && Math.abs(ey.x - o.x) < 1e-9 && ex.x - o.x > 0 && ey.y - o.y > 0;
    const kept = keepsAxes ? routing.constraints.map(c => ({ ...c, at: c.axis === 'h' ? f({ x: 0, y: c.at }).y : f({ x: c.at, y: 0 }).x })) : [];
    if (kept.length) out.constraints = kept; else delete out.constraints;
  }
  return out;
}

export const translateBounds = (b: Bounds, dx: number, dy: number): Bounds => ({ ...b, x: b.x + dx, y: b.y + dy });

/** A rectangle element rotated about `pivot`: its centre orbits, its own orientation turns by the same angle. */
export function rotateRect(b: Bounds, rotationDeg: number, pivot: Point, deltaDeg: number): { bounds: Bounds; rotationDeg: number } {
  const c = rotatePoint(centreOf(b), pivot, deltaDeg);
  return { bounds: { x: c.x - b.width / 2, y: c.y - b.height / 2, width: b.width, height: b.height }, rotationDeg: rotationDeg + deltaDeg };
}

/**
 * A rectangle element scaled about `origin`. Rotated by a right angle the width follows the vertical factor, because the
 * element's own axes are swapped. Callers reject non-uniform scaling for other rotations.
 */
export function scaleRect(b: Bounds, rotationDeg: number, origin: Point, sx: number, sy: number): Bounds {
  const c = centreOf(b);
  const nx = origin.x + (c.x - origin.x) * sx, ny = origin.y + (c.y - origin.y) * sy;
  const swapped = isRightAngle(rotationDeg) && ((((rotationDeg / 90) % 2) + 2) % 2) === 1;
  const w = b.width * (swapped ? sy : sx), h = b.height * (swapped ? sx : sy);
  return { x: nx - w / 2, y: ny - h / 2, width: w, height: h };
}
