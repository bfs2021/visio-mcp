# Routing contract proposal (R20 #59, with the persistence part of R19 #58)

Status: **proposal for owner review. No code written.** Decisions already taken by the owner (2026-10-20/21): the previous route is saved for automatic orthogonal connectors, routes edited by the user or MCP are always preserved, and existing connectors with waypoints load as Manual.

## 1. What the audit found

- `ConnectorElement` has `route` (`straight|orthogonal|curved`) and `waypoints`. Nothing records who chose a route, and the router's output is not stored anywhere.
- After PR #72 the planner (`layout/routePlanner.ts`) keeps routes, but its memory is the canvas adapter's session map. The hidden projection, a reopened file and an export plan from scratch, so surfaces can differ.
- The planner is pure (`planRoutes(elements, layers, pageArea, previous)`), needs no maxGraph, and can run inside the command engine. `settleAttachments` and `settleJoins` already show the pattern: compute after a plan, add upserts to the same forward/inverse diff, so one undo reverses all of it.
- The schema is additive-friendly (`schemaVersion` is 1, R16 added `join` and `hops` without a bump), but readers are strict (`additionalProperties: false`), so a file written by a newer app is rejected by an older one.

## 2. Proposed contract

```jsonc
// ConnectorElement (document.schema.json), all new fields optional
"routing": {
  "mode":     "automatic" | "guided" | "manual" | "frozen",   // absent = derived, see 3
  "advisory": [ {x, y}, ... ]                                  // derived route, whole path incl. both anchors, maxItems 512
}
```

- **`mode` absent is derived, not stored:** `waypoints` non-empty reads as **manual**, otherwise **automatic**. That is the owner's migration rule with no document rewrite and no migration step. Existing documents load unchanged and the fixtures need no edits.
- **`advisory`** is the planner's last result for this connector. It is derived data: never authoritative, written only by the engine (section 4), never accepted from MCP or DrawScript, and ignored for `straight`/`curved` routes. Written for `automatic` and `guided`; for `frozen` it is the authority (see 3); absent for `manual` (its authority is `waypoints`).
- **Not in stage A:** `segmentConstraints`, `preferredCorridors`, `avoidance`, `crossingPolicy`. Their shape is outlined in section 6 so stage A does not paint us into a corner.

### Operations (same set in GUI, DrawScript, MCP)

`createConnector` and `patch` get `routing: { mode }` only.

| Request | Effect |
|---|---|
| `mode: automatic` | Clears `waypoints` and `advisory` (reset route); the planner replans. |
| `mode: manual` | Keeps the current drawn route as `waypoints` (so nothing moves), clears `advisory`. |
| `mode: frozen` | Copies the current drawn route into `advisory` and stops all planning of it. |
| `mode: guided` | Rejected until stage B (needs segment constraints from R21). |
| bend drag, `waypoints` edit | Sets `mode: manual` implicitly (today's behaviour, now named). |

DrawScript: `route=orthogonal mode=automatic|manual|frozen`; MCP: `routing.mode`. Rejections use the structured error shape already used by joins (for example frozen on a connector whose ends are not both glued or whose ends cannot be reached).

### Mode behaviour (stage A)

| Mode | Planner | When an end or obstacle moves |
|---|---|---|
| automatic | keep, elastic, repair, search, unresolved (as PR #72) with `previous` = `advisory` | route follows with minimum change; writes the new `advisory` |
| manual | `routeOrthogonal` through `waypoints`, never searched (as today) | waypoints stay; elbows between them follow the ends |
| frozen | `advisory` only: elastic end follow, no search, no repair | if it cannot follow (an end turned to another side) the route is kept as it was and reported `unresolved` with a reason; never replaced silently |

## 3. Persistence and the command engine

1. New engine step `settleRoutes(before, plan)` after `settleJoins`, before `settleAttachments`. It calls `planRoutes` on the document the plan would produce, with `previous` built from each connector's `routing.advisory`, and adds an upsert only for connectors whose advisory changed (compared with tolerance 0.01 pt). The inverse diff restores the old advisory, so **undo and redo restore routes exactly**.
2. The canvas adapter and the hidden projection take `previous` from the document the same way and drop the session map (kept only as a fallback for a gesture preview that has no committed document yet). Same document, same geometry, on every surface.
3. **Idempotence is a tested invariant:** planning a document whose advisories came from planning it changes nothing (`plan(doc, plan(doc).routes)` is all `kept`). `settleJoins` and `settleAttachments` read routes through the projection, which then re-plans from the already updated advisories and agrees.
4. **Loading never writes.** Opening a file plans in memory from the stored advisory; the document becomes dirty only when a transaction changes something. A reopened file therefore shows the stored routes and does not rearrange.
5. **Change report.** `TransactionResult` gets an optional `routeChanges: [{id, provenance, reason}]` (from `PlanResult.changes`), separate from `changed`, so an advisory-only update to a neighbouring connector does not look like an edit by the author. `get_objects` shows `routing.mode` and provenance; the advisory points are omitted unless asked for.
6. **Precision.** Points are rounded to 0.001 pt on write; the planner compares ends with a 0.01 pt tolerance (it currently uses 1e-6), so stored values do not look like a moved end.
7. **Copy, paste and duplicate** translate the advisory by the same offset as the copy. Duplicate page copies it unchanged. Paste into another document keeps it; if the ends differ the planner follows elastically.
8. **Joins and hops** are unchanged: `join` ends are placed on the target route as drawn (advisory included); hops are drawn from the final routes.

## 4. Visio and the other readers

- Export: the drawn route is already written as geometry. `ConFixedCode=1` stays for connectors with authored waypoints (R08c). Whether `frozen` also writes it, and how `automatic` should look in Visio's own recalculation, is decided in R27 against real Visio, not here. Until then `frozen` exports like `manual`.
- Import: the importer produces waypoints for a drawn route as now, so imported connectors read as `manual`; this is the same rule as migration. R27 may refine it.
- Older readers reject files with `routing` (strict schema). This is the same cost R16 accepted; the app's own versions move together. No version bump is proposed.

## 5. Tests that would gate stage A

- Contract: generated TS/C# round trip; schema rejects `advisory` in MCP input; `guided` rejected.
- Migration: PSGL and Lego-like fixtures with waypoints read as `manual`, byte-identical routes; connectors without waypoints read as `automatic`.
- Engine: advisory written for automatic orthogonal connectors only; unchanged connectors get no upsert; undo/redo restore advisory and route; idempotence; paste/duplicate offset; delete of an end shape.
- Stability: save, reopen, export and compare `routePoints` of every connector (no change); 1 pt nudge of an unrelated shape changes no advisory.
- Modes: reset to automatic, freeze, unfreeze, manual-to-automatic, frozen with an impossible end (unresolved, route retained).
- Surfaces: canvas projection, hidden projection, `inspect_layout`, VSDX export give identical geometry for one document.
- Drag latency and the 60/60 stress case stay within the budget measured in PR #72 (fresh 56 ms, nudge 5 ms).

## 6. Outline for stage B (not part of this approval)

`segmentConstraints: [{ id, axis: "h"|"v", coordinate, anchor: "page"|"from"|"to", between: [vertexKey, vertexKey]? }]` with stable ids and an explicit anchor so a constraint survives route simplification; `guided` plans only the spans between constraints. It is created by the segment drag of R21 #60 and the manually added vertex of R30 #70, and carried to Visio by R27.

## 7. Alternatives considered

- **Session-only memory** (current PR #72): rejected by the owner; reopened files and other surfaces differ.
- **Store only interior points:** smaller, but the anchors are what show an end has moved; storing the whole path keeps the planner's input self-contained.
- **Store track ids / segment ids now:** needs the segment model of R21 first; deferred.
- **Frozen as promoted waypoints:** simple, but every bend becomes an authored waypoint, which is the problem R20 exists to remove, and Manual and Frozen would be indistinguishable.

## 8. Questions for the owner

1. Is `frozen` as "advisory is the authority, planner never searches" right, rather than promoting the route to waypoints?
2. Stage A without `guided` and segment constraints (they land with R21), correct?
3. When the planner changes in a later release, stored routes stay as they are until the user resets or optimises them (R24). Acceptable?
4. Report route updates of neighbouring connectors in a separate `routeChanges` list rather than in `changed`?
5. No schema version bump, accepting that older app versions cannot open files saved with `routing`?
