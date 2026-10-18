import type { Bounds, Element, Page } from '../contracts/generated';

/** Marks the parts of a swim-lane that are structure (frame, bands, header cells, rules), not content that belongs in a lane. */
export const STRUCTURE_KEY = 'swimlane.structure';

export const isLaneStructure = (el: Element): boolean => el.metadata[STRUCTURE_KEY] === '1';

export interface LaneCell { index: number; title: string; bounds: Bounds }
export interface Swimlane { groupId: string; title: string; bounds: Bounds; lanes: LaneCell[]; phases: LaneCell[]; structureIds: ReadonlySet<string>; partIds: ReadonlySet<string> }

const textOf = (el: Element | undefined): string => (el && (el.kind === 'text' || el.kind === 'shape') ? el.text?.value ?? '' : '');

/**
 * The swim-lane components on a page, read from their named parts. Lanes are the bands (full width, one per lane); phases are the columns
 * of the header row, extended down through the lanes. Reads only; a component whose parts were edited into something unrecognisable is skipped.
 */
export function findSwimlanes(page: Page): Swimlane[] {
  const byId = new Map(page.elements.map(e => [e.id, e] as const));
  const out: Swimlane[] = [];
  for (const g of page.elements) {
    if (g.kind !== 'group' || g.metadata['layout.template'] !== 'swimlane') continue;
    const role = new Map<string, Element>();
    for (const id of g.childIds) { const c = byId.get(id); const r = c?.metadata['layout.role']; if (c && r) role.set(r, c); }
    const frame = role.get('frame');
    if (!frame) continue;
    const lanes: LaneCell[] = [], phases: LaneCell[] = [];
    for (let i = 0; role.has(`lane.${i}.band`); i++) lanes.push({ index: i, title: textOf(role.get(`lane.${i}.title`)), bounds: role.get(`lane.${i}.band`)!.bounds });
    const bandsTop = lanes[0]?.bounds.y ?? frame.bounds.y, bandsBottom = lanes.length ? lanes[lanes.length - 1]!.bounds.y + lanes[lanes.length - 1]!.bounds.height : frame.bounds.y + frame.bounds.height;
    for (let i = 0; role.has(`phase.${i}.header`); i++) {
      const h = role.get(`phase.${i}.header`)!.bounds;
      phases.push({ index: i, title: textOf(role.get(`phase.${i}.title`)), bounds: { x: h.x, y: bandsTop, width: h.width, height: bandsBottom - bandsTop } });
    }
    out.push({ groupId: g.id, title: g.name ?? textOf(role.get('title')), bounds: frame.bounds, lanes, phases, partIds: new Set(g.childIds), structureIds: new Set(g.childIds.filter(id => { const c = byId.get(id); return !!c && isLaneStructure(c); })) });
  }
  return out;
}

const centreOf = (b: Bounds) => ({ x: b.x + b.width / 2, y: b.y + b.height / 2 });
const holds = (b: Bounds, p: { x: number; y: number }) => p.x >= b.x && p.x < b.x + b.width && p.y >= b.y && p.y < b.y + b.height;

/** Is this element one of the swim-lane's own parts (its text labels as well as its structure)? */
export const isPartOfSwimlane = (swimlanes: readonly Swimlane[], id: string): boolean => swimlanes.some(s => s.partIds.has(id));

export interface Membership { swimlane: string; lane?: string; phase?: string }

/** Which lane and phase the middle of a box is in, in the smallest swim-lane that holds it. */
export function membershipOf(swimlanes: readonly Swimlane[], bounds: Bounds): Membership | undefined {
  const c = centreOf(bounds);
  let best: Swimlane | undefined;
  for (const s of swimlanes) if (holds(s.bounds, c) && (!best || s.bounds.width * s.bounds.height < best.bounds.width * best.bounds.height)) best = s;
  if (!best) return undefined;
  const lane = best.lanes.find(l => holds(l.bounds, c)), phase = best.phases.find(p => holds(p.bounds, c));
  return { swimlane: best.title, ...(lane ? { lane: lane.title || `lane ${lane.index + 1}` } : {}), ...(phase ? { phase: phase.title || `phase ${phase.index + 1}` } : {}) };
}

/** The lane boundaries a box crosses by more than `tol` points: it is in one lane by its middle, but also spills into another. */
export function lanesCrossed(sw: Swimlane, bounds: Bounds, tol = 2): LaneCell[] {
  const top = bounds.y, bottom = bounds.y + bounds.height;
  return sw.lanes.filter(l => Math.min(bottom, l.bounds.y + l.bounds.height) - Math.max(top, l.bounds.y) > tol && bounds.x < l.bounds.x + l.bounds.width && bounds.x + bounds.width > l.bounds.x);
}
