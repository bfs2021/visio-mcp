import { useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { connectable, nameOf, roleOf } from '../editor/objectNav';
import { useModel } from './useModel';

/** Connect two objects without a pointer: choose where the connector starts and ends from lists. */
export function ConnectDialog({ model }: { model: EditorModel }) {
  useModel(model);
  const p = model.connectPrompt;
  const [from, setFrom] = useState<string | null>(null);
  const [to, setTo] = useState<string | null>(null);
  if (!p) return null;
  const page = model.currentPage;
  const picks = connectable(page);
  const a = from ?? p.from, b = to ?? p.to;
  const options = picks.map(e => <option key={e.id} value={e.id}>{nameOf(page, e)} ({roleOf(e)})</option>);
  return (
    <div className="dialog-backdrop" role="presentation">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="connect-title" data-testid="connect-dialog"
        onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); model.connectCancel(); } }}>
        <h2 id="connect-title">Connect objects</h2>
        <div className="row"><label>From <select autoFocus value={a} data-testid="connect-from" onChange={e => setFrom(e.target.value)}>{options}</select></label></div>
        <div className="row"><label>To <select value={b} data-testid="connect-to" onChange={e => setTo(e.target.value)}>{options}</select></label></div>
        {p.error && <p role="alert" className="diag diag-warning" data-testid="connect-error">{p.error}</p>}
        <div className="row dialog-actions">
          <button type="button" data-testid="connect-create" onClick={() => void model.connectCreate(a, b)}>Connect</button>
          <button type="button" data-testid="connect-cancel" onClick={() => model.connectCancel()}>Cancel</button>
        </div>
      </div>
    </div>
  );
}
