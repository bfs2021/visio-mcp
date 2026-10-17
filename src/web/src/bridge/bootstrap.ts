import { CommandEngine } from '../commands/CommandEngine';
import { newDocument } from '../model/newDocument';
import { BridgeClient, type WebViewLike } from './BridgeClient';
import { createRequestRouter } from './RequestRouter';
import type { EngineHandlerOptions } from './engineHandlers';
import { createHostServices } from './hostServices';

declare global {
  interface Window { chrome?: { webview?: WebViewLike } }
}

/**
 * Creates the engine over a new A4 document, answers host requests, mirrors committed changes to the host and announces readiness.
 * `editor.ready` is sent only after the handler is registered, so the host never sees a ready editor that cannot answer.
 */
export function startApp(webview: WebViewLike | undefined, root: HTMLElement | null = typeof document !== 'undefined' ? document.getElementById('status') : null, hooks: { onSessionChanged?: () => void; render?: EngineHandlerOptions['render']; layoutContext?: EngineHandlerOptions['layoutContext']; rasterise?: EngineHandlerOptions['rasterise'] } = {}) {
  const engine = new CommandEngine({ document: newDocument() });
  const show = () => {
    if (typeof document !== 'undefined') document.body.dataset.revision = String(engine.revision); // a visible, testable mirror of the revision
    if (!root) return;
    const page = engine.store.snapshot().document.pages[0]!;
    root.textContent = `Ready · revision ${engine.revision} · ${page.widthPt.toFixed(1)} × ${page.heightPt.toFixed(1)} pt`;
  };
  show();
  engine.onCommitted(show);

  let client: BridgeClient | null = null;
  const announce = (name: 'editor.ready' | 'editor.session') => client?.sendEvent(name, { protocolVersion: 1, ...engine.scope(), revision: engine.revision });
  // The router exists with or without a host, so a plain browser (tests, development) can exercise the same request path.
  const router = createRequestRouter(engine, { ...(hooks.render ? { render: hooks.render } : {}), ...(hooks.layoutContext ? { layoutContext: hooks.layoutContext } : {}), ...(hooks.rasterise ? { rasterise: hooks.rasterise } : {}), onSessionChanged: () => { router.cancelGesture(); show(); announce('editor.session'); hooks.onSessionChanged?.(); } });
  if (!webview) return { engine, client: null, host: null, router };
  client = new BridgeClient(webview);
  const host = createHostServices(client);
  client.onRequest(router.dispatch);
  const sender = client;
  engine.onCommitted(e => sender.sendEvent('editor.committed', e as unknown as Record<string, unknown>));
  announce('editor.ready');
  return { engine, client, host, router };
}
