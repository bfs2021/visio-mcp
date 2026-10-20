# W13 Task 7 — combined acceptance and evidence

Continuation on `codex/w13-preferences` in `C:/Users/claude/.codex/worktrees/r03-icons/diagram-app`, 2026-10-20. Original main baseline `e67f2e7`; implementation start `eb9211d`; integrated main/review base `465de59` through merge `09af9be`. Tasks1–6 and initial Task7 code were complete at `bd15e52`. The controller retained issue#35 ownership/coordination throughout. No primary checkout edits, push, PR, Project transition, issue closure, main merge or W14 runtime work were performed by this continuation.

## Review resolutions and commits

- Whole-branch static review found two Important issues: legacy Inspector absent-text defaults and a compatibility-report modal guard omission. Verified the causes against planner/UI paths, then added regressions before product edits.
- `0d74ade18edf9001961b1fb5ca194c16a6cd775c`: shared document `textDefaults` fallback, custom family option, `t.report` admission guards for Preferences and keyboard palette. Browser RED5/5 and direct workspace RED1/12, then affected browser22/22, full unit497/497 and typecheck passed.
- First scoped review confirmed those fixes but found a residual in-flight host reply race. `a74345513811c6e6be7b0d54bec69ba99af905ec` retains a late compatibility report and defers rendering until Preferences closes. Two delayed-reply Cancel/Save cases failed RED first; affected browser19/19, full unit497/497, typecheck and final build passed.
- Final scoped read-only review approved a743455 subject to acceptance. The three review artifacts are copied into `artifacts/w13-preferences/reviews/`. No lifecycle fix has been made speculatively.

## Evidence reconciliation

Conclusive controller logs at bd15e52 were inspected, not rerun without cause: unit496, full browser150, typecheck, generated and build success. Silent compiler/generated logs are corroborated by the controller's sequential stop-on-failure runner and later completed steps; separate original exit metadata did not exist and was not fabricated.

Review changes justified subsequent verification: full generated/build/browser155 passed at0d74ade; after the final rendering change, affected browser19/full unit497/typecheck/build passed against the final product source. Final-head generated verification and full browser157/157 subsequently passed on a743455, with separate logs and exit0 metadata. Initial/red/retry logs are preserved. Generated historical screenshots, performance output and native exports are restored after the suite completes, with current W13 screenshots copied into this package.

| Native command/run | Actual disposition |
| --- | --- |
| `dotnet test Diagram.Windows.slnx -m:1` atbd15e52 | Exit1. Contracts50, Host194, IPC33, MCP14, Visio156 passed; Windows testhost crashed after15 passes with disposed-CoreWebView2 Post callback, `WebViewHost.cs:86`. Original full log and result JSON retained. |
| Full retry with normal logger, built frontend0d74ade | Exit1 after explicit timeout termination. Same five suites passed; Windows17 passed then no progress. Verified exact process identity and terminated only its tree after the agreed10-minute native cutoff. Log/result/timeout/termination evidence retained. |
| Focused `CloseTests` with `--blame-hang-timeout 90s --blame-hang-dump-type none` on final build | PASS7/7 in17.4s. No sequence generated because every test completed. No close/shutdown patch was justified by this isolation run. |
| Full solution with normal logger and `--blame-hang-timeout 90s --blame-hang-dump-type mini` on final builda743455 | PASS558/558: Contracts50/Host194/IPC33/MCP14/Visio156/Windows111, exit0. Windows duration6.2057 minutes. No hang dump/sequence generated because every test completed. |
| Final `git diff --check` and staged diff check | PASS exit0; final staged check also verified before commit. |

The disposed-control failure was not reproduced by the first retry, which stalled instead. Source inspection found an agent shutdown path without transport Stop, but this remains a hypothesis rather than a demonstrated root cause; no speculative fix is claimed. The final full native pass clears that commit gate. The earlier crash/stall remain intermittent stability observations, not a root cause proven fixed.

## Deliverables and remaining limits

`docs/m5/W13-preferences.md` contains revision/integration provenance, exact standard checks, failure/retry history, automation scope, compatibility and manual NOT RUN. The design/plan authorization text and completed Task1–6 checkboxes now reflect the recorded user approval and execution; review/publication/acceptance gates remain honest. Evidence index, retained logs/results, screenshots, review copies, historical Task4 report and SHA-256 manifest live under `artifacts/w13-preferences/`.

Human installed-app/titlebar/dialog/font/recovery/Visio acceptance: NOT RUN. Browser/native automation and agent screenshot inspection are not human sign-off. Deferred Minor: malformed captured metadata has safe read-only fallback and parser/transaction diagnostics but no visible warning on ordinary open/read. OS dialogs can retain Windows theme; noncooperating external writers can race after the last tag check; cross-process live theme sync is excluded. Native ZIP-byte equality is not claimed; authored semantics/live document bytes and browser clean PNG/page pixels/hash equality are tested.

Publication and W14 handover remain controller-owned after acceptance. The full native and whitespace gates cleared before this evidence-only commit; controller publication remains outstanding.
