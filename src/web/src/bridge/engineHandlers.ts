import type { AppError, DiagramDocument, RequestEnvelope, ResponseEnvelope, Scope } from '../contracts/generated';
import type { CommandEngine } from '../commands/CommandEngine';
import { fail, ok, type Result } from '../model/result';
import { validateDocument, validateMutationRequest, validateScriptRequest, validateUndoRequest } from '../model/validate';
import { drawScriptCompiler } from '../script/compiler';
import type { Operation } from '../contracts/generated';
import { listAssets, readObjects, readSummary } from './readTools';

const toResponse = <T>(requestId: string, r: Result<T>): ResponseEnvelope =>
  r.ok ? { protocolVersion: 1, kind: 'response', requestId, result: r.value } : { protocolVersion: 1, kind: 'response', requestId, error: r.error as AppError };

export interface EngineHandlerOptions {
  /** Called after the engine starts a new session (document replaced), so the host can learn the new scope. */
  onSessionChanged?: (scope: Scope, revision: number) => void;
}

/**
 * Routes host-initiated doc.* requests to the engine. Scope arrives at the envelope top level and is merged into the
 * request here; the engine remains the authority on session, duplicate delivery and revision.
 */
export function createEngineHandler(engine: CommandEngine, opts: EngineHandlerOptions = {}) {
  return async (env: RequestEnvelope): Promise<ResponseEnvelope> => {
    const id = env.requestId;
    const scope = { documentId: env.documentId ?? '', sessionId: env.sessionId ?? '' };
    const p = env.params as Record<string, unknown>;
    const respond = <T>(r: Result<T>) => toResponse(id, r);

    switch (env.method) {
      case 'doc.snapshot':
        return respond(await engine.snapshot(scope));
      case 'doc.apply': {
        const req = validateMutationRequest({ ...p, ...scope });
        return respond(req.ok ? await engine.execute(req.value, 'mcp') : req);
      }
      case 'doc.executeScript': {
        const req = validateScriptRequest({ ...p, ...scope });
        if (!req.ok) return respond(req);
        // Show the model exactly what its script became. A retry answered from the cache never compiles, so it carries no operations.
        let operations: Operation[] | undefined;
        const r = await engine.executeScript(req.value, (src, snap, ctx) => {
          const out = drawScriptCompiler(src, snap, ctx);
          if (out.ok) operations = out.value.operations;
          return out;
        });
        return respond(r.ok ? ok({ transaction: r.value, ...(operations ? { operations } : {}) }) : r);
      }
      case 'doc.summary': {
        const s = await engine.snapshot(scope);
        if (!s.ok) return respond(s);
        const bool = (v: unknown) => (typeof v === 'boolean' ? v : undefined);
        return respond(readSummary(s.value, {
          ...(typeof p.pageId === 'string' ? { pageId: p.pageId } : {}),
          ...(bool(p.includeGeometry) !== undefined ? { includeGeometry: p.includeGeometry as boolean } : {}),
          ...(bool(p.includeStyle) !== undefined ? { includeStyle: p.includeStyle as boolean } : {}),
          ...(bool(p.includeHidden) !== undefined ? { includeHidden: p.includeHidden as boolean } : {}),
        }));
      }
      case 'doc.getObjects': {
        if (!Array.isArray(p.ids) || p.ids.length === 0 || p.ids.length > 500 || p.ids.some(i => typeof i !== 'string')) return respond(fail('invalid_request', 'ids must be 1 to 500 strings'));
        const s = await engine.snapshot(scope);
        if (!s.ok) return respond(s);
        const objects = readObjects(s.value, p.ids as string[], typeof p.pageId === 'string' ? p.pageId : undefined);
        return respond(objects.ok ? ok({ revision: s.value.revision, objects: objects.value }) : objects);
      }
      case 'assets.list': {
        const s = await engine.snapshot(scope);
        if (!s.ok) return respond(s);
        const tags = Array.isArray(p.tags) ? p.tags.filter((t): t is string => typeof t === 'string') : undefined;
        return respond(ok({ revision: s.value.revision, assets: listAssets(s.value, typeof p.query === 'string' ? p.query : undefined, tags) }));
      }
      case 'doc.render':
      case 'doc.inspectLayout':
        return respond(fail('not_ready', 'rendering and layout inspection arrive with the render tools (I31)', { retryable: false }));
      case 'doc.export':
        return respond(fail('not_ready', 'export arrives with the native file work (I40)', { retryable: false }));
      case 'doc.undo':
      case 'doc.redo': {
        const req = validateUndoRequest({ ...p, ...scope });
        if (!req.ok) return respond(req);
        return respond(env.method === 'doc.undo' ? await engine.undo(req.value) : await engine.redo(req.value));
      }
      case 'doc.changes': {
        if (typeof p.sinceRevision !== 'number' || !Number.isInteger(p.sinceRevision) || p.sinceRevision < 0) return respond(fail('invalid_request', 'sinceRevision must be a non-negative integer'));
        return respond(await engine.getChanges(scope, p.sinceRevision));
      }
      case 'doc.replace': {
        if (typeof p.baseRevision !== 'number' || !Number.isInteger(p.baseRevision)) return respond(fail('invalid_request', 'baseRevision must be an integer'));
        const doc = validateDocument(p.document);
        if (!doc.ok) return respond(doc);
        const r = await engine.replaceDocument(doc.value as DiagramDocument, { ...scope, baseRevision: p.baseRevision });
        if (r.ok) opts.onSessionChanged?.(engine.scope(), engine.revision);
        return respond(r);
      }
      case 'doc.markSaved': {
        if (typeof p.revision !== 'number' || !Number.isInteger(p.revision) || typeof p.path !== 'string') return respond(fail('invalid_request', 'revision and path are required'));
        const r = await engine.markSaved(scope, p.revision, p.path);
        return respond(r.ok ? ok({ revision: p.revision }) : r);
      }
      case 'doc.status': {
        const s = await engine.snapshot(scope);
        return respond(s.ok ? ok({ ...engine.status(), ...engine.scope(), revision: engine.revision }) : s);
      }
      default:
        return respond(fail('method_not_found', `unknown method ${env.method}`));
    }
  };
}
