import type { Element, Layer } from '../contracts/generated';
import { effectiveState } from '../canvas/cellMapping';
import type { Rect } from './obstacleRoute';

/** The axis-aligned box around an element after its rotation. */
export function visualRect(el: { bounds: Rect; rotationDeg: number }): Rect {
  const { x, y, width: w, height: h } = el.bounds;
  const a = (el.rotationDeg * Math.PI) / 180, c = Math.abs(Math.cos(a)), s = Math.abs(Math.sin(a));
  const W = w * c + h * s, H = w * s + h * c;
  return { x: x + w / 2 - W / 2, y: y + h / 2 - H / 2, width: W, height: H };
}

const contains = (outer: Rect, inner: Rect, tol = 0.5): boolean =>
  outer.x <= inner.x + tol && outer.y <= inner.y + tol && outer.x + outer.width >= inner.x + inner.width - tol && outer.y + outer.height >= inner.y + inner.height - tol;
const holds = (r: Rect, p: { x: number; y: number }): boolean => p.x > r.x && p.x < r.x + r.width && p.y > r.y && p.y < r.y + r.height;

export const BACKGROUND_SHARE = 0.2; // a shape this large (share of the page) is a backdrop, not an obstacle

/**
 * The shapes a connector must route around. Not obstacles: the connector's own two shapes, text boxes (labels sit on lines on purpose),
 * invisible shapes, hidden or layer-hidden elements, and containers or backdrops. A container is a shape that fully holds another candidate,
 * or one of the connector's own shapes or free end points, or that covers a large share of the page.
 */
export function selectObstacles(elements: readonly Element[], layers: readonly Layer[], endIds: ReadonlySet<string>, endRects: readonly Rect[], freePoints: ReadonlyArray<{ x: number; y: number }>, pageArea: number): Rect[] {
  const candidates: Array<{ id: string; rect: Rect }> = [];
  for (const el of elements) {
    if (el.kind === 'connector' || el.kind === 'group' || el.kind === 'text' || endIds.has(el.id)) continue;
    if (!effectiveState(el, layers).visible) continue;
    if (el.kind === 'shape' && el.style.fill.kind === 'none' && el.style.stroke.kind === 'none') continue;
    candidates.push({ id: el.id, rect: visualRect(el) });
  }
  return candidates
    .filter(c => c.rect.width * c.rect.height < BACKGROUND_SHARE * pageArea)
    .filter(c => !endRects.some(e => contains(c.rect, e)) && !freePoints.some(p => holds(c.rect, p)))
    .filter(c => !candidates.some(o => o.id !== c.id && o.rect.width * o.rect.height < c.rect.width * c.rect.height && contains(c.rect, o.rect)))
    .map(c => c.rect);
}
