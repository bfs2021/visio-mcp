# Connector routing and editing — design and implementation brief

**Project:** [rp2839/visio-mcp](https://github.com/rp2839/visio-mcp)  
**Purpose:** Deliver a connector system that is visually clean, easy to edit directly, semantically correct, and—above all—**predictable**.  
**Status:** Proposed design for agent review and decomposition; **not** an instruction to replace existing routing code wholesale.

## 1. Product objective

A good diagram editor should make tidy connectors almost effortless. Automatically produce clean orthogonal routes that avoid obstacles and unnecessary crossings, support actual T-junctions, maintain consistent separation, and simplify bends when shapes align. Equally important, a minor edit must **not** rearrange unrelated, carefully positioned connectors.

**Primary rule: preserve the user's intent and existing satisfactory geometry; optimise only what needs changing.** A route that is slightly longer but stable and legible is better than a marginally shorter route that moves unpredictably.

The following are separate operations, not synonyms: **calculate a new route**, **adjust one segment**, **add a routing preference**, **lock a segment**, **freeze a whole route**, and **optimise the entire diagram**. Implement distinct behaviours and persistence semantics.

## 2. Existing implementation and integration points

Read the repository before modifying it, particularly:

- [`docs/m5/R08-orthogonal-routing.md`](https://github.com/rp2839/visio-mcp/blob/main/docs/m5/R08-orthogonal-routing.md): R08 routes honouring endpoint ports and waypoints.
- [`docs/m5/R08b-obstacle-avoidance.md`](https://github.com/rp2839/visio-mcp/blob/main/docs/m5/R08b-obstacle-avoidance.md): grid/obstacle-edge search, 8 pt clearance, 6 pt track separation, 80,000-step budget. It currently recomputes all orthogonal routes on certain document changes, in z-order; its fallback can retain a route that crosses an obstacle.
- [`docs/m5/R08c-visio-route-preservation.md`](https://github.com/rp2839/visio-mcp/blob/main/docs/m5/R08c-visio-route-preservation.md): confirmed Visio `ConFixedCode=1` preserves authored waypoint routes during endpoint movement; automatic routes remain subject to Visio's own rerouting.
- [`docs/m5/R13-connector-attachments.md`](https://github.com/rp2839/visio-mcp/blob/main/docs/m5/R13-connector-attachments.md): length-based connector labels and shapes pinned to connector routes, including same-transaction position updates; in native Visio the pinned shapes are not live-linked.
- [R16 / issue #11](https://github.com/rp2839/visio-mcp/issues/11): multi-branch joins and line hops are proposed, **not assumed implemented**.
- `src/web/src/layout/{orthogonalRoute,obstacleRoute,obstacles}.ts`, `src/web/src/canvas/{MaxGraphAdapter,gestureHooks,intents,SnapshotProjector}.ts`, `src/web/src/layout/inspectLayout.ts`, `src/web/src/components/Inspector.tsx`, `contracts/document.schema.json` and `contracts/operations.schema.json`.

Preserve the architecture: the canonical document/command engine is authoritative; maxGraph cells and computed projection routes are derived. GUI, DrawScript and MCP must use the same transactional operations, validation and route planner. Native VSDX remains an editable-object export, not a flattened picture.

## 3. Non-negotiable behavioural invariants

1. **No surprise rerouting.** Editing shape A must leave connector C's path unchanged if A is not attached to C, does not intersect C's protected clearance and does not affect a junction/constraint C depends on. Preserve actual path coordinates, not merely endpoint IDs or semantic hashes.
2. **Hard obstacles are hard.** Never silently draw a route across an explicitly forbidden element, label or region. An infeasible new route is reported as unresolved, with a visible editing diagnostic and actionable choices, rather than presented as successful. Existing routes that become invalid retain their previous geometry where possible and are flagged until repaired; no silent violation on export.
3. **Intent survives movement.** A user-adjusted lane, segment, waypoint, port, junction or corridor remains recognisably in place when a connected shape moves. Change only the minimum dependent geometry needed to maintain valid glue and orthogonality.
4. **Orthogonality is exact.** Every orthogonal segment is axis-aligned in page coordinates (within the application's declared numerical tolerance). No tiny diagonal artefacts, zero-length segments or duplicate/collinear redundant vertices in the committed rendered route.
5. **A gesture is one edit.** Live preview may change during a drag, but pointer release produces one atomic, undoable command. Escape restores the prior path. Undo/redo restores route intent, junctions, pinned labels/shapes and route geometry consistently.
6. **Semantics beat appearance.** A geometric crossing is not a connection; a T-junction is an explicit network connection. Line hops must never create or remove topology.
7. **Deterministic and explainable.** Given the same canonical document, stable routing preferences and settings, the same initial route is generated. A re-route reports which connectors changed and why. No hidden global randomisation.
8. **Stable interoperability.** Preserve connector IDs, glue, author-created routing intent, independent editable elements and junction semantics as faithfully as the VSDX format allows; emit explicit compatibility diagnostics where it cannot.

## 4. Route modes and persistence

Expose understandable user-facing modes, backed by clear internal policy. Suggested model (names are proposals, not a mandatory schema):

| Mode | Meaning | On endpoint/obstacle movement |
|---|---|---|
| **Automatic (stable)** — default | Router chooses path; previous valid corridor has strong preference | Keep current path if legal, elastically adapt near the affected end, then repair locally; full re-route only as necessary |
| **Guided** | One or more user corridor hints/waypoints, or pinned segments | Preserve hints and locked tracks; optimise only unconstrained spans |
| **Manual** | User controls the route's meaningful bends | Preserve meaningful authored bends; add/remove purely mechanical elbows as necessary for orthogonality |
| **Frozen** | Whole route is explicitly protected | Adjust glued endpoint stubs only where possible; warn if moving an endpoint makes constraints impossible; do not substitute a new route silently |

**Do not treat every generated vertex as an authored waypoint.** Currently bend drags can write all bends as absolute waypoints, converting the entire route to manual. Store separately: (a) authored constraints, (b) optional advisory/preferred path or track IDs, and (c) derived render path. Segment locks should carry stable IDs or references and a coordinate/relative offset with explicit anchoring rules; avoid tying intent solely to array indices that change on simplification.

A new schema might have a `routing` object containing `mode`, `waypointConstraints`, `segmentConstraints`, `preferredCorridors`, `avoidance` and `crossingPolicy`. Validate references, use schema versioning/migrations, update generated TypeScript/C# contracts, and do not overload unrelated style or metadata fields. Preserve existing `route` values and `waypoints` semantics during migration. Treat computed path geometry as a projection unless it is deliberately persisted as an **advisory stability preference**; explain when that preference causes document changes. Save/reload must not materially rearrange paths.

## 5. Obstacles, ports and routing constraints

Provide element-level and optional region-level routing policies:

- **Forbidden (hard):** route and its declared clearance must not cross the element. Respect shape outline and stroke where feasible; conservative rotated bounds are acceptable initially. Support transparent but deliberately protected regions.
- **Avoid (soft):** prefer a clear route, but allow an explicitly reported violation if geometry offers no reasonable alternative.
- **Pass-through / background:** decorative frames and backdrops; a route may travel inside them. Their borders may need separate crossing rules.
- **Corridor / gateway:** an intentional permitted passage through an otherwise protected container or swim-lane boundary.

Do **not** determine routing semantics only from fill visibility, element kind or a `20% of page` size heuristic. Those can remain migration defaults, but users need explicit override. A locked element remains an obstacle. Labels and text can be protected where appropriate; do not blanket-ignore all text boxes. A shape attached at an endpoint is exempt only for the controlled entry/exit stub, not its whole interior. Hidden/layer-hidden elements are ignored for visual routing by default, with deliberate policy for printable/hidden technical layers.

Use clearance from the **visible stroke/shape outline**, not merely its centre-line. Account for arrowhead size, labels, corner clearance, rotated shapes and pinned shapes. Define precedence when policies conflict. Keep port glue correct for the shape's rotation; support fixed ports, dynamic perimeter ports and constrained preferred sides. A router must not choose a direction that cuts through the connected shape. For multiple links, stagger entry/exit tracks and fan out rather than drawing several connectors directly on top of one another. Port/corridor capacity and allowed directions should be extensible.

Swim-lanes, nested containers and background frames require semantic distinction between **inside**, **outside**, **border** and **permitted crossing**, rather than being universally ignored or universally blocked.

## 6. Stable incremental routing strategy

The existing `orthogonalRoute.ts` and `obstacleRoute.ts` provide useful geometry/search primitives. Keep them and evolve the orchestration rather than introducing a second conflicting router inside maxGraph.

On an edit, compute an **impact set**: connectors attached to moved/resized/rotated shapes; connectors whose current route/clearance intersects changed hard obstacles; connectors attached to altered junctions; and any connectors whose explicit constraints now reference changed objects. Neighbouring routes can be reconsidered only if a genuine track conflict is created. A harmless nudge must not invalidate every connector or change route priority because of z-order.

For an affected connector, attempt the following in order:

1. **Keep:** reuse its current preferred path if legal and all hard constraints still hold.
2. **Elastic endpoint repair:** move only the minimum endpoint stubs/elbows to follow changed glue while retaining interior track coordinates. Insert a short dogleg if needed; remove it when alignment eliminates the need.
3. **Local repair:** splice a detour around a newly blocking obstacle between stable upstream/downstream segments, retaining locked tracks, labelled portions and unaffected branches.
4. **Full search:** run a deterministic orthogonal router (obstacle-edge visibility graph or sparse Hanan/grid graph using A*/Dijkstra with direction as part of the state), seeded by the previous preferred corridors. Only then choose a completely new path.
5. **Unresolved:** if no hard-constraint-compliant path exists, return structured failure and retain/flag the previous route as appropriate. Never silently reinterpret a failed route as successful.

Separate **feasibility** from **aesthetics**. Hard constraints (forbidden crossings, endpoint glue, required waypoints/segment locks, direction and topology) are inviolable. Among feasible routes use a documented weighted or lexicographic quality function: minimise accidental connector crossings, overlap with other connectors, bends, visual awkwardness, path length, proximity to obstacles and—critically—**deviation from the previous route**. Penalise many tiny steps, reversing or needlessly zig-zagging, and crossing a label. Prefer consistent clearance and parallel tracks. Penalise unnecessary route changes even when a slightly shorter path exists; use hysteresis so tiny edits do not flip equally good solutions.

Make crossing detection geometric and explicit: perpendicular intersections, collinear overlapping runs, T-joins, endpoint contacts and parallel near-misses are different cases. A bend penalty alone will not achieve good aesthetics. Stable connector/order IDs should break ties; rendering z-index alone must not decide route priority. Provide congestion/fan-out costs that prevent all routes collapsing onto the same track.

**Global optimisation must be a deliberate command** (`Beautify all connectors`) with a before/after preview, changed-connector count, and one-step undo. It must never trigger merely because a shape moved or an unrelated property changed.

## 7. Direct manipulation: the essential UX

When a connector is selected, expose a **grabbable midpoint handle on each meaningful horizontal or vertical segment**, not only each vertex. Handles should remain visually small but have generous screen-space hit areas and be reachable at different zoom levels. Hover highlights the affected run and indicates its drag axis.

Dragging a horizontal run vertically (or a vertical run horizontally) should:

- Maintain its orientation and move it along its perpendicular axis.
- Extend, shrink or insert its two adjacent orthogonal segments as required, with a continuously updated preview.
- Preserve remote segments, glue points, labels, branches and constraints unless they are truly dependent.
- Snap to grid, shape/port centre-lines, nearby parallel connectors, guide lines and existing tracks (with sensible thresholds in screen pixels and a modifier to temporarily disable snapping).
- Remove redundant bends when adjacent parallel runs become collinear; avoid oscillation around the snap threshold by using hysteresis.
- Respect segment locks; explain blocked drag rather than moving an unrelated segment. Honour locked layers and elements.
- Commit **only the relevant routing constraint** for that run, not every generated bend. Permit the user to unpin that constraint or return the selected connector to Automatic.

When dragging a straight connector away from alignment, automatically create the minimum necessary orthogonal jog. When bringing the shapes or dragged runs back into alignment, collapse the jog automatically if it contains no authored/locked point or junction. A user must be able to remove a step simply by dragging the runs together; dragging a segment across another must not leave a pile of tiny stale vertices.

Add distinct, discoverable affordances for **move segment**, **move bend**, **add/remove waypoint**, **pin/unpin segment**, **change port/side**, **reset route**, **reroute around obstacle**, **freeze/unfreeze route** and **optimise selected**. Context menu + Inspector controls are sufficient initially; avoid cluttering the resting canvas. Keyboard nudges on selected segments should use the same semantics and undo system. Allow precise numeric positioning of selected segments. For an endpoint move, show a live route preview; labels and pinned elements must track the preview, not jump only after release if practicable.

Use maxGraph's handlers for hit-testing/rendering where beneficial, but do not let its automatic routing silently mutate the canonical model. Integrate with the existing gesture admission gate and one-transaction command engine.

## 8. Branches, trunks and junctions (R16)

Support **real connector networks**, not a convincing picture made from unrelated lines. A branch should be attachable to a trunk, to a named junction, or to a compatible shape port. Hovering over a connector offers an explicit **Create branch here** affordance. The junction has stable identity and visible edit feedback (for example a small junction dot when selected); an incidental crossing never creates one.

Prefer a topology model of stable junction IDs and connected legs/edges. A network may share a single visible trunk segment while multiple branches join it; avoid overpainting identical segments and avoid duplicated arrowheads at the join. A dragged trunk/junction moves its dependent branches predictably; a rerouted trunk preserves connections. Branches should maintain 90° joins where possible and merge gracefully into a common bus, with regular spacing and sensible arrowheads at the actual destinations. Define what happens when a junction or trunk is deleted, a segment is split/merged, or a network is copied/grouped.

Visio interoperability needs an **explicit tested mapping**, potentially to glued connector legs and small invisible junction shapes carrying stable identity/user cells. Do not assume native Visio treats arbitrary crossing or shape-on-line attachment as network topology. Round-trip and native-edit tests must prove the chosen representation, or the exporter must report a specific limitation.

## 9. Crossings and line hops

First try to **remove unnecessary crossings by routing**; line hops are a last-resort visual disambiguator, not a substitute for routing quality. Distinguish intentional junctions from non-connecting crossings. Provide policy to prefer no crossing, permit crossings, or require explicit approval for a crossing in designated areas. Prioritise continuous main trunks over small branches.

At an unavoidable orthogonal crossing, optionally render a small consistent **bridge/hop**, assigned to one connector according to a deterministic or explicit user policy. Do not draw two hops, obscure nearby labels/arrowheads, or create a fake junction. Add per-document defaults and per-connector overrides (never, automatic, always, preferred over/under). Choose hop radius relative to stroke size and zoom-independent page geometry. Automatic hops must adapt to moves and disappear when the crossing disappears. Verify editable VSDX export/import behaviour in actual Visio; if a renderer cannot preserve a hop, report the limitation rather than flattening the whole diagram.

## 10. Visual polish and diagram-wide tools

Aesthetic consistency matters as much as shortest paths. Prefer a few clear main horizontal/vertical routes; regular parallel spacing; symmetric departures; aligned port entries; gentle, purposeful elbows; and clear space around labels. Distinguish line style/colour semantics from routing priority: a thick infrastructure trunk may get route priority, but crossing policy should be explicitly configurable.

Provide non-destructive read-only diagnostics through `inspect_layout` and optional overlay: obstacle crossings with IDs and bounds; connector-to-connector crossings; unintended shared segments; near-zero segments; excess bends; too-tight clearance; cramped labels; unstable/unresolved routes; and suggestions to straighten or align. Warnings must distinguish actual defects from intentional container boundary crossings, decoration, valid T-junctions and deliberate manual routes. A **Preview beautification** command should compare current/proposed routes before committing. The user should be able to choose selected connectors only versus the full page.

## 11. MCP, DrawScript and programmatic contract

The LLM must have exactly the same routing controls available to a human and no way to bypass hard constraints silently. Provide discoverable schemas, examples and units for operations conceptually equivalent to:

- Set routing mode and policies on a connector or protected region.
- Connect via a particular shape port/side; create and move a named junction or branch.
- Add, move or remove a route waypoint; pin/unpin or shift a specific segment/track.
- Ask for **route proposals** without changing the document, optionally scoped to selected IDs.
- Commit a selected proposal against a document revision; explicitly **reroute selected** or **beautify page**.
- Inspect exact routed coordinates, junctions, obstacles, crossings, quality diagnostics, route provenance (`kept`, `elastic`, `locally repaired`, `fully rerouted`) and changed IDs.

Follow the existing base-revision/transaction model: agent operations target stable IDs, not brittle array indices; proposals are read-only until committed; the result reports visually affected connector IDs and structured route failures. An instruction such as **“Move the vertical run of link L 12 pt right; do not alter any other connector”** must either make just that change or return a constraint/conflict error. Never silently relax the scope.

The router itself should be pure and usable by GUI projection, offscreen rendering, `inspect_layout`, image export, native VSDX export and MCP preview. Avoid divergent route calculations between the live canvas and hidden `SnapshotProjector`.

## 12. Native Visio behaviour and evidence

Preserve existing R08c observations and tests. Map Guided/Manual/Frozen routes to native Visio geometry and appropriate ShapeSheet route-fixing cells only after verifying their behaviour with real Visio. R08c has empirical evidence for `ConFixedCode=1` on authored waypoint routes, but that does **not** mean every routing mode has identical native semantics. Distinguish **Visio recalculation within Visio** from **canonical recomputation when re-importing into the editor**.

Test at least: glued-end movement horizontally and vertically; static side ports; segment preservation; a connector with one branch and another crossing but not connected; label position; line hops; save/reopen in Visio; import into this app; and embedding/activating the VSDX in Word. Any loss of segment locks, junction semantics, hops or routing preferences must appear in machine-readable compatibility findings. Preserve object editability; do not rasterise an entire connector network to hide a limitation.

## 13. Acceptance criteria and regression scenarios

Build small, deterministic **unit fixtures**, direct-manipulation **Playwright tests**, MCP transaction tests, and Windows/Visio/Word acceptance evidence. In particular:

| Scenario | Required result |
|---|---|
| Move an unrelated shape by 1 pt beside a complicated diagram | Byte-for-byte identical unaffected `routePoints` within documented coordinate normalisation; no unexpected changed IDs |
| Move an endpoint shape a few points | Same interior track; only necessary entry/exit stubs or local elbows change; glue maintained |
| Straight line becomes misaligned and then aligned again | The minimum orthogonal dogleg appears and disappears; no redundant vertices remain |
| Drag the midpoint of an internal segment | Only local geometry changes; drag preview matches commit; one undo restores the original |
| A hard obstacle is added in a route's path | Valid local detour, or explicit unresolved failure; never an unreported hard crossing |
| Force a dense or impossible route/budget exhaustion | Deterministic diagnostic, bounded runtime, no successful-looking forbidden crossing |
| Two or more parallel connectors | Separate tracks and consistent spacing; no long accidental overprinting |
| Unavoidable crossing / T-junction | Crossing remains disconnected, optional one hop; T-junction remains connected as shapes move |
| Move trunk or junction | Branch endpoints remain attached; labels/pinned shapes stay associated; undo is atomic |
| Save, close, reopen; then export/import VSDX | Intent, visual geometry and glue remain stable within documented compatibility limits |
| Re-run renderer/projector and multiple different editor/MCP requests | Same geometry for same canonical revision and routing preferences; identical results across surfaces |

Use the repository's actual PSGL swim-lane and Lego infrastructure regression cases (101 shapes/67 connectors in the latter), not only hand-picked toy cases. Compare route coordinates and topology **as well as screenshots**. Keep separate editor and native-Visio image baselines, with explicit review for baseline changes. Measure drag latency and routing completion on the existing 60-shape/60-connector stress case and denser worst-case fixtures. Define realistic platform-specific budgets from current baseline measurements; enforce a bounded search and an observable failure mode, not freezing or silently violating hard policy.

## 14. Proposed delivery sequence

**Package CR1 — Route stability and diagnostics (highest priority).** Baseline route-change fixtures and current behaviour, extract/shared route-planning seam where needed, compute targeted impact sets, keep valid existing routes, expose route provenance and unresolved hard-constraint findings. Crucial acceptance: the 1 pt unrelated nudge cannot rearrange other connectors.

**Package CR2 — Segment-editing UX.** Mid-segment drag handles, live elastic orthogonal preview, minimal local waypoints/segment constraints, snap/straighten/collapse, keyboard nudge, one-step undo/redo, Inspector/context-menu actions. Avoid converting every computed bend into manual waypoints.

**Package CR3 — Constraints and aesthetic routing.** Explicit hard/soft/pass-through semantics, label and region avoidance, deterministic crossing and overlap penalties, track spacing/fan-out, endpoint-side constraints, local reroute and preview-selected/global beautify.

**Package CR4 — Networks and hops (coordinate with R16).** Stable junction topology, branch/trunk editing, real T-junctions and crossing disambiguation; R16's issue ownership and dependencies must be checked before claiming or overlapping work.

**Package CR5 — Contracts and interoperability hardening.** Fully discoverable MCP/DrawScript operations, schema migration, round-trip fidelity, native-Visio move and Word acceptance, diagnostics and complete regression evidence. API/schema work will also be required incrementally in CR1–CR4, not deferred here.

Keep these as reviewable issues/PRs, following [`docs/development/issue-workflow.md`](https://github.com/rp2839/visio-mcp/blob/main/docs/development/issue-workflow.md); do not silently implement a broad rewrite or duplicate existing R08/R08b/R08c/R13/R16 scope. Begin with an audit of what current main already satisfies and document proposed contract changes **before coding**.

## 15. Definition of done

The outcome is not simply “every route is orthogonal.” A user can create a busy, attractive technical diagram, place trunks and branches, drag an individual run into alignment, eliminate unnecessary bends with one movement, and move unrelated objects without their finished connectors changing. Impossible requests are explained, not silently compromised. The same intent is exposed safely to an LLM via MCP/DrawScript, and exported Visio remains natively editable with documented, tested limitations.
