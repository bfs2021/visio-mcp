# R08c: keeping authored routes when Visio moves an attached shape

Recorded 2026-10-18. GitHub issue #3. Follows [R08](R08-orthogonal-routing.md) and [R08b](R08b-obstacle-avoidance.md).

## What native Visio does (measured)
`tools/probe-visio-reroute.ps1` opens a copy of an exported file in Visio, moves a shape and prints each glued
connector's Geometry1 vertices before and after. On `artifacts/swimlane/swimlane-r08.vsdx` (VM-SCH03 moved 0.2 in right):

| Connector | Before | After moving the shape |
|---|---|---|
| VM-SCH03 to Lego SFTP (authored waypoints), default cells | 8 vertices | **14 vertices**: Visio recalculated the route itself, added line-jump arcs and changed the path |
| VM-SCH03 to Lego SFTP, `ConFixedCode` 1, 2 or 3 | 8 vertices | **8 vertices**: bends stay at the same page positions; only the glued end and its neighbouring vertex follow |
| Unrelated connector | 7 vertices | unchanged |

So Visio does throw the exported route away when an attached shape moves, and a nonzero `ConFixedCode`
(Shape Layout) stops it. Evidence: `artifacts/swimlane/r08c-baseline-default-reroute.txt` (default cells) and `artifacts/swimlane/r08c-fixedcode-1-2-3.txt`.

## Policy
The exporter writes `ConFixedCode = 1` on an **orthogonal connector that has author waypoints**. Automatic routes (no waypoints)
stay reroutable: Visio recalculates them, as it does for any connector it owns. Straight connectors are not affected.
Glue is unchanged, so the ends still follow the shapes.

## Verification on the new export (`swimlane-r08c.vsdx`)
- ConFixedCode is 1 on the three waypointed connectors and 0 on the rest.
- Moving VM-SCH03 right by 0.2 in: the two waypointed connectors keep 8 and 7 vertices with the bends at the same page positions;
  the automatic VM-SCH03 to VM-FIL01 connector went from 3 vertices to 11 (Visio's own reroute).
  `artifacts/swimlane/r08c-native-move.txt`
- Save, reopen: the geometry is identical after reopening (same file).
- Moving the shape 0.3 in down: the route stays orthogonal; the vertex next to the moved end moves with it, so no diagonal appears.
  `artifacts/swimlane/r08c-native-move-vertical.txt`
- Importer: waypoints and route come back (`OnlyAuthoredWaypointRoutesAreFixedAgainstVisioRerouting`).

## Trade-offs
- A fixed route does not reroute itself: moving a shape a long way in Visio can leave the old bends crossing other shapes. Visio's
  Reroute command still works on the connector; the editor recomputes it from the waypoints when the file is next opened there.
- Automatic routes (including R08b detours) are not preserved in Visio after a move. Visio's own reroute of even a simple
  straight link was seen to add hops and a detour (3 to 11 vertices). If that proves a problem, fix every bent orthogonal connector too.
- Only 0 versus nonzero was tested; 1 was chosen as the documented value. The cell is a Visio ShapeSheet cell, not guaranteed for other
  Visio versions; the test machine's version is recorded in the I05 evidence.
