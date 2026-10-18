import { expect, it } from 'vitest';
import { iconCatalog, searchIcons, iconOperations } from '../src/icons/catalog';
import { parsePath } from '../src/geometry/svgPath';
import { createTestEngine, element } from './support/engine';
const search = (query: string) => { const r = searchIcons(query); if (!r.ok) throw new Error(r.error.message); return r.value; };

it('finds versioned icons by name and semantic tags with deterministic bounded results', () => {
  expect(search('docker').icons.map(i => i.id)).toContain('lucide:container');
  expect(search('repository').icons.length).toBeGreaterThan(1);
  expect(search('git branch').icons[0]?.id).toBe('lucide:git-branch');
  expect(searchIcons('', 1000).ok).toBe(false);
  expect(search('unknown thing').icons).toEqual([]);
  expect(new Set(iconCatalog.icons.map(i => i.id)).size).toBe(26);
  for (const icon of iconCatalog.icons) expect(parsePath(icon.path).ok, icon.id).toBe(true);
});

it('places an icon as one editable canonical shape with pinned metadata and supports undo', async () => {
  const t = createTestEngine(); const id = '00000000-0000-4000-8000-000000009001';
  const prepared = iconOperations({ iconId: 'lucide:git-branch', pageId: t.ids.page, elementId: id, x: 72, y: 80, sizePt: 24, colour: '#16A34A', strokeWidthPt: 1.5 });
  expect(prepared.ok).toBe(true); if (!prepared.ok) return;
  const before = t.store.snapshot();
  expect((await t.engine.execute(t.request(prepared.value.operations), 'mcp')).ok).toBe(true);
  const shape = element(t.store.snapshot(), id);
  expect(shape.kind).toBe('shape');
  expect(shape.metadata['icon.id']).toBe('lucide:git-branch');
  expect(shape.metadata['icon.catalogVersion']).toBe(iconCatalog.version);
  expect((shape as any).geometry.svgPath).toMatch(/^M /);
  expect((shape as any).style.stroke.colour).toBe('#16A34A');
  await t.engine.undo({ ...t.scope(), transactionId: '00000000-0000-4000-8000-000000009002', baseRevision: 1 });
  expect(t.store.snapshot().document.pages).toEqual(before.document.pages);
});

it('rejects unknown icons, stale catalogue versions and invalid placement settings', () => {
  for (const params of [{ iconId: 'missing' }, { iconId: 'lucide:container', catalogVersion: 'old' }, { iconId: 'lucide:container', sizePt: NaN }, { iconId: 'lucide:container', colour: 'url(http://example.com)' }, { iconId: 'lucide:container', strokeWidthPt: -1 }])
    expect(iconOperations({ pageId: '00000000-0000-4000-8000-000000000100', ...params }).ok).toBe(false);
});
