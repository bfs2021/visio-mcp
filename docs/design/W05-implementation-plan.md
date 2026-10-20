# W05 implementation plan

Approved design: W05-recent-files.md. Implement on codex/w05-recent-files from e34999e.

1. Red/green bounded atomic host MRU store tests: order, ten entries, stable IDs, corruption, removal, last folder, failure preservation.
2. Integrate only successful native Open/Save/import outcomes; recent entry IDs resolve host-side; use remembered folder in Open dialogs.
3. Add File > Recent dialog through existing tab flow, keyboard focus/error handling and preservation browser checks.
4. Native Windows success/cancel/failure/persistence tests, complete suites, independent read-only review and evidence.
5. Push separate PR and update #27/Project. Leave jump list unchecked pending W15 #37: current fixed first-instance MCP pipe prevents safe second-process activation.

W07 overlap: add history only after successful saves; eventual conflict-aware save path must retain that success-only hook. No startup/IPC or public contract edits.
