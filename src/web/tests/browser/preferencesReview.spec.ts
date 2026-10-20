import { test, expect } from '@playwright/test';
import { centre, openEditor, snapshot } from './support';

for (const kind of ['shape', 'connector'] as const) {
  for (const legacy of [false, true]) {
    test(`empty ${kind} inspector uses captured font before ${legacy ? 'explicit legacy formatting' : 'first text materialization'}`, async ({ page }) => {
      const errors = await openEditor(page);
      await page.getByTestId('menu-edit').click();
      await page.getByTestId('menu-preferences').click();
      await page.getByLabel('Default font', { exact: true }).fill('Aptos');
      await page.getByLabel('Default font size (pt)').fill('19');
      await page.getByRole('button', { name: 'Save preferences' }).click();
      await page.getByTestId('tab-new').click();
      const operation = kind === 'shape'
        ? { op: 'createShape', alias: 'empty', bounds: { x: 100, y: 100, width: 160, height: 80 } }
        : { op: 'createConnector', alias: 'empty', from: { glue: 'none', point: { x: 100, y: 100 } }, to: { glue: 'none', point: { x: 260, y: 180 } } };
      expect((await page.evaluate(op => (window as any).__editor.agentApply([op]), operation)).ok).toBe(true);
      const element = (await snapshot(page)).document.pages[0]!.elements[0]!;
      expect(kind === 'shape' ? (element as any).text : (element as any).label).toBeUndefined();
      const before = await snapshot(page);
      const point = await centre(page, element.id);
      await page.mouse.click(point.x, point.y);
      await expect(page.getByTestId('inspector').locator('h2')).toHaveText(kind === 'shape' ? 'Shape' : 'Connector');
      await expect(page.getByTestId('field-font')).toHaveValue('Aptos');
      await expect(page.getByTestId('field-fontsize')).toHaveValue('19');
      expect(await snapshot(page)).toEqual(before);
      if (legacy) {
        // Size is changed while the text block is still absent: legacy 12 must commit.
        await page.getByTestId('field-fontsize').fill('12');
        await page.getByTestId('field-fontsize').press('Enter');
        await page.getByTestId('field-font').selectOption('Segoe UI');
      }
      await page.getByTestId('field-text').fill('First authored text');
      await page.getByTestId('field-text').blur();
      await expect.poll(async () => {
        const current = (await snapshot(page)).document.pages[0]!.elements[0] as any;
        return kind === 'shape' ? current.text : current.label;
      }).toMatchObject({ value: 'First authored text', fontFamily: legacy ? 'Segoe UI' : 'Aptos', fontSizePt: legacy ? 12 : 19 });
      await expect(page.getByTestId('field-font')).toHaveValue(legacy ? 'Segoe UI' : 'Aptos');
      expect(errors).toEqual([]);
    });
  }
}

test('compatibility report blocks the keyboard palette route to Preferences', async ({ page }) => {
  const errors = await openEditor(page);
  await page.evaluate(() => (window as any).__editor.setHost({ exportVsdx: async () => ({
    ok: false, error: { code: 'compatibility_loss', message: 'Review required', retryable: false,
      details: { path: 'C:\\exports\\review.vsdx', diagnostics: [{ severity: 'warning', code: 'curves_flattened', message: 'Review curves.' }] } },
  }) }));
  await page.getByTestId('menu-file').click();
  await page.getByTestId('menu-export-visio').click();
  await expect(page.getByTestId('compat-dialog')).toBeVisible();
  await page.keyboard.press('Control+Shift+P');
  // Reproduce the complete old route if the palette incorrectly opens.
  if (await page.getByTestId('help-palette').isVisible()) {
    await page.getByTestId('palette-input').fill('Preferences');
    await page.keyboard.press('Enter');
  }
  await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
  await expect(page.getByTestId('help-palette')).toHaveCount(0);
  await expect(page.getByTestId('compat-dialog')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await page.getByTestId('compat-close').click();
  await page.keyboard.press('Control+Shift+P');
  await page.getByTestId('palette-input').fill('Preferences');
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('preferences-dialog')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  expect(errors).toEqual([]);
});

for (const closeAction of ['Cancel', 'Save preferences']) {
  test(`late compatibility report waits until Preferences closes with ${closeAction}`, async ({ page }) => {
    const errors = await openEditor(page);
    await page.evaluate(() => (window as any).__editor.setHost({
      exportVsdx: async (opts: { acceptLoss?: boolean }) => opts.acceptLoss
        ? { ok: true, value: { cancelled: true } }
        : new Promise(resolve => { (window as any).__finishVisio = () => resolve({
          ok: false, error: { code: 'compatibility_loss', message: 'Delayed export review', retryable: false,
            details: { path: 'C:\\exports\\delayed.vsdx', diagnostics: [{ severity: 'warning', code: 'curves_flattened', message: 'Review delayed curves.' }] } },
        }); }),
    }));
    await page.getByTestId('menu-file').click();
    await page.getByTestId('menu-export-visio').click();
    await page.getByTestId('menu-edit').click();
    await page.getByTestId('menu-preferences').click();
    await expect(page.getByTestId('preferences-dialog')).toBeVisible();
    await page.evaluate(async () => {
      (window as any).__finishVisio();
      // Let the delayed model reply and React render settle before testing modal ownership.
      await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    });
    await expect(page.getByTestId('compat-dialog')).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await page.getByRole('button', { name: closeAction, exact: true }).click();
    await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
    await expect(page.getByTestId('compat-dialog')).toBeVisible();
    await expect(page.getByTestId('compat-summary')).toHaveText('Delayed export review');
    await expect(page.getByTestId('diag-item')).toContainText('Review delayed curves.');
    await expect(page.getByTestId('compat-confirm')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await page.getByTestId('compat-close').click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}
