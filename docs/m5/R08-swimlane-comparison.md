# Swim-lane recreation: PSGL Dataflow

Recorded 2026-10-18. The reference was a screenshot of `Swim-lane_flow_diag.vsdx`; the
original file's shape positions, text, colours and connections were read from its XML
to place objects exactly. The diagram was built through the real MCP server (a Node client
running `apply_operations`, `render_page`, `inspect_layout`, `export_document`) against the
installed editor. Files are in `artifacts/swimlane/`:

| File | What |
|---|---|
| `gen.py`, `ops.json` | Generator and the 54 operations applied (one transaction) |
| `run.mjs` | MCP client script |
| `mine.png` | Editor render |
| `original-visio.png`, `mine-visio.png` | Both files rendered by Visio itself, same size |

## Result
All 5 lanes, 3 day columns, title bar, 13 shapes, 11 connectors, 4 numbered markers and the
dashed Day 2 line were reproduced as editable objects. Positions, sizes, fills, text and
connections match the original closely in the editor.

## Differences against the original in Visio
1. **Connector routing (largest).** The editor's orthogonal route ignores port side and
   waypoints. Straight routes with explicit waypoints looked right in the editor, but Visio
   dropped the waypoints (`waypoints_dropped`, 3 connectors), so those draw as diagonals.
2. Flat fills; the original uses gradients.
3. Stored-data and lined-document shapes were hand-authored paths; Visio flattens curves.
4. Arrowheads are larger than the original's.
5. The Day 2 dashed line and column rules were unglued connectors (8 UNGLUED_CONNECTOR_END).
6. Lane labels wrap differently ("Corona / Sensical" is one line here, two in the original).
7. `inspect_layout`: 33 issues, mostly false positives from lanes, strips and the frame
   (15 CONNECTOR_CROSSES_SHAPE, 4 OVERLAP, 4 TEXT_OVERFLOW, 2 LOW_CONTRAST).

Suggested features are tracked as R08–R14 in `IMPLEMENTATION_PROGRESS.md`.
