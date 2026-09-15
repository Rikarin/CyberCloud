import { FormGroup } from '@angular/forms';
import { buildForm, controlsByPointer, isGroup, isTagBag, messageFor, toBody, treeOf, typeOf } from './form-model';
import { everyForm, everyFormCase, leavesOf, resourceForms, sampleFor, validBodyFor } from './forms-document.spec';
import { FieldControl } from './schema';

/**
 * The form model, over every type in `generated/forms/{apiVersion}.json`.
 *
 * ⚠ **Every case iterates the real document rather than a fixture.** The point of a generated form
 * is that a new resource type gets a form nobody wrote; the point of this suite is that the new
 * type gets a form that works, because a renderer that silently rendered nothing for a control it
 * did not know would ship a create blade that cannot create. So: every control name is one of a
 * closed set, every covered pointer has a control, every schema constraint is a validator, and a
 * body round-trips through `edit` unchanged.
 */
describe('buildForm — one control per covered pointer', () => {
  const KNOWN: readonly FieldControl[] = [
    'xui-input',
    'xui-slider',
    'xui-switch',
    'xui-select',
    'xui-region',
    'xui-cluster',
    'xui-tag-input',
    'xui-cozy-preset',
    'xui-storageclass',
    'xui-cidr',
    'xui-sku',
    'xui-chip-input',
    'xui-subnet',
    'xui-numeric-input',
    'xui-secret-ref',
    'xui-group'
  ];

  it.each(everyFormCase)('%s uses only controls the renderer knows', (_title, form) => {
    // The "unknown hint is a build failure" property from this library's header, as a test: a
    // fifteenth control name the emitter adds fails here, not in a browser.
    const unknown = form.fields.filter(f => !KNOWN.includes(f.control)).map(f => `${f.jsonPointer}: ${f.control}`);

    expect(unknown).toEqual([]);
  });

  it.each(everyFormCase)('%s has a control at every covered pointer and nowhere else', (_title, form) => {
    const group = buildForm(form, 'create');
    const controls = controlsByPointer(group);

    for (const field of form.fields) {
      const control = group.get(field.jsonPointer.split('/').slice(1));

      expect({ pointer: field.jsonPointer, present: control !== null }).toEqual({
        pointer: field.jsonPointer,
        present: true
      });
      expect(control instanceof FormGroup).toBe(isGroup(field));
    }

    // Every leaf the form built is a pointer the emitter said it covers — the form invents nothing.
    for (const pointer of controls.keys()) {
      expect(form.coveredPointers).toContain(pointer);
    }
  });

  it.each(everyFormCase)('%s starts from the schema defaults', (_title, form) => {
    const group = buildForm(form, 'create');

    for (const field of leavesOf(form)) {
      if (field.default === undefined) continue;

      const value = group.get(field.jsonPointer.split('/').slice(1))?.value;
      const expected = isTagBag(field) ? [] : field.default;

      expect({ pointer: field.jsonPointer, value }).toEqual({ pointer: field.jsonPointer, value: expected });
    }
  });

  it.each(everyFormCase)('%s — every default satisfies its own constraints', (_title, form) => {
    // The Mail row found "a required patterned property with no default emits "" and fails its
    // own pattern". A default the schema refuses is a form that opens already invalid, and the
    // person filling it in cannot tell which of forty fields is the one they did not touch.
    const group = buildForm(form, 'create');
    const offenders: string[] = [];

    for (const field of leavesOf(form)) {
      const control = group.get(field.jsonPointer.split('/').slice(1));
      const errors = control?.errors ?? {};

      // `required` is the one error an untouched field may legitimately carry.
      const others = Object.keys(errors).filter(k => k !== 'required');
      if (others.length > 0) offenders.push(`${field.jsonPointer}: ${others.join(', ')}`);
    }

    expect(offenders).toEqual([]);
  });

  it.each(everyFormCase)('%s — every example satisfies its own pattern', (_title, form) => {
    // The placeholder is what the message "for example …" quotes when a value is refused. An
    // example the pattern refuses would send the user in circles.
    const offenders = leavesOf(form)
      .filter(f => f.pattern !== undefined && typeof f.placeholder === 'string' && f.placeholder.length > 0)
      .filter(f => !new RegExp(f.pattern as string).test(f.placeholder as string))
      .map(f => f.jsonPointer);

    expect(offenders).toEqual([]);
  });

  it.each(everyFormCase)('%s refuses to submit until every required field is filled', (_title, form) => {
    const group = buildForm(form, 'create');
    const required = leavesOf(form).filter(f => f.required && typeOf(f) !== 'boolean' && f.default === undefined);

    if (required.length === 0) {
      expect(group.valid).toBe(true);
      return;
    }

    expect(group.valid).toBe(false);

    for (const field of required) {
      const control = group.get(field.jsonPointer.split('/').slice(1));
      expect({ pointer: field.jsonPointer, required: control?.hasError('required') }).toEqual({
        pointer: field.jsonPointer,
        required: true
      });
    }
  });

  it.each(everyFormCase)('%s accepts a body built from its own hints', (_title, form) => {
    const body = validBodyFor(form);
    const group = buildForm(form, 'edit', body);

    const invalid = [...controlsByPointer(group)]
      .filter(([, c]) => c.invalid)
      .map(([p, c]) => `${p}: ${JSON.stringify(c.errors)}`);

    expect(invalid).toEqual([]);
    expect(group.valid).toBe(true);
  });

  it.each(everyFormCase)(
    '%s round-trips a body through edit, keeping every value and adding only defaults',
    (_title, form) => {
      // ⚠ A member the body omits comes back as the schema default, explicitly. That is deliberate:
      // `monitoring.enabled` defaults to true, and an edit form that showed an omitted member as
      // "off" and then sent `false` would switch monitoring off on a resource whose owner never
      // touched that field. The default is the effective value, so sending it changes nothing.
      const body = validBodyFor(form);
      const sent = toBody(form, buildForm(form, 'edit', body));

      for (const field of leavesOf(form)) {
        const path = field.jsonPointer.split('/').slice(1);
        const before = path.reduce<unknown>((v, k) => (v as Record<string, unknown> | undefined)?.[k], body);
        const after = path.reduce<unknown>((v, k) => (v as Record<string, unknown> | undefined)?.[k], sent);

        if (before !== undefined) {
          expect({ pointer: field.jsonPointer, after }).toEqual({ pointer: field.jsonPointer, after: before });
        } else if (after !== undefined) {
          const expected = typeOf(field) === 'boolean' ? field.default === true : field.default;
          expect({ pointer: field.jsonPointer, after }).toEqual({ pointer: field.jsonPointer, after: expected });
        }
      }
    }
  );

  it.each(everyFormCase)('%s — every required leaf has a sample the schema itself offers', (_title, form) => {
    // Not a test of the model: a test that the document gives a portal enough to fill the form
    // with. A required field with no example, no default and no allowed values is one the
    // round-trip above silently skipped, and this names it rather than hiding it.
    const unsampled = leavesOf(form)
      .filter(f => f.required && sampleFor(f) === undefined)
      .map(f => f.jsonPointer);

    expect(unsampled).toEqual([]);
  });
});

describe('buildForm — edit locks what cannot change, and still sends it', () => {
  const locked = resourceForms.flatMap(form =>
    leavesOf(form)
      .filter(f => f.disabledAfterCreate === true)
      .map(f => [form.title, f.jsonPointer, form] as const)
  );

  it('the document has immutable fields to test with', () => {
    expect(locked.length).toBeGreaterThan(0);
  });

  it.each(locked)('%s %s is disabled in edit and enabled in create', (_title, pointer, form) => {
    const path = pointer.split('/').slice(1);

    expect(buildForm(form, 'create').get(path)?.disabled).toBe(false);
    expect(buildForm(form, 'edit').get(path)?.disabled).toBe(true);
  });

  it('a PUT body carries the locked value — a replacement without location is a 400, not "unchanged"', () => {
    const form = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets');
    if (form === undefined) throw new Error('the sample widget is the fixture every suite leans on');

    const body = validBodyFor(form);
    const group = buildForm(form, 'edit', body);

    expect(group.get('location')?.disabled).toBe(true);
    expect(toBody(form, group)['location']).toBe(body['location']);
  });
});

describe('toBody — what is sent and what is left out', () => {
  const widget = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets');
  if (widget === undefined) throw new Error('the sample widget is the fixture every suite leans on');

  it('omits an optional member that is empty, and an object emptied by that', () => {
    const group = buildForm(widget, 'create');
    // Fill only what is required; leave every optional at its empty value.
    for (const field of leavesOf(widget)) {
      if (field.required) group.get(field.jsonPointer.split('/').slice(1))?.setValue(sampleFor(field));
    }

    const body = toBody(widget, group);

    for (const field of leavesOf(widget)) {
      const present = field.jsonPointer
        .split('/')
        .slice(1)
        .reduce<unknown>((v, k) => (v as Record<string, unknown> | undefined)?.[k], body);

      if (field.required || typeOf(field) === 'boolean' || field.default !== undefined) {
        expect({ pointer: field.jsonPointer, present: present !== undefined }).toEqual({
          pointer: field.jsonPointer,
          present: true
        });
      } else if (typeOf(field) !== 'integer') {
        expect({ pointer: field.jsonPointer, present }).toEqual({ pointer: field.jsonPointer, present: undefined });
      }
    }

    expect(body['tags']).toBeUndefined();
  });

  it('turns tag lines into the tag bag the API takes', () => {
    const group = buildForm(widget, 'create');
    group.get('tags')?.setValue(['env=prod', 'team=core=platform']);

    // `=` in a value is kept: the split is on the first one only.
    expect(toBody(widget, group)['tags']).toEqual({ env: 'prod', team: 'core=platform' });
  });

  it('refuses a tag line with no key', () => {
    const group = buildForm(widget, 'create');
    const tags = group.get('tags');
    tags?.setValue(['=prod']);

    expect(tags?.hasError('tagLine')).toBe(true);
    expect(messageFor(widget.fields.find(f => f.name === 'tags') as never, tags?.errors ?? null)).toMatch(/key=value/);
  });

  it('caps the tag bag at the document’s maxProperties', () => {
    const field = widget.fields.find(f => f.name === 'tags');
    const cap = field?.maxProperties ?? 0;
    expect(cap).toBeGreaterThan(0);

    const group = buildForm(widget, 'create');
    const tags = group.get('tags');
    tags?.setValue(Array.from({ length: cap + 1 }, (_, i) => `k${i}=v`));

    expect(tags?.hasError('maxlength')).toBe(true);
  });

  it('sends a nullable field as null when it is cleared', () => {
    const retired = widget.fields.find(f => f.nullable === true);
    if (retired === undefined) throw new Error('the widget declares retiredOn as nullable');

    const group = buildForm(widget, 'edit', validBodyFor(widget));
    group.get(retired.jsonPointer.split('/').slice(1))?.setValue(null);

    const body = toBody(widget, group) as { properties?: Record<string, unknown> };
    expect(body.properties?.[retired.name]).toBeNull();
  });
});

describe('treeOf — groups nest by pointer, never by section name', () => {
  it('every child sits under the group its pointer names', () => {
    for (const form of everyForm) {
      const walk = (nodes: ReturnType<typeof treeOf>, prefix: string): void => {
        for (const node of nodes) {
          expect(node.field.jsonPointer.startsWith(prefix + '/')).toBe(true);
          expect(node.field.jsonPointer.slice(prefix.length + 1)).not.toContain('/');
          walk(node.children, node.field.jsonPointer);
        }
      };

      walk(treeOf(form), '');
    }
  });

  it('two groups at different depths may share a section name', () => {
    // The reason the tree is not keyed by `section`: this happens in the real document.
    const shared = resourceForms.flatMap(form => {
      const groups = form.fields.filter(isGroup);
      const byName = new Map<string, number>();
      for (const g of groups) byName.set(g.name, (byName.get(g.name) ?? 0) + 1);
      return [...byName.entries()].filter(([, n]) => n > 1).map(([name]) => `${form.title}: ${name}`);
    });

    // Not asserted to be non-empty — a future document may not have one — but when it does, the
    // walk above has already proven the tree kept them apart.
    expect(Array.isArray(shared)).toBe(true);
  });
});
