import { useEffect, useRef } from 'react';
import type { ViewportInfo } from '../canvas/CanvasAdapter';
import { toPoints, type Unit } from '../model/validate';

const SIZE = 22;

/** A unit-labelled ruler aligned to the page origin. `horizontal` rulers follow scrollLeft, vertical ones scrollTop. */
export function Ruler({ horizontal, viewport, unit, length }: { horizontal: boolean; viewport: ViewportInfo | null; unit: Unit; length: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const canvas = ref.current;
    if (!canvas || !viewport) return;
    const dpr = window.devicePixelRatio || 1;
    const w = horizontal ? length : SIZE, h = horizontal ? SIZE : length;
    canvas.width = Math.max(1, Math.floor(w * dpr));
    canvas.height = Math.max(1, Math.floor(h * dpr));
    canvas.style.width = `${w}px`;
    canvas.style.height = `${h}px`;
    const ctx = canvas.getContext('2d')!;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = '#f1f5f9';
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = '#475569';
    ctx.strokeStyle = '#94a3b8';
    ctx.font = '10px system-ui, sans-serif';
    ctx.textBaseline = 'top';

    const ptPerUnit = toPoints(1, unit);
    const pxPerUnit = viewport.scale * ptPerUnit;
    const steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];
    const step = steps.find(s => s * pxPerUnit >= 48) ?? 1000;
    const origin = viewport.marginPt * viewport.scale - (horizontal ? viewport.scrollLeft : viewport.scrollTop); // px of the page origin
    const first = Math.floor((0 - origin) / pxPerUnit / step) * step;
    const last = Math.ceil((length - origin) / pxPerUnit / step) * step;
    for (let v = first; v <= last + 1e-9; v += step) {
      const x = origin + v * pxPerUnit;
      ctx.beginPath();
      if (horizontal) { ctx.moveTo(x + 0.5, SIZE - 10); ctx.lineTo(x + 0.5, SIZE); } else { ctx.moveTo(SIZE - 10, x + 0.5); ctx.lineTo(SIZE, x + 0.5); }
      ctx.stroke();
      const label = Number.isInteger(v) ? String(v) : v.toFixed(1);
      if (horizontal) ctx.fillText(label, x + 3, 2);
      else { ctx.save(); ctx.translate(2, x + 3); ctx.rotate(-Math.PI / 2); ctx.translate(-14, 0); ctx.fillText(label, 0, 0); ctx.restore(); }
      for (let i = 1; i < 5; i++) {
        const m = x + (i * step * pxPerUnit) / 5;
        ctx.beginPath();
        if (horizontal) { ctx.moveTo(m + 0.5, SIZE - 4); ctx.lineTo(m + 0.5, SIZE); } else { ctx.moveTo(SIZE - 4, m + 0.5); ctx.lineTo(SIZE, m + 0.5); }
        ctx.stroke();
      }
    }
  }, [horizontal, viewport, unit, length]);
  return <canvas ref={ref} className={horizontal ? 'ruler ruler-h' : 'ruler ruler-v'} data-testid={horizontal ? 'ruler-h' : 'ruler-v'} data-unit={unit} />;
}

export const RULER_SIZE = SIZE;
