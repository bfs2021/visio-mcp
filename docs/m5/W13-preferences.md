# W13 — persisted preferences and new-document defaults

Issue [#35](https://github.com/rp2839/visio-mcp/issues/35), branch `codex/w13-preferences`, evidence reconciled 2026-10-20. Product implementation and focused task reviews are complete. Whole-branch and scoped reviews are complete; final automated results are recorded below. Publication remains controller-owned; this report does not approve integration or human acceptance.

## Revision and integration provenance

- Original main baseline: `e67f2e7c007b48e370006b465242e65d00906d10`; implementation started above `eb9211dfa2cf1f9bbee4c0d12d3cf3f017fb6c8c`.
- Integrated current main: `465de59a60301ae70669d8f5fd0ae9d2fee38357`, merged into this branch at `09af9be`. The controller handled issue ownership and upstream coordination before this evidence continuation.
- Tested product head: `bd15e52c38f07c8e0660dff73985992d5ba33657` was the first combined acceptance head. Review fixes are `0d74ade18edf9001961b1fb5ca194c16a6cd775c` (Inspector captured defaults/custom font and existing-report guards) and `a74345513811c6e6be7b0d54bec69ba99af905ec` (deferred late report rendering). Final product head is `a743455`. This evidence commit changes documentation/evidence only.
- Main includes W05 (`4f23028`), W08 (`e2defc8`), W10 (`9cd4cf8`), W11 (`9e2fb3f`), W12 (`a04f2b8`), W15 (`61de40c`), W16 (`df2d5c2`) and W17 (`465de59`). They are integrated through main, rather than assumed available from their former review branches. The W13 merge preserved shared commands/guards and regenerated validators; final full suites below cover that combined tree.

## Standard checks

Commands ran in the isolated feature worktree with `C:/Program Files/nodejs` and `C:/Program Files/dotnet` on PATH. Node `v24.20.0`, .NET SDK `10.0.402` were confirmed during acceptance reconciliation. npm commands use `src/web`; dotnet and git commands use the repository root.

| Command | Result and evidence |
| --- | --- |
| `npm test -- --maxWorkers=2` | PASS, final 53 files / 497 tests; [final log](../../artifacts/w13-preferences/verification/modal-race-unit.log). Original 496-test run retained as `full-unit.log`. |
| `npm run typecheck` | PASS after final fix; [final log](../../artifacts/w13-preferences/verification/modal-race-typecheck.log). Original log retained. |
| `npm run generate:check` | PASS on final a743455; [final log](../../artifacts/w13-preferences/verification/final-head-generated.log), with exit/result JSON. Original successful log retained. |
| `npm run build` | PASS after final fix; [final build log](../../artifacts/w13-preferences/verification/modal-race-build.log). Original successful log retained. |
| `npm run test:browser -- --workers=2` | PASS, final uninterrupted **157/157** on a743455; [final log](../../artifacts/w13-preferences/verification/final-head-browser.log) and result JSON. Earlier full150/full155 and targeted19 logs remain preserved. |
| `dotnet test Diagram.Windows.slnx -m:1` | PASS, final **558/558**: Contracts50, Host194, IPC33, MCP14, Visio156, Windows111. [Final log](../../artifacts/w13-preferences/verification/full-windows-final.log) and [result JSON](../../artifacts/w13-preferences/verification/full-windows-final.result.json) identify final a743455 and exit0. Initial crash and timeout attempts remain below. |
| `git diff --check` | PASS, exit0; [log/result](../../artifacts/w13-preferences/verification/whitespace.result.json). Final staged check also required before commit. |

The original full-* frontend logs are retained successful controller runs against bd15e52, inspected rather than repeated without cause. The table above points to final-head verification performed after the substantive review fixes. Their sequential runner stops on a nonzero exit; later completed build/browser logs corroborate the normally silent typecheck/generated checks. Those original logs do not contain separate process-exit metadata. Review changes justified subsequent full/focused verification; each result above identifies its actual tested head. The missing full Windows solution run was started fresh by this continuation and records its timestamps/exit1 in `full-windows.result.json`; it is not a success. Earlier task-specific checks are historical evidence, not substitutes for the combined suites.

Build warnings remain for maxgraph direct eval and a chunk larger than 500 kB. Native test output retains compiler/analyzer warnings in existing schema parity, multi-document and crash-recovery tests. Browser harness colour-environment warnings do not represent application failures.

## Acceptance covered by automation

- Shared schema parity and bounds; fresh independent generated defaults; bounded host storage; missing/corrupt/future files; reset backups; failed writes; stale tags and concurrent application instances.
- Preferences load before readiness, with safe failure diagnostics that survive restored-tab activation. Startup, File New (including custom page), Ctrl+N, plus tab, final-tab fallback and registry/agent factories use captured defaults. A picker retains its opening snapshot.
- Captured font/grid metadata supplies add-page, shape/text/connector, GUI/MCP/DSL creation. Explicit formatting wins. Legacy/malformed metadata remains unchanged and falls back with diagnostics. Components/icons retain authored formatting through copy/paste and JSON import.
- Save/Cancel/Escape/Reset, invalid fields, installed/unavailable fonts, conflict/Reload, failed-write draft retention, focus containment/restoration, lifecycle and modal guards, all-tab units and session-only toolbar overrides.
- Light/dark chrome, including merged palette/find/recent controls, meets tested contrast thresholds. Existing document content, revision, dirty/undo status and export colours stay unchanged by view choices.
- Deterministic recovery timing retains pending work, counters, commit/save thresholds and sessions; controlled in-flight update/disposal races settle without later enqueues. Native preference/recovery checks cover persistence, restart and recovered drawing durability.

The full browser preservation case creates a nonempty authored green shape/text, compares document JSON SHA-256 before/after Save, asserts zero changed cropped page RGBA values, identical clean `doc.render` PNG base64 and unchanged revision. The hashes and clean export bytes are assertions within that run, not separately retained exported files. Native Visio acceptance exports/imports before and after Save, compares authored style/text/page background and exact live document bytes. It checks semantic colours/formatting, not ZIP-byte equality (native archives can differ in packaging metadata).

Final light/dark/dialog screenshots are in [artifacts/w13-preferences/screenshots](../../artifacts/w13-preferences/screenshots). They were generated by the final browser preservation scenario and visually inspected during reconciliation: chrome changes while the authored shape and white page remain visible. This agent inspection is not human manual acceptance. [SHA256SUMS.txt](../../artifacts/w13-preferences/SHA256SUMS.txt) identifies retained evidence bytes. Historical screenshots/PERF artifacts regenerated by broad tests were restored; the W13 copies preserve this run without rewriting earlier evidence.

## Initial failures, red tests and retries

All retained initial/red logs remain under [verification](../../artifacts/w13-preferences/verification). A retry filename alone is not evidence of a transient product defect.

| Initial evidence | Cause and subsequent evidence |
| --- | --- |
| `factory-red.log` | Expected TDD failure: generated `createPreferencesDefaults` was absent. `16421dc` added the factory; `factory-green.log` passes 33 tests, with typecheck/generate logs. |
| `dark-merge-red.log` | Real contrast regression in newly merged palette/find/recent chrome. Tokens fixed in `bd15e52`; `dark-merge-green.log` passes, and the full browser run includes it. |
| `authored-paths-initial.log` | Test selected the first shape (a component shape), rather than the newly inserted icon, and expected the icon colour. Selection corrected to the returned icon element ID; `authored-paths-retry.log` passes 11 tests. |
| `new-routes-initial.log` | Strict locator matched both component width and new-page width. Scoped to the New dialog; `new-routes-retry.log` passes the route scenario. |
| `native-visio-initial.log` | Assertion expected uppercase hex while native import canonicalizes hex to lowercase. Corrected expected canonical text; `native-visio-retry.log` passes 1/1. Authored colours were preserved semantically. |

Earlier task reports retain their own failure history. Task 4 had 20/21 browser tests pass, followed by a targeted retry of the tab-limit test after source editing stopped; Vite reload was the inferred transient cause, not proven. Task 6 corrected an export screenshot selector matching hidden renderer pages and verified the native-close notification and dark diagnostic fixes RED/GREEN. These historical attempts are distinct from the final uninterrupted 150-test browser success. See [Task 4 report](../../artifacts/w13-preferences/historical/task-4-report.md) and [Task 6 report](../../.superpowers/sdd/2026-10-19-w13-preferences/task-6-report.md).

## Compatibility and remaining acceptance

No public MCP method or document schema-version change. Internal preferences get/save stay outside the external live-control allowlist; agent bootstrap permits the narrow read needed before readiness. `editor.defaults.v1` stores grid/font only. Existing drawings receive no migration or automatic reformatting. Browser tests use deterministic memory preferences; native tests use isolated roots. A valid unavailable font is retained and uses platform fallback; no font download is performed.

Application instances serialize preferences writes and detect stale tags; a noncooperating external writer can still race after the final tag check. Corrupt/future files remain untouched until explicit reset with backup. Cross-process live theme synchronization is not required. OS-owned dialogs may retain Windows styling. Native WPF/background/restart are asserted, but titlebar appearance is not pixel-certified.

| Human/manual acceptance | State |
| --- | --- |
| Installed-app Preferences feel, keyboard/focus, light/dark shell and titlebar | NOT RUN |
| OS-owned dialogs and unavailable-font appearance on representative machines | NOT RUN |
| Human restart/recovery/Visio inspection with personal drawings | NOT RUN |
| Fresh independent whole-branch review | Original review plus two scoped re-reviews complete; final scoped verdict approved a743455 subject to acceptance. See retained reviews. |

Tasks 1–6 and Task 7 code are complete. This package records automated evidence and remaining limits. The controller owns push/PR attachment and issue/Project handover after acceptance. No push, PR, Project transition, issue closure, main merge or W14 implementation was performed by this acceptance continuation.

## Final review resolutions

[Whole-branch review](../../artifacts/w13-preferences/reviews/final-review.md) covered storage, startup, captured defaults, theme/export isolation and recovery. It found two Important GUI issues. [First scoped review](../../artifacts/w13-preferences/reviews/final-fix-review.md) confirmed the Inspector and existing-report guard fixes in `0d74ade` but found a residual delayed lifecycle reply race. [Final scoped review](../../artifacts/w13-preferences/reviews/modal-race-review.md) approved `a743455`, subject to acceptance.

The Inspector now shows document-captured Aptos 19 for absent shape text/connector labels, retains a custom family option, and commits an explicit choice of legacy Segoe UI 12 before first text. Five browser regressions and a direct workspace guard test failed RED before the fix; affected browser22/unit497/typecheck then passed. A compatibility report already open blocks Preferences and the Ctrl+Shift+P palette route. A report arriving from an in-flight export is retained and waits until Preferences closes; both Cancel and Save delayed-reply cases failed RED before the rendering fix, then the affected19/unit497/typecheck checks passed. All red/green logs remain in verification.

Minor review limit, deferred: malformed captured metadata falls back safely and is preserved with parser/transaction diagnostics, but ordinary open/read does not surface a visible warning. No new load warning is claimed. This deferred UX gap does not change the tested byte/revision preservation behavior.
## Full native attempts and diagnostic retry

The initial full solution attempt at bd15e52 exited1: the five non-Windows suites passed, then Windows testhost aborted after15 passes with an unhandled `CoreWebView2` disposed-control message callback at `WebViewHost.cs:86`. The normal-logger retry using built frontend0d74ade passed the same five suites and17 Windows tests, then stopped progressing after `CloseTests.ADocumentWithNoChangesClosesWithoutAsking`. Its exact in-flight test was not identified by that uninstrumented run. After10 minutes of native runtime with unchanged progress, the verified run process tree alone was terminated; the original log, exit1 result, timeout record and termination output are preserved.

A bounded focused `CloseTests` run on the final a743455 build passed7/7 in17.4 seconds. No close/shutdown patch was justified. The final full solution run on that same final build added normal console logging, `--blame-hang-timeout 90s`, `--blame-hang-dump-type mini` and a W13 results directory; its exact arguments/timestamps are in the result JSON. It exited0 with all558 tests passing, including111 Windows tests in6.2057 minutes. No dump or sequence file was generated because all tests completed.

These are intermittent crash/stall observations with a successful full diagnostic retry, not confirmed baseline failures or fixed lifecycle defects. Read-only shutdown inspection found a possible agent transport-stop gap, but it was not established as the cause and no speculative lifecycle change was made. The preserved initial failures remain a stability limit; the final pass does not prove their root cause resolved.
