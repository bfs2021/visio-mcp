import { describe, expect, it } from 'vitest';
import type { ConnectorElement, ImageElement } from '../src/contracts/generated';
import { canonicalSerialize } from '../src/model/canonical';
import { createTestEngine, exactElementHashes } from './support/engine';
import { byId, els, group, id, refused, run, shape } from './support/ops';

type T = ReturnType<typeof createTestEngine>;
const s = (t: T, n: number) => t.ids.shapes[n]!;
const PAGE2 = id(800), PAGE3 = id(802), L2 = id(810);
const doc = (t: T) => t.store.snapshot().document;
/** The document without its revision counter, for before/after comparisons. */
const body = (t: T) => canonicalSerialize({ ...doc(t), revision: 0 });

async function undo(t: T) {
  const r = await t.engine.undo({ ...t.scope(), transactionId: id(7000 + t.engine.revision), baseRevision: t.engine.revision });
  expect(r.ok).toBe(true);
}
async function redo(t: T) {
  const r = await t.engine.redo({ ...t.scope(), transactionId: id(7500 + t.engine.revision), baseRevision: t.engine.revision });
  expect(r.ok).toBe(true);
}

describe('pages', () => {
  it('add_rename_size_and_undo', async () => {
    const t = createTestEngine();
    const before = body(t);
    const r = await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second', widthPt: 300, heightPt: 200 }]);
    expect(r.changedPageIds).toEqual([PAGE2]);
    expect(doc(t).pages.map(p => p.name)).toEqual(['Page 1', 'Second']);
    expect(doc(t).pages[1]).toMatchObject({ widthPt: 300, heightPt: 200, elements: [] });
    expect(doc(t).pages[1]!.layers).toHaveLength(1); // a new page starts with one layer like a new document
    await run(t, [{ op: 'setPage', pageId: PAGE2, patch: { name: 'Renamed', widthPt: 400 } }]);
    expect(doc(t).pages[1]).toMatchObject({ name: 'Renamed', widthPt: 400, heightPt: 200 });
    await undo(t);
    await undo(t);
    expect(body(t)).toBe(before);
    await redo(t);
    expect(doc(t).pages).toHaveLength(2);
  });

  it('reorder_moves_a_page_and_undo_restores_the_order', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'B' }, { op: 'addPage', id: PAGE3, name: 'C' }]);
    const before = body(t);
    const r = await run(t, [{ op: 'reorderPage', pageId: PAGE3, index: 0 }]);
    expect(r.changedPageIds).toContain(PAGE3);
    expect(doc(t).pages.map(p => p.name)).toEqual(['C', 'Page 1', 'B']);
    await undo(t);
    expect(body(t)).toBe(before);
    await redo(t);
    expect(doc(t).pages.map(p => p.name)).toEqual(['C', 'Page 1', 'B']);
  });

  it('addPage_at_an_index', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Front', index: 0 }]);
    expect(doc(t).pages.map(p => p.name)).toEqual(['Front', 'Page 1']);
  });

  it('duplicate_remaps_everything_inside_the_page', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'addLayer', id: L2, name: 'Notes' },
      { op: 'assignLayers', targets: [s(t, 1)], layerIds: [L2], mode: 'add' },
      { op: 'createConnector', id: id(950), alias: 'link', from: { elementId: s(t, 0), glue: 'dynamic' }, to: { elementId: s(t, 1), glue: 'dynamic' } },
      { op: 'group', targets: [s(t, 0), s(t, 1)], id: id(900) },
    ]);
    const before = t.store.snapshot();
    const r = await run(t, [{ op: 'duplicatePage', pageId: t.ids.page, id: PAGE2 }]);
    expect(doc(t).pages.map(p => p.id)).toEqual([t.ids.page, PAGE2]); // the copy follows its source
    expect(doc(t).pages[1]!.name).toBe('Page 1 copy');
    const copy = doc(t).pages[1]!;
    const original = doc(t).pages[0]!;
    expect(copy.elements).toHaveLength(original.elements.length);
    expect(r.created).toHaveLength(original.elements.length);
    const ids = new Set(original.elements.map(e => e.id));
    expect(copy.elements.every(e => !ids.has(e.id))).toBe(true);
    const copiedIds = new Set(copy.elements.map(e => e.id));
    const link = copy.elements.find(e => e.kind === 'connector') as ConnectorElement;
    expect(copiedIds.has(link.from.elementId!) && copiedIds.has(link.to.elementId!)).toBe(true);
    const g = copy.elements.find(e => e.kind === 'group');
    expect(g && g.kind === 'group' && g.childIds.every(c => copiedIds.has(c))).toBe(true);
    const layerIds = new Set(copy.layers.map(l => l.id));
    expect(copy.layers.map(l => l.name)).toEqual(original.layers.map(l => l.name));
    expect(copy.layers.every(l => !original.layers.some(o => o.id === l.id))).toBe(true);
    expect(copy.elements.every(e => e.layerIds.every(l => layerIds.has(l)))).toBe(true);
    expect(copy.elements.find(e => e.alias === 'link')).toBeDefined(); // aliases are page-scoped, so they carry over
    expect(canonicalSerialize(doc(t).pages[0]!)).toBe(canonicalSerialize(before.document.pages[0]!));
    await undo(t);
    expect(doc(t).pages).toHaveLength(1);
    expect(exactElementHashes(t.store.snapshot())).toEqual(exactElementHashes(before));
  });

  it('delete_resolves_its_elements_explicitly_and_undo_brings_them_back', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }]);
    await run(t, [{ op: 'createShape', id: id(801), pageId: PAGE2, bounds: { x: 1, y: 1, width: 10, height: 10 } }, { op: 'createShape', id: id(802 + 100), pageId: PAGE2, bounds: { x: 1, y: 1, width: 10, height: 10 } }]);
    const before = body(t);
    const r = await run(t, [{ op: 'deletePage', pageId: PAGE2 }]);
    expect(r.deleted.sort()).toEqual([id(801), id(902)].sort());
    expect(r.changedPageIds).toEqual([PAGE2]);
    expect(doc(t).pages).toHaveLength(1);
    await undo(t);
    expect(body(t)).toBe(before);
    await redo(t);
    expect(doc(t).pages).toHaveLength(1);
  });

  it('the_last_page_cannot_be_deleted', async () => {
    const t = createTestEngine();
    const e = await refused(t, [{ op: 'deletePage', pageId: t.ids.page }]);
    expect(e.code).toBe('invalid_request');
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }, { op: 'deletePage', pageId: t.ids.page }]);
    expect(doc(t).pages.map(p => p.id)).toEqual([PAGE2]);
  });

  it('cross_page_references_cannot_commit', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }]);
    await run(t, [{ op: 'createShape', id: id(801), pageId: PAGE2, bounds: { x: 1, y: 1, width: 10, height: 10 } }]);
    const e = await refused(t, [{ op: 'createConnector', pageId: PAGE2, from: { elementId: id(801), glue: 'dynamic' }, to: { elementId: s(t, 0), glue: 'dynamic' } }]);
    expect(e.code).toBe('invalid_request');
    const g = await refused(t, [{ op: 'group', targets: [s(t, 0), id(801)] }]);
    expect(g.details?.reason).toBe('mixed_pages');
    const dupe = await refused(t, [{ op: 'createShape', id: s(t, 0), pageId: PAGE2, bounds: { x: 1, y: 1, width: 10, height: 10 } }]);
    expect(dupe.code).toBe('invalid_request');
  });

  it('other_pages_are_never_touched_by_a_page_edit', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }]);
    const first = doc(t).pages[0]!;
    await run(t, [{ op: 'setPage', pageId: PAGE2, patch: { name: 'X' } }]);
    expect(doc(t).pages[0]).toBe(first);
  });
});

describe('layers', () => {
  it('add_set_assign_and_undo', async () => {
    const t = createTestEngine();
    const before = body(t);
    const add = await run(t, [{ op: 'addLayer', id: L2, name: 'Notes', printable: false }]);
    expect(add.changedLayerIds).toEqual([L2]);
    expect(doc(t).pages[0]!.layers[1]).toMatchObject({ id: L2, name: 'Notes', visible: true, locked: false, printable: false, snap: true, glue: true });
    await run(t, [{ op: 'setLayer', layerId: L2, patch: { visible: false, name: 'Hidden notes', colourOverride: '#ff0000' } }]);
    expect(doc(t).pages[0]!.layers[1]).toMatchObject({ visible: false, name: 'Hidden notes', colourOverride: '#ff0000' });
    const a = await run(t, [{ op: 'assignLayers', targets: [s(t, 0), s(t, 1)], layerIds: [L2] }]); // default mode adds
    expect(a.changed.sort()).toEqual([s(t, 0), s(t, 1)].sort());
    expect(shape(t, s(t, 0)).layerIds).toEqual([t.ids.layer, L2]);
    const same = await run(t, [{ op: 'assignLayers', targets: [s(t, 0)], layerIds: [L2], mode: 'add' }]);
    expect(same.noChange).toBe(true);
    await run(t, [{ op: 'assignLayers', targets: [s(t, 0)], layerIds: [t.ids.layer], mode: 'remove' }]);
    expect(shape(t, s(t, 0)).layerIds).toEqual([L2]);
    await run(t, [{ op: 'assignLayers', targets: [s(t, 0)], layerIds: [t.ids.layer], mode: 'set' }]);
    expect(shape(t, s(t, 0)).layerIds).toEqual([t.ids.layer]);
    for (let i = 0; i < 5; i++) await undo(t);
    expect(body(t)).toBe(before);
  });

  it('hiding_a_layer_keeps_its_objects', async () => {
    const t = createTestEngine();
    const before = els(t).length;
    await run(t, [{ op: 'setLayer', layerId: t.ids.layer, patch: { visible: false } }]);
    expect(els(t)).toHaveLength(before);
  });

  it('deleting_a_layer_unassigns_members_and_reports_them', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addLayer', id: L2, name: 'Notes' }, { op: 'assignLayers', targets: [s(t, 0), s(t, 1)], layerIds: [L2], mode: 'set' }]);
    const before = body(t);
    const r = await run(t, [{ op: 'deleteLayer', layerId: L2 }]);
    expect(r.changedLayerIds).toEqual([L2]);
    expect(r.changed.sort()).toEqual([s(t, 0), s(t, 1)].sort());
    expect(shape(t, s(t, 0)).layerIds).toEqual([]);
    expect(doc(t).pages[0]!.layers.map(l => l.id)).toEqual([t.ids.layer]);
    await undo(t);
    expect(body(t)).toBe(before);
  });

  it('unknown_layers_and_cross_page_layers_are_refused', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'addPage', id: PAGE2, name: 'Second' }, { op: 'addLayer', id: L2, pageId: PAGE2, name: 'Other' }]);
    expect((await refused(t, [{ op: 'assignLayers', targets: [s(t, 0)], layerIds: [L2] }])).code).toBe('invalid_request');
    expect((await refused(t, [{ op: 'setLayer', layerId: id(999), patch: { visible: false } }])).code).toBe('not_found');
  });
});

describe('assets', () => {
  const sha = (c: string) => c.repeat(64);
  const logo3 = { id: 'asset:logo-v3', name: 'Logo v3', mimeType: 'image/png' as const, sha256: sha('c'), tags: [] };

  it('replaceAsset_swaps_every_reference_or_only_the_named_images', async () => {
    const t = createTestEngine();
    await run(t, [
      { op: 'createImage', id: id(960), bounds: { x: 0, y: 0, width: 10, height: 10 }, assetId: 'asset:logo' },
      { op: 'createImage', id: id(961), bounds: { x: 0, y: 0, width: 10, height: 10 }, assetId: 'asset:logo' },
    ]);
    const some = await run(t, [{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2', targets: [id(960)] }]);
    expect(some.changed).toEqual([id(960)]);
    const all = await run(t, [{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2' }]);
    expect(all.changed.sort()).toEqual([t.ids.image, id(961)].sort());
    expect(els(t).filter(e => e.kind === 'image').every(e => (e as ImageElement).assetId === 'asset:logo-v2')).toBe(true);
    const bad = await refused(t, [{ op: 'replaceAsset', from: 'asset:logo-v2', to: 'asset:missing' }]);
    expect(bad.code).toBe('invalid_request');
    const wrong = await refused(t, [{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2', targets: [id(960)] }]);
    expect(wrong.code).toBe('invalid_request'); // id(960) no longer points at asset:logo
  });

  it('an_asset_in_use_cannot_be_removed_until_its_references_move', async () => {
    const t = createTestEngine();
    const e = await refused(t, [{ op: 'removeAsset', assetId: 'asset:logo' }]);
    expect(e.code).toBe('dependency_conflict');
    expect(e.details?.elementIds).toEqual([t.ids.image]);
    const before = body(t);
    const r = await run(t, [{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2' }, { op: 'removeAsset', assetId: 'asset:logo' }]);
    expect(r.changedAssetIds).toEqual(['asset:logo']);
    expect(doc(t).assets.map(a => a.id)).toEqual(['asset:logo-v2']);
    await undo(t);
    expect(body(t)).toBe(before);
  });

  it('catalogue_edits_register_describe_and_remove', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'registerAsset', asset: logo3 }]);
    await run(t, [{ op: 'setAsset', assetId: 'asset:logo-v3', patch: { name: 'Third', tags: ['logo', 'v3'], provenance: 'test' } }]);
    expect(doc(t).assets.find(a => a.id === 'asset:logo-v3')).toMatchObject({ name: 'Third', tags: ['logo', 'v3'], provenance: 'test', sha256: sha('c') });
    await run(t, [{ op: 'removeAsset', assetId: 'asset:logo-v3' }]);
    expect(doc(t).assets.some(a => a.id === 'asset:logo-v3')).toBe(false);
    expect((await refused(t, [{ op: 'setAsset', assetId: 'asset:nope', patch: { name: 'x' } }])).code).toBe('not_found');
  });

  it('a_group_is_not_an_image_target', async () => {
    const t = createTestEngine();
    await run(t, [{ op: 'group', targets: [t.ids.image, s(t, 0)], id: id(900) }]);
    expect(group(t, id(900)).childIds).toHaveLength(2);
    const e = await refused(t, [{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2', targets: [s(t, 0)] }]);
    expect(e.code).toBe('invalid_request');
    expect(byId<ImageElement>(t, t.ids.image).assetId).toBe('asset:logo');
  });
});

describe('arrowhead size', () => {
  it('is set, validated, undone and left implicit when medium', async () => {
    const t = createTestEngine();
    const a = id(900), b = id(901), c = id(902);
    await run(t, [
      { op: 'createShape', id: a, bounds: { x: 0, y: 0, width: 50, height: 50 } },
      { op: 'createShape', id: b, bounds: { x: 200, y: 0, width: 50, height: 50 } },
      { op: 'createConnector', id: c, from: { elementId: a, glue: 'dynamic' }, to: { elementId: b, glue: 'dynamic' }, style: { endArrowSize: 'large', startArrow: 'open', startArrowSize: 'small' } },
    ]);
    expect(byId<ConnectorElement>(t, c).style).toMatchObject({ endArrowSize: 'large', startArrowSize: 'small', startArrow: 'open' });
    const before = body(t);
    await run(t, [{ op: 'set', target: c, patch: { style: { endArrowSize: 'medium' } } }]);
    expect(byId<ConnectorElement>(t, c).style.endArrowSize).toBe('medium');
    await undo(t);
    expect(body(t)).toBe(before);
    const bad = await refused(t, [{ op: 'set', target: c, patch: { style: { endArrowSize: 'huge' } } } as never]);
    expect(bad.code).toBe('invalid_request');
  });

  it('is refused on a shape, which has no arrowheads', async () => {
    const t = createTestEngine();
    const a = id(903);
    await run(t, [{ op: 'createShape', id: a, bounds: { x: 0, y: 0, width: 50, height: 50 } }]);
    await refused(t, [{ op: 'set', target: a, patch: { style: { endArrowSize: 'large' } } } as never]);
  });
});
