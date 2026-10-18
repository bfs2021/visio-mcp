import { test, expect } from '@playwright/test';
import { openEditor, snapshot } from './support';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

test('previews a component without editing, inserts it, reflows current text, and undoes the reflow', async ({ page }) => {
  const errors = await openEditor(page);
  await page.getByLabel('Component title', { exact: true }).fill('Infrastructure repository');
  await page.getByLabel('Component items', { exact: true }).fill('Bicep modules\nEnvironment parameters');
  await page.getByRole('button', { name: 'Preview component', exact: true }).click();
  await expect(page.getByAltText('Component preview', { exact: true })).toBeVisible();
  expect((await snapshot(page)).revision).toBe(0);
  await page.getByRole('button', { name: 'Apply component', exact: true }).click();
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.filter(e => e.kind === 'group').length).toBe(1);
  const created = await snapshot(page); const group = created.document.pages[0]!.elements.find(e => e.kind === 'group')!;
  const item = created.document.pages[0]!.elements.find(e => e.metadata['layout.role'] === 'item.0')!;
  const edit = [{ op: 'set', target: item.id, patch: { text: { value: 'A longer infrastructure requirement that needs several wrapped lines to fit', fontSizePt: 16, colour: '#15803D', horizontalAlign: 'right' } } }];
  expect((await page.evaluate(ops => (window as any).__editor.agentApply(ops), edit)).ok).toBe(true);
  const before = await snapshot(page);
  await page.getByLabel('Component width (pt)', { exact: true }).fill('280');
  await page.getByRole('button', { name: 'Preview reflow', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply component', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Apply component', exact: true }).click();
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === group.id)!.bounds.width).toBe(280);
  const after = await snapshot(page); const changed = after.document.pages[0]!.elements.find(e => e.id === item.id)!;
  expect(changed.kind === 'text' && changed.text.value).toContain('longer infrastructure');
  expect(changed.kind === 'text' && changed.text.colour).toBe('#15803D');
  expect(changed.kind === 'text' && changed.text.horizontalAlign).toBe('right');
  const drawn = await page.evaluate(() => {
    const sheet = document.querySelector('[data-testid="page-sheet"]')!;
    const svg = sheet.parentElement!.parentElement!.querySelector(':scope > svg')!;
    return Array.from(svg.querySelectorAll('text')).filter(t => getComputedStyle(t).fill === 'rgb(21, 128, 61)').map(t => t.getBoundingClientRect().width);
  });
  expect(drawn.length).toBeGreaterThan(1); expect(Math.max(...drawn)).toBeLessThan(250);
  const folder = fileURLToPath(new URL('../../../../artifacts/r04-components/', import.meta.url)); mkdirSync(folder, { recursive: true });
  await page.screenshot({ path: folder + '/components-panel.png' });
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await snapshot(page)).document.pages).toEqual(before.document.pages);
  expect(errors).toEqual([]);
});

test('a conflicting edit disables application of a stale preview', async ({ page }) => {
  const errors = await openEditor(page);
  await page.getByRole('button', { name: 'Preview component', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply component', exact: true })).toBeEnabled();
  await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'createShape', bounds: { x: 10, y: 10, width: 20, height: 20 } }]));
  await expect(page.getByRole('button', { name: 'Apply component', exact: true })).toBeDisabled();
  expect(errors).toEqual([]);
});

test('manual component resizing updates the width used by the reflow controls', async ({ page }) => {
  await openEditor(page);
  await page.getByRole('button', { name: 'Preview component', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply component', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Apply component', exact: true }).click();
  const group = (await snapshot(page)).document.pages[0]!.elements.find(e => e.kind === 'group')!;
  await page.evaluate(id => (window as any).__editor.agentApply([{ op: 'resize', target: id, bounds: { width: 320 } }]), group.id);
  await expect(page.getByLabel('Component width (pt)', { exact: true })).toHaveValue('320');
});
