import { useEffect, useMemo, useRef, useState } from 'react';
import type { ConnectorElement, Operation } from '../contracts/generated';
import type { EditorModel } from '../editor/EditorModel';
import type { GestureToken } from '../canvas/GestureController';
import { planRoutes } from '../layout/routePlanner';
import { moveSegment, segmentsOf, type RouteSegment } from '../layout/routeSegments';
import { snapCandidates, snapRun, SNAP_PX, type RunSnap } from '../layout/segmentSnap';
import { RULER_SIZE } from './Ruler';

const HIT_PX = 16;
const HANDLE_PX = 8;

interface Drag { token: GestureToken; segment: RouteSegment; startClient: number; held: RunSnap | undefined }

/**
 * A midpoint handle on each inner run of the selected orthogonal connector (R21). Dragging a handle slides the run sideways,
 * with a live preview computed by the same function the command uses, so what you see is what is committed. One gesture is one
 * undoable edit; Escape puts the run back. Arrow keys on a focused handle nudge it the same way.
 */
export function SegmentHandles({ model }: { model: EditorModel }) {
  const vp = model.viewport;
  const page = model.currentPage;
  const el = model.selection.length === 1 ? page.elements.find(e => e.id === model.selection[0]) : undefined;
  const connector = el?.kind === 'connector' && el.route === 'orthogonal' ? (el as ConnectorElement) : undefined;
  const routes = useMemo(
    () => planRoutes({ elements: page.elements, layers: page.layers, pageArea: page.widthPt * page.heightPt }).routes,
    [page, model.snapshot.revision], // eslint-disable-line react-hooks/exhaustive-deps
  );
  const drag = useRef<Drag | null>(null);
  const [preview, setPreview] = useState<{ points: { x: number; y: number }[]; kind?: string; blocked?: string } | null>(null);
  const [hover, setHover] = useState<number | null>(null);

  useEffect(() => {
    const escape = (e: KeyboardEvent) => {
      const d = drag.current;
      if (e.key !== 'Escape' || !d) return;
      drag.current = null; model.controller.cancel(d.token); setPreview(null);
    };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
  }, [model]);

  const drawn = connector ? routes.get(connector.id)?.points : undefined;
  if (!vp || !connector || !drawn) return null;

  const sx = (x: number) => (x + vp.marginPt) * vp.scale - vp.scrollLeft;
  const sy = (y: number) => (y + vp.marginPt) * vp.scale - vp.scrollTop;
  const path = (pts: readonly { x: number; y: number }[]) => pts.map((p, i) => `${i ? 'L' : 'M'}${sx(p.x)},${sy(p.y)}`).join(' ');
  const others = new Map([...routes].map(([id, r]) => [id, r.points] as const));
  const op = (segment: RouteSegment, to: number): Operation => ({ op: 'moveSegment', target: connector.id, segment: segment.index, to: Math.round(to * 1000) / 1000 });

  const cancel = () => { const d = drag.current; if (!d) return; drag.current = null; model.controller.cancel(d.token); setPreview(null); };
  const down = (seg: RouteSegment) => (e: React.PointerEvent<SVGElement>) => {
    if (e.button !== 0 || model.controller.isActive()) return;
    e.preventDefault(); e.stopPropagation(); e.currentTarget.setPointerCapture(e.pointerId);
    drag.current = { token: model.controller.begin('bend', model.engine.scope(), [connector.id]), segment: seg, startClient: seg.axis === 'h' ? e.clientY : e.clientX, held: undefined };
  };
  const move = (e: React.PointerEvent<SVGElement>) => {
    const d = drag.current; if (!d) return;
    const here = d.segment.axis === 'h' ? e.clientY : e.clientX;
    const raw = d.segment.coord + (here - d.startClient) / vp.scale;
    const reach = SNAP_PX / vp.scale;
    const snapped = e.altKey ? { value: raw } : snapRun(raw, snapCandidates(page, connector, d.segment.axis, others, raw, reach * 2), reach, d.held);
    d.held = snapped;
    const moved = moveSegment(drawn, d.segment.index, snapped.value);
    const planned = model.controller.preview(d.token, moved.ok ? [op(d.segment, snapped.value)] : []);
    setPreview({ points: moved.ok ? moved.points : drawn, ...(snapped.kind ? { kind: snapped.kind } : {}), ...(!planned.ok ? { blocked: planned.error.message } : {}) });
  };
  const up = async (e: React.PointerEvent<SVGElement>) => {
    const d = drag.current; if (!d) return;
    drag.current = null;
    e.currentTarget.releasePointerCapture?.(e.pointerId);
    const blocked = preview?.blocked;
    setPreview(null);
    if (blocked) { model.controller.cancel(d.token); model.say(blocked, 'warning'); return; }
    const r = await model.controller.commit(d.token);
    if (!r.ok && r.error.code !== 'revision_conflict') model.say(r.error.message, 'warning');
  };
  const key = (seg: RouteSegment) => (e: React.KeyboardEvent<SVGElement>) => {
    if (e.key === 'Escape') { cancel(); return; }
    const sign = seg.axis === 'h' ? ({ ArrowUp: -1, ArrowDown: 1 } as Record<string, number>)[e.key] : ({ ArrowLeft: -1, ArrowRight: 1 } as Record<string, number>)[e.key];
    if (sign === undefined) return;
    e.preventDefault(); e.stopPropagation();
    const grid = page.grid.snap ? page.grid.spacingPt : 10;
    void model.apply('nudge', [{ op: 'moveSegment', target: connector.id, segment: seg.index, by: sign * (e.shiftKey ? grid : 1) }]);
  };

  const segs = segmentsOf(drawn).filter(s => s.movable && s.length > 0);
  return (
    <div style={{ position: 'absolute', left: RULER_SIZE, top: RULER_SIZE, right: 0, bottom: 0, pointerEvents: 'none', overflow: 'hidden', zIndex: 5 }} data-testid="segment-overlay">
      <svg width="100%" height="100%" style={{ position: 'absolute', inset: 0, overflow: 'visible' }}>
        {preview && <path d={path(preview.points)} fill="none" stroke={preview.blocked ? '#dc2626' : '#0284c7'} strokeWidth={2} strokeDasharray="6 3" data-testid="segment-preview" />}
        {hover !== null && !preview && (() => { const s = segs.find(x => x.index === hover); return s ? <line x1={sx(s.from.x)} y1={sy(s.from.y)} x2={sx(s.to.x)} y2={sy(s.to.y)} stroke="#0284c7" strokeWidth={3} opacity={0.6} /> : null; })()}
        {segs.map(s => {
          const cx = sx((s.from.x + s.to.x) / 2), cy = sy((s.from.y + s.to.y) / 2);
          const cursor = s.axis === 'h' ? 'ns-resize' : 'ew-resize';
          return (
            <g key={s.index} data-testid={`segment-handle-${s.index}`} role="slider" tabIndex={0} aria-label={`Move ${s.axis === 'h' ? 'horizontal' : 'vertical'} run ${s.index}`}
              aria-orientation={s.axis === 'h' ? 'vertical' : 'horizontal'} aria-valuenow={Math.round(s.coord * 100) / 100}
              style={{ pointerEvents: 'auto', cursor, outline: 'none' }}
              onPointerDown={down(s)} onPointerMove={move} onPointerUp={e => void up(e)} onPointerCancel={cancel} onLostPointerCapture={cancel}
              onPointerEnter={() => setHover(s.index)} onPointerLeave={() => setHover(null)} onFocus={() => setHover(s.index)} onBlur={() => setHover(null)} onKeyDown={key(s)}>
              <rect x={cx - HIT_PX / 2} y={cy - HIT_PX / 2} width={HIT_PX} height={HIT_PX} fill="transparent" />
              <rect x={cx - HANDLE_PX / 2} y={cy - HANDLE_PX / 2} width={HANDLE_PX} height={HANDLE_PX} rx={2} fill="#fff" stroke="#0284c7" strokeWidth={2} />
            </g>
          );
        })}
      </svg>
    </div>
  );
}
