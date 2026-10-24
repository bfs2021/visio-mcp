# R19 Route Stability Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete R19’s transaction, join-aware routing, and MCP diagnostic acceptance criteria on top of the already integrated R19 planner and approved R20 routing contract.

**Architecture:** Keep `planRoutes` as the one pure route planner. The transaction engine will settle join geometry before writing advisory routes, then return the planner’s route changes and findings; MCP reads will derive route diagnostics from the same revision-bound projection used by export.

**Tech Stack:** TypeScript, Vitest, JSON Schema, generated TypeScript/C# contracts, MCP engine handlers, .NET tests.

**Spec:** GitHub issue #58 acceptance criteria; `docs/m5/R19-route-stability.md`; approved contract `docs/design/routing-contract-proposal.md` and owner decisions recorded on issue #59.

## Global Constraints

- Preserve R20’s approved rules: advisory routes are persisted; Frozen never searches; Guided waits for R21; explicit reroute is required to forget an automatic route; `routeChanges` supplements `changed`; no schema version bump.
- The transaction order is `planPatch` → `settleJoins` → `settleRoutes` → hard-route preflight → `settleAttachments`.
- Route diagnostics must be derived from the same snapshot revision and planner used for projection/export; reads must not mutate the document.
- Keep R22 obstacle policies and R29 levels behavior intact; do not implement R23 or R27 scope here.
- Do not claim original PSGL/Lego fidelity or manual Visio validation without the original source files or an available Visio run.

## Review Focus

- A connector joined to an automatic connector must store advisory endpoints after the joined endpoint is settled; pin this with the moved-target branch test.
- A route rejected by hard-route preflight must not be committed or reported as if it succeeded; preserve the rejection and diagnostic behavior.
- Frozen and manual connectors must keep their current mode semantics while join settling moves neighboring geometry; cover both in the routing regression suite.
- Repeated planning and harmless edits must keep unrelated advisory points and changed IDs identical; assert idempotence and the 1 pt nudge baseline.
- MCP read results must bind diagnostics to their returned revision and remain read-only; test revision, diagnostics, and unchanged canonical hashes.

---

### Task 1: Settle join geometry before advisory routes

**Files:**
- Modify: `src/web/src/commands/CommandEngine.ts`
- Test: `src/web/tests/joins.test.ts`
- Test: `src/web/tests/routeModes.test.ts`

**Interfaces:**
- Consumes: `settleJoins(before, plan, snapshotProjector, snapshotOf): Plan` and `settleRoutes(before, plan): Plan`.
- Produces: transaction plans whose route advisory endpoints match their final joined endpoints; `routeChanges` still identifies every changed connector and remains undoable.

- [ ] **Step 1: Write failing regression test** `a joined branch stores the route from the settled join endpoint` in `src/web/tests/joins.test.ts`. Create an orthogonal bus and joined branch with the real test projector; move a bus endpoint shape; assert the branch’s `to.point` equals the settled bus route position, the stored advisory ends at that point, and both affected IDs appear in the transaction result as appropriate.
- [ ] **Step 2: Run the focused test and confirm failure** with `npm test -- --run src/web/tests/joins.test.ts -t "settled join endpoint"` from `src/web`.
- [ ] **Step 3: Reorder the phases** in `CommandEngine.execute` so `settleJoins` consumes `planned.value`, `settleRoutes` consumes the joined plan, hard-route preflight evaluates the settled route plan, and `settleAttachments` consumes the routed plan.
- [ ] **Step 4: Run join and route-mode tests** with `npm test -- --run src/web/tests/joins.test.ts src/web/tests/routeModes.test.ts`; confirm join placement, stored routes, undo/redo, Frozen, hard preflight, and idempotence pass.
- [ ] **Step 5: Commit** as `fix: route connectors after settling joins`.

### Task 2: Return structured route findings with transactions

**Files:**
- Modify: `contracts/protocol.schema.json`
- Generate: `src/web/src/contracts/generated.ts`, `src/Diagram.Core.Contracts/Generated/Contracts.g.cs` via `tools/generate-contracts.mjs`
- Modify: `src/web/src/commands/CommandEngine.ts`
- Test: `src/web/tests/routeModes.test.ts`
- Test: `src/web/tests/contracts.test.ts`

**Interfaces:**
- Consumes: `Plan.routeChanges?: RouteChange[]` and `Plan.routeFindings?: RouteFinding[]`.
- Produces: optional `TransactionResult.routeFindings?: RouteFinding[]`, carrying structured findings from the committed route plan.

- [ ] **Step 1: Write failing tests** asserting an unresolved route is returned in `TransactionResult.routeFindings` with its `elementId`, `code`, severity, and blocker IDs, and that a successful transaction with no findings omits the optional property.
- [ ] **Step 2: Run the focused tests and confirm failure** with `npm test -- --run src/web/tests/routeModes.test.ts src/web/tests/contracts.test.ts` from `src/web`.
- [ ] **Step 3: Add optional `routeFindings` to the `TransactionResult` schema** using the existing `RouteFinding` definition, then run `node tools/generate-contracts.mjs` from the repository root; do not hand-edit generated contracts.
- [ ] **Step 4: Include findings in `CommandEngine.resultOf`** when present, and include the empty-plan type only if TypeScript requires it.
- [ ] **Step 5: Run focused tests and generated-contract checks** with `npm test -- --run src/web/tests/routeModes.test.ts src/web/tests/contracts.test.ts` and `npm run check:contracts` from `src/web`.
- [ ] **Step 6: Commit** as `feat: include route findings in transaction results`.

### Task 3: Expose projection diagnostics in `get_objects`

**Files:**
- Modify: `src/web/src/bridge/engineHandlers.ts`
- Test: `src/web/tests/readtools.test.ts`
- Modify: `src/Diagram.Mcp/DiagramTools.cs`

**Interfaces:**
- Consumes: `CommandEngine.exportSnapshot(scope): Promise<Result<ExportSnapshot>>`, which returns the canonical snapshot and projection tied to the same document, session, and revision. `doc.getObjects` will read canonical objects from this returned snapshot, avoiding a second, potentially newer snapshot.
- Produces: additive `routeDiagnostics` in `doc.getObjects` responses, containing requested connector IDs with `routeProvenance`, optional `routeReason`, and `routeFindings`; canonical `objects` stay unchanged.

- [ ] **Step 1: Write a failing handler test** that calls `doc.getObjects` for an unresolved connector and asserts the response contains its canonical object plus the matching projection diagnostics at the same revision; also assert the document hash is unchanged.
- [ ] **Step 2: Run the focused test and confirm failure** with `npm test -- --run src/web/tests/readtools.test.ts -t "route diagnostics"` from `src/web`.
- [ ] **Step 3: Extend the `doc.getObjects` handler** to obtain the revision-bound export snapshot, read canonical objects from that same snapshot, filter projection diagnostics to requested connector IDs, and add `routeDiagnostics` beside the existing `objects` response.
- [ ] **Step 4: Update the `get_objects` MCP description** to document the additive diagnostics and their read-only, revision-bound nature.
- [ ] **Step 5: Run the read-tool and MCP contract tests** with `npm test -- --run src/web/tests/readtools.test.ts` and the repository MCP suite.
- [ ] **Step 6: Commit** as `feat: expose route diagnostics in get_objects`.

### Task 4: Verify cross-surface stability and record evidence

**Files:**
- Test: `src/web/tests/routeModes.test.ts`
- Test: existing snapshot/export/inspect-layout tests identified by `rg "inspectLayout|exportSnapshot|routeProvenance" src/web/tests`
- Test: `src/web/tests/browser/segments.spec.ts`
- Modify: `docs/m5/R19-route-stability.md`

**Interfaces:**
- Consumes: route data from `planRoutes`, committed `routeChanges`/`routeFindings`, and revision-bound projection diagnostics.
- Produces: verified R19 matrix for projection, MCP inspection, export, transaction result, undo/redo, unrelated nudge, and repeat planning.

- [ ] **Step 1: Add missing assertions** that an already-planned document returns no changes, a 1 pt unrelated nudge preserves all unaffected stored routes and IDs, unresolved findings agree across projection/inspection/MCP/transaction response, and export uses the same route points for the same revision.
- [ ] **Step 2: Run the focused route and surface tests**, confirming exact route-point and changed-ID comparisons.
- [ ] **Step 3: Run the full web suite, generated contract checks, MCP/.NET suites, `segments.spec.ts` 60/60 routing-latency case, and available Visio checks**; record each exact command and result separately.
- [ ] **Step 4: Update `docs/m5/R19-route-stability.md`** with fresh results, baseline fixture status, and explicit `NOT RUN` limits for original PSGL/Lego files or manual Visio where unavailable. Check off only criteria supported by evidence.
- [ ] **Step 5: Commit** as `test: record R19 route stability evidence`.

## Coverage gaps to report

The repository issue requires original PSGL and Lego coordinate/topology baselines and a manual owner check. If original VSDX inputs or Visio are unavailable, document those as `NOT RUN` and leave the associated R19 criteria open; reconstructed fixtures do not substitute for original-source evidence.
