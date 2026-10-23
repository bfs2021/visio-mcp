# R21 segment editing (stage 1 and 2)

Issue #60. Stages: 1 the engine, 2 the canvas. Stage 3 (Guided mode, per-run constraints, context menu) is not built.

## What exists

- `layout/routeSegments.ts`: the runs of an orthogonal route (`segmentsOf`), `moveSegment` (slides one inner run, neighbours lengthen or shorten, a run moved level with a parallel neighbour merges into it) and `simplifyRoute` (no repeated or collinear points; both ends stay).
- Operation `moveSegment {target, segment, to | by}`. Automatic becomes manual (the moved route is kept as waypoints), manual keeps its waypoints, frozen stays frozen with the moved route. The two runs that leave a port are refused with reason `segment_fixed`; locked elements and layers are refused by the usual edit check. DrawScript: `move-segment c segment=1 by=10pt` or `to=120pt`.
- Canvas: `components/SegmentHandles.tsx` draws a midpoint handle on each inner run of the selected connector, with a 16 px hit area at any zoom. The dashed preview is computed by the same function the command runs. One drag is one gesture and one transaction; Escape cancels. Snapping (`layout/segmentSnap.ts`): grid, centre-lines of the joined shapes, guides and other connectors' parallel runs, within 8 screen pixels, held until 1.5 times that distance. Alt turns it off.
- Keyboard: arrow keys on a focused handle nudge 1 pt, Shift nudges one grid step (10 pt without a grid). Inspector: a position field per run in the document unit.
- Jogs: already provided by R19/R20. A straight route whose ends fall out of line gets one jog, and aligning the ends again returns the two-point route (tested).

## Not built, and why

- **Only the relevant constraint is stored.** Moving a run stores the whole route as waypoints (manual). A per-run constraint needs Guided mode, which needs the planner to honour constraints (stage 3).
- Pin/unpin a run, context-menu entries, add/remove waypoint on a run.
- Live follow of labels, pinned shapes (R13) and joined branches (R16) during an end-shape drag. They still settle on release.
- A blocked drag (lock) is reported when the drag is released; the preview turns red while it is blocked, but the reason is not shown until release.
- Drag latency budget on the 60/60 stress case is not measured.
- Manual check by the owner: NOT RUN.
