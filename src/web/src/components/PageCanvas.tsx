import { useEffect, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { ContextMenu } from './ContextMenu';
import { ObjectList } from './ObjectList';
import { Ruler, RULER_SIZE } from './Ruler';
import { useModel } from './useModel';

export function PageCanvas({ model }: { model: EditorModel }) {
  useModel(model);
  const host = useRef<HTMLDivElement>(null);
  const frame = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 800, h: 600 });
  const [menuAt, setMenuAt] = useState<{ x: number; y: number } | null>(null);

  useEffect(() => {
    const el = host.current!;
    model.attachCanvas(el);
    const ro = new ResizeObserver(() => { const r = frame.current!.getBoundingClientRect(); setSize({ w: r.width - RULER_SIZE, h: r.height - RULER_SIZE }); });
    ro.observe(frame.current!);
    return () => { ro.disconnect(); model.detachCanvas(); };
  }, [model]);

  const onDrop = (e: React.DragEvent) => {
    const what = e.dataTransfer.getData('application/x-diagram-shape');
    const picture = e.dataTransfer.getData('application/x-diagram-asset');
    if (!what && !picture) return;
    e.preventDefault();
    const vp = model.canvas!.getViewport();
    const rect = host.current!.getBoundingClientRect();
    const at = { x: (e.clientX - rect.left + vp.scrollLeft) / vp.scale - vp.marginPt, y: (e.clientY - rect.top + vp.scrollTop) / vp.scale - vp.marginPt };
    if (picture) { try { void model.placeAsset(JSON.parse(picture) as { scope: 'library' | 'document'; id: string }, at); } catch { /* not ours */ } return; }
    void model.place(what, at);
  };

  return (
    <div className="canvas-frame" ref={frame}>
      <div className="ruler-corner" />
      <Ruler horizontal viewport={model.viewport} unit={model.unit} length={size.w} />
      <Ruler horizontal={false} viewport={model.viewport} unit={model.unit} length={size.h} />
      <div
        className="canvas-host" data-testid="canvas" ref={host}
        onDragOver={e => { if (e.dataTransfer.types.includes('application/x-diagram-shape') || e.dataTransfer.types.includes('application/x-diagram-asset')) e.preventDefault(); }}
        onDrop={onDrop}
        onMouseDown={() => setTimeout(() => frame.current?.querySelector<HTMLElement>('[data-testid=object-list]')?.focus({ preventScroll: true }), 0)} // keys go to the object list after a click, so the keyboard carries on from the clicked object
        onContextMenu={e => { e.preventDefault(); setMenuAt({ x: e.clientX, y: e.clientY }); }}
      />
      <ObjectList model={model} />
      {menuAt && <ContextMenu model={model} at={menuAt} onClose={() => setMenuAt(null)} />}
    </div>
  );
}
