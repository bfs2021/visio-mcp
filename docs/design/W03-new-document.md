# W03 — New document and page-size templates

Status: approved by the user in chat, 2026-10-18; implemented on the feature branch.
Issue: https://github.com/rp2839/visio-mcp/issues/25
Branch: `codex/w03-new-document`, based on main `0c336be`.

## Intended outcome

A person can start another drawing from File > New or Ctrl+N without restarting the app or losing existing work. They choose a starting page; the result is an untitled, unsaved document with its own identity, history and recovery session. W03 extends existing File, document-factory, revision-checking and recovery flows. It does not implement the W02 tab system.

## Proposed behavior

1. File > New and Ctrl+N open the same accessible New document dialog. The default is A4 landscape. Choices reuse the existing page-size definitions: A4, A3 and Letter in landscape/portrait, plus Custom. Custom width/height use the current editor units, convert to points and pass the existing document/page validation; nonfinite, nonpositive or unsupported dimensions cannot be applied. Dimensions are visible before creation.
2. Cancel or Escape closes the picker without changing the drawing, history, save path, recovery files or identity. Repeated New requests while its dialog or save prompt is open do not start duplicate workflows. New is deferred while an editor gesture is uncommitted, using the existing interaction guards.
3. In the current single-document UI, Create checks the current document's unsaved state. If necessary, show Save / Don't save / Cancel using the same semantics as W01. Save continues only after a successful save of the current revision. A failed save or cancelled Save as leaves the old drawing open. Don't save is consent to discard that document only; do not call the window-close action.
4. Successful creation produces a new document, page, layer and session identity. It has the selected size, white page, existing default grid/layer settings, empty elements/assets and title Untitled. Selection, undo/redo and stale component/previews are reset. Saved path and saved time are cleared; the title/status show the new document as unsaved. Saving asks for a path.
5. The explicit New action is recoverable even before the first object is drawn. Starting a new drawing must not revive a document explicitly discarded through the prompt. Recovery cleanup is scoped to the old session ID and follows successful creation; it cannot delete the newly created session. A failed creation must retain the original document and recovery evidence.
6. If W02's tab-creation interface is present at implementation/integration time, Create adds and activates a user-owned tab instead of replacing the current document. The old tab and its history/path/recovery stay intact; there is no discard prompt when nothing is being replaced. W03 uses that shared workspace interface, not a second tab registry or an agent-owned background document.

## Approach and alternatives

Use a small React picker and a shared page-preset module, extend the existing document factory to accept validated page dimensions, and route creation through one editor lifecycle action. The existing revision-pinned replacement path provides the single-document fallback; a narrow host lifecycle call clears the matching saved path and coordinates session recovery. Creation must recheck document ID, session ID and revision after asynchronous prompts. If work changed in the meantime, preserve it and ask the person to retry rather than discard newer edits.

A direct Ctrl+N action that always creates A4 would be faster but would hide the requested starting-page choice. Building W03 on a private tab implementation would conflict with W02 and duplicate ownership/recovery logic. The picker plus the existing lifecycle, then W02's shared creation seam, is the recommended approach.

## Coordination and scope

Claude owns W02a (#40) on main: Workspace/DocumentState, DocumentRegistry and per-session recovery. Before touching shared lifecycle files, reread that issue and latest code, publish the exact seam/file overlap, and reconcile with its public interfaces. Do not rewrite that refactor or change its automatic session-restore decisions. If the interface is still moving, keep the preset/dialog work isolated until it settles; do not merge a destructive single-document replacement into a tabbed build.

Expected files: `src/web/src/model/newDocument.ts`, a shared page-preset module extracted from `components/PageTabs.tsx`, a New dialog, `components/Toolbar.tsx`, the existing keyboard handler, `editor/EditorModel.ts`, and targeted host/bridge lifecycle files and tests. Exact shared-file changes will be recorded in #25 before editing. No canonical document/operation schema changes or public MCP tool additions are planned.

R09 has not been implemented, so swim-lane/flow starter choices are deferred to its existing conditional dependency. W03 will not present placeholder starters or manufacture R09 structures. Recent files, custom templates, print/export, preferences and MCP document-management tools belong to their separate issues.

## Verification

- Unit checks: all preset dimensions/orientations, custom-unit conversion/validation, independent identities, empty content, new unsaved state, fresh history, cancelled workflows, failed/cancelled saves, revision changes while prompting and repeated commands.
- Browser checks: File > New and Ctrl+N reach the same picker; keyboard focus/Escape; custom validation; chosen page dimensions; Save/Don't save/Cancel behavior and unrelated-object preservation on cancellation. With tabs available, the previous tab remains byte-exact with separate history.
- Real Windows/WebView2 checks: saved-path/title reset, save cancellation, explicit discard removes only the old recovery session, new document gets a checkpoint and can be recovered, and an old MCP identity cannot modify the new document. Test the real host bridge as well as pure UI state.
- Before each code push: usual frontend, browser, .NET, typecheck, contract-generation and build checks; preserve fresh evidence and known limits. Before integration, merge the current main and verify the combined tree. A design-only commit needs document/diff checks, not product tests.

## Approval requested

The user approved this design in chat on 2026-10-18. Implementation and verification are recorded in [W03 results](../m5/W03-new-document.md). W02a main `1dbcf56` was merged during development. It supplies internal workspace/registry interfaces but retains a single visible document; this implementation uses the protected replacement flow until W02b supplies the tab shell. Before merging PR #45, W02b/W02c were integrated: the picker now creates a separate tab and preserves the previous model and recovery track. See the integration section of the results report; the replacement path remains the standalone fallback.
