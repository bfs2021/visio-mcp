import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

/** Offers unfinished work back, and afterwards says what was and was not recovered. */
export function RecoveryDialog({ model }: { model: EditorModel }) {
  useModel(model);
  const offer = model.recoveryOffer;
  const report = model.recoveryReport;
  if (!offer && !report) return null;
  if (report) {
    return (
      <div className="dialog-backdrop" role="presentation">
        <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="rec-title" data-testid="recovery-report">
          <h2 id="rec-title">{report.title}</h2>
          <ul>{report.lines.map((l, i) => <li key={i} data-testid="recovery-line">{l}</li>)}</ul>
          <div className="row dialog-actions"><button type="button" data-testid="recovery-close" onClick={() => model.closeRecoveryReport()}>OK</button></div>
        </div>
      </div>
    );
  }
  const o = offer!;
  const when = new Date(o.lastWrite);
  return (
    <div className="dialog-backdrop" role="presentation">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="rec-title" data-testid="recovery-offer">
        <h2 id="rec-title">Recover unsaved work?</h2>
        <p data-testid="recovery-summary">
          An earlier session ended with changes that were not saved{o.originalPath ? ` (from ${o.originalPath})` : ''}. The last change that reached disk is revision {o.lastDurableRevision}
          {Number.isNaN(when.getTime()) ? '' : `, at ${when.toLocaleString()}`}.
        </p>
        {o.loss.messages.length > 0 && <><h3>What could not be recovered</h3><ul>{o.loss.messages.map((m, i) => <li key={i} className="diag diag-warning">{m}</li>)}</ul></>}
        <div className="row dialog-actions">
          <button type="button" data-testid="recovery-restore" onClick={() => void model.restoreOffered()}>Recover</button>
          <button type="button" data-testid="recovery-later" onClick={() => model.deferOffered()}>Not now</button>
          <button type="button" data-testid="recovery-discard" onClick={() => void model.discardOffered()}>Discard</button>
        </div>
      </div>
    </div>
  );
}
