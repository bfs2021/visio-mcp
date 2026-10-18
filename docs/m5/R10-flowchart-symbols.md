# R10: flowchart symbols with native Visio geometry and ports

Issue #5. Evidence: `artifacts/r10-flowchart/` (`editor.png`, `visio.png` drawn by Visio itself, `symbols.vsdx`, the file Visio re-saved).

## What was added
- **Four presets**: `terminator`, `stored-data`, `document`, `lined-document`, in the document and operation schemas (so MCP `createShape`, DrawScript, the palette and the inspector all accept them). Process is the existing `rectangle` and decision the existing `diamond`; DrawScript also accepts the names `process`, `terminator`/`startend`, `storedData`, `document`, `linedDocument`.
- **Outlines are circular arcs**, not flattened paths. `src/web/src/geometry/presetOutlines.ts` defines each outline as moves, lines and arcs in the shape's own width and height; the editor draws them with real arcs and meets connectors on the outline. A terminator's round ends are half the shorter side whatever the stretch.
- **Native Visio geometry.** `GeometryMap.cs` has the same outlines; the exporter writes `ArcTo` rows (end point plus the bulge `A`) with formulas in `Width`, `Height` and `MIN(Width,Height)`, so stretching the shape in Visio keeps it right. The lined document's line is its own open geometry section. The importer keeps arcs as arcs and recognises a preset by comparing the outline at the shape's own size; an outline redrawn in Visio becomes a custom path with an `outline_converted` note instead of being mistaken for the preset. No `curves_flattened` or `preset_approximated` notes are raised for these symbols.
- **Ports.** A new shape of each preset starts with north, east, south and west connection points on its own outline (for example a document's south point is on the wave's midpoint line, a stored-data west point is in its concave edge). They are ordinary canonical ports, written to Visio as Connection rows and read back.
- **Palette**: Flowchart gains Terminator, Stored data, Document and Lined document.

## Verification
- Unit: `presetOutlines.test.ts` (arc direction and centre, outline membership at known points, ports on the outline, new shapes get ports, explicit ports win). Browser: `flowchart.spec.ts`. Visio: `FlowchartGeometryTests` (round trip at four shapes, native `ArcTo` with size formulas, open section, redrawn outline not mistaken, arc flattening).
- Windows with real Visio: eight symbols built through MCP, exported, drawn by Visio (`visio.png` matches `editor.png`), saved by Visio, read back as the same presets with four ports each.

## Limitations
- Process and decision remain the rectangle and diamond (side ports are implied for routing; they are not written as Visio connection points unless a shape has explicit ports).
- At extreme aspect ratios the outlines distort (a very tall terminator becomes a lens, a tall document has a deep wave).
- A lined document's line is a vertical rule near the left edge; the original diagram's exact variant could not be checked because its file is not in the repository.
- Visio's own stencil masters are not used: the shapes are drawn geometry, as everything else is.

## Results
Unit 365, browser 97, Windows 69, host 141, MCP 14, Visio 134, contracts 24: all pass.
