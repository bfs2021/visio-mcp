import type { DiagramDocument } from '../contracts/generated';
import { toPoints } from './validate';

/** A new, empty document: one A4 landscape page, one layer, 5 mm grid with snapping. */
export function newDocument(newId: () => string = () => crypto.randomUUID()): DiagramDocument {
  return {
    schemaVersion: 1,
    id: newId(),
    title: 'Untitled',
    revision: 0,
    assets: [],
    metadata: {},
    pages: [{
      id: newId(),
      name: 'Page 1',
      widthPt: toPoints(297, 'mm'),
      heightPt: toPoints(210, 'mm'),
      background: { kind: 'solid', colour: '#ffffff', opacity: 1 },
      grid: { visible: true, spacingPt: toPoints(5, 'mm'), snap: true },
      guides: [],
      layers: [{ id: newId(), name: 'Layer 1', visible: true, locked: false, printable: true, snap: true, glue: true }],
      elements: [],
    }],
  };
}
