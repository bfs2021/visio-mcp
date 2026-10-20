import type { ViewportInfo } from '../canvas/CanvasAdapter';

export function mapIngressDropPoint(
  client: { x: number; y: number },
  rect: Pick<DOMRect, 'left' | 'right' | 'top' | 'bottom'>,
  viewport: ViewportInfo,
): { x: number; y: number } | null {
  if (!Number.isFinite(client.x) || !Number.isFinite(client.y) || !Number.isFinite(viewport.scale) || viewport.scale <= 0 ||
    !Number.isFinite(viewport.scrollLeft) || !Number.isFinite(viewport.scrollTop) || !Number.isFinite(viewport.marginPt)) return null;
  const inside = client.x >= rect.left && client.x <= rect.right && client.y >= rect.top && client.y <= rect.bottom;
  if (!inside) return { x: viewport.pageWidthPt / 2, y: viewport.pageHeightPt / 2 };
  const x = (client.x - rect.left + viewport.scrollLeft) / viewport.scale - viewport.marginPt;
  const y = (client.y - rect.top + viewport.scrollTop) / viewport.scale - viewport.marginPt;
  return Number.isFinite(x) && Number.isFinite(y) ? { x, y } : null;
}
