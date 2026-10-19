# W07 evidence

- `external-write-red.trx` demonstrates Save's original silent-overwrite behavior; `external-write-green.trx` verifies refusal after implementation.
- `disk-version.trx`: content fingerprint, exact-opened-byte baseline, final-guard and locked-file tests.
- `file-conflict.png`, `ui-verification.json`: real WebView2 dialog and a GUI Save as that preserves both the external original and local snapshot.
- `file-choices-final.trx`: eight real Windows conflict tests, including second external writes, stale/replayed consent, cancelled/same-path Save as, invalid reload, missing/locked files, failed new recovery checkpoint and background-tab reload.
- Other TRX files are intermediate runs; some intentionally fail or record test-setup/fixture issues and are superseded by final results. `reload-recovery-red.trx` and `choices-recovery-fixed.trx` used the wrong fault-event hook; the corrected final test uses SessionChanged and verifies the injection fired.
- `final-test-results/`, `dotnet-tests.txt`, `frontend-tests.txt`, `browser-tests.txt` and `summary.json`: final product commit 3c9e515; 411 .NET tests, 371 frontend unit tests and 104 browser tests passed; zero failures or skipped tests. Typecheck, generated-contract check and production build passed.

See [results and limitations](../../docs/m5/W07-save-conflicts.md). Automated interaction is not manual human acceptance.
