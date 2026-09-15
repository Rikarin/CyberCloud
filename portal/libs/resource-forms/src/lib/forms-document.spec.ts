import { apiVersion } from '@cybercloud/api';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { hintOf, isGroup, isTagBag, typeOf } from './form-model';
import { AnyForm, FieldChoice, FormField, FormsDocument, ResourceForm, ScopeForm } from './schema';

/**
 * The real document, so every suite in this library runs against what the emitter produced.
 *
 * ⚠ **Read from disk, never imported** — the same rule the renderer lives by, for a different
 * reason: an `import` of the JSON would compile it into the test bundle and pass regardless of what
 * the build serves. `readFileSync` of the checked-in file is the artefact `./build.sh Generate`
 * byte-checks, and its api-version is taken from the generated client so the two cannot drift
 * apart without this file failing to open.
 */
export const formsDocument: FormsDocument = JSON.parse(
  readFileSync(join(__dirname, '..', '..', '..', '..', '..', 'generated', 'forms', `${apiVersion}.json`), 'utf8')
) as FormsDocument;

/** Every resource form, in document order. */
export const resourceForms: readonly ResourceForm[] = Object.values(formsDocument.forms);

/** Both scope forms. */
export const scopeForms: readonly ScopeForm[] = Object.values(formsDocument.scopeForms);

/** Every form the renderer must handle. */
export const everyForm: readonly AnyForm[] = [...resourceForms, ...scopeForms];

/** `[name, form]` pairs for `it.each`. */
export const everyFormCase: readonly [string, AnyForm][] = everyForm.map(f => [f.title, f]);

/** The value-bearing fields of a form — everything that is not a group. */
export function leavesOf(form: AnyForm): readonly FormField[] {
  return form.fields.filter(f => !isGroup(f));
}

/**
 * A value the platform would accept for one field, from what the schema itself offers: the
 * example, the default, the first allowed value. `undefined` when the schema offers nothing
 * usable, which a test then reports rather than papering over.
 */
export function sampleFor(field: FormField): unknown {
  const type = typeOf(field);
  const pattern = field.pattern === undefined ? null : new RegExp(field.pattern);
  const choices = field.choices ?? [];
  const accepts = (value: string): boolean =>
    (pattern === null || pattern.test(value)) &&
    (choices.length === 0 || choices.some((c: FieldChoice) => c.value === value));

  switch (type) {
    case 'boolean':
      return field.default === undefined ? true : !field.default;
    case 'integer':
      return field.minimum ?? field.maximum ?? field.default ?? 1;
    case 'array': {
      const candidates = [field.placeholder, field.default, choices.map(c => c.value)].filter(
        Array.isArray
      ) as string[][];
      return candidates.find(c => c.length > 0 && c.every(accepts)) ?? [];
    }
    case 'object':
      return isTagBag(field) ? { env: 'test', owner: 'portal' } : undefined;
    default: {
      const candidates = [field.placeholder, field.default, choices[0]?.value, 'a'].filter(
        (c): c is string => typeof c === 'string' && c.length > 0
      );
      if (field.format === 'uuid') candidates.unshift('0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
      if (field.format === 'date-time') candidates.unshift('2026-09-15T08:00:00Z');
      return candidates.find(accepts);
    }
  }
}

/** A complete, valid body for a form: every required field filled from the schema's own hints. */
export function validBodyFor(form: AnyForm): Record<string, unknown> {
  const body: Record<string, unknown> = {};

  for (const field of form.fields) {
    if (!field.required) continue;
    if (isGroup(field)) continue;

    const value = sampleFor(field);
    if (value === undefined) continue;

    const path = field.jsonPointer.split('/').slice(1);
    let cursor = body;

    for (const segment of path.slice(0, -1)) {
      cursor = (cursor[segment] ??= {}) as Record<string, unknown>;
    }

    cursor[path[path.length - 1]] = value;
  }

  return body;
}

/**
 * The document itself, before any suite builds a form from it. A failure here means the emitter
 * changed shape, and every other suite in this library would fail for the same reason less
 * legibly.
 */
describe('generated/forms — the document the renderer reads', () => {
  it('is the api-version the generated client was built at', () => {
    expect(formsDocument.apiVersion).toBe(apiVersion);
    expect(formsDocument.format).toBe('1');
  });

  it('has a form for every type, and both scope forms', () => {
    expect(resourceForms.length).toBeGreaterThan(0);
    expect(scopeForms.map(s => s.scope).sort()).toEqual(['resourceGroup', 'subscription']);

    for (const form of resourceForms) {
      expect(form.overrideKey).toBe(`${form.resourceType}@${form.apiVersion}`);
      expect(form.fields.length).toBeGreaterThan(0);
    }
  });

  it('describes every field, in one of the two spellings', () => {
    // A field with no helper text is a field the person filling it in has to guess at. Both kinds
    // of form carry one, under different names — `hint` for a resource, `help` for a scope.
    for (const form of everyForm) {
      for (const field of leavesOf(form)) {
        expect({ form: form.title, field: field.jsonPointer, hint: hintOf(field) }).toEqual(
          expect.objectContaining({ hint: expect.stringMatching(/\S/) })
        );
      }
    }
  });
});
