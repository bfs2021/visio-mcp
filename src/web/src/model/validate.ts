import Ajv2020, { type ValidateFunction } from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import documentSchema from '../../../../contracts/document.schema.json';
import operationsSchema from '../../../../contracts/operations.schema.json';
import protocolSchema from '../../../../contracts/protocol.schema.json';
import type { DiagramDocument, MutationRequest, Operation, RequestEnvelope, ScriptRequest, UndoRequest } from '../contracts/generated';
import { fail, ok, type Result } from './result';

const ajv = new Ajv2020({ allErrors: false, strict: false });
addFormats(ajv);
ajv.addSchema(documentSchema);
ajv.addSchema(operationsSchema);
ajv.addSchema(protocolSchema);

const compile = <T>(id: string, def: string) => ajv.getSchema(`${id}#/$defs/${def}`) as ValidateFunction<T>;
const vDocument = compile<DiagramDocument>('document.schema.json', 'DiagramDocument');
const vOperation = compile<Operation>('operations.schema.json', 'Operation');
const vMutation = compile<MutationRequest>('protocol.schema.json', 'MutationRequest');
const vScript = compile<ScriptRequest>('protocol.schema.json', 'ScriptRequest');
const vUndo = compile<UndoRequest>('protocol.schema.json', 'UndoRequest');
const vEnvelope = compile<RequestEnvelope>('protocol.schema.json', 'RequestEnvelope');

const opNames = ['CreateShape', 'CreateText', 'CreateImage', 'CreateConnector', 'Set', 'Move', 'Resize', 'Rotate', 'Delete', 'RegisterAsset'];
const opValidators = new Map<string, ValidateFunction>(
  opNames.map(n => [n, ajv.getSchema(`operations.schema.json#/$defs/${n}`) as ValidateFunction]),
);
const opByTag: Record<string, string> = { createShape: 'CreateShape', createText: 'CreateText', createImage: 'CreateImage', createConnector: 'CreateConnector', set: 'Set', move: 'Move', resize: 'Resize', rotate: 'Rotate', delete: 'Delete', registerAsset: 'RegisterAsset' };

/** A oneOf failure only reports its first branch; validate against the branch the operation names so the error is about the right fields. */
function explainOperations(operations: unknown, basePath: string): Result<never> | null {
  if (!Array.isArray(operations)) return null;
  for (let i = 0; i < operations.length; i++) {
    const op = operations[i] as { op?: unknown } | null;
    const branch = typeof op?.op === 'string' ? opByTag[op.op] : undefined;
    if (!branch) return fail('invalid_request', `${basePath}/${i}/op is not a known operation`, { details: { path: `${basePath}/${i}/op`, keyword: 'enum', params: {} } });
    const v = opValidators.get(branch)!;
    if (!v(op)) {
      const e = v.errors![0]!;
      return fail('invalid_request', `${basePath}/${i}${e.instancePath} ${e.message ?? 'does not match the schema'}`, {
        details: { path: `${basePath}/${i}${e.instancePath}`, keyword: e.keyword, params: e.params as Record<string, unknown> },
      });
    }
  }
  return null;
}

/** Ajv's number type admits NaN and Infinity; the model must not. */
function nonFinitePath(value: unknown, path = ''): string | null {
  if (typeof value === 'number') return Number.isFinite(value) ? null : path || '/';
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i++) { const p = nonFinitePath(value[i], `${path}/${i}`); if (p) return p; }
  } else if (value && typeof value === 'object') {
    for (const [k, v] of Object.entries(value)) { const p = nonFinitePath(v, `${path}/${k}`); if (p) return p; }
  }
  return null;
}
const finiteError = (p: string): Result<never> =>
  fail('invalid_request', `${p} is not a finite number`, { details: { path: p, keyword: 'finite', params: {} } });

function schemaError(v: ValidateFunction, value?: unknown): Result<never> {
  const top = v.errors?.[0];
  const ops = (value as { operations?: unknown; params?: { operations?: unknown } } | undefined);
  const explained = top && /operations/.test(top.instancePath) || top?.keyword === 'oneOf'
    ? explainOperations(ops?.operations ?? ops?.params?.operations, ops?.operations !== undefined ? '/operations' : '/params/operations')
    : null;
  if (explained) return explained;
  const e = top;
  return fail('invalid_request', `${e?.instancePath || '/'} ${e?.message ?? 'does not match the schema'}`, {
    details: { path: e?.instancePath ?? '', keyword: e?.keyword ?? '', params: (e?.params ?? {}) as Record<string, unknown> },
  });
}

/** Schema validation plus the cross-reference rules JSON Schema cannot express. */
export function validateDocument(value: unknown): Result<DiagramDocument> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  if (!vDocument(value)) return schemaError(vDocument, value);
  const doc = value as DiagramDocument;
  const ids = new Set<string>();
  const assetIds = new Set<string>();
  for (const a of doc.assets) {
    if (assetIds.has(a.id)) return fail('invalid_request', `duplicate asset id ${a.id}`, { details: { assetId: a.id } });
    assetIds.add(a.id);
  }
  const pageIds = new Set<string>();
  for (const page of doc.pages) {
    if (pageIds.has(page.id)) return fail('invalid_request', `duplicate page id ${page.id}`);
    pageIds.add(page.id);
    const layerIds = new Set<string>();
    for (const l of page.layers) {
      if (layerIds.has(l.id)) return fail('invalid_request', `duplicate layer id ${l.id}`);
      layerIds.add(l.id);
    }
    const aliases = new Set<string>();
    const pageElements = new Set(page.elements.map(e => e.id));
    for (const el of page.elements) {
      if (ids.has(el.id)) return fail('invalid_request', `duplicate element id ${el.id}`, { details: { id: el.id } });
      ids.add(el.id);
      if (el.alias !== undefined) {
        if (aliases.has(el.alias)) return fail('invalid_request', `duplicate alias ${el.alias} on page ${page.name}`, { details: { alias: el.alias } });
        aliases.add(el.alias);
      }
      for (const l of el.layerIds) if (!layerIds.has(l)) return fail('invalid_request', `element ${el.id} references unknown layer ${l}`);
      if (!(el.bounds.width >= 0 && el.bounds.height >= 0)) return fail('invalid_request', `element ${el.id} has negative size`);
      if (el.kind === 'shape' || el.kind === 'image') {
        if (!(el.bounds.width > 0 && el.bounds.height > 0)) return fail('invalid_request', `element ${el.id} must have positive size`);
      }
      if (el.kind === 'image' && !assetIds.has(el.assetId)) return fail('invalid_request', `image ${el.id} references unknown asset ${el.assetId}`);
      if (el.kind === 'connector') {
        for (const ep of [el.from, el.to]) {
          if (ep.glue !== 'none' && (!ep.elementId || !pageElements.has(ep.elementId))) return fail('invalid_request', `connector ${el.id} has a glued endpoint without a same-page element`);
          if (ep.glue === 'none' && !ep.point) return fail('invalid_request', `connector ${el.id} has a free endpoint without a point`);
        }
      }
      if (el.kind === 'group') for (const c of el.childIds) if (!pageElements.has(c)) return fail('invalid_request', `group ${el.id} references unknown child ${c}`);
    }
  }
  return ok(doc);
}

type Creator = { op: string; id?: string; alias?: string };

function checkBatch(operations: Operation[]): Result<never> | null {
  const ids = new Set<string>();
  const aliases = new Set<string>();
  for (const op of operations) {
    if (!op.op.startsWith('create')) continue;
    const c = op as Creator;
    if (c.id) {
      if (ids.has(c.id)) return fail('invalid_request', `duplicate id ${c.id} in batch`, { details: { id: c.id } });
      ids.add(c.id);
    }
    if (c.alias) {
      if (aliases.has(c.alias)) return fail('invalid_request', `duplicate alias ${c.alias} in batch`, { details: { alias: c.alias } });
      aliases.add(c.alias);
    }
  }
  return null;
}

export function validateOperation(value: unknown): Result<Operation> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  return vOperation(value) ? ok(value as Operation) : (explainOperations([value], '') ?? schemaError(vOperation));
}

export function validateMutationRequest(value: unknown): Result<MutationRequest> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  if (!vMutation(value)) return schemaError(vMutation, value);
  const dup = checkBatch(value.operations);
  return dup ?? ok(value);
}

export function validateScriptRequest(value: unknown): Result<ScriptRequest> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  return vScript(value) ? ok(value as ScriptRequest) : schemaError(vScript, value);
}

export function validateUndoRequest(value: unknown): Result<UndoRequest> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  return vUndo(value) ? ok(value as UndoRequest) : schemaError(vUndo, value);
}

/** Envelope validation; `doc.apply` params are checked as a mutation request once scope is merged in. */
export function validateEnvelope(value: unknown): Result<RequestEnvelope> {
  const nf = nonFinitePath(value);
  if (nf) return finiteError(nf);
  if (!vEnvelope(value)) return schemaError(vEnvelope, value);
  const env = value as RequestEnvelope;
  if (env.method === 'doc.apply') {
    if (!env.documentId || !env.sessionId) return fail('invalid_request', 'doc.apply requires documentId and sessionId at the envelope top level');
    const merged = validateMutationRequest({ ...env.params, documentId: env.documentId, sessionId: env.sessionId });
    if (!merged.ok) return merged;
  }
  return ok(env);
}

export type Unit = 'mm' | 'cm' | 'in' | 'pt' | 'px';
const PER_UNIT: Record<Unit, number> = { mm: 72 / 25.4, cm: 72 / 2.54, in: 72, pt: 1, px: 0.75 };

/** Converts at the input boundary only; the model stores points. */
export function toPoints(value: number, unit: Unit): number {
  if (!Number.isFinite(value)) throw new RangeError(`not a finite number: ${value}`);
  const f = PER_UNIT[unit];
  if (f === undefined) throw new RangeError(`unknown unit: ${unit}`);
  return value * f;
}

const defFile = new Map<string, string>();
for (const s of [documentSchema, operationsSchema, protocolSchema]) {
  for (const name of Object.keys((s as { $defs: object }).$defs)) defFile.set(name, (s as { $id: string }).$id);
}

/** Validates a value against any named schema definition (for fixtures and generic tooling). */
export function validateDef(def: string, value: unknown): { valid: boolean; message?: string } {
  const id = defFile.get(def);
  if (!id) throw new Error(`unknown schema definition ${def}`);
  const v = ajv.getSchema(`${id}#/$defs/${def}`)!;
  const nf = nonFinitePath(value);
  if (nf) return { valid: false, message: `${nf} is not finite` };
  return v(value) ? { valid: true } : { valid: false, message: `${v.errors?.[0]?.instancePath} ${v.errors?.[0]?.message}` };
}

export const schemaDefinitionNames = (): string[] => [...defFile.keys()];
