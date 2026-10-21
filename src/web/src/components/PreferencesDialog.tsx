import { useEffect, useRef, useState } from 'react';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';
import { toPoints } from '../model/validate';
import { useWorkspace } from './useWorkspace';

const displaySpacing = (pt: number, unit: import('../model/validate').Unit) => Number((pt / toPoints(1, unit)).toPrecision(6));

export function PreferencesDialog({ ws }: { ws: WorkspaceModel }) {
  useWorkspace(ws);
  const model = ws.preferences;
  const box = useRef<HTMLDivElement>(null);
  const [spacingText, setSpacingText] = useState('');
  useEffect(() => {
    if (!model.dialogOpen) return;
    const app = document.querySelector('.app');
    const opener = document.querySelector<HTMLElement>('[data-testid="menu-edit"]');
    const siblings = Array.from(app?.children ?? []).filter(child => child !== box.current?.parentElement) as HTMLElement[];
    for (const sibling of siblings) sibling.inert = true;
    (box.current?.querySelector<HTMLElement>('select:not(:disabled),input:not(:disabled),button:not(:disabled)') ?? box.current)?.focus();
    return () => { for (const sibling of siblings) sibling.inert = false; if (opener?.isConnected) opener.focus(); };
  }, [model.dialogOpen]);
  useEffect(() => { if (model.dialogOpen && model.draft) box.current?.querySelector<HTMLElement>('select:not(:disabled),input:not(:disabled),button:not(:disabled)')?.focus(); }, [model.dialogOpen, !!model.draft]);
  useEffect(() => {
    if (model.draft) setSpacingText(String(displaySpacing(model.draft.grid.spacingPt, model.draft.units)));
  }, [model.draft?.units, model.draftEpoch, model.dialogOpen]);
  if (!model.dialogOpen) return null;
  const p = model.draft;
  const unavailable = p && model.installedFonts.length > 0 && !model.installedFonts.some(f => f.toLocaleLowerCase() === p.font.family.trim().toLocaleLowerCase());
  const cancel = () => { model.cancel(); void ws.host.appCloseCancelled(); };
  const numeric = (s: string) => s.trim() === '' ? Number.NaN : Number(s);
  return <div className="dialog-backdrop preferences-backdrop" role="presentation">
    <div ref={box} tabIndex={-1} className="dialog preferences-dialog" role="dialog" aria-modal="true" aria-labelledby="preferences-title" data-testid="preferences-dialog"
      onKeyDown={e => {
        if (e.key === 'Escape' && !model.busy) { e.preventDefault(); cancel(); }
        if (e.key === 'Tab') {
          const items = Array.from(box.current!.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled),input:not(:disabled)'));
          if (!items.length) { e.preventDefault(); box.current?.focus(); return; }
          if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items.at(-1)?.focus(); }
          else if (!e.shiftKey && document.activeElement === items.at(-1)) { e.preventDefault(); items[0]?.focus(); }
        }
      }}>
      <h2 id="preferences-title">Preferences</h2>
      {!p ? <p>Loading preferences…</p> : <>
        <fieldset><legend>Display</legend>
          <p className="muted small">These choices change editor views. Saving units applies them to every open tab without changing a drawing.</p>
          <label>Display units <select value={p.units} disabled={model.busy} onChange={e => model.edit({ units: e.target.value as typeof p.units })}>
            {(['mm', 'cm', 'in', 'pt', 'px'] as const).map(unit => <option key={unit} value={unit}>{unit}</option>)}
          </select></label>
          <label>Theme <select value={p.theme} disabled={model.busy} onChange={e => model.edit({ theme: e.target.value as typeof p.theme })}>
            <option value="light">Light</option><option value="dark">Dark</option>
          </select></label>
        </fieldset>
        <fieldset><legend>New drawings</legend>
          <p className="muted small">Grid and font choices are captured when a drawing is created. Existing drawings keep their content.</p>
          <label>Grid spacing ({p.units}) <input type="number" min="0" step="any" value={spacingText} disabled={model.busy}
            onChange={e => { setSpacingText(e.target.value); model.edit({ grid: { ...p.grid, spacingPt: toPoints(numeric(e.target.value), p.units) } }); }} /></label>
          <label className="preference-check"><input type="checkbox" checked={p.grid.visible} disabled={model.busy} onChange={e => model.edit({ grid: { ...p.grid, visible: e.target.checked } })} /> Show grid on new pages</label>
          <label className="preference-check"><input type="checkbox" checked={p.grid.snap} disabled={model.busy} onChange={e => model.edit({ grid: { ...p.grid, snap: e.target.checked } })} /> Snap to grid on new pages</label>
          <label>Default font <input type="text" list="installed-fonts" maxLength={128} value={p.font.family} disabled={model.busy} onChange={e => model.edit({ font: { ...p.font, family: e.target.value } })} /></label>
          <datalist id="installed-fonts">{model.installedFonts.map(name => <option key={name} value={name} />)}</datalist>
          {unavailable && <p className="muted small" data-testid="font-unavailable">This font is not installed here. It will be kept and the platform may show a substitute.</p>}
          <label>Default font size (pt) <input type="number" min="1" max="1000" step="any" value={Number.isNaN(p.font.sizePt) ? '' : p.font.sizePt} disabled={model.busy}
            onChange={e => model.edit({ font: { ...p.font, sizePt: numeric(e.target.value) } })} /></label>
        </fieldset>
        <fieldset><legend>Recovery</legend>
          <p className="muted small">The checkpoint interval applies after Save; recovery journaling stays on.</p>
          <label>Checkpoint interval (seconds) <input type="number" min="10" max="3600" step="1" value={Number.isNaN(p.recovery.checkpointSeconds) ? '' : p.recovery.checkpointSeconds} disabled={model.busy}
            onChange={e => model.edit({ recovery: { checkpointSeconds: numeric(e.target.value) } })} /></label>
        </fieldset>
        {model.diagnostic && <p className="diag diag-warning" role="status" data-testid="preferences-diagnostic">{model.diagnostic}</p>}
        {Object.entries(model.errors).map(([field, message]) => <p key={field} className="diag diag-error" role="alert" data-testid={`preferences-error-${field}`}>{message}</p>)}
        {model.conflicted && <p className="muted small">The settings changed on disk. Reload to use the current version; your draft stays until you choose Reload.</p>}
        <div className="row dialog-actions">
          <button type="button" disabled={model.busy} onClick={() => model.resetDraft()}>Reset to defaults</button>
          <button type="button" disabled={model.busy} onClick={() => void model.reload()}>Reload</button>
          <button type="button" disabled={model.busy} onClick={cancel}>Cancel</button>
          <button type="button" disabled={model.busy} onClick={() => void model.save().then(result => { if (result.ok) void ws.host.appCloseCancelled(); })}>Save preferences</button>
        </div>
      </>}
    </div>
  </div>;
}
