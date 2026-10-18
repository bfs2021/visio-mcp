import { useRef } from 'react';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';
import { useWorkspace } from './useWorkspace';

/** One tab per open document: name, a dot while it has unsaved work, and a close button. Roles and keys follow the ARIA tab pattern. */
export function TabStrip({ ws }: { ws: WorkspaceModel }) {
  useWorkspace(ws);
  const list = useRef<HTMLDivElement>(null);
  const tabs = ws.tabs;
  const active = ws.active;
  const focusTab = (i: number) => { (list.current?.querySelectorAll<HTMLElement>('[role=tab]')[i])?.focus(); };
  return (
    <div className="tabstrip" role="tablist" aria-label="Open documents" ref={list} data-testid="tabstrip">
      {tabs.map((t, i) => {
        const s = t.engine.status();
        const selected = t === active;
        return (
          <div key={t.tabId} className={`tab${selected ? ' selected' : ''}`} role="tab" aria-selected={selected} tabIndex={selected ? 0 : -1}
            data-testid="tab" data-unsaved={s.unsaved ? 'true' : 'false'} title={s.savedPath ?? 'Not saved to a file yet'}
            onClick={() => ws.activate(t)}
            onAuxClick={e => { if (e.button === 1) { e.preventDefault(); void ws.closeTab(t); } }}
            onKeyDown={e => {
              if (e.key === 'ArrowRight') { e.preventDefault(); const n = (i + 1) % tabs.length; ws.activate(tabs[n]!); focusTab(n); }
              else if (e.key === 'ArrowLeft') { e.preventDefault(); const n = (i - 1 + tabs.length) % tabs.length; ws.activate(tabs[n]!); focusTab(n); }
              else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); ws.activate(t); }
            }}>
            {s.unsaved && <span className="tab-dot" aria-label="unsaved changes" data-testid="tab-dot">●</span>}
            <span className="tab-name" data-testid="tab-name">{t.tabLabel}</span>
            <button type="button" className="tab-close" tabIndex={-1} aria-label={`Close ${t.tabLabel}`} data-testid="tab-close"
              onClick={e => { e.stopPropagation(); void ws.closeTab(t); }}>×</button>
          </div>
        );
      })}
      <button type="button" className="tab-new" aria-label="New document" title="New document (Ctrl+N)" data-testid="tab-new" onClick={() => ws.newTab()}>+</button>
      {ws.notice && <span className="tab-notice" role="status" data-testid="tab-notice">{ws.notice}</span>}
    </div>
  );
}
