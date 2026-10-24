# R24 Aesthetic Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add deterministic aesthetic route scoring, explicit crossing policy, and a read-only Beautify preview that commits the accepted route set atomically with one undo.

**Architecture:** Reuse R26's connector-contact geometry as a shared pure module after PR #90 merges. Preserve R19's implicit planner and add a bounded opt-in cost profile. Build editor preview from the same planner, bind acceptance to base revision and canonical route hash, and apply eligible routes through one transaction.

**Tech Stack:** TypeScript, React, JSON Schema, generated TypeScript/C# contracts, Vitest, Playwright, .NET contract and Windows tests.

**Spec:** `docs/design/R24-aesthetic-routing.md`; issue #63.

## Global Constraints

- Do not start implementation until PR #90 (R26) is merged; fetch the updated main, fast-forward/rebase `codex/r24-aesthetic-routing`, and confirm the branch is based on that merge.
- Preserve implicit R19 routing exactly when crossing policy is absent: 1 quality point per path point, 24 per bend, the current shared-run cost, and connector-ID ordering.
- Use 6 pt track spacing, 0.5 pt overlap tolerance, 10 pt minimum alongside length, and 2 pt short-run diagnostic threshold.
- Beautify uses 80,000 expanded states per target search and skips only targets that exhaust the budget or have no feasible candidate.
- Hard constraints, Frozen routes, locked elements/layers, static ports, R16 joins and R21 constraints always win; skip infeasible targets and continue.
- Use one undoable transaction; stale base revisions or mismatched preview hashes reject the entire commit before mutation.
- Optional fields preserve existing document serialization and schema version 1; generate TS/C# contracts from JSON Schema.
- Do not modify `IMPLEMENTATION_PROGRESS.md`; GitHub issue #63 is the status/ownership record.

## Review Focus

- PR #90 may change under review; refresh its merged result and preserve its finding codes and evidence while extracting shared contact classification.
- A route can overlap its own adjacent search edges at grid nodes; count a geometric contact once using merged intervals and R26's endpoint tolerance.
- A candidate may improve crossing count while worsening path length or deviation; compare the lexicographic score and hysteresis exactly as specified.
- A target can be infeasible while another is routable; retain the failed target's advisory and still apply the feasible subset.
- Preview measurements and commit measurements can diverge; hash rounded, canonical target routes and reject any mismatch.

---

## File map

- Create `src/web/src/layout/routeContacts.ts`: shared pure connector-contact classification extracted from R26.
- Modify `src/web/src/layout/inspectRoutes.ts`: adapt shared contacts to existing stable diagnostics and evidence.
- Create `src/web/src/layout/routeQuality.ts`: the R24 score types, constants, deterministic metrics and score function.
- Modify `src/web/src/layout/obstacleRoute.ts` and `routePlanner.ts`: opt-in search costs and target-only Beautify planning; leave normal no-policy behavior unchanged.
- Modify `contracts/document.schema.json` and `contracts/operations.schema.json`: optional document/connector crossing policy, routing priority, required side, defaults operation and Beautify operation. Modify `contracts/protocol.schema.json` for the required-side route finding; regenerate TS/C# DTOs.
- Modify `src/web/src/commands/planPatch.ts`, `CommandEngine.ts`, and `commands/routes.ts`: validate policy and required-side requests, build and apply a route-hash-bound atomic Beautify plan, and bypass broad hard-route preflight only for target failures that were skipped without mutation.
- Create `src/web/src/editor/beautifyPreview.ts` and `src/web/src/components/BeautifyDialog.tsx`; modify `EditorModel.ts`, `components/PageCanvas.tsx`, and `editor/appCommands.ts` for preview overlay, stale state, accept/cancel and menu entry.
- Add focused tests beside route planner, operations, editor and browser behavior; update `docs/m5/R24-aesthetic-routing.md` with exact results and remaining limits.

### Task 1: Extract shared R26 connector-contact classifier

**Files:**
- Create: `src/web/src/layout/routeContacts.ts`
- Modify: `src/web/src/layout/inspectRoutes.ts`
- Test: `src/web/tests/inspectRoutes.test.ts`
- Test: `src/web/tests/routeContacts.test.ts`

**Interfaces:**
- Consumes: routes `{ connectorId: string; points: readonly Point[]; mode: RouteMode; joinConnectorIds: readonly string[] }[]`.
- Produces: `classifyRouteContacts(routes): RouteContact[]`; each contact has sorted connector IDs, both segment indices, `classification`, and a point, overlap interval or gap as appropriate. R26 maps these values to its existing finding codes/evidence.

- [ ] Add `routeContacts.test.ts` cases for perpendicular crossings regardless of hop rendering, collinear overlap, T-join, endpoint contact, shared-end overlap, near miss at 5.99 pt, not-near-miss at 6 pt, endpoint tolerance, declared R16 joins, and adjacent segments that touch only at a route vertex; verify stable output under input order reversal.
- [ ] Run `npm --prefix src/web test -- tests/routeContacts.test.ts` and confirm the new classifier tests fail before the module exists.
- [ ] Move only pure segment/contact geometry from R26 `inspectRoutes.ts` to `routeContacts.ts`; export shared constants `TRACK_SPACING_PT=6`, `OVERLAP_PT=0.5`, `ALONGSIDE_PT=10`, and retain `SHORT_PT=2` for route-shape diagnostics.
- [ ] Adapt `inspectRoutes` to map returned contacts to unchanged R26 codes and evidence; update the R26 expected fixture only for the 4-to-6 pt near-miss threshold, and document the exact changed findings.
- [ ] Run `npm --prefix src/web test -- tests/routeContacts.test.ts tests/inspectRoutes.test.ts tests/routePlanner.test.ts` and `npm --prefix src/web run typecheck`; expect all focused tests and typecheck to pass.
- [ ] Commit `refactor: share route contact classification`.

### Task 2: Add deterministic opt-in route quality scoring

**Files:**
- Create: `src/web/src/layout/routeQuality.ts`
- Modify: `src/web/src/layout/obstacleRoute.ts`
- Modify: `src/web/src/layout/routePlanner.ts`
- Test: `src/web/tests/routeQuality.test.ts`
- Test: `src/web/tests/routePlanner.test.ts`

**Interfaces:**
- Consumes: `RouteQualityInput { route, previous?, otherRoutes, labelRects, obstacleRects, crossingsPolicy }`.
- Produces: `scoreRoute(input): { policyCrossings: number; qualityPoints: number }`; `planRoutes` gains optional `beautifyTargets?: ReadonlySet<string>` and `costProfile?: 'legacy' | 'aesthetic'`. No arguments means the exact current R19 behavior.

- [ ] Test each fixed score term: route length, bend, crossing, overlap, near-miss deficit, label intersection, clearance deficit, short-run deficit and reversal; assert weights and units from the spec.
- [ ] Test symmetric route deviation with collinear-interval merging, its 12-points/pt weight, 48-point hysteresis boundary, and that `avoid` beats `allow` only by policy-crossing count.
- [ ] Run `npm --prefix src/web test -- tests/routeQuality.test.ts` and confirm failure.
- [ ] Implement pure `scoreRoute`, the A* incremental costs, deterministic completed-path costs, and the exact priority/difficulty/span/ID target ordering. Reuse measured text bounds for every visible text/connector label; do not invent a second text measurer.
- [ ] Add opt-in search costs to `avoidObstacles`/`search`; keep existing exported defaults and cost arithmetic unchanged for `legacy`.
- [ ] Add planner tests proving no-policy PSGL/Lego/R05 routes and route-change IDs remain unchanged; `avoid` finds fewer crossings, `allow` permits them, track spacing uses 6 pt, and 80,000-state exhaustion has a stable skip result.
- [ ] Run `npm --prefix src/web test -- tests/routeQuality.test.ts tests/routePlanner.test.ts tests/routeModes.test.ts` and `npm --prefix src/web run typecheck`; expect all pass.
- [ ] Commit `feat: score aesthetic connector routes`.

### Task 3: Add optional routing policy and required-side contracts

**Files:**
- Modify: `contracts/document.schema.json`
- Modify: `contracts/operations.schema.json`
- Modify: `contracts/protocol.schema.json`
- Generate: `src/web/src/contracts/generated.ts`, `src/Diagram.Core.Contracts/Generated/Contracts.g.cs`
- Modify: `src/web/src/commands/planPatch.ts`
- Modify: `src/web/src/commands/CommandEngine.ts`
- Test: `src/web/tests/contracts.test.ts`
- Test: `src/web/tests/routePlanner.test.ts`

**Interfaces:**
- Consumes: `routingDefaults.crossing?: 'avoid'|'allow'`, connector `routingPolicy.crossing?: 'avoid'|'allow'`, `routingPolicy.priority?: -100..100`, endpoint `requiredSide?: north|east|south|west`.
- Produces: `setRoutingDefaults` and `beautify` operation DTOs. Static ports on a contradictory required side fail `invalid_request` with `details.reason='routing_side_conflict'`, connector ID and endpoint. An unreachable required side returns `ROUTE_REQUIRED_SIDE_UNRESOLVED`.

- [ ] Add contract tests for valid defaults, overrides, priorities at -100/100, rejected out-of-range values, required sides, invalid static-port conflicts, and rejection of connector-only policy fields on shapes/text.
- [ ] Run focused schema/operation tests and confirm the new fields/operations are rejected before schema edits.
- [ ] Split shared element `RoutingPolicy` from connector-only `ConnectorRoutingPolicy`; extend document/connector/endpoint schemas and add `ROUTE_REQUIRED_SIDE_UNRESOLVED` to `contracts/protocol.schema.json`. Add `setRoutingDefaults` and required-hash `beautify` operation schemas; regenerate TS/C# contracts using `npm --prefix src/web run generate`.
- [ ] Implement `setRoutingDefaults` as one document diff and validate `requiredSide` against fixed ports during operation planning; make `requestsHardRoutePreflight` recognize this operation and reject a newly unreachable default.
- [ ] Add UI-accessible connector fields later in Task 5; do not change line style/colour semantics.
- [ ] Run `npm --prefix src/web test -- tests/contracts.test.ts tests/routePlanner.test.ts`, `npm --prefix src/web run generate:check`, and `npm --prefix src/web run typecheck`; expect all pass.
- [ ] Commit `feat: add connector crossing policies and priority`.

### Task 4: Implement exact, read-only Beautify planning and hash

**Files:**
- Create: `src/web/src/editor/beautifyPreview.ts`
- Modify: `src/web/src/layout/routePlanner.ts`
- Test: `src/web/tests/beautifyPreview.test.ts`

**Interfaces:**
- Produces: `buildBeautifyPreview(document, pageId, targetIds, options): Promise<BeautifyPreview>` with `baseRevision`, sorted `targets`, `routes`, `skipped`, `changedCount`, `routeHash`; `hashBeautifyRoutes(targets, routes): Promise<string>` SHA-256 hashes canonical JSON with sorted IDs and 0.001 pt coordinates.

- [ ] Test preview route count/hash, deterministic ordering, selected-only versus page materialization, Manual/Frozen/locked skips, and no document/advisory mutation.
- [ ] Test that one infeasible or budget-exhausted connector is skipped while a feasible target still receives a proposed route.
- [ ] Run `npm --prefix src/web test -- tests/beautifyPreview.test.ts` and confirm failure.
- [ ] Implement the preview using one document snapshot and the Task 2 planner profile; skip unchanged/unfeasible targets and retain their exact current route/advisory.
- [ ] Test canonical hash equality for route coordinate noise below 0.001 pt and hash inequality for changed coordinates, target IDs or route order after canonical sorting.
- [ ] Run the focused suite and typecheck; expect all pass.
- [ ] Commit `feat: plan revision-bound beautification previews`.

### Task 5: Commit Beautify atomically and add editor controls

**Files:**
- Modify: `src/web/src/commands/planPatch.ts`
- Modify: `src/web/src/commands/CommandEngine.ts`
- Modify: `src/web/src/editor/EditorModel.ts`
- Create: `src/web/src/components/BeautifyDialog.tsx`
- Modify: `src/web/src/components/PageCanvas.tsx`
- Modify: `src/web/src/editor/appCommands.ts`
- Modify: `src/web/src/components/Inspector.tsx`
- Modify: `src/web/src/components/Toolbar.tsx` or document settings dialog as needed
- Test: `src/web/tests/beautify.test.ts`
- Test: `src/web/tests/browser/beautify.spec.ts`

**Interfaces:**
- Consumes: the exact Task 4 preview, `beautify` operation with its base revision and route hash, and `setRoutingDefaults`.
- Produces: one `TransactionResult` whose `changed`/`routeChanges` list contains only committed target IDs; the existing transaction stack provides exact undo/redo.

- [ ] Add engine tests for stale revision rejection, preview-hash mismatch rejection, target-only commit, one undo/redo, explicit Manual-to-Automatic conversion, and Frozen/locked skips.
- [ ] Add a transaction test with one newly infeasible target and one feasible target; assert the infeasible route is untouched, the feasible one commits, and broad R22 preflight does not reject the batch.
- [ ] Run `npm --prefix src/web test -- tests/beautify.test.ts` and confirm failure.
- [ ] Implement `beautify` planning/apply so expected hash is recomputed before any diff; reject on mismatch; only add upserts for changed eligible target routes; avoid creating unresolved updates for skipped targets.
- [ ] Add `Preview Beautify` in Arrange menu and selected/page dialog; draw exact candidate paths, changed count and skip reasons; disable acceptance on stale snapshot and close without mutation on cancel.
- [ ] Add Edit > Routing defaults dialog and Inspector controls for crossing override, priority, and required endpoint side; all changes use the existing transaction path.
- [ ] Add Playwright coverage for no-side-effect preview, page/selection scope, exact preview overlay, stale revision, accepted undo, and policy controls.
- [ ] Run `npm --prefix src/web test -- tests/beautify.test.ts tests/beautifyPreview.test.ts` and `npm --prefix src/web run test:browser -- tests/browser/beautify.spec.ts`; expect all pass.
- [ ] Commit `feat: add previewed beautify command`.

### Task 6: Verify regressions, documentation and platform contracts

**Files:**
- Modify: `docs/m5/R24-aesthetic-routing.md`
- Test: route, inspect, browser, transaction, contract and fixture suites above
- No tracker-file edits.

**Interfaces:**
- Produces: reproducible R24 acceptance evidence and a clear list of `PASS`, `FAIL`, or `NOT RUN` for real Visio and dense fixtures.

- [ ] Compare no-policy PSGL, Lego, R05 baselines and R26 expected diagnostics against the post-#90 baseline; record intentional threshold-only finding changes.
- [ ] Run `npm --prefix src/web test`, `npm --prefix src/web run typecheck`, `npm --prefix src/web run generate:check`, and `npm --prefix src/web run build`.
- [ ] Run `dotnet test Diagram.Portable.slnx --configuration Release`; run `dotnet test Diagram.Windows.slnx --configuration Release` and real Visio acceptance separately when available.
- [ ] Run the 60/60 and denser deterministic routing fixtures, record expanded-state counts, route changes, and preview wall-clock measurements; verify no route exceeds 80,000 expanded states.
- [ ] Record implementation commits, test totals, updated finding fixture details, manual Visio status and remaining dynamic-anchor/fan-out limits in `docs/m5/R24-aesthetic-routing.md` and issue #63.
- [ ] Run full web and portable .NET suites again if any preceding edit changes code; commit `test: record R24 aesthetic routing evidence`.

## Self-review

- Every R24 issue scope item is covered by score terms, policies, shared contact analysis, preview or an explicit limit (dynamic glue-anchor symmetry and named-port creation).
- R26 stays the source of contact semantics; its public finding codes remain stable, with only the documented 4-to-6 pt near-miss threshold change.
- The default route planner path and baselines remain unchanged when policy is absent; only explicit opt-in work uses aesthetic scoring.
- Preview and commit share planner inputs, a base revision and a canonical route hash; R22's broad preflight cannot turn one skipped target into a failed multi-target Beautify.
- PR #90 merge is a prerequisite; no implementation files are touched on the R26 branch.
