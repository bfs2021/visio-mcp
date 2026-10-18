import { test, expect, type Page } from '@playwright/test';
const MOVED = 300 * 72 / 25.4; // the field is in millimetres
import { openEditor, placeShape, snapshot, status } from './support';

/** Makes two edits in a page and returns what a host would have on disk: the checkpoint before them and the committed events after. */
async function recordSession(page: Page) {
  const checkpoint = await page.evaluate(() => (window as any).__editor.snapshot().document);
  const a = await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'kept');
  await page.getByTestId('field-x').fill('300');
  await page.getByTestId('field-x').press('Enter');
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements[0]!.bounds.x).toBeCloseTo(MOVED, 0);
  const events = await page.evaluate(() => (window as any).__editor.committedEvents());
  const finalDoc = (await snapshot(page)).document;
  return { checkpoint, events, finalDoc, id: a.id, documentId: checkpoint.id as string };
}

async function armHost(page: Page, rec: Awaited<ReturnType<typeof recordSession>>, opts: { auto?: boolean; loss?: string[]; events?: unknown[]; single?: boolean } = {}) {
  await page.evaluate(([r, o]) => {
    const calls: Record<string, unknown[]> = { fetch: [], discard: [], finish: [] };
    (window as any).__calls = calls;
    const events = (o.events ?? r.events) as unknown[];
    (window as any).__editor.setHost({
      recoveryStatus: async () => ({ ok: true, value: { lastDurableRevision: 7, lastCommittedRevision: 7, degraded: false, note: null } }),
      recoveryList: async () => ({ ok: true, value: { auto: !!o.auto, sessions: [
        { sessionId: 'old-session', documentId: r.documentId, title: 'Plan', originalPath: 'C:\\Work\\plan.diagram.json', pageCount: 1, objectCount: 1, checkpointRevision: 0, lastDurableRevision: events.length, lastWrite: '2026-10-18T02:00:00Z', loss: { tornTailDiscarded: true, corruptionAfterRevision: null, messages: o.loss ?? ['the last journal record was cut off by the crash and was discarded'] } },
        ...(o.single ? [] : [{ sessionId: 'other-session', documentId: 'other-doc', title: null, originalPath: null, pageCount: 3, objectCount: 42, checkpointRevision: 5, lastDurableRevision: 9, lastWrite: '2026-10-17T20:15:00Z', loss: { tornTailDiscarded: false, corruptionAfterRevision: null, messages: [] } }]),
      ] } }),
      recoveryFetch: async (id: string) => { calls.fetch!.push(id); return { ok: true, value: { sessionId: id, documentId: r.documentId, originalPath: 'C:\\Work\\plan.diagram.json', checkpointRevision: 0, document: r.checkpoint, events, lastDurableRevision: events.length, loss: { tornTailDiscarded: true, corruptionAfterRevision: null, messages: [] } } }; },
      recoveryDiscard: async (id: string) => { calls.discard!.push(id); return { ok: true, value: { discarded: id } }; },
      recoveryFinish: async (id: string, path: string | null) => { calls.finish!.push([id, path]); return { ok: true, value: { finished: id } }; },
      saveJson: async (asNew: boolean) => { (calls.save ??= []).push(asNew); await (window as any).__editor.markSaved('C:\\Work\\copy.diagram.json'); return { ok: true, value: { path: 'C:\\Work\\copy.diagram.json' } }; },
    });
  }, [rec, opts] as const);
}

async function freshPage(page: Page) {
  await page.reload();
  await page.waitForFunction(() => !!(window as any).__editor);
}

test('recovery: unfinished work is offered, restored exactly, marked unsaved, and reported', async ({ page }) => {
  const errors = await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  expect((await snapshot(page)).document.pages[0]!.elements).toHaveLength(0); // a fresh, empty editor
  await armHost(page, rec);
  await page.evaluate(() => void (window as any).__editor.checkRecovery());

  await expect(page.getByTestId('recovery-offer')).toBeVisible();
  const first = page.getByTestId('recovery-item').first();
  await expect(first.getByTestId('recovery-name')).toHaveText('Plan');
  await expect(first.getByTestId('recovery-path')).toHaveText('C:\\Work\\plan.diagram.json');
  await expect(first.getByTestId('recovery-summary')).toContainText('1 page, 1 object');
  await expect(first.getByTestId('recovery-summary')).toContainText('revision 3');
  await expect(page.getByText('cut off by the crash')).toBeVisible();
  await first.getByTestId('recovery-restore').click();

  await expect(page.getByTestId('recovery-report')).toBeVisible();
  await expect(page.getByTestId('recovery-line').first()).toContainText('Restored to revision 3');
  await expect(page.getByTestId('recovery-report')).toContainText('marked as unsaved');
  await expect(page.getByTestId('recovery-report')).toContainText('1 other unfinished session');
  // the document is the one that was lost, with the same ids and the same moved position
  const after = (await snapshot(page)).document.pages[0]!.elements;
  expect(after.map(e => e.id)).toEqual(rec.finalDoc.pages[0]!.elements.map((e: { id: string }) => e.id));
  expect(after[0]!.bounds.x).toBeCloseTo(MOVED, 0);
  const st = await status(page);
  expect(st.dirty).toBe(true); // recovered work stays dirty until it is saved
  const calls = await page.evaluate(() => (window as any).__calls);
  expect(calls.fetch).toContain('old-session'); // the thumbnails fetch every session; the restore fetched this one
  expect(calls).toMatchObject({ finish: [['old-session', 'C:\\Work\\plan.diagram.json']], discard: [] });
  await page.getByTestId('recovery-close').click();
  await expect(page.getByTestId('recovery-report')).toHaveCount(0);
  await expect(page.getByTestId('status-protected')).toContainText('revision 7');
  expect(errors).toEqual([]);
});

test('recovery: after a renderer crash the work is restored at once with a visible loss report', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  await armHost(page, rec, { auto: true });
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await expect(page.getByTestId('recovery-report')).toBeVisible(); // no question asked
  await expect(page.getByTestId('recovery-offer')).toHaveCount(0);
  expect((await snapshot(page)).document.pages[0]!.elements).toHaveLength(1);
  await expect(page.getByTestId('recovery-report')).toContainText('cut off by the crash');
});

test('recovery: a record that cannot be applied is reported, and only what is sound is restored', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  const broken = JSON.parse(JSON.stringify(rec.events));
  broken[1].resolvedDiff.upserts.elements[0].element.bounds.width = -3; // damaged but checksummed: valid JSON, invalid document
  await armHost(page, rec, { events: broken, loss: [], single: true });
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await page.getByTestId('recovery-restore').click();
  await expect(page.getByTestId('recovery-report')).toContainText('Restored to revision 1');
  await expect(page.getByTestId('recovery-report')).toContainText('Not recovered: revision 2 would make an invalid document');
  expect((await snapshot(page)).document.pages[0]!.elements[0]!.bounds.x).not.toBeCloseTo(MOVED, 0); // the move at revision 2 was not applied
});

test('recovery: not now keeps it for later, discard lets it go, and a restore after new edits opens beside them', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  await armHost(page, rec);
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await page.getByTestId('recovery-later').click();
  await expect(page.getByTestId('recovery-offer')).toHaveCount(0);
  expect((await page.evaluate(() => (window as any).__calls)).discard).toEqual([]);
  expect((await snapshot(page)).document.pages[0]!.elements).toHaveLength(0);

  await armHost(page, rec, { single: true });
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await page.getByTestId('recovery-discard').click();
  await expect(page.getByTestId('recovery-offer')).toHaveCount(0);
  expect((await page.evaluate(() => (window as any).__calls)).discard).toEqual(['old-session']);

  // editing while the question is open: the recovered work opens as its own tab, and the new work is left alone
  await armHost(page, rec, { single: true });
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await expect(page.getByTestId('recovery-offer')).toBeVisible();
  const made = await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'createShape', alias: 'new-work', bounds: { x: 10, y: 10, width: 40, height: 20 } }]));
  expect(made.ok).toBe(true);
  await page.getByTestId('recovery-restore').click();
  await expect(page.getByTestId('recovery-report')).toBeVisible();
  await expect(page.getByTestId('tab')).toHaveCount(2);
  expect((await snapshot(page)).document.pages[0]!.elements.map(e => e.id)).toEqual(rec.finalDoc.pages[0]!.elements.map((e: { id: string }) => e.id)); // the recovered tab is showing
  await page.getByTestId('recovery-close').click();
  await page.getByTestId('tab').nth(0).click();
  expect((await snapshot(page)).document.pages[0]!.elements.map(e => e.alias)).toEqual(['new-work']);
});

test('recovery: several sessions are listed with names, sizes and pictures, and each is discarded on its own', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  await armHost(page, rec);
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  const items = page.getByTestId('recovery-item');
  await expect(items).toHaveCount(2);
  await expect(page.getByTestId('recovery-offer')).toContainText('2 diagrams');
  await expect(items.nth(0).getByTestId('recovery-name')).toHaveText('Plan');
  await expect(items.nth(1).getByTestId('recovery-name')).toHaveText('Untitled diagram'); // no title and no file
  await expect(items.nth(1).getByTestId('recovery-summary')).toContainText('page'); // sizes are rebuilt from the journal once the picture is drawn
  await expect(items.nth(0).getByTestId('recovery-thumb')).toBeVisible(); // drawn from the recovered state
  const src = await items.nth(0).getByTestId('recovery-thumb').getAttribute('src');
  expect(src).toMatch(/^data:image\/png;base64,/);

  await items.nth(1).getByTestId('recovery-discard').click();
  await expect(items).toHaveCount(1);
  await expect(items.first().getByTestId('recovery-name')).toHaveText('Plan');
  expect((await page.evaluate(() => (window as any).__calls)).discard).toEqual(['other-session']);
});

test('recovery: after a restore the person is offered Save as, and saving closes the report', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await freshPage(page);
  await armHost(page, rec, { single: true });
  await page.evaluate(() => void (window as any).__editor.checkRecovery());
  await page.getByTestId('recovery-restore').click();
  await expect(page.getByTestId('recovery-save-as')).toBeVisible();
  await page.getByTestId('recovery-save-as').click();
  await expect(page.getByTestId('recovery-report')).toHaveCount(0);
  expect((await page.evaluate(() => (window as any).__calls)).save).toEqual([true]);
  await expect(page).toHaveTitle('copy.diagram.json - Diagram');
});
