import { test, expect, type Page } from '@playwright/test';
import { openEditor, placeShape, snapshot } from './support';

const tabs = (page: Page) => page.evaluate(() => (window as any).__editor.tabs() as Array<{ label: string; unsaved: boolean; documentId: string; active: boolean }>);
const count = async (page: Page) => (await snapshot(page)).document.pages[0]!.elements.length;

/** A host that records what the page asks of it. Opening "loads" a file by marking the active document saved under that path. */
async function armHost(page: Page, opts: { openPath?: string | null; saveWorks?: boolean } = {}) {
  await page.evaluate(o => {
    const calls: string[] = [];
    (window as any).__calls = calls;
    const ed = (window as any).__editor;
    ed.setHost({
      openJson: async () => {
        calls.push('open');
        if (o.openPath === null) return { ok: true, value: { cancelled: true } };
        await ed.markSaved(o.openPath);
        return { ok: true, value: { path: o.openPath } };
      },
      saveJson: async () => {
        calls.push('save');
        if (o.saveWorks === false) return { ok: true, value: { cancelled: true } };
        await ed.markSaved(`C:\\Work\\saved${calls.length}.diagram.json`);
        return { ok: true, value: { path: 'x' } };
      },
      recoveryDiscard: async (id: string) => { calls.push('discard'); return { ok: true, value: { discarded: id } }; },
      appClose: async (discard: boolean) => { calls.push(`close:${discard ? 'discard' : 'keep'}`); return { ok: true, value: { closing: true } }; },
      appCloseCancelled: async () => { calls.push('cancelled'); return { ok: true, value: { cancelled: true } }; },
    });
  }, opts);
}
const calls = (page: Page) => page.evaluate(() => (window as any).__calls as string[]);

test('a new tab starts blank, switching shows each document, and the unsaved dot follows the edits', async ({ page }) => {
  const errors = await openEditor(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'first');
  await page.getByTestId('tab-new').click();
  await expect(page.getByTestId('tab')).toHaveCount(2);
  await expect(page.getByTestId('tab-name')).toHaveText(['Untitled 1', 'Untitled 2']);
  expect((await tabs(page)).map(t => t.active)).toEqual([false, true]);
  expect(await count(page)).toBe(0);
  await placeShape(page, 'shape-ellipse', { x: 300, y: 200 }, 'second');
  await placeShape(page, 'shape-ellipse', { x: 400, y: 260 }, 'third');
  expect(await count(page)).toBe(2);
  await expect(page.getByTestId('tab').nth(0)).toHaveAttribute('data-unsaved', 'true');
  await page.getByTestId('tab').nth(0).click();
  expect(await count(page)).toBe(1); // the first document, exactly as it was left
  await page.getByTestId('tab').nth(1).click();
  expect(await count(page)).toBe(2);
  expect(errors).toEqual([]);
});

test('the title and status bar follow the active tab, and a saved tab shows its file name', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await page.getByTestId('tab-new').click();
  await page.evaluate(() => (window as any).__editor.markSaved('C:\\Work\\plan.diagram.json'));
  await expect(page).toHaveTitle('plan.diagram.json - Diagram');
  await expect(page.getByTestId('tab-name').nth(1)).toHaveText('plan.diagram.json');
  await page.getByTestId('tab').nth(0).click();
  await expect(page).toHaveTitle('Untitled - Diagram');
  await expect(page.getByTestId('status-saved')).toHaveText('Untitled');
});

test('closing an unchanged tab is immediate, and the last tab is replaced by a blank one', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await page.getByTestId('tab-new').click();
  await page.getByTestId('tab-close').nth(1).click();
  await expect(page.getByTestId('tab')).toHaveCount(1);
  await page.getByTestId('tab-close').first().click();
  await expect(page.getByTestId('tab')).toHaveCount(1); // never an empty window
  expect(await calls(page)).toEqual([]); // an unchanged tab leaves nothing to discard
});

test("closing a tab with changes asks; Cancel keeps it, Don't save discards its recovery session, Save saves first", async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await page.getByTestId('tab-new').click();
  await page.getByTestId('tab').nth(0).click();
  await page.getByTestId('tab-close').nth(0).click();
  await expect(page.getByTestId('close-prompt')).toContainText('closing this tab');
  await page.getByTestId('close-cancel').click();
  await expect(page.getByTestId('tab')).toHaveCount(2);
  expect(await calls(page)).toEqual([]); // cancelling a tab close does not talk to the window

  await page.getByTestId('tab-close').nth(0).click();
  await page.getByTestId('close-discard').click();
  await expect(page.getByTestId('tab')).toHaveCount(1);
  expect(await calls(page)).toEqual(['discard']);
  expect((await tabs(page))[0]!.label).toBe('Untitled 2');

  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'b');
  await page.getByTestId('tab-close').nth(0).click();
  await page.getByTestId('close-save').click();
  await expect.poll(() => calls(page)).toEqual(['discard', 'save']);
  await expect(page.getByTestId('close-prompt')).toHaveCount(0);
});

test('a cancelled Save as keeps the tab open and says so', async ({ page }) => {
  await openEditor(page);
  await armHost(page, { saveWorks: false });
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await page.getByTestId('tab-new').click();
  await page.getByTestId('tab').nth(0).click();
  await page.getByTestId('tab-close').nth(0).click();
  await page.getByTestId('close-save').click();
  await expect(page.getByTestId('close-error')).toContainText('tab was not closed');
  await expect(page.getByTestId('tab')).toHaveCount(2);
});

test('Open uses an untouched blank tab, otherwise adds one, and a cancelled Open leaves nothing behind', async ({ page }) => {
  await openEditor(page);
  await armHost(page, { openPath: 'C:\\Work\\one.diagram.json' });
  await page.evaluate(() => (window as any).__editor.openFile());
  expect((await tabs(page)).map(t => t.label)).toEqual(['one.diagram.json']); // the blank tab was used

  await armHost(page, { openPath: 'C:\\Work\\two.diagram.json' });
  await page.evaluate(() => (window as any).__editor.openFile());
  expect((await tabs(page)).map(t => t.label)).toEqual(['one.diagram.json', 'two.diagram.json']);
  expect((await tabs(page)).map(t => t.active)).toEqual([false, true]);

  await armHost(page, { openPath: null });
  await page.evaluate(() => (window as any).__editor.openFile());
  expect(await tabs(page)).toHaveLength(2); // cancelled: the tab made for it is gone again
  expect((await tabs(page)).map(t => t.active)).toEqual([false, true]);
});

test('opening a file that is already open shows that tab instead of making a copy', async ({ page }) => {
  await openEditor(page);
  await armHost(page, { openPath: 'C:\\Work\\one.diagram.json' });
  await page.evaluate(() => (window as any).__editor.openFile());
  await page.getByTestId('tab-new').click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'work'); // not blank, so Open would add a tab
  await armHost(page, { openPath: 'c:/work/ONE.diagram.json' }); // same file, spelled differently
  await page.evaluate(() => (window as any).__editor.openFile());
  const t = await tabs(page);
  expect(t.map(x => x.label)).toEqual(['one.diagram.json', 'Untitled 2']);
  expect(t.map(x => x.active)).toEqual([true, false]);
  await expect(page.getByTestId('status-message')).toContainText('already open');
});

test('closing the window with several unsaved tabs asks once for all of them', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await page.getByTestId('tab-new').click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'b');
  await page.getByTestId('tab-new').click(); // a third, untouched tab does not count
  const r = await page.evaluate(() => (window as any).__editor.beginClose());
  expect(r).toEqual({ dirty: true });
  await expect(page.getByTestId('close-prompt')).toContainText('2 documents have changes');
  await expect(page.getByTestId('close-save')).toHaveText('Save all');
  await page.getByTestId('close-save').click();
  await expect.poll(() => calls(page)).toEqual(['save', 'save', 'close:keep']);
  expect((await tabs(page)).every(t => !t.unsaved)).toBe(true);
});

test("Don't save any discards everything, and Cancel leaves every tab alone", async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await page.getByTestId('tab-new').click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'b');
  await page.evaluate(() => (window as any).__editor.beginClose());
  await page.getByTestId('close-cancel').click();
  expect(await calls(page)).toEqual(['cancelled']);
  await page.evaluate(() => (window as any).__editor.beginClose());
  await page.getByTestId('close-discard').click();
  expect(await calls(page)).toEqual(['cancelled', 'close:discard']);
});

test('keyboard: Ctrl+N makes a tab, Ctrl+Tab and Ctrl+Shift+Tab move between them, Ctrl+W closes', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+n');
  await expect(page.getByTestId('tab')).toHaveCount(2);
  await page.keyboard.press('Control+Tab');
  expect((await tabs(page)).map(t => t.active)).toEqual([true, false]);
  await page.keyboard.press('Control+Shift+Tab');
  expect((await tabs(page)).map(t => t.active)).toEqual([false, true]);
  await page.keyboard.press('Control+w');
  await expect(page.getByTestId('tab')).toHaveCount(1);
});

test('tabs follow the ARIA tab pattern and arrow keys move between them', async ({ page }) => {
  await openEditor(page);
  await page.getByTestId('tab-new').click();
  await expect(page.getByRole('tablist', { name: 'Open documents' })).toBeVisible();
  const first = page.getByRole('tab').nth(0);
  await expect(first).toHaveAttribute('aria-selected', 'false');
  await first.focus();
  await page.keyboard.press('Enter');
  await expect(first).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab').nth(1)).toHaveAttribute('aria-selected', 'true');
});

test('a document an agent opens in the background appears as a tab without taking over', async ({ page }) => {
  const errors = await openEditor(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'mine');
  await page.evaluate(() => (window as any).__editor.agentOpenDocument({ activate: false }));
  await expect(page.getByTestId('tab')).toHaveCount(2);
  expect((await tabs(page)).map(t => t.active)).toEqual([true, false]);
  expect(await count(page)).toBe(1); // what the person is looking at did not change
  expect(errors).toEqual([]);
});

test('past the limit a new tab is refused with a message', async ({ page }) => {
  await openEditor(page);
  for (let i = 0; i < 25; i++) await page.evaluate(() => (window as any).__editor.newTab());
  await expect(page.getByTestId('tab')).toHaveCount(20);
  await expect(page.getByTestId('tab-notice')).toContainText('20 documents');
});

test('copy in one tab pastes into another', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'src');
  await page.evaluate(id => (window as any).__editor.callTool('doc.status', {}), a.id);
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+c');
  await page.getByTestId('tab-new').click();
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+v');
  await expect.poll(() => count(page)).toBe(1);
});
