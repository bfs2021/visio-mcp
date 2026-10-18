import type { Port } from '../contracts/generated';

/**
 * The outlines of the flowchart presets that have curved edges. Each is a few rows of moves, lines and circular arcs in the shape's own width
 * and height, so a resize stretches them. The same outlines are written to Visio as native Move/Line/Arc rows (`Diagram.Visio/GeometryMap.cs`
 * holds the matching definitions), so the editor and the exported file draw the same shape.
 *
 * An arc runs from the current point to (x, y); `bulge` is how far its middle sits from the straight chord, positive when it bulges to the
 * right of the direction of travel (as seen on screen), negative to the left. A clockwise outline bulges outward with a negative value.
 */
export type OutlineRow = { op: 'M' | 'L'; x: number; y: number } | { op: 'A'; x: number; y: number; bulge: number };
export interface OutlineSection { rows: OutlineRow[]; /** An open stroke with no fill (a line drawn inside the shape). */ open?: boolean }

export const OUTLINE_PRESETS = ['terminator', 'stored-data', 'document', 'lined-document'] as const;
export type OutlinePreset = (typeof OUTLINE_PRESETS)[number];
export const isOutlinePreset = (p: string | undefined): p is OutlinePreset => !!p && (OUTLINE_PRESETS as readonly string[]).includes(p);

/** Half the shorter side: the radius of a terminator's rounded ends, however the shape is stretched. */
const half = (w: number, h: number) => Math.min(w, h) / 2;

const documentOutline = (w: number, h: number): OutlineRow[] => [
  { op: 'M', x: 0, y: 0 }, { op: 'L', x: w, y: 0 }, { op: 'L', x: w, y: 0.86 * h },
  { op: 'A', x: 0.5 * w, y: 0.86 * h, bulge: -0.12 * h }, // the wave dips below the edge on the right…
  { op: 'A', x: 0, y: 0.86 * h, bulge: 0.12 * h },         // …and rises above it on the left
  { op: 'L', x: 0, y: 0 },
];

export function outlineOf(preset: OutlinePreset, w: number, h: number): OutlineSection[] {
  switch (preset) {
    case 'terminator': {
      const m = half(w, h);
      return [{ rows: [{ op: 'M', x: m, y: 0 }, { op: 'L', x: w - m, y: 0 }, { op: 'A', x: w - m, y: h, bulge: -m }, { op: 'L', x: m, y: h }, { op: 'A', x: m, y: 0, bulge: -m }] }];
    }
    case 'stored-data':
      return [{ rows: [{ op: 'M', x: 0, y: 0 }, { op: 'L', x: 0.9 * w, y: 0 }, { op: 'A', x: 0.9 * w, y: h, bulge: -0.1 * w }, { op: 'L', x: 0, y: h }, { op: 'A', x: 0, y: 0, bulge: 0.1 * w }] }];
    case 'document':
      return [{ rows: documentOutline(w, h) }];
    case 'lined-document':
      return [{ rows: documentOutline(w, h) }, { open: true, rows: [{ op: 'M', x: 0.12 * w, y: 0 }, { op: 'L', x: 0.12 * w, y: 0.75 * h }] }];
  }
}

/** Where connectors attach by default, as fractions of the (unrotated) shape. A new shape of the preset starts with these. */
export const PRESET_PORTS: Readonly<Record<OutlinePreset, readonly Port[]>> = {
  terminator: [{ id: 'north', x: 0.5, y: 0 }, { id: 'east', x: 1, y: 0.5 }, { id: 'south', x: 0.5, y: 1 }, { id: 'west', x: 0, y: 0.5 }],
  'stored-data': [{ id: 'north', x: 0.45, y: 0 }, { id: 'east', x: 1, y: 0.5 }, { id: 'south', x: 0.45, y: 1 }, { id: 'west', x: 0.1, y: 0.5 }],
  document: [{ id: 'north', x: 0.5, y: 0 }, { id: 'east', x: 1, y: 0.43 }, { id: 'south', x: 0.5, y: 0.86 }, { id: 'west', x: 0, y: 0.43 }],
  'lined-document': [{ id: 'north', x: 0.5, y: 0 }, { id: 'east', x: 1, y: 0.43 }, { id: 'south', x: 0.5, y: 0.86 }, { id: 'west', x: 0, y: 0.43 }],
};

/** The centre, radius and sweep of the circular arc from (x0, y0) to (x1, y1) with the given bulge, in the form SVG arcs take. */
export function arcParameters(x0: number, y0: number, x1: number, y1: number, bulge: number): { radius: number; large: boolean; sweep: boolean; cx: number; cy: number } | null {
  const chord = Math.hypot(x1 - x0, y1 - y0), s = Math.abs(bulge);
  if (chord < 1e-9 || s < 1e-9) return null;
  const radius = (chord * chord / 4 + s * s) / (2 * s);
  // (dy, -dx) points to the left of travel on screen (y down); the centre lies on the side away from the bulge, `radius - s` from the middle of the chord
  // (a negative distance for an arc longer than a semicircle, which puts the centre on the bulge side)
  const nx = (y1 - y0) / chord, ny = -(x1 - x0) / chord, k = Math.sign(bulge);
  const cx = (x0 + x1) / 2 + nx * (radius - s) * k, cy = (y0 + y1) / 2 + ny * (radius - s) * k;
  return { radius, large: s > chord / 2, sweep: bulge < 0, cx, cy };
}

/** The outline as a closed polygon of unit coordinates (arcs sampled), for the perimeter connectors meet. Open sections are not part of the outline. */
export function outlinePolygon(preset: OutlinePreset, w: number, h: number, step = 12): Array<[number, number]> {
  const pts: Array<[number, number]> = [];
  for (const section of outlineOf(preset, w, h)) {
    if (section.open) continue;
    let x = 0, y = 0;
    for (const r of section.rows) {
      if (r.op === 'A') {
        const a = arcParameters(x, y, r.x, r.y, r.bulge);
        if (a) {
          const a0 = Math.atan2(y - a.cy, x - a.cx);
          let sweep = Math.atan2(r.y - a.cy, r.x - a.cx) - a0;
          // the arc turns clockwise on screen (increasing angles with y down) for a negative bulge, counter-clockwise for a positive one
          if (a.sweep) { while (sweep < 0) sweep += 2 * Math.PI; } else { while (sweep > 0) sweep -= 2 * Math.PI; }
          const n = Math.max(4, Math.ceil(Math.abs(sweep) / (Math.PI / step)));
          for (let i = 1; i < n; i++) { const t = a0 + sweep * i / n; pts.push([(a.cx + a.radius * Math.cos(t)) / w, (a.cy + a.radius * Math.sin(t)) / h]); }
        }
        pts.push([r.x / w, r.y / h]);
      } else if (r.op === 'L' || r.op === 'M') {
        if (r.op === 'L' || pts.length === 0) pts.push([r.x / w, r.y / h]);
      }
      x = r.x; y = r.y;
    }
  }
  return pts;
}
