# R01: MCP operation discovery

Completed 2026-10-18 (Europe/London), after approval of the bounded design.

`get_operation_schema` is a read-only tool implemented in the MCP companion. It
works without a running editor or open document, and does not call the app pipe.

- With no arguments: an index of all 29 canonical operation names and required fields.
- With `{"operation":"resize"}` (or another exact name): a self-contained JSON Schema,
  its transitively referenced definitions and a validated example operation.
- Both responses include coordinate units, canonical batch limits, UUID/alias targeting,
  patch rules, transaction identity/retry guidance and asset prerequisites.
- Unknown names return `invalid_request` and direct the caller to the operation index.
- `apply_operations` now advertises discovery in its tool description.

The schemas are embedded from the existing `contracts/*.schema.json` files at build
time. Discovery rewrites only reference pointers into local definitions; it does not
maintain a separate handwritten operation schema. Examples live in
`contracts/fixtures/operation-examples.json` and are validated against the canonical
schemas. Examples are independent fragments: object, page, layer and asset identities
must be replaced with valid state before applying them.

## Verification

- Test-first: offline discovery failed with Unknown tool before implementation;
  the example catalogue test failed before the catalogue existed.
- Full MCP suite: 12 passed, zero failures.
- Strengthened offline test after final assertions: passed. It checks every operation,
  canonical root and definition parity, example parity, local reference resolution,
  batch-limit parity and unknown-name errors through an actual stdio MCP client.
- Frontend: typecheck passed; 258 tests passed, including validation and exact coverage
  of the 29 operation examples.
- Read-only code review found no actionable issues.
- Release companion published to `%LOCALAPPDATA%/Programs/AgenticDiagram/mcp`.
- `node tools/verify-operation-discovery.mjs <installed Diagram.Mcp.exe>` launched
  that binary from the temporary folder with a nonexistent app pipe. All 29 returned
  self-contained schemas compiled in Ajv and validated their returned examples without
  reading any contract files or contacting the editor.

No mutation or document behavior changed. Existing MCP client connections may need
to reconnect to refresh their cached tool lists. Richer validation-error suggestions
and direct schema expansion within apply_operations remain possible follow-ups;
the delivered discovery tool provides the approved schema/example path.
