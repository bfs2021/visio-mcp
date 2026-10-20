import { test, expect } from '@playwright/test';
import { openEditor } from './support';

const about = {
  name: 'Agentic Diagram', version: '1.4.0.0', build: 'abc123', runtime: '.NET 10.0.0', protocolVersion: 1,
  companions: [{ label: 'Client 1', version: '1.4.0.0' }],
  folders: [{ label: 'Recovery', path: 'C:/Data/AgenticDiagram/recovery' }, { label: 'Exports', path: 'C:/Docs/Exports' }],
};

test('Help > Keyboard shortcuts lists every menu command that has a key, plus the keys that are not menu commands', async ({ page }) => {
  const errors = await openEditor(page);
  await page.getByTestId('menu-help').click();
  await page.getByTestId('menu-shortcuts').click();
  const dialog = page.getByTestId('help-shortcuts');
  await expect(dialog).toBeVisible();
  for (const text of ['Save as', 'Ctrl+Shift+S', 'Group', 'Ctrl+G', 'Zoom in', 'Ctrl+Shift+P', 'Next tab (most recently used)', 'Ctrl+Tab', 'Nudge the selection by 10 points']) {
    await expect(dialog).toContainText(text);
  }
  // every command in the menus that has no key is named, so nothing is missing from the list
  await expect(page.getByTestId('shortcuts-unbound')).toContainText('Actual size');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  expect(errors).toEqual([]);
});

test('Help > About shows the build, the connected MCP companion and the data folders from the host', async ({ page }) => {
  const errors = await openEditor(page);
  await page.evaluate(about => (window as any).__editor.setHost({ appAbout: async () => ({ ok: true, value: about }) }), about);
  await page.getByTestId('menu-help').click();
  await page.getByTestId('menu-about').click();
  await expect(page.getByTestId('about-version')).toHaveText('1.4.0.0 (build abc123)');
  await expect(page.getByTestId('about-companions')).toContainText('Client 1: version 1.4.0.0');
  await expect(page.getByTestId('about-folders')).toContainText('C:/Data/AgenticDiagram/recovery');
  await expect(page.getByTestId('about-folders')).toContainText('Exports');
  await page.getByTestId('help-about-close').click();
  await expect(page.getByTestId('help-about')).toBeHidden();
  expect(errors).toEqual([]);
});

test('About says when no companion is connected and when the host cannot answer', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(about => (window as any).__editor.setHost({ appAbout: async () => ({ ok: true, value: { ...about, companions: [] } }) }), about);
  await page.getByTestId('menu-help').click();
  await page.getByTestId('menu-about').click();
  await expect(page.getByTestId('about-companions')).toContainText('Not connected');
  await page.keyboard.press('Escape');
  await page.evaluate(() => (window as any).__editor.setHost({}));
  await page.getByTestId('menu-help').click();
  await page.getByTestId('menu-about').click();
  await expect(page.getByTestId('about-error')).toContainText('no host');
});

test('Ctrl+Shift+P opens the command palette: type to filter, arrows and Enter run a command, Escape closes it', async ({ page }) => {
  const errors = await openEditor(page);
  const grid = () => page.evaluate(() => (window as any).__editor.snapshot().document.pages[0].grid.visible as boolean);
  const before = await grid();
  await page.keyboard.press('Control+Shift+P');
  const input = page.getByTestId('palette-input');
  await expect(input).toBeFocused();
  await input.fill('grid');
  await expect(page.getByTestId('palette-item-grid')).toBeVisible();
  await expect(page.getByTestId('palette-item-save')).toBeHidden();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('help-palette')).toBeHidden();
  await expect.poll(grid).toBe(!before);

  await page.keyboard.press('Control+Shift+P');
  await page.getByTestId('palette-input').fill('zzzz');
  await expect(page.getByTestId('palette-list')).toContainText('No command matches');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('help-palette')).toBeHidden();

  await page.keyboard.press('Control+Shift+P');
  await expect(page.getByTestId('help-palette')).toBeVisible();
  await page.keyboard.press('Control+Shift+P'); // the same key closes it
  await expect(page.getByTestId('help-palette')).toBeHidden();
  expect(errors).toEqual([]);
});

test('the palette ignores a disabled command and other shortcuts do nothing while a Help panel is open', async ({ page }) => {
  await openEditor(page);
  await page.keyboard.press('Control+Shift+P');
  await page.getByTestId('palette-input').fill('delete');
  await page.keyboard.press('Enter'); // nothing is selected, so Delete is disabled and Enter does nothing
  await expect(page.getByTestId('help-palette')).toBeVisible();
  await page.keyboard.press('Control+n');
  await expect(page.getByTestId('new-document')).toBeHidden();
});
