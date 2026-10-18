import { useEffect, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { searchIcons } from '../icons/catalog';
import { useModel } from './useModel';

export function IconsPanel({ model }: { model: EditorModel }) {
  useModel(model);
  const [query, setQuery] = useState(''), [size, setSize] = useState(24), [colour, setColour] = useState('#334155'), [stroke, setStroke] = useState(2);
  const found = searchIcons(query, 50);
  const valid = Number.isFinite(size) && size >= 1 && size <= 1000 && Number.isFinite(stroke) && stroke > 0 && stroke <= size / 2;
  useEffect(() => {
    if (valid) model.iconSettings = { sizePt: size, colour, strokeWidthPt: stroke };
    else if (model.tool.kind === 'place' && model.tool.what.startsWith('icon:')) model.setTool({ kind: 'select' });
  }, [model, valid, size, colour, stroke]);
  return <details open data-testid="icons-panel">
    <summary>Icons</summary>
    <label>Search <input aria-label="Icon search" placeholder="branch, cloud, security…" value={query} maxLength={256} onChange={e => setQuery(e.target.value)} /></label>
    <div className="row"><label>Size (pt) <input aria-label="Icon size (pt)" type="number" value={size} min={1} max={1000} onChange={e => setSize(Number(e.target.value))} style={{ width: 56 }} /></label>
      <label>Stroke (pt) <input aria-label="Icon stroke (pt)" type="number" value={stroke} min={0.1} max={size / 2} step={0.1} onChange={e => setStroke(Number(e.target.value))} style={{ width: 56 }} /></label></div>
    <label>Colour <input aria-label="Icon colour" type="color" value={colour} onChange={e => setColour(e.target.value)} /></label>
    {!valid && <p className="muted">Choose a positive stroke no greater than half the icon size.</p>}
    <div className="shape-grid">
      {found.ok && found.value.icons.map(icon => <button key={icon.id} type="button" title={icon.name} aria-label={`Icon: ${icon.name}`} data-testid="icon-choice"
        className={`shape-item${model.tool.kind === 'place' && model.tool.what === `icon:${icon.id}` ? ' armed' : ''}`} disabled={!valid}
        onClick={() => { model.iconSettings = { sizePt: size, colour, strokeWidthPt: stroke }; model.setTool({ kind: 'place', what: `icon:${icon.id}` }); }}>
        <svg viewBox="0 0 100 100" width="28" height="28" aria-hidden="true"><path d={icon.path} fill="none" stroke={colour} strokeWidth={valid ? stroke / size * 100 : 100 / 12} strokeLinecap="round" strokeLinejoin="round" /></svg>
        <span>{icon.name}</span>
      </button>)}
    </div>
    {found.ok && !found.value.icons.length && <p className="muted">No matching icons.</p>}
    <small>Lucide · Click an icon, then click the page to place it.</small>
  </details>;
}
