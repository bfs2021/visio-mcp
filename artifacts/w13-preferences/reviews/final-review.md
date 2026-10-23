# W13 Preferences — fresh whole-branch review

Reviewed immutable base `465de59a60301ae70669d8f5fd0ae9d2fee38357` through head `bd15e52c38f07c8e0660dff73985992d5ba33657` on 2026-10-20. HEAD matched the requested head. Inputs: `review-465de59..bd15e52.diff` (1,761,450 bytes), `docs/design/W13-preferences.md`, `docs/superpowers/plans/2026-10-19-w13-preferences.md`, and the requesting-code-review reviewer template. All file references below are relative to `C:/Users/claude/.codex/worktrees/r03-icons/diagram-app`.

This was a read-only static review. No product edits, test runs, checkout/index/branch changes, pushes, or subagent dispatches were performed. This report is the only written artifact. The repository issue workflow was read; the parent executor retains the existing W13 claim and owns issue/project coordination. The initial report write was denied by the filesystem sandbox; the final artifact was saved in the assigned worktree using scoped escalation. No product code was edited.

## Strengths

- `PreferencesStore` combines an instance gate, exclusive sibling-file lease, original-byte content tags, a final pre-replace check, and unique durable backups. Invalid/future settings are not repaired during startup; write failures retain published settings.
- Settings limits/defaults come from the shared schema. Generated records are separate from the document schema and public MCP discovery. Frontend normalization, finite-number checks, Unicode length handling, and the host evaluator received focused review.
- Startup retains one bridge client across preference initialization and bootstrap. Visible/agent creation captures grid/font only; agent factories refresh persisted defaults before creating another drawing.
- Planner defaults apply only to new or first-materialized text. Existing formatting, explicit component/icon formatting, document colours, and export rendering remain outside view preference application.
- Recovery reschedules the existing timer through the existing queue/gate. The stopping flag prevents new jobs after disposal begins; commit thresholds, save checkpoints, and session counters remain intact.
- Added tests assert meaningful conflict/failure, startup, metadata/replay, deterministic recovery timing, view equality, and native export behavior. Their assertions were reviewed; their execution results are not certified here.

## Issues

### Critical (Must Fix)

None found.

### Important (Should Fix)

1. **[P2] Inspector retains legacy text defaults while the planner uses captured defaults.**
   - References: `src/web/src/components/Inspector.tsx:10`, `:21`, `:124`, `:161`; changed integration: `src/web/src/commands/planPatch.ts:917`.
   - In a drawing captured with Aptos 19, selecting a shape without a text block or a connector without a label still shows Segoe UI 12. Typing 12 into Size and leaving the field sends no operation because NumberField compares against the displayed legacy 12. Adding the first text then materializes Aptos 19. The explicit formatting intention is lost, and the inspector does not describe the font/size it will create.
   - Use shared `textDefaults(model.snapshot.document)` for absent-text fallbacks. Retain the actual family as an option when it is outside the inspector's five font choices; otherwise a newly created Aptos block has no matching select option.
   - Add real-browser coverage for an empty shape and connector in a custom-font drawing: initial inspector state, explicit change to the legacy font/size, and first text materialization. Existing browser coverage checks capture metadata, not this interaction.

2. **[P2] Preferences omits the Visio compatibility modal from its overlap guard.**
   - Reference: `src/web/src/workspace/WorkspaceModel.ts:40`.
   - The guard checks other dialogs but omits `t.report`, which drives CompatibilityDialog. While an import/export compatibility report is open, Ctrl+Shift+P can open the command palette (`App.tsx:37`). Choosing Preferences closes the palette before running the command (`HelpDialogs.tsx:103`), so `help.panel` no longer blocks it. Preferences opens over the still-active compatibility report. Both dialogs claim aria-modal, with a pending report underneath, violating the explicit no-overlapping-modals requirement.
   - Include compatibility reports in the shared guard, preferably also preventing palette opening over them. Add a browser regression for this keyboard route. Consider a report arriving from an already-running lifecycle operation while Preferences is loading/saving.

### Minor (Nice to Have)

1. **Malformed captured defaults have no visible warning on an ordinary open/read.**
   - References: `src/web/src/model/documentDefaults.ts:23`, `src/web/src/commands/CommandEngine.ts:231`.
   - The parser safely preserves invalid metadata and falls back, but its only outward diagnostic integration is a transaction result. Opening/restoring a malformed-capture drawing and reading it produces no visible diagnostic; ordinary GUI apply paths also do not display transaction warnings. A person can receive legacy defaults without learning why.
   - Expose a non-mutating document warning on load/restore/display while preserving metadata, revision, and hash. No migration or automatic repair is needed.

## Declined to judge

- W14 file-drop design: the branch includes its design document, but W14 runtime work is expressly outside W13.
- Live cross-process theme synchronization: explicitly excluded; re-reading settings on Preferences open/apply is the applicable contract.
- OS-owned file-dialog theme: expressly allowed to retain Windows styling.
- Guide persistence, installer/association work, font downloads, automatic recolouring, and document migration: expressly excluded.
- Byte-by-byte independent correctness of the entire generated validator: it contains approximately 1.4 MB of generated single-line output. I inspected the generator, schemas, declarations, preference/capture exports, and key bound/type/unknown-property logic. Freshness and broader regression behavior require the separate generated check and test suites.
- Fresh automated/manual acceptance and intended integration: left to the independently recovered acceptance work; this reviewer did not run tests or perform human sign-off.

## Verification gaps and recommendations

- Full frontend unit/typecheck/generated/build/browser and sequential Windows solution results are **PENDING** for the parent executor to recover and record. No historical task log is promoted into a fresh pass claim here.
- Fresh human acceptance is **NOT RUN by this reviewer**. Screenshot/export assertions and native test code are automated evidence, not manual sign-off.
- Add the two focused browser regressions, rerun affected checks after fixes, and complete the standard acceptance gate. Preserve initial failures/retries in W13 evidence and keep the issue open until intended integration is verified.
- No high-confidence storage data-loss, startup deadlock, arbitrary path access, or recovery interval durability regression was identified in the authored paths reviewed.

## Assessment

**Ready to merge? With fixes.**

Host persistence, captured defaults, startup, and recovery updates are coherent. Resolve the two GUI integration issues and obtain pending acceptance results before treating W13 as ready for integration. This static verdict does not certify unrun suites or manual acceptance.
