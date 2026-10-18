// Verify the published MCP tool using only its returned schemas, not repository contracts.
// Requires npm ci in src/web for Ajv. Usage: node tools/verify-operation-discovery.mjs <Diagram.Mcp.exe>
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { createInterface } from 'node:readline';

const require = createRequire(new URL('../src/web/package.json', import.meta.url));
const Ajv = require('ajv/dist/2020.js').default;
assert.ok(process.argv[2], 'Pass the published Diagram.Mcp.exe path');
const child = spawn(process.argv[2], [], {
  cwd: tmpdir(), stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, DIAGRAM_PIPE: `discovery-offline-${crypto.randomUUID()}` },
});
const pending = new Map();
let nextId = 0;
child.stderr.on('data', () => {});
child.on('error', error => { for (const { reject } of pending.values()) reject(error); });
child.on('exit', () => { for (const { reject } of pending.values()) reject(new Error('MCP process exited')); });
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  const message = JSON.parse(line);
  const entry = pending.get(message.id);
  if (entry) {
    clearTimeout(entry.timer); pending.delete(message.id);
    if (message.error) entry.reject(new Error(JSON.stringify(message.error)));
    else entry.resolve(message.result);
  }
});
function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timeout: ${method}`)); }, 30000);
    pending.set(id, { resolve, reject, timer });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
async function call(args) {
  const result = await rpc('tools/call', { name: 'get_operation_schema', arguments: args });
  assert.ok(!result.isError, JSON.stringify(result));
  return result.structuredContent;
}
try {
  await rpc('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'discovery-verifier', version: '1.0' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const catalogue = await rpc('tools/list', {});
  for (const name of ['render_reference_region', 'compare_reference']) {
    const tool = catalogue.tools.find(t => t.name === name);
    assert.ok(tool, `Missing published tool ${name}`);
    for (const key of ['documentId', 'sessionId', 'referenceAssetId', 'referenceSha256', 'cropPx'])
      assert.ok(tool.inputSchema.required.includes(key), `${name} must require ${key}`);
    assert.ok(!tool.inputSchema.properties.transactionId, `${name} must be read-only`);
    if (name === 'compare_reference') assert.ok(tool.inputSchema.required.includes('pageId'));
  }
  const index = await call({});
  assert.ok(index.operations.length > 0);
  for (const { operation } of index.operations) {
    const result = await call({ operation });
    const validate = new Ajv({ strict: false }).compile(result.schema);
    assert.ok(validate(result.example), `${operation}: ${JSON.stringify(validate.errors)}`);
  }
  console.log(`Published MCP: ${index.operations.length} self-contained schemas compiled and examples validated, with no editor connection or contract file access.`);
  console.log('Published reference crop/comparison tools discovered with required source version and document identities.');
} finally {
  for (const entry of pending.values()) clearTimeout(entry.timer);
  pending.clear(); lines.close(); child.kill();
}
