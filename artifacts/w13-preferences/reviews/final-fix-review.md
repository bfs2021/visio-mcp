# W13 Preferences — scoped final-fix re-review

Reviewed exact range `bd15e52..0d74ade18edf9001961b1fb5ca194c16a6cd775c` on 2026-10-20, branch `codex/w13-preferences`, in `C:/Users/claude/.codex/worktrees/r03-icons/diagram-app`. HEAD matched the requested head. Inputs: the immutable five-file patch, `.superpowers/sdd/2026-10-19-w13-preferences/final-review.md`, `docs/design/W13-preferences.md`, and surrounding Inspector/planner, preferences, export, command and dialog code. All references below are relative to this worktree.

Scope is only the two Important findings from the prior review and regressions introduced by their fixes. No tests, product edits, Git-state changes, pushes, or agent dispatches were performed. This report is the only written artifact. The issue workflow was read; the parent executor retains the W13 claim and issue/project coordination. No pull or coordination mutation was performed during this assigned read-only review. A plain `git status` was unavailable in the sandbox, and an explicit worktree comparison also failed with permission denied; immutable patch contents and HEAD were obtained successfully. No clean-worktree assertion is made.

## Strengths

- `src/web/src/components/Inspector.tsx:121` now uses the shared `textDefaults(model.snapshot.document)` only for absent shape text/connector labels. This matches first materialization at `src/web/src/commands/planPatch.ts:916` and `:917`, keeps authored TextBlocks authoritative, and makes 12 pt an actual change when the captured default is 19 pt.
- `src/web/src/components/Inspector.tsx:161` includes the actual current family in the select options and deduplicates the built-in choices. Custom captured families such as Aptos therefore have a matching selected option before and after first text creation.
- `src/web/src/workspace/WorkspaceModel.ts:40` prevents opening Preferences while a compatibility report already exists, including a report on another tab. `src/web/src/App.tsx:37` also prevents the reported Ctrl+Shift+P route from opening the palette over that report. Closing the report restores the route.
- `src/web/tests/browser/preferencesReview.spec.ts:4` covers both empty object kinds, untouched captured formatting and explicit legacy formatting, including a no-mutation assertion when selecting the object. The browser scenario at `:44` checks the complete original palette route and reopening after dismissal; `src/web/tests/preferencesFlow.test.ts:164` directly covers the workspace entry guard. These assertions were inspected, not executed.

## Critical (Must Fix)

None found in this scoped patch.

## Important (Should Fix)

1. **[P2] A pending Visio export can still publish a compatibility modal over Preferences.**
   - References: changed guard `src/web/src/workspace/WorkspaceModel.ts:40`; producer `src/web/src/editor/EditorModel.ts:718`, `:723`, `:736`; busy wiring `src/web/src/main.tsx:96`; rendering `src/web/src/App.tsx:106`, `:117`.
   - Reproduction from the async control flow: start Export to Visio, keep its host response pending, then open Preferences before the response arrives. Export has not set `report` or another prompt, and `ws.isBusy()` only tests active gestures, so the new guard permits Preferences. Resolve export with `compatibility_loss` (or successful diagnostics): the continuation assigns `report` unconditionally, while App independently renders CompatibilityDialog and PreferencesDialog. This can occur while Preferences is reading or saving; both dialogs claim modal status.
   - This is a residual of the prior modal-overlap finding, whose recommendation explicitly called out already-running lifecycle operations. It is **not a newly introduced regression**. The already-visible-report keyboard scenario is fixed, but the no-overlap requirement remains incomplete.
   - Track the in-flight lifecycle operation in the shared admission guard, or defer report presentation until Preferences closes, preserving diagnostics and the explicit export confirmation. Add a browser regression with a deliberately delayed host export response and assert a single visible dialog through load/save and dismissal.

## Minor (Nice to Have)

No new Minor issue identified in the scoped fix. The prior malformed-capture warning finding was not reopened or re-evaluated.

## Declined to judge

- The rest of W13, storage/startup/recovery correctness, generated output, unrelated modal/shortcut behavior, and W14.
- Installed-font rendering/fallback quality on Windows; this review verifies that the captured family is retained as a valid select option and passed through the existing planner.
- Overall branch acceptance, integration readiness, fresh automated results or human sign-off.

## Verification gaps

- Tests, typecheck, build and browser interaction were **NOT RUN**, as requested. New test coverage and production paths received static inspection only.
- The new modal browser test starts after the compatibility report is already visible. It does not exercise the delayed-response interleaving above.
- Parent-owned acceptance should retain actual command results and distinguish automated from fresh manual evidence. Existing historical pass records are not promoted to fresh evidence by this review.

## Verdict

**With fixes.** The Inspector finding and the original already-visible-report keyboard route are addressed. No new regression was identified in the fix range. One Important residual remains in the same compatibility-modal requirement: an in-flight export can present a report while Preferences is open. Address that ordering before marking both original Important findings fully resolved. This is a scoped static verdict, not certification of unrun checks or the whole branch.
