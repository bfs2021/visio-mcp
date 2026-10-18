import type { DiagramDocument, ResolvedDiff } from '../contracts/generated';
import { applyDiff } from './diff';
import { fail, ok, type Result } from '../model/result';
import { validateDocument } from '../model/validate';

/** What the host hands over for one unfinished session: its last checkpoint and the resolved changes committed after it. */
export interface RecoveryPayload {
  sessionId: string;
  documentId: string;
  originalPath?: string | null;
  checkpointRevision: number;
  document: unknown;
  events: unknown[];
  lastDurableRevision: number;
}

export interface Restored {
  document: DiagramDocument;
  /** The revision the rebuilt document is at (the checkpoint's, plus every record that applied). */
  revision: number;
  applied: number;
  /** Set when records after `revision` could not be used; the person is told what was not recovered. */
  problem?: string;
}

const hasLists = (o: unknown, keys: string[]): boolean => !!o && typeof o === 'object' && keys.every(k => Array.isArray((o as Record<string, unknown>)[k]));

function asDiff(ev: unknown, documentId: string): Result<ResolvedDiff> {
  const e = ev as { documentId?: unknown; revision?: unknown; resolvedDiff?: Record<string, unknown> } | null;
  const d = e?.resolvedDiff;
  if (!e || !d || typeof d !== 'object') return fail('invalid_request', 'a recovery record has no resolved diff');
  if (e.documentId !== documentId) return fail('document_mismatch', 'a recovery record belongs to a different document');
  if (!Number.isInteger(d.previousRevision) || !Number.isInteger(d.revision) || e.revision !== d.revision) return fail('invalid_request', 'a recovery record has inconsistent revisions');
  if (!hasLists(d.upserts, ['elements', 'pages', 'layers', 'assets']) || !hasLists(d.deletes, ['elementIds', 'pageIds', 'layerIds', 'assetIds'])) return fail('invalid_request', 'a recovery record is not a resolved diff');
  return ok(d as unknown as ResolvedDiff);
}

/**
 * Rebuilds the last durable state: the checkpoint, then each record in order, exactly as it was committed (generated ids, undo and redo
 * effects included, so nothing is replayed as a new action). Everything is validated into a candidate; nothing is published here. If a
 * record cannot be applied the result stops before it and says why, so the loss can be reported rather than hidden.
 */
export function restoreRecovery(candidate: RecoveryPayload): Result<Restored> {
  const base = validateDocument(candidate.document);
  if (!base.ok) return fail('invalid_request', `the recovery checkpoint is not a valid document: ${base.error.message}`);
  let doc = base.value;
  if (doc.id !== candidate.documentId) return fail('document_mismatch', 'the recovery checkpoint is for a different document');
  let revision = candidate.checkpointRevision;
  let applied = 0;
  let problem: string | undefined;
  for (const ev of candidate.events) {
    const d = asDiff(ev, candidate.documentId);
    if (!d.ok) { problem = d.error.message; break; }
    if (d.value.previousRevision !== revision || d.value.revision !== revision + 1) {
      problem = `revision ${d.value.revision} does not follow revision ${revision}`;
      break;
    }
    let next: DiagramDocument;
    try { next = applyDiff(doc, d.value, d.value.revision); } catch (e) { problem = `revision ${d.value.revision} could not be applied: ${e instanceof Error ? e.message : String(e)}`; break; }
    const valid = validateDocument(next);
    if (!valid.ok) { problem = `revision ${d.value.revision} would make an invalid document: ${valid.error.message}`; break; }
    doc = valid.value;
    revision = d.value.revision;
    applied++;
  }
  return ok({ document: doc, revision, applied, ...(problem ? { problem } : {}) });
}
