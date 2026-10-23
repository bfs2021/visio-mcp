import type { Rect } from './obstacleRoute';

/** The axis-aligned box around an element after its rotation. */
export function visualRect(el: { bounds: Rect; rotationDeg: number }): Rect {
  const { x, y, width: w, height: h } = el.bounds;
  const a = (el.rotationDeg * Math.PI) / 180, c = Math.abs(Math.cos(a)), s = Math.abs(Math.sin(a));
  const W = w * c + h * s, H = w * s + h * c;
  return { x: x + w / 2 - W / 2, y: y + h / 2 - H / 2, width: W, height: H };
}

export const BACKGROUND_SHARE = 0.2;