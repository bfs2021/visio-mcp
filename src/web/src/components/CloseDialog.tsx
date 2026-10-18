import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

/** Shown when the window is closed with unsaved changes: save them, throw them away, or stay. */
export function CloseDialog({ model }: { model: EditorModel }) {
  useModel(model);
  const p = model.closePrompt;
  if (!p) return null;
  const name = p.fileName ? p.fileName.split(/[\\/]/).pop() : 'This diagram';
  return (
    <div className="dialog-backdrop" role="presentation">
      <div className="dialog" role="alertdialog" aria-modal="true" aria-labelledby="close-title" aria-describedby="close-text" data-testid="close-prompt"
        onKeyDown={e => { if (e.key === 'Escape') void model.closeCancel(); }}>
        <h2 id="close-title">Save changes before closing?</h2>
        <p id="close-text">{name} has changes that were not saved. If you don&apos;t save, they are discarded.</p>
        {p.error && <p className="diag diag-warning" data-testid="close-error">{p.error}</p>}
        <div className="row dialog-actions">
          <button type="button" autoFocus data-testid="close-save" disabled={p.saving} onClick={() => void model.closeSave()}>{p.saving ? 'Saving…' : 'Save'}</button>
          <button type="button" data-testid="close-discard" disabled={p.saving} onClick={() => void model.closeDiscard()}>Don&apos;t save</button>
          <button type="button" data-testid="close-cancel" disabled={p.saving} onClick={() => void model.closeCancel()}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
