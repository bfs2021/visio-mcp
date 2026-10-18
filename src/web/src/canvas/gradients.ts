import { SvgCanvas2D } from '@maxgraph/core';
import type { LinearGradientPaint } from '../contracts/generated';

/**
 * Multi-stop linear gradients at any angle for maxGraph, which only knows two colours and four directions. The gradient travels through the
 * style as a `gradientDirection` string starting with `diagram:`; this patches the SVG canvas to draw it as a real `linearGradient`
 * (angle, any number of stops, per-stop opacity). The angle is applied in the shape's own pixels, so 45 degrees is 45 degrees on a wide shape
 * too, which is how Visio draws it.
 */
const PREFIX = 'diagram:';

interface Spec { a: number; s: Array<[number, string, number]> }

export const gradientDirection = (p: LinearGradientPaint): string =>
  PREFIX + JSON.stringify({ a: p.angleDeg, s: p.stops.map(x => [x.offset, x.colour, x.opacity]) } satisfies Spec);

const hash = (text: string): string => {
  let h = 5381;
  for (let i = 0; i < text.length; i++) h = ((h << 5) + h + text.charCodeAt(i)) | 0;
  return (h >>> 0).toString(36);
};

const split = (direction: string): { spec: Spec; w: number; h: number } | null => {
  if (!direction.startsWith(PREFIX)) return null;
  const at = direction.lastIndexOf('|');
  const [w, h] = direction.slice(at + 1).split('x').map(Number);
  return { spec: JSON.parse(direction.slice(PREFIX.length, at)) as Spec, w: w || 1, h: h || 1 };
};

let patched = false;
export function patchGradients(): void {
  if (patched) return;
  patched = true;
  const proto = SvgCanvas2D.prototype as unknown as Record<string, (...args: unknown[]) => unknown>;
  const setGradient = proto.setGradient!, createGradientId = proto.createGradientId!, createSvgGradient = proto.createSvgGradient!;

  // the size of the shape is only known here, and the angle needs it
  proto.setGradient = function (this: unknown, c1, c2, x, y, w, h, direction, a1, a2) {
    const d = typeof direction === 'string' && direction.startsWith(PREFIX) ? `${direction}|${Math.round(Number(w))}x${Math.round(Number(h))}` : direction;
    return setGradient.call(this, c1, c2, x, y, w, h, d, a1, a2);
  };
  proto.createGradientId = function (this: unknown, start, end, alpha1, alpha2, direction) {
    return typeof direction === 'string' && direction.startsWith(PREFIX) ? `mx-gradient-d-${hash(direction)}` : createGradientId.call(this, start, end, alpha1, alpha2, direction);
  };
  proto.createSvgGradient = function (this: { createElement(name: string): Element }, start, end, alpha1, alpha2, direction) {
    const parsed = typeof direction === 'string' ? split(direction) : null;
    if (!parsed) return createSvgGradient.call(this, start, end, alpha1, alpha2, direction);
    const { spec, w, h } = parsed;
    const rad = (spec.a * Math.PI) / 180, dx = Math.cos(rad), dy = Math.sin(rad);
    // in the shape's unit square a linear gradient P1 to P2 has isolines perpendicular to P2 - P1 in that square. Pixel angle theta needs the gradient vector
    // g = (w dx, h dy) / L, so P2 - P1 = g / |g|^2, with L the extent of the box along the direction (corner to corner).
    const L = Math.abs(w * dx) + Math.abs(h * dy) || 1;
    const gx = (w * dx) / L, gy = (h * dy) / L, n2 = gx * gx + gy * gy || 1;
    const vx = gx / n2, vy = gy / n2;
    const gradient = this.createElement('linearGradient');
    const pct = (v: number) => `${Math.round(v * 10000) / 100}%`;
    gradient.setAttribute('x1', pct(0.5 - vx / 2)); gradient.setAttribute('y1', pct(0.5 - vy / 2));
    gradient.setAttribute('x2', pct(0.5 + vx / 2)); gradient.setAttribute('y2', pct(0.5 + vy / 2));
    for (const [offset, colour, opacity] of spec.s) {
      const stop = this.createElement('stop');
      stop.setAttribute('offset', pct(offset));
      stop.setAttribute('style', `stop-color:${colour}${opacity < 1 ? `;stop-opacity:${opacity}` : ''}`);
      gradient.appendChild(stop);
    }
    return gradient;
  };
}
