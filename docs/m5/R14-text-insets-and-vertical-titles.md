# R14: text margins, vertical text and vertical lane titles

Issue #9. Evidence: `artifacts/r14-text-fit/` (`editor.png`, `visio.png` drawn by Visio itself, `r14.vsdx`, `verification.txt`, `editor-insets.png`).

Rotated lane titles used to need a hand-rotated frame, and per-shape margins could not be set apart from one uniform padding.

## What was added
- **Margins per side.** `TextBlock.insetsPt { top, right, bottom, left }` replaces the uniform `paddingPt` when present. Margins are in the *text's own frame*, as in Visio: for vertical text `left` is where the line starts (the bottom of the shape) and `top` is the shape's left side. Patching: a missing side keeps its value (or the uniform padding when there was no per-side set), `insetsPt: null` or a new `paddingPt` returns to the uniform margin.
- **Vertical text.** `TextBlock.orientation: "vertical"` reads bottom to top inside the unrotated shape. Wrapping flows along the shape's height; measurement (`get_text_layout`, `inspect_layout`, grow-box proposals) reports in the shape's frame, so a 40pt-wide lane header holds a title that would not fit sideways.
- **Swim-lane.** Component content `laneTitles: "vertical"` (default `horizontal`) turns every lane title to read up its header. It refuses, with the usual explanation, a title that cannot fit the lane height or header width without changing its font. Reflow keeps explicit text, font sizes and orientation.
- **Inspector.** "Vertical" checkbox and four "Margin ... pt" fields on any shape, text box or connector label.
- **DrawScript.** `textDir=vertical|horizontal`, `insetTop= insetRight= insetBottom= insetLeft=` (on `add` and `set`).
- **Visio.** Margins are written as `TopMargin/BottomMargin/LeftMargin/RightMargin`; vertical text as a quarter-turned text block (`TxtAngle` 90 degrees, `TxtWidth`/`TxtHeight` the shape's height/width, pinned at the middle). Both read back (a text angle of 90 degrees with no other change is vertical; margins that differ become `insetsPt`).

## Verification
- Unit `textInsets.test.ts` (7): per-side measurement, vertical flow and frame, text-frame margins, patch merging and clearing, DrawScript, swim-lane titles vertical and refused when too long.
- Browser `textInsets.spec.ts` (1): vertical text drawn taller than wide, a left margin offsets the text, the Inspector toggles Vertical and sets a margin.
- Visio `TextInsetsTests` (2): margins and orientation round trip; the text block is the shape turned.
- Windows with real Visio `VerticalTitleVisioTests`: a swim-lane with 40pt lane headers and vertical titles made through MCP; the editor's text measurement (`get_text_layout`, Visio target) reports every text fits; Visio opens the file and its own `TEXTWIDTH`/`TEXTHEIGHT` of each title fit inside the turned text block (`verification.txt`: 81.1x14.4 in 114.0x24.0, 49.7x14.4 in 74.0x24.0, 51.9x14.4 in 74.0x24.0 pt); `editor.png` and `visio.png` show the same layout.

## Limitations
- Only bottom-to-top text is offered (the usual lane title); top-to-bottom (a text angle of 270 degrees) is not written or read.
- Vertical text on a connector label is not exported as turned (Visio places a label itself).
- The fit check covers the lane titles of the PSGL-shaped swim-lane; the other reference diagram has no vertical text.
- Default text size is unchanged (12pt, 4pt padding); the swim-lane component keeps its own title sizes.
