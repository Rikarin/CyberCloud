import { AbstractControl, FormControl, FormGroup, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { AnyForm, FieldType, FormField } from './schema';

/** Whether the form creates a resource or replaces one that exists. */
export type FormMode = 'create' | 'edit';

/**
 * A field with its children, which is what the renderer walks.
 *
 * The document is flat — one entry per JSON Pointer — and the renderer needs a tree, because a
 * group is a `<fieldset>` and its members go inside it. The tree is built once from the pointers
 * and never from `section`: `section` names the group a field belongs to, and two groups at
 * different depths can share a name (`/properties/storage` and `/properties/backup/storage`), so it
 * cannot be a key.
 */
export interface FieldNode {
  readonly field: FormField;
  readonly children: readonly FieldNode[];
}

/** The type a scope-form field has when the document does not say: every one of them is a string. */
export function typeOf(field: FormField): FieldType {
  return field.type ?? 'string';
}

/** The helper text, from whichever member this kind of form carries it in. */
export function hintOf(field: FormField): string {
  return field.hint ?? field.help ?? '';
}

/** Whether a field's value is a bag of key/value pairs. */
export function isTagBag(field: FormField): boolean {
  return field.control === 'xui-tag-input' && typeOf(field) === 'object';
}

/** Whether a field's value is a list of strings — chips, or a multi-select over a closed set. */
export function isList(field: FormField): boolean {
  return typeOf(field) === 'array';
}

/** Whether a field is a group of other fields rather than a value. */
export function isGroup(field: FormField): boolean {
  return field.control === 'xui-group';
}

/** The fields as a tree, in document order. */
export function treeOf(form: AnyForm): readonly FieldNode[] {
  const byPointer = new Map<string, { field: FormField; children: FieldNode[] }>();
  const roots: FieldNode[] = [];

  for (const field of form.fields) {
    const node = { field, children: [] as FieldNode[] };
    byPointer.set(field.jsonPointer, node);

    const parent = byPointer.get(field.jsonPointer.slice(0, field.jsonPointer.lastIndexOf('/')));

    if (parent === undefined) roots.push(node);
    else parent.children.push(node);
  }

  return roots;
}

/**
 * Builds the reactive form for a schema, in one mode, over an optional existing body.
 *
 * What the controls hold, per kind — and `toBody` is the only place that turns it back into JSON:
 *
 * | Kind                    | Control value                    |
 * | ----------------------- | -------------------------------- |
 * | string, any widget      | `string`                         |
 * | integer                 | `number \| null`                 |
 * | boolean                 | `boolean`                        |
 * | array (chips, multi)    | `string[]`                       |
 * | tag bag                 | `string[]` of `key=value` lines  |
 * | group                   | a nested `FormGroup`             |
 *
 * ⚠ **`edit` disables every `disabledAfterCreate` field, and the disabled value still ships.**
 * `x-cybercloud-immutable` means the platform refuses a change, not that the member may be
 * omitted — a `PUT` is a full replacement, and a body without `location` is a `400` on the
 * required member rather than "unchanged". So `toBody` reads `getRawValue()`, which includes
 * disabled controls, and the renderer shows the field greyed with `disabledReason` as its title.
 */
export function buildForm(form: AnyForm, mode: FormMode, initial?: unknown): FormGroup {
  const group = new FormGroup({});

  for (const node of treeOf(form)) addNode(group, node, mode, memberOf(initial, node.field.name));

  return group;
}

function addNode(parent: FormGroup, node: FieldNode, mode: FormMode, initial: unknown): void {
  const { field } = node;

  if (isGroup(field)) {
    const child = new FormGroup({});
    for (const member of node.children) addNode(child, member, mode, memberOf(initial, member.field.name));
    parent.addControl(field.name, child);
    return;
  }

  const control = new FormControl(initialValueOf(field, initial), {
    nonNullable: typeOf(field) !== 'integer' && !field.nullable,
    validators: validatorsOf(field)
  });

  if (mode === 'edit' && field.disabledAfterCreate === true) control.disable();

  parent.addControl(field.name, control);
}

function memberOf(value: unknown, name: string): unknown {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>)[name] : undefined;
}

/** What a control starts with: the body's value in `edit`, else the schema's default, else empty. */
function initialValueOf(field: FormField, fromBody: unknown): unknown {
  const source = fromBody !== undefined ? fromBody : field.default;

  switch (typeOf(field)) {
    case 'integer':
      return typeof source === 'number' ? source : null;
    case 'boolean':
      return source === true;
    case 'array':
      return Array.isArray(source) ? source.map(String) : [];
    case 'object':
      // A tag bag. Anything else typed `object` is a group and never reaches here.
      return typeof source === 'object' && source !== null
        ? Object.entries(source as Record<string, unknown>).map(([k, v]) => `${k}=${String(v)}`)
        : [];
    default:
      if (source === null && field.nullable === true) return null;
      return typeof source === 'string' ? source : source === undefined || source === null ? '' : String(source);
  }
}

/**
 * The client-side half of docs/plan/20's "schema + async server validation, one message shape".
 * Every constraint the document states becomes a validator; the server's answers arrive through
 * `applyServerErrors` and land on the same controls.
 */
function validatorsOf(field: FormField): ValidatorFn[] {
  const validators: ValidatorFn[] = [];
  const type = typeOf(field);

  if (field.required && type !== 'boolean') {
    validators.push(type === 'array' || isTagBag(field) ? nonEmptyList : Validators.required);
  }

  if (type === 'integer') {
    validators.push(integer);
    if (field.minimum !== undefined) validators.push(Validators.min(field.minimum));
    if (field.maximum !== undefined) validators.push(Validators.max(field.maximum));
  }

  if (type === 'string') {
    if (field.minLength !== undefined) validators.push(Validators.minLength(field.minLength));
    if (field.maxLength !== undefined) validators.push(Validators.maxLength(field.maxLength));
    if (field.pattern !== undefined) validators.push(Validators.pattern(field.pattern));
    if (field.format === 'uuid') validators.push(Validators.pattern(UUID));
    if (field.choices !== undefined && field.choices.length > 0)
      validators.push(oneOf(field.choices.map(c => c.value)));
  }

  if (type === 'array') {
    if (field.pattern !== undefined) validators.push(everyMatches(new RegExp(field.pattern)));
    if (field.choices !== undefined && field.choices.length > 0)
      validators.push(everyOneOf(field.choices.map(c => c.value)));
  }

  if (isTagBag(field)) {
    validators.push(tagLines);
    if (field.maxProperties !== undefined) validators.push(Validators.maxLength(field.maxProperties));
  }

  return validators;
}

const UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

const nonEmptyList: ValidatorFn = control =>
  Array.isArray(control.value) && control.value.length > 0 ? null : { required: true };

const integer: ValidatorFn = control =>
  control.value === null || control.value === undefined || Number.isInteger(control.value) ? null : { integer: true };

const oneOf =
  (values: readonly string[]): ValidatorFn =>
  control =>
    control.value === '' || control.value === null || values.includes(control.value) ? null : { oneOf: { values } };

const everyOneOf =
  (values: readonly string[]): ValidatorFn =>
  control =>
    (control.value as string[]).every(v => values.includes(v)) ? null : { oneOf: { values } };

const everyMatches =
  (pattern: RegExp): ValidatorFn =>
  control =>
    (control.value as string[]).every(v => pattern.test(v)) ? null : { pattern: { requiredPattern: pattern.source } };

/** A tag line is `key=value` with a non-empty key. `=` alone, or a bare word, is refused. */
const tagLines: ValidatorFn = control =>
  (control.value as string[]).every(line => /^[^=]+=/.test(line)) ? null : { tagLine: true };

/**
 * The request body for a form's current value.
 *
 * ⚠ **Optional and empty means absent.** The generated schemas carry `default: ""` for optional
 * strings whose pattern admits the empty string, and `additionalProperties: false` everywhere — so
 * an optional member with nothing in it is left out rather than sent as `""`, and an object whose
 * members were all left out is left out too. A required member is always sent, empty or not,
 * because the platform's error for it is more useful than a silently dropped field.
 */
export function toBody(form: AnyForm, group: FormGroup): Record<string, unknown> {
  const body: Record<string, unknown> = {};

  for (const node of treeOf(form)) {
    const value = bodyOf(node, group.getRawValue() as Record<string, unknown>);
    if (value !== undefined) body[node.field.name] = value;
  }

  return body;
}

function bodyOf(node: FieldNode, raw: Record<string, unknown>): unknown {
  const { field } = node;
  const value = raw[field.name];

  if (isGroup(field)) {
    const inner = (value ?? {}) as Record<string, unknown>;
    const built: Record<string, unknown> = {};

    for (const child of node.children) {
      const childValue = bodyOf(child, inner);
      if (childValue !== undefined) built[child.field.name] = childValue;
    }

    return Object.keys(built).length === 0 && !field.required ? undefined : built;
  }

  if (isTagBag(field)) {
    const lines = value as string[];
    if (lines.length === 0 && !field.required) return undefined;

    return Object.fromEntries(
      lines.map(line => {
        const at = line.indexOf('=');
        return [line.slice(0, at), line.slice(at + 1)];
      })
    );
  }

  switch (typeOf(field)) {
    case 'array':
      return (value as string[]).length === 0 && !field.required ? undefined : value;
    case 'integer':
      return value === null && !field.required ? undefined : value;
    case 'boolean':
      return value;
    default:
      if (value === null) return field.nullable === true ? null : undefined;
      return value === '' && !field.required ? undefined : value;
  }
}

/** Every leaf control, keyed by JSON Pointer — what server errors and tests address. */
export function controlsByPointer(group: FormGroup, prefix = ''): Map<string, AbstractControl> {
  const found = new Map<string, AbstractControl>();

  for (const [name, control] of Object.entries(group.controls)) {
    const pointer = `${prefix}/${name}`;

    if (control instanceof FormGroup) {
      for (const [inner, leaf] of controlsByPointer(control, pointer)) found.set(inner, leaf);
    } else {
      found.set(pointer, control);
    }
  }

  return found;
}

/** The error `key` a validator produced, as one message. */
export function messageFor(field: FormField, errors: ValidationErrors | null): string | null {
  if (errors === null) return null;

  if (errors['server'] !== undefined) return String(errors['server']);
  if (errors['required'] !== undefined) return $localize`:@@forms.required:${field.label}:label: is required.`;
  if (errors['integer'] !== undefined) return $localize`:@@forms.integer:${field.label}:label: must be a whole number.`;
  if (errors['min'] !== undefined)
    return $localize`:@@forms.min:${field.label}:label: must be at least ${String(field.minimum)}:min:.`;
  if (errors['max'] !== undefined)
    return $localize`:@@forms.max:${field.label}:label: must be at most ${String(field.maximum)}:max:.`;
  if (errors['minlength'] !== undefined)
    return $localize`:@@forms.minLength:${field.label}:label: needs at least ${String(field.minLength)}:min: characters.`;
  if (errors['maxlength'] !== undefined)
    return isTagBag(field)
      ? $localize`:@@forms.maxTags:At most ${String(field.maxProperties)}:max: tags.`
      : $localize`:@@forms.maxLength:${field.label}:label: allows at most ${String(field.maxLength)}:max: characters.`;
  if (errors['pattern'] !== undefined)
    return field.placeholder !== undefined
      ? $localize`:@@forms.patternExample:${field.label}:label: is not in the expected form, for example ${String(field.placeholder)}:example:.`
      : $localize`:@@forms.pattern:${field.label}:label: is not in the expected form.`;
  if (errors['oneOf'] !== undefined)
    return $localize`:@@forms.oneOf:${field.label}:label: is not one of the allowed values.`;
  if (errors['tagLine'] !== undefined) return $localize`:@@forms.tagLine:Write each tag as key=value.`;

  return $localize`:@@forms.invalid:${field.label}:label: is not valid.`;
}
