import type { DiagramDocument, Element, MutationRequest, Operation, ShapeElement, Snapshot } from '../../src/contracts/generated';
import { CommandEngine } from '../../src/commands/CommandEngine';
import { canonicalSerialize } from '../../src/model/canonical';
import type { DocumentStore } from '../../src/model/store';

/** Deterministic UUIDs: 00000000-0000-4000-8000-<prefix><counter>. */
export function sequenceIds(prefix = '1'): () => string {
  let n = 0;
  return () => `00000000-0000-4000-8000-${prefix}${String(++n).padStart(11, '0')}`;
}

export const TEXT = {
  value: '', fontFamily: 'Segoe UI', fontSizePt: 12, bold: false, italic: false, underline: false, colour: '#000000',
  horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 4,
} as const;

export interface Seed {
  document: DiagramDocument;
  ids: { page: string; layer: string; image: string; shapes: string[]; shape: string };
}

/** One image, 19 ordinary shapes, two approved asset descriptors, one page and one layer. */
export function seedDocument(count = 20): Seed {
  const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  const page = id(100);
  const layer = id(101);
  const elements: Element[] = [];
  const image = id(200);
  elements.push({
    id: image, kind: 'image', layerIds: [layer], zIndex: 1, locked: false, hidden: false,
    bounds: { x: 20, y: 20, width: 64, height: 64 }, rotationDeg: 0, metadata: {},
    assetId: 'asset:logo', fit: 'contain', opacity: 1, preserveAspectRatio: true,
  });
  const shapes: string[] = [];
  for (let i = 0; i < count - 1; i++) {
    const sid = id(300 + i);
    shapes.push(sid);
    elements.push({
      id: sid, kind: 'shape', alias: `s${i}`, name: `Shape ${i}`, layerIds: [layer], zIndex: i + 2, locked: false, hidden: false,
      bounds: { x: 100 + (i % 5) * 120, y: 40 + Math.floor(i / 5) * 90, width: 100, height: 60 }, rotationDeg: 0, metadata: { n: String(i) },
      geometry: { preset: 'rectangle' },
      style: { fill: { kind: 'solid', colour: '#dbeafe', opacity: 1 }, stroke: { kind: 'solid', colour: '#1e5ac8', opacity: 1 }, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' },
      text: { ...TEXT, value: `Shape ${i}` },
    } satisfies ShapeElement);
  }
  const sha = (c: string) => c.repeat(64);
  return {
    ids: { page, layer, image, shapes, shape: shapes[0]! },
    document: {
      schemaVersion: 1, id: id(1), title: 'Seed', revision: 0, metadata: {},
      assets: [
        { id: 'asset:logo', name: 'Logo', mimeType: 'image/png', sha256: sha('a'), tags: ['logo'] },
        { id: 'asset:logo-v2', name: 'Logo v2', mimeType: 'image/png', sha256: sha('b'), tags: ['logo'] },
      ],
      pages: [{
        id: page, name: 'Page 1', widthPt: 841.8897637795, heightPt: 595.2755905512, background: { kind: 'solid', colour: '#ffffff', opacity: 1 },
        grid: { visible: true, spacingPt: 14.1732283465, snap: true }, guides: [],
        layers: [{ id: layer, name: 'Layer 1', visible: true, locked: false, printable: true, snap: true, glue: true }],
        elements,
      }],
    },
  };
}

export function createTestEngine(opts: { document?: DiagramDocument } = {}) {
  const seed = seedDocument();
  const document = opts.document ?? seed.document;
  const idFactory = sequenceIds('1');
  const engine = new CommandEngine({ document, idFactory, sessionIdFactory: sequenceIds('5') });
  const txIds = sequenceIds('9');
  const store: DocumentStore = engine.store;

  const scope = () => ({ documentId: engine.documentId, sessionId: engine.sessionId });
  /** A mutation request carrying the current scope and revision plus a fresh fixed-sequence transaction UUID. */
  const request = (operations: Operation[], overrides: Partial<MutationRequest> = {}): MutationRequest => ({
    ...scope(), transactionId: txIds(), baseRevision: engine.revision, atomic: true, operations, ...overrides,
  });
  return { engine, store, seed, ids: seed.ids, idFactory, request, scope, get documentId() { return engine.documentId; }, get sessionId() { return engine.sessionId; } };
}

export function exactElementHashes(snapshot: Snapshot): Record<string, string> {
  const out: Record<string, string> = {};
  for (const page of snapshot.document.pages) for (const el of page.elements) out[el.id] = canonicalSerialize(el);
  return out;
}

export function hashesExcept(snapshot: Snapshot, ...except: string[]): Record<string, string> {
  const all = exactElementHashes(snapshot);
  for (const id of except) delete all[id];
  return all;
}

export function element(snapshot: Snapshot, id: string): Element {
  for (const p of snapshot.document.pages) for (const e of p.elements) if (e.id === id) return e;
  throw new Error(`no element ${id}`);
}
