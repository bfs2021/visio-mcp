# W03 — New document and page-size templates

[Approved design](../design/W03-new-document.md) · [Issue #25](https://github.com/rp2839/visio-mcp/issues/25)

File > New and Ctrl+N now open the same page-size picker. It offers A4/A3/Letter in landscape or portrait and custom dimensions in the current editor units. The existing page-size picker and New share their preset definitions. Dimensions must be finite, greater than zero and no larger than the canonical 14,400pt limit. Cancel/Escape leave the drawing and its history unchanged. The dialog manages focus, keeps Tab inside even while Save is pending, and rejects repeated New requests or active gestures.

## Document lifecycle

The current single-document UI asks Save / Don't save / Cancel before replacing unsaved work. Cancelled or failed saves keep the old drawing open. Identity, session and revision are captured before the picker opens and checked again after asynchronous work; newer edits are never silently discarded. Invalid documents fail before replacement and restore the old host path.

A successful New starts a fresh untitled unsaved document with a new page/layer/session, empty content/assets and independent history. The host path, saved time, selection and transient reports are reset. Save asks where to put it. The old file is not overwritten by New. File actions are serialized so an earlier Save finishes against its original document before replacement; the late-save regression deliberately stalls the atomic writer to test this.

An explicitly created blank document is meaningful unsaved work at revision zero. A backwards-compatible `keepUnsaved` flag lives in recovery session metadata, not the canonical drawing. Protection is per W02a recovery track. The first checkpoint and flag must be durable before the old session is retired. Saving clears the marker. Recovery transfers it to the replacement session before deleting the source, so a recovered blank drawing survives another restart before any edit/save. If protection fails after creation, the page warns the person to save and the previous recovery session is retained.

The close/restart scenario exposed queued bridge replies reaching a disposed WebView2. The host now stops posting before an approved window close and cancels pending router requests through the existing failure path. Replies already queued check the stop flag on the UI thread. Cancelled close requests do not stop the bridge. A close request while New's Save is pending is cancelled without stacking another dialog.

## W02 coordination and limits

This branch includes main `1dbcf56` through merge `32c5e75`. The conflict in `RecoveryManager` was resolved around Claude's per-document Track registry; saved paths use the per-document `SetPath` API. Shared-file changes were recorded on #40. No canonical document/operation schemas or public MCP tools changed.

W02a exposes internal workspace/registry interfaces while the visible app still shows one document. W03 therefore uses the protected single-document fallback from its issue. **The visible tab shell and New-as-tab behavior belong to W02b (#41).** Its integration should reuse this picker and factory, bypass replacement consent when preserving the current tab, create the new tab through the workspace, and leave the previous model/history/view state and recovery track intact. The lifecycle class separates creation and post-creation callbacks for this handover; replace both callbacks so the old model is not reset. Initialize the new engine explicitly unsaved and call `KeepCurrentUnsavedAsync` for the new recovery track before claiming protection. Do not leave the replacement host call wired into a tabbed New action.

R09 swim-lane/flow starters remain deferred because R09 is not implemented. Recent files, custom templates and preferences stay with their own issues. This is automated Windows/WebView2 evidence; a human manual acceptance pass is not claimed.

## Verification

Commands on the combined tree:

- `npm --prefix src/web test`: 341 passed after W02a merge. An earlier run under simultaneous compilation exceeded the existing 5s contract-generator test timeout; the subsequent full run passed without changing its timeout.
- `npm --prefix src/web run typecheck`, `npm --prefix src/web run generate:check`, `npm --prefix src/web run build`.
- `npx --prefix src/web playwright test --config src/web/playwright.config.ts`: 63 passed, including the pending-save close/focus regression.
- `dotnet test Diagram.Windows.slnx --logger "trx;LogFilePrefix=w03-final" --results-directory artifacts/w03-new-document/final-test-results`: 353 passed across all six test projects, including 44 Windows application tests.

[Evidence](../../artifacts/w03-new-document/README.md) includes the actual WebView2 picker, created snapshot, fresh UI-to-host verification and TRX results. Deliberate red runs reproduced the lost marker after a second restart and the concurrent Save/New failure; the first shutdown-race run aborted and is retained. Independent read-only review identified the lost marker after recovery; the regression now performs the actual recovery and a second restart.

Final code verified: `79b6884`. The final targeted Windows run (`FullyQualifiedName~NewDocumentTests|FullyQualifiedName~CloseTests`) passed all 10 tests after the final frontend build. Typecheck, generated-contract check and production build passed. Manual human acceptance: NOT RUN.
