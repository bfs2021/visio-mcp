import type { Element, Operation, Page, Snapshot } from '../contracts/generated';
import { effectiveState } from '../canvas/cellMapping';
import { ancestorsOf, parentMap } from '../commands/groups';
import { canonicalSerialize } from '../model/canonical';
import { fail, ok, type Result } from '../model/result';

export interface FindOptions { query: string; caseSensitive: boolean; includeHidden: boolean }
export interface FindHit {
  id: string; pageId: string; pageName: string; label: string; snippet: string;
  fields: Array<'text' | 'name' | 'alias'>; hidden: boolean; locked: boolean; replaceable: boolean;
}
export interface ReplacementProposal {
  documentId: string; sessionId: string; baseRevision: number; operations: Operation[];
}
function literal(query: string, caseSensitive: boolean, global = false): RegExp {
  const special = '\\^$.*+?()[]{}|';
  const escaped = Array.from(query, c => special.includes(c) ? '\\' + c : c).join('');
  return new RegExp(escaped, `${global ? 'g' : ''}${caseSensitive ? '' : 'i'}u`);
}
/** Ancestor locks are enforced by the command planner as well as the leaf's own layer state. */
function stateOf(page: Page, el: Element, parents: Map<string, string>) {
  const states = [el, ...ancestorsOf(parents, el.id).map(id => page.elements.find(e => e.id === id)).filter((e): e is Element => !!e)].map(e => effectiveState(e, page.layers));
  return { hidden: states.some(s => !s.visible), locked: states.some(s => s.locked) };
}
export function searchDocument(snapshot: Snapshot, options: FindOptions): FindHit[] {
  if (!options.query) return [];
  const re = literal(options.query, options.caseSensitive);
  const hits: FindHit[] = [];
  for (const page of snapshot.document.pages) {
    const parents = parentMap(page.elements);
    for (const el of page.elements) {
      const state = stateOf(page, el, parents);
      if (state.hidden && !options.includeHidden) continue;
      const text = 'text' in el ? el.text?.value ?? '' : '';
      const fields: FindHit['fields'] = [];
      if (re.test(text)) fields.push('text');
      if (el.name && re.test(el.name)) fields.push('name');
      if (el.alias && re.test(el.alias)) fields.push('alias');
      if (!fields.length) continue;
      const at = text.search(re);
      hits.push({ id: el.id, pageId: page.id, pageName: page.name, label: el.name || el.alias || el.kind,
        snippet: text.slice(Math.max(0, at - 30), Math.max(0, at - 30) + 160), fields, ...state,
        replaceable: fields.includes('text') && !state.hidden && !state.locked });
    }
  }
  return hits;
}
export function replacementProposal(snapshot: Snapshot, options: FindOptions, replacement: string, currentId?: string): Result<ReplacementProposal> {
  const operations: Operation[] = [];
  if (options.query) {
    const re = literal(options.query, options.caseSensitive, true);
    const hits = searchDocument(snapshot, options).filter(h => h.replaceable && (!currentId || h.id === currentId));
    const byId = new Map(snapshot.document.pages.flatMap(p => p.elements.map(e => [e.id, e] as const)));
    for (const hit of hits) {
      const el = byId.get(hit.id)!;
      if (!('text' in el) || !el.text) continue;
      const value = el.text.value.replace(re, () => replacement); // replacement is literal, including dollar signs
      if (value !== el.text.value) operations.push({ op: 'set', target: el.id, patch: { text: { value } } });
    }
  }
  if (operations.length > 1000) return fail('invalid_request', 'Replace all exceeds the 1000-object transaction limit. Narrow the search; no changes were applied.');
  return ok({ documentId: snapshot.documentId, sessionId: snapshot.sessionId, baseRevision: snapshot.revision, operations });
}
function styleKey(el: Element): string | null {
  if (el.kind === 'group') return null;
  const text = 'text' in el && el.text ? { ...el.text, value: '' } : undefined;
  if (el.kind === 'image') return canonicalSerialize({kind:el.kind,fit:el.fit,opacity:el.opacity,preserveAspectRatio:el.preserveAspectRatio});
  return canonicalSerialize({kind:el.kind,...('style' in el ? {style:el.style}:{}),text});
}
export function matchingSelection(page: Page, seedId: string, mode: 'style' | 'layers'): { ids: string[]; limitation?: string } {
  const seed = page.elements.find(e => e.id === seedId);
  if (!seed) return {ids:[],limitation:'Select one object first.'};
  const key = mode === 'style' ? styleKey(seed) : canonicalSerialize([...seed.layerIds].sort());
  if (key === null) return {ids:[],limitation:'Groups do not have a comparable appearance. Select a member instead.'};
  const parents = parentMap(page.elements);
  return {ids:page.elements.filter(el => {
    const state = stateOf(page,el,parents);
    if (state.hidden || state.locked || el.kind === 'group') return false;
    return (mode === 'style' ? styleKey(el) : canonicalSerialize([...el.layerIds].sort())) === key;
  }).map(el=>el.id)};
}
