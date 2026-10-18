# W02a: workspace and document registry (no visible change)

Issue #40, part of #24. Design: `docs/design/tabbed-documents.md`.

## What changed

Host
- `BridgeRouter` keeps every open document (id -> scope, revision) and which one is active. `CurrentScope`/`CurrentRevision` mean the active document, so existing callers are unchanged. New: `ScopeOf(id)`, `Documents`, events `DocumentClosed` and `DocumentReplaced`.
- Page events: `editor.ready`/`editor.session` (restart; `replaces` names the document replaced, default the active one; `active:false` for background), `editor.document.opened`, `.closed`, `.activated`. `SendAsync` checks the named document and its session, so a request can no longer reach the wrong document.
- `RecoveryManager` follows one recovery session per open document (checkpoint counters, saved revision and degraded state are per session). Closing an untouched document removes its session; unsaved work stays until `DiscardSessionAsync` (Don't save). `CandidatesAsync` never offers a session that is open. `CollectGarbageAsync` asks every open document for its retained pictures, and collects nothing if one cannot answer.
- `HostServices` keeps a file path per document and moves it when a document is replaced.

Page
- `workspace/Workspace.ts`: ordered `DocumentState`s (engine, ui, announced id), active document, add/close/activate, id -> engine lookup.
- `startApp` builds the workspace with one tab, exposes `openDocument`, `closeDocument`, `activateDocument`, and announces them to the host. Requests are resolved by `documentId`; the admission gate's user-gesture hold applies only to the active document.

## Tests added
- Host: `MultiDocumentTests` (8): addressing by id, activation/close, replacement, one session and journal per document, close then Don't save, open documents never offered, background-only picture survives collection, an unanswering document blocks collection.
- Page: `workspace.test.ts` (6): announcements, routing by id, background edit isolation, gesture hold only on the active document, close/activate, restart says what it replaced.
- `CloseTests.SaveWritesTheFile...` now waits for the window's close handler to remove the session (it raced).

## Results
Unit 335, browser 60, Windows 41 (excluding Office/Performance/Demo), host 127, MCP 13, contracts 24: all pass.
