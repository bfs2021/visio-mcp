# R21 segment editing (stages 1 to 3)

Issue #60. Stages: 1 the engine, 2 the canvas, 3 guided routes (pinned runs).

## What exists

- `layout/routeSegments.ts`: the runs of an orthogonal route (`segmentsOf`), `moveSegment` (slides one inner run, neighbours lengthen or shorten, a run moved level with a parallel neighbour merges into it) and `simplifyRoute` (no repeated or collinear points; both ends stay).
- Operation `moveSegment {target, segment, to | by}`. Automatic becomes manual (the moved route is kept as waypoints), manual keeps its waypoints, frozen stays frozen with the moved route. The two runs that leave a port are refused with reason `segment_fixed`; locked elements and layers are refused by the usual edit check. DrawScript: `move-segment c segment=1 by=10pt` or `to=120pt`.
- Canvas: `components/SegmentHandles.tsx` draws a midpoint handle on each inner run of the selected connector, with a 16 px hit area at any zoom. The dashed preview is computed by the same function the command runs. One drag is one gesture and one transaction; Escape cancels. Snapping (`layout/segmentSnap.ts`): grid, centre-lines of the joined shapes, guides and other connectors' parallel runs, within 8 screen pixels, held until 1.5 times that distance. Alt turns it off.
- Keyboard: arrow keys on a focused handle nudge 1 pt, Shift nudges one grid step (10 pt without a grid). Inspector: a position field per run in the document unit.
- Jogs: already provided by R19/R20. A straight route whose ends fall out of line gets one jog, and aligning the ends again returns the two-point route (tested).

## Stage 3: guided routes

- **Contract.** `routing.constraints: [{axis: h|v, index, at}]` (document schema `RunConstraint`). A run is found by its axis and its position among the inner runs of that axis, counted from the start. A connector with constraints is *guided*; no new mode value is stored. Operation `pinSegment {target, segment, pinned}` and DrawScript `pin-segment` / `unpin-segment`.
- **Moving a run now pins it.** On an automatic or guided connector `moveSegment` stores only that run's constraint (and the new stored route), not waypoints. A manual connector still edits its waypoints and a frozen one stays frozen. This replaces the stage 1 behaviour (automatic became manual).
- **Planner.** The router draws the route as for an automatic connector (keep, follow, repair or search), then holds each pinned run (`guide` in `routePlanner.ts`). If a pinned run no longer exists, would cross an obstacle, or would change how the route leaves a port, the route is left as drawn and flagged `unresolved` with the reason, and the stored constraint is kept. Nothing is moved to a place the person did not choose.
- **Stability.** Pinned runs stay put when an end shape moves; the rest follows. Planning again from the stored route changes nothing (tested). Pins travel with a move, duplicate or paste (a turn drops them). Re-route keeps pins; **Reset to Automatic** (route mode select) drops them.
- **UI.** Pinned handles are filled. Double-click or right-click a handle to pin or release the run; the Inspector has a Pinned checkbox per run and shows `guided` as the mode. Grabbing a handle on a locked connector or layer explains the refusal at once and starts no drag.
- Export: a guided connector exports like an automatic one (its stored route); the pins are not written to the VSDX.
- Measured: `moveSegment` commit on the 60 shape / 60 connector page, 33 ms (Playwright, headless).

## Not built, and why

- Pins are addressed by axis and ordinal. If the router later draws a route with a different number of runs, the ordinal can point at a different run, or at none (then the route is flagged unresolved). Matching by position along the route would be more robust and is a possible refinement.
- Context menu: pin and release are on the handle (double-click, right-click) and in the Inspector, not in the canvas context menu. Add/remove waypoint on a run is not offered.
- Live follow of labels, pinned shapes (R13) and joined branches (R16) during an end-shape drag: they still settle on release.
- Manual check by the owner of the guided behaviour: NOT RUN.
