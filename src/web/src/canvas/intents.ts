import type { Bounds, Element, Endpoint, Operation, Page, Point, Snapshot } from '../contracts/generated';
import { visualBounds, unionBounds } from '../commands/geometry';
import { anchorsOf, guideCorrection, layersSnap, type GuideSnapOptions } from './guideSnapping';
import { collapseToGroups, leavesOfTargets } from './selection';

/**
 * Pure translation from what the user did on the canvas into canonical operations. Snapping happens here, in the interaction
 * planner, so the committed coordinates are the snapped ones and the canvas never makes a second, hidden adjustment.
 */

export type Intent =
  | { type: 'move'; ids: string[]; dx: number; dy: number }
  | { type: 'resize'; id: string; bounds: Bounds }
  | { type: 'rotate'; id: string; deltaDeg: number }
  | { type: 'bend'; id: string; waypoints: Point[] }
  | { type: 'reconnect'; id: string; end: 'from' | 'to'; elementId?: string; point?: Point }
  | { type: 'text'; id: string; value: string }
  | { type: 'connect'; from: string; to: string };

export interface SnapContext { enabled: boolean; spacingPt: number }

export function pageOf(snapshot: Snapshot, pageId?: string): Page {
  const pages = snapshot.document.pages;
  return (pageId ? pages.find(p => p.id === pageId) : undefined) ?? pages[0]!;
}

export function findElement(snapshot: Snapshot, id: string): Element | undefined {
  for (const p of snapshot.document.pages) for (const e of p.elements) if (e.id === id) return e;
  return undefined;
}

/** An element snaps only if the page grid snaps and every layer it belongs to allows snapping. */
export function snapContextFor(page: Page, element: Element | undefined): SnapContext {
  const layersAllow = !element || element.layerIds.every(id => page.layers.find(l => l.id === id)?.snap !== false);
  return { enabled: page.grid.snap && layersAllow && page.grid.spacingPt > 0, spacingPt: page.grid.spacingPt };
}

export const snap = (v: number, ctx: SnapContext): number => (ctx.enabled ? Math.round(v / ctx.spacingPt) * ctx.spacingPt : v);
const round = (v: number, places = 9) => Math.round(v * 10 ** places) / 10 ** places;

/** Connectors that are positioned entirely by their glued endpoints move with their shapes, not by themselves. */
const movesByItself = (e: Element) => e.kind !== 'connector' || e.from.glue === 'none' || e.to.glue === 'none' || e.waypoints.length > 0;

export function moveOperations(snapshot: Snapshot, ids: string[], dx: number, dy: number, guides?: GuideSnapOptions): Operation[] {
  const elements = ids.map(id => findElement(snapshot, id)).filter((e): e is Element => !!e && movesByItself(e));
  if (!elements.length || (dx === 0 && dy === 0)) return []; // no movement is no edit, however far from the grid the element sits
  // Snap the lead element's top-left to the grid and move everything by the same corrected delta, so relative layout is kept.
  const lead = elements.find(e => e.kind !== 'connector') ?? elements[0]!;
  const ctx = snapContextFor(pageContaining(snapshot, lead.id), lead);
  const page = pageContaining(snapshot, lead.id);
  const allowGuides = elements.every(e => layersSnap(page,e));
  const boxes=elements.filter(e=>e.kind!=='connector').map(visualBounds);
  const extent=unionBounds(boxes.length?boxes:[lead.bounds]);
  const moved={...extent,x:extent.x+dx,y:extent.y+dy};
  const gx=allowGuides?guideCorrection(page,'x',anchorsOf(moved,'x'),guides):undefined;
  const gy=allowGuides?guideCorrection(page,'y',anchorsOf(moved,'y'),guides):undefined;
  const sdx=round(gx===undefined?snap(lead.bounds.x+dx,ctx)-lead.bounds.x:dx+gx);
  const sdy=round(gy===undefined?snap(lead.bounds.y+dy,ctx)-lead.bounds.y:dy+gy);
  if (sdx === 0 && sdy === 0) return [];
  return elements.map(e => ({ op: 'move', target: e.id, delta: { xPt: sdx, yPt: sdy } }));
}

export function pageContaining(snapshot: Snapshot, id: string): Page {
  return snapshot.document.pages.find(p => p.elements.some(e => e.id === id)) ?? snapshot.document.pages[0]!;
}

export function resizeOperations(snapshot: Snapshot, id: string, bounds: Bounds, guides?: GuideSnapOptions): Operation[] {
  const el = findElement(snapshot, id);
  if (!el || el.kind === 'connector') return [];
  const b0 = el.bounds;
  if (b0.x === bounds.x && b0.y === bounds.y && b0.width === bounds.width && b0.height === bounds.height) return []; // unchanged is not an edit
  const ctx = snapContextFor(pageContaining(snapshot, id), el);
  const x = snap(bounds.x, ctx), y = snap(bounds.y, ctx);
  const right = snap(bounds.x + bounds.width, ctx), bottom = snap(bounds.y + bounds.height, ctx);
  const next = ctx.enabled && right > x && bottom > y
    ? { x, y, width: right - x, height: bottom - y }
    : { x: bounds.x, y: bounds.y, width: bounds.width, height: bounds.height };
  const page = pageContaining(snapshot,id);
  if (layersSnap(page,el)) {
    // Resize only the changed edge; a centre target adjusts that edge by twice the correction.
    for (const axis of ['x','y'] as const) {
      const size = axis === 'x' ? 'width' : 'height';
      const startChanged = bounds[axis] !== b0[axis];
      const endChanged = bounds[axis]+bounds[size] !== b0[axis]+b0[size];
      if (!startChanged && !endChanged) continue;
      const vb = visualBounds({...el,bounds} as Element);
      const points = el.rotationDeg === 0 ? anchorsOf(bounds,axis).filter((_,i)=>i===1||(i===0&&startChanged)||(i===2&&endChanged)) : anchorsOf(vb,axis);
      const correction=guideCorrection(page,axis,points,guides);
      if (correction === undefined) continue;
      // At nonzero rotation retain the resized rectangle and translate its visual extent to the guide.
      if (el.rotationDeg !== 0) { next[axis]=bounds[axis]+correction;next[size]=bounds[size];continue; }
      const edgePoints = [bounds[axis],bounds[axis]+bounds[size]];
      const edgeCorrection=guideCorrection(page,axis,startChanged&&endChanged?edgePoints:[edgePoints[startChanged?0:1]!],guides);
      const delta = edgeCorrection === correction ? correction : 2*correction;
      const start = startChanged ? bounds[axis]+delta : bounds[axis];
      const end = endChanged ? bounds[axis]+bounds[size]+delta : bounds[axis]+bounds[size];
      if(end>start){next[axis]=start;next[size]=end-start;}
    }
  }
  if(el.rotationDeg !== 0 && layersSnap(page,el)) {
    const finalVisual=visualBounds({...el,bounds:next} as Element);
    next.x += guideCorrection(page,'x',anchorsOf(finalVisual,'x'),guides)??0;
    next.y += guideCorrection(page,'y',anchorsOf(finalVisual,'y'),guides)??0;
  }
  const rounded = { x: round(next.x), y: round(next.y), width: round(next.width), height: round(next.height) };
  const same = rounded.x === el.bounds.x && rounded.y === el.bounds.y && rounded.width === el.bounds.width && rounded.height === el.bounds.height;
  return same ? [] : [{ op: 'resize', target: id, bounds: rounded }];
}

export function rotateOperations(snapshot: Snapshot, id: string, deltaDeg: number): Operation[] {
  const el = findElement(snapshot, id);
  if (!el || el.kind === 'connector' || el.kind === 'group') return [];
  const delta = round(deltaDeg, 2);
  return delta === 0 ? [] : [{ op: 'rotate', target: id, deltaDeg: delta }];
}

export function intentOperations(snapshot: Snapshot, intent: Intent, guides?: GuideSnapOptions): Operation[] {
  switch (intent.type) {
    case 'move': return moveOperations(snapshot, intent.ids, intent.dx, intent.dy, guides);
    case 'resize': return resizeOperations(snapshot, intent.id, intent.bounds, guides);
    case 'rotate': return rotateOperations(snapshot, intent.id, intent.deltaDeg);
    case 'bend': return [{ op: 'set', target: intent.id, patch: { waypoints: intent.waypoints.map(p => ({ x: round(p.x, 3), y: round(p.y, 3) })) } }];
    case 'reconnect': {
      const endpoint: Endpoint = intent.elementId ? { elementId: intent.elementId, glue: 'dynamic' } : { point: intent.point ?? { x: 0, y: 0 }, glue: 'none' };
      return [{ op: 'set', target: intent.id, patch: { [intent.end]: endpoint } }];
    }
    case 'text': return [{ op: 'set', target: intent.id, patch: { text: { value: intent.value } } }];
    case 'connect': return [{ op: 'createConnector', from: { elementId: intent.from, glue: 'dynamic' }, to: { elementId: intent.to, glue: 'dynamic' }, route: 'orthogonal' }];
  }
}

/**
 * Delete the selection, taking attached connectors with their shapes and not deleting a connector twice. A group whose members are all
 * selected is deleted as one object, members included; members of a partly selected group are removed from it.
 */
export function deleteOperations(snapshot: Snapshot, ids: string[]): Operation[] {
  const targets = collapseToGroups(snapshot, ids);
  const doomed = new Set(leavesOfTargets(snapshot, targets));
  const ops: Operation[] = [];
  for (const id of targets) {
    const el = findElement(snapshot, id);
    if (!el) continue;
    if (el.kind === 'connector') {
      const covered = [el.from, el.to].some(ep => ep.elementId && doomed.has(ep.elementId));
      if (!covered) ops.push({ op: 'delete', target: id });
    } else if (el.kind === 'group') ops.push({ op: 'delete', target: id, children: 'delete', withConnectors: true });
    else ops.push({ op: 'delete', target: id, withConnectors: true });
  }
  return ops;
}

export function nudgeOperations(snapshot: Snapshot, ids: string[], dx: number, dy: number): Operation[] {
  return ids.map(id => findElement(snapshot, id)).filter((e): e is Element => !!e && movesByItself(e) && !e.locked)
    .map(e => ({ op: 'move', target: e.id, delta: { xPt: dx, yPt: dy } }) as Operation);
}
