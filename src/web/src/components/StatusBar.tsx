import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

export function StatusBar({ model }: { model: EditorModel }) {
  useModel(model);
  const s = model.engine.status();
  return (
    <footer className="statusbar">
      <span id="status" className={`message ${model.status.kind}`} data-testid="status-message" role="status">{model.status.text}</span>
      <span data-testid="status-revision">Revision {model.engine.revision}</span>
      <span data-testid="status-selection">{model.targets.length} selected</span>
      {model.protectedRevision !== null && (
        <span data-testid="status-protected" className={model.protectionNote ? 'message warning' : ''} title={model.protectionNote ?? 'The newest revision written to the recovery journal'}>
          {model.protectionNote ? `Recovery behind: ${model.protectionNote}` : `Protected to revision ${model.protectedRevision}`}
        </span>
      )}
      <span data-testid="status-saved">{s.savedPath ? `${s.dirty ? 'Unsaved changes' : 'Saved'}: ${s.savedPath}` : 'Not saved'}</span>
    </footer>
  );
}
