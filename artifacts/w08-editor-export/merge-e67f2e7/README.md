# PR47 conflict-resolution verification

Main anchor e67f2e7 includes W07. Resolution retains W07 FileConflictFlow, pinned conflict-aware saves, both host routes and the optional AtomicFileWriter publication guard. The file-action gate includes export and file-conflict actions. Modal/shortcut guards retain both flows; focus conflict presentation waits while export is open. Read-only review found no substantive integration regressions.

Frontend: 399 passed. Typecheck, generated-contract check, web build and complete .NET build passed. Final browser rerun: 115 passed. All non-desktop .NET projects passed (348 tests). Desktop run: 87 passed and one failure (88 total): the copy operation succeeded and exposed PNG/bitmap formats, but Clipboard.GetImage returned null in its test. A focused rerun of all 16 export and file-conflict tests passed without source changes. Initial full coverage therefore recorded 435 passed and one failed; both results are retained.

Initial extra .NET attempt failed in MSBuild node creation with OutOfMemoryException before tests ran; its dotnet-core.txt output is retained. Retry uses one MSBuild worker and no node reuse. Initial browser run had 114 passes and one timeout in the first agentApply of the revision-snapshot test; the isolated repeat passed. The initial browser log/context are retained; the complete rerun passed all 115 tests. No source changes were made to suppress those failures.

Manual human acceptance NOT RUN. The historical PDF text-extraction limit remains documented in the main report.

Commands: dotnet test Diagram.Windows.slnx --no-build -m:1 -nr:false --filter FullyQualifiedName!~Diagram.Windows.Tests, followed by dotnet test tests/Diagram.Windows.Tests --no-build -m:1 -nr:false. The zero-test Windows TRX in the first command is deliberately excluded; the separate desktop run covers that project. A subsequent filtered run covers EditorExportTests and FileConflictTests. The transient clipboard failure remains a diagnostic limit, not a clean first run.
