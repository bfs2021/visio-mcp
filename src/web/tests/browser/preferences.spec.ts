import { test, expect } from '@playwright/test';
import { openEditor, snapshot } from './support';
import { createHash } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const open = async (page: import('@playwright/test').Page) => {
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-preferences').click();
  return page.getByTestId('preferences-dialog');
};

test('Cancel and Escape discard a draft and restore menu focus', async ({ page }) => {
  await openEditor(page);
  await open(page);
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
  await expect(page.getByTestId('menu-edit')).toBeFocused();
  await open(page);
  await expect(page.getByLabel('Theme')).toHaveValue('light');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
});

test('Save applies theme and units to tab views without editing the drawing', async ({ page }) => {
  await openEditor(page);
  await page.getByTestId('tab-new').click();
  await page.evaluate(() => {
    (window as any).__closeCancelled = 0;
    (window as any).__editor.setHost({ appCloseCancelled: async () => { (window as any).__closeCancelled++; return { ok: true, value: {} }; } });
  });
  const before = await snapshot(page);
  const status = await page.evaluate(() => (window as any).__editor.status());
  await open(page);
  await page.getByLabel('Display units').selectOption('in');
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.getByTestId('unit')).toHaveValue('in');
  expect(await page.evaluate(() => (window as any).__closeCancelled)).toBe(1);
  expect(await snapshot(page)).toEqual(before);
  expect(await page.evaluate(() => (window as any).__editor.status())).toEqual(status);
  await page.getByTestId('tab').first().click();
  await expect(page.getByTestId('unit')).toHaveValue('in');
});

test('Reset changes only the draft until Save', async ({ page }) => {
  await openEditor(page);
  await open(page);
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByRole('button', { name: 'Reset to defaults' }).click();
  await expect(page.getByLabel('Theme')).toHaveValue('light');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});

test('invalid fields stay in the dialog and unavailable fonts remain allowed', async ({ page }) => {
  await openEditor(page);
  await open(page);
  await page.getByLabel('Default font', { exact: true }).fill('');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await expect(page.getByTestId('preferences-dialog')).toBeVisible();
  await expect(page.getByTestId('preferences-error-font')).toBeVisible();
  await page.getByLabel('Default font', { exact: true }).fill('My Valid Uninstalled Font');
  await expect(page.getByTestId('font-unavailable')).toBeVisible();
  await expect(page.locator('#installed-fonts option[value="Consolas"]')).toHaveCount(1);
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await expect(page.getByTestId('preferences-dialog')).toHaveCount(0);
  await open(page);
  await expect(page.getByLabel('Default font', { exact: true })).toHaveValue('My Valid Uninstalled Font');
});

test('stale settings offer Reload and a failed write leaves the draft visible', async ({ page }) => {
  await openEditor(page);
  await open(page);
  await page.getByLabel('Theme').selectOption('dark');
  await page.evaluate(() => (window as any).__editor.preferencesHostSave({ units: 'cm' }));
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await expect(page.getByTestId('preferences-error-form')).toContainText('changed');
  await expect(page.getByLabel('Theme')).toHaveValue('dark');
  await page.getByRole('button', { name: 'Reload' }).click();
  await expect(page.getByLabel('Theme')).toHaveValue('light');
  await expect(page.getByLabel('Display units')).toHaveValue('cm');
  await page.evaluate(() => (window as any).__editor.preferencesFailWrites());
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  await expect(page.getByTestId('preferences-error-form')).toContainText('could not be written');
  await expect(page.getByLabel('Theme')).toHaveValue('dark');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});

test('dark chrome preserves page pixels and document hash while toolbar unit override stays local', async ({ page }) => {
  await openEditor(page);
  expect((await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'createShape', alias: 'authored', text: { value: 'Authored colours stay unchanged' }, bounds: { x: 80, y: 80, width: 180, height: 80 }, style: { fill: { kind: 'solid', colour: '#22aa66', opacity: 1 } } }]))).ok).toBe(true);
  const pageId = (await snapshot(page)).document.pages[0]!.id;
  const render = () => page.evaluate(id => (window as any).__editor.callTool('doc.render', { scope: 'page', pageId: id, format: 'png', maxWidth: 842, maxHeight: 596 }), pageId);
  const cleanBefore = await render();
  expect(cleanBefore.error).toBeUndefined();
  const evidence = resolve(process.cwd(), '../../.superpowers/sdd/2026-10-19-w13-preferences/task-6-screenshots');
  mkdirSync(evidence, { recursive: true });
  const sheet = await page.getByTestId('canvas').getByTestId('page-sheet').boundingBox();
  if (!sheet) throw new Error('diagram page missing');
  const beforePixels = await page.screenshot({ path: resolve(evidence, 'light.png') });
  const hash = (value: unknown) => createHash('sha256').update(JSON.stringify(value)).digest('hex');
  const beforeHash = hash((await snapshot(page)).document);
  const beforeRevision = (await snapshot(page)).revision;
  await page.getByTestId('tab-new').click();
  await page.getByTestId('unit').selectOption('cm');
  await page.getByTestId('tab').first().click();
  await expect(page.getByTestId('unit')).toHaveValue('mm');
  await open(page);
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByLabel('Display units').selectOption('in');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  const afterPixels = await page.screenshot({ path: resolve(evidence, 'dark.png') });
  const selectedTabContrast = await page.locator('.page-tabs .tab.active').first().evaluate(tab => {
    const style = getComputedStyle(tab);
    const luminance = (css: string) => {
      const rgb = css.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(channel => {
        const value = channel / 255;
        return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
      });
      return rgb[0]! * 0.2126 + rgb[1]! * 0.7152 + rgb[2]! * 0.0722;
    };
    const a = luminance(style.color), b = luminance(style.backgroundColor);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  });
  expect(selectedTabContrast).toBeGreaterThanOrEqual(4.5);
  const differentPagePixels = await page.evaluate(async ({ before, after, box }) => {
    const read = async (data: string) => {
      const image = new Image(); image.src = `data:image/png;base64,${data}`; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
      const context = canvas.getContext('2d')!; context.drawImage(image, 0, 0);
      return context.getImageData(Math.ceil(box.x + 4), Math.ceil(box.y + 4), Math.floor(box.width - 8), Math.floor(box.height - 8)).data;
    };
    const a = await read(before), b = await read(after);
    let changes = 0; for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) changes++;
    return changes;
  }, { before: beforePixels.toString('base64'), after: afterPixels.toString('base64'), box: sheet });
  expect(differentPagePixels).toBe(0);
  const cleanAfter = await render();
  expect(cleanAfter.error).toBeUndefined();
  expect(cleanAfter.result.base64).toBe(cleanBefore.result.base64);
  expect(hash((await snapshot(page)).document)).toBe(beforeHash);
  expect((await snapshot(page)).revision).toBe(beforeRevision);
  await open(page);
  await page.screenshot({ path: resolve(evidence, 'dark-dialog.png') });
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.getByTestId('tab').last().click();
  await expect(page.getByTestId('unit')).toHaveValue('in');
});


test('focus stays in Preferences and competing keyboard and lifecycle actions are blocked', async ({ page }) => {
  await openEditor(page);
  await page.getByTestId('tab-new').click();
  const tabs = await page.evaluate(() => (window as any).__editor.tabs());
  await open(page);
  await expect(page.getByLabel('Display units')).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(page.getByRole('button', { name: 'Save preferences' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.getByLabel('Display units')).toBeFocused();
  await page.keyboard.press('Control+n');
  await page.keyboard.press('Control+Tab');
  await page.evaluate(async () => {
    const editor = (window as any).__editor;
    editor.newTab(); editor.activateTab(0); await editor.closeTab();
  });
  expect(await page.evaluate(() => (window as any).__editor.tabs())).toEqual(tabs);
  expect(await page.evaluate(() => (window as any).__editor.beginClose())).toEqual({ dirty: true });
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('menu-edit')).toBeFocused();
});

test('dark diagnostic, sidebar, recovery and tab notice text meets normal-text contrast', async ({ page }) => {
  await openEditor(page);
  await open(page);
  await page.getByLabel('Theme').selectOption('dark');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  const contrast = async (locator: import('@playwright/test').Locator) => {
    await locator.scrollIntoViewIfNeeded();
    await expect(locator).toBeVisible();
    return locator.evaluate(element => {
      const luminance = (css: string) => {
        const rgb = css.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(c => c / 255).map(c => c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
        return rgb[0]! * .2126 + rgb[1]! * .7152 + rgb[2]! * .0722;
      };
      let parent: Element | null = element;
      while (parent && getComputedStyle(parent).backgroundColor === 'rgba(0, 0, 0, 0)') parent = parent.parentElement;
      const a = luminance(getComputedStyle(element).color), b = luminance(getComputedStyle(parent!).backgroundColor);
      return (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
    });
  };
  expect.soft(await contrast(page.locator('.side-section h3').first()), 'sidebar heading').toBeGreaterThanOrEqual(4.5);
  await open(page);
  await page.getByLabel('Default font', { exact: true }).fill('');
  await page.getByRole('button', { name: 'Save preferences' }).click();
  expect.soft(await contrast(page.getByTestId('preferences-error-font')), 'validation error').toBeGreaterThanOrEqual(4.5);
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.evaluate(async () => {
    const editor = (window as any).__editor;
    editor.setHost({ recoveryList: async () => ({ ok: true, value: { auto: false, sessions: [{ sessionId: 'contrast-session', documentId: 'contrast-document', title: 'Recovered plan', originalPath: 'C:\\Work\\plan.diagram.json', pageCount: 1, objectCount: 0, checkpointRevision: 0, lastDurableRevision: 1, lastWrite: '2026-10-20T12:00:00Z', loss: { tornTailDiscarded: true, corruptionAfterRevision: null, messages: ['The incomplete final journal record was discarded.'] } }] } }) });
    await editor.checkRecovery();
  });
  expect.soft(await contrast(page.getByTestId('recovery-path')), 'recovery secondary text').toBeGreaterThanOrEqual(4.5);
  expect.soft(await contrast(page.locator('.recovery-info .diag-warning')), 'recovery warning').toBeGreaterThanOrEqual(4.5);
  await page.getByTestId('recovery-later').click();
  // Isolated representatives cover conditional status/notice classes against their real shell backgrounds.
  await page.evaluate(() => {
    for (const [parent, cls, label] of [['.statusbar', 'message warning', 'Warning'], ['.statusbar', 'message error', 'Error'], ['.tabstrip', 'tab-notice', 'Tab limit reached'], ['.tabstrip', 'tab-banner', 'Restored drawings']] as const) {
      const element = document.createElement('span'); element.className = cls; element.textContent = label; element.dataset.contrast = cls;
      document.querySelector(parent)!.append(element);
    }
  });
  for (const cls of ['message warning', 'message error', 'tab-notice', 'tab-banner']) expect.soft(await contrast(page.locator(`[data-contrast="${cls}"]`)), cls).toBeGreaterThanOrEqual(4.5);
});
