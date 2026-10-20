# W13 Preferences — residual modal arbitration review

Reviewed exact immutable diff `0d74ade..a74345513811c6e6be7b0d54bec69ba99af905ec` on 2026-10-20 in `C:/Users/claude/.codex/worktrees/r03-icons/diagram-app`. HEAD matched the requested head. The patch changes only `src/web/src/App.tsx` and `src/web/tests/browser/preferencesReview.spec.ts` (38 insertions, 1 deletion).

Inputs: `final-review.md`, `final-fix-review.md`, `docs/design/W13-preferences.md`, the exact Git diff, and surrounding report producers, PreferencesModel/PreferencesDialog, subscriptions, workspace lifecycle guards and browser support. File references below are relative to this worktree.

This is a read-only static review with this report as its only written artifact. No tests, product edits, checkout/pull/index changes, push, or subagent dispatch were performed. The repository coordination workflow was read; the parent executor retains ownership of the W13 claim and issue/project coordination. This report does not assert current issue status or whole-worktree cleanliness.

## Findings

### Critical

None found in the scoped patch.

### Important

None found in the scoped patch. The residual Important finding in `final-fix-review.md` is addressed by the rendering condition.

### Minor

None found in the scoped patch. The earlier malformed captured-default warning finding remains outside this review and is not resolved by this verdict.

## Evidence and reasoning

- `App.tsx:107` mounts CompatibilityDialog only when `ws.preferences.dialogOpen` is false. PreferencesDialog renders a dialog only when that same property is true. Thus these two aria-modal dialogs cannot coexist in the committed React tree, including when a host response arrives during Preferences loading, editing, reloading or saving. The predicate uses dialog ownership, not the narrower busy flag.
- The condition does not clear, copy, replace or consume `EditorModel.report`. Both import diagnostics (`EditorModel.ts:711`) and export diagnostics/confirmation (`:723`, `:736`) retain their existing model-owned contents. CompatibilityDialog reads the current report on mount and has no report-mutating mount/unmount effect. The export confirmation callback and captured destination path remain intact; rendering alone cannot accept compatibility loss or initiate another export.
- Preferences cancellation clears only its draft/dialog state and notifies subscribers. Successful Save closes the dialog only after persistence succeeds; the intermediate apply notification leaves it open. Validation, conflict and I/O failures leave it open, so the report remains deferred. WorkspaceModel subscribes to preferences changes (`WorkspaceModel.ts:75`) and each adopted editor (`:125`); App uses that workspace subscription. Closing Preferences therefore causes the current report to mount without requiring another host response.
- Deferral introduces no separate pending queue or cached report. A report that is cleared before presentation remains cleared; a replacement report is the one subsequently shown. Closing the displayed report still sets the authoritative field to null and bumps the model (`EditorModel.ts:740`). Nothing in this patch can resurrect a dismissed report. Existing report replacement and session-lifetime policies remain unchanged.
- The existing admission guard against opening Preferences over any tab's report (`WorkspaceModel.ts:40`), palette guard (`App.tsx:37`), Preferences shortcut guard (`App.tsx:36`), lifecycle/tab guards and router gesture wiring remain untouched. The deferred report still participates in model-based guards while hidden. This change does not admit additional lifecycle operations or discard compatibility diagnostics to obtain exclusivity.
- The new parameterized browser scenarios (`preferencesReview.spec.ts:73`) exercise the original failure ordering through menu controls: export begins, Preferences opens, and the deliberately pending export resolves with compatibility_loss. They assert absence of the compatibility component and one visible dialog while Preferences owns the modal, then check its summary, diagnostic, confirmation control and single-dialog state after both Cancel and Save. Final dismissal checks that no visible dialog remains. The accept-loss mock branch avoids hanging if invoked, but these tests do not invoke it.

## Declined to judge

- Whole-branch acceptance, storage/recovery/startup correctness, unrelated modal arbitration and W14.
- Existing CompatibilityDialog focus management, report session/revision validation, simultaneous report replacement, and background-tab report presentation beyond confirming this patch leaves those policies unchanged.
- Fresh automated results, native interaction, human acceptance or intended integration. No historical pass is promoted into fresh evidence here.

## Verification gaps

- Tests, typecheck, build and browser interaction were NOT RUN, as requested. Parent-owned acceptance must record actual results for this head.
- Added tests deliberately delay the export response, but do not deliberately delay Preferences get/save, exercise save failure/conflict, or cover delayed import and successful export diagnostics. Static inspection supports the shared rendering condition for these states; those are not separately exercised by the new scenarios.
- The tests check confirmation visibility, not callback invocation or preserved export path, and do not reopen Preferences after report dismissal to test absence of stale report resurrection. These are coverage limits, not observed defects.
- Keyboard focus after the handoff is not asserted. Preferences restores its opener on close and CompatibilityDialog retains its existing behavior; this patch establishes dialog exclusivity without adding a new focus policy.

## Verdict

**Approved for this scoped residual fix, subject to the parent executor's automated acceptance gate.** The pending compatibility report is retained, presentation is deferred until Preferences closes, both Cancel and successful Save release it, and the reviewed patch does not weaken existing modal or lifecycle guards. No new scoped defect was identified. This is a static verdict and does not certify unrun tests or whole-branch integration readiness.
