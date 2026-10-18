# R12: free lines and arrowhead size

Recorded 2026-10-18. GitHub issue #7.

## Change
- **Free lines.** A connector whose two ends are both free (`glue: none`) is a deliberate line: a rule,
  divider or dashed marker. `inspect_layout` no longer raises `UNGLUED_CONNECTOR_END` for it. A connector
  with exactly one free end is still reported. No new element kind was needed.
- **Arrowhead size.** `LineStyle.startArrowSize` / `endArrowSize` (`small`, `medium`, `large`; absent means
  medium) in the document and operation schemas, regenerated TypeScript and C# contracts, the inspector
  (Start size / End size), DrawScript (`startArrowSize=`, `endArrowSize=` on `connect` and `set`), the
  maxGraph render (marker size 3/6/9) and Visio (`BeginArrowSize` / `EndArrowSize` 1/2/4, read back on import;
  medium stays implicit). Open ends use the existing `open` arrowhead.

## Evidence
| Check | Result |
|---|---|
| Layout: both ends free not reported, one free still reported | `tests/layout.test.ts` |
| Set, validate, undo; refused on a shape | `tests/structure.test.ts` (arrowhead size) |
| DrawScript fields and bad value | `tests/script.test.ts` |
| Inspector control and rendered head size in the real editor | `tests/browser/render.spec.ts` |
| Visio round trip, medium stays implicit | `StructuralTests.ArrowSizesSurviveTheRoundTripAndMediumStaysImplicit` |
| Swim-lane re-render in Visio with small heads | `artifacts/swimlane/mine-r12-visio.png` (heads now close to the original's); UNGLUED_CONNECTOR_END 8 to 0 |
| Suites | 314 unit, 49 browser, 125 Visio, 24 contract, 13 MCP, 119 host |

## Limits
Three sizes only, not Visio's seven. The dashed Day 2 line still has a connector's glue fields in the
document; it is a line by convention (both ends free) rather than a separate element.
