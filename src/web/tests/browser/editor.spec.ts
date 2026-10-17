import { test, expect } from '@playwright/test';
import type { ConnectorElement, ImageElement, ShapeElement, TextElement } from '../../src/contracts/generated';
import {
  armPicture, bmpFixture, byAlias, centre, drag, elements, handle, jpegFixture, openEditor, placeShape, pngFixture, ptToClient, revision, scale,
  selection, sha256, snapshot, status, svgFixture,
} from './support';

const MM = 72 / 25.4;
const GRID = 5 * MM; // 5 mm, the default page grid

test('manual_edit_without_llm', async ({ page }) => {
  const errors = await openEditor(page);

  await test.step('a finite A4 landscape page with unit-labelled rulers and a visible grid', async () => {
    const sheet = await page.getByTestId('page-sheet').boundingBox();
    expect(sheet!.width / sheet!.height).toBeCloseTo(297 / 210, 2);
    await expect(page.getByTestId('ruler-h')).toHaveAttribute('data-unit', 'mm');
    await expect(page.getByTestId('ruler-v')).toHaveAttribute('data-unit', 'mm');
    await expect(page.getByTestId('page-sheet')).toHaveAttribute('data-grid', 'on');
    const rulerPixels = await page.getByTestId('ruler-h').evaluate((c: HTMLCanvasElement) => c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data.some(v => v !== 0));
    expect(rulerPixels).toBe(true);
    expect((await snapshot(page)).document.pages[0]!.widthPt).toBeCloseTo(297 * MM, 3);
  });

  let rect!: ShapeElement, ellipse!: ShapeElement;
  await test.step('create a rectangle and an ellipse from the palette, snapped to the grid', async () => {
    rect = await placeShape(page, 'shape-rectangle', { x: 120, y: 120 }, 'rect') as ShapeElement;
    ellipse = await placeShape(page, 'shape-ellipse', { x: 420, y: 160 }, 'ellipse') as ShapeElement;
    for (const s of [rect, ellipse]) {
      expect(s.kind).toBe('shape');
      expect(s.bounds.x / GRID).toBeCloseTo(Math.round(s.bounds.x / GRID), 4);
      expect(s.bounds.y / GRID).toBeCloseTo(Math.round(s.bounds.y / GRID), 4);
    }
    expect(rect.geometry.preset).toBe('rectangle');
    expect(ellipse.geometry.preset).toBe('ellipse');
    // The canvas really draws them: the topmost thing under each centre is the shape's own SVG node, with its fill, not the page sheet or the svg root.
    for (const [id, fill] of [[rect.id, '#ffffff'], [ellipse.id, '#ffffff']] as const) {
      const c = await centre(page, id);
      const hit = await page.evaluate(({ x, y }) => { const n = document.elementFromPoint(x, y); return { tag: n?.tagName, fill: n?.getAttribute('fill') }; }, c);
      expect(['rect', 'path', 'ellipse']).toContain(hit.tag);
      expect(hit.fill).toBe(fill);
    }
  });

  await test.step('create a text box and edit its text in place', async () => {
    await page.getByTestId('tool-text').click();
    const at = await ptToClient(page, { x: 120, y: 330 });
    await page.mouse.click(at.x, at.y);
    await expect.poll(async () => (await elements(page)).some(e => e.kind === 'text')).toBe(true);
    const textEl = (await elements(page)).find(e => e.kind === 'text') as TextElement;
    expect(textEl.text.value).toBe('Text');
    await page.evaluate(id => (window as any).__editor.textEdit(id, 'Hello'), textEl.id);
    await expect.poll(async () => ((await elements(page)).find(e => e.id === textEl.id) as TextElement).text.value).toBe('Hello');
  });

  await test.step('connect the shapes with a glued connector', async () => {
    await page.getByTestId('tool-connector').click();
    await drag(page, await centre(page, rect.id), await centre(page, ellipse.id));
    await expect.poll(async () => (await elements(page)).filter(e => e.kind === 'connector').length).toBe(1);
    const c = (await elements(page)).find(e => e.kind === 'connector') as ConnectorElement;
    expect(c.from.elementId).toBe(rect.id);
    expect(c.to.elementId).toBe(ellipse.id);
    expect(c.from.glue).toBe('dynamic');
    await expect(page.getByTestId('tool-select')).toHaveAttribute('aria-pressed', 'true'); // the tool returns to select
  });

  await test.step('move a shape by dragging: one revision, the connector stays glued and untouched', async () => {
    const connectorBefore = JSON.stringify((await elements(page)).find(e => e.kind === 'connector'));
    await page.mouse.click((await centre(page, rect.id)).x, (await centre(page, rect.id)).y);
    expect(await selection(page)).toEqual([rect.id]);
    const r0 = await revision(page);
    const from = await centre(page, rect.id);
    const k = await scale(page);
    await drag(page, from, { x: from.x + 40 * k, y: from.y + 30 * k });
    await expect.poll(() => revision(page)).toBe(r0 + 1);
    const moved = await byAlias(page, 'rect');
    expect(moved.bounds.x).toBeGreaterThan(rect.bounds.x + 20);
    expect(moved.bounds.x / GRID).toBeCloseTo(Math.round(moved.bounds.x / GRID), 4); // committed coordinates are the snapped ones
    expect(moved.bounds.y / GRID).toBeCloseTo(Math.round(moved.bounds.y / GRID), 4);
    expect(JSON.stringify((await elements(page)).find(e => e.kind === 'connector'))).toBe(connectorBefore);
    rect = moved as ShapeElement;
  });

  await test.step('resize from the corner handle and rotate from the rotation handle', async () => {
    await page.mouse.click((await centre(page, rect.id)).x, (await centre(page, rect.id)).y);
    const r0 = await revision(page);
    const se = await handle(page, rect.id, 'se');
    const k = await scale(page);
    await drag(page, se, { x: se.x + 30 * k, y: se.y + 20 * k });
    await expect.poll(() => revision(page)).toBe(r0 + 1);
    const resized = await byAlias(page, 'rect');
    expect(resized.bounds.width).toBeGreaterThan(rect.bounds.width + 15);
    expect((resized.bounds.x + resized.bounds.width) / GRID).toBeCloseTo(Math.round((resized.bounds.x + resized.bounds.width) / GRID), 4);

    const c = await centre(page, rect.id);
    const rh = await handle(page, rect.id, 'rotate');
    await drag(page, rh, { x: c.x + 120, y: c.y });
    await expect.poll(async () => (await byAlias(page, 'rect')).rotationDeg).not.toBe(0);
    expect(Math.abs((await byAlias(page, 'rect')).rotationDeg - 90)).toBeLessThan(15);
    rect = await byAlias(page, 'rect') as ShapeElement;
  });

  await test.step('inspector: style, text and geometry in millimetres', async () => {
    await page.mouse.click((await centre(page, rect.id)).x, (await centre(page, rect.id)).y);
    await expect(page.getByTestId('inspector')).toBeVisible();
    await page.getByTestId('field-fill-colour').fill('#ff0000');
    await expect.poll(async () => ((await byAlias(page, 'rect')) as ShapeElement).style.fill).toEqual({ kind: 'solid', colour: '#ff0000', opacity: 1 });
    await page.getByTestId('field-text').fill('Process');
    await page.getByTestId('field-text').blur();
    await expect.poll(async () => ((await byAlias(page, 'rect')) as ShapeElement).text?.value).toBe('Process');
    await page.getByTestId('field-x').fill('50');
    await page.getByTestId('field-x').press('Enter');
    await expect.poll(async () => (await byAlias(page, 'rect')).bounds.x).toBeCloseTo(50 * MM, 3);
    await page.getByTestId('field-angle').fill('0');
    await page.getByTestId('field-angle').press('Enter');
    await expect.poll(async () => (await byAlias(page, 'rect')).rotationDeg).toBe(0);
    await page.getByTestId('field-locked').check();
    await expect.poll(async () => (await byAlias(page, 'rect')).locked).toBe(true);
    await page.getByTestId('field-locked').uncheck();
    await expect.poll(async () => (await byAlias(page, 'rect')).locked).toBe(false);
  });

  await test.step('undo and redo restore exact state with new revisions', async () => {
    const before = JSON.stringify((await elements(page)).find(e => e.alias === 'rect'));
    const r0 = await revision(page);
    await page.getByTestId('field-x').fill('80');
    await page.getByTestId('field-x').press('Enter');
    await expect.poll(() => revision(page)).toBe(r0 + 1);
    await page.getByTestId('tool-undo').click();
    await expect.poll(() => revision(page)).toBe(r0 + 2);
    expect(JSON.stringify((await elements(page)).find(e => e.alias === 'rect'))).toBe(before);
    await page.getByTestId('tool-redo').click();
    await expect.poll(async () => (await byAlias(page, 'rect')).bounds.x).toBeCloseTo(80 * MM, 3);
    await page.locator('body').click({ position: { x: 700, y: 20 } });
    await page.keyboard.press('Control+z');
    await expect.poll(async () => (await byAlias(page, 'rect')).bounds.x).not.toBeCloseTo(80 * MM, 3);
  });

  await test.step('multi-selection by rubber band moves both shapes in one revision', async () => {
    const a = await ptToClient(page, { x: 20, y: 40 });
    const b = await ptToClient(page, { x: 560, y: 260 });
    await drag(page, a, b);
    await expect.poll(async () => (await selection(page)).length).toBeGreaterThanOrEqual(2);
    const sel = await selection(page);
    expect(sel).toContain(rect.id);
    expect(sel).toContain(ellipse.id);
    const r0 = await revision(page);
    const ids = (await selection(page)).filter(id => id === rect.id || id === ellipse.id);
    const lead = await centre(page, ellipse.id);
    const k = await scale(page);
    const before = await Promise.all(ids.map(async id => (await elements(page)).find(e => e.id === id)!.bounds));
    await drag(page, lead, { x: lead.x, y: lead.y + 40 * k });
    await expect.poll(() => revision(page)).toBe(r0 + 1);
    const after = await Promise.all(ids.map(async id => (await elements(page)).find(e => e.id === id)!.bounds));
    for (let i = 0; i < ids.length; i++) expect(after[i]!.y).toBeGreaterThan(before[i]!.y + 20);
  });

  await test.step('copy and paste regenerate identities and re-attach copied connectors', async () => {
    await page.evaluate(() => (window as any).__editor.selection());
    await page.locator('body').click({ position: { x: 700, y: 20 } });
    await page.keyboard.press('Control+a');
    const total = (await elements(page)).length;
    await page.keyboard.press('Control+c');
    await page.keyboard.press('Control+v');
    await expect.poll(async () => (await elements(page)).length).toBe(total * 2 - ((await elements(page)).filter(e => e.kind === 'connector').length > 0 ? 0 : 0));
    const els = await elements(page);
    expect(new Set(els.map(e => e.id)).size).toBe(els.length);
    const connectors = els.filter((e): e is ConnectorElement => e.kind === 'connector');
    expect(connectors).toHaveLength(2);
    const copy = connectors.find(c => c.from.elementId !== rect.id)!;
    const ids = new Set(els.map(e => e.id));
    expect(ids.has(copy.from.elementId!)).toBe(true);
    expect(ids.has(copy.to.elementId!)).toBe(true);
    expect(copy.from.elementId).not.toBe(rect.id);
    const aliases = els.map(e => e.alias).filter(Boolean);
    expect(new Set(aliases).size).toBe(aliases.length);
  });

  await test.step('delete removes a shape together with its connector', async () => {
    const before = (await elements(page)).length;
    await page.locator('body').click({ position: { x: 700, y: 20 } });
    // Pasted copies sit on top of the originals, so a click on this spot selects the topmost ellipse (a copy). Either way, the same rule applies.
    const c = await centre(page, ellipse.id);
    await page.mouse.click(c.x, c.y);
    const [victim] = await selection(page);
    expect((await elements(page)).find(e => e.id === victim)).toMatchObject({ kind: 'shape', geometry: { preset: 'ellipse' } });
    await page.keyboard.press('Delete');
    await expect.poll(async () => (await elements(page)).length).toBe(before - 2); // the ellipse and the connector that ended at it
    expect((await elements(page)).some(e => e.id === victim)).toBe(false);
    expect((await elements(page)).filter(e => e.kind === 'connector').every(c2 => (c2 as ConnectorElement).to.elementId !== victim)).toBe(true);
  });

  await test.step('zoom and pan: pointer-centred zoom, fit and scroll', async () => {
    const sheet0 = await page.getByTestId('page-sheet').boundingBox();
    const host = await page.getByTestId('canvas').boundingBox();
    await page.mouse.move(host!.x + host!.width / 2, host!.y + host!.height / 2);
    await page.keyboard.down('Control');
    await page.mouse.wheel(0, -300);
    await page.keyboard.up('Control');
    await expect.poll(async () => (await page.getByTestId('page-sheet').boundingBox())!.width).toBeGreaterThan(sheet0!.width * 1.2);
    await page.getByTestId('tool-zoom-fit').click();
    await expect.poll(async () => Math.abs((await page.getByTestId('page-sheet').boundingBox())!.width - sheet0!.width)).toBeLessThan(sheet0!.width * 0.3);
    const sheet1 = await page.getByTestId('page-sheet').boundingBox();
    expect(sheet1!.width / sheet1!.height).toBeCloseTo(297 / 210, 2); // still A4
  });

  expect(errors).toEqual([]);
});

test('each picture format becomes an independent image object with its own hash', async ({ page }) => {
  const errors = await openEditor(page);
  const fixtures = [pngFixture(), await jpegFixture(page), bmpFixture(), svgFixture()];
  for (const fx of fixtures) {
    const sha = await armPicture(page, fx);
    const before = (await elements(page)).length;
    await page.getByTestId('insert-picture').click();
    await expect.poll(async () => (await elements(page)).length).toBe(before + 1);
    const img = (await elements(page)).find(e => e.kind === 'image' && (e as ImageElement).assetId.endsWith(sha)) as ImageElement;
    expect(img).toBeDefined();
    const asset = (await snapshot(page)).document.assets.find(a => a.id === img.assetId)!;
    expect(asset.sha256).toBe(sha256(fx.bytes)); // the document records the hash of the exact bytes
    expect(asset.mimeType).toBe(fx.mime);
    expect(img.preserveAspectRatio).toBe(true);
    expect(await selection(page)).toEqual([img.id]);
  }
  const imgs = (await elements(page)).filter((e): e is ImageElement => e.kind === 'image');
  expect(imgs).toHaveLength(4);
  expect(new Set(imgs.map(i => i.id)).size).toBe(4);
  expect(new Set(imgs.map(i => i.assetId)).size).toBe(4); // four independent assets, not one flattened picture
  // The same bytes inserted again reuse the asset (one blob) but create a second independent object.
  await armPicture(page, fixtures[0]!);
  await page.getByTestId('insert-picture').click();
  await expect.poll(async () => (await elements(page)).filter(e => e.kind === 'image').length).toBe(5);
  expect((await snapshot(page)).document.assets).toHaveLength(4);
  await page.waitForTimeout(200);
  expect(errors.filter(e => !/Failed to load resource|favicon/.test(e))).toEqual([]);
});

test('grid toggle, snapping and alignment guides', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 100, y: 100 }, 'a');
  const b = await placeShape(page, 'shape-ellipse', { x: 400, y: 260 }, 'b');
  void a;

  // Grid visibility is a page property changed through the engine.
  await page.getByTestId('menu-view').click();
  await page.getByTestId('menu-grid').click();
  await expect(page.getByTestId('page-sheet')).toHaveAttribute('data-grid', 'off');
  expect((await snapshot(page)).document.pages[0]!.grid.visible).toBe(false);
  await page.getByTestId('menu-view').click();
  await page.getByTestId('menu-grid').click();
  await expect(page.getByTestId('page-sheet')).toHaveAttribute('data-grid', 'on');

  // With snapping on, coordinates land on configured spacing multiples; with it off they do not.
  const k = await scale(page);
  const c0 = await centre(page, b.id);
  await page.mouse.click(c0.x, c0.y);
  await drag(page, c0, { x: c0.x + 13.3 * k, y: c0.y + 7.7 * k });
  await expect.poll(async () => (await byAlias(page, 'b')).bounds.x).not.toBe(b.bounds.x);
  const snapped = await byAlias(page, 'b');
  expect(snapped.bounds.x / GRID).toBeCloseTo(Math.round(snapped.bounds.x / GRID), 4);
  await page.getByTestId('menu-view').click();
  await page.getByTestId('menu-snap').click();
  expect((await snapshot(page)).document.pages[0]!.grid.snap).toBe(false);
  const c1 = await centre(page, b.id);
  await drag(page, c1, { x: c1.x + 13.3 * k, y: c1.y + 7.7 * k });
  await expect.poll(async () => (await byAlias(page, 'b')).bounds.x).not.toBe(snapped.bounds.x);
  const free = await byAlias(page, 'b');
  expect(Math.abs(free.bounds.x / GRID - Math.round(free.bounds.x / GRID))).toBeGreaterThan(0.02);
  await page.getByTestId('menu-view').click();
  await page.getByTestId('menu-snap').click();

  // An alignment guide is drawn while an edge lines up with another shape.
  const target = await byAlias(page, 'a');
  const mover = await byAlias(page, 'b');
  const mc = await centre(page, mover.id);
  const wantTopY = target.bounds.y + 1; // within a pixel or two of a's top edge
  await drag(page, mc, { x: mc.x, y: mc.y + (wantTopY - mover.bounds.y) * k }, { release: false, steps: 12 });
  const guide = await page.evaluate(() => [...document.querySelectorAll('.canvas-host svg path, .canvas-host svg line, .canvas-host svg polyline')]
    .some(n => /^(#ff0000|rgb\(255, 0, 0\))$/i.test(n.getAttribute('stroke') ?? '')));
  await page.mouse.up();
  expect(guide).toBe(true);
});

test('long_text_edit_and_drag_stale_backstop', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 100, y: 100 }, 'a');
  const k = await scale(page);

  // A drag in progress: the interlock is raised, an agent edit that was already admitted still commits, and the human drag is then discarded.
  await page.mouse.click((await centre(page, a.id)).x, (await centre(page, a.id)).y);
  const c = await centre(page, a.id);
  await drag(page, c, { x: c.x + 60 * k, y: c.y + 40 * k }, { release: false });
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(true);
  const r0 = await revision(page);
  await page.evaluate(id => (window as any).__editor.agentApply([{ op: 'set', target: id, patch: { name: 'agent was here' } }]), a.id);
  await expect.poll(() => revision(page)).toBe(r0 + 1);
  await page.mouse.up();
  await expect.poll(async () => (await status(page)).text).toContain('discarded');
  const after = await byAlias(page, 'a');
  expect(after.name).toBe('agent was here');
  expect(after.bounds).toEqual(a.bounds); // the stale drag never overwrote the newer state
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(false);

  // Escape cancels an in-progress drag: nothing commits, the interlock drops.
  const r1 = await revision(page);
  const c2 = await centre(page, a.id);
  await drag(page, c2, { x: c2.x + 50 * k, y: c2.y }, { release: false });
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(true);
  await page.keyboard.press('Escape');
  await page.mouse.up();
  expect(await revision(page)).toBe(r1);
  expect((await byAlias(page, 'a')).bounds).toEqual(a.bounds);
  await expect.poll(() => page.evaluate(() => (window as any).__editor.gestureActive())).toBe(false);

  // In-place text editing also raises the interlock; committing releases it with one revision.
  const r2 = await revision(page);
  await page.evaluate(id => (window as any).__editor.textEdit(id, 'typed text'), a.id);
  await expect.poll(() => revision(page)).toBe(r2 + 1);
  expect(((await byAlias(page, 'a')) as ShapeElement).text?.value).toBe('typed text');
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(false);
  // A cancelled text edit changes nothing.
  await page.evaluate(id => (window as any).__editor.textEdit(id, null), a.id);
  expect(await revision(page)).toBe(r2 + 1);

  // Selection and zoom never raise the interlock.
  await page.mouse.click((await centre(page, a.id)).x, (await centre(page, a.id)).y);
  await page.getByTestId('tool-zoom-in').click();
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(false);
});

test('export during a drag uses committed geometry', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 100, y: 100 }, 'a');
  await page.mouse.click((await centre(page, a.id)).x, (await centre(page, a.id)).y);
  const c = await centre(page, a.id);
  const k = await scale(page);
  await drag(page, c, { x: c.x + 80 * k, y: c.y + 80 * k }, { release: false });
  const exp = await page.evaluate(() => (window as any).__editor.exportSnapshot());
  expect(exp.error).toBeUndefined();
  const el = exp.document.pages[0].elements.find((e: { alias?: string }) => e.alias === 'a');
  expect(el.bounds).toEqual(a.bounds);
  expect(exp.projection.revision).toBe(exp.revision);
  await page.mouse.up();
});

test('projection does not emit edits and cell types do not escape', async ({ page }) => {
  await openEditor(page);
  const r0 = await revision(page);
  // An agent edit is projected onto the canvas without the canvas reporting it back as a GUI edit.
  await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'createShape', alias: 'p', bounds: { x: 60, y: 60, width: 100, height: 50 } }]));
  await expect.poll(() => revision(page)).toBe(r0 + 1);
  await page.waitForTimeout(300);
  expect(await revision(page)).toBe(r0 + 1); // no echo transaction
  const changes = await page.evaluate(async () => {
    const e = (window as any).__editor;
    return e.snapshot().revision;
  });
  expect(changes).toBe(r0 + 1);
  // The test window exposes plain data only: ids, bounds and JSON, never canvas cells.
  const leak = await page.evaluate(() => {
    const e = (window as any).__editor;
    const seen = [e.snapshot(), e.selection(), e.centre(e.snapshot().document.pages[0].elements[0].id)];
    return seen.some(v => JSON.stringify(v).includes('"cell"') || JSON.stringify(v).includes('mxCell'));
  });
  expect(leak).toBe(false);
  const el = await byAlias(page, 'p');
  expect(el.id).toMatch(/^[0-9a-f-]{36}$/);
});

test('connector glue follows a moved shape and survives projection rebuilds', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 80, y: 100 }, 'a');
  const b = await placeShape(page, 'shape-ellipse', { x: 420, y: 100 }, 'b');
  await page.getByTestId('tool-connector').click();
  await drag(page, await centre(page, a.id), await centre(page, b.id));
  await expect.poll(async () => (await elements(page)).some(e => e.kind === 'connector')).toBe(true);
  const conn = (await elements(page)).find(e => e.kind === 'connector') as ConnectorElement;
  const before = await centre(page, conn.id);
  // Move b via an agent edit: the canvas reroutes the connector from the unchanged canonical endpoints.
  await page.evaluate(id => (window as any).__editor.agentApply([{ op: 'move', target: id, delta: { xPt: 0, yPt: 150 } }]), b.id);
  await expect.poll(async () => (await centre(page, conn.id)).y).not.toBeCloseTo(before.y, 0);
  const after = (await elements(page)).find(e => e.id === conn.id) as ConnectorElement;
  expect(JSON.stringify(after)).toBe(JSON.stringify(conn)); // canonical connector unchanged
  expect(after.to.elementId).toBe(b.id);
  const exp = await page.evaluate(() => (window as any).__editor.exportSnapshot());
  expect(exp.projection.connectors[conn.id].routePoints.length).toBeGreaterThanOrEqual(2); // derived route, carried with the snapshot revision
});

test('evidence: a small diagram built by hand', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-flowchart-rounded-rectangle', { x: 90, y: 90 }, 'start');
  const b = await placeShape(page, 'shape-flowchart-diamond', { x: 330, y: 90 }, 'decide');
  const c = await placeShape(page, 'shape-network-cylinder', { x: 570, y: 70 }, 'db');
  for (const [el, text] of [[a, 'Start'], [b, 'OK?'], [c, 'Store']] as const) {
    await page.evaluate(([id, t]) => (window as any).__editor.textEdit(id, t), [el.id, text] as const);
  }
  for (const [from, to] of [[a, b], [b, c]] as const) {
    await page.getByTestId('tool-connector').click();
    await drag(page, await centre(page, from.id), await centre(page, to.id));
    await expect.poll(async () => (await elements(page)).filter(e => e.kind === 'connector').length).toBeGreaterThan(0);
  }
  const fx = pngFixture();
  await armPicture(page, fx);
  await page.getByTestId('insert-picture').click();
  await expect.poll(async () => (await elements(page)).filter(e => e.kind === 'image').length).toBe(1);
  const c0 = await centre(page, a.id);
  await page.mouse.click(c0.x, c0.y);
  await page.getByTestId('field-fill-colour').fill('#bfdbfe');
  await page.screenshot({ path: '../../docs/m1/evidence/I12/editor.png' });
});

test('real double-click text editing, keyboard nudges and duplicate', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 150, y: 120 }, 'a');
  const c = await centre(page, a.id);

  // Double-click opens maxGraph's in-place editor; typing and clicking away commits one revision.
  const r0 = await revision(page);
  await page.mouse.dblclick(c.x, c.y);
  await page.keyboard.type('Typed by hand');
  const empty = await ptToClient(page, { x: 700, y: 500 });
  await page.mouse.click(empty.x, empty.y);
  await expect.poll(async () => ((await byAlias(page, 'a')) as ShapeElement).text?.value).toBe('Typed by hand');
  expect(await revision(page)).toBe(r0 + 1);

  // Arrow keys nudge the selection by 1 pt, shift by 10 pt: each press is its own transaction.
  await page.mouse.click(c.x, c.y);
  const x0 = (await byAlias(page, 'a')).bounds.x;
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await byAlias(page, 'a')).bounds.x).toBeCloseTo(x0 + 1, 4);
  await page.keyboard.press('Shift+ArrowRight');
  await expect.poll(async () => (await byAlias(page, 'a')).bounds.x).toBeCloseTo(x0 + 11, 4);

  // Ctrl+D duplicates with a fresh identity and selects the copy.
  const before = (await elements(page)).length;
  await page.keyboard.press('Control+d');
  await expect.poll(async () => (await elements(page)).length).toBe(before + 1);
  const copy = (await elements(page)).find(e => e.alias === 'a-2')!;
  expect(copy).toBeDefined();
  expect(copy.id).not.toBe(a.id);
  expect(await selection(page)).toEqual([copy.id]);

  // Delete via the menu, then undo brings it back with the same identity.
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-delete').click();
  await expect.poll(async () => (await elements(page)).length).toBe(before);
  await page.getByTestId('tool-undo').click();
  await expect.poll(async () => (await elements(page)).some(e => e.id === copy.id)).toBe(true);
});
