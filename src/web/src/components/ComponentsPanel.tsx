import { useEffect, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import type { ComponentProposal } from '../layout/components';
import { componentWarningText, samples, type Content, type TemplateId } from '../layout/componentTemplates';
import { useModel } from './useModel';

type Preview = ComponentProposal & { base64: string; mimeType: string };
const bodyOf = (id: TemplateId, c: Content) => id === 'environment-columns' ? c.columns!.map(x => [x.title, ...(x.items ?? [])].join(' | ')).join('\n') : id === 'pipeline' ? c.steps!.map(x => [x.title, x.detail ?? ''].join(' | ')).join('\n') : (c.items ?? []).join('\n');
export function ComponentsPanel({ model }: { model: EditorModel }) {
  useModel(model);
  const [template, setTemplate] = useState<TemplateId>('repository-card');
  const [title, setTitle] = useState(samples['repository-card'].title), [subtitle, setSubtitle] = useState(samples['repository-card'].subtitle ?? ''), [body, setBody] = useState(bodyOf('repository-card', samples['repository-card'])), [footer, setFooter] = useState(samples['repository-card'].footer ?? '');
  const [width, setWidth] = useState(400), [padding, setPadding] = useState(16), [gap, setGap] = useState(10), [align, setAlign] = useState('left'), [target, setTarget] = useState('visio');
  const [alignmentChanged, setAlignmentChanged] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null), [busy, setBusy] = useState(false), [message, setMessage] = useState('');
  const serial = useRef(0);
  const selected = model.selectedElements(); const group = selected.length === 1 && selected[0]?.kind === 'group' && selected[0].metadata['layout.template'] ? selected[0] : undefined;
  const groupId = group?.id, pageId = model.currentPage.id, sessionId = model.snapshot.sessionId;
  const invalidate = () => { serial.current++; setPreview(null); setMessage(''); setBusy(false); };
  useEffect(() => {
    invalidate(); setAlignmentChanged(false);
    if (group) {
      try { const s = JSON.parse(group.metadata['layout.settings'] ?? '{}'); setWidth(Math.round(group.bounds.width * 1000) / 1000); setPadding(s.paddingPt ?? 16); setGap(s.gapPt ?? 10); setAlign(s.align ?? 'left'); setTarget(s.target ?? 'visio'); }
      catch { setMessage('This component has invalid saved settings.'); }
    }
  }, [groupId, pageId, sessionId]);
  useEffect(() => {
    if (group) { invalidate(); setWidth(Math.round(group.bounds.width * 1000) / 1000); }
  }, [groupId, group?.bounds.width]);
  const content = (): Content => {
    const lines = body.split('\n').filter(s => s.trim().length);
    const c: Content = { title, ...(subtitle ? { subtitle } : {}) };
    if (template === 'environment-columns') c.columns = lines.map(line => { const [name = '', ...items] = line.split('|').map(s => s.trim()); return { title: name, items }; });
    else if (template === 'pipeline') c.steps = lines.map(line => { const [name = '', ...detail] = line.split('|').map(s => s.trim()); return { title: name, detail: detail.join(' | ') }; });
    else { c.items = lines; if (footer) c.footer = footer; }
    return c;
  };
  const generate = async (reflow: boolean) => {
    const ticket = ++serial.current; setBusy(true); setPreview(null); setMessage('');
    try {
      const params = { pageId, layout: { widthPt: width, paddingPt: padding, gapPt: gap, target, ...(!reflow || alignmentChanged ? { align } : {}) }, ...(reflow ? { componentId: groupId } : { templateId: template, content: content(), x: Math.max(16, (model.currentPage.widthPt - width) / 2), y: 32 }) };
      const result = await model.proposeComponent(params);
      if (ticket !== serial.current) return;
      if (result.ok) setPreview(result.value); else setMessage(result.error.message);
    } catch (e) { if (ticket === serial.current) setMessage(e instanceof Error ? e.message : 'Preview failed'); }
    finally { if (ticket === serial.current) setBusy(false); }
  };
  const stale = preview && (preview.baseRevision !== model.snapshot.revision || preview.documentId !== model.snapshot.documentId || preview.sessionId !== sessionId);
  return <details open className="components-panel" data-testid="components-panel">
    <summary>Layout components</summary>
    <label>Type <select aria-label="Component type" value={template} onChange={e => {
      invalidate(); const id = e.target.value as TemplateId; const c = samples[id]; setTemplate(id); setTitle(c.title); setSubtitle(c.subtitle ?? ''); setBody(bodyOf(id, c)); setFooter(c.footer ?? ''); setWidth(id === 'environment-columns' ? 600 : 400); setAlign(id === 'repository-card' || id === 'labelled-container' ? 'left' : 'center');
    }}>{Object.keys(samples).map(id => <option key={id} value={id}>{id.replaceAll('-', ' ')}</option>)}</select></label>
    <label>Title <input aria-label="Component title" value={title} maxLength={512} onChange={e => { invalidate(); setTitle(e.target.value); }} /></label>
    <label>Subtitle <input aria-label="Component subtitle" value={subtitle} maxLength={512} onChange={e => { invalidate(); setSubtitle(e.target.value); }} /></label>
    <label>{template === 'environment-columns' ? 'Columns: title | items' : template === 'pipeline' ? 'Steps: title | detail' : 'Items: one per line'}<textarea aria-label="Component items" rows={3} value={body} maxLength={16000} onChange={e => { invalidate(); setBody(e.target.value); }} /></label>
    {(template === 'repository-card' || template === 'labelled-container') && <label>Footer <input aria-label="Component footer" value={footer} maxLength={512} onChange={e => { invalidate(); setFooter(e.target.value); }} /></label>}
    {([['Width (pt)', width, setWidth, 120, 2000], ['Padding (pt)', padding, setPadding, 0, 100], ['Gap (pt)', gap, setGap, 0, 100]] as const).map(([name, value, setter, min, max]) => <label key={name}>{name}<input style={{ width: 68 }} aria-label={`Component ${name.toLowerCase()}`} type="number" value={value} min={min} max={max} onChange={e => { invalidate(); setter(Number(e.target.value)); }} /></label>)}
    <label>Align <select aria-label="Component alignment" value={align} onChange={e => { invalidate(); setAlign(e.target.value); setAlignmentChanged(true); }}><option>left</option><option>center</option><option>right</option></select></label>
    {groupId && <label><input type="checkbox" checked={alignmentChanged} onChange={e => { invalidate(); setAlignmentChanged(e.target.checked); }} /> Apply alignment on reflow</label>}
    <label>Text fitting <select aria-label="Component text target" value={target} onChange={e => { invalidate(); setTarget(e.target.value); }}><option value="visio">Conservative for Visio</option><option value="editor">Editor fonts</option></select></label>
    <div className="row"><button type="button" disabled={busy} onClick={() => void generate(false)}>Preview component</button><button type="button" disabled={busy || !groupId} onClick={() => void generate(true)}>Preview reflow</button></div>
    <small>Reflow uses the selected component’s current text and styles. It applies layout controls above.</small>
    {preview && <img alt="Component preview" src={`data:${preview.mimeType};base64,${preview.base64}`} style={{ width: '100%', maxHeight: 240, objectFit: 'contain', background: 'white' }} />}
    <button type="button" disabled={busy || !preview || !!stale} onClick={() => { if (preview) void model.applyComponent(preview).then(ok => { if (ok) { invalidate(); setAlignmentChanged(false); } }); }}>Apply component</button>
    {(message || stale) && <p role="status">{message || 'The document changed. Preview again before applying.'}</p>}
    {preview?.warnings.map(w => <p key={w} className="muted small">{componentWarningText(w)}</p>)}
  </details>;
}
