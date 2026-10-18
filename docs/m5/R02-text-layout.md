# R02: text measurement and fitting

Completed 2026-10-18 (Europe/London), after approval of the bounded read-only tool design.

## Delivered behavior

`get_text_layout(documentId, sessionId, ids, strategy?, target?, minFontSizePt?, pageId?)`
reads a committed snapshot and returns its document/session identity and revision,
per-object measurements and warnings, and ordinary `operations` to apply separately.
It never edits the document or automatically retries a conflicting proposal.

- `strategy: "grow_box"` (default) preserves font size and grows dimensions as needed.
  Wrapped text keeps its width unless a word needs more room; height is recomputed.
- `strategy: "shrink_font"` preserves the box, never increases font size and respects
  `minFontSizePt` (default 8). If that floor cannot fit, no edit is proposed.
- Requests support 1–50 text objects or shapes with text, deduplicate repeated IDs,
  resolve aliases only with page scope, and use UUIDs in suggested operations.
- Objects locked directly, by layer or by ancestor group receive no edit proposal.
- Growth beyond the page is flagged; growth can overlap neighbours, so render/inspect
  before relying on a proposal. Images, groups and connector labels are unsupported.
- Text advances come from editor fonts. Line height is estimated at 1.2 em. Measurements
  share their implementation with `inspect_layout`; no synthetic font context is used
  when the real editor context is unavailable.
- Visio mode is explicitly conservative, not a native renderer or a guarantee: at least
  one em per Unicode symbol, then 10% extra advance/line height and 2pt extra inset per
  side. Symbol fallback and nowrap export risks are reported. Line counts follow the
  editor's wrap setting; actual Visio wrapping can differ.
- Nonbreaking spaces stay unbroken. Empty labels require no change. Measurements have
  bounded work (131,072 selected characters and an 8,000,000 measured-character budget),
  finite output checks and a linear measured-character path for wide wrapped lines.
- Text-only measurement does not wait for unrelated image assets to load.

Apply `operations` using `apply_operations`, the returned `revision` as `baseRevision`,
and a new transaction UUID. Remeasure after a revision/session conflict. Suggested
operations use existing `resize` and `set` contracts, so schema discovery remains valid.

## Evidence and verification

- Test-first checks reproduced the missing module, unknown MCP tool and missing bridge
  route before implementation. Regression checks covered empty labels, NBSP, excessive
  measurement work, nonfinite geometry and the observed Visio symbol-width discrepancy.
- Typecheck and production frontend build passed; 267 frontend tests passed.
- All 13 MCP tests passed, including forwarding of scope, target, strategy and minimum
  font size without inventing transaction identity; malformed empty selections are refused.
- All 24 Windows/WebView2 tests passed (OfficeAcceptanceTests excluded from this run).
  New tests cover measured proposals, read-only behavior, applying a fit, stale revision
  refusal, and preserving unrelated objects.
- The original light-mode reference was promoted to
  `tests/fixtures/diagrams/repository-deployment-light.operations.json`. The acceptance
  test builds 62 elements, fits just the two arrow labels through MCP, verifies every
  other object is byte-identical, renders, and exports an editable Visio file.
- The first 10% allowance alone failed in real Visio: two 24pt arrows measured about
  24pt in the browser but 48.88pt through Visio's TEXTWIDTH function. This motivated
  the documented Unicode-symbol floor and regression test.
- For the narrow reference gutters, shrink_font proposes 11.822pt arrows without
  changing box positions. Real Visio reports 24.08pt required width inside each 30pt
  box. Its render shows the arrows side-by-side, with 62 shapes and 39 text blocks.
- Read-only review found three edge cases (NBSP, work budget, numeric overflow), all
  corrected and re-reviewed. The final symbol-policy delta had no actionable findings.

Reproduce native verification after the reference Windows test with
`pwsh -File tools/verify-reference-text-fit.ps1`. Evidence is under
`artifacts/r02-text-fit/` (`proposal.json`, `editor.png`, `reference-text-fit.vsdx`,
`visio.png`, `visio-validation.json`). This does not claim general native font parity.

## Installed update

Published the tested app to
`%LOCALAPPDATA%/Programs/AgenticDiagram/versions/r02-text-layout` and updated the
Agentic Diagram Start menu shortcut. The MCP companion remains at its existing
`%LOCALAPPDATA%/Programs/AgenticDiagram/mcp` location. The installed app DLL hash
matches the tested release. All 29 published operation schemas still compile and
validate their examples offline through the installed companion.

The previously running app and its open diagram were left intact. Save that work,
close the old window, reopen via the Start menu and reconnect MCP clients to use
the updated tool with the new editor. R03/R04 and the rest of R05 remain open.
