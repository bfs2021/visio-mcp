# R05 fresh regression evidence

See [the report](../../docs/m5/R05-regression.md) for commands, baseline policy and limits.

- `suite-summary.json`: final combined-tree run, 332 frontend / 52 browser / 335 .NET passed. Final TRX files are in `final-test-results`.
- `verification.json`: actual MCP/editor/native-Visio fixture results, 62 objects / 39 texts / 62 stable IDs, unrelated-object preservation and explicit compatibility diagnostics.
- `editor.png`, `visio.png`: clean render from each renderer, compared only against its own pinned baseline.
- `displaced-control.png`, `missing-divider-control.png`: deliberate failures; `restored.png`: clean restored output.
- `objects.json`, `layout.json`, `export.json`: semantic objects, planted 5pt near-alignment finding plus two legacy-icon overflow estimates, export approximations.
- `reference.vsdx`, `visio-saved.vsdx`: editable export and independent native save/reopen evidence.
- `demo-result.json`, `demo-alignment-findings.json`, `demo-alignment-preview.png`: fresh automated demo with all steps passing and the 11.3pt CRM/ERP offset reported by the inspector.
- `test-results`: initial/focused runs. The initial full run had one existing dynamic-glue test failure; isolated rerun and the complete final Visio suite passed. The failure is retained, not silently removed.

Automated Office evidence is not a human manual sign-off. The source diagram retains legacy icon approximations. Baseline provenance and hashes live under `tests/fixtures/diagrams/r05-baselines` and are checked by the fixture test.
