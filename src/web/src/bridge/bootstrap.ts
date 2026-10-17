import { CommandEngine } from '../commands/CommandEngine';
import { newDocument } from '../model/newDocument';
import { BridgeClient, type WebViewLike } from './BridgeClient';
import { createEngineHandler } from './engineHandlers';
import { createHostServices } from './hostServices';

declare global {
  interface Window { chrome?: { webview?: WebViewLike } }
}

/**
 * Creates the engine over a new A4 document, answers host requests, mirrors committed changes to the host and announces readiness.
 * `editor.ready` is sent only after the handler is registered, so the host never sees a ready editor that cannot answer.
 */
export function startApp(webview: WebViewLike | undefined, root: HTMLElement | null = typeof document !== 'undefined' ? document.getElementById('status') : null, hooks: { onSessionChanged?: () => void } = {}) {
  const engine = new CommandEngine({ document: newDocument() });
  const show = () => {
    if (typeof document !== 'undefined') document.body.dataset.revision = String(engine.revision); // a visible, testable mirror of the revision
    if (!root) return;
    const page = engine.store.snapshot().document.pages[0]!;
    root.textContent = `Ready · revision ${engine.revision} · ${page.widthPt.toFixed(1)} × ${page.heightPt.toFixed(1)} pt`;
  };
  show();
  engine.onCommitted(show);

  if (!webview) return { engine, client: null, host: null };
  const client = new BridgeClient(webview);
  const host = createHostServices(client);
  const announce = (name: 'editor.ready' | 'editor.session') =>
    client.sendEvent(name, { protocolVersion: 1, ...engine.scope(), revision: engine.revision });

  client.onRequest(createEngineHandler(engine, { onSessionChanged: () => { show(); announce('editor.session'); hooks.onSessionChanged?.(); } }));
  engine.onCommitted(e => client.sendEvent('editor.committed', e as unknown as Record<string, unknown>));
  announce('editor.ready');
  return { engine, client, host };
}
