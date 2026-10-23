# R22 Routing Policy Design

Date: 2026-10-23
Issue: [R22 #61](https://github.com/rp2839/visio-mcp/issues/61)
Status: Design for owner review

## Goal

Give people and agents explicit control over which geometry an automatic connector must avoid, may prefer to avoid, or may pass through. Keep the current routing behavior for documents that do not opt in, and make the editor, DrawScript, MCP, diagnostics, exports, and hidden projections apply the same rules.

R22 builds on R19 route planning, R20 persisted route modes, R21 segment constraints, existing shape ports, R13 labels/attachments, and R16 joins/hops.

## Scope

R22 adds per-element obstacle intent, text protection, clearance, and endpoint side preferences. The policy applies when the route planner plans an automatic or guided orthogonal connector. Manual and frozen connectors retain R20 behavior; the router does not search for a replacement route for them.

R22 does not add layer or level ordering (R23/R29), container gateways or inside/outside semantics (R17), automatic global beautification (R24), new junction topology (R30), or native Visio routing-mode mappings (R27). A pass-through element is transparent to routing across its fill and border; intentional openings through protected containers require the later gateway design.

## Document contract

Add an optional routingPolicy object to shapes, text elements, and connectors. Its obstacle and clearance members apply to shape/text geometry; text controls the element's text or connector-label bounds. All members are optional:

- obstacle: automatic, forbidden, avoid, or passThrough.
- clearancePt: non-negative extra distance from the visible outline, bounded to 2,000 pt. Omission uses the existing 8 pt base clearance.
- text: ignore or avoid, controlling whether the element's own text or connector label bounds are considered an obstacle. Omission remains ignore.
- On connector endpoints, optional preferredSide: north, east, south, or west.

The exact fixed-port path already exists: a static endpoint with a named port resolves to that shape port and keeps it. preferredSide is a soft preference for dynamic glue only; an explicitly named static port wins. The planner may choose another side if the preferred side cannot satisfy hard constraints, and emits a stable ROUTE_SIDE_PREFERENCE_RELAXED finding. The preferredSide enum names the shape-local side; the side is rotated with the shape before planning. No new port identity or port-definition model is introduced.

The optional routingPolicy is distinct from connector routing (R20 mode, constraints and advisory route) and style. automatic preserves the legacy obstacle-selection behavior for that element; it does not mean “always an obstacle.”

## Policy semantics and precedence

For visible elements, evaluate a per-element routingPolicy.obstacle first:

1. forbidden is a hard obstacle. The route must keep the declared clearance from it. If no valid path exists, the planner retains the best prior route, marks the connector unresolved, and reports the blocking element and reason. It never presents a violating path as successful.
2. avoid is a soft obstacle. The planner prefers a route outside its clearance. If the bounded search cannot find a route that avoids all soft obstacles, the planner may route with those soft obstacles relaxed and emits one stable ROUTING_AVOIDANCE_VIOLATION finding per violated element. A route that also violates a forbidden policy remains unresolved.
3. passThrough is ignored by obstacle search and crossing diagnostics. Routes may pass through its interior or cross its border.
4. automatic, or an absent obstacle, uses today's migration heuristic for that element. Existing files are not rewritten and retain current route geometry until a user edits policy or a connector is otherwise changed.

Hidden elements and elements on hidden layers remain ignored, matching current visual routing. Locked elements remain obstacles. An attached endpoint shape is excluded only from the controlled glue stub; the rest of its visible outline is still forbidden/avoided according to its effective policy. A route may not cut through its endpoint shape interior.

When a layer policy is added in R23, explicit element policy will override a layer default; R22 does not define or evaluate layer routing roles. Future container rules may refine passThrough, but cannot weaken an explicit forbidden element without an explicit gateway operation.

Text bounds are separate from shape bounds. Text remains ignored by default. When text: avoid is set, the measured text bounds (including connector labels) join routing obstacles for other connectors. A connector does not treat its own label as an obstacle to itself. For rotated shapes, text bounds are transformed with the shape. The same text measurement path used by the editor is used by planning and diagnostics.

## Geometry and route results

Obstacle bounds use a conservative page-space axis-aligned envelope of the rotated element outline. The keep-out margin is the requested clearancePt plus half the visible stroke width. The base clearance remains 8 pt when no explicit value is supplied. Where a connector's arrowhead geometry contributes to a crossing or clearance, the planner uses its rendered bounds; unsupported or ambiguous arrowhead geometry is reported as an approximation rather than silently treated as exact.

The planner keeps R19's deterministic bounded search and stable connector-ID tie breaking. Hard-policy failures use UNRESOLVED_ROUTE with structured blocker IDs and a reason. A soft avoid violation has a separate stable diagnostic code and does not mark a feasible route unresolved. Route changes continue through R19/R20 routeChanges; policy-only edits remain ordinary document edits and route provenance describes any changed route.

## User and agent operations

The same values are available through the document contract, Inspector, DrawScript and MCP operations. Add or edit an element's routingPolicy; set or clear an endpoint preferredSide; inspect the effective policy and route diagnostics. Calls validate policy values and target IDs before planning. A hard-policy conflict returns a structured failure in the transaction result and does not silently relax policy.

Inspector controls expose obstacle behavior, extra clearance, text avoidance, and preferred endpoint side. Existing named fixed ports remain selectable through the current port UI/API. A concise explanation is shown when a dynamic endpoint uses a different side because the requested preference was infeasible.

MCP operation schemas document enum values, units, defaults, precedence and examples. Advisory routes remain engine-owned and cannot be set by callers.

## Compatibility and migration

All new fields are optional. Absent fields use the exact legacy behavior, without loading-time writes or changes to stored routes. The document schema version remains unchanged, following the additive R20 contract decision; an older strict reader may reject a document after a new routing policy is saved, which must be documented in release notes and compatibility output.

Visio export keeps visible geometry and preserves existing custom-port behavior. It does not claim native support for new obstacle policies or dynamic endpoint preferences. If a saved policy cannot be represented by VSDX, export emits a stable informational compatibility finding containing the element/connector ID and unsupported policy. It does not rasterize or discard the editor document's policy. Import restores policy only when it is present in supported user cells; otherwise the imported geometry follows the existing migration rule.

## Failure handling

When a transaction adds or changes a hard policy or fixed-port reference and its resulting routes cannot satisfy that policy, reject the transaction without changing the document. If a later geometry edit makes an existing route impossible to satisfy, commit the geometry edit, retain the prior route, and report UNRESOLVED_ROUTE with blocker IDs and a reason. A soft avoid violation is allowed only as a reported result. Bounded-search exhaustion remains observable and cannot be reported as a clean route.

Malformed or unknown policy values fail contract validation before transaction application. Hidden elements retain the current visual-routing default; R22 does not add a hidden-technical-layer override.

## Verification

The implementation is accepted when tests establish:

- Existing files with no routingPolicy preserve route coordinates and have no load-time writes.
- Explicit forbidden obstacles are never crossed; a large forbidden region is respected even when it covers more than 20% of the page.
- An impossible forbidden route is unresolved with blocker identity and retains the previous path.
- Soft avoid routes around an obstacle when feasible and reports a violation when not; pass-through elements cause neither a detour nor a crossing diagnostic.
- Text is ignored by default and protected text/connector labels are avoided by other connectors.
- Outline clearance accounts for stroke; rotated shapes, endpoint entry/exit stubs, dynamic preferred sides, and named static ports behave correctly.
- Inspector, DrawScript, MCP validation, canvas, hidden projection, layout inspection and export report the same effective policy and route for one revision.
- Undo/redo restores policy and route together; manual/frozen route modes and R21 segment pins do not silently change.
- Visio export emits stable compatibility findings for policies without verified native mapping.
- Existing routing, R19/R20/R21, R13 attachment, R16 join/hop, and export suites pass.

## Decisions requested in review

1. Is the routingPolicy object and its four obstacle values acceptable?
2. Should preferredSide stay soft for dynamic endpoints, with existing named static ports remaining the hard choice?
3. Is preserving legacy behavior for absent/automatic policies the right migration rule?
4. Is it acceptable to defer container gateways, layer/level order, and native Visio semantics to R17/R23/R29/R27?
5. Is the documented strict-reader compatibility limitation acceptable without a schema version bump?
