import type { Bounds, DiagramDocument, Element, GroupElement, Page } from '../contracts/generated';
import { connectorExtent, rotatedExtent, unionBounds } from './geometry';

/** child id to the id of the group that holds it. Groups form an acyclic forest, so a child has at most one parent. */
export function parentMap(elements: readonly Element[]): Map<string, string> {
  const parents = new Map<string, string>();
  for (const e of elements) if (e.kind === 'group') for (const c of e.childIds) parents.set(c, e.id);
  return parents;
}

/** Nearest parent first. */
export function ancestorsOf(parents: ReadonlyMap<string, string>, id: string): string[] {
  const out: string[] = [];
  for (let p = parents.get(id); p !== undefined; p = parents.get(p)) {
    if (out.includes(p)) break; // defensive: a cycle never ends the walk
    out.push(p);
  }
  return out;
}

/** The leaves (non-group elements) under `id`, or `id` itself for a leaf. Document order is preserved. */
export function leavesUnder(byId: ReadonlyMap<string, Element>, id: string): string[] {
  const el = byId.get(id);
  if (!el) return [];
  if (el.kind !== 'group') return [id];
  const out: string[] = [];
  const seen = new Set<string>();
  const walk = (g: GroupElement) => {
    if (seen.has(g.id)) return;
    seen.add(g.id);
    for (const c of g.childIds) {
      const ce = byId.get(c);
      if (!ce) continue;
      if (ce.kind === 'group') walk(ce); else out.push(c);
    }
  };
  walk(el);
  return out;
}

/** The deterministic tight union of descendant canonical geometry. Routes are never part of it. */
export function groupBoundsFrom(byId: ReadonlyMap<string, Element>, group: GroupElement): Bounds {
  const boxes: Bounds[] = [];
  for (const id of leavesUnder(byId, group.id)) {
    const e = byId.get(id)!;
    boxes.push(e.kind === 'connector' ? connectorExtent(e) : rotatedExtent(e.bounds, e.rotationDeg));
  }
  if (!boxes.length) throw new RangeError(`group ${group.id} has no members`);
  return unionBounds(boxes);
}

export function deriveGroupBounds(document: DiagramDocument | Page, groupId: string): Bounds {
  const pages = 'pages' in document ? document.pages : [document];
  for (const p of pages) {
    const byId = new Map(p.elements.map(e => [e.id, e] as const));
    const g = byId.get(groupId);
    if (g?.kind === 'group') return groupBoundsFrom(byId, g);
  }
  throw new RangeError(`no group ${groupId}`);
}
