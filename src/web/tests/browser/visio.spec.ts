import { test, expect } from '@playwright/test';
import { openEditor } from './support';

test('visio_export_asks_before_losing_anything', async ({ page }) => {
  const errors = await openEditor(page);
  await page.evaluate(() => {
    const calls: unknown[] = [];
    (window as any).__calls = calls;
    (window as any).__editor.setHost({
      exportVsdx: async (opts: { acceptLoss?: boolean; path?: string }) => {
        calls.push(opts);
        if (!opts.acceptLoss) {
          return { ok: false, error: { code: 'compatibility_loss', message: '1 thing could not be carried across exactly, and 0 notes. Nothing was written.', retryable: false,
            details: { path: 'C:\exports\d.vsdx', diagnostics: [{ severity: 'warning', code: 'curves_flattened', message: 'Curves were turned into short straight lines.' }] } } };
        }
        return { ok: true, value: { path: opts.path, revision: 3, lossy: true, summary: '1 thing could not be carried across exactly, and 0 notes.', diagnostics: [{ severity: 'warning', code: 'curves_flattened', message: 'Curves were turned into short straight lines.' }] } };
      },
      openVsdx: async () => ({ ok: true, value: { path: 'C:\in.vsdx', lossy: false, summary: 'Everything was carried across.', diagnostics: [{ severity: 'info', code: 'outline_converted', message: 'note' }] } }),
    });
  });

  await page.getByTestId('menu-file').click();
  await page.getByTestId('menu-export-visio').click();
  await expect(page.getByTestId('compat-dialog')).toBeVisible();
  await expect(page.getByTestId('diag-item')).toContainText('curves flattened');
  expect(await page.evaluate(() => (window as any).__calls)).toEqual([{}]);

  await page.getByTestId('compat-confirm').click();
  await expect.poll(() => page.evaluate(() => (window as any).__calls.length)).toBe(2);
  expect(await page.evaluate(() => (window as any).__calls[1])).toEqual({ acceptLoss: true, path: 'C:\exports\d.vsdx' });
  await expect(page.getByTestId('compat-summary')).toContainText('could not be carried across');
  await page.getByTestId('compat-close').click();
  await expect(page.getByTestId('compat-dialog')).toHaveCount(0);

  await page.getByTestId('menu-file').click();
  await page.getByTestId('menu-open-visio').click();
  await expect(page.getByTestId('compat-dialog')).toBeVisible();
  await page.getByTestId('compat-close').click();
  expect(errors).toEqual([]);
});

test('visio_cancel_export_writes_nothing', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    (window as any).__n = 0;
    (window as any).__editor.setHost({ exportVsdx: async (o: { acceptLoss?: boolean }) => { (window as any).__n++; return o.acceptLoss
      ? { ok: true, value: {} } : { ok: false, error: { code: 'compatibility_loss', message: 'x', retryable: false, details: { diagnostics: [{ severity: 'warning', code: 'preset_approximated', message: 'm' }] } } }; } });
  });
  await page.getByTestId('menu-file').click();
  await page.getByTestId('menu-export-visio').click();
  await page.getByTestId('compat-close').click();
  await expect(page.getByTestId('compat-dialog')).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__n)).toBe(1);
});
