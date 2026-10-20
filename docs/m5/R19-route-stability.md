# R19 Route stability (stage 1)

Issue #58. Design: `docs/design/connector-routing-brief.md`, review `docs/design/connector-routing-review.md`.

## What this stage delivers

A pure route planner, `src/web/src/layout/routePlanner.ts`, used by the live canvas (`MaxGraphAdapter.project`). It replaces the old
"any change re-routes every automatic orthogonal connector, in z-order, from empty tracks" behaviour.

For each automatic orthogonal connector, in connector id order (never z-order), it tries in turn:

| Step | Provenance | Rule |
|---|---|---|
| Authored | `authored` | Connectors with waypoints are computed from them, never searched. They claim their tracks first. |
| Keep | `kept` | Ends unmoved and the previous route still clear of the obstacles: identical points. |
| Elastic | `elastic` | An end moved: only the first and last runs follow it; interior tracks stay. |
| Local repair | `repaired` | An obstacle landed on one run: a detour is searched between the vertices either side of the blocked run. |
| Full search | `rerouted` | The R08b search, with the other routes' tracks occupied. |
| Unresolved | `unresolved` | Nothing clear found: the best route available is returned and flagged, never reported as success. |

Hysteresis has one limit: a kept route is dropped when the obstacle that forced its detour has gone and the clear default route is
shorter by more than 48 pt (two turns' worth). Smaller gains never flip a route.

Every returned route is checked for exact orthogonality (`routeDefects`: no diagonal, zero-length or redundant vertex).

Provenance and reason are in the projection (`ConnectorProjection.routeProvenance`, `routeReason`), and `inspect_layout` reports an
`UNRESOLVED_ROUTE` warning for an unresolved route.

## Evidence

| Check | Result |
|---|---|
| Unit tests `tests/routePlanner.test.ts` (13) | PASS |
| 60 shape / 60 connector baseline `tests/baselines/route-planner-stress-60.json` (coordinates, file snapshot, change needs review) | PASS |
| Fresh planning of the 60/60 case | 56 ms on the dev machine |
| 1 pt nudge of one shape in that case | 5 ms; 59 routes kept, 1 elastic, 1 change reported |
| Playwright: nudging an unrelated shape leaves every route identical | PASS |
| `inspect_layout` `UNRESOLVED_ROUTE` test | PASS |
| vitest 455, Playwright 140, Host 166, Ipc 33, Visio 156, Contracts 24, Mcp 14, `generate:check` | PASS |

Behaviour changes to note:
- Automatic connectors are routed in id order, not z-order, so a first load can place parallel tracks differently from before.
- Authored (waypoint) routes claim tracks before automatic ones, so an automatic connector now avoids running along an authored one.
  The R08b browser test used an authored connector between the same ports as an automatic one; it now uses different ports.

## Not done in this stage (remains on #58 and #59)

- **Persisting the previous route** (owner decision: yes, for automatic orthogonal connectors). Until it is saved, history lives only in
  the session, so a freshly opened document, the hidden projection and exports plan from scratch and may differ from a long-lived
  canvas. The contract needs review in #59 before code.
- The planner serves the canvas only; the hidden projection, `inspect_layout`, image and VSDX export and MCP use it through that
  adapter, but there is no standalone call yet and the command engine does not use it.
- Changed connector IDs and provenance in the transaction result.
- R16 joins (PR #57) as an input to the impact set, and the Lego (`lego-infra-r08b.vsdx`) and PSGL baselines: those files are
  not in the repository, so only the generated 60/60 diagram is recorded. Lego baseline: NOT RUN.
- Seeding the full search with the previous corridors; drag-latency budgets in the browser.
- Manual owner check of the editor: NOT RUN.
