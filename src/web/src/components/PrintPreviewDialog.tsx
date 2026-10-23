import { useEffect, useRef } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import type { PrintMargins } from '../editor/EditorPrintOptions';
import { useModel } from './useModel';

export function PrintPreviewDialog({ model }: { model: EditorModel }) {
  useModel(model);
  const prompt = model.printPrompt;
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const old = document.activeElement as HTMLElement | null;
    box.current?.querySelector<HTMLElement>('input:not(:disabled),select:not(:disabled),button:not(:disabled)')?.focus();
    return () => { if (old?.isConnected) old.focus(); };
  }, []);
  if (!prompt) return null;
  const options = prompt.options;
  const device = prompt.devices.find(x => x.id === options.printerId);
  const media = device?.media.find(x => x.id === options.mediaId);
  const updateMargins = (edge: keyof PrintMargins, value: number) => model.changePrintOptions({ margins: { ...options.margins, [edge]: Number.isFinite(value) ? Math.max(0, value) : 0 } });

  const sheetUrl = (svg: string) => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
  return <div className="dialog-backdrop">
    <div ref={box} tabIndex={-1} className="dialog print-preview-dialog" role="dialog" aria-modal="true" aria-labelledby="print-preview-title" data-testid="print-preview"
      onKeyDown={e => {
        e.stopPropagation();
        if (e.key === 'Escape') { e.preventDefault(); model.cancelPrint(); }
        if (e.key === 'Tab') {
          const items = Array.from(box.current!.querySelectorAll<HTMLElement>('input:not(:disabled),select:not(:disabled),button:not(:disabled)'));
          if (!items.length) { e.preventDefault(); box.current?.focus(); }
          else if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items.at(-1)?.focus(); }
          else if (!e.shiftKey && document.activeElement === items.at(-1)) { e.preventDefault(); items[0]?.focus(); }
        }
      }}>
      <h2 id="print-preview-title">Print preview</h2>
      <p className="hint">Preview the exact sheets that will be sent to the selected printer.</p>
      {prompt.error && <p role="alert" className="diag diag-warning">{prompt.error}</p>}
      {prompt.loadingDevices ? <p role="status">Loading printers…</p> : <div className="print-options">
        <label>Printer <select aria-label="Printer" disabled={prompt.busy || !prompt.devices.length} value={options.printerId} onChange={e => {
          const next = prompt.devices.find(x => x.id === e.target.value);
          model.changePrintOptions({ printerId: e.target.value, mediaId: next?.media[0]?.id ?? '' });
        }}>{prompt.devices.map(x => <option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
        <label>Paper <select aria-label="Paper" disabled={prompt.busy || !device?.media.length} value={options.mediaId} onChange={e => model.changePrintOptions({ mediaId: e.target.value })}>{device?.media.map(x => <option key={x.id} value={x.id}>{x.label}</option>)}</select></label>
        <label>Pages <input aria-label="Pages" value={options.pageRange} disabled={prompt.busy} placeholder="1-3,5" onChange={e => model.changePrintOptions({ pageRange: e.target.value })} /></label>
        <label>Orientation <select aria-label="Orientation" disabled={prompt.busy} value={options.orientation} onChange={e => model.changePrintOptions({ orientation: e.target.value as 'portrait' | 'landscape' })}><option value="portrait">Portrait</option><option value="landscape">Landscape</option></select></label>
        <label>Layout <select aria-label="Layout" disabled={prompt.busy} value={options.layout} onChange={e => model.changePrintOptions({ layout: e.target.value as 'fit' | 'tile' })}><option value="fit">Fit one page per sheet</option><option value="tile">Tile at full size</option></select></label>
        <fieldset className="print-margins"><legend>Margins (points)</legend>
          {(['topPt', 'rightPt', 'bottomPt', 'leftPt'] as const).map(edge => <label key={edge}>{edge.replace('Pt', '')}<input aria-label={`Margin ${edge.replace('Pt', '')}`} type="number" min="0" step="1" disabled={prompt.busy} value={options.margins[edge]} onChange={e => updateMargins(edge, e.currentTarget.valueAsNumber)} /></label>)}
        </fieldset>
      </div>}
      {media && <p className="hint">Paper: {media.widthPt} × {media.heightPt} pt. The printer’s minimum non-printable margins are applied to the preview.</p>}
      {prompt.marginWarnings.map((warning, i) => <p key={i} className="diag diag-warning">{warning}</p>)}
      {prompt.sheets.length > 0 && <div className="print-sheets" aria-label="Prepared print sheets">
        {prompt.sheets.map(sheet => <figure key={sheet.sheetNumber} data-testid="print-sheet">
          <img src={sheetUrl(sheet.svg)} alt={`Sheet ${sheet.sheetNumber}, source page${sheet.sourcePageNumbers.length === 1 ? '' : 's'} ${sheet.sourcePageNumbers.join(', ')}`} />
          <figcaption>Sheet {sheet.sheetNumber} · source page{sheet.sourcePageNumbers.length === 1 ? '' : 's'} {sheet.sourcePageNumbers.join(', ')}</figcaption>
        </figure>)}
      </div>}
      {prompt.busy && <p role="status">{prompt.jobId ? 'Sending to output…' : 'Preparing print preview…'}</p>}
      <div className="dialog-actions">
        <button disabled={prompt.busy || prompt.loadingDevices || !options.printerId} onClick={() => void model.preparePrint()}>Prepare preview</button>
        <button disabled={prompt.busy || !prompt.jobId} onClick={() => void model.finishPrint('print')}>Print</button>
        <button disabled={prompt.busy || !prompt.jobId} onClick={() => void model.finishPrint('savePdf')}>Save print-layout PDF</button>
        <button disabled={prompt.busy} onClick={() => model.cancelPrint()}>Cancel</button>
      </div>
    </div>
  </div>;
}
