import { useSyncExternalStore } from 'react';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';

export function FileIngressQueue({ ws }: { ws: WorkspaceModel }) {
  useSyncExternalStore(ws.ingress.subscribe, ws.ingress.getVersion);
  const batches = [...new Set(ws.ingress.items.map(item => item.batchId))];
  if (!batches.length) return null;
  const dismiss = (batchId: string) => ws.ingress.dismissBatch(batchId, ticketIds => { void ws.host.dismissIngress(ticketIds); });
  const labelFor = (item: (typeof ws.ingress.items)[number]) => ws.tabs.find(tab => tab.engine.scope().documentId === item.target.documentId && tab.engine.scope().sessionId === item.target.sessionId)?.tabLabel ?? 'drawing';
  return <aside className="file-ingress-queue" aria-label="Dropped files" data-testid="file-ingress-queue">
    <div className="file-ingress-heading"><strong>Dropped files</strong>{ws.ingress.blockedReason && <span role="status">{ws.ingress.blockedReason}</span>}</div>
    {batches.map(batchId => <section className="file-ingress-batch" key={batchId} aria-label="File drop batch">
      <ul>{ws.ingress.items.filter(item => item.batchId === batchId).map(item => <li key={item.ticketId} data-status={item.status}>
        <span className="file-ingress-name" title={item.displayName}>{item.displayName}</span>
        <div className={`file-ingress-status status-${item.status}`} role={item.status === 'failed' ? 'alert' : 'status'}>
          {item.status === 'opening' ? 'Opening…' : item.status === 'offered' ? `Picture ready · ${labelFor(item)}` : item.status === 'opened' ? (item.message ?? 'Opened') : item.status === 'placed' || item.status === 'kept' ? (item.message ?? item.status) : item.status === 'failed' ? (item.message ?? 'Failed') : item.status === 'cancelled' ? 'Cancelled' : 'Waiting'}
          {item.message && item.status === 'offered' && <span>{item.message}</span>}
        </div>
        {item.image && <img className="file-ingress-preview" src={`/assets/${item.image.asset.sha256}`} alt={`Preview of ${item.image.asset.name}`} />}
        {item.image?.notes.map((note, index) => <span className="file-ingress-note" key={index}>{note}</span>)}
        {item.status === 'offered' && <div className="file-ingress-actions">
          <button type="button" onClick={() => void ws.placeIngressImage(item.ticketId)}>Place in {labelFor(item)}</button>
          {item.message?.includes('Choose a fresh placement') && <button type="button" onClick={() => ws.chooseCurrentTargetForIngressImage(item.ticketId)}>Choose current drawing</button>}
          <button type="button" onClick={() => ws.keepIngressImage(item.ticketId)}>Keep in library</button>
        </div>}
      </li>)}</ul>
      <div className="file-ingress-actions">
        {ws.ingress.items.some(item => item.batchId === batchId && (item.status === 'pending' || item.status === 'opening')) && <button type="button" onClick={() => ws.cancelIngress(batchId)}>Cancel remaining</button>}
        <button type="button" disabled={ws.ingress.items.some(item => item.batchId === batchId && item.status === 'opening')} onClick={() => dismiss(batchId)}>Dismiss</button>
      </div>
    </section>)}
  </aside>;
}
