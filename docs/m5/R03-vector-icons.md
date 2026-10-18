# R03: searchable editable vector icons

The editor includes 26 bundled Lucide icons pinned to version 1.59.0, upstream
commit `1ef60af944de207e0d5176905a0759ba12e6ee8d`, and catalogue version
`lucide-1.59.0-normalized-1`. No network access is needed to search or place them.
Source SVGs and the upstream licence are retained in `vendor/lucide/`.

## Use in the editor

Open **Shapes → Icons**, search by name or tags (for example `branch`, `docker`,
`repository`, `security`), choose size, colour and stroke, then select an icon and
click the page. Size and stroke use points. Settings continue to update an armed
icon; invalid values cancel placement. The result is one ordinary editable shape,
with normal resize, rotation, style, copy/paste, grouping and undo behavior.

## Use through MCP

1. Call `get_document_summary` for document/session identity and a page ID.
2. Call `search_icons(documentId, sessionId, query?, limit?)` for stable IDs,
   tags, catalogue/source version, geometry hashes and a PNG preview sheet.
   The default limit is 24; use 50 to show the entire current subset.
3. Call `get_icon(documentId, sessionId, pageId, iconId, x?, y?, sizePt?,
   colour?, strokeWidthPt?, catalogVersion?, elementId?)` for a preview and
   two ordinary operations: create the vector shape and set attribution metadata.
4. Review, then pass the returned operations and baseRevision to `apply_operations`
   with the returned identity and a new transaction ID. Handle revision conflicts
   normally; discovery and previews never edit the document.
5. Use `render_region` and `compare_reference` to inspect the placed icon at a
   useful scale. The comparison tool can show a source screenshot crop beside it.

Position is the top-left in points. Icons are square, size 1–1000pt, with
`#RRGGBB` stroke colour, transparent fill, and a positive stroke no larger than
half the size. Default size is 24pt; omitted stroke is size/12. A supplied stale
catalogue version is rejected. Metadata pins the icon ID, catalogue version,
upstream commit, path hash and full licence notice; later catalogue updates
cannot silently change already placed geometry.

## Geometry and native format

The editor, preview and PNG renderer now draw `geometry.svgPath` rather than
silently substituting the shape preset. Paths use a normalised 100×100 box and
support absolute/relative M, L, H, V, C, Q and Z. Cubic/quadratic curves flatten
to 16 line segments per curve in both implementations. Unsupported commands,
missing numbers, nonfinite/excessive coordinates and excessive work are rejected
at transaction/render boundaries and identified by layout inspection. Legacy
unsupported paths are visibly marked on the canvas. Native export retains its
explicit diagnostic if an unsupported legacy outline must be replaced.

The catalogue generator converts upstream circles, curves and other primitives
to pinned M/L/Z paths, so their geometry follows the same path pipeline across
canvas, PNG and native Visio. Native exports contain real editable geometry,
not bitmap icons. Native geometry fingerprints suppress floating-point round-off
before quantisation so unedited icons retain their exact path after import.
Visio's line-join control remains unsupported and is explicitly reported as
`line_join_not_mapped`; joins can differ slightly. Curves are approximations,
not arbitrary SVG support. Custom geometry can be supplied through existing
`createShape`/`set` operations; there is no SVG upload or arbitrary icon-fetch tool.

## Reproduce verification

From the repository root, with Node 24 and .NET 10:

```powershell
npm --prefix src/web ci
npm --prefix src/web test
npm --prefix src/web run typecheck
npm --prefix src/web run generate:check
npm --prefix src/web run build
npx --prefix src/web playwright install chromium
npx --prefix src/web playwright test --config src/web/playwright.config.ts
dotnet test Diagram.Windows.slnx
./tools/verify-vector-icons.ps1 # requires installed Visio; independent invisible instance
```

`tools/generate-icons.ps1` regenerates the catalogue offline from the bundled
sources. `-Fetch` explicitly refreshes those sources from the pinned upstream
commit; updating the version requires review of sources, normalization and tests.

`IconGeometryTests` checks malformed/adjacent-decimal paths and every bundled
icon's exact geometry, identity, hash and licence across native export/import.
`VectorIconTests` uses a real MCP client, pipe, WPF window and WebView2 to check
read-only search/preview, styled placement and rendering. Browser tests cover
search, settings changed after arming, invalid settings, undo and hollow-node
pixels. Native Visio verification checks all 26 editable shapes, geometry rows,
size, stroke and caps. Evidence is written under `artifacts/r03-icons/`.

Verified on 2026-10-18: **285 frontend tests, 43 browser tests and 328 .NET tests pass**; typecheck, production build and generated-contract check pass. Offline catalogue regeneration matches the committed catalogue in the fresh Windows checkout. The installed Visio verified all 26 icons. See `artifacts/r03-icons/verification.json` and `visio-validation.json` for counts and native checks.

The development branch is based on main `18a9685` (including I51) and leaves
main untouched. Deployment/activation of this branch is separate from acceptance:
existing app/MCP processes continue to run their installed version.

## Remaining work

R03's bounded subset is delivered. More icon families and icon-specific semantic
ports can be added later. R04 reusable layout components and R05 broader tolerant
image/semantic regression coverage remain separate tracker work.
