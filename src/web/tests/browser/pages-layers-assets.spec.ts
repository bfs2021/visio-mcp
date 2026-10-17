import { test, expect, type Page } from '@playwright/test';
import type { ImageElement } from '../../src/contracts/generated';
import { bmpFixture, centre, openEditor, pngFixture, placeShape, ptToClient, selection, sha256, snapshot, status, svgFixture, type ImageFixture } from './support';

const MM = 72 / 25.4;
/** Clicks an empty part of the page sheet. */
async function clearSelection(page: Page) { const p = await ptToClient(page, { x: 20, y: 570 }); await page.mouse.click(p.x, p.y); expect(await selection(page)).toEqual([]); }
const pages = async (page: Page) => (await snapshot(page)).document.pages;

// ------------------------------------------------------------------------------------------------ pages

test('pages: add, rename, reorder, duplicate, delete, size presets and custom size', async ({ page }) => {
  const errors = await openEditor(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'first');

  await page.getByTestId('page-add').click();
  await expect.poll(async () => (await pages(page)).length).toBe(2);
  expect((await pages(page))[1]!.name).toBe('Page 2');
  await expect(page.getByTestId('page-tab').nth(1)).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('page-sheet')).toBeVisible();
  // the new page is shown, so it is empty on the canvas; page 1 still has its shape
  expect((await pages(page))[1]!.elements).toHaveLength(0);

  await page.getByTestId('page-tab').nth(1).dblclick();
  await page.getByTestId('page-rename-input').fill('Overview');
  await page.getByTestId('page-rename-input').press('Enter');
  await expect.poll(async () => (await pages(page))[1]!.name).toBe('Overview');

  // blank names are refused, the old name stays
  await page.getByTestId('page-tab').nth(1).dblclick();
  await page.getByTestId('page-rename-input').fill('   ');
  await page.getByTestId('page-rename-input').press('Enter');
  expect((await pages(page))[1]!.name).toBe('Overview');
  expect((await status(page)).text).toMatch(/needs a name/);

  await page.getByTestId('page-left').click();
  await expect.poll(async () => (await pages(page)).map(p => p.name)).toEqual(['Overview', 'Page 1']);
  await expect(page.getByTestId('page-left')).toBeDisabled();
  await page.getByTestId('page-right').click();
  await expect.poll(async () => (await pages(page)).map(p => p.name)).toEqual(['Page 1', 'Overview']);

  // presets
  await page.getByTestId('page-size').selectOption('a3-portrait');
  await expect.poll(async () => (await pages(page))[1]!.widthPt).toBeCloseTo(297 * MM, 2);
  expect((await pages(page))[1]!.heightPt).toBeCloseTo(420 * MM, 2);
  expect((await pages(page))[0]!.widthPt).toBeCloseTo(297 * MM, 2); // page 1 is untouched (A4 landscape: 297 wide)
  expect((await pages(page))[0]!.heightPt).toBeCloseTo(210 * MM, 2);
  // custom size in the current unit (mm)
  await page.getByTestId('page-w').fill('250');
  await page.getByTestId('page-h').fill('125');
  await page.getByTestId('page-size-apply').click();
  await expect.poll(async () => (await pages(page))[1]!.widthPt).toBeCloseTo(250 * MM, 2);
  expect((await pages(page))[1]!.heightPt).toBeCloseTo(125 * MM, 2);
  await expect(page.getByTestId('page-size')).toHaveValue('custom');

  // duplicate page 1: same elements with new ids, and it becomes the current page
  await page.getByTestId('page-tab').nth(0).click();
  await page.getByTestId('page-duplicate').click();
  await expect.poll(async () => (await pages(page)).length).toBe(3);
  const all = await pages(page);
  expect(all.map(p => p.name)).toEqual(['Page 1', all[1]!.name, 'Overview']); // the copy sits right after its source
  expect(all[1]!.elements).toHaveLength(1);
  expect(all[1]!.elements[0]!.id).not.toBe(all[0]!.elements[0]!.id);
  expect(all[1]!.elements[0]!.bounds).toEqual(all[0]!.elements[0]!.bounds);
  await expect(page.getByTestId('page-tab').nth(1)).toHaveAttribute('aria-selected', 'true');

  // delete the current page; one page is always kept
  await page.getByTestId('page-delete').click();
  await expect.poll(async () => (await pages(page)).length).toBe(2);
  await page.getByTestId('page-tab').nth(1).click();
  await page.getByTestId('page-delete').click();
  await expect.poll(async () => (await pages(page)).length).toBe(1);
  await expect(page.getByTestId('page-delete')).toBeDisabled();
  expect((await pages(page))[0]!.elements.map(e => e.alias)).toEqual(['first']);
  expect(errors).toEqual([]);
});

test('pages: undo brings back a deleted page and the tabs follow', async ({ page }) => {
  await openEditor(page);
  await page.getByTestId('page-add').click();
  await expect.poll(async () => (await pages(page)).length).toBe(2);
  await page.getByTestId('page-delete').click();
  await expect.poll(async () => (await pages(page)).length).toBe(1);
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-undo').click();
  await expect.poll(async () => (await pages(page)).length).toBe(2);
  await expect(page.getByTestId('page-tab')).toHaveCount(2);
});

test('pages: a connector on the second page is routed (derived routes cover every page)', async ({ page }) => {
  await openEditor(page);
  await page.getByTestId('page-add').click();
  await expect.poll(async () => (await pages(page)).length).toBe(2);
  const p2 = (await pages(page))[1]!.id;
  const a = '00000000-0000-4000-8000-0000000000a1', b = '00000000-0000-4000-8000-0000000000b1';
  const r = await page.evaluate(([pg, ia, ib]) => (window as any).__editor.agentApply([
    { op: 'createShape', id: ia, alias: 'p2a', pageId: pg, bounds: { x: 40, y: 40, width: 80, height: 40 } },
    { op: 'createShape', id: ib, alias: 'p2b', pageId: pg, bounds: { x: 300, y: 200, width: 80, height: 40 } },
    { op: 'createConnector', alias: 'p2c', pageId: pg, from: { elementId: ia, glue: 'dynamic' }, to: { elementId: ib, glue: 'dynamic' } },
  ]), [p2, a, b] as const);
  expect(r.ok).toBe(true);
  const projection = await page.evaluate(() => (window as any).__editor.callTool('doc.inspectLayout', {}));
  expect(JSON.stringify(projection)).not.toContain('UNGLUED'); // the connector is glued on page 2, and the layout tool saw its route
  const shot = await page.evaluate(() => (window as any).__editor.exportSnapshot());
  const conn = shot.document.pages[1].elements.find((e: { kind: string }) => e.kind === 'connector');
  expect(shot.projection.connectors[conn.id].routePoints.length).toBeGreaterThanOrEqual(2);
});

// ------------------------------------------------------------------------------------------------ layers

const layerRows = (page: Page) => page.getByTestId('layer-row');
const currentLayers = async (page: Page) => (await snapshot(page)).document.pages[0]!.layers;

test('layers: add, rename, assign, visible, locked, print, delete, and the flags really change what can be done', async ({ page }) => {
  const errors = await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  const b = await placeShape(page, 'shape-ellipse', { x: 500, y: 150 }, 'b');

  await page.getByTestId('layer-new-name').fill('Notes');
  await page.getByTestId('layer-add').click();
  await expect.poll(async () => (await currentLayers(page)).map(l => l.name)).toEqual(['Layer 1', 'Notes']);
  const notes = (await currentLayers(page))[1]!;

  // rename
  const row = layerRows(page).nth(1);
  await row.getByTestId('layer-name').fill('Annotations');
  await row.getByTestId('layer-name').press('Enter');
  await expect.poll(async () => (await currentLayers(page))[1]!.name).toBe('Annotations');

  // assignment: b joins Annotations as well (an object may belong to several layers)
  const pt = await ptToClient(page, { x: 500, y: 150 });
  await page.mouse.click(pt.x, pt.y);
  expect(await selection(page)).toEqual([b.id]);
  expect((await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!.layerIds).toEqual([]); // a new object starts on no layer: always visible, never locked
  await layerRows(page).first().getByTestId('layer-member').check();
  await row.getByTestId('layer-member').check();
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!.layerIds.length).toBe(2);
  await row.getByTestId('layer-member').uncheck();
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!.layerIds).toHaveLength(1);
  await row.getByTestId('layer-member').check();
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!.layerIds.length).toBe(2);
  await clearSelection(page);

  // visible: hide the layer; b is on it, so b disappears from the canvas and cannot be picked, even by Select all
  await row.getByTestId('layer-visible').uncheck();
  await expect.poll(async () => (await currentLayers(page))[1]!.visible).toBe(false);
  await page.mouse.click(pt.x, pt.y);
  expect(await selection(page)).toEqual([]);
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-select-all').click();
  expect(await selection(page)).toEqual([a.id]);
  // hiding a layer drops anything selected on it
  await row.getByTestId('layer-visible').check();
  await page.mouse.click(pt.x, pt.y);
  expect(await selection(page)).toEqual([b.id]);
  await row.getByTestId('layer-visible').uncheck();
  await expect.poll(async () => selection(page)).toEqual([]);
  await row.getByTestId('layer-visible').check();

  // locked: b cannot move, resize or take inspector edits; a (not on the layer) still can
  await row.getByTestId('layer-locked').check();
  await expect.poll(async () => (await currentLayers(page))[1]!.locked).toBe(true);
  await page.mouse.click(pt.x, pt.y);
  expect(await selection(page)).toEqual([b.id]);
  const before = (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!;
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur()); // keys go to the canvas shortcuts, not a checkbox
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await status(page)).text).toMatch(/locked/i);
  await page.getByTestId('field-w').fill('300');
  await page.getByTestId('field-w').press('Enter');
  await expect.poll(async () => (await status(page)).text).toMatch(/locked/i);
  const dragFrom = await centre(page, b.id);
  await page.mouse.move(dragFrom.x, dragFrom.y); await page.mouse.down(); await page.mouse.move(dragFrom.x + 80, dragFrom.y + 40, { steps: 5 }); await page.mouse.up();
  const after = (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!;
  expect(after.bounds).toEqual(before.bounds);
  expect(after.rotationDeg).toBe(before.rotationDeg);
  const free = await ptToClient(page, { x: 200, y: 150 });
  await page.mouse.click(free.x, free.y);
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await snapshot(page)).document.pages[0]!.elements.find(e => e.id === a.id)!.bounds.x).toBeGreaterThan(a.bounds.x);
  await row.getByTestId('layer-locked').uncheck();

  // print flag
  await row.getByTestId('layer-print').uncheck();
  await expect.poll(async () => (await currentLayers(page))[1]!.printable).toBe(false);

  // delete: members lose the membership, nothing else changes; the last layer cannot be deleted
  await row.getByTestId('layer-delete').click();
  await expect.poll(async () => (await currentLayers(page)).length).toBe(1);
  expect((await snapshot(page)).document.pages[0]!.elements.find(e => e.id === b.id)!.layerIds).toEqual([(await currentLayers(page))[0]!.id]);
  expect((await snapshot(page)).document.pages[0]!.layers.some(l => l.id === notes.id)).toBe(false);
  await expect(layerRows(page).first().getByTestId('layer-delete')).toBeDisabled();
  expect(errors).toEqual([]);
});

// ------------------------------------------------------------------------------------------------ pictures

interface Entry { id: string; name: string; fx: ImageFixture; tags: string[] }

async function armLibrary(page: Page, entries: Entry[], extra: { replacement?: ImageFixture; imports?: Entry[] } = {}) {
  const fixtures = [...entries.map(e => e.fx), ...(extra.replacement ? [extra.replacement] : []), ...(extra.imports ?? []).map(e => e.fx)];
  for (const fx of fixtures) await page.route(`**/assets/${sha256(fx.bytes)}`, route => route.fulfill({ status: 200, contentType: fx.mime, body: fx.bytes }));
  const plain = (e: Entry) => ({ id: e.id, name: e.name, tags: e.tags, mimeType: e.fx.mime, sha256: sha256(e.fx.bytes), width: e.fx.width, height: e.fx.height, version: 1, scope: 'library' });
  await page.evaluate(([list, replacementSha, imports]) => {
    const lib = list.map(x => ({ ...x }));
    const queue = imports.map(x => ({ ...x }));
    (window as any).__lib = lib;
    (window as any).__editor.setHost({
      libraryList: async () => ({ ok: true, value: { assets: lib.map(x => ({ ...x })) } }),
      libraryImport: async () => {
        const next = queue.shift();
        if (!next) return { ok: true, value: { cancelled: true } };
        lib.push(next);
        return { ok: true, value: { asset: next, created: true, idWasTaken: false, notes: [] } };
      },
      libraryUse: async (id: string, hash?: string) => {
        const a = lib.find(x => x.id === id);
        if (!a) return { ok: false, error: { code: 'not_found', message: `no library asset ${id}`, retryable: false } };
        return { ok: true, value: { ref: { preparationId: 'prep-1', expiresAt: '2030-01-01T00:00:00Z', asset: { id: a.id, name: a.name, mimeType: a.mimeType, sha256: hash ?? a.sha256, tags: a.tags, provenance: 'library' } }, notes: [], width: a.width, height: a.height } };
      },
      librarySetMetadata: async (id: string, patch: { name?: string; tags?: string[] }) => {
        const a = lib.find(x => x.id === id)!;
        if (patch.name) a.name = patch.name;
        if (patch.tags) a.tags = patch.tags;
        return { ok: true, value: { asset: { ...a } } };
      },
      libraryDelete: async (id: string) => { lib.splice(lib.findIndex(x => x.id === id), 1); return { ok: true, value: { deleted: id } }; },
      libraryReplaceSource: async (id: string) => {
        const a = lib.find(x => x.id === id)!;
        a.sha256 = replacementSha; a.version += 1;
        return { ok: true, value: { asset: { ...a } } };
      },
    });
  }, [entries.map(plain), extra.replacement ? sha256(extra.replacement.bytes) : '', (extra.imports ?? []).map(plain)] as const);
}

const rowFor = (page: Page, scope: 'document' | 'library', id: string) => page.locator(`[data-testid=asset-row][data-scope=${scope}][data-asset-id="${id}"]`);
const images = async (page: Page) => (await snapshot(page)).document.pages[0]!.elements.filter((e): e is ImageElement => e.kind === 'image');

test('pictures: library list with thumbnails, import, tags, rename, place, and the document pins what it uses', async ({ page }) => {
  const errors = await openEditor(page);
  const png = pngFixture(), svg = svgFixture(), bmp = bmpFixture();
  const logo: Entry = { id: 'asset:checker', name: 'checker.png', fx: png, tags: ['brand'] };
  const later: Entry = { id: 'asset:gradient', name: 'gradient.bmp', fx: bmp, tags: [] };
  await armLibrary(page, [logo, { id: 'asset:logo', name: 'logo.svg', fx: svg, tags: [] }], { imports: [later] });
  await page.getByTestId('asset-refresh').click(); // the panel first read the library before the test host existed
  await expect(rowFor(page, 'library', 'asset:checker')).toBeVisible();
  await page.getByTestId('asset-import').click(); // adds the next file in the queue, then lists again

  await expect(rowFor(page, 'library', 'asset:checker')).toBeVisible();
  await expect(rowFor(page, 'library', 'asset:gradient')).toBeVisible();
  // thumbnails are real pictures that loaded
  await expect.poll(() => rowFor(page, 'library', 'asset:checker').getByTestId('asset-thumb').evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth > 0)).toBe(true);
  expect((await snapshot(page)).document.assets).toEqual([]); // browsing the library pins nothing

  // tags and rename (library scope)
  await rowFor(page, 'library', 'asset:checker').click();
  await page.getByTestId('asset-tags').fill('Brand, partner , brand');
  await page.getByTestId('asset-tag-apply').click();
  await expect.poll(() => page.evaluate(() => (window as any).__lib.find((x: { id: string }) => x.id === 'asset:checker').tags)).toEqual(['brand', 'partner']);
  await page.getByTestId('asset-name').fill('Checker mark');
  await page.getByTestId('asset-rename').click();
  await expect(rowFor(page, 'library', 'asset:checker').locator('strong')).toHaveText('Checker mark');

  // placing pins the descriptor and creates the image in one transaction
  const rev = (await snapshot(page)).revision;
  await page.getByTestId('asset-place').click();
  await expect.poll(async () => (await images(page)).length).toBe(1);
  const snap = await snapshot(page);
  expect(snap.revision).toBe(rev + 1);
  expect(snap.document.assets.map(a => a.id)).toEqual(['asset:checker']);
  expect(snap.document.assets[0]!.sha256).toBe(sha256(png.bytes));
  await expect(rowFor(page, 'document', 'asset:checker')).toBeVisible();
  await expect(rowFor(page, 'document', 'asset:checker').locator('.badge').first()).toHaveText('1');
  expect(errors).toEqual([]);
});

test('pictures: single replacement changes one image, global replacement changes all and reports how many', async ({ page }) => {
  const errors = await openEditor(page);
  const png = pngFixture(), svg = svgFixture(), bmp = bmpFixture();
  const entries: Entry[] = [
    { id: 'asset:checker', name: 'checker.png', fx: png, tags: [] },
    { id: 'asset:logo', name: 'logo.svg', fx: svg, tags: [] },
    { id: 'asset:gradient', name: 'gradient.bmp', fx: bmp, tags: [] },
  ];
  await armLibrary(page, entries);
  await page.getByTestId('asset-refresh').click();
  // place the same library picture twice
  await rowFor(page, 'library', 'asset:checker').click();
  await page.getByTestId('asset-place').click();
  await expect.poll(async () => (await images(page)).length).toBe(1);
  await rowFor(page, 'library', 'asset:checker').click();
  await page.getByTestId('asset-place').click();
  await expect.poll(async () => (await images(page)).length).toBe(2);
  const [first, second] = await images(page);
  expect(first!.assetId).toBe('asset:checker');

  // single: select the second picture on the page, use the logo for it; the first keeps the checker
  const unrelatedBefore = JSON.stringify((await images(page)).find(i => i.id === first!.id));
  const pt = await centre(page, second!.id);
  await page.mouse.click(pt.x, pt.y);
  expect(await selection(page)).toEqual([second!.id]);
  await rowFor(page, 'library', 'asset:logo').click();
  await page.getByTestId('asset-use-one').click();
  await expect.poll(async () => (await images(page)).find(i => i.id === second!.id)!.assetId).toBe('asset:logo');
  expect(JSON.stringify((await images(page)).find(i => i.id === first!.id))).toBe(unrelatedBefore); // only that image changed
  expect((await images(page)).find(i => i.id === first!.id)!.assetId).toBe('asset:checker');
  expect((await snapshot(page)).document.assets.map(a => a.id).sort()).toEqual(['asset:checker', 'asset:logo']);

  // global: every use of the checker becomes the gradient, and the status says how many
  await rowFor(page, 'document', 'asset:checker').click();
  await page.getByTestId('asset-replace-with').selectOption('library:asset:gradient');
  await page.getByTestId('asset-replace-all').click();
  await expect.poll(async () => (await images(page)).find(i => i.id === first!.id)!.assetId).toBe('asset:gradient');
  expect((await status(page)).text).toMatch(/in 1 picture/);
  expect((await images(page)).find(i => i.id === second!.id)!.assetId).toBe('asset:logo'); // the logo image is not a user of the checker

  // an asset that images still use cannot be deleted; an unused one can
  await rowFor(page, 'document', 'asset:gradient').click();
  await page.getByTestId('asset-delete').click();
  expect((await status(page)).text).toMatch(/still used by 1 picture/);
  expect((await snapshot(page)).document.assets.some(a => a.id === 'asset:gradient')).toBe(true);
  await rowFor(page, 'document', 'asset:checker').click();
  await page.getByTestId('asset-delete').click();
  await expect.poll(async () => (await snapshot(page)).document.assets.some(a => a.id === 'asset:checker')).toBe(false);

  // Use for selected picture needs exactly one picture selected
  await clearSelection(page);
  await rowFor(page, 'library', 'asset:logo').click();
  await page.getByTestId('asset-use-one').click();
  expect((await status(page)).text).toMatch(/Select one picture/);
  expect(errors).toEqual([]);
});

test('pictures: a newer library version of an id the document already pins becomes a separate version; old images stay', async ({ page }) => {
  const errors = await openEditor(page);
  const png = pngFixture(), svg = svgFixture();
  await armLibrary(page, [{ id: 'asset:logo', name: 'logo.png', fx: png, tags: [] }], { replacement: svg });
  await page.getByTestId('asset-refresh').click();
  await rowFor(page, 'library', 'asset:logo').click();
  await page.getByTestId('asset-place').click();
  await expect.poll(async () => (await images(page)).length).toBe(1);
  const pinnedBefore = (await snapshot(page)).document.assets.find(a => a.id === 'asset:logo')!;

  // the library source changes; the document keeps what it pinned
  await rowFor(page, 'library', 'asset:logo').click();
  await page.getByTestId('asset-replace-source').click();
  await expect(rowFor(page, 'document', 'asset:logo').getByText('newer')).toBeVisible();
  expect((await snapshot(page)).document.assets.find(a => a.id === 'asset:logo')).toEqual(pinnedBefore);
  expect((await images(page))[0]!.assetId).toBe('asset:logo');

  // placing the library version again pins a distinct, full-hash version next to the old one
  await rowFor(page, 'library', 'asset:logo').click();
  await page.getByTestId('asset-place').click();
  await expect.poll(async () => (await images(page)).length).toBe(2);
  const versionId = `asset:logo~${sha256(svg.bytes)}`;
  const snap = await snapshot(page);
  expect(snap.document.assets.map(a => a.id).sort()).toEqual(['asset:logo', versionId].sort());
  expect(snap.document.assets.find(a => a.id === 'asset:logo')).toEqual(pinnedBefore);
  expect((await images(page)).map(i => i.assetId).sort()).toEqual(['asset:logo', versionId].sort());
  expect(errors).toEqual([]);
});

test('pictures: drag a picture from the panel onto the page, rename and tag a document asset, copy its id, delete a library entry', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  await armLibrary(page, [{ id: 'asset:checker', name: 'checker.png', fx: png, tags: [] }]);
  await page.getByTestId('asset-refresh').click();
  const source = rowFor(page, 'library', 'asset:checker');
  await expect(source).toBeVisible();
  const target = await ptToClient(page, { x: 300, y: 200 });
  await source.dragTo(page.getByTestId('canvas'), { targetPosition: { x: Math.round(target.x - (await page.getByTestId('canvas').boundingBox())!.x), y: Math.round(target.y - (await page.getByTestId('canvas').boundingBox())!.y) } });
  await expect.poll(async () => (await images(page)).length).toBe(1);
  const img = (await images(page))[0]!;
  expect(img.bounds.x + img.bounds.width / 2).toBeCloseTo(300, -1);

  await rowFor(page, 'document', 'asset:checker').click();
  await page.getByTestId('asset-name').fill('Mark');
  await page.getByTestId('asset-rename').click();
  await expect.poll(async () => (await snapshot(page)).document.assets[0]!.name).toBe('Mark');
  await page.getByTestId('asset-tags').fill('x, y');
  await page.getByTestId('asset-tag-apply').click();
  await expect.poll(async () => (await snapshot(page)).document.assets[0]!.tags).toEqual(['x', 'y']);
  await page.getByTestId('asset-copy-id').click();
  await expect.poll(async () => (await status(page)).text).toContain('asset:checker');

  // removing the library entry leaves the document's own copy alone
  await rowFor(page, 'library', 'asset:checker').click();
  await page.getByTestId('asset-delete').click();
  await expect(rowFor(page, 'library', 'asset:checker')).toHaveCount(0);
  expect((await snapshot(page)).document.assets.map(a => a.id)).toEqual(['asset:checker']);
  expect((await images(page))).toHaveLength(1);
});
