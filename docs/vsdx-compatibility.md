# Visio (.vsdx) compatibility

What the native Visio reader and writer carry across, and what they tell you when they cannot. The code is `src/Diagram.Visio`. Every row names the diagnostic you will see; rows marked **unproven** have unit tests only and were not checked in a real Visio.

How to read the status column:
- **Exact**: written as real Visio cells and read back unchanged. Proven in Visio (16.0 build 20430, see `docs/m4/evidence/I40/visio-com.json`) unless marked unproven.
- **Approximate**: written, but Visio cannot hold it exactly. A warning diagnostic is produced; an export needs the person to agree first (`compatibility_loss`, or `acceptLoss` from the host).
- **Not carried**: left out, with a warning.

Units: the editor uses points (1/72 in), top-left origin, y down, rotation clockwise. Visio uses inches, bottom-left origin, y up, angle counter-clockwise. Both conversions happen at the file boundary only.

## Export (canvas to .vsdx)

| Canvas feature | Visio | Status | Diagnostic |
|---|---|---|---|
| Rectangle, rounded rectangle, ellipse, diamond, triangle, hexagon, parallelogram, callout | Geometry section written as formulas of Width and Height, so the shape resizes in Visio | Exact | |
| Cylinder, cloud | Geometry written, shape differs slightly from the canvas | Approximate | `preset_approximated` |
| Custom SVG path (M L H V Z C Q) | Geometry rows; curves become short straight lines | Approximate | `curves_flattened` |
| Custom path that is too long to keep for round trip | Geometry kept, source path dropped | Approximate | `svg_path_not_exported` |
| Fill, line colour, width, dash pattern | Fill and line cells | Exact | |
| Line join | Not mapped | Not carried | `line_join_not_mapped` |
| Text (runs, size, colour, bold, italic, alignment) | Real Text, Character and Paragraph sections | Exact | |
| Text wrapping mode | Not mapped | Not carried | `text_wrap_not_mapped` |
| Characters XML cannot hold (control characters) | Removed from the text | Approximate | `text_characters_removed` |
| Rotation | Angle cell | Exact | |
| PNG, JPEG, GIF, BMP pictures | Foreign shape with the bitmap as a media part; fit and frame kept | Exact | |
| SVG pictures | Rasterised by the editor when it is available, otherwise skipped | Approximate / Not carried | `svg_picture_not_exported`, `svg_source_not_carried` |
| Picture opacity | Not mapped | Not carried | `image_opacity_not_mapped` |
| Connector, straight or elbow | 1-D shape with GUARD formulas | Exact | |
| Connector curved | Exported as a polyline | Approximate | `curve_exported_as_polyline` |
| Connector waypoints | Kept for round trip only; Visio re-routes | Not carried | `waypoints_dropped` |
| Glue to a shape (dynamic) | `_WALKGLUE` / `_XFTRIGGER`; follows moves in Visio | Exact | |
| Glue to a named port | Falls back to dynamic glue | Approximate | `port_glue_dropped` |
| Glue to a missing target | Connector left unglued | Not carried | `glue_target_missing` |
| Groups | Group shape; resizing in Visio scales members | Exact | |
| Stacking order of grouped members | Flattened to one order | Approximate | `group_stacking_flattened` |
| Layers: name, visible, locked, no-print | Real Visio layers | Exact | |
| Guides | Guide shapes | Exact | |
| Page size, orientation, grid | Page sheet | Exact | |
| Page background colour | Not mapped | Not carried | `page_background_not_mapped` |
| Assets not placed on any page | Not exported | Not carried | `unused_asset_not_exported` |
| Identity, alias, name, metadata, locked, hidden | `Agent*` user cells | Exact | |

Every export is read back before it is saved: the same pages, element ids and connector targets must come out, or nothing is written. The result is reproducible (fixed timestamps, stable ordering). Exporting never marks the document saved.

## Import (.vsdx to canvas)

| In the file | Result | Diagnostic |
|---|---|---|
| Files made by this application, or saved again by Visio | Identity, geometry, layers, glue and pictures restored | |
| Other simple shapes, text, 1-D connectors, groups, layers, pictures | Imported as editable objects | |
| Shape dropped from a Visio stencil (master) | Size, outline and ports taken from the master and imported as an ordinary shape; the link to the master is not kept | `master_inlined` (info, once per master) |
| Shape with no outline, or whose master cannot be found | A placeholder rectangle at the right position, original name kept as metadata | `unsupported_shape` |
| Outline converted to a polyline or path | Imported as a custom shape | `outline_converted` |
| Picture that is not a supported bitmap | Replaced by a labelled placeholder | `picture_replaced` |
| Picture whose bytes no longer match the recorded hash | The new bytes are used and the change reported | `picture_moved` |
| Missing, invalid or copied ids | Fresh ids made from the file bytes and position, the same every time; counted and explained in the identity report (not a diagnostic) | |
| Repeated alias | Later shape loses the alias | `alias_repeated` |
| Layers | Matched by name | |
| Macros and embedded objects | Never read or run; listed only | `inert_part_ignored` |

**Proven in real Visio (I41):** the app's own export edited in Visio, a Visio duplicate, a resized group, multi-layer shapes, Word embedding, and plain stencil rectangles (`docs/acceptance/WA-results-2026-10-17.md`).

**Unproven:** files from tools other than this application and Visio, complex stencil masters (groups, connectors, theme colours), 3-D, shadows, data graphics, and background pages. Anything with no outline falls to the placeholder row above.

## Refusals (file is never half-opened)

The package guard refuses with a structured error: more than 64 MiB, more than 2000 parts, over 256 MiB expanded, an excessive compression ratio, names with `..`, absolute paths, backslashes, colons or duplicates, XML with a DTD (no external entities are ever read), relationships that point outside the package, missing document part, damaged or empty zips. Fixtures: `tests/fixtures/vsdx/hostile`.

Opening replaces the document only if the editor is still where the open began, and every picture is stored durably first.
