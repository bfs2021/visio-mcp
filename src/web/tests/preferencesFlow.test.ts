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
it('an initialized agent refreshes host defaults for its next drawing without editing an older one', async () => {
 const service = memoryPreferencesService();
 const app = startAgentApp(undefined, { preferencesService: service });
 const create = async (id: string) => app.router.dispatch({ protocolVersion: 1, kind: 'request', requestId: id, method: 'app.createDocument', params: {} });
 const first = await create('first'); expect(first.error).toBeUndefined();
 const oldDoc = app.workspace.documents[0]!.engine.store.snapshot().document;
 const current = await service.get(); if (!current.ok) throw new Error('get failed');
 const changed = current.value.preferences; changed.grid.spacingPt = 91; changed.font.family = 'Consolas';
 await service.save(changed, current.value.versionTag);
 const second = await create('second'); expect(second.error).toBeUndefined();
 expect(app.workspace.documents[1]!.engine.store.snapshot().document.pages[0]!.grid.spacingPt).toBe(91);
 expect(app.workspace.documents[1]!.engine.store.snapshot().document.metadata['editor.defaults.v1']).toContain('Consolas');
 expect(app.workspace.documents[0]!.engine.store.snapshot().document).toEqual(oldDoc);
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
import type { PreferencesService } from '../src/bridge/initializePreferences';
import { newDocument } from '../src/model/newDocument';
import { PreferencesModel } from '../src/preferences/PreferencesModel';
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
it('keeps draft on failed writes and conflicts, then reloads disk explicitly', async () => {
 const service = memoryPreferencesService();
 const model = new PreferencesModel(defaultPreferences(), 'memory:0', null, service);
 await model.open();
 model.edit({ theme: 'dark', font: { family: 'Consolas', sizePt: 15 } });
 const competing = defaultPreferences(); competing.units = 'in';
 await service.save(competing, 'memory:0');
 const conflict = await model.save();
 expect(conflict.ok).toBe(false);
 expect(model.draft?.theme).toBe('dark');
 expect(model.current.theme).toBe('light');
 expect(model.conflicted).toBe(true);
 await model.reload();
 expect(model.draft?.units).toBe('in');
 expect(model.draft?.theme).toBe('light');
 expect(model.conflicted).toBe(false);
 model.edit({ font: { family: '   ', sizePt: 12 } });
 expect((await model.save()).ok).toBe(false);
 expect(model.errors.font).toBeTruthy();
 model.resetDraft();
 expect(model.draft).toEqual(defaultPreferences());
 expect(model.current.units).toBe('in');
 expect((await model.save()).ok).toBe(true);
 expect(model.draft).toBeNull();
});

it('cancel drops edits without saving and successful save applies once', async () => {
 const service = memoryPreferencesService();
 const model = new PreferencesModel(defaultPreferences(), 'memory:0', null, service);
 let applied = 0; model.subscribe(() => applied++);
 await model.open(); model.edit({ units: 'cm' }); model.cancel();
 expect(model.current.units).toBe('mm');
 await model.open(); model.edit({ units: 'in' });
 expect((await model.save()).ok).toBe(true);
 expect(model.current.units).toBe('in');
 expect(applied).toBeGreaterThan(0);
 expect((await service.get())).toMatchObject({ ok: true, value: { preferences: { units: 'in' } } });
});
it('transport failures retain the open draft and expose an error for retry', async () => {
 const underlying = memoryPreferencesService();
 let breakRead = false, breakWrite = false;
 const service: PreferencesService = {
  get: () => breakRead ? Promise.reject(new Error('read disconnected')) : underlying.get(),
  save: (preferences, tag, reset) => breakWrite ? Promise.reject(new Error('write disconnected')) : underlying.save(preferences, tag, reset),
 };
 const model = new PreferencesModel(defaultPreferences(), 'memory:0', null, service);
 await model.open(); model.edit({ theme: 'dark' });
 breakWrite = true;
 expect((await model.save()).ok).toBe(false);
 expect(model.draft?.theme).toBe('dark');
 expect(model.errors.form).toContain('write disconnected');
 expect(model.busy).toBe(false);
 breakRead = true;
 await model.reload();
 expect(model.draft?.theme).toBe('dark');
 expect(model.errors.form).toContain('read disconnected');
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
