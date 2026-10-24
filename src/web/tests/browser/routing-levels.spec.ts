import { test, expect } from '@playwright/test';
import { centre, openEditor } from './support';

// R23: a shape sent to a lower level than a connector is behind it, so the route runs over it instead of around it.
test('sending a frame back a level from Arrange straightens the connector over it, and undo restores the detour', async ({ page }) => {
  await openEditor(page);
  const ids = {
    a: '00000000-0000-4000-8000-000000000c01', b: '00000000-0000-4000-8000-000000000c02',
    frame: '00000000-0000-4000-8000-000000000c03', link: '00000000-0000-4000-8000-000000000c04',
  };
  expect((await page.evaluate(ids => (window as any).__editor.agentApply([
    { op: 'createShape', id: ids.a, bounds: { x: 60, y: 280, width: 60, height: 40 } },
    { op: 'createShape', id: ids.b, bounds: { x: 700, y: 280, width: 60, height: 40 } },
    { op: 'createShape', id: ids.frame, bounds: { x: 300, y: 200, width: 200, height: 200 } },
    { op: 'createConnector', id: ids.link, from: { elementId: ids.a, glue: 'static', port: 'east' }, to: { elementId: ids.b, glue: 'static', port: 'west' }, route: 'orthogonal' },
  ]), ids)).ok).toBe(true);
  const route = async () => (await page.evaluate(() => (window as any).__editor.exportSnapshot())).projection.connectors[ids.link].routePoints as Array<{ x: number; y: number }>;
  const straight = async () => (await route()).every(p => p.y === 300);
  await expect.poll(straight).toBe(false); // same level: the frame is in the way

  const at = await centre(page, ids.frame);
  await page.mouse.click(at.x, at.y);
  await page.getByTestId('arrange-level-input').fill('-1');
  await page.getByTestId('arrange-level-set').click();
  await expect.poll(straight).toBe(true);
  expect(await route()).toEqual([{ x: 120, y: 300 }, { x: 700, y: 300 }]);

  const inspected = await page.evaluate(() => (window as any).__editor.callTool('doc.inspectLayout', {}));
  const issues = (inspected.result ?? inspected).issues;
  expect(issues).toContainEqual(expect.objectContaining({ code: 'CONNECTOR_PASSES_OVER_SHAPE', elementIds: [ids.link, ids.frame], evidence: expect.objectContaining({ rule: 'lower_level', connectorLevel: 0 }) }));
  expect(issues.filter((i: any) => i.code === 'CONNECTOR_CROSSES_SHAPE')).toHaveLength(0);

  await page.keyboard.press('Control+z');
  await expect.poll(straight).toBe(false);
});
