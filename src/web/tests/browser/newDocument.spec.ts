import { test, expect } from '@playwright/test';
import { openEditor, placeShape, snapshot } from './support';

test('New from menu and Ctrl+N share the picker; Cancel preserves the current drawing', async ({ page }) => {
  await openEditor(page); await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'keep'); const before = await snapshot(page);
  await page.getByRole('button', { name: 'File', exact: true }).click();
  await page.getByRole('menuitem', { name: /New/ }).click();
  await expect(page.getByRole('dialog', { name: 'New document', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click(); expect(await snapshot(page)).toEqual(before);
  await page.keyboard.press('Control+n'); await expect(page.getByRole('dialog', { name: 'New document', exact: true })).toBeVisible();
  await page.getByLabel('Starting page').selectOption('letter-portrait'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByRole('alertdialog')).toContainText('Save changes');
  await page.getByRole('button', { name: "Don't save", exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0); const after = await snapshot(page);
  expect(after.documentId).not.toBe(before.documentId); expect(after.document.pages[0]).toMatchObject({ widthPt: 612, heightPt: 792, elements: [] });
  await expect(page).toHaveTitle('● Untitled - Diagram');
});

test('custom validation and Escape are safe; a cancelled Save as retains work', async ({ page }) => {
  await openEditor(page); await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'keep'); const before = await snapshot(page);
  await page.keyboard.press('Control+n'); await page.getByLabel('Starting page').selectOption('custom');
  await page.getByLabel('Width (mm)').fill('0'); await page.getByRole('button', { name: 'Create', exact: true }).click();
  await expect(page.getByTestId('new-error')).toBeVisible(); expect(await snapshot(page)).toEqual(before);
  await page.getByLabel('Width (mm)').fill('300'); await page.getByLabel('Height (mm)').fill('200');
  await page.getByRole('button', { name: 'Create', exact: true }).click();
  await page.evaluate(() => (window as any).__editor.setHost({ saveJson: async () => ({ ok: true, value: { cancelled: true } }) }));
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByTestId('new-error')).toContainText('not saved'); expect(await snapshot(page)).toEqual(before);
  await page.keyboard.press('Escape'); await expect(page.getByRole('alertdialog')).toHaveCount(0); expect(await snapshot(page)).toEqual(before);
});
