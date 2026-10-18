# R11: linear gradient fills

Issue #6. Evidence: `artifacts/r11-gradients/` (`editor.png`, `editor-render.png`, `visio.png` drawn by Visio itself, `gradients.vsdx`, the file Visio re-saved).

## What was added
- **Contract**: a `linear-gradient` paint (`angleDeg` -360 to 360, 2 to 8 `stops` each with `offset` 0 to 1, `colour`, `opacity`). 0 degrees runs left to right, 90 top to bottom (clockwise as seen on screen). Gradients are for shape fills only: strokes and page backgrounds refuse them with a message, and stops must be in order.
- **Editor and snapshots**: maxGraph only knows two colours and four directions, so its SVG canvas is patched (`canvas/gradients.ts`) to draw a real multi-stop `linearGradient` at any angle, with per-stop opacity. The angle is applied in the shape's own pixels, so 45 degrees is 45 degrees on a wide shape, as in Visio. Renders for agents (`render_page`) use the same drawing path.
- **Visio**: written as `FillGradientEnabled`, `FillGradientDir` 0 (linear), `FillGradientAngle` (radians), `RotateGradientWithShape` and a `FillGradient` section with one row per stop (colour, transparency = 1 minus opacity, position). The first stop is also kept as the plain fill colour. Read back to the same angle and stops. A Visio gradient of another kind (radial, rectangular, path) becomes its first colour as a solid fill with a `gradient_approximated` note; a gradient with no stops written (Visio's theme default) is left as the plain fill.
- **Controls**: Inspector Fill gains a Gradient button (blends the current colour to white), then a type selector, start and end colours and an angle. DrawScript: `fill="gradient 90 #ffffff #336699"`, with optional `@offset` per stop and `#rrggbbaa` transparency, for example `"gradient 0 #ff0000 #ffff0080@0.3 #0000ff"`. MCP: set `style.fill` to the paint (see `get_operation_schema`).
- **Inspection**: text contrast is judged against the worst stop behind it; a gradient counts as painted for the thin-rule check.

## Verification
- Unit: `gradient.test.ts` (7): create/change/replace whole, fills only, stop order and schema bounds, DrawScript forms and mistakes, contrast with stops.
- Browser: `gradient.spec.ts` (2): the SVG gradient vectors for 0, 90 and 45 degrees on a 200 x 60 shape, per-stop opacity, and pixels sampled from a `doc.render` PNG at both ends and the middle of each; Inspector turns a solid fill into a gradient, edits angle and colours, switches back, undo restores.
- Visio: `GradientTests` (9): five angles round trip with all stops and transparency, the cells and section written, solid fill carries none, radial import approximated with a note, fill pattern none stays none.
- Windows with real Visio: five shapes made through MCP (0, 90, 45 with three stops, a fade to transparent, 135 on an ellipse), a gradient stroke refused, exported, opened by Visio (every shape reports `FillGradientEnabled` = 1), drawn (`visio.png` matches `editor-render.png`), re-saved and read back with the same angles, stops and transparency.

## Limitations
- Linear only (radial, rectangular and path gradients are not in the contract). More than 8 stops is refused.
- Gradient strokes and gradient page backgrounds are not supported.
- Visio's own theme gradients (no explicit stops in the file) read back as plain fills.
- Multi-stop gradients are edited in the Inspector at their first and last stops only; the middle stops are kept and can be changed with operations or a script.

## Results
Unit 372, browser 99, Windows 70, host 141, MCP 14, Visio 143, contracts 24: all pass.
