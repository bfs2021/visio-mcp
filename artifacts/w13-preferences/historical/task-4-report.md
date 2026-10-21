# W13 Task 4 — startup and fresh-document captures

Implemented on `codex/w13-preferences`, starting from ecc4a9b. Controller owns issue #35 claim and current-main coordination. No push or merge performed.

## Behavior and handoff

- Both entry points await `initializePreferences(webview?)` before calling synchronous bootstrap. It returns `{ client?, preferences, preferencesService }`. One BridgeClient survives the handshake; no temporary canonical drawing or ready/open announcement is created while settings load. BridgeClient's existing 15-second timeout falls back to defaults.
- `startApp(webview, root, hooks)` and `startAgentApp(webview, hooks)` accept `initialPreferences?: AppPreferences`, `preferences?: PreferencesModel`, and `client?: BridgeClient` in hooks. Both return `preferences`. Production passes the helper result; synchronous tests may pass an explicit snapshot. Model construction and getters copy input/output.
- `PreferencesModel(initial?, versionTag?, diagnostic?)` exposes cloned `current`, `snapshotDefaults()`, `subscribe`, and `apply(value, versionTag?)`. Apply validates and is intended only after successful persistence. Task 6 extends this minimal model with dialog state. `initialPreferences.preferencesService` in main provides typed get/save; browser memory storage has validation, version checking, and isolated per-view state, without localStorage.
- Startup, workspace new tabs, plus, final-tab fallback, and agent registry factories capture defaults. Workspace adoption wires NewDocumentFlow to the same model. New captures at begin, retaining its values through size/confirmation/save. Existing document payloads bypass the factory; opening/restoring keeps authored content.
- Failed/malformed reads retain defaults and a diagnostic. Main displays the diagnostic using the existing nonblocking warning announcement (`firstModel.say`). Settings-dialog retention/display is Task 6.
- Native internal agent channel now permits exactly `host.preferences.get` in addition to existing ping. Save and file services remain denied. External pipe/discovery unchanged.
- Controller ruling fixed Task 2's unsupported `conflict` error code: host and browser memory use existing `revision_conflict` with `details.reason = preferences_changed` and the current snapshot/tag. No public schema addition. Task 6 should preserve draft and offer Reload for that reason.
- Agent preferences currently load once per view startup; successful-save publication/refresh is Task 6. Units/theme/font listing/recovery timing/dialog are outside this task.

## Test-first evidence

1. Lifecycle regression failed as expected: startup grid was legacy 14.173228346456694 instead of 42. After factory/model wiring: 7/7 focused tests passed.
2. Agent registry regression failed with the same legacy-grid mismatch before agent wiring.
3. Handshake tests initially could not import the absent helper; a minimal signature stub then produced behavioral RED: startup occurred immediately and read failure had no diagnostic. Implemented helper; all handshake tests passed.
4. Native channel test RED: pre-ready agent get never reached the host. Save/open denial cases already passed. Narrow read exception produced GREEN with all 32 ChannelTests/RouterTests.
5. Added integration/memory/open-preservation/timeout regression coverage. These are supplementary checks, not separately claimed RED cycles.

An initial test-write command used an incorrect doubled src/web path and wrote no file; corrected before the lifecycle RED run. Typechecking caught unsupported conflict typing; controller ruled existing revision_conflict. Initial native test compile used a nonexistent defaults helper name, corrected to PreferencesValidation.DefaultPreferences before running integration tests.

## Fresh verification

- `npm test -- tests/preferencesFlow.test.ts tests/newDocument.test.ts`: final 13/13 PASS (includes controlled 15-second silent-host timeout).
- `npm test`: 46 files, 445 tests PASS. This run preceded the final additional timeout-only test; that addition passed focused verification.
- `npm run typecheck`: PASS after final test addition.
- `npm run generate:check`: PASS.
- `npm run build`: PASS after final production changes. Existing maxgraph direct-eval/chunk-size warnings remain.
- `dotnet test tests/Diagram.Host.Tests --filter 'FullyQualifiedName~ChannelTests|FullyQualifiedName~RouterTests' --no-restore -v minimal`: 32 PASS.
- `dotnet test tests/Diagram.Windows.Tests --filter 'FullyQualifiedName~PreferencesHostTests|FullyQualifiedName~LifecycleMethodsAreNotReachableOverThePipe' --no-restore -v minimal`: 4 PASS. Real WebView2 visible startup and separate agent registry capture persisted grid/font, with zero initial revision.
- After simplifying the native conflict payload construction, `dotnet test tests/Diagram.Windows.Tests --filter 'FullyQualifiedName~PreferencesHostTests' --no-restore -v minimal`: 3 PASS.
- `npm run test:browser -- tests/browser/newDocument.spec.ts tests/browser/tabs.spec.ts tests/browser/workspace.spec.ts`: 20 PASS, 1 FAIL. `past the limit a new tab is refused with a message` reported destroyed execution context/navigation while source whitespace cleanup ran concurrently; Vite reload is the inferred cause. With edits stopped, `npm run test:browser -- tests/browser/tabs.spec.ts --grep 'past the limit'`: 1 PASS. Initial failure is retained here; no claim of an uninterrupted 21/21 run.
- `git diff --check`: PASS. Generated performance-timing output from unit tests was reverted as unrelated noise.

Existing native warnings: xUnit1031 in MultiDocumentTests and CS8631 in CrashRecoveryTests. Browser emits NO_COLOR/FORCE_COLOR warning.

## Files and self-review

Production: bridge/bootstrap.ts, bridge/agentApp.ts, bridge/initializePreferences.ts, preferences/PreferencesModel.ts, main.tsx, agentMain.ts, editor/NewDocumentFlow.ts, workspace/WorkspaceModel.ts, BridgeRouter.cs, HostServices.Preferences.cs.
Tests: preferencesFlow.test.ts, ChannelTests.cs, PreferencesHostTests.cs.

Reviewed transport creation for duplicate listeners/correlation collisions, native channel routing before readiness, capture cloning and picker stability, and authored document bypass. No changes to public MCP methods, document schema, or persisted documents on open. No independent agent review was launched (controller owns review). Full Windows/browser suites and fresh human acceptance: NOT RUN here; controller owns final combined verification. Existing browser tests cover File > New/Ctrl+N/plus/close/restore routes; custom preference values are checked at shared factories and native startup/agent integration, not through a new settings UI (Task 6).

## Review fix round 1

Review found the initial editor's status warning disappeared when workspace restoration selected another tab. Removed firstModel.say wiring; WorkspaceModel exposes the shared model's preferencesDiagnostic, and TabStrip renders it as a persistent, nonblocking role=status banner independent of per-tab status, transient tab notices, and recovery banners. This also avoids clobbering tab error messages. Task 6 can clear the shared diagnostic after a successful reload/reset; the view reads that shared value.

RED: `npm run test:browser -- tests/browser/workspace.spec.ts --grep 'preferences read warning'` failed (1/1) after two saved tabs were restored and the second activated: preferences-diagnostic element absent. The fixture injects a real bridge read failure before startup, then exercises restoreWorkspace and tab clicks. GREEN: `npm run test:browser -- tests/browser/workspace.spec.ts` passed 6/6 with edits stopped for the entire run, including the new warning-persistence regression and existing restore cases. `npm test -- tests/preferencesFlow.test.ts tests/workspace.test.ts` passed 13/13. `npm run typecheck` passed. Full suites were not repeated for this minimal fix per controller scope. Existing NO_COLOR/FORCE_COLOR browser warning remains.

Changed files: main.tsx, workspace/WorkspaceModel.ts, components/TabStrip.tsx, tests/browser/workspace.spec.ts. Self-review: persistent warning is not tied to active tab, restore banner replacement, or tab notice clearing; no document mutation or modal behavior added.
