import { useState } from 'react';
import type { Unit } from '../model/validate';
import type { EditorModel } from '../editor/EditorModel';
import { arrangeActions } from '../editor/arrangeActions';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';
import { useModel } from './useModel';

type MenuItem = { label: string; shortcut?: string; run: () => void; disabled?: boolean; checked?: boolean; id: string };

/** The Arrange menu is the shared action list, with a separator between its sections. */
function arrangeMenu(model: EditorModel): Array<MenuItem | 'sep'> {
  const items: Array<MenuItem | 'sep'> = [];
  let section = '';
  for (const a of arrangeActions(model)) {
    if (section && a.section !== section) items.push('sep');
    section = a.section;
    items.push({ id: a.id, label: a.label, run: a.run, disabled: a.disabled, ...(a.shortcut ? { shortcut: a.shortcut } : {}) });
  }
  return items;
}

export function Toolbar({ model, ws }: { model: EditorModel; ws: WorkspaceModel }) {
  useModel(model);
  const [open, setOpen] = useState<string | null>(null);
  const hasSelection = model.selection.length > 0;
  const page = model.currentPage;
  const status = model.engine.status();

  const menus: Array<{ name: string; items: Array<MenuItem | 'sep'> }> = [
    { name: 'File', items: [
      { id: 'new', label: 'New…', shortcut: 'Ctrl+N', run: () => model.newDocument.begin() },
      { id: 'open', label: 'Open…', shortcut: 'Ctrl+O', run: () => void ws.openFile() },
      { id: 'save', label: 'Save', shortcut: 'Ctrl+S', run: () => void model.save(false) },
      { id: 'save-as', label: 'Save as…', shortcut: 'Ctrl+Shift+S', run: () => void model.save(true) },
      'sep',
      { id: 'open-visio', label: 'Open Visio drawing…', run: () => void ws.openFile('vsdx') },
      { id: 'close-tab', label: 'Close tab', shortcut: 'Ctrl+W', run: () => void ws.closeTab(model) },
      { id: 'export-visio', label: 'Export to Visio (.vsdx)…', run: () => void model.exportVsdx() },
    ] },
    { name: 'Edit', items: [
      { id: 'undo', label: 'Undo', shortcut: 'Ctrl+Z', run: () => void model.undo() },
      { id: 'redo', label: 'Redo', shortcut: 'Ctrl+Y', run: () => void model.redo() },
      'sep',
      { id: 'copy', label: 'Copy', shortcut: 'Ctrl+C', run: () => model.copy(), disabled: !hasSelection },
      { id: 'paste', label: 'Paste', shortcut: 'Ctrl+V', run: () => void model.paste() },
      { id: 'duplicate', label: 'Duplicate', shortcut: 'Ctrl+D', run: () => void model.duplicate(), disabled: !hasSelection },
      { id: 'delete', label: 'Delete', shortcut: 'Del', run: () => void model.deleteSelection(), disabled: !hasSelection },
      'sep',
      { id: 'select-all', label: 'Select all', shortcut: 'Ctrl+A', run: () => model.selectAll() },
      'sep',
      { id: 'preferences', label: 'Preferences…', run: () => void ws.openPreferences() },
    ] },
    { name: 'Arrange', items: arrangeMenu(model) },
    { name: 'View', items: [
      { id: 'zoom-in', label: 'Zoom in', shortcut: 'Ctrl++', run: () => model.zoomBy(1.25) },
      { id: 'zoom-out', label: 'Zoom out', shortcut: 'Ctrl+-', run: () => model.zoomBy(0.8) },
      { id: 'zoom-fit', label: 'Fit page', shortcut: 'Ctrl+0', run: () => model.zoomToFit() },
      { id: 'zoom-100', label: 'Actual size', run: () => model.zoomReset() },
      'sep',
      { id: 'grid', label: 'Show grid', checked: page.grid.visible, run: () => void model.toggleGrid('visible') },
      { id: 'snap', label: 'Snap to grid', checked: page.grid.snap, run: () => void model.toggleGrid('snap') },
      'sep',
      { id: 'script-drawer', label: 'Script drawer', checked: model.drawerOpen && model.drawerTab === 'script', run: () => model.toggleDrawer('script') },
      { id: 'transaction-drawer', label: 'Transactions', checked: model.drawerOpen && model.drawerTab === 'transactions', run: () => model.toggleDrawer('transactions') },
    ] },
  ];

  const tool = (id: string, label: string, active: boolean, run: () => void, disabled = false) => (
    <button type="button" className={`tool${active ? ' active' : ''}`} data-testid={`tool-${id}`} title={label} aria-pressed={active} disabled={disabled} onClick={run}>{label}</button>
  );

  return (
    <header className="toolbar" onMouseLeave={() => setOpen(null)}>
      <nav className="menubar" aria-label="Menu">
        {menus.map(m => (
          <div key={m.name} className="menu">
            <button type="button" className="menu-title" data-testid={`menu-${m.name.toLowerCase()}`} aria-expanded={open === m.name} onClick={() => setOpen(open === m.name ? null : m.name)}>{m.name}</button>
            {open === m.name && (
              <div className="menu-list" role="menu">
                {m.items.map((it, i) => it === 'sep' ? <hr key={i} /> : (
                  <button key={it.id} type="button" role="menuitem" data-testid={`menu-${it.id}`} disabled={it.disabled}
                    onClick={() => { setOpen(null); it.run(); }}>
                    <span className="check">{it.checked ? '✓' : ''}</span><span>{it.label}</span><kbd>{it.shortcut ?? ''}</kbd>
                  </button>
                ))}
              </div>
            )}
          </div>
        ))}
      </nav>
      <div className="tools" role="toolbar" aria-label="Tools">
        {tool('select', 'Select', model.tool.kind === 'select', () => model.setTool({ kind: 'select' }))}
        {tool('connector', 'Connector', model.tool.kind === 'connector', () => model.setTool(model.tool.kind === 'connector' ? { kind: 'select' } : { kind: 'connector' }))}
        {tool('text', 'Text', model.tool.kind === 'place' && model.tool.what === 'text', () => model.setTool({ kind: 'place', what: 'text' }))}
        {tool('picture', 'Picture', false, () => void model.insertPicture())}
        {tool('script', 'Script', model.drawerOpen, () => model.toggleDrawer())}
        <span className="sep" />
        {tool('undo', 'Undo', false, () => void model.undo())}
        {tool('redo', 'Redo', false, () => void model.redo())}
        {tool('delete', 'Delete', false, () => void model.deleteSelection(), !hasSelection)}
        <span className="sep" />
        {tool('zoom-out', '−', false, () => model.zoomBy(0.8))}
        <span className="zoom" data-testid="zoom">{model.viewport ? `${Math.round((model.viewport.scale / (96 / 72)) * 100)}%` : ''}</span>
        {tool('zoom-in', '+', false, () => model.zoomBy(1.25))}
        {tool('zoom-fit', 'Fit', false, () => model.zoomToFit())}
        <span className="sep" />
        <label className="unit">Units
          <select value={model.unit} data-testid="unit" onChange={e => { model.unit = e.target.value as Unit; model.bump(); }}>
            {(['mm', 'cm', 'in', 'pt', 'px'] as Unit[]).map(u => <option key={u} value={u}>{u}</option>)}
          </select>
        </label>
        <span className="dirty" data-testid="dirty" title={status.dirty ? 'Unsaved changes' : 'All changes saved'}>{status.dirty ? '●' : '○'}</span>
      </div>
    </header>
  );
}

