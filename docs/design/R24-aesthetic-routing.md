# R24 — Aesthetic routing and explicit beautification

Status: **design proposal for owner review; no implementation written.** Scope: issue #63, building on R19–R23 and R16/R29. This proposal defines the first deliverable across route quality, contact classification, crossing policy, and explicit preview/commit. R25 remains the MCP proposal API; this issue supplies the deterministic planner and user-facing operation it can later reuse.

## Goals and invariants

R24 improves newly searched and explicitly beautified orthogonal routes while preserving R19's route stability. Ordinary edits continue to keep a clear stored route. A shape move, style change, load, projection, export, or inspection never invokes beautification. Only an explicit user operation may optimize an already-clear route.

Hard feasibility remains ahead of aesthetics: forbidden obstacles, glued endpoints and required sides, authored waypoints, segment constraints, real joins, locked content, and the route mode cannot be violated to improve appearance. The same pure page-space planner is used by canvas, hidden projection, export, and transaction planning. Stable connector IDs and fixed search ordering break ties. Search remains bounded; exhaustion produces a structured unresolved result rather than a route reported as successful.

## Contact classification

Add a pure classifier over pairs of orthogonal route segments. It reports stable, page-space contacts with both connector IDs, segment indices, point or overlap interval, and a classification:

- `perpendicular-crossing`: interiors intersect at right angles and neither segment ends at the contact;
- `collinear-overlap`: same-axis segments share positive length;
- `t-join`: one segment endpoint meets the interior of another;
- `endpoint-contact`: route endpoints meet;
- `parallel-near-miss`: distinct parallel runs are closer than the configured minimum track spacing, without overlap.

A declared R16 join is intentional topology, not an accidental crossing. Shared endpoints, same-network segments, and the small endpoint tolerance are classified separately and do not incur accidental-crossing cost. Classification never edits topology or creates a join. The classifier feeds route scoring, preview, and later R25/R26 diagnostics.

## Deterministic quality and routing behavior

Use a documented lexicographic route score with hard feasibility first, then the active crossing-policy violations, accidental collinear overlap and near-miss congestion, deviation from the stored route, track-spacing/fan-out consistency, bends and zig-zags, clearance, and length. Route deviation is the dominant geometric term during ordinary repair, with a documented hysteresis threshold; this preserves R19's stability. For explicit Beautify, the caller opts into a bounded quality improvement: crossing/overlap and track defects can outweigh route deviation, but the planner still retains the current route when the improvement does not exceed the hysteresis threshold. Ties resolve by connector ID and stable grid/search order.

The search accounts for perpendicular crossings as well as shared runs. Earlier routes are considered in stable ID order. A connector's explicit routing priority may affect which connector claims a congested track first; it never comes from stroke width or colour. Equal priorities use connector ID. Track spacing uses the existing page-point spacing as its default and penalizes both long overlaps and close parallel runs. Fan-out distributes links leaving or entering one shape across available sides/coordinates while respecting static ports, R22 preferred sides, required endpoint sides, and hard constraints. If no valid fan-out exists, keep a valid route and report congestion rather than changing glue.

Crossing policy is optional at document and connector level, with connector override. Values are `avoid` (default: search to remove avoidable accidental crossings), `allow` (crossings are permitted but still classified), and `approvalRequired` (do not commit a new accidental crossing without explicit approval). A designated region is represented by an existing shape/region policy; absent such a region, `approvalRequired` applies to the connector's whole route. Intentional R16 joins never require crossing approval. Hops remain a rendering choice after routing (R16); R24 does not change hop appearance or Visio export behavior. R29's level/z-order hop ownership remains authoritative.

Routing priority is an optional small signed integer on a connector routing policy, default 0. It only orders route planning under congestion and does not change geometry by itself. Changing priority is not an implicit beautify request.

## Explicit Beautify preview and commit

Expose `preview beautify` and `beautify` as an explicit operation with either selected connector IDs or page scope. Preview is read-only and returns the base document revision, target IDs, proposed routes, per-route provenance/findings, and the count of routes that would change. The editor presents current and proposed paths distinctly and lets the user accept or cancel. It does not mutate the document or write stored advisories.

Accept submits one atomic `beautify` transaction against the preview's base revision. If the revision changed, reject as stale and require a fresh preview. The planner recomputes deterministically from the same document revision and commits exactly those target routes through the existing diff, so undo restores every route and advisory in one step. Untargeted connectors remain fixed and are treated as obstacles/tracks; they are not silently beautified as side effects. A page preview reports how a target will interact with fixed outside routes.

By default only automatic orthogonal connectors on unlocked content are eligible. Manual and Frozen routes and locked content appear as skipped with a reason. The explicit preview can include Manual connectors only after the user chooses “convert included routes to Automatic”; that conversion clears waypoints and is part of the same undoable transaction. Frozen routes remain ineligible until the user explicitly changes their mode to Automatic first, because R20 defines the frozen advisory as authoritative. Locked-layer protection remains hard even when a connector is selected; the preview reports it as skipped.

`approvalRequired` crossings are shown in preview but cannot be committed until the user explicitly approves the listed crossing IDs. Approval is bound to the preview revision and exact crossings; a changed revision or changed contact set invalidates it. No approval silently relaxes forbidden obstacles, endpoint constraints, locks, or topology.

## Contract and compatibility proposal

Add optional connector `routingPolicy.crossing` (`avoid | allow | approvalRequired`) and `routingPolicy.priority` (integer, bounded to -100..100). Add optional document `routingDefaults.crossing` with the same enum; missing values mean `avoid`. Add optional endpoint `requiredSide` alongside R22's soft `preferredSide`; static named ports continue to take precedence and a contradictory required-side request is rejected.

Add a `beautify` transaction operation accepting either unique `targets` or a page-scope flag, plus explicit `includeManual` and exact crossing approvals. Target identity is required for the apply step; the UI supplies the preview's base revision using the existing transaction revision check. Read-only preview stays in the editor until R25 defines a public MCP proposal/commit contract. The planner and classifier return exact route coordinates and stable findings, so R25 can expose them without another geometry implementation.

All fields are optional; default policy is `avoid`, default priority 0, and no required side. Existing documents therefore retain their serialized form and R19's stored-route behavior. No schema-version bump is proposed because the repository has no migration policy and already accepts optional R16/R20/R22/R23/R29 fields in schema v1. Older strict readers may reject files that use the new fields, the same known limitation as other additive fields. Existing route geometry is unchanged on load; new searches and explicit beautification may choose different geometry.

## Acceptance and regression

- Unit-test the classifier for each contact class, endpoint tolerance, shared R16 join, and non-connected crossing; classification is order-independent and stable.
- Unit-test each score term, policy mode, priority tie, ID tie, hysteresis, fan-out, spacing, and bounded-search failure.
- Verify ordinary edits keep a clear stored path and never beautify unrelated connectors; compare route coordinates and changed IDs.
- Verify preview has no document side effects, reports exact count/routes/skips/approval crossings, and becomes stale after a revision change.
- Verify selected/page apply changes only eligible target IDs, commits once, undo/redo restores routes and advisories exactly, and manual conversion is explicit and undoable.
- Verify avoidable crossings are removed before hops; allowed unavoidable crossings stay disconnected and classified; approval-required crossings block commit until the exact crossing set is approved.
- Run PSGL and Lego route/visual regressions, the 60-shape/60-connector stress case and denser bounded cases, all route/planner/transaction/editor suites, generated contract checks, and fresh Windows/Visio acceptance separately. Report Visio acceptance separately if no real Visio install is available.

## Deliberate limits

This issue does not add new connector-network topology, regional routing gateways, segment handles/locks beyond R21, route proposal persistence/API from R25, diagnostics overlays from R26, or new Visio crossing cells from R27. Dense fan-out that cannot preserve hard constraints is reported rather than solved by violating them. Region-scoped crossing approval initially uses existing region/shape boundaries; adding a standalone region model requires a separate contract change.
