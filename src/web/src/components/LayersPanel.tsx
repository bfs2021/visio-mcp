import { useEffect, useRef, useState } from 'react';
import type { Layer } from '../contracts/generated';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

function NameField({ layer, model }: { layer: Layer; model: EditorModel }) {
  const [text, setText] = useState(layer.name);
  useEffect(() => setText(layer.name), [layer.name]);
  const commit = () => { if (text.trim() !== layer.name) void model.setLayer(layer.id, { name: text }); if (!text.trim()) setText(layer.name); };
  return <input value={text} data-testid="layer-name" aria-label={`Name of layer ${layer.name}`} onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }} />;
}

/** Layers of the current page: visible, locked, print, membership of the selection, rename, add, delete. */
export function LayersPanel({ model }: { model: EditorModel }) {
  useModel(model);
  const page = model.currentPage;
  const selected = model.selectedElements();
  const [name, setName] = useState('');
  const memberBox = useRef<Record<string, HTMLInputElement | null>>({});

  const state = (layerId: string): 'all' | 'some' | 'none' => {
    const inLayer = selected.filter(e => e.layerIds.includes(layerId)).length;
    return selected.length === 0 || inLayer === 0 ? 'none' : inLayer === selected.length ? 'all' : 'some';
  };
  useEffect(() => { for (const l of page.layers) { const b = memberBox.current[l.id]; if (b) b.indeterminate = state(l.id) === 'some'; } });

  return (
    <section className="side-section" aria-label="Layers" data-testid="layers-panel">
      <h2>Layers</h2>
      <ul className="layer-list">
        {page.layers.map(l => (
          <li key={l.id} data-testid="layer-row" data-layer-id={l.id} data-layer-name={l.name}>
            <input type="checkbox" ref={el => { memberBox.current[l.id] = el; }} checked={state(l.id) === 'all'} disabled={selected.length === 0}
              title="The selection belongs to this layer" aria-label={`Selection is in ${l.name}`} data-testid="layer-member"
              onChange={e => void model.assignSelectionToLayer(l.id, e.target.checked)} />
            <NameField layer={l} model={model} />
            <label title="Visible"><input type="checkbox" checked={l.visible} data-testid="layer-visible" onChange={e => void model.setLayer(l.id, { visible: e.target.checked })} />👁</label>
            <label title="Locked"><input type="checkbox" checked={l.locked} data-testid="layer-locked" onChange={e => void model.setLayer(l.id, { locked: e.target.checked })} />🔒</label>
            <label title="Print"><input type="checkbox" checked={l.printable} data-testid="layer-print" onChange={e => void model.setLayer(l.id, { printable: e.target.checked })} />🖨</label>
            <button type="button" data-testid="layer-delete" title="Delete layer" aria-label={`Delete layer ${l.name}`} disabled={page.layers.length <= 1} onClick={() => void model.deleteLayer(l.id)}>×</button>
          </li>
        ))}
      </ul>
      <div className="row">
        <input value={name} placeholder="New layer" data-testid="layer-new-name" aria-label="New layer name" onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') { void model.addLayer(name); setName(''); } }} />
        <button type="button" data-testid="layer-add" onClick={() => { void model.addLayer(name); setName(''); }}>Add</button>
      </div>
    </section>
  );
}
