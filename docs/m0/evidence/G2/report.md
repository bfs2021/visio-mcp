# G2 — Canvas interaction and image preview (I02)

**Result: PASS.** `npm --prefix spikes/canvas run test:browser`: 3 of 3 pass (Chromium 1243 via Playwright 1.63.0, headless, Windows 11).

## Versions
@maxgraph/core 0.24.0, vite 8.3.2, typescript 7.0.2, @playwright/test 1.63.0 (all pinned, `spikes/canvas/package-lock.json`).
maxGraph 0.25.0 exists; not evaluated, stay on the pinned version for M0.

## What was proven
| Check | Result |
|---|---|
| Move, resize (SE handle) and rotate (rotation handle) by real mouse drags; each gesture bumps the revision by exactly 1 | PASS |
| Connector keeps the same source/target element IDs and port constraints through move/resize/rotate | PASS |
| Untouched element (`ellipse`) is deep-equal before and after edits to another element | PASS |
| Picture bytes round-trip unchanged through the canvas (DTO to canvas to DTO) | PASS |
| Gesture hook: `begin` then `commit` for move/resize/rotate/bend, and `begin` then `cancel` (Escape mid-drag) with state restored exactly, revision unchanged | PASS |
| In-place text edit: `begin`/`commit` and `begin`/`cancel` | PASS |
| Selection and zoom emit no gesture events | PASS |
| Export renders the whole scene offscreen (SVG via maxGraph `ImageExport`, rasterised to PNG), sized by scene bounds not viewport (checked at 4x zoom) | PASS |
| PNG picture and SVG vector asset both draw to a canvas and read back without taint (assets are `data:` URIs) | PASS |
| DTO is plain data; no maxGraph cell objects cross `project`/`readProbeState` | PASS |

## Findings to carry forward
1. **maxGraph 0.24.0 defect:** `VertexHandler.reset()` reads `sizers[index].node`, but the rotation handle (index -2) has no sizer. Pressing Escape during a rotate drag throws a `TypeError` before the handler is cleaned up. The probe works around it by giving `reset()` a stand-in sizer for the duration of the call. The production canvas needs the same patch (or a newer maxGraph that fixes it).
2. maxGraph has no begin/cancel gesture events. The probe derives them by wrapping `start`/`mouseUp`/`reset` on `VertexHandler`, `EdgeHandler`, `ElbowEdgeHandler` and `SelectionHandler`. Move `begin` fires on the first preview past tolerance, not on mouse-down. This depends on handler internals, so pin the version.
3. Escape is not wired by default: the probe calls `graph.escape()` from a `keydown` listener.
4. Revision is counted from model `change` events (one per outermost batch), so one gesture is one revision. `project()` sets the revision from the scene rather than bumping it.
5. The elbow connector's bend handle is a single virtual bend (`bends[1]`). Bends are stored as edge geometry points and read into `connector.bends`.
6. Rotation is style `rotation` with unrotated geometry, which matches Visio (`Angle` with unrotated size).

## Timing (informational, not a gate)
Project 9 ms. Offscreen render to PNG about 1.0 s on first call (SVG build under 1 ms; the rest is image decode and rasterisation at 2x). Slow preview alone is not a functional failure.

## Evidence files
`canvas.png` (offscreen render, scene after the test's edits), `viewport.png`, `canonical-state.json`, `timing.json`.
