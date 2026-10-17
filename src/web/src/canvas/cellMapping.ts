import { HexagonShape, Point, ShapeRegistry, type AbstractCanvas2D, type CellStyle } from '@maxgraph/core';

// HexagonShape is the public path-shape base: its redrawPath is replaced below, keeping addPoints/rounding helpers.
import type { ConnectorElement, Element, ImageElement, Layer, Paint, ShapeElement, TextBlock, TextElement } from '../contracts/generated';

/** A parallelogram preset (maxGraph has none built in). */
class ParallelogramShape extends HexagonShape {
  redrawPath(c: AbstractCanvas2D, _x: number, _y: number, w: number, h: number): void {
    const dx = 0.2 * w;
    this.addPoints(c, [new Point(dx, 0), new Point(w, 0), new Point(w - dx, h), new Point(0, h)], this.isRounded, this.getBaseArcSize(), true);
  }
}

/** A rectangular callout with a tail at the bottom left. */
class CalloutShape extends HexagonShape {
  redrawPath(c: AbstractCanvas2D, _x: number, _y: number, w: number, h: number): void {
    const body = 0.8 * h;
    this.addPoints(c, [
      new Point(0, 0), new Point(w, 0), new Point(w, body), new Point(0.45 * w, body), new Point(0.2 * w, h), new Point(0.25 * w, body), new Point(0, body),
    ], this.isRounded, this.getBaseArcSize(), true);
  }
}

let registered = false;
export function registerCustomShapes(): void {
  if (registered) return;
  registered = true;
  ShapeRegistry.add('parallelogram', ParallelogramShape);
  ShapeRegistry.add('callout', CalloutShape);
}

const SHAPE_FOR_PRESET: Record<string, string | undefined> = {
  rectangle: undefined, 'rounded-rectangle': undefined, ellipse: 'ellipse', diamond: 'rhombus', triangle: 'triangle', hexagon: 'hexagon',
  parallelogram: 'parallelogram', cylinder: 'cylinder', cloud: 'cloud', callout: 'callout',
};

const DASH: Record<string, string | undefined> = { solid: undefined, dashed: '6 4', dotted: '1 3', 'dash-dot': '6 3 1 3' };
const ARROW: Record<string, string> = { none: 'none', block: 'block', open: 'open', diamond: 'diamond', oval: 'oval' };

function paint(style: CellStyle, colourKey: 'fillColor' | 'strokeColor', opacityKey: 'fillOpacity' | 'strokeOpacity', p: Paint): void {
  if (p.kind === 'none') { style[colourKey] = 'none'; return; }
  style[colourKey] = p.colour;
  style[opacityKey] = Math.round(p.opacity * 100);
}

function textStyle(style: CellStyle, t: TextBlock): void {
  style.fontFamily = t.fontFamily;
  style.fontSize = t.fontSizePt;
  style.fontColor = t.colour;
  style.fontStyle = (t.bold ? 1 : 0) | (t.italic ? 2 : 0) | (t.underline ? 4 : 0);
  style.align = t.horizontalAlign;
  style.verticalAlign = t.verticalAlign;
  style.spacing = t.paddingPt;
  if (t.wrap) style.whiteSpace = 'wrap';
}

/** Visible unless hidden or any assigned layer is hidden; locked if it or any assigned layer is locked (design D2). */
export function effectiveState(el: Element, layers: readonly Layer[]): { visible: boolean; locked: boolean } {
  const assigned = el.layerIds.map(id => layers.find(l => l.id === id)).filter((l): l is Layer => !!l);
  return { visible: !el.hidden && assigned.every(l => l.visible), locked: el.locked || assigned.some(l => l.locked) };
}

const LOCKED: CellStyle = { movable: false, resizable: false, rotatable: false, editable: false, deletable: false, bendable: false };

export function vertexStyle(el: ShapeElement | TextElement | ImageElement, locked: boolean, assetUrl: (assetId: string) => string): CellStyle {
  const style: CellStyle = { rotation: el.rotationDeg, resizable: true };
  if (el.kind === 'image') {
    style.shape = 'image';
    style.image = assetUrl(el.assetId);
    style.imageAspect = el.fit !== 'stretch' && el.preserveAspectRatio;
    style.opacity = Math.round(el.opacity * 100);
    style.strokeColor = 'none';
    style.fillColor = 'none';
  } else if (el.kind === 'text') {
    style.fillColor = 'none';
    style.strokeColor = 'none';
    textStyle(style, el.text);
  } else {
    const shape = SHAPE_FOR_PRESET[el.geometry.preset ?? 'rectangle'];
    if (shape) style.shape = shape;
    if (el.geometry.preset === 'rounded-rectangle') {
      style.rounded = true;
      style.absoluteArcSize = true;
      style.arcSize = (el.geometry.cornerRadiusPt ?? 8) * 2;
    }
    paint(style, 'fillColor', 'fillOpacity', el.style.fill);
    paint(style, 'strokeColor', 'strokeOpacity', el.style.stroke);
    style.strokeWidth = el.style.strokeWidthPt;
    const dash = DASH[el.style.dash];
    if (dash) { style.dashed = true; style.dashPattern = dash; }
    if (el.text) textStyle(style, el.text);
  }
  return locked ? { ...style, ...LOCKED } : style;
}

const PORT: Record<string, [number, number] | undefined> = { north: [0.5, 0], south: [0.5, 1], west: [0, 0.5], east: [1, 0.5] };

export function edgeStyle(c: ConnectorElement, locked: boolean): CellStyle {
  const style: CellStyle = { strokeColor: c.style.colour, strokeWidth: c.style.widthPt, startArrow: ARROW[c.style.startArrow] as CellStyle['startArrow'], endArrow: ARROW[c.style.endArrow] as CellStyle['endArrow'] };
  const dash = DASH[c.style.dash];
  if (dash) { style.dashed = true; style.dashPattern = dash; }
  if (c.route === 'orthogonal') style.edgeStyle = 'elbowEdgeStyle';
  if (c.route === 'curved') style.curved = true;
  if (c.label) textStyle(style, c.label);
  for (const [ep, x, y] of [[c.from, 'exitX', 'exitY'], [c.to, 'entryX', 'entryY']] as const) {
    const fixed = ep.glue === 'static' && ep.port ? PORT[ep.port] : undefined;
    if (fixed) { style[x] = fixed[0]; style[y] = fixed[1]; }
  }
  return locked ? { ...style, ...LOCKED } : style;
}

export function labelOf(el: Element): string {
  if (el.kind === 'shape') return el.text?.value ?? '';
  if (el.kind === 'text') return el.text.value;
  if (el.kind === 'connector') return el.label?.value ?? '';
  return '';
}
