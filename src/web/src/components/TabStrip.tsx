import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';
import { useWorkspace } from './useWorkspace';

type MenuTarget = { kind: 'tab'; tab: EditorModel; x: number; y: number } | { kind: 'list'; x: number; y: number };

/**
 * One tab per open document: name, a dot while it has unsaved work, a close button. Tabs can be dragged (or moved with Ctrl+Shift+Arrow) to
 * reorder, have a right-click menu, and scroll when there are too many to fit, with a list of all of them. Roles and keys follow the ARIA tab pattern.
 */
export function TabStrip({ ws }: { ws: WorkspaceModel }) {
  useWorkspace(ws);
  const scroller = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState(false);
  const [menu, setMenu] = useState<MenuTarget | null>(null);
  const drag = useRef<{ tab: EditorModel; startX: number; moved: boolean } | null>(null);
  const suppressClick = useRef(false);
  const tabs = ws.tabs;
  const active = ws.active;

  const tabElements = () => Array.from(scroller.current?.querySelectorAll<HTMLElement>('[data-tab-id]') ?? []);
  const focusTab = (tab: EditorModel) => requestAnimationFrame(() => tabElements().find(el => el.dataset.tabId === tab.tabId)?.focus());

  // Is there more than fits? Then the arrows and the list appear; the active tab is kept in view.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const measure = () => setOverflow(el.scrollWidth > el.clientWidth + 1);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [tabs.length, ws.agentDocs.length]);
  useEffect(() => { tabElements().find(el => el.dataset.tabId === active.tabId)?.scrollIntoView({ inline: 'nearest', block: 'nearest' }); }, [active.tabId, tabs.length]);

  const scrollBy = (dx: number) => scroller.current?.scrollBy({ left: dx, behavior: 'smooth' });

  // ---- dragging to reorder (pointer events, so it works the same with mouse, pen and touch)
  const onPointerDown = (e: React.PointerEvent, tab: EditorModel) => {
    if (e.button !== 0 || (e.target as HTMLElement).closest('.tab-close')) return;
    drag.current = { tab, startX: e.clientX, moved: false };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  };
  const onPointerMove = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d) return;
    if (!d.moved && Math.abs(e.clientX - d.startX) < 5) return;
    d.moved = true;
    // the tab goes where the pointer is: the first tab whose middle is to the right of it
    const els = tabElements().filter(el => el.dataset.tabId !== d.tab.tabId);
    let index = els.findIndex(el => { const r = el.getBoundingClientRect(); return e.clientX < r.left + r.width / 2; });
    if (index < 0) index = els.length;
    if (ws.tabs.indexOf(d.tab) !== index) ws.moveTab(d.tab, index);
  };
  const endDrag = () => {
    if (drag.current?.moved) { suppressClick.current = true; setTimeout(() => { suppressClick.current = false; }, 0); }
    drag.current = null;
  };

  const onKeyDown = (e: React.KeyboardEvent, tab: EditorModel, i: number) => {
    const go = (n: number) => { const t = tabs[(n + tabs.length) % tabs.length]!; ws.activate(t); focusTab(t); };
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
      e.preventDefault();
      ws.moveTab(tab, Math.max(0, Math.min(tabs.length - 1, i + (e.key === 'ArrowLeft' ? -1 : 1))));
      focusTab(tab);
    } else if (e.key === 'ArrowRight') { e.preventDefault(); go(i + 1); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); go(i - 1); }
    else if (e.key === 'Home') { e.preventDefault(); go(0); }
    else if (e.key === 'End') { e.preventDefault(); go(tabs.length - 1); }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); ws.activate(tab); }
    else if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) {
      e.preventDefault();
      const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
      setMenu({ kind: 'tab', tab, x: r.left, y: r.bottom });
    }
  };

  return (
    <div className="tabstrip" data-testid="tabstrip">
      {overflow && <button type="button" className="tab-scroll" aria-label="Scroll tabs left" data-testid="tab-scroll-left" tabIndex={-1} onClick={() => scrollBy(-240)}>‹</button>}
      <div className="tabs-scroll" role="tablist" aria-label="Open documents" aria-orientation="horizontal" ref={scroller}>
        {tabs.map((t, i) => {
          const s = t.engine.status();
          const selected = t === active;
          return (
            <div key={t.tabId} data-tab-id={t.tabId} className={`tab${selected ? ' selected' : ''}${drag.current?.tab === t && drag.current.moved ? ' dragging' : ''}`} role="tab" aria-selected={selected} tabIndex={selected ? 0 : -1}
              aria-label={s.unsaved ? `${t.tabLabel}, unsaved changes` : t.tabLabel}
              data-testid="tab" data-unsaved={s.unsaved ? 'true' : 'false'} title={s.savedPath ?? 'Not saved to a file yet'}
              onPointerDown={e => onPointerDown(e, t)} onPointerMove={onPointerMove} onPointerUp={endDrag} onPointerCancel={endDrag}
              onClick={() => { if (!suppressClick.current) ws.activate(t); }}
              onAuxClick={e => { if (e.button === 1) { e.preventDefault(); void ws.closeTab(t); } }}
              onContextMenu={e => { e.preventDefault(); ws.activate(t); setMenu({ kind: 'tab', tab: t, x: e.clientX, y: e.clientY }); }}
              onKeyDown={e => onKeyDown(e, t, i)}>
              {s.unsaved && <span className="tab-dot" aria-hidden="true" data-testid="tab-dot">●</span>}
              <span className="tab-name" data-testid="tab-name">{t.tabLabel}</span>
              <button type="button" className="tab-close" tabIndex={-1} aria-label={`Close ${t.tabLabel}`} data-testid="tab-close"
                onClick={e => { e.stopPropagation(); void ws.closeTab(t); }}>×</button>
            </div>
          );
        })}
        {ws.agentDocs.map(d => (
          <div key={d.documentId} className="tab agent" role="tab" aria-selected="false" tabIndex={0} data-testid="agent-tab"
            aria-label={`${d.title ?? 'Untitled'}, made in the background`}
            title="Made in the background by an agent. Click to open it here."
            onClick={() => void ws.showAgentDocument(d.documentId)}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); void ws.showAgentDocument(d.documentId); } }}>
            <span className="tab-dot" aria-hidden="true">✦</span>
            <span className="tab-name" data-testid="agent-tab-name">{d.title ?? 'Untitled'}</span>
            <button type="button" className="tab-close" tabIndex={-1} data-testid="agent-tab-close"
              aria-label={ws.armedDiscard === d.documentId ? `Discard ${d.title ?? 'document'} for good` : `Close ${d.title ?? 'document'}`}
              onClick={e => { e.stopPropagation(); void ws.discardAgentDocument(d.documentId); }}>{ws.armedDiscard === d.documentId ? 'Discard?' : '×'}</button>
          </div>
        ))}
      </div>
      {overflow && <button type="button" className="tab-scroll" aria-label="Scroll tabs right" data-testid="tab-scroll-right" tabIndex={-1} onClick={() => scrollBy(240)}>›</button>}
      {overflow && (
        <button type="button" className="tab-scroll" aria-label="List all documents" aria-haspopup="menu" title="All open documents" data-testid="tab-list"
          onClick={e => { const r = e.currentTarget.getBoundingClientRect(); setMenu({ kind: 'list', x: r.left, y: r.bottom }); }}>▾</button>
      )}
      <button type="button" className="tab-new" aria-label="New document" title="New document (Ctrl+N)" data-testid="tab-new" onClick={() => ws.newTab()}>+</button>
      {ws.notice && <span className="tab-notice" role="status" data-testid="tab-notice">{ws.notice}</span>}
      {ws.preferencesDiagnostic && <div className="tab-banner" role="status" data-testid="preferences-diagnostic">{ws.preferencesDiagnostic}</div>}
      {ws.banner && (
        <div className="tab-banner" role="status" data-testid="restore-banner" title={ws.banner.lines.join(' ')}>
          <span>{ws.banner.text}</span>
          {ws.banner.lines.length > 0 && <span className="muted"> {ws.banner.lines.join(' ')}</span>}
          <button type="button" aria-label="Dismiss" data-testid="restore-banner-dismiss" onClick={() => ws.dismissBanner()}>×</button>
        </div>
      )}
      <span className="sr-only" role="status" data-testid="tab-live">{ws.liveMessage}</span>
      {menu && <TabMenu ws={ws} target={menu} onClose={() => { setMenu(null); }} />}
    </div>
  );
}

interface Item { id: string; label: string; disabled?: boolean; checked?: boolean; run: () => void }

/** The menu for one tab, or the list of every tab. Arrow keys move, Enter chooses, Escape closes; it closes when the pointer goes elsewhere. */
function TabMenu({ ws, target, onClose }: { ws: WorkspaceModel; target: MenuTarget; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const away = (e: Event) => { if (!ref.current?.contains(e.target as Node)) onClose(); };
    document.addEventListener('mousedown', away, true);
    ref.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
    return () => document.removeEventListener('mousedown', away, true);
  }, [onClose]);

  const items: Item[] = target.kind === 'list'
    ? ws.tabs.map(t => ({ id: t.tabId, label: `${t.engine.status().unsaved ? '● ' : ''}${t.tabLabel}`, checked: t === ws.active, run: () => ws.activate(t) }))
    : (() => {
        const tab = target.tab;
        const i = ws.tabs.indexOf(tab);
        const path = tab.engine.status().savedPath;
        return [
          { id: 'close', label: 'Close', run: () => void ws.closeTab(tab) },
          { id: 'close-others', label: 'Close others', disabled: ws.tabs.length < 2, run: () => void ws.closeMany(ws.tabs.filter(t => t !== tab)) },
          { id: 'close-right', label: 'Close to the right', disabled: i >= ws.tabs.length - 1, run: () => void ws.closeMany(ws.tabs.slice(i + 1)) },
          { id: 'copy-path', label: 'Copy path', disabled: !path, run: () => void ws.copyPath(tab) },
          { id: 'reveal', label: 'Show in Explorer', disabled: !path, run: () => void ws.reveal(tab) },
        ];
      })();

  const move = (e: React.KeyboardEvent) => {
    const buttons = Array.from(ref.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? []);
    const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); buttons[(at + 1) % buttons.length]?.focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); buttons[(at - 1 + buttons.length) % buttons.length]?.focus(); }
    else if (e.key === 'Escape') { e.preventDefault(); onClose(); }
  };
  return (
    <div className="context-menu tab-menu" role="menu" data-testid={target.kind === 'list' ? 'tab-list-menu' : 'tab-menu'} ref={ref} onKeyDown={move}
      style={{ left: Math.min(target.x, window.innerWidth - 240), top: target.y }}>
      {items.map(it => (
        <button key={it.id} type="button" role={target.kind === 'list' ? 'menuitemradio' : 'menuitem'} aria-checked={target.kind === 'list' ? !!it.checked : undefined}
          data-testid={`tabmenu-${it.id}`} disabled={it.disabled} onClick={() => { onClose(); it.run(); }}>
          <span>{it.label}</span><kbd>{it.checked ? '✓' : ''}</kbd>
        </button>
      ))}
    </div>
  );
}
