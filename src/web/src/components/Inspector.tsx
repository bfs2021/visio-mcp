import { useEffect, useState } from 'react';
import type { ConnectorElement, Element, ImageElement, Operation, Paint, ShapeElement, TextBlock, TextElement } from '../contracts/generated';
import type { EditorModel } from '../editor/EditorModel';
import { ArrangePanel } from './ArrangePanel';
import { useModel } from './useModel';

const round = (v: number) => Math.round(v * 1000) / 1000;

// What a shape without a text block shows (the planner merges edits into the same defaults).
const DEFAULT_TEXT: TextBlock = {
  value: '', fontFamily: 'Segoe UI', fontSizePt: 12, bold: false, italic: false, underline: false, colour: '#000000',
  horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 4,
};

/** A field that edits locally and commits one transaction on Enter or blur, so typing never makes a revision per keystroke. */
function NumberField({ label, value, onCommit, testId, step = 'any', min }: { label: string; value: number; onCommit: (v: number) => void; testId: string; step?: string; min?: number }) {
  const [text, setText] = useState(String(round(value)));
  useEffect(() => setText(String(round(value))), [value]);
  const commit = () => {
    const n = Number(text);
    if (text.trim() !== '' && Number.isFinite(n) && round(n) !== round(value)) onCommit(n);
    else setText(String(round(value)));
  };
  return (
    <label className="field">
      <span>{label}</span>
      <input type="number" value={text} step={step} min={min} data-testid={testId}
        onChange={e => setText(e.target.value)} onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }} />
    </label>
  );
}

function PaintField({ label, paint, testId, onCommit }: { label: string; paint: Paint; testId: string; onCommit: (p: Paint) => void }) {
  const colour = paint.kind === 'solid' ? paint.colour : '#ffffff';
  const opacity = paint.kind === 'solid' ? paint.opacity : 1;
  return (
    <div className="field paint">
      <span>{label}</span>
      <input type="checkbox" checked={paint.kind === 'none'} aria-label={`${label}: none`} data-testid={`${testId}-none`}
        onChange={e => onCommit(e.target.checked ? { kind: 'none' } : { kind: 'solid', colour, opacity })} /> <small>none</small>
      <input type="color" value={colour} disabled={paint.kind === 'none'} data-testid={`${testId}-colour`}
        onChange={e => onCommit({ kind: 'solid', colour: e.target.value, opacity })} />
      <input type="number" min={0} max={100} value={Math.round(opacity * 100)} disabled={paint.kind === 'none'} aria-label={`${label} opacity %`} data-testid={`${testId}-opacity`}
        onChange={e => onCommit({ kind: 'solid', colour, opacity: Math.min(1, Math.max(0, Number(e.target.value) / 100)) })} />
    </div>
  );
}

export function Inspector({ model }: { model: EditorModel }) {
  useModel(model);
  const els = model.selectedElements();
  const set = (patch: Record<string, unknown>) => void model.apply('inspector', els.map(e => ({ op: 'set', target: e.id, patch }) as Operation));

  if (els.length === 0) {
    const page = model.currentPage;
    return (
      <aside className="panel panel-right" aria-label="Inspector">
        <h2>Page</h2>
        <p className="muted">{page.name}: {model.toUnit(page.widthPt).toFixed(1)} × {model.toUnit(page.heightPt).toFixed(1)} {model.unit}</p>
        <p className="muted">Grid {page.grid.visible ? 'shown' : 'hidden'}, {page.grid.snap ? 'snapping' : 'free'}, every {model.toUnit(page.grid.spacingPt).toFixed(1)} {model.unit}</p>
        <p className="muted">Select an object to edit it.</p>
      </aside>
    );
  }

  if (els.length > 1) {
    const shapes = els.filter((e): e is ShapeElement => e.kind === 'shape');
    return (
      <aside className="panel panel-right" aria-label="Inspector">
        <h2>{els.length} objects</h2>
        {shapes.length > 0 && (
          <section>
            <h3>Style</h3>
            <PaintField label="Fill" testId="multi-fill" paint={shapes[0]!.style.fill} onCommit={fill => void model.apply('inspector', shapes.map(s => ({ op: 'set', target: s.id, patch: { style: { fill } } }) as Operation))} />
            <PaintField label="Line" testId="multi-stroke" paint={shapes[0]!.style.stroke} onCommit={stroke => void model.apply('inspector', shapes.map(s => ({ op: 'set', target: s.id, patch: { style: { stroke } } }) as Operation))} />
          </section>
        )}
        <section>
          <h3>Arrange</h3>
          <button type="button" onClick={() => void model.duplicate()}>Duplicate</button>{' '}
          <button type="button" onClick={() => void model.deleteSelection()} data-testid="inspector-delete">Delete</button>
        </section>
        <ArrangePanel model={model} />
      </aside>
    );
  }

  const el = els[0]!;
  const u = model.unit;
  const geometry = el.kind !== 'connector';
  const isGroup = el.kind === 'group';
  const move = (dx: number, dy: number) => void model.apply('inspector', [{ op: 'move', target: el.id, delta: { xPt: dx, yPt: dy } }]);
  const text: TextBlock | undefined = el.kind === 'shape' ? el.text ?? DEFAULT_TEXT : el.kind === 'text' ? el.text : el.kind === 'connector' ? el.label ?? DEFAULT_TEXT : undefined;
  const setText = (patch: Partial<TextBlock>) => set({ text: patch });

  return (
    <aside className="panel panel-right" aria-label="Inspector" data-testid="inspector" key={el.id}>
      <h2>{el.kind[0]!.toUpperCase() + el.kind.slice(1)}</h2>
      <label className="field"><span>Name</span>
        <input type="text" defaultValue={el.name ?? ''} data-testid="field-name" key={`${el.id}-${el.name}`}
          onBlur={e => { if (e.target.value !== (el.name ?? '')) set({ name: e.target.value === '' ? null : e.target.value }); }} />
      </label>

      {geometry && (
        <section>
          <h3>Geometry ({u})</h3>
          <div className="grid2">
            <NumberField label="X" testId="field-x" value={model.toUnit(el.bounds.x)} onCommit={v => move(model.fromUnit(v) - el.bounds.x, 0)} />
            <NumberField label="Y" testId="field-y" value={model.toUnit(el.bounds.y)} onCommit={v => move(0, model.fromUnit(v) - el.bounds.y)} />
            <NumberField label="W" testId="field-w" value={model.toUnit(el.bounds.width)} min={0} onCommit={v => void model.apply('inspector', [{ op: 'resize', target: el.id, bounds: { width: model.fromUnit(v) } }])} />
            <NumberField label="H" testId="field-h" value={model.toUnit(el.bounds.height)} min={0} onCommit={v => void model.apply('inspector', [{ op: 'resize', target: el.id, bounds: { height: model.fromUnit(v) } }])} />
            {isGroup
              ? <NumberField label="Turn by °" testId="field-turn" key={`turn-${model.snapshot.revision}`} value={0} onCommit={v => void model.apply('inspector', [{ op: 'rotate', target: el.id, deltaDeg: v }])} />
              : <NumberField label="Angle °" testId="field-angle" value={el.rotationDeg} onCommit={v => void model.apply('inspector', [{ op: 'rotate', target: el.id, angleDeg: v }])} />}
          </div>
        </section>
      )}

      {el.kind === 'shape' && <ShapeStyle model={model} el={el} set={set} />}
      {el.kind === 'connector' && <ConnectorStyle el={el} set={set} />}
      {el.kind === 'image' && <ImageProps el={el} model={model} set={set} />}

      {text && (
        <section>
          <h3>Text</h3>
          <textarea defaultValue={text.value} rows={3} data-testid="field-text" key={`${el.id}-${text.value}`}
            onBlur={e => { if (e.target.value !== text.value) setText({ value: e.target.value }); }} />
          <div className="grid2">
            <NumberField label="Size pt" testId="field-fontsize" value={text.fontSizePt} min={1} onCommit={v => setText({ fontSizePt: v })} />
            <label className="field"><span>Font</span>
              <select value={text.fontFamily} data-testid="field-font" onChange={e => setText({ fontFamily: e.target.value })}>
                {['Segoe UI', 'Arial', 'Calibri', 'Consolas', 'Times New Roman'].map(f => <option key={f}>{f}</option>)}
              </select>
            </label>
          </div>
          <div className="row">
            {(['bold', 'italic', 'underline'] as const).map(k => (
              <label key={k}><input type="checkbox" checked={text[k]} data-testid={`field-${k}`} onChange={e => setText({ [k]: e.target.checked })} /> {k[0]!.toUpperCase() + k.slice(1)}</label>
            ))}
            <label><input type="checkbox" checked={text.wrap} data-testid="field-wrap" onChange={e => setText({ wrap: e.target.checked })} /> Wrap</label>
          </div>
          <div className="grid2">
            <label className="field"><span>Align</span>
              <select value={text.horizontalAlign} data-testid="field-halign" onChange={e => setText({ horizontalAlign: e.target.value as TextBlock['horizontalAlign'] })}>
                <option>left</option><option>center</option><option>right</option>
              </select>
            </label>
            <label className="field"><span>Vertical</span>
              <select value={text.verticalAlign} data-testid="field-valign" onChange={e => setText({ verticalAlign: e.target.value as TextBlock['verticalAlign'] })}>
                <option>top</option><option>middle</option><option>bottom</option>
              </select>
            </label>
          </div>
        </section>
      )}

      <section>
        <h3>Arrange</h3>
        <div className="row">
          <label><input type="checkbox" checked={el.locked} data-testid="field-locked" onChange={e => set({ locked: e.target.checked })} /> Locked</label>
          {!isGroup && <label><input type="checkbox" checked={el.hidden} data-testid="field-hidden" onChange={e => set({ hidden: e.target.checked })} /> Hidden</label>}
        </div>
        <button type="button" onClick={() => void model.duplicate()} data-testid="inspector-duplicate">Duplicate</button>{' '}
        <button type="button" onClick={() => void model.deleteSelection()} data-testid="inspector-delete">Delete</button>
      </section>

      <ArrangePanel model={model} />

      {!isGroup && <section>
        <h3>Layers</h3>
        {model.currentPage.layers.map(l => (
          <label key={l.id} className="row"><input type="checkbox" checked={el.layerIds.includes(l.id)} data-testid={`layer-${l.name}`}
            onChange={e => set({ layerIds: e.target.checked ? [...el.layerIds, l.id] : el.layerIds.filter(x => x !== l.id) })} /> {l.name}</label>
        ))}
      </section>}
      <p className="muted small" title={el.id}>id {el.id.slice(0, 8)}…</p>
    </aside>
  );
}

function ShapeStyle({ el, set }: { model: EditorModel; el: ShapeElement; set: (p: Record<string, unknown>) => void }) {
  return (
    <section>
      <h3>Style</h3>
      <PaintField label="Fill" testId="field-fill" paint={el.style.fill} onCommit={fill => set({ style: { fill } })} />
      <PaintField label="Line" testId="field-stroke" paint={el.style.stroke} onCommit={stroke => set({ style: { stroke } })} />
      <div className="grid2">
        <NumberField label="Width pt" testId="field-strokewidth" value={el.style.strokeWidthPt} min={0} onCommit={v => set({ style: { strokeWidthPt: v } })} />
        <label className="field"><span>Dash</span>
          <select value={el.style.dash} data-testid="field-dash" onChange={e => set({ style: { dash: e.target.value } })}>
            <option>solid</option><option>dashed</option><option>dotted</option><option>dash-dot</option>
          </select>
        </label>
      </div>
    </section>
  );
}

function ConnectorStyle({ el, set }: { el: ConnectorElement; set: (p: Record<string, unknown>) => void }) {
  const arrows = ['none', 'block', 'open', 'diamond', 'oval'];
  return (
    <section>
      <h3>Connector</h3>
      <label className="field"><span>Colour</span><input type="color" value={el.style.colour} data-testid="field-line-colour" onChange={e => set({ style: { colour: e.target.value } })} /></label>
      <div className="grid2">
        <NumberField label="Width pt" testId="field-line-width" value={el.style.widthPt} min={0} onCommit={v => set({ style: { widthPt: v } })} />
        <label className="field"><span>Dash</span>
          <select value={el.style.dash} data-testid="field-line-dash" onChange={e => set({ style: { dash: e.target.value } })}><option>solid</option><option>dashed</option><option>dotted</option><option>dash-dot</option></select>
        </label>
        <label className="field"><span>Start</span>
          <select value={el.style.startArrow} data-testid="field-start-arrow" onChange={e => set({ style: { startArrow: e.target.value } })}>{arrows.map(a => <option key={a}>{a}</option>)}</select>
        </label>
        <label className="field"><span>End</span>
          <select value={el.style.endArrow} data-testid="field-end-arrow" onChange={e => set({ style: { endArrow: e.target.value } })}>{arrows.map(a => <option key={a}>{a}</option>)}</select>
        </label>
        <label className="field"><span>Start size</span>
          <select value={el.style.startArrowSize ?? 'medium'} data-testid="field-start-arrow-size" onChange={e => set({ style: { startArrowSize: e.target.value } })}><option>small</option><option>medium</option><option>large</option></select>
        </label>
        <label className="field"><span>End size</span>
          <select value={el.style.endArrowSize ?? 'medium'} data-testid="field-end-arrow-size" onChange={e => set({ style: { endArrowSize: e.target.value } })}><option>small</option><option>medium</option><option>large</option></select>
        </label>
        <label className="field"><span>Route</span>
          <select value={el.route} data-testid="field-route" onChange={e => set({ route: e.target.value })}><option>straight</option><option>orthogonal</option><option>curved</option></select>
        </label>
      </div>
      <p className="muted small">From {el.from.glue === 'none' ? 'a point' : 'a shape'} to {el.to.glue === 'none' ? 'a point' : 'a shape'}</p>
    </section>
  );
}

function ImageProps({ el, model, set }: { el: ImageElement; model: EditorModel; set: (p: Record<string, unknown>) => void }) {
  const asset = model.snapshot.document.assets.find(a => a.id === el.assetId);
  return (
    <section>
      <h3>Picture</h3>
      <p className="muted small" data-testid="image-asset" title={asset?.sha256}>{asset?.name ?? el.assetId} · {asset?.mimeType} · {asset?.sha256.slice(0, 12)}…</p>
      <div className="grid2">
        <label className="field"><span>Fit</span>
          <select value={el.fit} data-testid="field-fit" onChange={e => set({ fit: e.target.value })}><option>contain</option><option>cover</option><option>stretch</option></select>
        </label>
        <NumberField label="Opacity %" testId="field-image-opacity" value={Math.round(el.opacity * 100)} min={0} onCommit={v => set({ opacity: Math.min(1, Math.max(0, v / 100)) })} />
      </div>
    </section>
  );
}

export type { Element, TextElement };
