# Lego Infrastructure recreation

Recorded 2026-10-18. Reference: screenshot of `Lego Infrastructure - v2.vsdx`. The file's XML
was flattened (group offsets, rotation, local geometry, layers, character and fill data) and
converted to 172 operations applied in one transaction through the real MCP server
(`artifacts/lego-infra/gen.py`, `run.mjs`, `ops.json`). Both files were rendered by Visio:
`original-visio.png` and `mine-visio.png`; editor render `mine.png`.

## Result
Reproduced as editable objects: 101 shapes, 67 connectors and two layers (Connector, symbols).
Nested dashed containers with 66%-transparent fills, rotated labels at three sizes and in both
directions, polyline connectors with bends and arrowheads at either end, line hops, 13 database
symbols, a cloud and an icon. In the editor the page is near-identical to the original.

## What worked
- Rotated text (+90, -90, 180) and per-label sizes (6, 8, 11 pt) with bold.
- Layers, fill transparency, dashed outlines and long orthogonal polylines through waypoints.
- The page, page size and z-order from a single transaction.

## Differences in Visio
1. **12 straight connectors with waypoints lose them on export** (`waypoints_dropped`), so
   those lines draw as diagonals across the diagram. The editor shows them correctly. (R08)
2. Database cylinders and the Azure icon are approximations of the stencil masters. (R18)
3. Default text size is smaller than the original's (Visio's inherited size was not carried). (R14)
4. Layout inspector: 134 UNGLUED_CONNECTOR_END, 62 CONNECTOR_CROSSES_SHAPE, 33 TEXT_OVERFLOW,
   29 OVERLAP, 17 LOW_CONTRAST. Almost all come from free lines and nested containers. (R09, R12, R17)
5. No tool could read the `.vsdx`; the structure was parsed by hand. (R15)
6. Crossing hops and T-junction branches were hand-built. (R16)

Suggested features are tracked as R15–R18 in `IMPLEMENTATION_PROGRESS.md`, alongside R08–R14.
