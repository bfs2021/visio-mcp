import { test, expect, type Page } from '@playwright/test';
import { openEditor, snapshot, centre } from './support';

const stops = (...s: Array<[number, string, number]>) => s.map(([offset, colour, opacity]) => ({ offset, colour, opacity }));
const paint = (angleDeg: number, ...s: Array<[number, string, number]>) => ({ kind: 'linear-gradient', angleDeg, stops: stops(...s) });

/** The colour of one pixel of a rendered page (what an agent's render_page returns). */
async function pixel(page: Page, base64: string, x: number, y: number): Promise<[number, number, number, number]> {
  return page.evaluate(async ([b64, px, py]) => {
    const blob = await (await fetch(`data:image/png;base64,${b64}`)).blob();
    const bmp = await createImageBitmap(blob);
    const c = document.createElement('canvas'); c.width = bmp.width; c.height = bmp.height;
    const g = c.getContext('2d')!; g.drawImage(bmp, 0, 0);
    return Array.from(g.getImageData(Math.round(px as number), Math.round(py as number), 1, 1).data) as [number, number, number, number];
  }, [base64, x, y] as const);
}

test('gradient fills are drawn at their angle with every stop, in the editor and in a render', async ({ page }) => {
  const errors = await openEditor(page);
  const ops = [
    { op: 'createShape', alias: 'across', bounds: { x: 40, y: 40, width: 200, height: 60 }, style: { fill: paint(0, [0, '#ffffff', 1], [1, '#0000ff', 1]) } },
    { op: 'createShape', alias: 'down', bounds: { x: 280, y: 40, width: 200, height: 60 }, style: { fill: paint(90, [0, '#ffffff', 1], [1, '#ff0000', 1]) } },
    { op: 'createShape', alias: 'diagonal', bounds: { x: 40, y: 140, width: 200, height: 60 }, style: { fill: paint(45, [0, '#ffff00', 1], [0.5, '#00aa00', 1], [1, '#000066', 1]) } },
    { op: 'createShape', alias: 'seethrough', bounds: { x: 280, y: 140, width: 200, height: 60 }, style: { fill: paint(0, [0, '#000000', 1], [1, '#000000', 0]) } },
  ];
  expect((await page.evaluate(o => (window as any).__editor.agentApply(o), ops)).ok).toBe(true);

  // the SVG gradients: a vector in the unit square that is correct for the shape's own proportions
  const gradients = await page.evaluate(() => [...document.querySelectorAll('linearGradient[id^="mx-gradient-d-"]')].map(g => ({
    x1: g.getAttribute('x1'), y1: g.getAttribute('y1'), x2: g.getAttribute('x2'), y2: g.getAttribute('y2'),
    stops: [...g.querySelectorAll('stop')].map(s => ({ offset: s.getAttribute('offset'), style: s.getAttribute('style') })),
  })));
  expect(gradients).toHaveLength(4);
  const across = gradients.find(g => g.stops.length === 2 && g.x1 === '0%' && g.y1 === '50%')!;
  expect([across.x2, across.y2]).toEqual(['100%', '50%']);
  const down = gradients.find(g => g.x1 === '50%' && g.y1 === '0%')!;
  expect([down.x2, down.y2]).toEqual(['50%', '100%']);
  const diagonal = gradients.find(g => g.stops.length === 3)!;
  expect(diagonal.stops.map(s => s.offset)).toEqual(['0%', '50%', '100%']);
  expect(parseFloat(diagonal.x1!)).toBeCloseTo(-9.6, 0); // 45 degrees on a 200 x 60 shape runs past the box in x so the corners get the end colours
  expect(parseFloat(diagonal.y1!)).toBeCloseTo(32.1, 0);
  const see = gradients.find(g => g.stops.some(s => /stop-opacity:0/.test(s.style ?? '')))!;
  expect(see.stops[1]!.style).toContain('stop-opacity:0');

  // the render an agent gets shows the same blends
  const els = (await snapshot(page)).document.pages[0]!.elements;
  const pageId = (await snapshot(page)).document.pages[0]!.id;
  const rendered = await page.evaluate(id => (window as any).__editor.callTool('doc.render', { scope: 'page', pageId: id, format: 'png', maxWidth: 842, maxHeight: 596 }), pageId);
  expect(rendered.error).toBeUndefined();
  const { base64, widthPx, heightPx } = rendered.result as { base64: string; widthPx: number; heightPx: number };
  const k = widthPx / 841.89;
  const at = (alias: string, fx: number, fy: number) => { const e = els.find(x => x.alias === alias)!; return [e.bounds.x + e.bounds.width * fx, e.bounds.y + e.bounds.height * fy].map(v => v * k) as [number, number]; };
  const sample = (alias: string, fx: number, fy: number) => { const [x, y] = at(alias, fx, fy); return pixel(page, base64, x, y); };
  const left = await sample('across', 0.05, 0.5), right = await sample('across', 0.95, 0.5);
  expect(left[0]).toBeGreaterThan(200);                // near white on the left
  expect(right[0]).toBeLessThan(60);                   // near blue on the right
  expect(right[2]).toBeGreaterThan(200);
  const top = await sample('down', 0.5, 0.05), bottom = await sample('down', 0.5, 0.95);
  expect(top[1]).toBeGreaterThan(200);                 // white at the top
  expect(bottom[1]).toBeLessThan(60);                  // red at the bottom
  const topLeft = await sample('diagonal', 0.02, 0.1), middle = await sample('diagonal', 0.5, 0.5), bottomRight = await sample('diagonal', 0.98, 0.9);
  expect(topLeft[0]).toBeGreaterThan(200);             // yellow start
  expect(middle[1]).toBeGreaterThan(120);              // green middle
  expect(middle[0]).toBeLessThan(80);
  expect(bottomRight[2]).toBeGreaterThan(60);          // dark blue end
  expect(bottomRight[0]).toBeLessThan(40);
  const clear = await sample('seethrough', 0.95, 0.5);
  expect(clear[0]).toBeGreaterThan(230);               // fades to the white page
  await page.screenshot({ path: '../../artifacts/r11-gradients/editor.png' });
  expect(errors).toEqual([]);
  void centre;
});

test('the inspector turns a solid fill into a gradient and edits its colours and angle', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'createShape', alias: 'box', bounds: { x: 100, y: 100, width: 160, height: 80 }, style: { fill: { kind: 'solid', colour: '#2563eb', opacity: 1 } } }]));
  const box = (await snapshot(page)).document.pages[0]!.elements[0]!;
  const c = await centre(page, box.id);
  await page.mouse.click(c.x, c.y);
  const fillOf = async () => ((await snapshot(page)).document.pages[0]!.elements[0] as any).style.fill;
  await page.getByTestId('field-fill-to-gradient').click();
  expect(await fillOf()).toMatchObject({ kind: 'linear-gradient', angleDeg: 90, stops: [{ offset: 0, colour: '#2563eb' }, { offset: 1, colour: '#ffffff' }] });
  await page.getByTestId('field-fill-angle').fill('45');
  await page.getByTestId('field-fill-angle').press('Enter');
  await expect.poll(async () => (await fillOf()).angleDeg).toBe(45);
  await page.getByTestId('field-fill-stop-last').fill('#ff8800');
  await expect.poll(async () => (await fillOf()).stops[1].colour).toBe('#ff8800');
  await page.getByTestId('field-fill-kind').selectOption('solid');
  expect(await fillOf()).toEqual({ kind: 'solid', colour: '#2563eb', opacity: 1 });
  // a unit-undo returns the gradient
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await fillOf()).kind).toBe('linear-gradient');
});
