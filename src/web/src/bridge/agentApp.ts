import type { AppPreferences } from '../contracts/generated';
import { PreferencesModel } from '../preferences/PreferencesModel';
import { CommandEngine } from '../commands/CommandEngine';
import { fail, ok } from '../model/result';
import { newDocument } from '../model/newDocument';
import { toPoints } from '../model/validate';
import { Workspace, type DocumentState } from '../workspace/Workspace';
import { BridgeClient, type WebViewLike } from './BridgeClient';
import { createRequestRouter } from './RequestRouter';
import type { EngineHandlerOptions } from './engineHandlers';

/**
 * The page behind the agent-only view. It has the engine, the request router and the renderer, and nothing else: no editor chrome, no tabs,
 * no host services. Documents an agent creates live here and are announced to the host as they open and close. Handing one to the person's
 * window is the host's doing; this page only answers `app.createDocument` and `app.dropDocument`.
 */
export function startAgentApp(webview: WebViewLike | undefined, hooks: {
  initialPreferences?: AppPreferences; preferences?: PreferencesModel; client?: BridgeClient;
  render?: EngineHandlerOptions['render']; layoutContext?: EngineHandlerOptions['layoutContext']; rasterise?: EngineHandlerOptions['rasterise'];
  /** Gives a new engine what only the page can supply (the snapshot projector that resolves routes for exports). */
  configureEngine?: (engine: CommandEngine) => void;
} = {}) {
  const preferences = hooks.preferences ?? new PreferencesModel(hooks.initialPreferences);
  const workspace = new Workspace();

  const createDocument: NonNullable<NonNullable<EngineHandlerOptions['app']>['createDocument']> = params => {
    const doc = newDocument(undefined, undefined, preferences.snapshotDefaults());
    if (params.title !== undefined) {
      if (typeof params.title !== 'string' || params.title.length === 0 || params.title.length > 200) return fail('invalid_request', 'title must be 1 to 200 characters');
      doc.title = params.title;
    }
    const w = params.widthPt, h = params.heightPt;
    if (w !== undefined || h !== undefined) {
      if (typeof w !== 'number' || typeof h !== 'number' || !(w >= 36 && w <= 14400 && h >= 36 && h <= 14400)) return fail('invalid_request', 'widthPt and heightPt must both be numbers from 36 to 14400');
      doc.pages[0]!.widthPt = w; doc.pages[0]!.heightPt = h;
    } else if (params.page === 'A4-portrait') {
      doc.pages[0]!.widthPt = toPoints(210, 'mm'); doc.pages[0]!.heightPt = toPoints(297, 'mm');
    }
    const engine = new CommandEngine({ document: doc });
    hooks.configureEngine?.(engine);
    const state = workspace.add(engine, { activate: false });
    return ok({ ...state.engine.scope(), revision: engine.revision, title: doc.title, pageId: doc.pages[0]!.id });
  };
  const dropDocument: NonNullable<NonNullable<EngineHandlerOptions['app']>['dropDocument']> = documentId =>
    workspace.close(documentId) ? ok({ dropped: documentId }) : fail('document_mismatch', 'that document is not in this view');

  const router = createRequestRouter(workspace.engineOf, {
    ...(hooks.render ? { render: hooks.render } : {}), ...(hooks.layoutContext ? { layoutContext: hooks.layoutContext } : {}), ...(hooks.rasterise ? { rasterise: hooks.rasterise } : {}),
    app: { createDocument, dropDocument },
  });
  if (!webview) return { preferences, workspace, router, client: null };
  const client = hooks.client ?? new BridgeClient(webview);
  client.onRequest(router.dispatch);
  const mirror = (state: DocumentState) => state.engine.onCommitted(e => client.sendEvent('editor.committed', e as unknown as Record<string, unknown>));
  workspace.subscribe(e => {
    if (e.kind === 'opened') {
      mirror(e.state);
      client.sendEvent('editor.document.opened', { protocolVersion: 1, ...e.state.engine.scope(), revision: e.state.engine.revision, active: false });
    } else if (e.kind === 'closed') client.sendEvent('editor.document.closed', { protocolVersion: 1, documentId: e.documentId });
  });
  client.sendEvent('editor.ready', { protocolVersion: 1 }); // no document yet: the page can answer
  return { preferences, workspace, router, client };
}
