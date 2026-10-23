import { test, expect } from '@playwright/test';
import { openEditor, snapshot } from './support';

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
