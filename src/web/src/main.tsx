import '@maxgraph/core/css/common.css';
import './styles.css';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { startApp } from './bridge/bootstrap';
import { EditorModel, noHost, type HostApi } from './editor/EditorModel';
import type { HostServices } from './bridge/hostServices';

function hostApi(host: HostServices | null): HostApi {
  if (!host) return noHost;
  return {
    pickImage: () => host.call('host.pickImage'),
    openJson: () => host.call('host.openJson'),
    saveJson: saveAs => host.call('host.saveJson', { saveAs }),
  };
}

let model: EditorModel | null = null;
const { engine, host } = startApp(window.chrome?.webview, null, { onSessionChanged: () => model?.sessionReplaced() });
model = new EditorModel(engine);
model.host = hostApi(host);

// A read-only window onto the editor for automated tests. It is compiled out of production builds, and nothing here edits the document.
if (import.meta.env.VITE_TEST_API === '1') {
  const m = model;
  (window as unknown as { __editor: unknown }).__editor = {
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
