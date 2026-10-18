# R08: orthogonal routing that honours ports and waypoints

Recorded 2026-10-18. Found by the PSGL swim-lane and Lego infrastructure recreations
([swim-lane](R08-swimlane-comparison.md), [Lego](R15-lego-infrastructure-comparison.md)).

## Problem
maxGraph's elbow style ignored which side of a shape a port is on and used only the first
waypoint as a hint, so arrows entered top ports sideways and multi-bend routes collapsed.
The workaround (straight route with waypoints) drew correctly in the editor but Visio dropped
the waypoints, leaving diagonals.

## Change
- `src/web/src/layout/orthogonalRoute.ts`: pure routing in page points.
  - `endFor` turns a connector end into an anchor and an outward axis. Static ports use the port
    fraction (the four side names, or the shape's own ports), rotated with the shape. Dynamic ends
    face the other end and float, so the renderer finds the outline point. Free points face the route.
  - `routeOrthogonal` makes L, Z and U shapes that leave and enter along the end axes, and joins
    author waypoints in order with elbows (never a diagonal).
- `MaxGraphAdapter.upsertEdge`: orthogonal connectors get their computed bends as explicit
  geometry points and no maxGraph edge style; static ends are pinned with exit/entry constraints
  (perimeter only for side midpoints). The projection therefore carries the real route, which
  `inspect_layout`, renders and the Visio exporter already use.
- `tools/publish-local.ps1`: builds and installs the app and companion to a named version folder.

## Evidence
| Check | Result |
|---|---|
| `tests/orthogonalRoute.test.ts` | 10 pass (Z, L, U, behind-the-source, waypoints, rotation, no diagonals over 24 port/position combinations) |
| Browser: ports and waypoints in the real editor | pass |
| Suites | 311 unit, 48 browser, 124 Visio, 13 MCP |
| Swim-lane, orthogonal ports + 2 waypointed routes | `artifacts/swimlane/mine-r08.png`, `mine-r08-visio.png`: matches original routing; export lossy:false |
| Lego, 67 connectors (12 with waypoints) | `artifacts/lego-infra/mine-r08-visio.png`: no diagonals; export lossy:false |

## Limits
No obstacle avoidance (R08b). Visio recalculates a glued connector's route if an attached shape is
moved there (R08c). Bend-handle drags store every bend as a waypoint, which makes the route manual.
Existing documents with orthogonal connectors and waypoints now follow those waypoints rather than
maxGraph's single-hint elbow.
