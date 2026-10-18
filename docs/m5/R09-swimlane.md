# R09: swim-lane structure

Issue #4. Evidence: `artifacts/r09-swimlane/` (preview, editor render, layout report, `swimlane.vsdx`).

## What was added
- **`swimlane` layout component** (the fifth template of `list_layout_components` / `get_layout_component`). Content: `title`, `lanes` (1 to 10, each `title`, optional `heightPt` 30 to 600 and `colour` #rrggbb), optional `phases` (0 to 8 columns), `laneHeaderWidthPt` (default 96). Layout: `widthPt` (default 760). It makes one group of ordinary shapes and text: frame, title bar, a corner cell and phase header row, a band and a coloured header cell per lane, 1 pt column rules, and centred labels. Everything keeps a stable id and role (`lane.N.band`, `lane.N.header`, `lane.N.title`, `phase.N.header`, `phase.N.title`, `phase.N.rule`), so it is editable like any other drawing, reflows to a new width with the same ids and keeps text a person typed, and undoes in one step.
- **Reported membership.** `get_document_summary` gives every element that is not part of the swim-lane a `lane: { swimlane, lane, phase }` (decided by the element's middle; a point in the header column has a lane but no phase) and lists each swim-lane's lanes and phases with their bounds. Lanes are read from the bands, so resizing or moving a band by ordinary edits changes membership.
- **Inspection understands containment.** Structure parts carry `swimlane.structure`; they are not reported as OVERLAP or CONNECTOR_CROSSES_SHAPE, are not routing obstacles, and the 1 pt rules are not ZERO_SIZE. Genuine collisions between content shapes are still reported. New info-level `STRADDLES_LANES` names an element that spills over a lane boundary.
- Tool text mentions the new template and the membership report.

## Verification
- Unit: `swimlane.test.ts` (10) plus the component suites now cover five templates.
- Windows (real MCP server): build the PSGL shape (five lanes, three day columns), preview changes nothing, shapes report the right lane and phase, inspection shows only the two real collisions, reflow then undo restores the pages exactly, export to `.vsdx`, read back, membership and reflow still work.

## Not done, and why
- The `.vsdx` is the swim-lane as ordinary shapes in a group. Visio's own cross-functional swim-lane list (native containers that move and resize lanes together) is not produced; that needs the container work in R17.
- Lane titles are horizontal (vertical lane titles are R14). Resizing one lane does not move the lanes below it automatically; the component's reflow can only change width.
- Membership is by the middle of an element; connectors have none.

## Results
Unit 360, browser 95, Windows 68, MCP 14, Visio 126: all pass.
