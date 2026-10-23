import { test, expect } from '@playwright/test';
import { centre, openEditor, ptToClient, snapshot } from './support';

const element = async (page: import('@playwright/test').Page, id: string) =>
  (await snapshot(page)).document.pages[0]!.elements.find(el => el.id === id) as any;

async function selectAt(page: import('@playwright/test').Page, point: { x: number; y: number }) {
  const client = await ptToClient(page, point);
  await page.mouse.click(client.x, client.y);
}

test('MCP layout inspection surfaces route policy findings from the committed projection', async ({ page }) => {
  await openEditor(page);
  const ids = {
    a: '00000000-0000-4000-8000-000000000a01', b: '00000000-0000-4000-8000-000000000a02',
    soft: '00000000-0000-4000-8000-000000000a03', link: '00000000-0000-4000-8000-000000000a04',
  };
  const applied = await page.evaluate(ids => (window as any).__editor.agentApply([
    { op: 'createShape', id: ids.a, bounds: { x: 20, y: 480, width: 60, height: 40 } },
    { op: 'createShape', id: ids.b, bounds: { x: 740, y: 480, width: 60, height: 40 } },
    { op: 'createShape', id: ids.soft, bounds: { x: 0, y: 0, width: 841, height: 595 } },
    { op: 'createConnector', id: ids.link, from: { elementId: ids.a, glue: 'static', port: 'east' }, to: { elementId: ids.b, glue: 'static', port: 'west' }, route: 'orthogonal' },
    { op: 'set', target: ids.soft, patch: { routingPolicy: { obstacle: 'avoid' } } },
  ]), ids);
  expect(applied.ok).toBe(true);
  const committed = await page.evaluate(() => (window as any).__editor.exportSnapshot());
  expect(committed.projection.connectors[ids.link].routeFindings).toContainEqual(expect.objectContaining({ code: 'ROUTING_AVOIDANCE_VIOLATION', blockerIds: [ids.soft] }));
  const inspected = await page.evaluate(() => (window as any).__editor.callTool('doc.inspectLayout', {}));
  const report = inspected.result ?? inspected;
  expect(report.issues).toContainEqual(expect.objectContaining({ code: 'ROUTING_AVOIDANCE_VIOLATION', elementIds: [ids.link, ids.soft] }));
  expect(report.issues.filter((issue: any) => issue.code === 'CONNECTOR_CROSSES_SHAPE' && issue.elementIds.includes(ids.soft))).toHaveLength(0);
  expect((await snapshot(page)).revision).toBe(committed.revision);
});

test('inspector_sets_and_clears_obstacle_policy', async ({ page }) => {
  await openEditor(page);
  const id = '00000000-0000-4000-8000-000000000b01';
  expect((await page.evaluate(id => (window as any).__editor.agentApply([
    { op: 'createShape', id, bounds: { x: 120, y: 130, width: 100, height: 60 }, text: { value: 'Database' } },
  ]), id)).ok).toBe(true);
  await selectAt(page, { x: 170, y: 160 });

  await page.getByTestId('field-obstacle-policy').selectOption('forbidden');
  await page.getByTestId('field-route-clearance').fill('16');
  await page.getByTestId('field-route-clearance').press('Enter');
  await page.getByTestId('field-text-obstacle-policy').selectOption('avoid');
  await expect.poll(async () => {
    const policy = (await element(page, id)).routingPolicy;
    return [policy?.obstacle, policy?.clearancePt, policy?.text];
  }).toEqual(['forbidden', 16, 'avoid']);

  await selectAt(page, { x: 700, y: 500 });
  await selectAt(page, { x: 170, y: 160 });
  await expect(page.getByTestId('field-obstacle-policy')).toHaveValue('forbidden');
  await expect(page.getByTestId('field-route-clearance')).toHaveValue('16');
  await expect(page.getByTestId('field-text-obstacle-policy')).toHaveValue('avoid');
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await element(page, id)).routingPolicy).toBeUndefined();
});

test('inspector_sets_dynamic_endpoint_preferred_side', async ({ page }) => {
  await openEditor(page);
  const ids = {
    a: '00000000-0000-4000-8000-000000000b11', b: '00000000-0000-4000-8000-000000000b12',
    wall: '00000000-0000-4000-8000-000000000b13', link: '00000000-0000-4000-8000-000000000b14',
  };
  expect((await page.evaluate(ids => (window as any).__editor.agentApply([
    { op: 'createShape', id: ids.a, bounds: { x: 100, y: 100, width: 100, height: 60 }, text: { value: 'A' } },
    { op: 'createShape', id: ids.b, bounds: { x: 100, y: 500, width: 100, height: 60 }, text: { value: 'B' } },
    { op: 'createShape', id: ids.wall, bounds: { x: 210, y: 80, width: 40, height: 160 } },
    { op: 'createConnector', id: ids.link, from: { elementId: ids.a, glue: 'dynamic' }, to: { elementId: ids.b, glue: 'dynamic' }, route: 'orthogonal' },
  ]), ids)).ok).toBe(true);
  expect((await page.evaluate(wall => (window as any).__editor.agentApply([
    { op: 'set', target: wall, patch: { routingPolicy: { obstacle: 'forbidden' } } },
  ]), ids.wall)).ok).toBe(true);
  const mid = await centre(page, ids.link);
  await page.mouse.click(mid.x, mid.y);

  await page.getByTestId('field-from-preferred-side').selectOption('south');
  await page.getByTestId('field-to-preferred-side').selectOption('north');
  await expect.poll(async () => {
    const current = await element(page, ids.link);
    return [current.from.preferredSide, current.to.preferredSide];
  }).toEqual(['south', 'north']);

  await selectAt(page, { x: 700, y: 500 });
  const updatedMid = await centre(page, ids.link);
  await page.mouse.click(updatedMid.x, updatedMid.y);
  await expect(page.getByTestId('field-from-preferred-side')).toHaveValue('south');
  await page.getByTestId('field-from-preferred-side').selectOption('east');
  await expect(page.getByTestId('route-side-relaxed')).toContainText('preferred endpoint side had no clear route');
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
  await expect.poll(async () => {
    const current = await element(page, ids.link);
    return [current.from.preferredSide, current.to.preferredSide];
  }).toEqual([undefined, undefined]);
});
