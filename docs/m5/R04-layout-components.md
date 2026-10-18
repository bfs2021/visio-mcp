# R04: measured reusable layout components

R04 extends the existing read-only proposal workflow with four finite templates:
`repository-card`, `labelled-container`, `environment-columns`, and a vertical
`pipeline`. Each creates an ordinary group of named editable text and vector
shapes. It installs no automatic constraints and changes no canonical document
or operation schema.

## Editor workflow

In **Shapes → Layout components**, choose a type, enter its title and content,
choose width, padding, gap, alignment and text-fitting target, then preview.
Preview changes nothing. **Apply component** uses one atomic transaction and
selects the new group. All distances in these controls are points.

For environment columns, each content line is `Title | item | item`.
For pipeline stages, each line is `Title | detail`. Cards and containers use one
item per line, with optional subtitle/footer. Icons use the pinned R03 catalogue;
MCP content can specify `iconId` for the heading and environment columns.

Edit existing text, fonts, colours and other styling using ordinary editor tools.
Select the group and use **Preview reflow** to fit that current content to a new
width or spacing. Manual resizing updates the reflow width control. Reflow keeps
part IDs, text, font sizes and styling. Alignment is preserved unless you change
it or check **Apply alignment on reflow**. Any changed document revision makes
an old preview inapplicable; preview again before applying.

## MCP workflow

1. `get_document_summary` supplies document/session identity and page IDs.
2. `list_layout_components(documentId, sessionId)` returns a versioned catalogue,
   self-contained per-template content schemas, layout schema, examples and limits.
3. `get_layout_component(documentId, sessionId, pageId, templateId?, content?,
   layout?, componentId?, x?, y?)` returns a PNG preview, named `parts` role-to-UUID
   map, group ID, bounds, warnings, identity/baseRevision and ordinary operations.
4. Apply those operations using `apply_operations` and a new transaction ID.
   The original proposal may conflict after human/agent edits; regenerate it.

Creation uses `templateId`; omitted `content` uses the complete catalogue example.
Supplied content for environment columns/pipelines requires `columns`/`steps`.
Reflow uses `componentId` and excludes `templateId`/`content`: it reads current
text/styles from the group rather than overwriting them with saved text copies.
Its x/y default to the current frame position, width to the current frame width,
and other layout controls to stored settings. Explicit x/y relocate the layout.

Example creation parameters (add the identity from the summary):

```json
{
  "pageId": "<page UUID>",
  "templateId": "repository-card",
  "x": 32,
  "y": 32,
  "content": {
    "title": "Infrastructure repository",
    "subtitle": "Owned by Sensical",
    "items": ["Bicep modules", "Environment parameters", "Release manifests"],
    "footer": "Validate → Plan → Deploy → Verify",
    "iconId": "lucide:git-branch"
  },
  "layout": { "widthPt": 420, "paddingPt": 16, "gapPt": 10, "target": "visio" }
}
```

## Fit, preservation and limits

Width is 120–2000pt; padding/gap are 0–100pt. Alignment is left/center/right.
The default target is `visio`, using R02's conservative font-advance/line-height
estimate; `editor` uses browser font advances. Fonts never shrink during reflow.
An unbreakable word or nowrap text that cannot fit is refused, with guidance to
increase width. Heights grow to fit measured content. Page overflow is reported,
and no neighbouring objects are automatically moved.

Each string is at most 512 characters; total content/current text is limited to
16,384 characters, parts to 120, height to 10,000pt and measurement work to
1,000,000 characters. Cards/containers have at most 8 items; columns have 1–6
columns with at most 8 items each; pipelines have 1–10 stages. The bounded schemas
and errors make these limits discoverable.

Group metadata stores template/version, layout settings and a text-free recipe.
Child `layout.role` metadata supplies stable semantic names. Duplication remains
independent because no original child IDs are stored in the recipe. Unknown
versions, missing/duplicate/retyped parts, changed membership, locks (including
layers/ancestors) and rotated structures are refused. Ungrouped/damaged structures
need manual editing or creation of a replacement component. Reflow does not add
or remove slots, rebuild damaged components or replace custom icon geometry.

Plain SVG text now projects the same measured word breaks used by text fitting;
maxGraph's native plain-text SVG path otherwise ignored wrapping. Original text
and editing values remain unchanged. This also fixes wrapping in PNG/SVG exports
without HTML/foreignObject labels. The existing text-measurement tests still pass.

## Verification and evidence

`components.test.ts` covers all templates, atomic undo, immutable planning,
measured reflow, current text/style preservation, stable IDs, independent copies,
locks, damaged/rotated structures, limits and content-schema parity.
`componentBridge.test.ts` checks discovery, isolated read-only previews and font
context requirements. Browser tests exercise preview/apply/reflow/undo, actual SVG
line widths, edited alignment, stale revisions and manual-resize width controls.

`LayoutComponentTests` runs a real MCP client, shim, pipe and WPF/WebView2 editor:
it creates all four templates, exports native VSDX, preserves all text on import
and obtains valid reflow proposals for all four imported groups.
`tools/verify-layout-components.ps1` opens an independent invisible native Visio
instance and checks four editable groups, part dimensions, exact text, and native
`TEXTHEIGHT` fit for all 32 text boxes. Evidence is in `artifacts/r04-components/`.

Reproduce from the root with Node 24, .NET 10 and PowerShell 7:

```powershell
npm --prefix src/web test
npm --prefix src/web run typecheck
npm --prefix src/web run generate:check
npm --prefix src/web run build
npx --prefix src/web playwright test --config src/web/playwright.config.ts
dotnet test Diagram.Windows.slnx
./tools/verify-layout-components.ps1
```

Verified 2026-10-18: **297 frontend tests, 46 browser tests and 329 .NET tests pass**, plus typecheck, production build and generated-contract checks. Native Visio confirmed all four groups and all 32 text boxes fit. See `artifacts/r04-components/verification.json` and `visio-validation.json`.

R04 is developed on `codex/r04-layout-components`, based on merged R03 at
`ebc332b`. Deployment and app/MCP reconnection are separate from development
acceptance; the installed version is not changed. R05's broader semantic and
image regression coverage and the remaining I52 acceptance work remain separate.
