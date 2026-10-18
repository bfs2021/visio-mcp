import { test, expect, type Page } from '@playwright/test';
import type { RenderResult } from '../../src/render/SnapshotRenderer';
import { armPicture, centre, drag, elements, openEditor, placeShape, pngFixture, revision, scale, sha256, snapshot } from './support';

type Envelope = { result?: any; error?: { code: string; message: string; details?: any } };
const call = (page: Page, method: string, params: Record<string, unknown>): Promise<Envelope> => page.evaluate(([m, p]) => (window as any).__editor.callTool(m, p), [method, params] as const);
const apply = (page: Page, operations: unknown[]) => page.evaluate(ops => (window as any).__editor.agentApply(ops), operations);

/** Decodes the image the tool returned and reads pixels from it, so the test judges real pixels, not metadata. */
async function pixels(page: Page, r: RenderResult, points: Array<[number, number]>): Promise<{ width: number; height: number; at: number[][]; dark: number; coloured: (rgb: [number, number, number], tol?: number) => Promise<number> }> {
  const out = await page.evaluate(async ([b64, mime, pts]) => {
    const img = new Image();
    img.src = `data:${mime};base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
    const g = c.getContext('2d')!; g.drawImage(img, 0, 0);
    const at = (pts as Array<[number, number]>).map(([x, y]) => Array.from(g.getImageData(Math.min(c.width - 1, Math.max(0, Math.round(x))), Math.min(c.height - 1, Math.max(0, Math.round(y))), 1, 1).data));
    const all = g.getImageData(0, 0, c.width, c.height).data;
    let dark = 0;
    for (let i = 0; i < all.length; i += 4) if (all[i]! < 90 && all[i + 1]! < 90 && all[i + 2]! < 90 && all[i + 3]! > 200) dark++;
    return { width: c.width, height: c.height, at, dark };
  }, [r.base64, r.mimeType, points] as const);
  return {
    ...out,
    coloured: (rgb, tol = 24) => page.evaluate(async ([b64, mime, want, t]) => {
      const img = new Image(); img.src = `data:${mime};base64,${b64}`; await img.decode();
      const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
      const g = c.getContext('2d')!; g.drawImage(img, 0, 0);
      const d = g.getImageData(0, 0, c.width, c.height).data; let n = 0;
      for (let i = 0; i < d.length; i += 4) if (Math.abs(d[i]! - want[0]) <= t && Math.abs(d[i + 1]! - want[1]) <= t && Math.abs(d[i + 2]! - want[2]) <= t && d[i + 3]! > 200) n++;
      return n;
    }, [r.base64, r.mimeType, rgb, tol] as const),
  };
}

const RED = { kind: 'solid', colour: '#ff0000', opacity: 1 };
const BLUE = { kind: 'solid', colour: '#0000ff', opacity: 1 };
const box = (x: number, y: number, w = 100, h = 60) => ({ x, y, width: w, height: h });
const rect = (id: string, b: object, fill: object = RED, extra: object = {}) => ({ op: 'createShape', id, bounds: b, style: { fill, stroke: { kind: 'none' } }, ...extra });
const U = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

test('a clean render shows committed shapes and no editor chrome, with the committed revision', async ({ page }) => {
  const errors = await openEditor(page);
  await apply(page, [rect(U(1), box(100, 100), RED, { alias: 'red' }), rect(U(2), box(400, 300), BLUE)]);
  // select a shape: the live canvas now shows handles, which must not appear in a render
  await page.evaluate(() => (window as any).__editor);
  const res = await call(page, 'doc.render', { scope: 'page' });
  expect(res.error).toBeUndefined();
  const r = res.result as RenderResult;
  expect(r.mimeType).toBe('image/png');
  expect(r.widthPx).toBe(1600);
  expect(r.revision).toBe(await revision(page));
  expect(r.mode).toBe('clean');

  const k = r.widthPx / r.cropPt.width;
  const px = await pixels(page, r, [[150 * k, 130 * k], [450 * k, 330 * k], [10, 10]]);
  expect(px.at[0]!.slice(0, 3)).toEqual([255, 0, 0]); // the red shape's centre
  expect(px.at[1]!.slice(0, 3)).toEqual([0, 0, 255]);
  expect(px.at[2]!.slice(0, 3)).toEqual([255, 255, 255]); // page background in the corner
  expect(await px.coloured([0, 255, 0], 40)).toBe(0); // no selection-handle green anywhere

  // selecting and rendering again gives the very same bytes: the render has no interest in selection
  const centre = await page.evaluate(id => (window as any).__editor.centre(id), U(1));
  await page.mouse.click(centre.x, centre.y);
  expect(await page.evaluate(() => (window as any).__editor.selection())).toEqual([U(1)]);
  const again = (await call(page, 'doc.render', { scope: 'page' })).result as RenderResult;
  expect(again.base64).toBe(r.base64);
  expect(errors.filter(e => !/Failed to load resource|favicon/.test(e))).toEqual([]);
});

test('debug mode adds ids and bounds over the picture; clean never does', async ({ page }) => {
  await openEditor(page);
  await apply(page, [rect(U(1), box(100, 100), { kind: 'solid', colour: '#dddddd', opacity: 1 }, { alias: 'gateway' })]);
  const clean = (await call(page, 'doc.render', { scope: 'page', mode: 'clean' })).result as RenderResult;
  const debug = (await call(page, 'doc.render', { scope: 'page', mode: 'debug' })).result as RenderResult;
  expect(debug.base64).not.toBe(clean.base64);
  expect(debug.mode).toBe('debug');
  expect(await (await pixels(page, clean, [])).coloured([225, 29, 72], 10)).toBe(0); // the first overlay colour does not occur in a clean render
  expect(await (await pixels(page, debug, [])).coloured([225, 29, 72], 10)).toBeGreaterThan(50); // the label chip and the dashed box do
});

test('the picture shows the revision that was committed when it was asked for, not the live one', async ({ page }) => {
  await openEditor(page);
  await apply(page, [rect(U(1), box(100, 100), RED)]);
  const asked = await revision(page);
  const pending = call(page, 'doc.render', { scope: 'page' }); // queued behind committed work, drawn after
  await apply(page, [{ op: 'set', target: U(1), patch: { style: { fill: BLUE } } }]); // a later commit while it renders
  const r = (await pending).result as RenderResult;
  expect(r.revision).toBe(asked);
  expect(await revision(page)).toBe(asked + 1);
  const k = r.widthPx / r.cropPt.width;
  expect((await pixels(page, r, [[150 * k, 130 * k]])).at[0]!.slice(0, 3)).toEqual([255, 0, 0]); // still red: that is what revision `asked` looked like
});

test('pictures and text are drawn, and a missing picture fails instead of leaving a blank', async ({ page }) => {
  await openEditor(page);
  const fx = pngFixture();
  const sha = await armPicture(page, fx);
  const assetId = `asset:${fx.name.replace(/\W+/g, '-')}~${sha}`;
  await apply(page, [
    { op: 'registerAsset', asset: { id: assetId, name: fx.name, mimeType: fx.mime, sha256: sha, tags: [], provenance: 'test' } },
    { op: 'createImage', id: U(1), assetId, bounds: box(100, 100, 128, 128) },
    { op: 'createText', id: U(2), bounds: box(400, 100, 300, 60), text: { value: 'Hello render', fontSizePt: 28, colour: '#000000' } },
  ]);
  const ok = await call(page, 'doc.render', { scope: 'region', elementIds: [U(1), U(2)], paddingPt: 10 });
  expect(ok.error, JSON.stringify(ok.error)).toBeUndefined();
  const r = ok.result as RenderResult;
  const px = await pixels(page, r, []);
  expect(px.dark).toBeGreaterThan(200); // glyphs were drawn (font ready) and the checker picture has dark squares
  const k = r.widthPx / r.cropPt.width;
  const imageCentre = (await pixels(page, r, [[(164 - r.cropPt.x) * k, (164 - r.cropPt.y) * k]])).at[0]!;
  expect(imageCentre[3]).toBe(255);

  // now the bytes go missing: the render must say so
  await page.unroute(`**/assets/${sha}`);
  await page.route(`**/assets/${sha}`, route => route.fulfill({ status: 404, body: '' }));
  const gone = await call(page, 'doc.render', { scope: 'page' });
  expect(gone.error?.code).toBe('not_found');
  expect(gone.error?.message).toMatch(/not available/);
  void sha256;
});

test('a region around a rotated group includes the group visual bounds, with padding', async ({ page }) => {
  await openEditor(page);
  await apply(page, [
    rect(U(1), box(100, 100)), rect(U(2), box(300, 100)),
    { op: 'rotate', target: U(2), angleDeg: 90 },
    { op: 'group', targets: [U(1), U(2)], id: U(9), alias: 'pair' },
  ]);
  // U(2) is 100 x 60 about (350,130): turned a quarter it covers x 320..380, y 80..180; the group runs x 100..380, y 80..180
  const res = await call(page, 'doc.render', { scope: 'region', elementIds: ['pair'], paddingPt: 10 });
  const r = res.result as RenderResult;
  expect(r.cropPt).toEqual({ x: 90, y: 70, width: 300, height: 120 });
  expect(r.widthPx / r.heightPx).toBeCloseTo(300 / 120, 1);
  const px = await pixels(page, r, []);
  expect(await px.coloured([255, 0, 0], 8)).toBeGreaterThan(1000);
  // the rotated shape's far corner is inside the crop: red reaches the right-hand side of the picture
  const k = r.widthPx / r.cropPt.width;
  expect((await pixels(page, r, [[(378 - r.cropPt.x) * k, (130 - r.cropPt.y) * k]])).at[0]!.slice(0, 3)).toEqual([255, 0, 0]);
});

test('jpeg uses the page background and pixel limits are enforced', async ({ page }) => {
  await openEditor(page);
  await apply(page, [{ op: 'setPage', patch: { background: { kind: 'solid', colour: '#ffcc00', opacity: 1 } } }]);
  const jpeg = (await call(page, 'doc.render', { scope: 'page', format: 'jpeg', maxWidth: 800 })).result as RenderResult;
  expect(jpeg.mimeType).toBe('image/jpeg');
  const c = (await pixels(page, jpeg, [[5, 5]])).at[0]!;
  expect(Math.abs(c[0]! - 255)).toBeLessThan(8); expect(Math.abs(c[1]! - 204)).toBeLessThan(10); expect(c[2]!).toBeLessThan(15);
  const png = (await call(page, 'doc.render', { scope: 'page', maxWidth: 800 })).result as RenderResult;
  expect((await pixels(page, png, [[5, 5]])).at[0]!.slice(0, 3)).toEqual([255, 204, 0]);

  const big = await call(page, 'doc.render', { scope: 'page', maxWidth: 8192, maxHeight: 8192 });
  expect(big.error?.code).toBe('limit_exceeded');
  const bad = await call(page, 'doc.render', { scope: 'region' });
  expect(bad.error?.code).toBe('invalid_request');
  expect((await snapshot(page)).document.pages[0]!.elements).toHaveLength(0);
});

test('layout inspection in the real editor measures real text and leaves the document alone', async ({ page }) => {
  await openEditor(page);
  await apply(page, [
    { op: 'createShape', id: U(1), alias: 'tight', bounds: box(100, 100, 70, 24), text: { value: 'A label that will not fit in here', fontSizePt: 14 } },
    { op: 'createShape', id: U(2), alias: 'roomy', bounds: box(300, 100, 240, 80), text: { value: 'Fits easily', fontSizePt: 14 } },
    { op: 'createShape', id: U(3), alias: 'off', bounds: box(800, 100) },
  ]);
  const before = JSON.stringify(await elements(page));
  const rev = await revision(page);
  const res = await call(page, 'doc.inspectLayout', {});
  expect(res.error).toBeUndefined();
  const issues = res.result.issues as Array<{ code: string; elementIds: string[]; pageId: string; bounds: unknown; evidence: any }>;
  const overflow = issues.filter(i => i.code === 'TEXT_OVERFLOW');
  expect(overflow.map(i => i.elementIds[0])).toEqual([U(1)]);
  expect(overflow[0]!.evidence.measured).toBe('estimated');
  expect(issues.find(i => i.code === 'OUTSIDE_PAGE')!.elementIds).toEqual([U(3)]);
  expect(res.result.notes.join(' ')).toMatch(/estimated/);
  expect(res.result.revision).toBe(rev);
  expect(JSON.stringify(await elements(page))).toBe(before);
  expect(await revision(page)).toBe(rev);
});

test('svg output is a standalone vector file with pictures inlined', async ({ page }) => {
  await openEditor(page);
  const fx = pngFixture();
  const sha = await armPicture(page, fx);
  const assetId = `asset:${fx.name.replace(/\W+/g, '-')}~${sha}`;
  await apply(page, [
    { op: 'registerAsset', asset: { id: assetId, name: fx.name, mimeType: fx.mime, sha256: sha, tags: [], provenance: 'test' } },
    { op: 'createImage', id: U(1), assetId, bounds: box(100, 100, 128, 128) },
    rect(U(2), box(300, 100), RED, { alias: 'r' }),
  ]);
  const res = await call(page, 'doc.render', { scope: 'page', format: 'svg' });
  expect(res.error, JSON.stringify(res.error)).toBeUndefined();
  const r = res.result as RenderResult;
  expect(r.mimeType).toBe('image/svg+xml');
  const text = Buffer.from(r.base64, 'base64').toString('utf8');
  expect(text.startsWith('<svg')).toBe(true);
  expect(text).toContain('data:image/png;base64,'); // the picture travels inside the file
  expect(text).not.toContain('/assets/'); // no reference to the application's own address
  expect(text.toLowerCase()).toContain('#ff0000');
  const parsed = await page.evaluate(t => { const d = new DOMParser().parseFromString(t, 'image/svg+xml'); return d.querySelector('parsererror') ? 'bad' : d.documentElement.tagName; }, text);
  expect(parsed).toBe('svg');
});

test('the export snapshot barrier gives committed geometry and routes even in the middle of a drag', async ({ page }) => {
  await openEditor(page);
  const a = await placeShape(page, 'shape-rectangle', { x: 100, y: 100 }, 'a');
  const b = await placeShape(page, 'shape-rectangle', { x: 400, y: 300 }, 'b');
  await apply(page, [{ op: 'createConnector', id: U(3), from: { elementId: a.id, glue: 'dynamic' }, to: { elementId: b.id, glue: 'dynamic' }, route: 'orthogonal' }]);
  const before = (await call(page, 'doc.exportSnapshot', {})).result;
  expect(before.projection.connectors[U(3)].routePoints.length).toBeGreaterThan(1);
  const centre = await page.evaluate(id => (window as any).__editor.centre(id), a.id);
  const c = { x: centre.x, y: centre.y };
  await page.mouse.click(c.x, c.y); // select it first, as a person would
  const k = await scale(page);
  await drag(page, c, { x: c.x + 80 * k, y: c.y + 80 * k }, { release: false }); // mid-drag, nothing committed
  expect(await page.evaluate(() => (window as any).__editor.gestureActive())).toBe(true);
  const during = (await call(page, 'doc.exportSnapshot', {})).result;
  expect(during.revision).toBe(before.revision);
  expect(JSON.stringify(during.document)).toBe(JSON.stringify(before.document)); // committed geometry, not the preview
  expect(JSON.stringify(during.projection.connectors)).toBe(JSON.stringify(before.projection.connectors));
  expect(during.projection.revision).toBe(during.revision); // the sidecar is the same revision as the document
  await page.mouse.up();
});

test('an approved picture can be rasterised for formats that cannot hold the original', async ({ page }) => {
  await openEditor(page);
  const fx = pngFixture();
  const sha = await armPicture(page, fx);
  const ok = await call(page, 'assets.rasterise', { sha256: sha, maxPixels: 256 });
  expect(ok.error, JSON.stringify(ok.error)).toBeUndefined();
  expect(ok.result.widthPx).toBeGreaterThan(0);
  const head = Buffer.from(ok.result.base64, 'base64').subarray(0, 4);
  expect([...head]).toEqual([0x89, 0x50, 0x4e, 0x47]);
  await page.unroute(`**/assets/${sha}`);
  await page.route(`**/assets/${sha}`, route => route.fulfill({ status: 404, body: '' }));
  expect((await call(page, 'assets.rasterise', { sha256: sha })).error?.code).toBe('not_found');
  expect((await call(page, 'assets.rasterise', { sha256: 'nope' })).error?.code).toBe('invalid_request');
});

test('a connector to a rotated non-rectangular shape ends on its outline, not on its rotated frame', async ({ page }) => {
  await openEditor(page);
  const para = { x: 100, y: 200, width: 160, height: 100 };
  await apply(page, [
    { op: 'createShape', id: U(11), bounds: para, rotationDeg: 15, geometry: { preset: 'parallelogram' }, style: { fill: RED, stroke: { kind: 'none' } } },
    { op: 'createShape', id: U(12), bounds: box(500, 220), style: { fill: BLUE, stroke: { kind: 'none' } } },
    { op: 'createConnector', id: U(13), from: { elementId: U(11), glue: 'dynamic' }, to: { elementId: U(12), glue: 'dynamic' }, route: 'orthogonal' },
  ]);
  const shot = (await call(page, 'doc.exportSnapshot', {})).result;
  const start = shot.projection.connectors[U(13)].routePoints[0];
  // into the shape's own frame: undo the rotation about its centre, then measure the distance to the nearest edge of the parallelogram
  const cx = para.x + para.width / 2, cy = para.y + para.height / 2, a = -15 * Math.PI / 180;
  const dx = start.x - cx, dy = start.y - cy;
  const p = { x: cx + dx * Math.cos(a) - dy * Math.sin(a), y: cy + dx * Math.sin(a) + dy * Math.cos(a) };
  const poly = [[0.2, 0], [1, 0], [0.8, 1], [0, 1]].map(([u, v]) => ({ x: para.x + u! * para.width, y: para.y + v! * para.height }));
  const dist = Math.min(...poly.map((s, i) => {
    const e = poly[(i + 1) % poly.length]!, vx = e.x - s.x, vy = e.y - s.y;
    const t = Math.max(0, Math.min(1, ((p.x - s.x) * vx + (p.y - s.y) * vy) / (vx * vx + vy * vy)));
    return Math.hypot(p.x - (s.x + t * vx), p.y - (s.y + t * vy));
  }));
  expect(dist).toBeLessThan(1);
});

test('an orthogonal connector leaves and enters along its ports and passes through its waypoints', async ({ page }) => {
  await openEditor(page);
  await apply(page, [
    { op: 'createShape', id: U(21), bounds: box(100, 100), style: { fill: RED, stroke: { kind: 'none' } } },
    { op: 'createShape', id: U(22), bounds: box(400, 300), style: { fill: BLUE, stroke: { kind: 'none' } } },
    { op: 'createShape', id: U(24), bounds: box(100, 500), style: { fill: BLUE, stroke: { kind: 'none' } } },
    { op: 'createConnector', id: U(23), from: { elementId: U(21), port: 'east', glue: 'static' }, to: { elementId: U(22), port: 'north', glue: 'static' }, route: 'orthogonal' },
    { op: 'createConnector', id: U(25), from: { elementId: U(21), port: 'west', glue: 'static' }, to: { elementId: U(24), port: 'west', glue: 'static' }, route: 'orthogonal', waypoints: [{ x: 40, y: 130 }, { x: 40, y: 530 }] },
  ]);
  const routes = (await call(page, 'doc.exportSnapshot', {})).result.projection.connectors;
  const l = routes[U(23)].routePoints;
  expect(l).toHaveLength(3); // east port, one elbow, north port
  expect(l[1].y).toBeCloseTo(l[0].y, 3);
  expect(l[1].x).toBeCloseTo(l[2].x, 3);
  expect(l[2].y).toBeGreaterThan(l[1].y); // arrives going down into the north port
  const w = routes[U(25)].routePoints;
  expect(w.some((p: { x: number; y: number }) => Math.abs(p.x - 40) < 0.01 && Math.abs(p.y - 130) < 0.01)).toBe(true);
  expect(w.some((p: { x: number; y: number }) => Math.abs(p.x - 40) < 0.01 && Math.abs(p.y - 530) < 0.01)).toBe(true);
  expect(w.every((p: { x: number; y: number }, i: number) => i === 0 || Math.abs(p.x - w[i - 1].x) < 0.01 || Math.abs(p.y - w[i - 1].y) < 0.01)).toBe(true);
});

test('the inspector sets arrowhead sizes on a selected connector, and the render shows the larger head', async ({ page }) => {
  await openEditor(page);
  await apply(page, [
    { op: 'createConnector', id: U(31), from: { glue: 'none', point: { x: 100, y: 200 } }, to: { glue: 'none', point: { x: 400, y: 200 } }, route: 'straight', style: { widthPt: 2.25, endArrow: 'block' } },
  ]);
  const c = await centre(page, U(31));
  await page.mouse.click(c.x, c.y);
  await expect(page.getByTestId('field-end-arrow-size')).toHaveValue('medium');
  const sizeOf = async () => {
    const r = (await call(page, 'doc.render', { scope: 'page' })).result as RenderResult;
    return (await pixels(page, r, [])).dark;
  };
  const medium = await sizeOf();
  await page.getByTestId('field-end-arrow-size').selectOption('large');
  const el = (await elements(page)).find(e => e.id === U(31)) as any;
  expect(el.style.endArrowSize).toBe('large');
  const large = await sizeOf();
  expect(large).toBeGreaterThan(medium);
  await page.getByTestId('field-end-arrow-size').selectOption('small');
  expect(await sizeOf()).toBeLessThan(medium);
});
