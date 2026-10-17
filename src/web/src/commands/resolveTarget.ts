import type { DiagramDocument, ErrorCode, Page, Snapshot } from '../contracts/generated';
import { fail, ok, type Result } from '../model/result';

const UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

export type Lookup =
  | { ok: true; pageIdx: number; index: number }
  | { ok: false; code: ErrorCode; message: string; details: Record<string, unknown> };

const miss = (code: ErrorCode, message: string, details: Record<string, unknown> = {}): Lookup => ({ ok: false, code, message, details });

/**
 * UUID first, then an exact page-scoped alias, then an exact display name. A UUID needs no page scope; anything else does.
 * A name shared by several elements is ambiguous and reports every candidate: a quoted string never authorises guessing.
 */
export function lookupTarget(pages: readonly Page[], ref: string, scopePageId: string | undefined): Lookup {
  if (UUID.test(ref)) {
    for (let pageIdx = 0; pageIdx < pages.length; pageIdx++) {
      const index = pages[pageIdx]!.elements.findIndex(e => e.id === ref);
      if (index >= 0) return { ok: true, pageIdx, index };
    }
    return miss('not_found', `no element ${ref}`, { target: ref });
  }
  if (!scopePageId) return miss('invalid_request', `"${ref}" is not a UUID and the request has no page scope`, { target: ref });
  const pageIdx = pages.findIndex(p => p.id === scopePageId);
  if (pageIdx < 0) return miss('not_found', `page ${scopePageId} does not exist`);
  const page = pages[pageIdx]!;
  const byAlias = page.elements.findIndex(e => e.alias === ref);
  if (byAlias >= 0) return { ok: true, pageIdx, index: byAlias };
  const named = page.elements.map((e, i) => ({ e, i })).filter(x => x.e.name === ref);
  if (named.length === 1) return { ok: true, pageIdx, index: named[0]!.i };
  if (named.length > 1) return miss('ambiguous_target', `"${ref}" matches ${named.length} elements`, { target: ref, candidates: named.map(x => x.e.id) });
  return miss('not_found', `nothing is called "${ref}"`, { target: ref });
}

/** The page a request without an explicit pageId means: the only page of a one-page document. */
export const defaultScope = (doc: DiagramDocument): string | undefined => (doc.pages.length === 1 ? doc.pages[0]!.id : undefined);

/** Resolves a target to the UUID of the element it names, against a committed snapshot. */
export function resolveTarget(snapshot: Snapshot, pageId: string | undefined, target: string): Result<string> {
  const doc = snapshot.document;
  const r = lookupTarget(doc.pages, target, pageId ?? defaultScope(doc));
  if (!r.ok) return fail(r.code, r.message, { details: r.details });
  return ok(doc.pages[r.pageIdx]!.elements[r.index]!.id);
}
