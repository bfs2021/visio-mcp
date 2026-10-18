import { useEffect, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { PAGE_PRESETS } from '../model/pagePresets';
import { useModel } from './useModel';

export function NewDocumentDialog({ model }: { model: EditorModel }) {
  useModel(model);
  if (!model.newDocument.prompt) return null;
  return <Picker model={model} />;
}
function Picker({ model }: { model: EditorModel }) {
  const flow = model.newDocument, p = flow.prompt!;
  const [preset, setPreset] = useState('a4-landscape');
  const [width, setWidth] = useState(String(Number(model.toUnit(p.size.widthPt).toFixed(4))));
  const [height, setHeight] = useState(String(Number(model.toUnit(p.size.heightPt).toFixed(4))));
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    box.current?.querySelector<HTMLElement>('select,button')?.focus();
    return () => { if (previous?.isConnected) previous.focus(); };
  }, []);
  useEffect(() => { box.current?.querySelector<HTMLElement>('select,button')?.focus(); }, [p.stage]);
  const chosen = PAGE_PRESETS.find(s => s.id === preset);
  const size = chosen ?? { widthPt: model.fromUnit(Number(width)), heightPt: model.fromUnit(Number(height)) };
  const name = model.engine.status().savedPath?.split(/[\\/]/).pop() ?? 'This diagram';
  return <div className="dialog-backdrop" role="presentation">
    <div ref={box} className="dialog" role={p.stage === 'confirm' ? 'alertdialog' : 'dialog'} aria-modal="true" aria-labelledby="new-title" aria-describedby="new-description" data-testid="new-document"
      onKeyDown={e => {
        if (e.key === 'Escape') { e.preventDefault(); flow.cancel(); }
        if (e.key === 'Tab') {
          const items = Array.from(box.current!.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled),input:not(:disabled)'));
          const first = items[0], last = items[items.length - 1];
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
        }
      }}>
      <h2 id="new-title">{p.stage === 'confirm' ? 'Save changes before creating a new drawing?' : 'New document'}</h2>
      <p id="new-description">{p.stage === 'confirm' ? `${name} has changes that were not saved. If you don't save, they are discarded.` : 'Choose the starting page size for your new drawing.'}</p>
      {p.stage === 'size' && <>
        <label>Starting page <select aria-label="Starting page" value={preset} onChange={e => setPreset(e.target.value)} disabled={p.saving}>
          {PAGE_PRESETS.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}<option value="custom">Custom</option>
        </select></label>
        {preset === 'custom' ? <div className="row">
          <label>Width ({model.unit}) <input aria-label={`Width (${model.unit})`} type="number" value={width} min="0" step="any" disabled={p.saving} onChange={e => setWidth(e.target.value)} /></label>
          <label>Height ({model.unit}) <input aria-label={`Height (${model.unit})`} type="number" value={height} min="0" step="any" disabled={p.saving} onChange={e => setHeight(e.target.value)} /></label>
        </div> : <p>{model.toUnit(size.widthPt).toFixed(2)} × {model.toUnit(size.heightPt).toFixed(2)} {model.unit}</p>}
      </>}
      {p.error && <p role="alert" className="diag diag-warning" data-testid="new-error">{p.error}</p>}
      <div className="row dialog-actions">
        {p.stage === 'confirm' ? <>
          <button type="button" disabled={p.saving} onClick={() => void flow.saveAndCreate()}>Save</button>
          <button type="button" disabled={p.saving} onClick={() => void flow.discard()}>Don&apos;t save</button>
        </> : <button type="button" disabled={p.saving} onClick={() => void flow.choose(size)}>Create</button>}
        <button type="button" disabled={p.saving} onClick={() => flow.cancel()}>Cancel</button>
      </div>
    </div>
  </div>;
}

