# G1 — Editable native-file round trip (I01)

**Result: PASS, with the backend caveat below.** Every probed check passes in real Visio and Word. Several only pass because of post-save patches over OfficeIMO.Visio 3.4.4.
OfficeIMO's own connector, group and page-size output is not usable as is.

## Environment
- Windows 11 Enterprise 10.0.26300; Visio (Microsoft 365 Plan 2) and Word (Microsoft 365) desktop
- .NET SDK 10.0.402, Node 24.14.1, WebView2 runtime 155.0.4283.60
- OfficeIMO.Visio 3.4.4 (pinned, `spikes/vsdx/packages.lock.json`), System.IO.Hashing 10.0.0
- Manual checks: 2026-10-16. Visio screenshots were supplied in the session, not stored here.

## Files
| File | Meaning |
|---|---|
| `generated.vsdx` | Current probe output (`dotnet run --project spikes/vsdx -- --output ...`) |
| `visio-saved.vsdx` | v1: first Visio save. **Failure evidence**: glue dropped, UUID truncated |
| `visio-saved-v2.vsdx` | glue kept, UUID kept, but connector zig-zags (fixed geometry) |
| `visio-saved-v3.vsdx` | straight connector OK; page reset to A4 portrait, page auto-resize moved the canvas |
| `visio-saved-v4.vsdx` | page and canvas stable; moves/rotation done by hand |
| `visio-saved-v5.vsdx` | adds PNG picture; later re-saved with a native JPEG picture for comparison |
| `visio-saved-v6.vsdx` | metric page (`U=MM`) and SVG source cell; all 6 tests pass against it |
| `visio-saved-v7.vsdx` | adds the Extras page: group resized by hand (2.0 in to 3.77 in wide), multi-layer shape, picture provenance cells; all 10 tests pass against it |
| `embedded.docx` | v4 embedded in Word, activated, saved |

## Checklist
| Check | Result |
|---|---|
| Opens in Visio without repair | PASS |
| Edit text / fill | PASS |
| Move target shape, glue follows, connector stays straight | PASS (after GluePatch) |
| `User.AgentId` visible and exact (full UUID) | PASS (after `U="STR"`) |
| Layers (A/B) and membership survive a Visio save | PASS |
| Connection points (ports) survive a Visio save | PASS |
| PNG picture opens, moves, resizes, bytes unchanged (SHA-256), AgentId kept | PASS (after ImagePatch) |
| Replace picture via Visio UI | NOT AVAILABLE in this Visio build, for native pictures too. Agents replace the media part. |
| Word: embed, activate, save, reopen | PASS (`embedded.docx` holds the vsdx and an EMF preview) |
| SVG source preservation (string user cell `SvgSource`) | PASS, also through a Visio save (v6) |
| Metric page: 297 x 210 mm landscape, `U=MM` kept by Visio | PASS (v6: `PageWidth V=11.69 U=MM`, scale cells kept as MM) |
| Duplicate AgentId | Library keeps duplicates; importer must enforce uniqueness (tested) |
| Group resize: children keep their proportions of the group | PASS (v7: group 2.0 to 3.77 in wide, child width 0.3 of group, `Sheet.20!Width*0.3` formulas kept) |
| Multi-layer policy: one shape in two layers | PASS (v7: `LayerMember="0;1"` kept; policy is that shapes may be in several layers) |
| Image provenance (`ImageSource`, `ImageMime`, `ImageOriginalSha256`) | PASS, bytes still match the recorded hash after a Visio save; swapping the media part is detected as a mismatch |

## What OfficeIMO.Visio 3.4.4 gets wrong (patched in the probe)
1. **Connector glue is discarded by Visio.** It writes `Connect` without `FromPart`/`ToPart`
   and no `PAR(PNT(...))` formulas. Visio drops the glue on save. Fix: `GluePatch`.
2. **Dynamic reroute zig-zag.** Literal `LineTo` points get extra vertices on every reroute.
   Fix: geometry from formulas (`MoveTo(0,0)`, `LineTo(Width,Height)`), 1-D transform with
   `GUARD` formulas as Visio writes it, `ShapeRouteStyle=2`.
3. **UUID in a user cell is read as a number** unless `U="STR"`: `1111...-...` became `11111111`.
4. **Page size** is written in mm in an inches slot (`PageWidth V=297 U=MM`); Visio resets to
   A4 portrait. Visio stores inches in `V` and uses `U` only as the display unit, so the fix
   (`GluePatch.FixPageUnits`) converts size and margin cells to inches and keeps `U=MM`, which
   gives a metric UI. OfficeIMO's reader then misreads the page size; read it from the XML.
5. **Auto page resize** is on by default and shifts the canvas when shapes move. Fix:
   `AutoResizeDrawing = false`.
6. **Group children are written with literal values**, so a Visio resize would not scale them. Fix: `GluePatch.PatchGroups` writes formulas relative to the group.
7. **No picture API.** Fix: `ImagePatch` writes a `Foreign`/`Bitmap` shape, media part and
   relationship, structured like Visio's own picture shape.

## Observations to carry forward
- When the page was written with `U=IN`, Visio rewrote `PageScale`/`DrawingScale` to `1 IN`. The probe now writes `U=MM` (metric UI); the v6 save should confirm Visio keeps it.
- Visio re-bases layer row indices (IX 1,2 → 0,1) and keeps membership consistent.
- After some moves, Visio rewrote the connector `Height` formula as a literal
  (`GUARD(-0.19685...DL)`) once, in v3. It did not recur in v4/v5.
- OfficeIMO reports a Visio-saved connector inside `Shapes`; count shapes from the XML, not the
  library's `Shapes + Connectors`.

## Backend decision (provisional)
OfficeIMO.Visio can be kept as a reader and for basic shapes, but **export of connectors,
pictures, page setup and string cells must go through our own package writer** (or patch layer
as in this probe). A production exporter should not depend on OfficeIMO's connector or page
output. Material failure to stop production work: not triggered, as every gap above has a
working, Visio-verified patch.

## Notes from the group probe
- The group has no outline of its own (OfficeIMO writes no geometry for it), so only the children are visible. Production should decide whether groups draw a frame.
- Visio kept the group's own Width/Height as the canonical bounds and recomputed child values from the formulas.
- Run `dotnet test` on a copy of a Visio-saved file while Visio still has it open; the open file is locked against .NET.
