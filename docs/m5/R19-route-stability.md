# R19 Route stability — implementation and evidence

Issue #58. This report covers the R19 planner from PR #72, route persistence and modes from R20, and the engine/MCP integration completed on `codex/r19-route-stability`.

## Behavior

`src/web/src/layout/routePlanner.ts` is the shared pure planner used by the live canvas and the hidden snapshot projection. Transaction edits run through the same planner after join settlement and before attachment settlement. The stored advisory route remains the stability preference under the approved R20 contract; route updates join the same atomic diff and undo history as the user's edit.

For each automatic orthogonal connector, planning tries to keep a clear prior route, elastically adjust endpoint runs, repair the blocked portion locally, search for a new route, and finally retain the best available points with an explicit unresolved finding. Frozen connectors never search. A blocked Frozen route may still adjust its end runs to keep glue when doing so introduces no new hard-obstacle crossing; it remains unresolved and reports blocker IDs. If following would newly cross an obstacle or cannot preserve the route safely, the stored points are retained.

Transaction results include `routeChanges` for connectors whose route provenance changed and `routeFindings` for structured diagnostics. `get_objects` returns canonical objects with `routeDiagnostics` from the same revision-bound projection; `inspect_layout`, projection/export, and MCP therefore report the same unresolved route without mutating the document. If projection is unavailable, `get_objects` still returns canonical objects, an empty diagnostics list, and `routeDiagnosticsStatus` explaining why diagnostics could not be produced.

## Acceptance evidence

| R19 acceptance criterion | Result | Evidence / limit |
|---|---|---|
| Baselines for PSGL, Lego, 60/60 stress, and unit fixtures with coordinate/topology comparisons | **PARTIAL** | The seeded 60-shape / 60-connector route snapshot and unit fixtures are checked in at `src/web/tests/baselines/route-planner-stress-60.json`. Original PSGL and `lego-infra-r08b.vsdx` files are not in the repository, so original coordinate/topology comparisons remain **NOT RUN**. |
| A 1 pt unrelated nudge leaves unaffected routes identical and reports no unexpected changed IDs | **PASS** | `routeModes.test.ts` checks a far shape nudge; `routePlanner.test.ts` checks the 60/60 impact set and exact route stability. |
| Moving an endpoint preserves interior tracks while maintaining glue | **PASS** | `routePlanner.test.ts` checks elastic endpoint movement and unchanged interior route coordinates; `routeModes.test.ts` checks persisted advisory endpoints and undo. |
| A hard obstacle yields a valid detour or explicit bounded unresolved failure | **PASS** | Unit coverage exercises local repair, impossible routes, hard blockers, and Frozen routes that follow moved ends through existing blockers only when no new obstacle crossing is introduced. These routes remain unresolved with blocker findings. The seeded 60/60 planner test has a 5 s bound; its measured segment-commit browser check completed in **60 ms** against the 1.5 s target. |
| One planner gives consistent geometry for canvas, hidden projection, inspection, export, and MCP | **PASS** | Browser test `R20: the inspector freezes a route...` verifies the same frozen route and unresolved finding in transaction output, export projection, `inspect_layout`, and `get_objects` at one revision. The canvas and hidden projector both call `MaxGraphAdapter.project`, which uses `planRoutes`. |
| Provenance and structured failures are available in projection, `get_objects` / `inspect_layout`, and transaction result | **PASS** | `routeModes.test.ts`, `readtools.test.ts`, and the browser cross-surface test assert provenance, reason, blocker IDs, and unchanged document hashes for reads. A projection-failure regression confirms `get_objects` preserves canonical reads and reports diagnostics as unavailable. |
| Drag latency and routing time remain within measured budgets | **PASS** | 60/60 segment move: **60 ms**, below the browser target of 1,500 ms. The planner’s 60/60 fresh-plan fixture remains deterministic and exact with a 5,000 ms unit-test bound. |

## Fresh verification

| Check | Result |
|---|---|
| Web unit suite | **648 passed**, 67 test files |
| TypeScript | `tsc --noEmit` passed |
| Generated contracts | `tools/generate-contracts.mjs --check` passed |
| Browser: R19 nudge, R20 frozen route diagnostics, and merged R23 level-routing checks | **3 passed**; R20 rerun after the Frozen follow fix also passed |
| Browser: 60/60 segment edit latency | **1 passed**, 60 ms |
| Portable .NET solution | **489 passed**: Contracts 50, MCP 14, Host 224, IPC 33, Visio 168 |
| Original PSGL/Lego source comparison | **NOT RUN** — original files are absent |
| Manual owner check in the editor and Visio | **NOT RUN** — requires a human review session |

The portable .NET run reported existing test-source analyzer/nullability warnings; all tests passed. Browser tests were launched with a temporary config that called the installed Vite executable directly because `npm`/`npx` are not on this machine's `PATH`; that temporary config was removed after verification.

## Remaining before R19 can be marked Done

- Add or supply the original PSGL and Lego VSDX source files, then commit coordinate/topology baselines for them.
- Complete the manual owner check in the editor and, where applicable, confirm the behavior in Visio.

Reconstructed samples do not replace those original-source comparisons. Issue #58 should remain open until both remaining checks are reviewed and accepted.
