import { useSyncExternalStore } from 'react';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';

export function FileIngressQueue({ ws }: { ws: WorkspaceModel }) {
  useSyncExternalStore(ws.ingress.subscribe, ws.ingress.getVersion);
  const batches = [...new Set(ws.ingress.items.map(item => item.batchId))];
  if (!batches.length) return null;
  const dismiss = (batchId: string) => ws.ingress.dismissBatch(batchId, ticketIds => { void ws.host.dismissIngress(ticketIds); });
  return <aside className="file-ingress-queue" aria-label="Dropped files" data-testid="file-ingress-queue">
    <div className="file-ingress-heading"><strong>Dropped files</strong>{ws.ingress.blockedReason && <span role="status">{ws.ingress.blockedReason}</span>}</div>
    {batches.map(batchId => <section className="file-ingress-batch" key={batchId} aria-label="File drop batch">
      <ul>{ws.ingress.items.filter(item => item.batchId === batchId).map(item => <li key={item.ticketId} data-status={item.status}>
        <span className="file-ingress-name" title={item.displayName}>{item.displayName}</span>
        <span className={`file-ingress-status status-${item.status}`} role={item.status === 'failed' ? 'alert' : 'status'}>
          {item.status === 'opening' ? 'Opening…' : item.status === 'opened' ? (item.message ?? 'Opened') : item.status === 'failed' ? (item.message ?? 'Failed') : item.status === 'cancelled' ? 'Cancelled' : 'Waiting'}
        </span>
      </li>)}</ul>
      <div className="file-ingress-actions">
        {ws.ingress.items.some(item => item.batchId === batchId && (item.status === 'pending' || item.status === 'opening')) && <button type="button" onClick={() => ws.cancelIngress(batchId)}>Cancel remaining</button>}
        <button type="button" disabled={ws.ingress.items.some(item => item.batchId === batchId && item.status === 'opening')} onClick={() => dismiss(batchId)}>Dismiss</button>
      </div>
    </section>)}
  </aside>;
}
