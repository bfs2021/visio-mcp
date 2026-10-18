import type { ComponentProposal } from './layout/components';
import '@maxgraph/core/css/common.css';
import './styles.css';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { startApp } from './bridge/bootstrap';
import { EditorModel, noHost, type HostApi } from './editor/EditorModel';
import type { HostServices } from './bridge/hostServices';
import { createSnapshotRenderer } from './render/SnapshotRenderer';
import { createBrowserLayoutContext } from './layout/browserContext';

function hostApi(host: HostServices | null): HostApi {
  if (!host) return noHost;
  return {
    pickImage: () => host.call('host.pickImage'),
    openJson: () => host.call('host.openJson'),
    saveJson: saveAs => host.call('host.saveJson', { saveAs }),
    recoveryStatus: () => host.call('host.recovery.status'),
    recoveryList: () => host.call('host.recovery.list'),
    recoveryFetch: sessionId => host.call('host.recovery.fetch', { sessionId }),
    recoveryDiscard: sessionId => host.call('host.recovery.discard', { sessionId }),
    recoveryFinish: (sessionId, originalPath) => host.call('host.recovery.finish', { sessionId, originalPath }),
    appClose: discard => host.call('host.app.close', { discard }),
    appCloseCancelled: () => host.call('host.app.closeCancelled'),
    libraryList: (query, tags) => host.call('host.library.list', { query, tags }),
    libraryImport: () => host.call('host.library.import'),
    libraryUse: (id, hash) => host.call('host.library.use', { id, hash }),
    libraryReplaceSource: id => host.call('host.library.replaceSource', { id }),
    librarySetMetadata: (id, patch) => host.call('host.library.setMetadata', { id, ...patch }),
    libraryDelete: id => host.call('host.library.delete', { id }),
    openVsdx: () => host.call('host.openVsdx'),
    exportVsdx: opts => host.call('host.exportVsdx', opts),
  };
}

let model: EditorModel | null = null;
const assetUrl = (snapshot: { document: { assets: Array<{ id: string; sha256: string }> } }, id: string) => {
  const a = snapshot.document.assets.find(x => x.id === id);
  return a ? `/assets/${a.sha256}` : '';
};
const renderer = createSnapshotRenderer({ assetUrl: assetUrl as never });
const { engine, host, router, sessionChanged } = startApp(window.chrome?.webview, null, { onSessionChanged: () => model?.sessionReplaced(), render: renderer.render, rasterise: renderer.rasterise, layoutContext: createBrowserLayoutContext(assetUrl as never), beforeClose: async () => model!.beginClose() });
model = new EditorModel(engine);
model.host = hostApi(host);
if (host) model.newDocument.create = (document, expected, discard) => host.call('host.newDocument', { document, expected, discard });
model.proposeComponent = async params => {
  if (!router) return { ok: false, error: { code: 'not_ready', message: 'The editor bridge is unavailable', retryable: false } };
  const r = await router.dispatch({ protocolVersion: 1, kind: 'request', requestId: crypto.randomUUID(), ...engine.scope(), method: 'components.get', params } as never);
  return r.error ? { ok: false, error: r.error } : { ok: true, value: r.result as unknown as ComponentProposal & { base64: string; mimeType: string } };
};
model.afterRecoveryRestore = sessionChanged;
model.renderPreview = async doc => {
  const r = await renderer.render({ documentId: doc.id, sessionId: doc.id, revision: 0, document: doc }, undefined, { scope: 'page', pageId: doc.pages[0]!.id, maxWidth: 280, maxHeight: 200, format: 'png' });
  return r.ok ? `data:${r.value.mimeType};base64,${r.value.base64}` : null;
};
void model.checkRecovery(); // unfinished work from an earlier run is offered (or, after a renderer crash, restored at once)
if (router) model.onGestureState = active => router.setGestureActive(active); // an outside mutation waits (briefly) while the user is mid-gesture

// A read-only window onto the editor for automated tests. It is compiled out of production builds, and nothing here edits the document.
if (import.meta.env.VITE_TEST_API === '1') {
  const m = model;
  const recorded: unknown[] = [];
  engine.onCommitted(e => recorded.push(JSON.parse(JSON.stringify(e))));
  (window as unknown as { __editor: unknown }).__editor = {
    committedEvents: () => JSON.parse(JSON.stringify(recorded)),
    checkRecovery: () => m.checkRecovery(),
    beginClose: () => m.beginClose(),
    markSaved: async (path: string) => { await m.engine.markSaved(m.engine.scope(), m.engine.revision, path); m.say(`Saved ${path}.`, 'info'); },
    snapshot: () => JSON.parse(JSON.stringify(m.snapshot)),
    selection: () => [...m.selection],
    status: () => ({ ...m.status, ...m.engine.status() }),
    centre: (id: string) => m.canvas!.centerOf(id),
    handle: (id: string, which: 'rotate' | 'se' | 'bend') => m.canvas!.handleOf(id, which),
    sheetRect: () => m.canvas!.sheetRect(),
    viewport: () => m.viewport,
    textEdit: (id: string, text: string | null) => {
      const g = m.canvas!.graph;
      const cell = g.getDataModel().getCell(id)!;
      g.startEditingAtCell(cell);
      const ed = g.getPlugin('CellEditorHandler') as unknown as { textarea: HTMLElement };
      if (text !== null) ed.textarea.innerText = text;
      g.stopEditing(text === null);
    },
    callTool: (method: string, params: Record<string, unknown>) => router!.dispatch({ protocolVersion: 1, kind: 'request', requestId: crypto.randomUUID(), method, ...m.engine.scope(), params } as never),
    setHost: (h: Partial<HostApi>) => { m.host = { ...noHost, ...h }; },
    exportSnapshot: async () => { const r = await m.engine.exportSnapshot(m.engine.scope()); return r.ok ? JSON.parse(JSON.stringify(r.value)) : { error: r.error }; },
    changesSince: async (revision: number) => { const r = await m.engine.getChanges(m.engine.scope(), revision); return r.ok ? JSON.parse(JSON.stringify(r.value)) : { error: r.error }; },
    targets: () => [...m.targets],
    gestureActive: () => m.controller.isActive(),
    agentApply: (operations: unknown[], baseRevision?: number) => m.engine.execute({
      ...m.engine.scope(), transactionId: crypto.randomUUID(), baseRevision: baseRevision ?? m.engine.revision, atomic: true, operations,
    } as never, 'mcp'),
    nodeCount: () => m.canvas!.graph.getDataModel().getCell(m.currentPage.id) ? 0 : (m.canvas!.graph.getDefaultParent().getChildCount()),
  };
}

createRoot(document.getElementById('root')!).render(<App model={model} />);

