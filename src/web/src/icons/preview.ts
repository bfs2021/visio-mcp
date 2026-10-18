import type { Element, ShapeElement, Snapshot, TextElement } from '../contracts/generated';
import type { CatalogIcon } from './catalog';

/** Private preview scene passed only to the snapshot renderer; it never enters the store, history or live canvas. */
export function iconPreview(snapshot: Snapshot, icons: CatalogIcon[], opts: { colour?: string; strokeRatio?: number } = {}): Snapshot {
  const cols = Math.min(5, Math.max(1, icons.length)), rows = Math.max(1, Math.ceil(icons.length / cols));
  const elements: Element[] = [];
  const id = (n: number) => `00000000-0000-4000-8800-${String(n).padStart(12, '0')}`;
  const base = (n: number) => ({ id: id(n), layerIds: [], zIndex: n, locked: false, hidden: false, rotationDeg: 0, metadata: {} });
  icons.forEach((icon, i) => {
    const x = 12 + i % cols * 120, y = 12 + Math.floor(i / cols) * 112;
    elements.push({ ...base(i * 2 + 1), kind: 'shape', bounds: { x: x + 28, y, width: 64, height: 64 }, geometry: { svgPath: icon.path },
      style: { fill: { kind: 'none' }, stroke: { kind: 'solid', colour: opts.colour ?? '#334155', opacity: 1 }, strokeWidthPt: (opts.strokeRatio ?? 1 / 12) * 64, dash: 'solid', lineCap: 'round', lineJoin: 'round' } } satisfies ShapeElement);
    elements.push({ ...base(i * 2 + 2), kind: 'text', bounds: { x, y: y + 72, width: 120, height: 32 },
      text: { value: icon.name, fontFamily: 'Arial', fontSizePt: 11, bold: false, italic: false, underline: false, colour: '#334155', horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 0 } } satisfies TextElement);
  });
  const page = { ...snapshot.document.pages[0]!, widthPt: cols * 120 + 24, heightPt: rows * 112 + 24, layers: [], guides: [], elements, background: { kind: 'solid' as const, colour: '#FFFFFF', opacity: 1 }, grid: { visible: false, snap: false, spacingPt: 10 } };
  return { ...snapshot, document: { ...snapshot.document, assets: [], pages: [page] } };
}
