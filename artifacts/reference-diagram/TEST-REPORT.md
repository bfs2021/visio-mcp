# Light-mode reference diagram functionality test

Tested 2026-10-17 against the installed release, using its stdio MCP server and live app pipe. No product code was changed. The two scripts under tools/reference-diagram-*.mjs are exploratory probe utilities.

## Result

- Created a new page without altering the existing blank page.
- One initial atomic batch of 64 operations created the page and 62 editable elements.
- Reproduced all 39 text blocks, two repository columns, action panels, release record, tenancy and three environments on a white background.
- Rendered through render_page and inspected through inspect_layout.
- Exported through export_document, opened the output in real Microsoft Visio and exported visio-render.png.
- Visio reports 62 shapes and 39 nonempty text blocks on page 2. Comparing text with whitespace removed found zero missing text blocks.
- Corrected two short icon text boxes and arrow wrapping with subsequent atomic MCP operations. Final revision: 3.
- Remaining visual differences: repository icon is a Unicode approximation; cloud uses the built-in preset and is approximated again on export. Spacing/font appearance is close rather than pixel exact.
- Final layout inspection contains five ZERO_SIZE warnings for visible 0.8-point divider rectangles and no text overflow warnings. These divider warnings are false positives in this example.
- Export diagnostics: 1 preset_approximated and 39 informational text_wrap_not_mapped entries.

## Recommended product improvements

1. **Discoverable operation contracts.** apply_operations exposes an untyped operations JSON parameter, without the full operation union to the caller. Add a schema/resource discovery tool (including geometry/text schemas, examples, units, alias scoping and limits). In this probe, set requests incorrectly using pageId or bounds were rejected atomically; reading the repository schema revealed resize plus UUID targets was required. Improve validation errors with the matching operation's allowed fields and a correction example.
2. **Export-aware text fitting.** A pair of arrows fitted side-by-side in the app but wrapped in Visio. Add text measurement/fit support, export-target padding and diagnostics for likely wrapping before export. Test rendered results in both WebView2 and Visio. Fixing this example required wider boxes and a smaller arrow font.
3. **Vector icons with consistent rendering.** Add branch/repository, code, server and cloud symbols with explicit geometry, rather than font-dependent glyphs. cellMapping.ts currently maps presets but does not render geometry.svgPath, even though the canonical model and Visio export can carry paths. Implement path rendering consistently across editor, snapshot and native export, with cross-renderer tests.
4. **Thin-rule layout diagnostics.** inspectLayout.ts marks any box dimension below 1 point as ZERO_SIZE. Use visible painted area/stroke and intended line geometry to distinguish valid thin rules from invisible objects. This example's five rules are visibly present in both renders.
5. **A reusable acceptance fixture.** Promote the reference structure and wording to a regression fixture with semantic checks (39 text blocks, stable IDs and object counts), overflow checks, image comparison with tolerances, and actual Visio export rendering. Keep icon/preset approximations explicit rather than treating identical text as visual equivalence.

## Artifacts

- light-mode.png: final app render.
- repository-deployment-light.vsdx: editable Visio file; diagram is on page 2.
- visio-render.png: final real-Visio render.
- operations.json, refinements.json, arrow-refinements.json: probe requests.
- inspect_layout.json and export_document.json: final tool diagnostics.
- visio-text.json and visio-validation.json: native validation evidence.

The test covers creation, incremental repair, render, inspection and export. It does not establish save/reopen parity in the app, connector glue behavior, or general reference-image understanding without an agent planning the layout.
