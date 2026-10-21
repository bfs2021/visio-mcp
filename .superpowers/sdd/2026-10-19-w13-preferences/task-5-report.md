# W13 Task 5 — recovery checkpoint timing

## Result

`RecoveryManager.UpdateCheckpointIntervalAsync` validates 10–3600 whole seconds and queues the timer change behind existing recovery work. The existing manager, sessions, journal, counters, 50-commit trigger, save trigger and pending-edit rule remain in place. A `TimeProvider` constructor seam supplies a deterministic timer to tests while production uses system time. Shutdown blocks later enqueues and interval changes.

The host initializes the timer from the persisted preferences snapshot. After a successful `host.preferences.save`, it awaits the interval change before returning the success payload. A save gate keeps concurrent host saves and timer updates in order. Conflict and I/O failures leave the live interval alone. Once persistence succeeds, request cancellation no longer cancels the live update.

## TDD and verification

- RED: `dotnet test tests/Diagram.Host.Tests --filter Recovery --no-restore` failed compilation because `UpdateCheckpointIntervalAsync` and the `timeProvider` constructor argument did not exist.
- GREEN: the same command passed 24/24 after the core implementation; final focused run passed 25/25 after adding queued-work coverage.
- RED: `dotnet test tests/Diagram.Windows.Tests --filter PreferencesRecovery --no-restore` failed compilation because the native harness lacked a recovery clock seam.
- GREEN: native preferences recovery tests passed 3/3 after wiring the seam, including persisted startup timing, saved interval timing, a durable native drawing across shutdown/restart, stale-tag conflict, and write failure preserving the old cadence.
- Final focused native recovery run: `dotnet test tests/Diagram.Windows.Tests --filter 'FullyQualifiedName~PreferencesRecovery|FullyQualifiedName~CrashRecovery' --no-restore -- RunConfiguration.MaxCpuCount=1` passed 6/6.
- Full Host run: `dotnet test tests/Diagram.Host.Tests --no-restore` passed 172/172.
- `git diff --check` reported no whitespace errors. Build output contains existing xUnit1031 warnings in `MultiDocumentTests.cs` and CS8631 in `CrashRecoveryTests.cs`; the tested commands exited zero.

## Files and review

- `src/Diagram.Host.Core/RecoveryManager.cs`: timer injection, serialized update, shutdown enqueue guard.
- `src/Diagram.App/MainWindow.xaml.cs`: load persisted interval at startup and test clock option.
- `src/Diagram.App/HostServices.Preferences.cs`: ordered successful-save timer application.
- `tests/Diagram.Host.Tests/RecoveryTests.Interval.cs`, `RecoveryTests.cs`: controlled-time manager coverage.
- `tests/Diagram.Windows.Tests/PreferencesRecoveryTests.cs`, `BridgeTests.cs`: native save/restart/failure coverage.

Self-review: existing constructor positions remain unchanged, so current call sites and millisecond test options compile. A timer tick already queued before an update may still finish, as required. The update completion represents a successful `ITimer.Change`, not merely queue insertion. Disposal and the update share the manager gate; callbacks cannot enqueue once shutdown begins. Persistence failure does not call the timer update. The native test verifies recovery data and timing through the real host/renderer, but it is automated evidence, not fresh manual acceptance. No full Windows suite, browser suite, main merge, or push was performed in this Task 5 handoff.

## Review fix round 1 — controlled shutdown race

Added `InFlightCheckpointAndQueuedUpdateSettleWhenDisposeBegins`: a `TaskCompletionSource` holds the real snapshot reply while a checkpoint is in flight; an interval update waits behind it; fake timer disposal is held after shutdown starts. The test advances the fake clock, verifies a new drain cannot enqueue, releases both gates, and confirms the queued update settles with `ObjectDisposedException`, disposal finishes, revision 1 remains durable, and later clock advances do not cause another snapshot or checkpoint. There are no sleeps.

The current implementation passed this supplementary test without a runtime change. To prove sensitivity, a temporary mutation that suppressed `done.TrySetException` made the test fail after its bounded 5-second wait (`TimeoutException` instead of `ObjectDisposedException`). A second temporary mutation removed the `!stopping` enqueue guard; the test failed because a drain was accepted after shutdown began. Both mutations were restored, and `git diff` showed no production changes.

Final checks after restoration:

- `dotnet test tests/Diagram.Host.Tests --filter Recovery --no-restore`: 26 passed, 0 failed.
- `dotnet test tests/Diagram.Host.Tests --no-restore`: 173 passed, 0 failed.
- `git diff --check`: no whitespace errors. Existing test analyzer warnings appeared during the first targeted run; the final two runs reused build output and exited zero.
