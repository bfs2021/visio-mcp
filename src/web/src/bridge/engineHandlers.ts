import type { AppError, DiagramDocument, RequestEnvelope, ResponseEnvelope, Scope } from '../contracts/generated';
import type { CommandEngine } from '../commands/CommandEngine';
import { fail, ok, type Result } from '../model/result';
import { validateDocument, validateMutationRequest, validateScriptRequest, validateUndoRequest } from '../model/validate';
import { drawScriptCompiler } from '../script/compiler';
import type { Operation } from '../contracts/generated';
import { listAssets, readObjects, readSummary } from './readTools';
import { inspectLayout, type LayoutContext } from '../layout/inspectLayout';
import { getTextLayout } from '../layout/textLayout';
import type { Projection, Snapshot } from '../contracts/generated';
import type { RenderParams, RenderResult } from '../render/SnapshotRenderer';
import { iconCatalog, iconInfo, iconOperations, searchIcons } from '../icons/catalog';
import { iconPreview } from '../icons/preview';

const toResponse = <T>(requestId: string, r: Result<T>): ResponseEnvelope =>
  r.ok ? { protocolVersion: 1, kind: 'response', requestId, result: r.value } : { protocolVersion: 1, kind: 'response', requestId, error: r.error as AppError };

export interface EngineHandlerOptions {
  /** Called after the engine starts a new session (document replaced), so the host can learn the new scope. */
  onSessionChanged?: (scope: Scope, revision: number) => void;
  /** Rasterises a committed snapshot. Only the page has a drawing surface; without it, render answers not_ready. */
  render?: (snapshot: Snapshot, projection: Projection | undefined, params: RenderParams) => Promise<Result<RenderResult>>;
  /** Turns an approved asset (by hash) into a PNG, for formats that cannot hold the original (an SVG picture inside a Visio file). */
  rasterise?: (sha256: string, maxPixels: number) => Promise<Result<{ base64: string; widthPx: number; heightPx: number }>>;
  /** Text measurement and picture sizes for layout inspection, supplied by the page. */
  layoutContext?: (snapshot: Snapshot, options?: { includeAssets?: boolean }) => Promise<Pick<LayoutContext, 'measureWidth' | 'assetSize'>>;
}

/** Used when the page cannot measure text: a rough average advance, and the report says its sizes are estimates either way. */
const approximateWidth: LayoutContext['measureWidth'] = (text, font) => text.length * font.sizePt * 0.5;

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
      case 'icons.search':
      case 'icons.get': {
        const snap = await engine.snapshot(scope); if (!snap.ok) return respond(snap);
        if (!opts.render) return respond(fail('not_ready', 'icon previews require the editor drawing surface'));
        let icons: typeof iconCatalog.icons;
        let metadata: Record<string, unknown>;
        let colour = '#334155', strokeRatio = 1 / 12;
        if (env.method === 'icons.search') {
          const found = searchIcons(p.query ?? '', p.limit ?? 24); if (!found.ok) return respond(found);
          icons = found.value.icons;
          metadata = { icons: icons.map(iconInfo), total: found.value.total, hasMore: found.value.total > icons.length };
        } else {
          if (!snap.value.document.pages.some(page => page.id === p.pageId)) return respond(fail('not_found', 'pageId is not in the open document'));
          const prepared = iconOperations(p); if (!prepared.ok) return respond(prepared);
          const v = prepared.value; icons = [v.icon]; colour = v.colour; strokeRatio = v.strokeWidthPt / v.sizePt;
          metadata = { icon: { ...iconInfo(v.icon), geometry: { svgPath: v.icon.path } }, elementId: v.elementId, operations: v.operations,
            baseRevision: snap.value.revision, destinationPageId: p.pageId,
            guidance: 'Apply these operations with the returned document/session identity and baseRevision, using a new transactionId. This preview changed nothing.' };
        }
        const scene = iconPreview(snap.value, icons, { colour, strokeRatio });
        const image = await opts.render(scene, undefined, { scope: 'page', pageId: scene.document.pages[0]!.id, maxWidth: 1200, maxHeight: 1200, format: 'png' });
        return respond(image.ok ? ok({ ...image.value, ...metadata, preview: true, previewOf: 'icon_catalog', catalogVersion: iconCatalog.version,
          sourceVersion: iconCatalog.sourceVersion, sourceCommit: iconCatalog.sourceCommit, licence: iconCatalog.licence }) : image);
      }
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
      case 'doc.exportSnapshot': {
        // The barrier read export and save use: committed state at one revision plus the derived routes at that same revision.
        const snap = await engine.exportSnapshot(scope);
        return respond(snap);
      }
      case 'assets.rasterise': {
        if (!opts.rasterise) return respond(fail('not_ready', 'this editor has no drawing surface to rasterise with'));
        if (typeof p.sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(p.sha256)) return respond(fail('invalid_request', 'sha256 must be a 64 character hex string'));
        return respond(await opts.rasterise(p.sha256, typeof p.maxPixels === 'number' ? Math.min(4096, Math.max(16, p.maxPixels)) : 1024));
      }
      case 'doc.render': {
        if (!opts.render) return respond(fail('not_ready', 'this editor has no drawing surface to render with', { retryable: false }));
        if (p.scope !== 'page' && p.scope !== 'region') return respond(fail('invalid_request', 'scope must be page or region'));
        // A barrier read: the picture shows the revision committed at this point, however far the live document moves while it is drawn.
        const snap = await engine.exportSnapshot(scope);
        if (!snap.ok) return respond(snap);
        return respond(await opts.render(snap.value as Snapshot, snap.value.projection, p as unknown as RenderParams));
      }
      case 'doc.textLayout': {
        const snap = await engine.snapshot(scope);
        if (!snap.ok) return respond(snap);
        if (!opts.layoutContext) return respond(fail('not_ready', 'The editor font measurement context is not available'));
        const measured = await opts.layoutContext(snap.value, { includeAssets: false });
        return respond(getTextLayout(snap.value, measured, p));
      }
      case 'doc.inspectLayout': {
        let snap: Result<Snapshot & { projection?: Projection }> = await engine.exportSnapshot(scope);
        // A host with no drawing surface has no resolved routes; connectors are then judged by straight lines, and the report says so.
        if (!snap.ok && snap.error.code === 'projection_failed') snap = await engine.snapshot(scope);
        if (!snap.ok) return respond(snap);
        const measured = opts.layoutContext ? await opts.layoutContext(snap.value as Snapshot) : { measureWidth: approximateWidth };
        const report = inspectLayout(snap.value as Snapshot, { ...measured, ...(snap.value.projection ? { projection: snap.value.projection } : {}) }, typeof p.pageId === 'string' ? { pageId: p.pageId } : {});
        return respond(ok({ documentId: snap.value.documentId, sessionId: snap.value.sessionId, revision: snap.value.revision, issues: report.issues, notes: report.notes }));
      }
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
