# Connector routing design brief: review and decisions

Reviewed 2026-10-20 by Claude against main (465de59) and PR #57 (R16). Brief: [connector-routing-brief.md](connector-routing-brief.md), written by ChatGPT from a research request by the owner, after the owner found connector edits hard to apply in `lego-infra-r08b.vsdx`. The owner added one point the brief covers thinly: **layers** (see R23).

The brief says it is a proposal for agent review and not an instruction to rewrite the router, and asks for an audit of main and contract proposals before coding. This document is the audit and the decision record; the work is split into issues R19 to R28.

## What the audit found on main

| Brief concern | Finding |
|---|---|
| Unrelated edits reroute connectors | Confirmed. `MaxGraphAdapter.project` signs every element's bounds, rotation, hidden state and layers plus every connector's ends. Any change clears the occupied tracks and reroutes **every** automatic orthogonal connector in z-order. Parallel-track choice depends on earlier routes. (R08b documents this as deliberate: a route is a pure function of the document. That is deterministic, not stable.) |
| No memory of the previous path | Confirmed. "Keep a clear route" compares with the R08 base route, not with the previous result. |
| Failure looks like success | Confirmed. When no clear route exists or the 80,000-step budget runs out the unchanged route is kept, even if it crosses a shape; only `inspect_layout` reports `CONNECTOR_CROSSES_SHAPE`. |
| A bend drag converts a whole route to manual | Confirmed. `intents.ts` `case 'bend'` writes all bends as absolute waypoints, and author waypoints are never rerouted. |
| No segment handles | Confirmed. maxGraph edge handlers give vertex and end handles only. |
| Obstacle semantics are heuristics | Confirmed. `selectObstacles`: text boxes, invisible shapes, lane structure, anything over 20% of the page and any shape that holds another candidate or an end are ignored. No user or agent control. |
| Router inside the canvas adapter | Confirmed. `orthogonalRoute` is a method of `MaxGraphAdapter`; the hidden projection graph is a second instance. No pure planner. |
| No route provenance or structured failure | Confirmed. |
| "R16 is proposed, not implemented" | **Out of date.** PR #57 implements joins (a branch end at a position along a trunk connector) and arc hops, with a real-Visio round trip. It is simpler than the brief's junction-ID topology (see R28). |
| Layers | The model has only layer flags (`visible`, `locked`, `printable`, `snap`, `glue`), no stacking order between layers; front-to-back is the element `zIndex`. Nothing in routing uses layers except skipping hidden ones. |

## Decisions

| Brief section | Decision | Where |
|---|---|---|
| 1 Product objective, 3 Invariants | **Adopt** as the acceptance frame. Invariants 1 to 5 and 7 become R19 and R21 criteria; 2 and 8 also R22 and R27; 6 (semantics beat appearance) is already how R16 works. | R19, R21 |
| 4 Route modes and persistence | **Implement.** Contract proposal reviewed in the issue before code. Persisting an advisory path is an owner decision (recommended: yes, for automatic orthogonal connectors only). | R20 |
| 5 Obstacles, ports and constraints | **Implement** forbidden, avoid and pass-through, label protection, outline clearance, rotation-correct ports, preferred sides. **Hold** the corridor/gateway region and port capacity. | R22; R28 |
| (owner) Layers behind a connector | **Implement, new.** A shape on a layer behind the connector is passed through; the same layer or in front is avoided; layer roles and element overrides; needs a defined layer order. | R23 |
| 6 Stable incremental routing | **Implement** (highest priority): impact sets, keep, elastic repair, local repair, full search, unresolved. Keep the existing search primitives; extract a pure planner. **Not now:** switching to a visibility graph or Hanan grid (only if baselines demand it). | R19; R28 |
| 7 Direct manipulation | **Implement.** Midpoint handles, jog create and collapse, snapping with hysteresis, locks, one-transaction gesture, keyboard and numeric control, live end-move preview. | R21 |
| 8 Branches, trunks, junctions | **Mostly delivered by PR #57** in a simpler form. **Hold** the junction entity, shared trunk painting, arrowhead dedupe and the "Create branch here" affordance until the owner has used the join model. Compare Visio representations in real Visio. | R16 #11; R27; R28 |
| 9 Crossings and hops | **Split.** Route first to avoid crossings, classify crossings, crossing policy: implement. Hop policy beyond `none`/`arc`: hold. | R24; R28 |
| 10 Visual polish and diagnostics | **Implement.** Quality function, tracks and fan-out, explicit priority property (not inferred from line weight), Beautify as an explicit command with preview and one-step undo; `inspect_layout` findings and overlay that tell defects from intent. | R24, R26 |
| 11 MCP, DrawScript contract | **Implement.** Route proposals without changing the document, commit against a base revision, scoped reroute and beautify, exact routed coordinates and provenance, "change only this run" or an error. | R25 |
| 12 Native Visio behaviour | **Implement** as an evidence-led matrix in real Visio, with machine-readable compatibility findings. Only map a mode to Visio cells after verifying it. | R27 |
| 13 Acceptance and regression | **Adopt**: coordinates and topology compared as well as pictures, PSGL and Lego fixtures, 60/60 stress case, bounded runtime. Baselines come first (R19). | R19 and all |
| 14 Delivery sequence CR1 to CR5 | **Adopt with changes.** CR1 = R19; CR2 = R20 + R21; CR3 = R22 + R23 + R24; CR4 = R16 (done in PR #57) plus the held junction work; CR5 = R25 + R26 + R27. Schema work happens inside each issue, not at the end. | below |
| 15 Definition of done | **Adopt.** | |

**Declined:** replacing the router or adding one inside maxGraph; treating every generated bend as authored; semantics from fill, kind or the 20% rule as the rule (kept only as migration default); implicit global optimisation; rasterising a network to hide a Visio limitation; persisting a computed path as the canonical route. Details and reasons: R28.

## Suggested order

1. **R19** baselines and stability (the 1 pt nudge). Nothing later is safe to build without it.
2. **R20** contract proposal reviewed (can overlap R19's second half), then **R21** segment editing, which is what the owner hit first.
3. **R22**, then **R23** (layers), then **R24**.
4. **R25**, **R26**, **R27** grow alongside; each earlier issue adds its operations and findings as it lands rather than waiting.

R23 and R22 touch `obstacles.ts` and the Layers panel and should not be built in parallel. R21 and R19 both touch `MaxGraphAdapter`; R19's planner extraction should land first.

## Decisions needed from the owner

1. **Persist the advisory route** (R19, R20)? Recommended yes, automatic orthogonal connectors only. Without it a reopened file is routed fresh.
2. **What "behind" means for layers** (R23): layer order, element z-index, or layer order then z-index (proposed)? And should old documents keep today's routing until they opt in (proposed)?
3. **Treat existing connectors that have waypoints as Manual** on migration (R20)?
4. **Keep the simple join model** from PR #57 for now and revisit junction entities only if it proves too limited (R28)?

## Owner decisions (2026-10-20)

1. **Persist the advisory route**: yes, for automatic orthogonal connectors. Routes edited manually by the user or by MCP are always preserved (R19 #58, R20 #59).
2. **"Behind"** means the Visio notion, element order (send backward/forward), not layer order. Layers controlling rendering order is a separate feature, R29 #69. Caution recorded in R23 #62: z-order alone cannot mean pass-through, because connectors normally sit in front of everything; pass-through also needs an explicit or suggested background role.
3. **Migration**: existing connectors with waypoints load as Manual (R20 #59).
4. **Junction and manually added vertex**: included now, as R30 #70, built on the R16 join model (PR #57). Struck from the on-hold list in R28 #67.
