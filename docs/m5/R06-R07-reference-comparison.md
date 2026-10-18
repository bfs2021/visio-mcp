# R06/R07: source crops and canvas comparison

Approved bounded design, 2026-10-18. Extends the existing approved asset library,
snapshot renderer and MCP image-block responses. No new product dependencies.

## Workflow

1. In the editor's Pictures panel choose **Add to library…** and select the source
   screenshot. This approves its bytes independently of placing it on a page.
2. Read `get_document_summary` for document/session identity and `list_assets` for
   the reference asset's ID, scope and exact lowercase SHA-256 hash.
3. Use `render_reference_region` to inspect a source crop. Source coordinates are
   integer pixels in the original raster, with top-left origin. The crop must be
   wholly inside the source; invalid or fractional crops are rejected.
4. Use `compare_reference` with the same crop and a canvas region (points), or
   selected objects (UUIDs or page-scoped aliases/names). Refine crops and inspect
   at the returned revision before making any edits through existing transactions.

Example comparison arguments (substitute actual identities and hash):

```json
{
  "documentId": "00000000-0000-4000-8000-000000000001",
  "sessionId": "00000000-0000-4000-8000-000000000002",
  "pageId": "6c1143cf-f076-4d34-840c-000000000001",
  "referenceAssetId": "asset:application-screenshot",
  "referenceSha256": "<64 lowercase hex characters from list_assets>",
  "referenceScope": "library",
  "cropPx": { "x": 582, "y": 100, "width": 32, "height": 34 },
  "elementIds": ["application-git-branch"],
  "mode": "side_by_side",
  "panelSizePx": 512,
  "paddingPt": 6
}
```

For source-only magnification, omit the canvas-specific parameters and call
`render_reference_region`; set `scale` (default 8) if desired.

## Output and guarantees

Both tools return a real MCP PNG image block and JSON metadata without duplicate
base64 text. They do not add reference images to the diagram, change objects,
increment revision or alter undo history. The host resolves approved asset IDs and
hashes; no caller-provided path is opened. Library history remains accessible by
its pinned hash while the library entry exists. Document pins work independently
of the library. Source dimensions are decoded from the pinned bytes, so replacing
the library source with a different-sized image cannot silently change the crop.

Metadata identifies document, session, committed revision and source asset/hash.
`reference` includes original dimensions, `cropPx`, `displayRectPx`, and
`sourceToOutput`. Comparison `canvas` includes the page, `cropPt`, raster size,
`rasterDisplayRectPx`, actual point-content `displayRectPx`, `renderPixelsPerPoint`
and `pointsToOutput`. Each mapping has `scale`, `translateX`, and `translateY`:

```
xOut = scale * xSource + translateX
yOut = scale * ySource + translateY
```

All output rectangles are in output pixels. Reference mapping inputs are original
source pixels; canvas mapping inputs are document points. Browser raster rounding
and its internal aspect-preserving margins are included in the canvas transform,
including thin regions. Side-by-side panels preserve each crop's aspect ratio and
centre it in equal square panels; differing crop ratios produce a note. This is
visual comparison, not automatic feature alignment. Output margins and captions
are outside the image crops.

Source pixels are explicitly sampled with nearest-neighbour lookup; WPF's drawing
sampler otherwise smoothed some CroppedBitmap pixels despite its scaling hint.
Canvas images retain their renderer's antialiasing. `overlay` draws the reference
first and the canvas at 50% opacity, and warns about theme differences. It provides
no similarity score and cannot recover detail missing from a small raster source.

## Limits and consistency

- Raster references: PNG, JPEG, BMP. SVG has no defined original pixel grid and is
  rejected explicitly by this first comparison tool. Existing asset byte/dimension
  validation runs before decoding.
- Source crop: integer x/y >= 0, width/height > 0; all four fields required.
- Source magnification: 1–16; output <=4096 pixels per side and <=16 megapixels.
- Comparison: panel size 64–2048 pixels, padding 0–2000 points (default 6 to protect
  edge strokes), one region or
  1–500 nonempty element targets. Existing renderer limits, including 8× maximum
  canvas rasterisation, still apply; magnification beyond this resamples that
  raster and does not create extra canvas detail.
- Encoded PNG <=16 MiB. One comparison/crop request is processed at a time per app.
- A snapshot validates identity and document asset pins. Comparison uses a render
  barrier at that same revision; an intervening edit returns `revision_conflict`
  instead of mixing revisions. Repeat the read with current identity/state.

## Evidence

Reusable fixtures include the editable application-release-flow operations and original
32×34 branch/container screenshot crops in `tests/fixtures/`.

The live test writes side-by-side and overlay images plus mapping metadata under
`artifacts/r07-comparison/`. It exercises the real WPF/WebView2 editor, pipe,
published tool definitions, approved library, and MCP image blocks.

- Red: live MCP test failed with `Unknown tool: render_reference_region` before implementation.
- Pixel regression: source black pixels were initially smoothed to grey by WPF;
  explicit sampling now preserves every tested pixel of an offset 16×16 crop at 4×.
- Review regression: a 1000×1-point region exposed raster letterboxing. The final
  mapping and thin-line output agree with the browser raster, without double-fitting.
- Source replacement with different dimensions retains old pinned bytes/dimensions;
  document pins work after library removal. Document snapshots remain identical
  before/after reads. Invalid hashes, scopes, missing/fractional/overflowing crops,
  conflicting canvas selectors, stale sessions and oversized output are rejected.
- Full MCP suite: 13 passed. Windows suite excluding Office acceptance: 26 passed.
  Final Release comparison checks: 2 passed. No frontend source changes were needed.
- Independent read-only review found one aspect/mapping issue; fixed and re-reviewed
  with no further actionable findings. Generated branch/container comparisons were
  visually inspected. This does not claim automatic or native Visio image matching.

## Installed update

App and companion are installed together under
`%LOCALAPPDATA%/Programs/AgenticDiagram/versions/r07-reference-comparison` (`mcp/`
contains the companion). The Agentic Diagram Start menu shortcut points to this
app. The existing Codex `diagram` MCP registration points to this companion, avoiding
replacement of binaries used by current MCP processes. Installed DLL hashes match
the published release; the installed companion discovers both new tool schemas
and all 29 existing operation schemas compile/validate offline.

The old running window was left open to preserve its four diagram pages. A VSDX
copy was exported as `Documents/AgenticDiagram/Exports/diagram-reproductions-before-r07-update.vsdx`.
Save work, close that window and reopen from the Start menu; reconnect the diagram
MCP server (or restart Codex) to load the new tools. Raster screenshot references
must be approved through **Pictures → Add to library…** before use.
