# R05 approved design

Approved in chat on 2026-10-18. Issue: https://github.com/rp2839/visio-mcp/issues/1
Branch: `codex/r05-regression`, isolated from Claude's main checkout.

Extend the existing layout-inspection and reference-regression workflows:

- Add conservative, read-only near-alignment hints for comparable visible peer shapes, with measured offsets and explicit heuristic guidance. Exclude intended containment, groups, rotated shapes, icons and thin rules. Do not auto-move objects.
- Recognise visible thin divider rectangles without suppressing genuinely tiny or unpainted objects. Preserve R12's deliberate free-line behavior.
- Strengthen the existing 62-object reference fixture with exact text, geometry, stable identity and unrelated-object-preservation checks, including native export/import.
- Add separate editor and native-Visio image baselines with documented tolerances, provenance and deliberate failure controls. Compare each renderer against its own baseline; do not imply editor/Visio pixel identity or exact reproduction of the source screenshot.
- Keep approximation/compatibility reports as explicit evidence. No canonical contract changes, router changes or new dependencies are planned.

Verification: focused diagnostic tests first, browser/MCP regression checks, native Visio inspection and full usual suites before integration. Baselines must be captured before product edits; updates are explicit and reviewed, never automatically accepted by a failing test.
