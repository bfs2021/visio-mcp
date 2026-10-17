import type { DiagnosticItem, EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

/** The compatibility report: what a Visio file did not carry, in plain words, with the choice to go ahead when that is a choice. */
export function CompatibilityDialog({ model }: { model: EditorModel }) {
  useModel(model);
  const r = model.report;
  if (!r) return null;
  const warnings = r.diagnostics.filter(d => d.severity !== 'info');
  const notes = r.diagnostics.filter(d => d.severity === 'info');
  const row = (d: DiagnosticItem, i: number) => (
    <li key={`${d.code}-${d.elementId ?? ''}-${i}`} className={`diag diag-${d.severity}`} data-testid="diag-item">
      <span className="diag-code">{d.code.replace(/_/g, ' ')}</span> {d.message}{d.action ? <em> ({d.action})</em> : null}
    </li>
  );
  return (
    <div className="dialog-backdrop" role="presentation">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="compat-title" data-testid="compat-dialog">
        <h2 id="compat-title">{r.title}</h2>
        <p data-testid="compat-summary">{r.summary}</p>
        {warnings.length > 0 && <><h3>Not carried across exactly</h3><ul>{warnings.map(row)}</ul></>}
        {notes.length > 0 && <details open={warnings.length === 0}><summary>{notes.length} note{notes.length === 1 ? '' : 's'}</summary><ul>{notes.map(row)}</ul></details>}
        <div className="row dialog-actions">
          {r.confirm && <button type="button" data-testid="compat-confirm" onClick={r.confirm.run}>{r.confirm.label}</button>}
          <button type="button" data-testid="compat-close" onClick={() => model.closeReport()}>{r.confirm ? 'Cancel' : 'Close'}</button>
        </div>
      </div>
    </div>
  );
}
