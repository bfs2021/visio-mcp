# G3 — Stdio MCP to bounded pipe and test host (I03)

**Result: PASS, with one check NOT RUN** (second-user refusal needs a second Windows account).
`dotnet test spikes/ipc.tests/IpcProbe.Tests.csproj`: 14 of 14 pass, stable over 4 consecutive runs (Windows 11, .NET 10.0.402).

## Versions
ModelContextProtocol 2.2.0 (pinned `[2.2.0]`, lockfiles in each project), Microsoft.Extensions.Hosting 10.0.0, xunit 2.9.3.

## Components
- `spikes/ipc`: `FrameCodec` (4-byte little-endian length plus UTF-8 JSON, 32 MiB cap, exact partial reads), `Envelope`, `PipeNames` (user SID + logon session + protocol version), `TestHost` (stub host), `HostClient` (lazy, one call at a time).
- `spikes/mcp`: official-SDK stdio server with one tool, `get_document_summary`, routed through `HostClient`. Stdout is MCP only; the startup line and logs go to stderr.

## Results
| Check | Result |
|---|---|
| `PartialReadRoundTrips`: one-byte reads reassemble exactly; truncated frame raises end-of-stream | PASS |
| `FrameLengthAbove32MiBRejectedBeforeAllocation`: header claiming 32 MiB + 1 gives `limit_exceeded`, payload allocations counted as 0; empty and malformed frames give `invalid_request` | PASS |
| `NonHandshakeFirstFrameRejected`: error `invalid_request`, connection closed, request never executed | PASS |
| Handshake: server assigns the client label and connection ID, does not echo caller text; wrong protocol version gives `unsupported_version` | PASS |
| `StdoutContainsOnlyMcp`: every stdout line is JSON-RPC 2.0; startup text appears only on stderr | PASS |
| `MissingHostIsNotRunning`: `not_running`, retryable; the shim returns it as a tool error and connects lazily once the host appears | PASS |
| Disconnect mid-call: call settles as `not_running` with `details.outcome=unknown`; the host saw exactly one request (no silent retry); the next call reconnects | PASS |
| 8 MiB frame round trip | PASS, 14-16 ms |
| Real stdio MCP: raw JSON-RPC client and the official SDK `McpClient` both list and call the tool | PASS |
| Pipe ACL: only the current user's SID is allowed; no Everyone, Authenticated Users or Network | PASS |
| Server verifies the peer's user SID and logon session by impersonation (`RunAsClient`); client verifies the pipe owner is the current user | PASS for the same user |
| Pipe-name collision fails (`FirstPipeInstance`) rather than attaching to another owner | Implemented; not separately tested |
| **Second Windows user refused** | **NOT RUN**: needs a second account. The ACL and peer checks above are the mechanism; refusal itself is unexercised. |

## Findings to carry forward
1. **Impersonation needs a prior read.** `NamedPipeServerStream.RunAsClient` fails until the server has read from the pipe, so the identity check has to run after the first frame arrives and before anything in it is acted on. Checking at connect time made every connection fail.
2. `PipeOptions.CurrentUserOnly` gives the ACL (verified through `GetAccessControl`). The allowed SID is `S-1-12-1-...`, an Entra ID account, so do not assume a classic `S-1-5-21` SID.
3. `Utf8JsonWriter` escapes non-ASCII by default; the codec uses relaxed escaping so frames stay UTF-8 and round-trip raw text.
4. The SDK's `CallToolResult` carries `StructuredContent` as a `JsonElement`; the shim returns tool errors as `isError` with `structuredContent.error.{code,message,retryable,details}` so an agent can branch on the code.
5. Test host and shim run as separate processes in the tests; the shim never starts the host.
6. Unix fake-transport tests would not establish Windows ACLs; the ACL test is skipped off Windows.

## Evidence files
`measurements.json` (timings, allowed SID), `mcp-transcript.txt` (raw stdio session: initialize, tools/list, tools/call).
