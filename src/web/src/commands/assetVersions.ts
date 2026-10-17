import type { Asset, ImageElement, Operation, PreparedAssetRef, Snapshot } from '../contracts/generated';

/** What it takes to use a prepared picture in a document: registration operations (maybe none) and the id images should point at. */
export interface DocumentAssetPlan {
  operations: Operation[];
  resolvedAssetId: string;
  /** The id differs from the library or upload id because the document already pins different bytes under it. */
  versioned: boolean;
}

/** The id without any version suffix: `asset:logo~abc` and `asset:logo` are the same picture's family. */
export const assetFamily = (id: string): string => (id.includes('~') ? id.slice(0, id.indexOf('~')) : id);

/**
 * Pins a prepared picture into a document without touching anything already there:
 * - not in the document: register it under its own id;
 * - already there with the same bytes: nothing to do;
 * - already there with different bytes (a newer library version, or another upload with the same name): register a separate version
 *   `asset:<slug>~<full sha-256>`. An existing id is never overwritten, so images that use it are unchanged.
 */
export function prepareDocumentAsset(snapshot: Snapshot, prepared: PreparedAssetRef): DocumentAssetPlan {
  const asset = prepared.asset;
  const assets = snapshot.document.assets;
  const same = assets.find(a => a.id === asset.id);
  if (!same) {
    const sameBytes = assets.find(a => a.sha256 === asset.sha256 && assetFamily(a.id) === assetFamily(asset.id));
    if (sameBytes) return { operations: [], resolvedAssetId: sameBytes.id, versioned: sameBytes.id !== asset.id };
    return { operations: [{ op: 'registerAsset', asset: { ...asset, tags: [...asset.tags] } }], resolvedAssetId: asset.id, versioned: false };
  }
  if (same.sha256 === asset.sha256) return { operations: [], resolvedAssetId: same.id, versioned: false };
  const versionId = `${assetFamily(asset.id)}~${asset.sha256}`;
  if (assets.some(a => a.id === versionId)) return { operations: [], resolvedAssetId: versionId, versioned: true };
  const version: Asset = { ...asset, id: versionId, tags: [...asset.tags] };
  return { operations: [{ op: 'registerAsset', asset: version }], resolvedAssetId: versionId, versioned: true };
}

/** Replace the picture of exactly one image: nothing else that shares its descriptor changes. */
export function replaceOneImage(snapshot: Snapshot, imageId: string, prepared: PreparedAssetRef): { operations: Operation[]; resolvedAssetId: string } | { error: string } {
  const image = imageElement(snapshot, imageId);
  if (!image) return { error: `${imageId} is not an image` };
  const plan = prepareDocumentAsset(snapshot, prepared);
  if (plan.resolvedAssetId === image.assetId) return { operations: [], resolvedAssetId: plan.resolvedAssetId };
  return {
    operations: [...plan.operations, { op: 'replaceAsset', from: image.assetId, to: plan.resolvedAssetId, targets: [imageId] }],
    resolvedAssetId: plan.resolvedAssetId,
  };
}

/** Replace a picture everywhere it is used. The result names every image that will look different, so nobody is surprised. */
export function replaceEverywhere(snapshot: Snapshot, fromAssetId: string, prepared: PreparedAssetRef): { operations: Operation[]; resolvedAssetId: string; affectedImageIds: string[] } | { error: string } {
  if (!snapshot.document.assets.some(a => a.id === fromAssetId)) return { error: `the document has no asset ${fromAssetId}` };
  const affected = imagesUsing(snapshot, fromAssetId).map(i => i.id);
  const plan = prepareDocumentAsset(snapshot, prepared);
  if (plan.resolvedAssetId === fromAssetId) return { operations: [], resolvedAssetId: plan.resolvedAssetId, affectedImageIds: [] };
  return { operations: [...plan.operations, { op: 'replaceAsset', from: fromAssetId, to: plan.resolvedAssetId }], resolvedAssetId: plan.resolvedAssetId, affectedImageIds: affected };
}

export const imagesUsing = (snapshot: Snapshot, assetId: string): ImageElement[] =>
  snapshot.document.pages.flatMap(p => p.elements.filter((e): e is ImageElement => e.kind === 'image' && e.assetId === assetId));

/** The check behind a Delete button: an asset in use stays until its images point elsewhere. */
export function canDeleteAsset(snapshot: Snapshot, assetId: string): { ok: true } | { ok: false; imageIds: string[] } {
  const users = imagesUsing(snapshot, assetId);
  return users.length ? { ok: false, imageIds: users.map(u => u.id) } : { ok: true };
}

function imageElement(snapshot: Snapshot, id: string): ImageElement | undefined {
  for (const p of snapshot.document.pages) for (const e of p.elements) if (e.id === id && e.kind === 'image') return e;
  return undefined;
}
