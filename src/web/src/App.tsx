import { useEffect } from 'react';
import type { EditorModel } from './editor/EditorModel';
import { Drawer } from './components/Drawer';
import { Inspector } from './components/Inspector';
import { PageCanvas } from './components/PageCanvas';
import { ShapesPanel } from './components/ShapesPanel';
import { StatusBar } from './components/StatusBar';
import { Toolbar } from './components/Toolbar';

const typing = (t: EventTarget | null) =>
  t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement || (t instanceof HTMLElement && t.isContentEditable);

/** Every shortcut calls the same model commands the buttons do, so keyboard, menu and toolbar share one path into the engine. */
function useShortcuts(model: EditorModel) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (typing(e.target)) return;
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && key === 'z' && !e.shiftKey) { e.preventDefault(); void model.undo(); }
      else if (mod && (key === 'y' || (key === 'z' && e.shiftKey))) { e.preventDefault(); void model.redo(); }
      else if (mod && key === 'g') { e.preventDefault(); void (e.shiftKey ? model.ungroup() : model.group()); }
      else if (mod && e.shiftKey && key === 'f') { e.preventDefault(); void model.reorder('front'); }
      else if (mod && e.shiftKey && key === 'b') { e.preventDefault(); void model.reorder('back'); }
      else if (mod && key === 'c') { e.preventDefault(); model.copy(); }
      else if (mod && key === 'v') { e.preventDefault(); void model.paste(); }
      else if (mod && key === 'd') { e.preventDefault(); void model.duplicate(); }
      else if (mod && key === 'a') { e.preventDefault(); model.selectAll(); }
      else if (mod && key === 's') { e.preventDefault(); void model.save(e.shiftKey); }
      else if (mod && key === 'o') { e.preventDefault(); void model.open(); }
      else if (mod && (key === '=' || key === '+')) { e.preventDefault(); model.zoomBy(1.25); }
      else if (mod && key === '-') { e.preventDefault(); model.zoomBy(0.8); }
      else if (mod && key === '0') { e.preventDefault(); model.zoomToFit(); }
      else if (e.key === 'Delete' || e.key === 'Backspace') { if (model.selection.length) { e.preventDefault(); void model.deleteSelection(); } }
      else if (e.key === 'Escape') { if (model.tool.kind !== 'select') model.setTool({ kind: 'select' }); }
      else if (e.key.startsWith('Arrow') && model.selection.length) {
        e.preventDefault();
        const step = e.shiftKey ? 10 : 1;
        void model.nudge(e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0, e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0);
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [model]);
}

export function App({ model }: { model: EditorModel }) {
  useShortcuts(model);
  return (
    <div className="app">
      <Toolbar model={model} />
      <ShapesPanel model={model} />
      <PageCanvas model={model} />
      <Inspector model={model} />
      <Drawer model={model} />
      <StatusBar model={model} />
    </div>
  );
}
