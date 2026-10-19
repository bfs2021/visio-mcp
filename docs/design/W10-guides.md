# W10 — draggable guides and snapping

Status: approved by the user in Codex chat, 2026-10-19. Issue: https://github.com/rp2839/visio-mcp/issues/32
Branch: codex/w10-guides. Base: main e67f2e7. Implement before W11.

## Purpose

Make precise diagram alignment possible through the existing rulers and canonical page guides, without hand-entering coordinates. Guide edits must be undoable, safe during concurrent edits, and remain editable after a Visio round trip.

## Existing foundation and scope

Page.guides and Guide UUID/orientation/positionPt already exist. Native Visio import/export already handles guide shapes. Rulers, viewport conversion, grid snapping, layer snap flags, gesture admission and atomic transactions already exist. PagePatch currently cannot edit guides: add optional guides to the existing setPage patch, referencing the existing Guide definition and 256-guide bound. Regenerate contracts and expose it through existing operation discovery; no new document format, public MCP tool or router.

## Interaction

Drag the top ruler to create a horizontal guide; drag the left ruler to create a vertical guide. Existing guides have visible drag handles; moving one commits only on release. Return a guide to its corresponding ruler to delete it. Escape, pointer cancellation or release outside the page cancels without changing the document. New UI guides must land inside the page; imported off-page guides and their positions remain preserved. Zoom, scrolling and units use the existing viewport's point conversion.

Each create/move/delete produces one revision-pinned setPage transaction and one undo entry. Unchanged releases are no-ops. Changing tab, page, document/session or revision invalidates the preview; never overwrite another guide change with a stale array. During a guide gesture, use the same gesture/admission interlock as canvas edits. Guides have stable IDs; do not regenerate untouched IDs.

Add View > Show guides and Snap to guides, initially enabled per editor tab. These are editor view choices, leaving W13 preference persistence separate. Hidden guides do not attract snapping. Guides are editor overlays: clean PNG/SVG/PDF exports contain no ruler/guide chrome. Saving JSON and exporting native Visio still preserve the guide data.

## Snapping policy

Snap creation, movement and resizing to nearby guide-aligned edges/centres within eight screen pixels, converted to page points at the current zoom. Use deterministic nearest-distance ordering with stable position/ID tie breaks. A guide within the threshold takes precedence over grid snapping on that axis; otherwise retain existing grid behaviour. Existing layer snap=false suppresses guide corrections, including the destination layer for a newly created shape. Keep groups/multiple selections rigid; if any participating movable object forbids snapping, apply no guide correction to that selection. Do not distort individual group members, override authored connector waypoints or introduce hidden snapping in the renderer. Rotated objects use documented page-space visual bounds; test the resulting committed coordinates.

## Validation and compatibility

Reuse canonical guide validation: finite positions, valid unique IDs and the 256-item bound. Preserve unrelated page fields, elements, text, layers and source metadata. The additive setPage field must agree across JSON schema, generated TypeScript/C#, planner, recovery/undo replay and get_operation_schema. Do not silently clamp imported coordinates or change the document schema version.

Likely files: Ruler/PageCanvas and a guide overlay, EditorModel, interaction planners/GestureController integration, Toolbar View menu, operations schema/generated contracts and setPage planner. Coordinate those shared files through #32; do not edit historical IMPLEMENTATION_PROGRESS.md.

## Verification and delivery

Meaningful unit tests cover guide validation/limits, single-undo edits, stale previews, deterministic snapping, zoom thresholds, mixed selections and snap-disabled layers. Browser tests drag real rulers/guides, cancel/delete/undo, scroll/zoom, toggle visibility and verify the committed snapshot and clean exports. Native Windows/Visio checks cover save/open, guide identity/orientation/position, and edits saved by Visio and reimported. Keep automation evidence distinct from manual human acceptance.

Run frontend tests, typecheck, generated check, production build, browser suite and all Windows solution tests with bounded build workers. Retain initial failures and retries explicitly. Commit in small steps, request read-only review, push a separate PR and leave integration/manual checks explicit. W09 waits for its W08 dependency; W11 follows independently.
