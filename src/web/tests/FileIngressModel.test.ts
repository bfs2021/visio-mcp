import { describe, expect, it } from 'vitest';
import { FileIngressModel, type IngressBatch } from '../src/workspace/FileIngressModel';

const batch: IngressBatch = {
  batchId: 'batch-1',
  items: [
    { ticketId: 'ticket-a', displayName: 'a.diagram.json', kind: 'json' },
    { ticketId: 'ticket-b', displayName: 'b.vsdx', kind: 'vsdx' },
    { ticketId: 'ticket-c', displayName: 'c.png', kind: 'image' },
  ],
  rejections: [],
};
const target = { documentId: 'doc-1', sessionId: 'session-1', baseRevision: 7 };

describe('FileIngressModel', () => {
  it('can drain a batch after an earlier empty drain', async () => {
    const model = new FileIngressModel();
    const open = async () => ({ opened: true as const });
    await model.drain(() => null, open);
    model.enqueue(batch, target);

    await model.drain(() => null, open);

    expect(model.items.map(item => item.status)).toEqual(['opened', 'opened', 'opened']);
  });

  it('processes each batch in input order and keeps the captured target with every item', async () => {
    const model = new FileIngressModel();
    model.enqueue(batch, target);
    const seen: string[] = [];

    await model.drain(() => null, async (item, pinned) => {
      seen.push(item.ticketId);
      expect(pinned).toEqual(target);
      return { opened: true as const };
    });

    expect(seen).toEqual(['ticket-a', 'ticket-b', 'ticket-c']);
    expect(model.items.map(item => item.status)).toEqual(['opened', 'opened', 'opened']);
  });

  it('advances pending items only when an open replaces the originally blank target session', async () => {
    const model = new FileIngressModel();
    model.enqueue(batch, target);
    const replacement = { documentId: target.documentId, sessionId: 'session-2', baseRevision: 0 };
    const seen: string[] = [];

    await model.drain(() => null, async item => {
      seen.push(item.target.sessionId);
      return { opened: true, ...(item.ticketId === 'ticket-a' ? { nextTarget: replacement } : {}) };
    });

    expect(seen).toEqual(['session-1', 'session-2', 'session-2']);
  });

  it('leaves the remaining items pending when a guard becomes active between opens', async () => {
    const model = new FileIngressModel();
    model.enqueue(batch, target);
    let openCalls = 0;
    let opened = 0;

    await model.drain(() => opened === 1 ? 'A dialog is open' : null, async () => {
      openCalls++;
      opened++;
      return { opened: true as const };
    });

    expect(openCalls).toBe(1);
    expect(model.items.map(item => item.status)).toEqual(['opened', 'pending', 'pending']);
    expect(model.blockedReason).toBe('A dialog is open');
  });

  it('cancels only pending tickets and reports each completed outcome', async () => {
    const model = new FileIngressModel();
    model.enqueue(batch, target);
    const dismissed: string[][] = [];
    const dismiss = (ids: string[]) => { dismissed.push(ids); };
    const open = async (item: { ticketId: string }) => {
      if (item.ticketId === 'ticket-a') model.cancel(batch.batchId, dismiss);
      return { opened: true as const };
    };

    await model.drain(() => null, open);

    expect(dismissed).toEqual([['ticket-b', 'ticket-c']]);
    expect(model.items.map(item => item.status)).toEqual(['opened', 'cancelled', 'cancelled']);
  });

  it('keeps earlier successes and converts an individual failure to an item result', async () => {
    const model = new FileIngressModel();
    model.enqueue(batch, target);

    await model.drain(() => null, async item => item.ticketId === 'ticket-b'
      ? { opened: false, error: 'invalid file' }
      : { opened: true as const });

    expect(model.items.map(item => item.status)).toEqual(['opened', 'failed', 'opened']);
    expect(model.items[1]?.message).toBe('invalid file');
  });
});
