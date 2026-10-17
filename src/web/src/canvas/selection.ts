import type { Element, Page, Snapshot } from '../contracts/generated';
import { ancestorsOf, leavesUnder, parentMap } from '../commands/groups';

/**
 * Groups are not drawn as cells of their own: the canvas holds the members, and a group is "selected" when all of its members are.
 * These helpers translate between the two views. The model keeps the canvas view (leaf ids); the user-facing view collapses
 * complete groups into one object.
 */

const pageHolding = (snapshot: Snapshot, id: string): Page | undefined => snapshot.document.pages.find(p => p.elements.some(e => e.id === id));

/** Clicking a member selects its whole top-level group, so the group moves, copies and deletes as one. */
export function expandToGroups(snapshot: Snapshot, ids: readonly string[]): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  const add = (id: string) => { if (!seen.has(id)) { seen.add(id); out.push(id); } };
  for (const id of ids) {
    const page = pageHolding(snapshot, id);
    if (!page) continue;
    const parents = parentMap(page.elements);
    const top = ancestorsOf(parents, id).at(-1);
    if (!top) { add(id); continue; }
    const byId = new Map(page.elements.map(e => [e.id, e] as const));
    for (const leaf of leavesUnder(byId, top)) add(leaf);
  }
  return out;
}

/** The selection as the user means it: every group whose members are all selected stands for them, and its parent group does too when it is complete. */
export function collapseToGroups(snapshot: Snapshot, ids: readonly string[]): string[] {
  const chosen = new Set(ids);
  const out: string[] = [];
  const seen = new Set<string>();
  for (const id of ids) {
    const page = pageHolding(snapshot, id);
    if (!page) continue;
    const parents = parentMap(page.elements);
    const byId = new Map(page.elements.map(e => [e.id, e] as const));
    // highest ancestor whose every leaf is selected
    let pick = id;
    for (const g of ancestorsOf(parents, id)) {
      if (leavesUnder(byId, g).every(l => chosen.has(l))) pick = g;
    }
    if (!seen.has(pick)) { seen.add(pick); out.push(pick); }
  }
  // a group already chosen covers its own members that were also listed
  return out.filter(id => !ancestorsOf(parentMapFor(snapshot, id), id).some(a => seen.has(a)));
}

function parentMapFor(snapshot: Snapshot, id: string): Map<string, string> {
  const page = pageHolding(snapshot, id);
  return page ? parentMap(page.elements) : new Map();
}

/** Every leaf under the given targets (a leaf stands for itself). */
export function leavesOfTargets(snapshot: Snapshot, targets: readonly string[]): string[] {
  const out: string[] = [];
  for (const t of targets) {
    const page = pageHolding(snapshot, t);
    if (!page) continue;
    const byId = new Map(page.elements.map(e => [e.id, e] as const));
    out.push(...leavesUnder(byId, t));
  }
  return out;
}

export type { Element };
