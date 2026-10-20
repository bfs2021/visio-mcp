import type { Scope } from '../contracts/generated';

export type IngressKind = 'json' | 'vsdx' | 'image';
export interface IngressItem { ticketId: string; displayName: string; kind: IngressKind }
export interface IngressDropTarget extends Scope { baseRevision: number; clientX: number; clientY: number }
export interface IngressBatch { batchId: string; items: IngressItem[]; rejections: string[]; target?: IngressDropTarget }
export interface IngressTarget extends Scope { baseRevision: number; placement?: { pageId: string; x: number; y: number } }
export type IngressStatus = 'pending' | 'opening' | 'opened' | 'failed' | 'cancelled';
export interface IngressQueueItem extends IngressItem {
  batchId: string;
  target: IngressTarget;
  status: IngressStatus;
  message?: string;
}
export type IngressOpenResult = { opened: true; path?: string; existingDocumentId?: string; nextTarget?: IngressTarget } | { opened: false; error: string };

/** Ordered, visible state for native file tickets. It never accepts or retains file paths. */
export class FileIngressModel {
  private queue: IngressQueueItem[] = [];
  private readonly listeners = new Set<() => void>();
  private running: Promise<void> | null = null;
  private version = 0;
  blockedReason: string | null = null;

  get items(): readonly IngressQueueItem[] { return this.queue; }
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => this.listeners.delete(listener); };
  getVersion = () => this.version;

  enqueue(batch: IngressBatch, target: IngressTarget): void {
    if (!batch || typeof batch.batchId !== 'string' || !batch.batchId || !Array.isArray(batch.items)) return;
    if (this.queue.some(item => item.batchId === batch.batchId)) return;
    const safeTarget = { ...target, ...(target.placement ? { placement: { ...target.placement } } : {}) };
    for (const item of batch.items) {
      if (!item || typeof item.ticketId !== 'string' || !item.ticketId || !['json', 'vsdx', 'image'].includes(item.kind)) continue;
      this.queue.push({ ...item, batchId: batch.batchId, target: safeTarget, status: 'pending' });
    }
    if (batch.rejections.length) {
      this.queue.push({ batchId: batch.batchId, ticketId: `${batch.batchId}:rejected`, displayName: 'Some dropped items were rejected', kind: 'json', target: safeTarget, status: 'failed', message: batch.rejections.join(' ') });
    }
    this.changed();
  }

  async drain(guard: (item: IngressQueueItem, target: IngressTarget) => string | null, open: (item: IngressQueueItem, target: IngressTarget) => Promise<IngressOpenResult>): Promise<void> {
    if (this.running) return this.running;
    this.running = Promise.resolve().then(async () => {
      try {
        for (const item of this.queue) {
          if (item.status !== 'pending') continue;
          const reason = guard(item, item.target);
          if (reason) { if (this.blockedReason !== reason) { this.blockedReason = reason; this.changed(); } return; }
          if (this.blockedReason !== null) { this.blockedReason = null; this.changed(); }
          item.status = 'opening';
          item.message = undefined;
          this.changed();
          try {
            const result = await open(item, item.target);
            if (this.isCancelled(item)) continue;
            if (result.opened) {
              item.status = 'opened'; item.message = result.existingDocumentId ? 'Already open; activated the existing drawing.' : undefined;
              if (result.nextTarget) for (const pending of this.queue) if (pending.batchId === item.batchId && pending.status === 'pending') pending.target = result.nextTarget;
            }
            else { item.status = 'failed'; item.message = result.error; }
          } catch (error) {
            if (!this.isCancelled(item)) { item.status = 'failed'; item.message = error instanceof Error ? error.message : String(error); }
          }
          this.changed();
        }
      } finally { this.running = null; }
    });
    return this.running;
  }

  cancel(batchId: string, dismiss: (ticketIds: string[]) => void): void {
    const pending = this.queue.filter(item => item.batchId === batchId && item.status === 'pending');
    for (const item of pending) item.status = 'cancelled';
    if (pending.length) dismiss(pending.map(item => item.ticketId));
    this.changed();
  }

  dismissBatch(batchId: string, dismiss: (ticketIds: string[]) => void): void {
    if (this.queue.some(item => item.batchId === batchId && item.status === 'opening')) return;
    this.cancel(batchId, dismiss);
    this.queue = this.queue.filter(item => item.batchId !== batchId);
    this.changed();
  }

  private changed(): void { this.version++; for (const listener of this.listeners) listener(); }
  private isCancelled(item: IngressQueueItem): boolean { return item.status === 'cancelled'; }
}
