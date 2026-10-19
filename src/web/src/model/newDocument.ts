import type { CapturedDocumentDefaults, DiagramDocument } from '../contracts/generated';
import { toPoints } from './validate';
import { DOCUMENT_DEFAULTS_KEY, serializeDocumentDefaults } from './documentDefaults';

export interface PageSize { widthPt: number; heightPt: number }

/** A new, empty document: one A4 landscape page, one layer, 5 mm grid with snapping. */
export function newDocument(newId: () => string = () => crypto.randomUUID(), size: PageSize = { widthPt: toPoints(297, 'mm'), heightPt: toPoints(210, 'mm') }, defaults?: CapturedDocumentDefaults): DiagramDocument {
  if (![size.widthPt, size.heightPt].every(n => Number.isFinite(n) && n > 0 && n <= 14400)) throw new RangeError('Invalid page dimensions');
  return {
    schemaVersion: 1,
    id: newId(),
    title: 'Untitled',
    revision: 0,
    assets: [],
    metadata: defaults === undefined ? {} : { [DOCUMENT_DEFAULTS_KEY]: serializeDocumentDefaults(defaults) },
    pages: [{
      id: newId(),
      name: 'Page 1',
      widthPt: size.widthPt,
      heightPt: size.heightPt,
      background: { kind: 'solid', colour: '#ffffff', opacity: 1 },
      grid: defaults === undefined ? { visible: true, spacingPt: toPoints(5, 'mm'), snap: true } : { ...defaults.grid },
      guides: [],
      layers: [{ id: newId(), name: 'Layer 1', visible: true, locked: false, printable: true, snap: true, glue: true }],
      elements: [],
    }],
  };
}
