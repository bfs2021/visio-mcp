import Ajv2020, { type ValidateFunction } from 'ajv/dist/2020.js';
import documentSchema from '../../../../contracts/document.schema.json';
import operationsSchema from '../../../../contracts/operations.schema.json';
import protocolSchema from '../../../../contracts/protocol.schema.json';

// Tests use runtime Ajv to check samples against the schemas themselves; production code uses the precompiled validators.
const ajv = new Ajv2020({ allErrors: false, strict: false });
const schemas = [documentSchema, operationsSchema, protocolSchema] as Array<{ $id: string; $defs: Record<string, unknown> }>;
for (const s of schemas) ajv.addSchema(s);

const defFile = new Map<string, string>();
for (const s of schemas) for (const name of Object.keys(s.$defs)) defFile.set(name, s.$id);

export function validateDef(def: string, value: unknown): { valid: boolean; message?: string } {
  const id = defFile.get(def);
  if (!id) throw new Error(`unknown schema definition ${def}`);
  const v = ajv.getSchema(`${id}#/$defs/${def}`) as ValidateFunction;
  return v(value) ? { valid: true } : { valid: false, message: `${v.errors?.[0]?.instancePath} ${v.errors?.[0]?.message}` };
}

export const schemaDefinitionNames = (): string[] => [...defFile.keys()];
