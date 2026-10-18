import { readFileSync } from 'node:fs';
import { expect, it } from 'vitest';
import operations from '../../../contracts/operations.schema.json';
import { validateDef } from './support/schemaCheck';

it('every discoverable operation has a canonical-schema-valid example', () => {
  const examples = JSON.parse(readFileSync(new URL('../../../contracts/fixtures/operation-examples.json', import.meta.url), 'utf8'));
  const defs = operations.$defs as Record<string, any>;
  const members = defs.Operation.oneOf.map((r: { $ref: string }) => r.$ref.split('/').at(-1)!);
  expect(Object.keys(examples).sort()).toEqual(members.map((d: string) => defs[d].properties.op.const).sort());
  for (const def of members) {
    const name = defs[def].properties.op.const;
    const result = validateDef(def, examples[name]);
    expect(result.valid, `${name}: ${result.message}`).toBe(true);
  }
});
