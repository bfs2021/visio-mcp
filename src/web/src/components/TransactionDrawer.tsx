import { useState } from 'react';
import type { TransactionSummary } from '../contracts/generated';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

const short = (id: string) => id.slice(0, 8);

function IdList({ label, ids, testId }: { label: string; ids: readonly string[]; testId: string }) {
  if (!ids.length) return null;
  return <p className="small" data-testid={testId}><strong>{label} ({ids.length}):</strong> {ids.map(i => <code key={i} title={i}>{short(i)}</code>).reduce<React.ReactNode[]>((a, c, i) => (i ? [...a, ', ', c] : [c]), [])}</p>;
}

/** What changed, newest first. Only the transaction on top of the undo stack can be undone: nothing here reaches back over later changes. */
export function TransactionDrawer({ model }: { model: EditorModel }) {
  useModel(model);
  const [picked, setPicked] = useState<string | null>(null);
  const list = model.engine.recentTransactions(50).slice().reverse();
  const head = model.engine.undoHead();
  const selected: TransactionSummary | undefined = list.find(t => t.transactionId === picked) ?? list[0];
  const source = selected ? model.scriptSources.get(selected.transactionId) : undefined;
  const canUndo = !!selected && head?.transactionId === selected.transactionId;

  return (
    <div className="tx-drawer" data-testid="tx-drawer">
      <ol className="tx-list" aria-label="Transactions">
        {list.length === 0 && <li className="muted">No changes yet.</li>}
        {list.map(t => (
          <li key={t.transactionId}>
            <button type="button" className={selected?.transactionId === t.transactionId ? 'active' : ''} data-testid={`tx-${t.revision}`} onClick={() => setPicked(t.transactionId)}>
              <span className="rev">r{t.revision}</span>
              <span className={`src src-${t.source}`}>{t.source}</span>
              <span className="desc">{t.description}</span>
              <span className="muted">+{t.created.length} ~{t.changed.length} −{t.deleted.length}</span>
            </button>
          </li>
        ))}
      </ol>
      <div className="tx-detail" data-testid="tx-detail">
        {selected ? (
          <>
            <h3>Revision {selected.revision} <small className="muted">from {selected.previousRevision} · {selected.source}</small></h3>
            <p className="small">{selected.description}</p>
            <IdList label="Created" ids={selected.created} testId="tx-created" />
            <IdList label="Changed" ids={selected.changed} testId="tx-changed" />
            <IdList label="Deleted" ids={selected.deleted} testId="tx-deleted" />
            {selected.changedPageIds.length > 0 && <IdList label="Pages" ids={selected.changedPageIds} testId="tx-pages" />}
            {selected.changedLayerIds.length > 0 && <IdList label="Layers" ids={selected.changedLayerIds} testId="tx-layers" />}
            {selected.changedAssetIds.length > 0 && <p className="small" data-testid="tx-assets"><strong>Assets:</strong> {selected.changedAssetIds.join(', ')}</p>}
            {source !== undefined && <pre className="tx-source" data-testid="tx-source">{source}</pre>}
            <div className="row">
              <button type="button" data-testid="tx-undo" disabled={!canUndo} onClick={() => void model.undo()}>Undo this transaction</button>
              {!canUndo && (
                <span className="muted small" data-testid="tx-undo-note">
                  {selected.source === 'undo' || selected.source === 'redo'
                    ? 'This records an undo or redo; use Redo or Undo on the Edit menu.'
                    : 'Later changes sit on top of this one. Undo those first: an earlier transaction is never undone over them.'}
                </span>
              )}
            </div>
          </>
        ) : <p className="muted">Select a transaction.</p>}
      </div>
    </div>
  );
}
