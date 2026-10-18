import { describe, expect, it } from 'vitest';
import { arcParameters, outlineOf, outlinePolygon, PRESET_PORTS, OUTLINE_PRESETS } from '../src/geometry/presetOutlines';
import { planPatch } from '../src/commands/planPatch';
import { newDocument } from '../src/model/newDocument';

const polygonArea = (pts: Array<[number, number]>) => Math.abs(pts.reduce((a, p, i) => { const q = pts[(i + 1) % pts.length]!; return a + (p[0] * q[1] - q[0] * p[1]); }, 0)) / 2;
const inside = (pts: Array<[number, number]>, x: number, y: number) => {
  let c = false;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const [xi, yi] = pts[i]!, [xj, yj] = pts[j]!;
    if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) c = !c;
  }
  return c;
};

describe('arcs', () => {
  it('a positive bulge bulges to the right of travel on screen, a negative one to the left, and the centre is on the other side', () => {
    // travelling east (y down): right is south
    const right = arcParameters(0, 0, 100, 0, 20)!;
    expect(right.cy).toBeLessThan(0); // centre north of the chord, so the arc passes south of it
    expect(right.sweep).toBe(false);
    const left = arcParameters(0, 0, 100, 0, -20)!;
    expect(left.cy).toBeGreaterThan(0);
    expect(left.sweep).toBe(true);
    expect(arcParameters(0, 0, 100, 0, 50)!.radius).toBeCloseTo(50); // a semicircle
    expect(arcParameters(0, 0, 100, 0, 50)!.large).toBe(false);
    expect(arcParameters(0, 0, 100, 0, 80)!.large).toBe(true);
    expect(arcParameters(0, 0, 0, 0, 5)).toBeNull();
  });
});

describe('flowchart outlines', () => {
  it('a terminator has semicircular ends whatever its width, and is a circle-ended pill', () => {
    const [s] = outlineOf('terminator', 120, 48);
    expect(s!.rows).toEqual([
      { op: 'M', x: 24, y: 0 }, { op: 'L', x: 96, y: 0 }, { op: 'A', x: 96, y: 48, bulge: -24 }, { op: 'L', x: 24, y: 48 }, { op: 'A', x: 24, y: 0, bulge: -24 },
    ]);
    // a tall one has its radius set by the shorter side
    const tall = outlineOf('terminator', 40, 100)[0]!.rows;
    expect(tall[0]).toEqual({ op: 'M', x: 20, y: 0 });
    const poly = outlinePolygon('terminator', 120, 48);
    expect(inside(poly, 0.5, 0.5)).toBe(true);
    expect(inside(poly, 0.01, 0.05)).toBe(false); // the corner is cut off by the round end
    expect(inside(poly, 0.99, 0.95)).toBe(false);
    expect(inside(poly, 0.02, 0.5)).toBe(true);    // but the middle of the end reaches the frame
    // the area of a stadium: rectangle plus a full circle
    const exact = 72 * 48 + Math.PI * 24 * 24;
    expect(polygonArea(poly) * 120 * 48).toBeGreaterThan(exact * 0.98);
    expect(polygonArea(poly) * 120 * 48).toBeLessThanOrEqual(exact);
  });

  it('stored data curves both ends the same way; a document has a wavy bottom; a lined document adds an open line', () => {
    const stored = outlinePolygon('stored-data', 100, 60);
    expect(inside(stored, 0.5, 0.5)).toBe(true);
    expect(inside(stored, 0.03, 0.5)).toBe(false);   // the left end is cut in by the concave edge
    expect(inside(stored, 0.97, 0.5)).toBe(true);    // the right end bulges out past the corners
    expect(inside(stored, 0.97, 0.03)).toBe(false);
    const doc = outlinePolygon('document', 100, 100);
    expect(inside(doc, 0.8, 0.95)).toBe(true);        // right half dips below the 0.86 line
    expect(inside(doc, 0.2, 0.95)).toBe(false);       // left half rises above it
    expect(inside(doc, 0.2, 0.7)).toBe(true);
    const lined = outlineOf('lined-document', 100, 100);
    expect(lined).toHaveLength(2);
    expect(lined[1]!.open).toBe(true);
    expect(outlinePolygon('lined-document', 100, 100)).toEqual(doc); // the line is not part of the outline
  });

  it('every preset has ports on its own outline, not on the frame', () => {
    for (const p of OUTLINE_PRESETS) {
      const poly = outlinePolygon(p, 120, 72);
      const ports = PRESET_PORTS[p];
      expect(ports.map(q => q.id)).toEqual(['north', 'east', 'south', 'west']);
      for (const q of ports) {
        // a port is on the outline if a point just inside is in the shape and a point just outside is not
        const cx = 0.5, cy = 0.5, dx = q.x - cx, dy = q.y - cy, n = Math.hypot(dx, dy);
        const eps = 0.03;
        expect([p, q.id, inside(poly, q.x - (dx / n) * eps, q.y - (dy / n) * eps)]).toEqual([p, q.id, true]);
        expect([p, q.id, inside(poly, q.x + (dx / n) * eps, q.y + (dy / n) * eps)]).toEqual([p, q.id, false]);
      }
    }
  });
});

describe('creating the symbols', () => {
  const create = (preset: string, ports?: unknown) => {
    const doc = newDocument(); const pageId = doc.pages[0]!.id;
    const r = planPatch(doc, [{ op: 'createShape', pageId, bounds: { x: 10, y: 10, width: 100, height: 50 }, geometry: { preset }, ...(ports ? { ports } : {}) } as never], { pageId, newId: () => crypto.randomUUID() });
    if (!r.ok) throw new Error(r.error.message);
    const el = r.value.forward.upserts.elements[0]!.element;
    return el as { kind: string; geometry: { preset: string }; ports?: Array<{ id: string }> };
  };

  it('a new shape starts with its preset ports; explicit ports win; other presets are unchanged', () => {
    for (const p of OUTLINE_PRESETS) expect(create(p).ports).toEqual(PRESET_PORTS[p]);
    expect(create('terminator', [{ id: 'only', x: 0, y: 0.5 }]).ports).toEqual([{ id: 'only', x: 0, y: 0.5 }]);
    expect(create('rectangle').ports).toBeUndefined();
  });

});
