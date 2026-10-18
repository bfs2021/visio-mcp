import { useEffect } from 'react';
import type { EditorModel, RecoveryOffer } from '../editor/EditorModel';
import { useModel } from './useModel';

const nameOf = (o: RecoveryOffer): string => o.title && o.title !== 'Untitled' ? o.title : (o.originalPath?.split(/[\\/]/).pop() ?? 'Untitled diagram');
const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`;

/** Offers unfinished work back, and afterwards says what was and was not recovered. */
export function RecoveryDialog({ model, onRecover }: { model: EditorModel; onRecover?: (sessionId: string) => void }) {
  useModel(model);
  const offers = model.recoveryOffers;
  const report = model.recoveryReport;
  const ids = offers.map(o => o.sessionId).join('|');
  useEffect(() => { for (const o of model.recoveryOffers) void model.loadOfferPreview(o.sessionId); }, [model, ids]);
  if (offers.length === 0 && !report) return null;
  if (report) {
    return (
      <div className="dialog-backdrop" role="presentation">
        <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="rec-title" data-testid="recovery-report">
          <h2 id="rec-title">{report.title}</h2>
          <ul>{report.lines.map((l, i) => <li key={i} data-testid="recovery-line">{l}</li>)}</ul>
          <div className="row dialog-actions">
            {report.saveAs && <button type="button" data-testid="recovery-save-as" onClick={() => void model.saveRecoveredAs()}>Save as…</button>}
            <button type="button" data-testid="recovery-close" onClick={() => model.closeRecoveryReport()}>OK</button>
          </div>
        </div>
      </div>
    );
  }
  return (
    <div className="dialog-backdrop" role="presentation">
      <div className="dialog recovery-list" role="dialog" aria-modal="true" aria-labelledby="rec-title" data-testid="recovery-offer">
        <h2 id="rec-title">{offers.length === 1 ? 'Recover unsaved work?' : `Recover unsaved work? (${offers.length} diagrams)`}</h2>
        <p>{offers.length === 1 ? 'An earlier session ended with changes that were not saved.' : 'Earlier sessions ended with changes that were not saved.'} Recover opens one in this window; Discard removes it for good.</p>
        <ul className="recovery-items">
          {offers.map(o => {
            const when = new Date(o.lastWrite);
            const preview = model.offerPreviews.get(o.sessionId);
            const stats = model.offerStats.get(o.sessionId);
            return (
              <li key={o.sessionId} className="recovery-item" data-testid="recovery-item" data-session={o.sessionId}>
                <div className="recovery-thumb" aria-hidden="true">{preview ? <img src={preview} alt="" data-testid="recovery-thumb" /> : <span>{preview === null ? 'No preview' : 'Loading…'}</span>}</div>
                <div className="recovery-info">
                  <strong data-testid="recovery-name">{nameOf(o)}</strong>
                  {o.originalPath && <div className="muted" data-testid="recovery-path">{o.originalPath}</div>}
                  <div data-testid="recovery-summary">
                    {stats ? `${plural(stats.pages, 'page')}, ${plural(stats.objects, 'object')}.` : `${plural(o.pageCount, 'page')}, ${plural(o.objectCount, 'object')} at the last checkpoint.`} The last change that reached disk is revision {o.lastDurableRevision}
                    {Number.isNaN(when.getTime()) ? '' : `, at ${when.toLocaleString()}`}.
                  </div>
                  {o.loss.messages.length > 0 && <ul>{o.loss.messages.map((m, i) => <li key={i} className="diag diag-warning">{m}</li>)}</ul>}
                  <div className="row recovery-actions">
                    <button type="button" data-testid="recovery-restore" onClick={() => (onRecover ? onRecover(o.sessionId) : void model.restoreOffered(o.sessionId))}>Recover</button>
                    <button type="button" data-testid="recovery-discard" onClick={() => void model.discardOffered(o.sessionId)}>Discard</button>
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
        <div className="row dialog-actions"><button type="button" data-testid="recovery-later" onClick={() => model.deferOffered()}>Not now</button></div>
      </div>
    </div>
  );
}
