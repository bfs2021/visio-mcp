import { useEffect, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

import { PAGE_PRESETS } from '../model/pagePresets';
export { PAGE_PRESETS } from '../model/pagePresets';

const near = (a: number, b: number) => Math.abs(a - b) < 0.5;

/** Page tabs along the bottom: switch, add, rename (double click), reorder, duplicate, delete, and size. */
export function PageTabs({ model }: { model: EditorModel }) {
  useModel(model);
  const pages = model.snapshot.document.pages;
  const current = model.currentPage;
  const [renaming, setRenaming] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [w, setW] = useState('');
  const [h, setH] = useState('');
  const index = pages.findIndex(p => p.id === current.id);

  useEffect(() => {
    setW(String(Math.round(model.toUnit(current.widthPt) * 100) / 100));
    setH(String(Math.round(model.toUnit(current.heightPt) * 100) / 100));
  }, [current.id, current.widthPt, current.heightPt, model.unit, model]);

  const preset = PAGE_PRESETS.find(p => near(p.widthPt, current.widthPt) && near(p.heightPt, current.heightPt))?.id ?? 'custom';
  const commitRename = () => { if (renaming) void model.renamePage(renaming, draft); setRenaming(null); };

  return (
    <nav className="page-tabs" aria-label="Pages" data-testid="page-tabs">
      <div className="tabs" role="tablist">
        {pages.map(p => (
          p.id === renaming
            ? <input key={p.id} className="tab-rename" autoFocus value={draft} data-testid="page-rename-input" aria-label="Page name"
                onChange={e => setDraft(e.target.value)} onBlur={commitRename}
                onKeyDown={e => { if (e.key === 'Enter') commitRename(); else if (e.key === 'Escape') setRenaming(null); }} />
            : <button key={p.id} type="button" role="tab" aria-selected={p.id === current.id} className={`tab${p.id === current.id ? ' active' : ''}`}
                data-testid="page-tab" data-page-id={p.id} title="Double click to rename"
                onClick={() => model.setCurrentPage(p.id)} onDoubleClick={() => { setRenaming(p.id); setDraft(p.name); }}>{p.name}</button>
        ))}
        <button type="button" className="tab-add" data-testid="page-add" title="Add a page" aria-label="Add a page" onClick={() => void model.addPage()}>+</button>
      </div>
      <div className="tab-tools">
        <button type="button" data-testid="page-left" title="Move page left" disabled={index <= 0} onClick={() => void model.movePage(current.id, -1)}>◀</button>
        <button type="button" data-testid="page-right" title="Move page right" disabled={index >= pages.length - 1} onClick={() => void model.movePage(current.id, 1)}>▶</button>
        <button type="button" data-testid="page-duplicate" onClick={() => void model.duplicatePage(current.id)}>Duplicate</button>
        <button type="button" data-testid="page-delete" disabled={pages.length <= 1} onClick={() => void model.deletePage(current.id)}>Delete</button>
        <label>Size
          <select data-testid="page-size" value={preset} onChange={e => { const p = PAGE_PRESETS.find(x => x.id === e.target.value); if (p) void model.setPageSize(current.id, p.widthPt, p.heightPt); }}>
            {PAGE_PRESETS.map(p => <option key={p.id} value={p.id}>{p.label}</option>)}
            <option value="custom">Custom</option>
          </select>
        </label>
        <label className="inline">W <input type="number" min={1} value={w} data-testid="page-w" onChange={e => setW(e.target.value)} /></label>
        <label className="inline">H <input type="number" min={1} value={h} data-testid="page-h" onChange={e => setH(e.target.value)} /></label>
        <span className="muted">{model.unit}</span>
        <button type="button" data-testid="page-size-apply" onClick={() => void model.setPageSize(current.id, model.fromUnit(Number(w)), model.fromUnit(Number(h)))}>Apply size</button>
      </div>
    </nav>
  );
}

