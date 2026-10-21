import { test, expect, type Page } from '@playwright/test';
import { openEditor, placeShape, snapshot } from './support';

const tabs = (page: Page) => page.evaluate(() => (window as any).__editor.tabs() as Array<{ label: string; unsaved: boolean; documentId: string; active: boolean }>);

/** Records what a host would have on disk for one unsaved document: the checkpoint before an edit and the events after. */
async function recordSession(page: Page) {
  const checkpoint = await page.evaluate(() => (window as any).__editor.snapshot().document);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'kept');
  const events = await page.evaluate(() => (window as any).__editor.committedEvents());
  const finalDoc = (await snapshot(page)).document;
  return { checkpoint, events, finalDoc };
}

type Armed = { memory: unknown; sessions: unknown[]; failOpen?: string[]; events: unknown[]; checkpoint: unknown };
async function armHost(page: Page, a: Armed) {
  await page.evaluate(arm => {
    const calls: Record<string, unknown[]> = { opened: [], finished: [], saves: [] };
    (window as any).__calls = calls;
    const ed = (window as any).__editor;
    ed.setHost({
      workspaceLoad: async () => ({ ok: true, value: arm.memory }),
      workspaceSave: async (tabs: unknown, active: unknown) => { calls.saves!.push({ tabs, active }); return { ok: true, value: { saved: 1 } }; },
      recoveryStatus: async () => ({ ok: true, value: { lastDurableRevision: 1, lastCommittedRevision: 1, degraded: false, note: null } }),
      recoveryList: async () => ({ ok: true, value: { auto: false, sessions: arm.sessions } }),
      recoveryFetch: async (id: string) => ({ ok: true, value: { sessionId: id, documentId: (arm.checkpoint as any).id, originalPath: null, checkpointRevision: 0, document: arm.checkpoint, events: arm.events, lastDurableRevision: (arm.events as unknown[]).length, loss: { tornTailDiscarded: false, corruptionAfterRevision: null, messages: [] } } }),
      recoveryFinish: async (id: string, path: string | null) => { calls.finished!.push([id, path]); return { ok: true, value: { finished: id } }; },
      recoveryDiscard: async (id: string) => ({ ok: true, value: { discarded: id } }),
      openJson: async (path?: string) => {
        calls.opened!.push(path ?? null);
        if (path && arm.failOpen?.includes(path)) return { ok: false, error: { code: 'io_error', message: 'no such file', retryable: false } };
        await ed.markSaved(path);
        return { ok: true, value: { path } };
      },
    });
  }, a);
}
const session = (id: string, over: object = {}) => ({ sessionId: id, documentId: 'doc-' + id, title: 'Plan', originalPath: null, pageCount: 1, objectCount: 1, checkpointRevision: 0, lastDurableRevision: 1, lastWrite: '2026-10-18T02:00:00Z', loss: { tornTailDiscarded: false, corruptionAfterRevision: null, messages: [] }, ...over });
const restore = (page: Page) => page.evaluate(() => (window as any).__editor.restoreWorkspace());
const fresh = async (page: Page) => { await page.reload(); await page.waitForFunction(() => !!(window as any).__editor); };

test('the last run comes back: unsaved work and saved files, in order, with the active tab, and a notice after a crash', async ({ page }) => {
  const errors = await openEditor(page);
  const rec = await recordSession(page);
  await fresh(page);
  await armHost(page, {
    checkpoint: rec.checkpoint, events: rec.events,
    sessions: [session('s1'), session('orphan', { title: 'Lost' })],
    memory: { found: true, cleanExit: false, activeDocumentId: 'b', tabs: [
      { documentId: 'a', sessionId: 's1', path: null, title: 'Plan', unsaved: true },
      { documentId: 'b', sessionId: 's2', path: 'C:\\Work\\two.diagram.json', title: 'two.diagram.json', unsaved: false },
      { documentId: 'c', sessionId: 's3', path: null, title: 'Untitled 3', unsaved: false },
    ] },
  });
  await restore(page);
  const t = await tabs(page);
  expect(t.map(x => x.label)).toEqual(['Untitled 1', 'two.diagram.json']); // the blank start tab was used for the first; the untouched untitled tab was dropped
  expect(t.map(x => x.unsaved)).toEqual([true, false]);
  expect(t.map(x => x.active)).toEqual([false, true]);
  const calls = await page.evaluate(() => (window as any).__calls);
  expect(calls.opened).toEqual(['C:\\Work\\two.diagram.json']);
  expect(calls.finished).toEqual([['s1', null]]);
  await expect(page.getByTestId('restore-banner')).toContainText('Restored 2 documents after an unexpected exit');
  await expect(page.getByTestId('recovery-offer')).toBeVisible(); // the session no tab owns is still asked about
  await expect(page.getByTestId('recovery-item')).toHaveCount(1);
  await page.getByTestId('recovery-later').click();
  await page.getByTestId('tab').nth(0).click();
  expect((await snapshot(page)).document.pages[0]!.elements.map(e => e.id)).toEqual(rec.finalDoc.pages[0]!.elements.map((e: { id: string }) => e.id));
  expect(errors).toEqual([]);
});

test('Recover on an orphan session opens it as a tab beside the others', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await fresh(page);
  await armHost(page, { checkpoint: rec.checkpoint, events: rec.events, sessions: [session('orphan')], memory: { found: true, cleanExit: true, activeDocumentId: 'b', tabs: [{ documentId: 'b', sessionId: 's2', path: 'C:\\Work\\two.diagram.json', title: 'x', unsaved: false }] } });
  await restore(page);
  await expect(page.getByTestId('restore-banner')).toHaveCount(0); // a clean exit with nothing unsaved reopens silently
  expect(await tabs(page)).toHaveLength(1);
  await page.getByTestId('recovery-restore').click();
  await expect(page.getByTestId('recovery-report')).toBeVisible();
  expect((await tabs(page)).map(x => x.label)).toEqual(['two.diagram.json', 'Untitled 2']);
  expect((await tabs(page)).map(x => x.unsaved)).toEqual([false, true]);
});

test('a file that cannot be reopened is reported and leaves no empty tab', async ({ page }) => {
  await openEditor(page);
  await armHost(page, { checkpoint: {}, events: [], sessions: [], failOpen: ['C:\\Gone\\x.diagram.json'], memory: { found: true, cleanExit: true, activeDocumentId: null, tabs: [
    { documentId: 'a', sessionId: 's1', path: 'C:\\Gone\\x.diagram.json', title: 'x.diagram.json', unsaved: false },
    { documentId: 'b', sessionId: 's2', path: 'C:\\Work\\ok.diagram.json', title: 'ok.diagram.json', unsaved: false },
  ] } });
  await restore(page);
  expect((await tabs(page)).map(x => x.label)).toEqual(['ok.diagram.json']);
  await expect(page.getByTestId('restore-banner')).toContainText('could not be reopened');
  await page.getByTestId('restore-banner-dismiss').click();
  await expect(page.getByTestId('restore-banner')).toHaveCount(0);
});

test('no remembered tabs falls back to offering unfinished work', async ({ page }) => {
  await openEditor(page);
  const rec = await recordSession(page);
  await fresh(page);
  await armHost(page, { checkpoint: rec.checkpoint, events: rec.events, sessions: [session('s1')], memory: { found: false } });
  await restore(page);
  await expect(page.getByTestId('recovery-offer')).toBeVisible();
  expect(await tabs(page)).toHaveLength(1);
});

test('the open tabs are sent to the host as they change, and an unchanged list is not sent twice', async ({ page }) => {
  await openEditor(page);
  await armHost(page, { checkpoint: {}, events: [], sessions: [], memory: { found: false } });
  await restore(page); // starts remembering
  await page.getByTestId('tab-new').click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  const saves = async () => (await page.evaluate(() => (window as any).__calls)).saves as any[];
  await expect.poll(async () => (await saves()).at(-1)?.tabs.map((t: any) => t.unsaved)).toEqual([false, true]);
  const last = (await saves()).at(-1);
  expect(last.tabs.map((t: any) => t.title)).toEqual(['Untitled 1', 'Untitled 2']);
  expect(last.active).toBe(last.tabs[1].documentId);
  const n = (await saves()).length;
  await page.evaluate(() => (window as any).__editor.persist());
  await page.waitForTimeout(400);
  expect((await saves()).length).toBe(n); // nothing changed, nothing sent
  await page.getByTestId('tab').nth(0).click();
  await expect.poll(async () => (await saves()).at(-1)?.active).toBe(last.tabs[0].documentId);
});

test('preferences read warning remains visible after restoring and activating other tabs', async ({ page }) => {
  await page.addInitScript(() => {
    const listeners: Array<(event: { data: unknown }) => void> = [];
    (window as any).chrome = { webview: {
      addEventListener: (_type: string, listener: (event: { data: unknown }) => void) => listeners.push(listener),
      postMessage: (message: any) => {
        if (message.kind !== 'request') return;
        queueMicrotask(() => listeners.forEach(listener => listener({ data: {
          protocolVersion: 1, kind: 'response', requestId: message.requestId,
          error: { code: 'io_error', message: message.method === 'host.preferences.get' ? 'Settings could not be read' : 'Host fixture unavailable', retryable: false },
        } })));
      },
    } };
  });
  await openEditor(page);
  await armHost(page, {
    checkpoint: (await snapshot(page)).document, events: [], sessions: [],
    memory: { found: true, cleanExit: true, activeDocumentId: 'b', tabs: [
      { documentId: 'a', sessionId: 's1', path: 'C:\\Work\\one.diagram.json', title: 'one.diagram.json', unsaved: false },
      { documentId: 'b', sessionId: 's2', path: 'C:\\Work\\two.diagram.json', title: 'two.diagram.json', unsaved: false },
    ] },
  });
  await restore(page);
  await expect(page.getByTestId('tab')).toHaveCount(2);
  expect((await tabs(page))[1]!.active).toBe(true);
  await expect(page.getByTestId('preferences-diagnostic')).toContainText('Settings could not be read');
  await page.getByTestId('tab').nth(0).click();
  await expect(page.getByTestId('preferences-diagnostic')).toContainText('Settings could not be read');
  await page.getByTestId('tab').nth(1).click();
  await expect(page.getByTestId('preferences-diagnostic')).toContainText('Settings could not be read');
});
