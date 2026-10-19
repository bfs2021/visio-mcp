import type { Element, Page } from '../contracts/generated';
import { effectiveState } from '../canvas/cellMapping';
import { parentMap } from '../commands/groups';

/**
 * Keyboard and screen-reader view of a page. Everything here is pure: the order objects are visited in, which object lies in a direction,
 * and the words that name an object. The canvas stays a picture; this is how the same objects are reached without a pointer.
 */

/** The objects a person can step to: top-level and visible, with a group standing for its members. Reading order: top to bottom, then left to right. */
export function navigableObjects(page: Page): Element[] {
  const parents = parentMap(page.elements);
  return page.elements
    .filter(e => !parents.has(e.id) && effectiveState(e, page.layers).visible)
    .map((e, i) => ({ e, i, box: visualBox(page, e) }))
    .sort((a, b) => a.box.y - b.box.y || a.box.x - b.box.x || a.i - b.i)
    .map(o => o.e);
}

export interface Box { x: number; y: number; width: number; height: number }

/** A connector has no bounds of its own worth using for order; use the box of its end points when both are glued to shapes on the page. */
export function visualBox(page: Page, e: Element): Box {
  if (e.kind === 'connector') {
    const byId = new Map(page.elements.map(x => [x.id, x] as const));
    const pts = [e.from, e.to].map(ep => ep.glue !== 'none' && ep.elementId ? centreOf(byId.get(ep.elementId)) : ep.point ?? null).filter((p): p is { x: number; y: number } => !!p);
    if (pts.length === 2) {
      const [a, b] = pts as [{ x: number; y: number }, { x: number; y: number }];
      return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) };
    }
  }
  return e.bounds;
}

const centreOf = (e: Element | undefined) => e ? { x: e.bounds.x + e.bounds.width / 2, y: e.bounds.y + e.bounds.height / 2 } : null;

/** Next or previous object, or null past either end (so Tab can leave the canvas instead of trapping the keyboard). */
export function stepFrom(order: readonly Element[], currentId: string | undefined, delta: 1 | -1): Element | null {
  if (!order.length) return null;
  const i = currentId === undefined ? -1 : order.findIndex(e => e.id === currentId);
  if (i < 0) return delta === 1 ? order[0]! : order[order.length - 1]!;
  return order[i + delta] ?? null;
}

export type Direction = 'left' | 'right' | 'up' | 'down';

/** The object nearest to `from` within a 90-degree cone in the given direction; across-distance counts double so the result feels straight. */
export function nearestIn(page: Page, order: readonly Element[], fromId: string, dir: Direction): Element | null {
  const from = order.find(e => e.id === fromId);
  if (!from) return null;
  const c = boxCentre(visualBox(page, from));
  let best: { e: Element; score: number } | null = null;
  for (const e of order) {
    if (e.id === fromId) continue;
    const p = boxCentre(visualBox(page, e));
    const dx = p.x - c.x, dy = p.y - c.y;
    const along = dir === 'right' ? dx : dir === 'left' ? -dx : dir === 'down' ? dy : -dy;
    const across = Math.abs(dir === 'left' || dir === 'right' ? dy : dx);
    if (along <= 0 || across > along) continue;
    const score = along + 2 * across;
    if (!best || score < best.score) best = { e, score };
  }
  return best?.e ?? null;
}
const boxCentre = (b: Box) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 });

/** What a person or a screen reader calls the object. */
export function nameOf(page: Page, e: Element): string {
  if (e.alias) return e.alias;
  if ((e.kind === 'shape' || e.kind === 'text') && e.text?.value?.trim()) return e.text.value.trim().replace(/\s+/g, ' ').slice(0, 60);
  if (e.kind === 'connector') {
    const byId = new Map(page.elements.map(x => [x.id, x] as const));
    const end = (ep: typeof e.from) => ep.glue !== 'none' && ep.elementId && byId.get(ep.elementId) ? nameOf(page, byId.get(ep.elementId)!) : 'a free end';
    return `from ${end(e.from)} to ${end(e.to)}`;
  }
  return `unnamed ${roleOf(e)}`;
}

export function roleOf(e: Element): string {
  switch (e.kind) {
    case 'shape': return 'shape';
    case 'text': return 'text';
    case 'image': return 'picture';
    case 'connector': return 'connector';
    case 'group': return `group of ${e.childIds.length}`;
  }
}

/** One line for assistive technology: name, role, where it is and how big, in the person's unit. */
export function describe(page: Page, e: Element, fmt: (pt: number) => string, unit: string): string {
  const b = e.bounds;
  const place = e.kind === 'connector' ? '' : `, at x ${fmt(b.x)} y ${fmt(b.y)} ${unit}, ${fmt(b.width)} by ${fmt(b.height)} ${unit}`;
  const flags = [e.locked ? 'locked' : ''].filter(Boolean).join(', ');
  return `${nameOf(page, e)}, ${roleOf(e)}${place}${flags ? `, ${flags}` : ''}`;
}

/** Elements a connector may join: anything that is not itself a connector. */
export function connectable(page: Page): Element[] {
  return page.elements.filter(e => e.kind !== 'connector' && effectiveState(e, page.layers).visible);
}
