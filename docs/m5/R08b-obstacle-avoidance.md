# R08b: orthogonal routing around other shapes

Recorded 2026-10-18. GitHub issue #2. Builds on [R08](R08-orthogonal-routing.md).

## Policy
- **Which routes.** Only automatic orthogonal routes (no author waypoints). Waypoints are never rerouted:
  the author's route wins.
- **Keep a clear route.** The R08 route is used unchanged when it is clear. Only a blocked route, or one that
  would run along an earlier connector, is searched again.
- **Obstacles** (`layout/obstacles.ts`): visible shapes and pictures. Not obstacles: the connector's own two shapes
  (those are avoided except for the stub that leaves or enters them), text boxes (labels sit on lines on purpose),
  invisible shapes (no fill, no line), hidden or layer-hidden elements, and containers or backdrops. A container is a
  shape that holds another candidate, holds either end shape or a free end point, or covers 20% or more of the page.
- **Clearance** 8 pt around every obstacle; a 12 pt stub leaves and enters each end.
- **Search** (`layout/obstacleRoute.ts`): least-cost orthogonal path on the grid formed by the obstacle edges (and a 6 pt
  track beside each), cost = length + 24 per bend, deterministic tie-breaking, 80,000-step budget.
- **Parallel spacing.** Connectors are routed in z-order. A later route pays 1 per point it runs along an earlier one, so
  it uses the neighbouring 6 pt track when that is cheaper than overprinting.
- **No route.** If no clear route exists or the budget runs out, the result is the unchanged R08 route. That is
  deterministic, and `inspect_layout` already reports it as CONNECTOR_CROSSES_SHAPE.
- **Determinism.** Any change to a shape or an orthogonal connector re-routes all orthogonal connectors, in z-order,
  from empty occupied tracks, so a route is a pure function of the document.

## Evidence
| Check | Result |
|---|---|
| `tests/obstacleRoute.test.ts` | 7 pass: clear route kept, detour, clearance, end shapes avoided, walled-in is blocked and returns the base route, deterministic, parallel fan-out |
| `tests/obstacles.test.ts` | 4 pass: what is and is not an obstacle |
| Browser: detour round a wall inside a container, author waypoints untouched, straight route back when the wall moves | pass |
| Browser: 60 shapes and 60 connectors, deterministic, quick | pass |
| Suites | 325 unit, 51 browser |
| Swim-lane, automatic routes without waypoints | `artifacts/swimlane/mine-r08b.png`, `mine-r08b-visio.png`: both long routes go around shapes in the editor and in Visio |
| Lego fixture (every connector has author waypoints or free ends) | unchanged: export lossy:false, the same layout findings as before |

## Limits
Routes minimise length and bends, not appearance: in the swim-lane example the two long routes run close together beside the
lane line. Use waypoints to steer. Lane rules and other lines are not obstacles. The search is on a coarse grid, so a very
dense page may exhaust the budget and fall back to the R08 route. Visio still recalculates a glued connector's route if an
attached shape is moved there (R08c).
