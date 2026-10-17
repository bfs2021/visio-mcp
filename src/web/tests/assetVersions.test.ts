import { describe, expect, it } from 'vitest';
import type { ImageElement, Operation, PreparedAssetRef } from '../src/contracts/generated';
import { canDeleteAsset, prepareDocumentAsset, replaceEverywhere, replaceOneImage } from '../src/commands/assetVersions';
import { createTestEngine, exactElementHashes, hashesExcept } from './support/engine';
import { id, refused, run } from './support/ops';

const sha = (c: string) => c.repeat(64);
const ref = (assetId: string, hash: string, name = 'Logo'): PreparedAssetRef => ({
  preparationId: 'prep-test', expiresAt: '2030-01-01T00:00:00Z',
  asset: { id: assetId, name, mimeType: 'image/png', sha256: hash, tags: ['logo'], provenance: 'library' },
});
const two = (t: ReturnType<typeof createTestEngine>) => run(t, [
  { op: 'createImage', id: id(960), bounds: { x: 0, y: 0, width: 10, height: 10 }, assetId: 'asset:logo' },
  { op: 'createImage', id: id(961), bounds: { x: 0, y: 0, width: 10, height: 10 }, assetId: 'asset:logo' },
]);
const asOps = (ops: Operation[]) => ops;

describe('document assets and versions', () => {
  it('SingleImageOnly', async () => {
    const t = createTestEngine();
    await two(t);
    const snap = t.store.snapshot();
    const unrelatedHashesBefore = hashesExcept(snap, id(960));
    const plan = replaceOneImage(snap, id(960), ref('asset:fresh', sha('d'), 'Fresh'));
    if ('error' in plan) throw new Error(plan.error);
    const r = await run(t, asOps(plan.operations));
    const changedElementIds = r.changed;
    expect(changedElementIds).toEqual([id(960)]);
    expect(hashesExcept(t.store.snapshot(), id(960))).toEqual(unrelatedHashesBefore);
    const after = t.store.snapshot().document.pages[0]!.elements;
    expect((after.find(e => e.id === id(960)) as ImageElement).assetId).toBe('asset:fresh');
    expect((after.find(e => e.id === id(961)) as ImageElement).assetId).toBe('asset:logo'); // its twin keeps the old picture
    expect(t.store.snapshot().document.assets.find(a => a.id === 'asset:logo')!.sha256).toBe(sha('a')); // the shared descriptor was not rewritten
  });

  it('GlobalReplacementReportsAffectedImages', async () => {
    const t = createTestEngine();
    await two(t);
    const plan = replaceEverywhere(t.store.snapshot(), 'asset:logo', ref('asset:fresh', sha('d'), 'Fresh'));
    if ('error' in plan) throw new Error(plan.error);
    expect([...plan.affectedImageIds].sort()).toEqual([t.ids.image, id(960), id(961)].sort());
    const r = await run(t, plan.operations);
    expect([...r.changed].sort()).toEqual([...plan.affectedImageIds].sort());
    const unrelated = hashesExcept(t.store.snapshot(), ...plan.affectedImageIds);
    expect(Object.keys(unrelated).length).toBe(19); // every ordinary shape untouched
    expect(replaceEverywhere(t.store.snapshot(), 'asset:nope', ref('asset:x', sha('e')))).toMatchObject({ error: expect.stringContaining('asset:nope') });
  });

  it('NewHashSameSlugPinsSeparateVersion', async () => {
    const t = createTestEngine();
    await two(t);
    const newer = ref('asset:logo', sha('f')); // the library moved on: same id, different bytes
    const plan = prepareDocumentAsset(t.store.snapshot(), newer);
    const newId = plan.resolvedAssetId;
    expect(newId).toBe(`asset:logo~${sha('f')}`);
    expect(plan.versioned).toBe(true);
    await run(t, plan.operations);
    const assets = t.store.snapshot().document.assets;
    expect(assets.find(a => a.id === 'asset:logo')!.sha256).toBe(sha('a')); // the old pin is intact
    expect(assets.find(a => a.id === newId)!.sha256).toBe(sha('f'));
    expect(prepareDocumentAsset(t.store.snapshot(), newer).operations).toEqual([]); // asking again reuses the version
    expect(prepareDocumentAsset(t.store.snapshot(), newer).resolvedAssetId).toBe(newId);
    // using it for one image changes only that image
    const before = exactElementHashes(t.store.snapshot());
    const one = replaceOneImage(t.store.snapshot(), id(961), newer);
    if ('error' in one) throw new Error(one.error);
    await run(t, one.operations);
    const after = exactElementHashes(t.store.snapshot());
    expect(Object.keys(after).filter(k => after[k] !== before[k])).toEqual([id(961)]);
  });

  it('a_bare_slug_with_the_same_bytes_needs_no_registration', () => {
    const t = createTestEngine();
    expect(prepareDocumentAsset(t.store.snapshot(), ref('asset:logo', sha('a')))).toEqual({ operations: [], resolvedAssetId: 'asset:logo', versioned: false });
    const fresh = prepareDocumentAsset(t.store.snapshot(), ref('asset:brand-new', sha('9')));
    expect(fresh.operations).toHaveLength(1);
    expect(fresh.resolvedAssetId).toBe('asset:brand-new');
  });

  it('InUseDeleteRejects', async () => {
    const t = createTestEngine();
    await two(t);
    const check = canDeleteAsset(t.store.snapshot(), 'asset:logo');
    expect(check).toMatchObject({ ok: false });
    expect(check.ok ? [] : check.imageIds.sort()).toEqual([t.ids.image, id(960), id(961)].sort());
    const e = await refused(t, [{ op: 'removeAsset', assetId: 'asset:logo' }]);
    expect(e.code).toBe('dependency_conflict');
    expect(canDeleteAsset(t.store.snapshot(), 'asset:logo-v2')).toEqual({ ok: true });
    await run(t, [{ op: 'removeAsset', assetId: 'asset:logo-v2' }]);
    expect(t.store.snapshot().document.assets.map(a => a.id)).toEqual(['asset:logo']);
  });

  it('ImportedDocumentWorksWithoutLibrary', async () => {
    // a document carries its pinned descriptors, so nothing here needs the host library
    const t = createTestEngine();
    const snap = t.store.snapshot();
    expect(snap.document.assets.map(a => a.id)).toEqual(['asset:logo', 'asset:logo-v2']);
    const plan = replaceOneImage(snap, t.ids.image, ref('asset:logo-v2', sha('b'), 'Logo v2'));
    if ('error' in plan) throw new Error(plan.error);
    expect(plan.operations).toEqual([{ op: 'replaceAsset', from: 'asset:logo', to: 'asset:logo-v2', targets: [t.ids.image] }]);
    await run(t, plan.operations);
    expect((t.store.snapshot().document.pages[0]!.elements.find(e => e.id === t.ids.image) as ImageElement).assetId).toBe('asset:logo-v2');
  });

  it('replacing_an_image_with_its_own_picture_is_a_no_op', () => {
    const t = createTestEngine();
    const plan = replaceOneImage(t.store.snapshot(), t.ids.image, ref('asset:logo', sha('a')));
    expect(plan).toEqual({ operations: [], resolvedAssetId: 'asset:logo' });
    expect(replaceOneImage(t.store.snapshot(), t.ids.shape, ref('asset:logo', sha('a')))).toMatchObject({ error: expect.any(String) });
  });
});
