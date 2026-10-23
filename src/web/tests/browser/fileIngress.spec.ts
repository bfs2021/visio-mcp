import { test, expect } from '@playwright/test';
import { openEditor } from './support';

test('native ingress notifications appear in order and open through the host ticket method', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(async () => {
    const ed = (window as any).__editor;
    const calls: string[] = [];
    (window as any).__ingressCalls = calls;
    ed.setHost({
      openIngress: async (ticketId: string) => { calls.push(ticketId); return { ok: true, value: { path: ticketId } }; },
      dismissIngress: async (ticketIds: string[]) => ({ ok: true, value: { dismissed: ticketIds.length } }),
    });
    ed.enqueueIngress({
      batchId: 'browser-batch',
      items: [
        { ticketId: 'ticket-one', displayName: 'one.diagram.json', kind: 'json' },
        { ticketId: 'ticket-two', displayName: 'two.vsdx', kind: 'vsdx' },
      ],
      rejections: [],
    });
    await ed.processIngress();
  });

  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue.locator('li')).toHaveCount(2);
  await expect(queue).toContainText('one.diagram.json');
  await expect(queue).toContainText('two.vsdx');
  await expect.poll(() => page.evaluate(() => (window as any).__ingressCalls)).toEqual(['ticket-one', 'ticket-two']);
  await expect(queue.locator('[data-status="opened"]')).toHaveCount(2);
});
