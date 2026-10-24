# R24 — Aesthetic routing and explicit beautification

Status: **owner-approved direction; revised per review on issue #63. No implementation code written.** This implements the aesthetic-routing, crossing-policy, and Beautify scope of #63 after PR #90 (R26) is merged. R19–R23 and R16/R29 remain authoritative for route stability, modes, obstacle policy, levels, joins, and hops.

## Goals and invariants

Ordinary edits continue to keep a clear stored route. A shape move, style change, load, projection, export, or inspection never beautifies a route. The new aesthetic cost profile is used only for an explicit reroute/Beautify request or when a document/connector explicitly opts into a crossing policy. Missing crossing policy preserves the current search costs and connector-ID routing order, so existing PSGL, Lego, R05 and R19 baselines do not move merely because R24 ships.

Hard feasibility is separate from aesthetic cost. Forbidden obstacles, endpoint glue, required endpoint sides, authored waypoints, R21 segment constraints, R16 joins, locked content and Frozen mode cannot be violated to improve appearance. A candidate that cannot satisfy hard constraints is skipped during Beautify; it does not reject valid changes to other targets. The same pure page-space planner and contact classifier feed the canvas, hidden projection, export, inspection, and transaction planner. Search has deterministic expansion budgets and stable ordering; budget exhaustion is reported and leaves that target unchanged.

## Shared connector-contact classification

PR #90 already implements the contact rules in `src/web/src/layout/inspectRoutes.ts`. R24 does not create another classifier. After #90 is merged, extract its pure contact geometry into `src/web/src/layout/routeContacts.ts`, preserving the existing `inspect_layout` finding codes and evidence. `inspectRoutes` and route scoring call this shared module. The shared result includes both connector IDs, segment indices, contact point or overlap interval, and the existing finding classification.

The shared contact rules distinguish perpendicular crossings (including hopped crossings), collinear overlaps, T-joins, unjoined endpoint contacts, and parallel near-misses. A declared R16 join is intentional and excluded from accidental crossing/overlap cost. A hop changes the drawing but not whether two routes cross. Contacts do not edit connector topology or create joins. The router and inspector use the same 0.5 pt overlap tolerance, 10 pt alongside minimum and 6 pt near-miss threshold.

Use one shared track spacing constant of **6 pt** for the planner, parallel-near-miss classification threshold, and inspector suggestion. Preserve the R26 overlap tolerance of **0.5 pt**, minimum alongside length of **10 pt**, and short-segment threshold of **2 pt**. Update R26's expected finding fixtures only for the intentional 4-to-6 pt threshold change; compare against the prior R26 results and document the changed findings.

## Deterministic score and routing order

The current A* cost remains unchanged for implicit planning: **1 quality point per path point and 24 per bend**, plus the current shared-run cost. This is the compatibility path. R24 adds an opt-in cost profile for explicit reroute/Beautify and for connectors with an explicit effective crossing policy.

For each candidate route, compute this lexicographic score:

1. **Policy crossings:** count of accidental perpendicular crossings when the effective policy is `avoid`; `allow` sets this component to zero. A lower count always wins. T-joins, declared joins and contacts at a route's own endpoints do not count.
2. **Quality points:** sum of the terms below. Route deviation is the dominant geometric term, followed by track quality; weights and units are fixed here and unit-tested term by term.

| Term | Cost |
|---|---:|
| Symmetric route deviation from the stored path, measured as changed run length | 12 quality points / pt |
| Candidate route length | 1 quality point / pt |
| Bends | 24 quality points / bend |
| Collinear overlap with another connector | 20 quality points / pt of overlap |
| Near-miss track deficit | 2 quality points / pt², where deficit is `(6 pt - gap) × alongside length` |
| Crossing a measured text/connector label rectangle | 500 quality points / intersected label |
| Run closer than 12 pt to a non-end shape, while remaining outside the hard 8 pt clearance | 1 quality point / pt² of clearance deficit × alongside length |
| Each run shorter than 4 pt | 20 quality points / pt of `(4 - run length)` |
| Each route reversal (same axis, opposite direction, after a turn) | 100 quality points / reversal |

The stored route is retained unless the candidate improves the policy-crossing count or improves quality by at least **48 quality points** when crossing count is unchanged. This hysteresis equals the existing 48 pt two-turn route-stability threshold in quality-score units. Normal implicit planning never compares this score and keeps R19's behavior. Route deviation is the symmetric difference of the current and candidate route line sets: `length(current) + length(candidate) - 2 × sharedCollinearLength`, after merging collinear intervals and rounding coordinates to 0.001 pt. In A* each added edge also pays 12 quality points per point outside the stored route; completed-candidate scoring uses the exact symmetric difference.

Search-time additive terms are length, bend, policy crossing, overlap, near-miss and measured-label intersection costs. Symmetric route deviation, short-run, reversal, and shape-clearance terms are computed on completed candidate paths and compared with the current route. The planner tests the current route plus one bounded A* candidate per target; it does not enumerate unbounded alternative paths. If no candidate can be found within the existing **80,000 expanded states per search**, that target is skipped with a budget-exhausted reason.

Beautify routes eligible connectors in descending explicit `routingPolicy.priority` (default 0), then descending count of hard obstacles intersecting their direct path, then descending endpoint Manhattan span, then connector ID ascending. This puts congested/difficult routes on tracks first and uses stable IDs only as the final tie-break. Without an explicit policy or Beautify operation, R19's existing connector-ID order remains unchanged. R29 `level` controls drawing order and pass-through; `routingPolicy.priority` controls routing order only.

Tracks are separated using 6 pt spacing and congestion cost across the full route, including the first and last 24 pt after existing endpoint anchors. The planner respects R22 preferred sides and R24 required sides; static named ports remain fixed. Symmetric redistribution of multiple dynamic glue anchors along a shape edge, group-wide alignment of port entries, and automatic creation of named ports are deferred; when existing anchors cannot provide separated fan-out, the planner reports residual congestion instead of changing glue or topology. Hops are still assigned by R16/R29 after route planning and do not alter the route score.

## Crossing policy and endpoint sides

Add optional `routingDefaults.crossing` to the document and `routingPolicy.crossing` to a connector, with `avoid | allow`; connector override wins. If both are absent, ordinary routing uses the legacy cost profile. An explicit Beautify defaults to `avoid` when no policy is configured. `allow` permits and classifies crossings without penalizing them. **Approval-required crossings are deferred to R25**, which can carry stable proposal/approval identities; there is no approval-required enum or designated-area model in R24.

Add optional endpoint `requiredSide` (`north | east | south | west`) alongside R22's `preferredSide`. Side names are in the endpoint shape's local coordinates and rotate with the shape. A static named port takes precedence only when it is on the required side; a contradictory port request fails preflight with `invalid_request`, `details.reason = routing_side_conflict`, and the connector ID/endpoint. A valid required side that becomes unreachable because of hard obstacles produces `ROUTE_REQUIRED_SIDE_UNRESOLVED`; explicit creation or policy changes are rejected by the existing hard-route preflight. `preferredSide` remains a soft preference.

## Explicit Beautify preview and commit

The editor exposes read-only **Preview Beautify** for selected connector IDs or the full page. Preview returns the document revision, target IDs, exact proposed page-space routes, provenance/findings, skipped targets with reasons, and changed-route count. It draws proposed paths as an overlay and does not mutate the document or write route advisories.

Accept applies one atomic `beautify` transaction. It includes the preview revision and a SHA-256 hash over canonical JSON of sorted target IDs and route coordinates rounded to **0.001 pt**. The transaction recomputes using the same pure planner and rejects as stale if either the revision or route hash differs. Thus the exact previewed route set is the route set committed. One undo restores every changed route and advisory.

Untargeted connectors remain fixed and are treated as obstacles/tracks. The target list is exact; no neighboring connector is silently beautified. By default only automatic orthogonal connectors on unlocked content are eligible. Manual connectors are skipped unless the user explicitly chooses to convert included routes to Automatic; clearing their waypoints is part of the same transaction and undo. Frozen routes remain ineligible until the user separately changes them to Automatic, because R20 makes their stored advisory authoritative. Locked elements/layers remain hard protected and are reported as skipped, even when selected.

If a target has no feasible candidate, retain its existing route and advisory, report it as skipped, and continue with other targets. This operation does not trigger the broad R22 hard-route preflight failure for unrelated or skipped targets. Any attempted change that newly violates a hard obstacle or endpoint constraint is still rejected. A zero-change preview cannot be committed as a mutation.

## Contract and compatibility

Contract additions are optional: document `routingDefaults.crossing`; connector `routingPolicy.crossing` and integer `routingPolicy.priority` bounded to **-100..100**; endpoint `requiredSide`; a `setRoutingDefaults` transaction operation for the document default; and a transaction `beautify` operation with either unique targets or page scope, plus `includeManual` and expected route hash. Add an Edit-menu **Routing defaults…** dialog and Inspector controls for connector crossing policy, priority and endpoint required side. Existing documents with no new values retain their serialized form and the existing implicit routing profile. No schema-version bump is proposed, consistent with R16/R20/R22/R23/R29. Older strict readers may reject files that use new fields, the known additive-field limitation.

The read-only editor preview remains local to the app for R24. R25 defines the public MCP proposal/commit contract using this same classifier, cost profile, base revision, route hash and findings; R24 does not invent a second MCP preview API.

## Acceptance and regression

- Unit-test the shared R26 classifier for each contact class, endpoint tolerance, declared R16 join, hopped crossing, and consistent 6 pt spacing threshold. `inspect_layout` and scoring report the same contacts.
- Unit-test every score term, route-deviation measurement, 48-point hysteresis boundary, `avoid`/`allow`, explicit priority order, obstacle difficulty order, ID tie-break, stable route result, and bounded-budget outcome.
- Verify implicit planning with missing policies produces byte-identical PSGL, Lego, R05 and R19 route geometry and change IDs. Explicit policies and Beautify may change only targeted routes.
- Verify preview has no document side effects and returns exact route hashes, counts and skip reasons. A changed revision or hash rejects commit.
- Verify selected/page apply updates only eligible targets, continues past an infeasible target, commits once, and undo/redo restores advisories and paths exactly. Manual conversion is explicit; Frozen and locked content remain untouched.
- Verify avoidable crossings are removed in `avoid`, allowed crossings remain disconnected and classified in `allow`, and hops are recomputed from final routes using unchanged R16/R29 rules.
- Run PSGL and Lego semantic/route/image regressions, the 60/60 stress fixture and denser bounded fixtures, route/planner/transaction/browser suites, schema/code-generation checks, and fresh Windows/Visio acceptance separately. Measure explicit Beautify preview time; do not change the existing 80,000-state deterministic limit to chase a timing target.

## Deliberate limits

R24 does not add approval-required crossings or standalone designated-region objects (R25 follow-up); automatic redistribution of dynamic glue anchors, group-wide port-entry alignment, or named-port creation; new topology or junction semantics; the R21 segment editor; R26 overlay redesign; or R27 Visio crossing cells. It does not change the R26 finding meanings except for using its shared 6 pt near-miss threshold. If the 60/60 or denser page exceeds the deterministic state budget, affected routes are skipped with a finding and the rest of the preview remains usable.
