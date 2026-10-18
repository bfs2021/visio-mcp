import { test, expect, type Page } from '@playwright/test';
import { openEditor, placeShape } from './support';

/** A host that records what the page asks of it. Save marks the document saved unless `saveWorks` is false (a cancelled Save as). */
async function armHost(page: Page, saveWorks = true) {
  await page.evaluate(works => {
    const calls: string[] = [];
    (window as any).__calls = calls;
    (window as any).__editor.setHost({
      saveJson: async () => { calls.push('save'); if (works) await (window as any).__editor.markSaved('C:\\Work\\plan.diagram.json'); return { ok: true, value: works ? { path: 'C:\\Work\\plan.diagram.json' } : { cancelled: true } }; },
      appClose: async (discard: boolean) => { calls.push(`close:${discard ? 'discard' : 'keep'}`); return { ok: true, value: { closing: true } }; },
      appCloseCancelled: async () => { calls.push('cancelled'); return { ok: true, value: { cancelled: true } }; },
    });
  }, saveWorks);
}
const calls = (page: Page) => page.evaluate(() => (window as any).__calls as string[]);
const beginClose = (page: Page) => page.evaluate(() => (window as any).__editor.beginClose() as { dirty: boolean });

test('closing a document with no changes asks nothing', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  expect(await beginClose(page)).toEqual({ dirty: false });
  await expect(page.getByTestId('close-prompt')).toHaveCount(0);
});

test('closing with unsaved changes asks, and Cancel keeps the window open', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  expect(await beginClose(page)).toEqual({ dirty: true });
  await expect(page.getByTestId('close-prompt')).toBeVisible();
  await expect(page.getByTestId('close-prompt')).toContainText('This diagram has changes that were not saved');
  await page.getByTestId('close-cancel').click();
  await expect(page.getByTestId('close-prompt')).toHaveCount(0);
  expect(await calls(page)).toEqual(['cancelled']);
  // asking again works
  expect(await beginClose(page)).toEqual({ dirty: true });
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('close-prompt')).toHaveCount(0);
  expect(await calls(page)).toEqual(['cancelled', 'cancelled']);
});

test("Don't save closes and asks the host to throw the work away", async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  await beginClose(page);
  await page.getByTestId('close-discard').click();
  expect(await calls(page)).toEqual(['close:discard']);
});

test('Save saves first, then closes keeping the file', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  await beginClose(page);
  await page.getByTestId('close-save').click();
  await expect.poll(() => calls(page)).toEqual(['save', 'close:keep']);
});

test('a cancelled Save as does not close the window, and says so', async ({ page }) => {
  await openEditor(page);
  await armHost(page, false);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  await beginClose(page);
  await page.getByTestId('close-save').click();
  await expect(page.getByTestId('close-error')).toContainText('not saved');
  expect(await calls(page)).toEqual(['save']); // never asked the host to close
  await expect(page.getByTestId('close-prompt')).toBeVisible();
  await page.getByTestId('close-cancel').click();
  expect(await calls(page)).toEqual(['save', 'cancelled']);
});

test('after a save the file name is shown in the prompt for later changes', async ({ page }) => {
  await openEditor(page);
  await armHost(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  await page.evaluate(() => (window as any).__editor.markSaved('C:\\Work\\plan.diagram.json'));
  await placeShape(page, 'shape-rectangle', { x: 400, y: 150 }, 'y');
  await beginClose(page);
  await expect(page.getByTestId('close-prompt')).toContainText('plan.diagram.json has changes');
});

test('the page title and status bar follow the file name and the unsaved state', async ({ page }) => {
  await openEditor(page);
  await expect(page).toHaveTitle('Untitled - Diagram');
  await expect(page.getByTestId('status-saved')).toHaveText('Untitled');
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'x');
  await expect(page).toHaveTitle('● Untitled - Diagram');
  await expect(page.getByTestId('status-saved')).toHaveText('Untitled, not saved');
  await page.evaluate(() => (window as any).__editor.markSaved('C:\\Work\\plan.diagram.json'));
  await expect(page).toHaveTitle('plan.diagram.json - Diagram');
  await expect(page.getByTestId('status-saved')).toHaveText('plan.diagram.json: saved');
  await expect(page.getByTestId('status-saved')).toHaveAttribute('title', 'C:\\Work\\plan.diagram.json');
  await placeShape(page, 'shape-rectangle', { x: 400, y: 150 }, 'y');
  await expect(page).toHaveTitle('● plan.diagram.json - Diagram');
  await expect(page.getByTestId('status-saved')).toHaveText('plan.diagram.json: unsaved changes');
});
