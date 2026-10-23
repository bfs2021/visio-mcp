import { test, expect } from '@playwright/test';
import { openEditor } from './support';

test('File Print prepares host sheets, invalidates them when settings change, and outputs the fresh job', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    const w = window as any; w.__printCalls = [];
    w.__editor.setHost({ editorPrint: async (action: string, params: any) => {
      w.__printCalls.push({ action, params });
      if (action === 'devices') return { ok: true, value: { devices: [{ id: 'printer-a', label: 'Printer A', media: [{ id: 'letter', label: 'Letter', widthPt: 612, heightPt: 792, minimumMargins: { leftPt: 12, topPt: 12, rightPt: 12, bottomPt: 12 } }] }] } };
      if (action === 'preview') return { ok: true, value: { jobId: 'job-1', sheets: [{ sheetNumber: 1, paperWidthPt: 612, paperHeightPt: 792, sourcePageNumbers: [1], svg: '<svg xmlns="http://www.w3.org/2000/svg" width="612pt" height="792pt"><text x="20" y="30">Preview sheet</text></svg>' }], settings: {}, marginWarnings: ['Printer margins adjusted'], expiresAt: new Date().toISOString(), sourcePageCount: 1 } };
      return { ok: true, value: { printed: true } };
    } });
  });
  await page.getByTestId('menu-file').click();
  await page.getByTestId('menu-print').click();
  const dialog = page.getByRole('dialog', { name: 'Print preview' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('Printer')).toHaveValue('printer-a');
  await dialog.getByRole('button', { name: 'Prepare preview' }).click();
  await expect(dialog.getByTestId('print-sheet')).toHaveCount(1);
  await expect(dialog.getByText('Printer margins adjusted')).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Print', exact: true })).toBeEnabled();
  await dialog.getByLabel('Layout').selectOption('tile');
  await expect(dialog.getByTestId('print-sheet')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Print', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Prepare preview' }).click();
  await dialog.getByRole('button', { name: 'Print', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  const calls = await page.evaluate(() => (window as any).__printCalls);
  expect(calls.map((c: any) => c.action)).toEqual(['devices', 'preview', 'preview', 'print']);
  expect(calls.at(-1).params.jobId).toBe('job-1');
});

test('Ctrl+P opens print preview', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => (window as any).__editor.setHost({ editorPrint: async () => ({ ok: true, value: { devices: [] } }) }));
  await page.keyboard.press('Control+p');
  await expect(page.getByRole('dialog', { name: 'Print preview' })).toBeVisible();
});
