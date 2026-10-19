import { expect, it, vi } from 'vitest';
import { startApp } from '../src/bridge/bootstrap';
import { defaultPreferences } from '../src/model/preferences';
import { WorkspaceModel } from '../src/workspace/WorkspaceModel';
import { EditorModel } from '../src/editor/EditorModel';
it('captures preferences for startup, plus, final-tab fallback and New picker', async () => {
 const p = defaultPreferences(); p.grid.spacingPt = 42;
 const app = startApp(undefined, null, { initialPreferences: p });
 const ws = new WorkspaceModel(app, new EditorModel(app.engine));
 expect(app.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(42);
 expect(ws.newTab()!.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(42);
 await ws.closeNow(ws.active, true); await ws.closeNow(ws.active, true);
 expect(ws.active.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(42);
 ws.active.newDocument.begin();
 app.preferences.apply({ ...p, grid: { ...p.grid, spacingPt: 99 } });
 await ws.active.newDocument.choose({ widthPt: 612, heightPt: 792 });
 expect(ws.active.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(42);
 expect(ws.newTab()!.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(99);
});

import { startAgentApp } from '../src/bridge/agentApp';
it('agent registry creates drawings with the same captured settings', async () => {
 const p = defaultPreferences(); p.grid.spacingPt = 42;
 const app = startAgentApp(undefined, { initialPreferences: p });
 const r = await app.router.dispatch({ protocolVersion: 1, kind: 'request', requestId: 'create', method: 'app.createDocument', params: {} });
 expect(r.error).toBeUndefined();
 expect(app.workspace.documents[0]!.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(42);
});

import { initializePreferences } from '../src/bridge/initializePreferences';
import type { WebViewLike } from '../src/bridge/BridgeClient';
it('waits for host preferences before ready and reuses one bridge listener', async () => {
 const messages: any[] = []; const listeners: Array<(event: { data: unknown }) => void> = [];
 const webview: WebViewLike = { postMessage: m => messages.push(m), addEventListener: (_t, l) => listeners.push(l) };
 let started = false;
 const pending = initializePreferences(webview).then(initial => { started = true; return startApp(webview, null, initial); });
 await Promise.resolve();
 expect(started).toBe(false); expect(messages).toHaveLength(1); expect(messages[0].method).toBe('host.preferences.get');
 const p = defaultPreferences(); p.grid.spacingPt = 73;
 listeners[0]!({ data: { protocolVersion: 1, kind: 'response', requestId: messages[0].requestId, result: { preferences: p, versionTag: 'test' } } });
 const app = await pending;
 expect(listeners).toHaveLength(1);
 expect(messages.filter(m => m.kind === 'event').map(m => m.method)).toEqual(['editor.ready']);
 expect(app.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(73);
});
it('read errors start with safe defaults and a nonblocking diagnostic', async () => {
 let listener: (event: { data: unknown }) => void = () => {};
 const webview: WebViewLike = { addEventListener: (_t, l) => { listener = l; }, postMessage: (m: any) => queueMicrotask(() => listener({ data: { protocolVersion: 1, kind: 'response', requestId: m.requestId, error: { code: 'io_error', message: 'Cannot read settings', retryable: false } } })) };
 const initial = await initializePreferences(webview);
 expect(initial.preferences.current).toEqual(defaultPreferences());
 expect(initial.preferences.diagnostic).toContain('Cannot read settings');
});

import { memoryPreferencesService } from '../src/bridge/initializePreferences';
import { newDocument } from '../src/model/newDocument';
it('browser settings validate, detect conflicts and remain isolated in memory', async () => {
 const service = memoryPreferencesService(), other = memoryPreferencesService();
 const first = await service.get(); if (!first.ok) throw new Error('get failed');
 const p = first.value.preferences; p.font.family = 'Consolas';
 const saved = await service.save(p, first.value.versionTag); expect(saved.ok).toBe(true);
 const conflict = await service.save(p, first.value.versionTag);
 expect(conflict).toMatchObject({ ok: false, error: { code: 'revision_conflict', details: { reason: 'preferences_changed' } } });
 expect(await other.get()).toMatchObject({ ok: true, value: { preferences: defaultPreferences() } });
 expect((await service.save({ ...p, font: { ...p.font, sizePt: 0 } }, 'memory:1')).ok).toBe(false);
});
it('opening authored or legacy documents preserves their exact content', () => {
 const p = defaultPreferences(); p.grid.spacingPt = 42;
 const app = startApp(undefined, null, { initialPreferences: p });
 const legacy = newDocument();
 const opened = app.openDocument({ document: { document: legacy } });
 expect(opened.engine.store.snapshot().document).toEqual(legacy);
 expect(opened.engine.revision).toBe(0);
});

it('a silent host times out to defaults without waiting for editor readiness', async () => {
 vi.useFakeTimers();
 try {
  const sent: any[] = [];
  const webview: WebViewLike = { addEventListener: () => {}, postMessage: message => sent.push(message) };
  const initializing = initializePreferences(webview);
  await vi.advanceTimersByTimeAsync(15_000);
  const initial = await initializing;
  expect(initial.preferences.current).toEqual(defaultPreferences());
  expect(initial.preferences.diagnostic).toContain('host did not answer');
  const app = startApp(webview, null, initial);
  expect(app.engine.revision).toBe(0);
  expect(sent.filter(message => message.kind === 'event').map(message => message.method)).toEqual(['editor.ready']);
 } finally { vi.useRealTimers(); }
});
