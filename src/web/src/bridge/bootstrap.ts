import { CommandEngine } from '../commands/CommandEngine';
import { newDocument } from '../model/newDocument';
import { Workspace, type DocumentState } from '../workspace/Workspace';
import { BridgeClient, type WebViewLike } from './BridgeClient';
import { createRequestRouter } from './RequestRouter';
import type { EngineHandlerOptions } from './engineHandlers';
import { createHostServices } from './hostServices';

declare global {
  interface Window { chrome?: { webview?: WebViewLike } }
}

/**
 * Creates the workspace with one engine over a new A4 document, answers host requests, mirrors committed changes to the host and
 * announces readiness. `editor.ready` is sent only after the handler is registered, so the host never sees a ready editor that cannot answer.
 * Documents opened or closed later are announced to the host as they happen, so it always knows which are open and which is active.
 */
export function startApp(webview: WebViewLike | undefined, root: HTMLElement | null = typeof document !== 'undefined' ? document.getElementById('status') : null, hooks: { onSessionChanged?: (state: DocumentState) => void; render?: EngineHandlerOptions['render']; layoutContext?: EngineHandlerOptions['layoutContext']; rasterise?: EngineHandlerOptions['rasterise']; beforeClose?: EngineHandlerOptions['beforeClose']; app?: EngineHandlerOptions['app'] } = {}) {
  const workspace = new Workspace();
  const first = workspace.add(new CommandEngine({ document: newDocument() }));
  const engine = first.engine;
  const show = () => {
    const active = workspace.active?.engine ?? engine;
    if (typeof document !== 'undefined') document.body.dataset.revision = String(active.revision); // a visible, testable mirror of the revision
    if (!root) return;
    const page = active.store.snapshot().document.pages[0]!;
    root.textContent = `Ready · revision ${active.revision} · ${page.widthPt.toFixed(1)} × ${page.heightPt.toFixed(1)} pt`;
  };
  show();
  engine.onCommitted(show);

  let client: BridgeClient | null = null;
  const announce = (name: 'editor.ready' | 'editor.session' | 'editor.document.opened', state: DocumentState, extra: Record<string, unknown> = {}) =>
    client?.sendEvent(name, { protocolVersion: 1, ...state.engine.scope(), revision: state.engine.revision, active: workspace.active === state, ...extra });
  // The one place that reacts to a replaced document, whether the host asked for it (open) or the page did (recovery).
  const sessionChanged = (state: DocumentState = workspace.active ?? first) => {
    const replaces = workspace.restarted(state);
    router.cancelGesture(); show(); announce('editor.session', state, { replaces }); hooks.onSessionChanged?.(state);
  };
  // The router exists with or without a host, so a plain browser (tests, development) can exercise the same request path.
  const router = createRequestRouter(workspace.engineOf, {
    ...(hooks.render ? { render: hooks.render } : {}), ...(hooks.layoutContext ? { layoutContext: hooks.layoutContext } : {}), ...(hooks.rasterise ? { rasterise: hooks.rasterise } : {}), ...(hooks.beforeClose ? { beforeClose: hooks.beforeClose } : {}), ...(hooks.app ? { app: hooks.app } : {}),
    onSessionChanged: (_scope, _revision, e) => { const s = workspace.stateOfEngine(e); if (s) sessionChanged(s); },
    isActive: workspace.isActive,
  });

  /** Opens another document in the workspace (an agent's, in the background, or the person's own) and tells the host. */
  const openDocument = (opts: { activate?: boolean; document?: ConstructorParameters<typeof CommandEngine>[0] } = {}): DocumentState => {
    const state = workspace.add(new CommandEngine(opts.document ?? { document: newDocument() }), opts.activate === undefined ? {} : { activate: opts.activate });
    state.engine.onCommitted(show);
    return state;
  };
  const closeDocument = (documentId: string) => workspace.close(documentId);
  const activateDocument = (documentId: string) => workspace.activate(documentId);

  if (!webview) return { engine, client: null, host: null, router, sessionChanged, workspace, openDocument, closeDocument, activateDocument };
  client = new BridgeClient(webview);
  const host = createHostServices(client);
  client.onRequest(router.dispatch);
  const sender = client;
  const mirror = (state: DocumentState) => state.engine.onCommitted(e => sender.sendEvent('editor.committed', e as unknown as Record<string, unknown>));
  mirror(first);
  workspace.subscribe(e => {
    if (e.kind === 'opened') { mirror(e.state); announce('editor.document.opened', e.state); }
    else if (e.kind === 'closed') sender.sendEvent('editor.document.closed', { protocolVersion: 1, documentId: e.documentId });
    else if (e.kind === 'activated') sender.sendEvent('editor.document.activated', { protocolVersion: 1, documentId: e.state.engine.scope().documentId });
  });
  announce('editor.ready', first);
  return { engine, client, host, router, sessionChanged, workspace, openDocument, closeDocument, activateDocument };
}
