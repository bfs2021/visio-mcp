import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { toPoints, validateDocument, validateEnvelope, validateMutationRequest, validateOperation } from '../src/model/validate';

const fixture = (name: string) => JSON.parse(readFileSync(new URL(`../../../contracts/fixtures/${name}`, import.meta.url), 'utf8'));
const U = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

const validBatch = () => ({
  documentId: U(1), sessionId: U(2), transactionId: U(3), baseRevision: 0, atomic: true as const,
  operations: [{ op: 'move', target: 'box', delta: { xPt: 1, yPt: 2 } }],
});

describe('contract validation', () => {
  it('accepts the valid apply fixture as an envelope and as a merged request', () => {
    expect(validateEnvelope(fixture('valid-apply.json')).ok).toBe(true);
  });

  it('rejects unknown operation fields', () => {
    const r = validateEnvelope(fixture('invalid-apply.json'));
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('invalid_request');
      expect(r.error.details?.keyword).toBe('additionalProperties');
    }
  });

  it('rejects unknown patch fields', () => {
    const r = validateOperation({ op: 'set', target: 'box', patch: { colour: '#ffffff' } });
    expect(r.ok).toBe(false);
  });

  it('rejects an empty patch and an empty bounds patch', () => {
    expect(validateOperation({ op: 'set', target: 'box', patch: {} }).ok).toBe(false);
    expect(validateOperation({ op: 'resize', target: 'box', bounds: {} }).ok).toBe(false);
  });

  it.each([NaN, Infinity, -Infinity])('rejects non-finite number %s', n => {
    expect(validateOperation({ op: 'move', target: 'box', delta: { xPt: n, yPt: 0 } }).ok).toBe(false);
  });

  it('rejects atomic:false', () => {
    expect(validateMutationRequest({ ...validBatch(), atomic: false }).ok).toBe(false);
  });

  it('rejects duplicate ids and duplicate aliases created in one batch', () => {
    const create = (extra: object) => ({ op: 'createShape', bounds: { x: 0, y: 0, width: 10, height: 10 }, ...extra });
    const sameId = validateMutationRequest({ ...validBatch(), operations: [create({ id: U(10) }), create({ id: U(10) })] });
    expect(sameId.ok).toBe(false);
    const sameAlias = validateMutationRequest({ ...validBatch(), operations: [create({ alias: 'a' }), create({ alias: 'a' })] });
    expect(sameAlias.ok).toBe(false);
    if (!sameAlias.ok) expect(sameAlias.error.details).toEqual({ alias: 'a' });
  });

  it('rejects an operation with both angleDeg and deltaDeg, and with neither', () => {
    expect(validateOperation({ op: 'rotate', target: 'a', angleDeg: 1, deltaDeg: 2 }).ok).toBe(false);
    expect(validateOperation({ op: 'rotate', target: 'a' }).ok).toBe(false);
    expect(validateOperation({ op: 'rotate', target: 'a', deltaDeg: 2 }).ok).toBe(true);
  });

  it('rejects scope repeated inside params', () => {
    const env = fixture('valid-apply.json');
    env.params.documentId = env.documentId;
    expect(validateEnvelope(env).ok).toBe(false);
  });

  it('converts units at the boundary: mm, cm, in, pt, px', () => {
    expect(toPoints(25.4, 'mm')).toBeCloseTo(72, 10);
    expect(toPoints(2.54, 'cm')).toBeCloseTo(72, 10);
    expect(toPoints(1, 'in')).toBe(72);
    expect(toPoints(5, 'pt')).toBe(5);
    expect(toPoints(96, 'px')).toBe(72);
    expect(toPoints(297, 'mm')).toBeCloseTo(841.8897637795, 6);
    expect(() => toPoints(Infinity, 'mm')).toThrow(RangeError);
  });

  it('accepts one valid batch with a create, a move and a nested style patch', () => {
    const r = validateMutationRequest({ ...validBatch(), operations: fixture('valid-apply.json').params.operations });
    expect(r.ok).toBe(true);
  });
});

describe('document validation', () => {
  const doc = () => ({
    schemaVersion: 1, id: U(1), title: 't', revision: 0, assets: [], metadata: {},
    pages: [{
      id: U(2), name: 'Page 1', widthPt: 842, heightPt: 595, background: { kind: 'none' },
      grid: { visible: true, spacingPt: 14.17, snap: true }, guides: [], layers: [], elements: [] as unknown[],
    }],
  });
  const shape = (n: number, extra: object = {}) => ({
    id: U(n), kind: 'shape', layerIds: [], zIndex: n, locked: false, hidden: false, bounds: { x: 0, y: 0, width: 10, height: 10 },
    rotationDeg: 0, metadata: {}, geometry: { preset: 'rectangle' },
    style: { fill: { kind: 'none' }, stroke: { kind: 'none' }, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' }, ...extra,
  });

  it('accepts a minimal document', () => {
    const d = doc();
    d.pages[0]!.elements.push(shape(10));
    expect(validateDocument(d).ok).toBe(true);
  });

  it('rejects duplicate element ids and duplicate aliases', () => {
    const a = doc(); a.pages[0]!.elements.push(shape(10), shape(10));
    expect(validateDocument(a).ok).toBe(false);
    const b = doc(); b.pages[0]!.elements.push(shape(10, { alias: 'x' }), shape(11, { alias: 'x' }));
    expect(validateDocument(b).ok).toBe(false);
  });

  it('rejects images with unknown assets and glued connectors with missing targets', () => {
    const img = doc();
    img.pages[0]!.elements.push({ ...shape(10), kind: 'image', assetId: 'asset:none', fit: 'contain', opacity: 1, preserveAspectRatio: true, geometry: undefined, style: undefined });
    expect(validateDocument(img).ok).toBe(false);
    const con = doc();
    con.pages[0]!.elements.push({
      id: U(10), kind: 'connector', layerIds: [], zIndex: 1, locked: false, hidden: false, bounds: { x: 0, y: 0, width: 0, height: 0 }, rotationDeg: 0, metadata: {},
      from: { elementId: U(99), glue: 'dynamic' }, to: { point: { x: 1, y: 1 }, glue: 'none' }, route: 'straight', waypoints: [],
      style: { colour: '#000000', widthPt: 1, dash: 'solid', startArrow: 'none', endArrow: 'block' },
    });
    expect(validateDocument(con).ok).toBe(false);
  });

  it('rejects unknown fields and non-finite geometry', () => {
    const a = doc(); (a as Record<string, unknown>).extra = 1;
    expect(validateDocument(a).ok).toBe(false);
    const b = doc(); b.pages[0]!.elements.push(shape(10, { bounds: { x: NaN, y: 0, width: 10, height: 10 } }));
    expect(validateDocument(b).ok).toBe(false);
  });
});

import { samples } from './support/samples';
import { schemaDefinitionNames, validateDef } from '../src/model/validate';

describe('shared DTO samples', () => {
  const defOf = (key: string) => (key === 'ResponseEnvelopeError' ? 'ResponseEnvelope' : key);

  it.each(Object.keys(samples))('sample %s is valid against its schema definition', key => {
    const r = validateDef(defOf(key), samples[key]);
    expect(r.message).toBeUndefined();
    expect(r.valid).toBe(true);
  });

  it('covers every cross-boundary type named by the overview plan', () => {
    const required = [
      'Scope', 'Snapshot', 'Projection', 'ExportSnapshot', 'TransactionResult', 'ResolvedDiff', 'ChangeSet', 'Diagnostic',
      'AppError', 'RequestEnvelope', 'ResponseEnvelope', 'CommittedEvent', 'PreparedAssetRef', 'PreparedAsset',
      'MutationRequest', 'ScriptRequest', 'UndoRequest', 'DiagramDocument', 'TransactionSummary',
    ];
    for (const r of required) expect(Object.keys(samples), r).toContain(r);
    expect(schemaDefinitionNames()).toEqual(expect.arrayContaining(required));
  });

  it('exercises every operation and every element kind', () => {
    const ops = (samples.MutationRequest as { operations: Array<{ op: string }> }).operations.map(o => o.op).sort();
    expect(ops).toEqual(['createConnector', 'createImage', 'createShape', 'createText', 'delete', 'move', 'registerAsset', 'resize', 'rotate', 'set']);
    const kinds = (samples.DiagramDocument as { pages: Array<{ elements: Array<{ kind: string }> }> }).pages[0]!.elements.map(e => e.kind).sort();
    expect(kinds).toEqual(['connector', 'group', 'image', 'shape', 'text']);
  });

  it('keeps the generated TypeScript and C# contracts in step with the schemas', async () => {
    const { execFileSync } = await import('node:child_process');
    const script = new URL('../../../tools/generate-contracts.mjs', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
    expect(() => execFileSync('node', [script, '--check'], { stdio: 'pipe' })).not.toThrow();
  });
});
