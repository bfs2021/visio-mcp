# W03 verification evidence

See [W03 results and limits](../../docs/m5/W03-new-document.md) and the [approved design](../../docs/design/W03-new-document.md).

- `new-picker.png`: real WebView2 UI showing the starting-page dialog over an existing saved drawing.
- `ui-created.json`, `verification.json`: fresh real page-to-host creation, empty untitled drawing, independent identity and cleared save path.
- `final-test-results/`: complete combined-tree .NET results.
- Other TRX files are intermediate development runs, including deliberate negative controls. They are not the final suite result: `restore-marker-red.trx` reproduces loss after recovery and a second restart; `late-save-red.trx` reproduces an in-flight Save conflicting with New. `w03-host.trx` records the disposed-WebView shutdown crash. Later fixed runs supersede these failures. The temporarily named `restore-marker-green.trx` also failed because the restored source timestamp did not force recompilation; the source was touched and `w03-new-regressions-fixed.trx` then passed all three scenarios.

Product verification is automated on this Windows/WebView2/Office installation. R09 starters and the W02b visible tab shell are outside this completed W03 fallback flow.

Final results: 341 frontend, 63 browser and 353 .NET tests passed; a final separate New/Close Windows run passed 10 tests. Typecheck, generated-contract checks and production build passed. See `suite-summary.json` for the tested code commit and result-file mapping.

The `merge-results` folder records integration with W02b–W02e and R05. `combined-frontend.txt` and `combined-browser.txt` are the final frontend runs (348 and 95 passed). Earlier partial full-suite files are superseded: one run overlapped a main integration and failed compilation; the next was stopped after the pre-fix shutdown cleanup failures were confirmed. `shutdown-fixed.trx` includes a test-setup workspace-file lock, addressed by waiting for the clean-exit write. Final results are the `shutdown-final.trx` targeted run and `w03-combined-final` full suite.
