# W02: tabbed documents with session restore

Status: **proposal for review**, 2026-10-18. GitHub issue #24 (W02). No code has been written. Questions that need a decision are in section 9.

## 1. What is wanted

From the user's request of 2026-10-18:

- Every document is a tab, as in Notepad++. New and opened documents are added as tabs.
- The previous session's tabs reopen when the app starts.
- Closing a tab with unsaved changes asks to save. If the person chooses not to save, the changes are discarded (and are not offered again by the recovery dialog).

The acceptance criteria on #24 add: each tab has its own undo history, selection, zoom and recovery; the MCP tools can address any document; Ctrl+Tab and Ctrl+W work; a tab shows the file name and an unsaved marker.

## 2. Where the product is today (single document everywhere)

| Layer | Single-document assumption | Where |
|---|---|---|
| Page | One `CommandEngine` (revision, history, saved state), one `EditorModel` (selection, current page, zoom, drawers, clipboard, recovery offers), one visible `MaxGraphAdapter`. | `src/web/src/commands/CommandEngine.ts`, `editor/EditorModel.ts`, `main.tsx` |
| Page bridge | `AdmissionGate` checks every `doc.*` request against one `scope()`; `startApp` creates one engine. | `bridge/AdmissionGate.ts`, `bridge/bootstrap.ts` |
| Host bridge | `BridgeRouter` holds one `Scope` (documentId + sessionId) and one revision; `SessionChanged` replaces it. | `Diagram.Host.Core/BridgeRouter.cs` |
| Recovery | `RecoveryManager` follows one session; `DiscardCurrentAsync`, `CloseCleanlyAsync`, `CandidatesAsync` exclude "the" current session; blob GC proves liveness from the one current scope. | `Diagram.Host.Core/RecoveryManager.cs` |
| Files | `HostServices.CurrentPath` is one path; the export-path policy trusts "the open document's folder"; Open replaces the document in place. | `Diagram.App/HostServices*.cs`, `ExportPathPolicy` |
| MCP | `LiveControl` fills a missing scope from `CurrentScope` for `doc.summary` and `assets.list`; every other tool carries `documentId` and `sessionId`. | `Diagram.App/LiveControl.cs`, `Diagram.Mcp/DiagramTools.cs` |
| Shell | Window close handler and title are document-level (W01, W04). | `MainWindow.xaml.cs`, `components/DocumentTitle.tsx` |

Two things already point the right way and are kept:

1. **Identity is already in the protocol.** Every document tool carries `documentId` and `sessionId`, and a mismatch is an error (`document_mismatch`, `session_mismatch`). An agent that read a summary of tab A cannot accidentally edit tab B. Multi-document needs the *router* to look the engine up by identity instead of comparing to one current value.
2. **Rendering and route projection do not need the visible canvas.** `SnapshotProjector` and `SnapshotRenderer` work on any snapshot. So a background tab can be edited and rendered by an agent without being on screen.

## 3. Recommended architecture

One WebView, one page, several documents.

```
 MainWindow (WPF)                         Page (React, one WebView2)
 ------------------                       ------------------------------------------
 DocumentRegistry  <== bridge ==>         Workspace
   documentId -> {sessionId, path,          tabs[], activeId, clipboard, preferences
                  title, unsaved,           DocumentState (one per tab)
                  recovery session}           CommandEngine   (history, revision, saved state)
 RecoveryManager (one RecoverySession         view state      (page, zoom, scroll, selection)
                  per open document)        one MaxGraphAdapter (shows the active tab)
 WorkspaceStore (workspace.json)            SnapshotProjector / Renderer (shared, any snapshot)
 LiveControl (MCP pipe) ----------------->  AdmissionGate routes by documentId to that engine
```

### 3.1 Page

- `Workspace` owns an ordered list of `DocumentState` and the active id. `DocumentState` wraps what is per document today: a `CommandEngine`, plus the per-document parts of `EditorModel` (current page, selection, zoom and scroll, drawer, Visio compatibility report, recovery report). The visible canvas, panels and dialogs bind to the **active** document, so most React components keep their `model` prop and receive `workspace.active`.
- One `MaxGraphAdapter` shows the active tab. Switching tabs cancels any gesture, projects the other snapshot and restores that tab's view state. A background tab keeps its engine, so agent edits to it still commit, journal and undo normally; they are drawn when the tab is next shown.
- `AdmissionGate` takes a resolver `(documentId) -> engine` instead of a single `scope()`. The user-gesture interlock (hold agent mutations while the user drags) applies only to the active document, which is an improvement: an agent can keep working on a background tab while the user draws.
- The clipboard is application level, so copy in one tab and paste into another works. Pasted pictures are pinned through the existing approved-asset path.
- The test API `window.__editor` keeps pointing at the active document, so existing browser tests change little.

### 3.2 Host

- `DocumentRegistry` replaces the single `Scope`. `BridgeRouter.CurrentScope` stays as "the active document's scope" for existing callers and tests, and gains `ScopeOf(documentId)` and `Documents`. The page announces `editor.document.opened`, `.session` (replaced), `.closed` and `.activated`.
- `RecoveryManager` follows **one `RecoverySession` per open document** (a dictionary keyed by sessionId). `DiscardCurrentAsync` becomes `DiscardAsync(sessionId)`; checkpoints, journals and the clean-close rule are per session. Blob garbage collection must ask **every** open document for its retained hashes (today it asks only the current one, which would delete pictures a background tab still needs; this is the highest-risk correctness point and gets its own test).
- `HostServices` stops holding `CurrentPath`; Save, Save as, Open, Export and the export-path policy are scoped by the document id the page sends.
- `MainWindow.Closing` generalises W01: `app.beforeClose` returns the list of documents with unsaved work and the page walks them (section 5).

### 3.3 Why not the alternatives

| Alternative | Why not (for now) |
|---|---|
| One WebView per tab | Strong isolation, but each tab pays for a browser process, fonts and the bundle; the router, recovery and agent plumbing still need the same registry; background rendering becomes N renderers. A renderer crash today restores one document; with one WebView it would restore all tabs from the workspace file, which is acceptable. Revisit only if a single page proves unstable with many large documents. |
| One window per document | Not what was asked, and multiplies window-level state (title, close, MCP default). |
| Keep one document, add a "recent files" menu | Does not satisfy tabs or session restore. |

## 4. Tabs and documents: behaviour

- **Strip:** file name (or `Untitled N`), a dot while unsaved, a close button, tooltip with the full path. Middle-click and Ctrl+W close. Drag to reorder. Overflow scrolls with arrows and a list button. Context menu: Close, Close others, Close to the right, Copy path, Show in Explorer. Roles: `tablist`/`tab`, arrow keys move, Enter or Space activates.
- **New (Ctrl+N):** adds `Untitled N` and activates it (W03 supplies the templates).
- **Open (Ctrl+O, drag and drop later):** adds a tab. If the file is already open, that tab is activated instead of opening a second copy. If the active tab is an untouched blank, it is replaced (Notepad++ behaviour) rather than left behind.
- **Visio import** opens as an unsaved tab named after the file (it has no JSON file yet).
- **Switch:** click, Ctrl+Tab and Ctrl+Shift+Tab (most recently used order), Ctrl+1..9.
- **Limit:** a soft limit of 20 tabs with a message; each engine keeps up to 1000 history entries and 16 MiB of cached results, so the practical cost is bounded per tab.
- **Titles:** the window title and taskbar follow the active tab (W04). Status bar follows the active tab.

## 5. Closing and saving

- **Close a tab with no unsaved work:** closes. The recovery session is completed and the tab leaves the workspace.
- **Close a tab with unsaved work:** the W01 prompt for that document: *Save* (Save as when untitled; the tab closes only if the save succeeded), *Don't save* (discard that document's recovery session, close), *Cancel*. Don't save means the changes are gone and are never offered again.
- **Close the window with unsaved tabs:** one prompt per changed tab, in tab order, activating each so the person sees what is being asked about, with extra buttons *Save all* and *Don't save any*. Cancel anywhere stops the close. A page that does not answer within 5 s still cannot trap the window (W01 rule). See question 2 for the alternative of not asking on exit.
- **Crash or kill:** nothing is asked at exit; section 6 brings the work back.

## 6. Session restore

A `workspace.json` (atomic write, in `%LOCALAPPDATA%\AgenticDiagram\workspace`) records the ordered tabs, the active one, a `cleanExit` flag and, per tab: `documentId`, `path` (or null), `title`, the recovery `sessionId` if there was one, and view state (page, zoom, scroll). It is rewritten on open, close, save, activate and reorder, debounced.

At start:

1. Read `workspace.json`. For each tab:
   - saved and unchanged since: reopen from `path` (validated like any Open; a missing or changed file gets a placeholder tab with *Locate*, *Close* and, for changes on disk, the W07 conflict choices);
   - unsaved work: rebuild from its recovery session using the existing `restoreRecovery` (checkpoint plus journal), mark it unsaved, keep its original path;
   - untitled and untouched: dropped.
2. Reactivate the previous active tab.
3. If `cleanExit` was false, show a single non-modal notice: "Restored N documents after an unexpected exit. Unsaved changes are kept." with a link to the loss report when a journal was damaged.
4. **Orphans:** recovery sessions that no workspace tab owns (the workspace file was lost, or a tab was closed during a crash) are the only ones that still raise the W06 dialog, now with each choice opening **as its own tab**.

This resolves the repeated-dialog complaint: after W01 and this change the question appears only for work the app cannot place.

## 7. MCP and agents

Existing tools keep their shape. What changes:

| Item | Behaviour |
|---|---|
| `get_document_summary` with no arguments | Describes the **active** tab and now also lists the other open documents (id, title, path, unsaved, revision). With identity arguments it describes that document. |
| `list_documents` (new, read-only) | All open documents: `documentId`, `sessionId`, title, path, active, unsaved, revision, page count. |
| `create_document` (new) | Adds an untitled tab (title, page size). By default it does **not** take focus (the tab gets a "new activity" mark) so an agent cannot yank the user out of their work. Returns the identity. |
| `show_document` (new) | Activates a tab. Refused with `busy_user_editing` while the user is mid-gesture, as mutations are today. |
| Opening, saving and closing documents | **Not offered to agents in this step.** Open and Save touch the file system, which the pipe deliberately never exposes (`LiveControl` offers document tools only). A later `open_document` could use the same consent window as export ("Claude asks to open C:\...: Allow?"). Agents cannot close tabs. |
| Errors | `document_mismatch` becomes `document_not_open` when the id matches no tab, and still `session_mismatch` when the tab was reopened. The `Identity` text on every tool says to call `list_documents` first. |
| Safety | Because every call pins `documentId` and `sessionId`, switching tabs between an agent's read and write cannot misdirect an edit. Existing single-document clients behave as before while one tab is open. |

## 8. Plan, tests and risks

Phases, each shippable and each keeping the existing suites green:

| Phase | Content | Size |
|---|---|---|
| P1 | Refactor with no visible change: `Workspace` + `DocumentState` in the page, `DocumentRegistry` and per-session `RecoveryManager` in the host, one tab. All current browser and Windows tests pass unchanged. | L |
| P2 | Tab strip, New and Open as tabs, switching with view state, per-tab close prompt, title and status, duplicate-open focus, replace-untouched-blank. | M |
| P3 | `workspace.json`, session restore, crash notice, orphan dialog opening tabs. | M |
| P4 | MCP: `list_documents`, `create_document`, `show_document`, summary lists others, gate routing and interlock per document, tool text and tests. | M |
| P5 | Polish: drag reorder, MRU Ctrl+Tab, context menu, overflow, tab limit, accessibility roles. | S |

Tests to add: registry and per-session recovery unit tests; blob GC with two open documents (a picture used only by the background tab survives); browser tests for open, switch, reorder, close with each of the three answers, duplicate-open focus, view state restore; real-window tests for multi-tab close (Save all, Don't save any, Cancel midway), crash then restore (kill with two dirty tabs, restart, both back and unsaved), orphan dialog; MCP tests for `list_documents`, create without focus, edit a background tab while the user drags in the active one, wrong-document errors.

Risks and mitigations:

- **Blob garbage collection** deleting pictures a background tab needs: collect over all registries; test first.
- **Test harness assumptions** (`CurrentScope`, `__editor`): keep both as "the active document" so existing tests keep working; fix the rest in P1.
- **`EditorModel` is large and single-document:** split deliberately in P1 (per-document state versus application state) rather than adding conditionals.
- **Renderer crash with many tabs:** restore all from the workspace plus recovery sessions; covered by a real-window test.
- **Agent focus theft:** background by default, explicit `show_document`.
- **Export-path policy** was written around "the open document's folder": scope it per document.
- **Clipboard across documents with pictures:** reuse the approved-asset pin on paste; test with a picture.
- **Memory:** bounded per tab; add a soft tab limit and show memory use in About (W12) later.

## 9. Decisions needed

Each has a recommendation; none blocks starting P1.

1. **Unsaved work after an unexpected exit:** restore it automatically with a notice (recommended), or ask first as the W06 dialog does today?
2. **Closing the window with unsaved tabs:** prompt per changed tab with Save all / Don't save any (recommended, matches the request), or follow Notepad++ and keep unsaved work silently for next time (could be a preference once W13 exists)?
3. **Agent-created documents:** open in the background (recommended) or take focus?
4. **Agents opening existing files:** leave out of the first release (recommended) or add now behind a consent window?
5. **Opening a file that is already open:** activate the existing tab (recommended) or allow a second copy?
6. **One WebView for all tabs** (recommended) or one per tab?
7. **Tab limit:** soft limit of 20 (proposed), higher, or none?
8. **Open or New while the active tab is an untouched blank:** replace it (recommended) or always add?

## 10. Proposed board changes (after approval)

Split #24 into sub-issues, in order: W02a refactor to Workspace and registry (P1), W02b tab strip and per-tab close (P2), W02c session restore (P3), W02d MCP document tools (P4), W02e tab polish and accessibility (P5). #24 stays as the parent. W06's orphan dialog and H01's restored-session lifecycle are folded into W02c and noted on those issues.
