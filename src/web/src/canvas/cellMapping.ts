import { HexagonShape, PerimeterRegistry, Point, ShapeRegistry, type AbstractCanvas2D, type CellStyle, type PerimeterFunction } from '@maxgraph/core';

// HexagonShape is the public path-shape base: its redrawPath is replaced below, keeping addPoints/rounding helpers.
import type { ConnectorElement, Element, ImageElement, Layer, Paint, ShapeElement, TextBlock, TextElement } from '../contracts/generated';
import { parsePath } from '../geometry/svgPath';
import { gradientDirection, patchGradients } from './gradients';
import { arcParameters, outlineOf, outlinePolygon, OUTLINE_PRESETS, type OutlinePreset } from '../geometry/presetOutlines';

class CustomPathShape extends HexagonShape {
  redrawPath(c: AbstractCanvas2D, _x: number, _y: number, w: number, h: number): void {
    const style = this.style as Record<string, unknown> | undefined;
    const parsed = parsePath(String(style?.svgPath ?? ''));
    c.setLineCap(String(style?.lineCap ?? 'butt')); c.setLineJoin(String(style?.lineJoin ?? 'miter'));
    if (!parsed.ok) { c.setStrokeColor('#EF4444'); c.moveTo(0, 0); c.lineTo(w, h); c.moveTo(w, 0); c.lineTo(0, h); return; }
    for (const step of parsed.value) {
      if (step.op === 'Z') c.close();
      else if (step.op === 'M') c.moveTo(step.x * w / 100, step.y * h / 100);
      else c.lineTo(step.x * w / 100, step.y * h / 100);
    }
  }
}

/**
 * The flowchart presets with curved edges (terminator, stored data, document, lined document): drawn from their outline rows with true circular
 * arcs, so they match the Visio geometry the exporter writes. A second, open section (the line in a lined document) is stroked on its own.
 */
function outlineShape(preset: OutlinePreset) {
  return class extends HexagonShape {
    redrawPath(c: AbstractCanvas2D, _x: number, _y: number, w: number, h: number): void {
      const sections = outlineOf(preset, w, h);
      sections.forEach((section, index) => {
        if (index > 0) { c.fillAndStroke(); c.begin(); }
        let x = 0, y = 0;
        for (const r of section.rows) {
          if (r.op === 'A') {
            const a = arcParameters(x, y, r.x, r.y, r.bulge);
            if (a) c.arcTo(a.radius, a.radius, 0, a.large, a.sweep, r.x, r.y); else c.lineTo(r.x, r.y);
          } else if (r.op === 'M') c.moveTo(r.x, r.y);
          else c.lineTo(r.x, r.y);
          x = r.x; y = r.y;
        }
        if (!section.open) c.close();
      });
    }
  };
}

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

/**
 * Perimeter for a polygon given in unit (0..1) coordinates of the unrotated frame. maxGraph rotates the approach point into the
 * shape's frame before calling this, so the connector meets the drawn outline rather than the frame's bounding rectangle.
 */
function polygonPerimeter(shape: ReadonlyArray<readonly [number, number]> | ((w: number, h: number) => ReadonlyArray<readonly [number, number]>)): PerimeterFunction {
  return (bounds, _vertex, next) => {
    const unit = typeof shape === 'function' ? shape(bounds.width, bounds.height) : shape;
    const cx = bounds.getCenterX(), cy = bounds.getCenterY();
    const dx = next.x - cx, dy = next.y - cy;
    let best = Infinity;
    for (let i = 0; i < unit.length; i++) {
      const a = unit[i]!, b = unit[(i + 1) % unit.length]!;
      const ax = bounds.x + a[0] * bounds.width - cx, ay = bounds.y + a[1] * bounds.height - cy;
      const ex = (b[0] - a[0]) * bounds.width, ey = (b[1] - a[1]) * bounds.height;
      const det = dx * ey - dy * ex;
      if (Math.abs(det) < 1e-9) continue;
      const t = (ax * ey - ay * ex) / det, u = (ax * dy - ay * dx) / det;
      if (t > 0 && u >= 0 && u <= 1 && t < best) best = t;
    }
    // A point at the centre, or a ray that misses, falls back to the centre like maxGraph's own perimeters.
    return new Point(cx + dx * (Number.isFinite(best) ? best : 0), cy + dy * (Number.isFinite(best) ? best : 0));
  };
}

const POLYGONS: Record<string, ReadonlyArray<readonly [number, number]> | undefined> = {
  parallelogram: [[0.2, 0], [1, 0], [0.8, 1], [0, 1]],
  callout: [[0, 0], [1, 0], [1, 0.8], [0, 0.8]],
};

/** Perimeter name for a preset; presets without an entry keep the frame rectangle (cylinder, cloud, rectangles). */
const PERIMETER_FOR_PRESET: Record<string, string | undefined> = {
  ellipse: 'ellipsePerimeter', diamond: 'rhombusPerimeter', triangle: 'trianglePerimeter', hexagon: 'hexagonPerimeter',
  parallelogram: 'diagram-parallelogram', callout: 'diagram-callout',
  terminator: 'diagram-outline-terminator', 'stored-data': 'diagram-outline-stored-data', document: 'diagram-outline-document', 'lined-document': 'diagram-outline-lined-document',
};

let registered = false;
export function registerCustomShapes(): void {
  if (registered) return;
  registered = true;
  patchGradients();
  ShapeRegistry.add('parallelogram', ParallelogramShape);
  ShapeRegistry.add('callout', CalloutShape);
  ShapeRegistry.add('custom-path', CustomPathShape);
  PerimeterRegistry.add('diagram-parallelogram', polygonPerimeter(POLYGONS.parallelogram!));
  PerimeterRegistry.add('diagram-callout', polygonPerimeter(POLYGONS.callout!));
  for (const p of OUTLINE_PRESETS) {
    ShapeRegistry.add(`outline-${p}`, outlineShape(p));
    PerimeterRegistry.add(`diagram-outline-${p}`, polygonPerimeter((w, h) => outlinePolygon(p, w, h)));
  }
}

const SHAPE_FOR_PRESET: Record<string, string | undefined> = {
  rectangle: undefined, 'rounded-rectangle': undefined, ellipse: 'ellipse', diamond: 'rhombus', triangle: 'triangle', hexagon: 'hexagon',
  parallelogram: 'parallelogram', cylinder: 'cylinder', cloud: 'cloud', callout: 'callout',
  terminator: 'outline-terminator', 'stored-data': 'outline-stored-data', document: 'outline-document', 'lined-document': 'outline-lined-document',
};

const DASH: Record<string, string | undefined> = { solid: undefined, dashed: '6 4', dotted: '1 3', 'dash-dot': '6 3 1 3' };
/** maxGraph arrow sizes; the markers scale with the line width as Visio's do. */
const ARROW_SIZE: Record<string, number> = { small: 3, medium: 6, large: 9 };
const ARROW: Record<string, string> = { none: 'none', block: 'block', open: 'open', diamond: 'diamond', oval: 'oval' };

function paint(style: CellStyle, colourKey: 'fillColor' | 'strokeColor', opacityKey: 'fillOpacity' | 'strokeOpacity', p: Paint): void {
  if (p.kind === 'none') { style[colourKey] = 'none'; return; }
  if (p.kind === 'linear-gradient') {
    // maxGraph holds the first and last colour; the whole gradient (angle, every stop, per-stop opacity) rides in the direction string (see gradients.ts)
    style[colourKey] = p.stops[0]!.colour;
    if (colourKey === 'fillColor') { style.gradientColor = p.stops[p.stops.length - 1]!.colour; style.gradientDirection = gradientDirection(p) as never; }
    return;
  }
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
    const perimeter = PERIMETER_FOR_PRESET[el.geometry.preset ?? 'rectangle'];
    if (perimeter) style.perimeter = perimeter;
    if (el.geometry.svgPath !== undefined) {
      style.shape = 'custom-path';
      Object.assign(style, { svgPath: el.geometry.svgPath, lineCap: el.style.lineCap, lineJoin: el.style.lineJoin });
    }
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
  style.startSize = ARROW_SIZE[c.style.startArrowSize ?? 'medium'];
  style.endSize = ARROW_SIZE[c.style.endArrowSize ?? 'medium'];
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
