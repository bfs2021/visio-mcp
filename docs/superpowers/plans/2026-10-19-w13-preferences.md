# W13 Preferences Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist user preferences and apply view choices, captured drawing defaults and recovery timing without rewriting existing drawings.

**Architecture:** The Windows host owns a bounded, validated, atomic preferences store. Generated shared contracts feed the renderer's draft dialog and immutable new-document defaults; recovery changes reuse the existing manager. Preferences load before a fresh document is created or announced ready.

**Tech Stack:** C#/.NET Windows host, React/TypeScript, JSON Schema/Ajv generated contracts, Vitest, Playwright and existing .NET/native Visio tests; no new dependencies.

**Spec:** [W13 written design](../../design/W13-preferences.md), approved in Codex chat on 2026-10-19. W14 is approved separately and excluded from this implementation plan.

## Global Constraints

- Work on `codex/w13-preferences` in the existing isolated worktree; baseline origin/main `e67f2e7`. Fetch and inspect main/issue claims before execution. Coordinate shared shell files with Claude's W12 (#34), preserving its commands; do not merge unrelated PRs implicitly.
- No new public MCP tool and no document schema-version change. Internal preferences methods are excluded from external live-control allowlists.
- Schema version 1; units `mm|cm|in|pt|px`, theme `light|dark`; defaults mm/light.
- Grid spacing finite 0.1–1440 pt, default `72 * 5 / 25.4`; visible/snap default true. Font family trimmed nonempty 1–128 characters, default Segoe UI; font size finite 1–1000 pt, default 12. Recovery interval integer 10–3600 seconds, default 60.
- Host-derived `preferences.json` beside workspace.json, maximum 64 KiB. No renderer-supplied storage path or browser-local persistence.
- Preserve corrupt/future files; explicit reset requires backup before replacement. Publish effective changes only after persistence succeeds.
- Captured document metadata key `editor.defaults.v1` contains grid/font only. Existing drawings receive no automatic migration, revision, formatting or dirty change.
- Preserve recovery journals, flush behavior, 50-commit/save checkpoints, counters and sessions. Updates cannot disable recovery.
- Small commits; standard checks before each push. No historical tracker edits and no automatic merge to main. Record initial failures/retries and manual acceptance honestly.

## Review Focus

- Settings changed by another app instance between dialog opening and Save must conflict, preserving both draft and current disk bytes (Task 2).
- Settings read during startup must not deadlock the readiness handshake or publish a temporary default drawing (Task 4).
- A first connector label, add-page operation or headless DSL edit must use the document's capture, not current global settings (Task 3).
- Reset of an unreadable/future file or a failed backup must not silently destroy the original (Task 2).
- Preference Save racing a queued checkpoint or application shutdown must retain durable work and avoid scheduling after disposal (Task 5).

## File and interface map

Create focused units rather than adding persistence/dialog logic to MainWindow/App:

- `contracts/preferences.schema.json`: canonical preferences and captured-default definitions; extend existing generator input list.
- `src/Diagram.Host.Core/PreferencesStore.cs`: storage, tags, backups and validation; `PreferencesValidation.cs`: canonical schema validation wrapper.
- `src/Diagram.App/HostServices.Preferences.cs`: internal get/save and installed-font suggestions, using existing host result conventions.
- `src/web/src/preferences/preferences.ts`: generated-contract validation/default helpers and typed internal reply types.
- `src/web/src/preferences/PreferencesModel.ts`: draft/apply state and view notifications; `src/web/src/components/PreferencesDialog.tsx`: accessible dialog.
- `src/web/src/model/documentDefaults.ts`: bounded metadata capture/parser; `src/web/src/preferences/theme.ts`: chrome-only theme application.
- Modify `tools/generate-contracts.mjs`, generated TS/C# DTOs and validators, `main.tsx`, `agentMain.ts`, `bridge/bootstrap.ts`, `bridge/hostServices.ts`, `editor/EditorModel.ts`, `editor/NewDocumentFlow.ts`, `workspace/WorkspaceModel.ts`, `model/newDocument.ts`, `commands/planPatch.ts`, `App.tsx`, `components/Toolbar.tsx`, `styles.css`, `MainWindow.xaml(.cs)`, `HostServices.cs` and `RecoveryManager.cs` only at the integration points described below.
- Tests: new `src/web/tests/preferences.test.ts`, `documentDefaults.test.ts`, `preferencesFlow.test.ts`, `browser/preferences.spec.ts`, `tests/Diagram.Host.Tests/PreferencesStoreTests.cs`, `tests/Diagram.Contracts.Tests/PreferencesParityTests.cs`, `tests/Diagram.Windows.Tests/PreferencesTests.cs`; extend existing newDocument, recovery, boundary and native roundtrip suites.

## Task 1: Canonical settings contract and validated defaults

**Interfaces:** Generate `AppPreferences` (schemaVersion, units, grid, font, recovery, theme) and `CapturedDocumentDefaults` (grid, font). TS exports `defaultPreferences(): AppPreferences`, `validatePreferences(value: unknown): Result<AppPreferences>` and `captureDefaults(p: AppPreferences): CapturedDocumentDefaults`. C# validation returns typed preferences or existing structured validation errors; no hand-maintained second limits table.

- [ ] Write failing `preferences.test.ts` and `PreferencesParityTests.cs`: default values above; reject NaN/infinity, 0.09/1440.01 grid, empty/129-character family, font 0/1001, recovery 9/3601/fraction, unknown versions/enums and wrong types. Family whitespace normalizes before bounds; valid absent installed font remains accepted.
- [ ] Run `npm test -- tests/preferences.test.ts` from `src/web`, and `dotnet test tests/Diagram.Contracts.Tests --filter PreferencesParity`; confirm intended missing-contract failures.
- [ ] Add schema definitions and extend generator discovery/standalone validation without changing document/protocol versions or public method discovery. Generate DTOs/validators with `npm run generate`. Export helpers with the interfaces above and schema-derived validation, rejecting unknown properties.
- [ ] Rerun both focused suites and `npm run generate:check`; expect PASS and identical host/frontend acceptance for the same boundary fixtures.
- [ ] Run standard checks below, then commit `feat(preferences): define validated shared settings contract` and push.

## Task 2: Atomic host preferences store and safe internal endpoints

**Interfaces:** `PreferencesSnapshot(AppPreferences Preferences, string VersionTag, string? Diagnostic)`; `PreferencesStore.ReadAsync(CancellationToken)` and `SaveAsync(AppPreferences preferences, string expectedVersionTag, bool resetInvalid, CancellationToken)` returning typed success/conflict/I/O/validation results. Version tags hash original bytes (including invalid/future files); missing has a stable sentinel. Internal `host.preferences.get` returns snapshot plus optional installed font names; `host.preferences.save` accepts preferences/expectedVersionTag/resetInvalid. No arbitrary paths.

- [ ] Write failing `PreferencesStoreTests.cs`: missing is defaults with no file; valid survives restart and isolated roots; 65537-byte/corrupt/future files survive reads; stale tag conflicts; failed backup/atomic replacement retains old bytes/effective state; reset backup equals original bytes. Two independent store instances saving the same expected tag yield one success and one conflict.
- [ ] Run `dotnet test tests/Diagram.Host.Tests --filter PreferencesStore`; expect missing-store failures.
- [ ] Implement store using AtomicFileWriter and in-process serialization plus an exclusive sibling lock-file handle (FileShare.None), with bounded cancellable acquisition retries. Under the same cross-process lock re-read/hash, compare tag, preserve invalid bytes for explicit reset, then replace atomically. Bound reads to 64 KiB plus one byte; preserve larger original via bounded-memory file copy for reset backup. Inaccessible source that cannot be backed up cannot be reset. Cancellation never publishes success. Use unique backup names, never overwrite previous backups.
- [ ] Register internal handlers in HostServices; derive root alongside WorkspaceStore in MainWindow. Validate payload before touching disk. Save returns current snapshot on conflict, error on failure; installed fonts are suggestions only. Apply neither renderer nor timer changes in this task. Extend boundary tests proving methods are absent from external live-control/public MCP discovery.
- [ ] Rerun focused store/boundary tests, including competing processes or independently instantiated lock holders; expect PASS. Run standard checks; commit `feat(preferences): persist host settings with conflict protection` and push.

## Task 3: Captured document defaults across canonical operations

**Interfaces:** `readDocumentDefaults(document: DiagramDocument): { defaults: CapturedDocumentDefaults; diagnostic?: string }`; `newDocument(newId?, size?, defaults?: CapturedDocumentDefaults): DiagramDocument` captures explicit supplied defaults and retains legacy defaults when omitted. Serialize metadata canonically with maximum 4096 UTF-8 bytes; validate parser input and preserve malformed source unchanged. `textDefaults(document)` supplies a fresh legacy TextBlock with captured font overlaid.

- [ ] Write failing `documentDefaults.test.ts` cases: custom font/grid capture; createShape/createText/createConnector and first set.text on empty shape/connector use capture; editing existing text retains format; explicit properties win; addPage uses capture; changing global defaults later has no effect. Missing/malformed/oversized metadata falls back with diagnostic without changing snapshot/hash/revision.
- [ ] Run `npm test -- tests/documentDefaults.test.ts tests/newDocument.test.ts`; expect assertions against current Segoe UI/5 mm defaults to fail.
- [ ] Implement metadata helpers and extend newDocument. Replace planner DEFAULT_TEXT use with per-document fresh defaults only at new/first-materialized text paths. Apply captured grid only when addPage supplies no explicit grid. Feed parser diagnostics through existing result/warning mechanisms without mutating document metadata. Copy/import/component/icon explicit formatting wins.
- [ ] Add canonical transaction and DSL tests using the same captured document; assert identical grid/font results, exact undo restoration and recovery replay equivalence. Extend JSON/native Visio metadata roundtrip tests to retain capture and UUIDs; legacy open must not insert metadata.
- [ ] Rerun focused TS and native roundtrip tests; expect PASS. Run standard checks; commit `feat(preferences): capture per-drawing grid and font defaults` and push.

## Task 4: Startup handshake and every fresh-document route

**Interfaces:** bootstrap accepts an explicit immutable initial `AppPreferences` snapshot; a host initialization helper obtains it before calling synchronous startApp. `PreferencesModel.current` is the effective snapshot, `snapshotDefaults()` returns a cloned capture. NewDocumentFlow retains its capture from begin through finish; WorkspaceModel and registry creation consume the same factory. Browser-only mode supplies in-memory defaults with get/save parity and no user disk access.

- [ ] Write failing `preferencesFlow.test.ts` cases for startup blank, File > New, Ctrl+N, plus tab, final-tab fallback and agent registry creation. Assert no `editor.ready`/document-open event before preferences arrive; a slow read must not hang waiting on editor.ready. Read failure uses defaults with visible nonblocking diagnostic. Existing restore/open preserves capture and content.
- [ ] Run `npm test -- tests/preferencesFlow.test.ts tests/newDocument.test.ts`; expect initialization/capture failures.
- [ ] Add the pre-ready host.preferences.get request path before startApp; host routes this request without requiring a live document scope. Keep other document requests guarded. Refactor main/agent startup through this helper; obtain preferences once per view startup, then capture defaults in every creation factory. Do not construct-and-replace a temporary canonical drawing. Retain deterministic browser-only setup.
- [ ] Update NewDocumentFlow to snapshot when picker opens. Add test changing the model defaults while picker is open and assert the resulting drawing retains the original capture. Add native startup/registry checks covering separate visible and agent views.
- [ ] Rerun focused TS/native tests and readiness/boundary regressions. Run standard checks; commit `feat(preferences): initialize all document factories from host settings` and push.

## Task 5: Reschedulable recovery without durability changes

**Interfaces:** `RecoveryManager.UpdateCheckpointIntervalAsync(TimeSpan interval, CancellationToken ct = default)` validates 10–3600 whole seconds and completes after rescheduling. Add an injected `TimeProvider` optional constructor argument; use its timer interface, preserving production system-time behavior and existing options/callers. Tests use a deterministic fake provider with explicit Advance.

- [ ] Extend `RecoveryTests.cs`: pending commit checkpoints at new interval, no-change tick does nothing, 50th commit/save still checkpoint, update preserves session/counters/queued work, invalid interval rejects, update/dispose races cannot enqueue after stop. Test with fake time, not long sleeps.
- [ ] Run `dotnet test tests/Diagram.Host.Tests --filter Recovery`; expect missing-update/seam failures.
- [ ] Implement update on existing synchronization/queue lifecycle; change the existing timer, never replace manager/tracks. Dispose awaits timer callbacks and work as before. Route successful host preferences save through awaited interval update before notifying views. Persistence errors must leave previous timer and live settings untouched; treat shutdown as terminal and reload persisted values next startup.
- [ ] Run focused tests and Windows recovery tests sequentially; demonstrate a native committed drawing remains recoverable after interval change and shutdown/crash scenario. Run standard checks; commit `feat(preferences): safely update recovery checkpoint timing` and push.

## Task 6: Preferences dialog and view-only application

**Interfaces:** `PreferencesModel.open(): Promise<void>`, `save(): Promise<Result<PreferencesSnapshot>>`, `cancel(): void`, `resetDraft(): void`, `reload(): Promise<void>`; typed draft/errors/busy state and subscriptions. Save keeps draft on error/conflict; success applies effective snapshot once. Dialog receives this model. `applyChromeTheme(theme: 'light'|'dark')` sets root data attribute and host-owned shell theme; no document colors are inputs.

- [ ] Write failing model and Playwright cases: Save/Cancel/Escape/Reset, invalid fields, unavailable font note, installed suggestions, stale tag Reload preserving disk, failed write preserving draft; opener focus restored and competing lifecycle/drag/modal actions guarded. Toolbar unit override stays session-local; explicit Preferences Save updates all tab views with zero revision/undo/dirty changes.
- [ ] Run `npm test -- tests/preferencesFlow.test.ts` and `npm run test:browser -- tests/browser/preferences.spec.ts --workers=1`; expect missing-dialog failures.
- [ ] Implement model, Edit > Preferences command and accessible modal with labels separating display/new-drawing/recovery choices. Reset changes draft only; Save sets resetInvalid only following explicit Reset. Grid editor converts displayed units to persisted points without repeated rounding drift. Read fresh host settings on open/apply; conflict offers Reload without silently merging. Expose nonblocking startup diagnostic without automatic reset.
- [ ] Convert editor chrome/ruler/panel hard-coded colors to theme tokens; keep page/canvas document colors and SnapshotRenderer/export untouched. Apply units to open EditorModel views on explicit successful save and notify other initialized agent views of current defaults without altering existing documents. Add host theme application only after successful save; preserve W12 menu/help commands.
- [ ] Capture light/dark browser screenshots. Compare clean export/page pixels and saved document hashes before/after unit/theme saves; assert exact document equality and unchanged exports. Test native Preferences restart, dialog focus and shell styling; OS-owned dialogs may remain Windows-themed.
- [ ] Rerun focused model/browser/native tests. Run standard checks; commit `feat(preferences): add persisted preferences dialog and editor themes` and push.

## Task 7: Whole-branch acceptance and handover

- [ ] Fetch and inspect main and W12 changes; resolve only necessary integration conflicts with coordinated ownership. Rerun checks after any resolution. Do not mark unmerged W05/W08/W10/W11 as dependencies silently satisfied.
- [ ] Run full standard checks and native Preferences/metadata/recovery scenarios sequentially. Save commands, logs, screenshots and output hashes under `artifacts/w13-preferences/`; write `docs/m5/W13-preferences.md` with baseline/head, automated results, initial failures/retries, compatibility notes and manual PASS/FAIL/NOT RUN.
- [ ] Obtain fresh read-only whole-branch review covering storage concurrency, startup readiness, captured defaults, theme/export isolation and recovery shutdown. Fix substantive findings and rerun affected tests plus standard checks before the final push.
- [ ] Commit evidence as `docs(preferences): record W13 verification and remaining acceptance`; push and open/attach a PR linked to #35. Move Project to In review, record approval/verification/remaining limits. Do not close before intended integration or merge automatically.
- [ ] Hand over W14 on its own branch after W13 dependency is available; W14 code is outside this plan.

## Standard checks before each push

Run with Node >=24.14.0 and the repository's existing .NET SDK. Use `C:/Program Files/nodejs` and `C:/Program Files/dotnet` on PATH where needed. Each command must exit zero; retain failed outputs before retrying.

From `src/web`:

```powershell
npm test
npm run typecheck
npm run generate:check
npm run build
npm run test:browser -- --workers=2
```

From repository root (Windows/native suites sequential, no concurrent Office runs):

```powershell
dotnet test Diagram.Windows.slnx -m:1
git diff --check
```

A failing check blocks the push until resolved or specifically documented as an unrelated baseline failure with evidence and user-visible limits; never label an unrun native/manual check PASS.

## Plan self-review and execution gate

Self-review completed: contract bounds, file failure/reset/conflict cases, defaults/legacy behavior, all startup factories, view-only application, recovery races and coordination map to Tasks 1–7. Review Focus cases are explicitly assigned tests. Interfaces above are the names subsequent tasks consume; generated definitions own wire data.

This document is for review, not implementation authorization. Recommended execution: Native, because the seven tasks share startup/host/default interfaces and sequential integration will avoid duplicate wiring; one fresh whole-branch reviewer remains required. User chooses execution method after reviewing this plan.

