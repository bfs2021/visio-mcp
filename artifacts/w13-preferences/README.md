# W13 verification evidence

Acceptance report: [docs/m5/W13-preferences.md](../../docs/m5/W13-preferences.md). Issue [#35](https://github.com/rp2839/visio-mcp/issues/35), branch `codex/w13-preferences`. Main baseline `465de59`; original combined product head `bd15e52`; review fixes `0d74ade` and `a743455`.

All commands use the isolated worktree. npm runs from `src/web`, dotnet/git from repository root, PATH prefixed with `C:/Program Files/nodejs;C:/Program Files/dotnet`. Runtime checked during reconciliation: Node v24.20.0 and .NET SDK10.0.402. `*.log` is normally ignored; these W13 logs are explicitly included as immutable acceptance evidence. Initial/red files are preserved, never overwritten by retries.

## Command index

| Command | Log(s) | Observation |
| --- | --- | --- |
| `npm test -- --maxWorkers=2` | `verification/full-unit.log` | 496/496 at bd15e52 |
| `npm run typecheck` | `verification/full-typecheck.log` | Successful controller run, normal silent compiler output |
| `npm run generate:check` | `verification/full-generated.log` | Successful controller run |
| `npm run build` | `verification/full-build.log` | Successful controller run, eval/chunk warnings retained |
| `npm run test:browser -- --workers=2` | `verification/full-browser-initial.log` | 150/150; first full run succeeded |
| `dotnet test Diagram.Windows.slnx -m:1` | `verification/full-windows.log` + `.result.json` | Exit1, Windows testhost aborted after15 passes; other five suites passed |
| `npm run generate:check`, `npm run build`, `npm run test:browser -- --workers=2` | `verification/post-review-{generated,build,browser}.log` + result JSON per command | Exit0 at0d74ade; browser155/155 |
| `dotnet test Diagram.Windows.slnx -m:1 --logger "console;verbosity=normal"` | `verification/full-windows-retry.log` + result JSON on exit | Exit1 after verified process-tree termination at the10-minute native cutoff; log/result/timeout/termination preserved |
| `npm test -- tests/preferences.test.ts --maxWorkers=2` | `verification/factory-red.log`, `factory-green.log` | Intended missing factory RED, then33/33; companion typecheck/generate logs |
| `npm test -- tests/documentDefaults.test.ts --maxWorkers=2` | `verification/authored-paths-initial.log`, `authored-paths-retry.log` | Incorrect test object selection corrected;11/11 |
| `npm run test:browser -- tests/browser/preferences.spec.ts --workers=1 -g 'saved grid and font'` | `verification/new-routes-initial.log`, `new-routes-retry.log` | Ambiguous test selector corrected;1/1 |
| `npm run test:browser -- tests/browser/preferences.spec.ts --workers=1 -g 'dark merged command'` | `verification/dark-merge-red.log`, `dark-merge-green.log` | Real merged-chrome contrast RED/GREEN;1/1 |
| Focused native Visio acceptance (test name logged; exact original invocation not retained) | `verification/native-visio-initial.log`, `native-visio-retry.log` | Canonical lowercase colour expectations corrected;1/1 |
| `npm run test:browser -- tests/browser/preferencesReview.spec.ts --workers=1` | `verification/final-review-browser-red.log` | Five expected review-regression failures before fix |
| `npm test -- tests/preferencesFlow.test.ts --maxWorkers=2` | `verification/final-review-unit-red.log` | Guard regression failed before fix;11/12 passed |
| `npm run test:browser -- tests/browser/preferencesReview.spec.ts tests/browser/preferences.spec.ts tests/browser/help.spec.ts tests/browser/visio.spec.ts --workers=2` | `verification/final-review-browser-green.log` | 22/22 after0d74ade fix |
| `npm test -- --maxWorkers=2`, `npm run typecheck` | `verification/final-review-unit-green.log`, `final-review-typecheck.log` | 497/497 + typecheck success for0d74ade |
| `npm run test:browser -- tests/browser/preferencesReview.spec.ts --workers=1 -g 'late compatibility'` | `verification/modal-race-red.log` | Two expected delayed-reply failures before rendering arbitration |
| `npm run test:browser -- tests/browser/preferencesReview.spec.ts tests/browser/preferences.spec.ts tests/browser/visio.spec.ts --workers=2` | `verification/modal-race-green.log` | 19/19 aftera743455 fix |
| `npm test -- --maxWorkers=2`, `npm run typecheck` | `verification/modal-race-unit.log`, `modal-race-typecheck.log` | Final product497/497 + typecheck success |

The original frontend runs predate this reconciliation; their completed output and the controller's sequential stop-on-failure runner establish their success. Separate original exit metadata was not retained, so no fabricated result JSON is provided for them. Later wrapper result files identify command, head, UTC start/end and actual exit. Logs with RED/initial names are development history, not final failure suppression.

## Screenshots, reviews and hashes

`screenshots/light.png`, `dark.png`, `dark-dialog.png` are browser-generated captures from the final affected preservation scenario with authored green shape/text. These copies were visually inspected; they are automated evidence, not human acceptance. Earlier task screenshots remain unchanged in the SDD folder.

`reviews/` retains the whole-branch review, first scoped review with residual race and final approved scoped race review. `historical/task-4-report.md` preserves the earlier transient-browser attempt described by acceptance; it is explicitly historical.

`SHA256SUMS.txt` hashes the retained logs/result JSON/screenshots/review and historical artifacts after all running logs are closed. Exact before/after document SHA and clean PNG equality were asserted inside the browser run; the numeric diagram hashes/export bytes were not separately written. Native Visio acceptance compared imported semantics and live document bytes rather than archive ZIP hashes. Manual installed-app and human Office acceptance remain NOT RUN.

## Final acceptance gate on a743455

- `npm test -- --maxWorkers=2`:497/497, `verification/modal-race-unit.log`.
- `npm run typecheck`:PASS, `verification/modal-race-typecheck.log`.
- `npm run build`:PASS, `verification/modal-race-build.log`.
- `npm run generate:check`:PASS, `verification/final-head-generated.log` and result JSON.
- `npm run test:browser -- --workers=2`:157/157, `verification/final-head-browser.log` and result JSON.
- `dotnet test Diagram.Windows.slnx -m:1 --logger "console;verbosity=normal" --blame-hang-timeout 90s --blame-hang-dump-type mini --results-directory artifacts/w13-preferences/verification/native-hang`:558/558, `verification/full-windows-final.log` and result JSON. Breakdown50/194/33/14/156/111 for Contracts/Host/IPC/MCP/Visio/Windows. Every test completed; no dump or sequence file was generated.
- Isolation before the final native retry: `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~CloseTests --no-restore -m:1 --logger "console;verbosity=normal" --blame-hang-timeout 90s --blame-hang-dump-type none --results-directory artifacts/w13-preferences/verification/close-hang`:7/7, `verification/close-hang.log` and result JSON. Every test completed; no sequence file generated.
- `git diff --check`:exit0, `verification/whitespace.log` and result JSON; staged check repeated immediately before evidence commit.

The native crash and later stalled attempt were not reproduced by the final full run and were not proved fixed. No speculative lifecycle patch was made. `historical-outputs-restored.txt` lists tracked artifacts restored after broad tests, preserving existing package evidence.
The local `.gitattributes` preserves exact evidence bytes across platforms; generated log output is treated as an opaque audit artifact for Git diff. The acceptance Markdown remains available for normal source review. SHA-256 covers the preserved raw bytes rather than Git-normalized line endings.

Copied review reports had extra blank lines at EOF; only the versioned copies were normalized to one final newline. Their findings/content and the original SDD reports are unchanged. `whitespace-staged-initial.log` retains that failed documentation-only check; final staged whitespace verification passed after normalization.
