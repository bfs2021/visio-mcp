import { useEffect, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { Ruler, RULER_SIZE } from './Ruler';
import { useModel } from './useModel';

export function PageCanvas({ model }: { model: EditorModel }) {
  useModel(model);
  const host = useRef<HTMLDivElement>(null);
  const frame = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 800, h: 600 });

  useEffect(() => {
    const el = host.current!;
    model.attachCanvas(el);
    const ro = new ResizeObserver(() => { const r = frame.current!.getBoundingClientRect(); setSize({ w: r.width - RULER_SIZE, h: r.height - RULER_SIZE }); });
    ro.observe(frame.current!);
    return () => { ro.disconnect(); model.detachCanvas(); };
  }, [model]);

  const onDrop = (e: React.DragEvent) => {
    const what = e.dataTransfer.getData('application/x-diagram-shape');
    if (!what) return;
    e.preventDefault();
    const vp = model.canvas!.getViewport();
    const rect = host.current!.getBoundingClientRect();
    void model.place(what, { x: (e.clientX - rect.left + vp.scrollLeft) / vp.scale - vp.marginPt, y: (e.clientY - rect.top + vp.scrollTop) / vp.scale - vp.marginPt });
  };

  return (
    <div className="canvas-frame" ref={frame}>
      <div className="ruler-corner" />
      <Ruler horizontal viewport={model.viewport} unit={model.unit} length={size.w} />
      <Ruler horizontal={false} viewport={model.viewport} unit={model.unit} length={size.h} />
      <div
        className="canvas-host" data-testid="canvas" ref={host}
        onDragOver={e => { if (e.dataTransfer.types.includes('application/x-diagram-shape')) e.preventDefault(); }}
        onDrop={onDrop}
      />
    </div>
  );
}
