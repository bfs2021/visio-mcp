import type { Element, Page } from '../contracts/generated';
import { segmentsOf } from './routeSegments';
import type { V } from './orthogonalRoute';

export type SnapKind = 'grid' | 'centre' | 'guide' | 'track';
export interface SnapCandidate { value: number; kind: SnapKind }
export interface RunSnap { value: number; kind?: SnapKind }

/** Snap reach in screen pixels, and the extra reach a snapped run keeps before it lets go (so it does not flicker at the edge). */
export const SNAP_PX = 8;
export const RELEASE_FACTOR = 1.5;

/**
 * Places for a run to snap to on its perpendicular axis: the grid, the centre-lines of the shapes the connector joins, the page
 * guides, and the tracks of other connectors' parallel runs.
 */
export function snapCandidates(page: Page, connector: Element, axis: 'h' | 'v', others: ReadonlyMap<string, readonly V[]>, around: number, reachPt: number): SnapCandidate[] {
  const out: SnapCandidate[] = [];
  const coordOf = (b: { x: number; y: number; width: number; height: number }) => (axis === 'h' ? b.y + b.height / 2 : b.x + b.width / 2);
  if (page.grid.snap && page.grid.spacingPt > 0) {
    const g = page.grid.spacingPt, first = Math.floor((around - reachPt) / g) * g;
    for (let v = first; v <= around + reachPt; v += g) out.push({ value: v, kind: 'grid' });
  }
  if (connector.kind === 'connector') {
    for (const end of [connector.from, connector.to]) {
      const shape = end.elementId ? page.elements.find(e => e.id === end.elementId) : undefined;
      if (shape) out.push({ value: coordOf(shape.bounds), kind: 'centre' });
    }
  }
  for (const guide of page.guides) if ((axis === 'h') === (guide.orientation === 'horizontal')) out.push({ value: guide.positionPt, kind: 'guide' });
  for (const [id, points] of others) {
    if (id === connector.id) continue;
    for (const s of segmentsOf(points)) if (s.axis === axis) out.push({ value: s.coord, kind: 'track' });
  }
  return out;
}

/** The nearest candidate within reach; a run that was snapped keeps its snap until the pointer is `RELEASE_FACTOR` times further away. */
export function snapRun(raw: number, candidates: readonly SnapCandidate[], reachPt: number, held?: RunSnap): RunSnap {
  if (held?.kind !== undefined && Math.abs(raw - held.value) <= reachPt * RELEASE_FACTOR) return held;
  let best: SnapCandidate | undefined;
  for (const c of candidates) if (Math.abs(raw - c.value) <= reachPt && (!best || Math.abs(raw - c.value) < Math.abs(raw - best.value))) best = c;
  return best ? { value: best.value, kind: best.kind } : { value: raw };
}
