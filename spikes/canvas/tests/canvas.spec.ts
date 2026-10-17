import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { ProbeSceneDto, GestureEvent } from '../src/projection';

const pngMagic = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const evidenceDir = fileURLToPath(new URL('../../../docs/m0/evidence/G2/', import.meta.url));

// Test-support surface exposed by src/main.ts (not part of the DTO contract).
type Pt = { x: number; y: number };
type Probe = {
  fixture(): ProbeSceneDto;
  project(s: ProbeSceneDto): void;
  readProbeState(): ProbeSceneDto;
  renderProbe(): Promise<Uint8Array>;
  gestures(): GestureEvent[];
  clearGestures(): void;
  centre(id: string): Pt;
  handle(id: string, which: 'rotate' | 'se' | 'bend'): Pt;
  tainted(): Promise<{ png: boolean; svg: boolean }>;
  zoom(f: number): void;
  textEdit(id: string, text: string | null): void;
  timing(): Record<string, number>;
};
declare global { interface Window { probe: Probe } }

const state = (p: Page) => p.evaluate(() => window.probe.readProbeState());
const pt = (p: Page, fn: 'centre' | 'handle', ...a: string[]) =>
  p.evaluate(([f, args]) => (window.probe as any)[f](...args), [fn, a] as const) as Promise<Pt>;
const gestures = (p: Page) => p.evaluate(() => window.probe.gestures());
const clear = (p: Page) => p.evaluate(() => window.probe.clearGestures());

async function drag(p: Page, from: Pt, to: Pt, release = true) {
  await p.mouse.move(from.x, from.y);
  await p.mouse.down();
  await p.mouse.move((from.x + to.x) / 2, (from.y + to.y) / 2, { steps: 4 });
  await p.mouse.move(to.x, to.y, { steps: 4 });
  if (release) await p.mouse.up();
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
  await page.waitForFunction(() => !!(window as any).probe);
  await page.evaluate(() => window.probe.project(window.probe.fixture()));
});

test('move_resize_rotate_glue_and_picture', async ({ page }) => {
  const before = await state(page);

  // Move the rectangle: connector stays glued to the same two element IDs.
  const c = await pt(page, 'centre', 'rect');
  await page.mouse.click(c.x, c.y);
  await drag(page, c, { x: c.x + 40, y: c.y + 60 });
  const after = await state(page);
  expect(after.revision).toBe(before.revision + 1);
  expect(after.connector.from.elementId).toBe(before.connector.from.elementId);
  expect(after.connector.to.elementId).toBe(before.connector.to.elementId);
  expect(after.rect.x).toBeCloseTo(before.rect.x + 40, 0);
  expect(after.rect.y).toBeCloseTo(before.rect.y + 60, 0);
  expect(after.ellipse).toEqual(before.ellipse); // untouched element is unchanged

  // Resize from the south-east handle.
  const se = await pt(page, 'handle', 'rect', 'se');
  await drag(page, se, { x: se.x + 30, y: se.y + 20 });
  const afterResize = await state(page);
  expect(afterResize.revision).toBe(after.revision + 1);
  expect(afterResize.rect.w).toBeCloseTo(after.rect.w + 30, 0);
  expect(afterResize.rect.h).toBeCloseTo(after.rect.h + 20, 0);
  expect(afterResize.ellipse).toEqual(before.ellipse);

  // Rotate by dragging the rotation handle round to the right of the centre.
  const rc = await pt(page, 'centre', 'rect');
  const rh = await pt(page, 'handle', 'rect', 'rotate');
  await drag(page, rh, { x: rc.x + 120, y: rc.y });
  const afterRot = await state(page);
  expect(afterRot.revision).toBe(afterResize.revision + 1);
  expect(afterRot.rect.angle).toBeCloseTo(90, -1);
  expect(afterRot.rect.w).toBeCloseTo(afterResize.rect.w, 0); // rotation does not change the unrotated size
  expect(afterRot.connector.from.elementId).toBe(before.connector.from.elementId);

  // Picture: bytes round-trip unchanged and the offscreen render is a PNG.
  expect(Array.from(after.picture.bytes)).toEqual(Array.from(before.picture.bytes));
  expect(after.picture.mime).toBe('image/png');
  const png = Uint8Array.from(await page.evaluate(async () => Array.from(await window.probe.renderProbe())));
  expect(Array.from(png.subarray(0, 8))).toEqual(pngMagic);

  // Evidence.
  mkdirSync(evidenceDir, { recursive: true });
  writeFileSync(`${evidenceDir}canvas.png`, png);
  writeFileSync(`${evidenceDir}canonical-state.json`,
    JSON.stringify({ ...afterRot, picture: { ...afterRot.picture, bytes: `<${afterRot.picture.bytes.length} bytes>` }, vector: { ...afterRot.vector, bytes: `<${afterRot.vector.bytes.length} bytes>` } }, null, 2));
  writeFileSync(`${evidenceDir}timing.json`, JSON.stringify(await page.evaluate(() => window.probe.timing()), null, 2));
  await page.screenshot({ path: `${evidenceDir}viewport.png` });
});

test('gesture_lifecycle_observable_and_cancellable', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  type Plan = () => Promise<{ from: Pt; to: Pt }>;
  const plans: Array<[string, string, Plan]> = [
    ['move', 'ellipse', async () => { const c = await pt(page, 'centre', 'ellipse'); return { from: c, to: { x: c.x + 50, y: c.y + 30 } }; }],
    ['resize', 'ellipse', async () => { const h = await pt(page, 'handle', 'ellipse', 'se'); return { from: h, to: { x: h.x + 25, y: h.y + 25 } }; }],
    ['rotate', 'ellipse', async () => { const h = await pt(page, 'handle', 'ellipse', 'rotate'); const c = await pt(page, 'centre', 'ellipse'); return { from: h, to: { x: c.x + 90, y: c.y } }; }],
    ['bend', 'connector', async () => { const h = await pt(page, 'handle', 'connector', 'bend'); return { from: h, to: { x: h.x, y: h.y - 40 } }; }],
  ];

  for (const [kind, owner, plan] of plans) {
    const c = await pt(page, 'centre', owner);
    await page.mouse.click(c.x, c.y);
    await clear(page);

    // Commit.
    const snap0 = await state(page);
    const p1 = await plan();
    await drag(page, p1.from, p1.to);
    let g = await gestures(page);
    expect(g.map(e => `${e.kind}:${e.phase}`), `${kind} commit`).toEqual([`${kind}:begin`, `${kind}:commit`]);
    expect(g[0].kind).toBe(kind);
    expect((await state(page)).revision, `${kind} revision`).toBe(snap0.revision + 1);

    // Cancel with Escape mid-drag: state is restored exactly and a cancel event is emitted.
    await clear(page);
    const snap1 = await state(page);
    const c2 = await pt(page, 'centre', owner);
    await page.mouse.click(c2.x, c2.y);
    const p2 = await plan();
    await drag(page, p2.from, p2.to, false);
    await page.keyboard.press('Escape');
    await page.mouse.up();
    g = await gestures(page);
    expect(g.map(e => `${e.kind}:${e.phase}`), `${kind} cancel`).toEqual([`${kind}:begin`, `${kind}:cancel`]);
    expect(await state(page), `${kind} restored`).toEqual(snap1);
  }

  // In-place text editing: begin/commit and begin/cancel.
  await clear(page);
  await page.evaluate(() => window.probe.textEdit('rect', 'edited'));
  let g = await gestures(page);
  expect(g.map(e => `${e.kind}:${e.phase}`)).toEqual(['text:begin', 'text:commit']);
  expect((await state(page)).rect.text).toBe('edited');
  await clear(page);
  await page.evaluate(() => window.probe.textEdit('rect', null)); // null = cancel
  g = await gestures(page);
  expect(g.map(e => `${e.kind}:${e.phase}`)).toEqual(['text:begin', 'text:cancel']);
  expect((await state(page)).rect.text).toBe('edited');

  // Selection and zoom must not activate the gesture hook.
  await clear(page);
  const c = await pt(page, 'centre', 'rect');
  await page.mouse.click(c.x, c.y);
  await page.evaluate(() => window.probe.zoom(1.5));
  await page.evaluate(() => window.probe.zoom(1));
  expect(await gestures(page)).toEqual([]);
  expect(errors).toEqual([]); // includes Escape during rotate, which throws in unpatched maxGraph 0.24.0
});

test('export_is_offscreen_and_untainted', async ({ page }) => {
  // Zoom in so most of the scene is outside the viewport, then render: output must still cover the whole scene.
  await page.evaluate(() => window.probe.zoom(4));
  const { head, w, h } = await page.evaluate(async () => {
    const b = await window.probe.renderProbe();
    const img = await createImageBitmap(new Blob([b as BlobPart], { type: 'image/png' }));
    return { head: Array.from(b.subarray(0, 8)), w: img.width, h: img.height };
  });
  expect(head).toEqual(pngMagic);
  expect(w).toBeGreaterThan(200);
  expect(h).toBeGreaterThan(100);
  expect(w).toBeLessThan(2000); // sized by the scene bounds, not by the zoomed viewport

  // A PNG picture and an SVG vector asset must both export without tainting the canvas.
  expect(await page.evaluate(() => window.probe.tainted())).toEqual({ png: false, svg: false });
});
