import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import axe from 'axe-core';
import { isGroup, typeOf } from '../form-model';
import { everyFormCase, leavesOf, resourceForms, sampleFor, validBodyFor } from '../forms-document.spec';
import { AnyForm, FormField } from '../schema';
import { ResourceFormRenderer } from './resource-form';

/**
 * The renderer, over every form in the document.
 *
 * `form-model.spec.ts` proves the control tree; this proves the DOM the tree becomes — that every
 * leaf is on screen as the widget docs/plan/20's mapping names, labelled, with its hint wired in,
 * and that the whole thing passes the same WCAG 2.2 AA rule set `apps/portal/src/app/a11y.spec.ts`
 * runs on every route. A generated form that renders a field nobody can reach by keyboard is the
 * "hundred resource types" promise failing one type at a time.
 */
const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  // See a11y.spec.ts: contrast needs rendered pixels, which jsdom has none of.
  rules: { 'color-contrast': { enabled: false } }
};

/** The element the mapping in `FormFieldNode` promises for a field. */
function widgetFor(field: FormField): string {
  const closed = field.choices !== undefined && field.choices.length > 0;

  switch (typeOf(field)) {
    case 'boolean':
      return 'xui-switch';
    case 'integer':
      return 'xui-numeric-input';
    case 'array':
      return closed ? 'xui-multi-select' : 'xui-tag-input';
    case 'object':
      return 'xui-tag-input';
    default:
      return closed ? 'xui-select' : 'input[xuiinput]';
  }
}

describe('ResourceFormRenderer — every form in the document renders, labelled and reachable', () => {
  let fixture: ComponentFixture<ResourceFormRenderer>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ResourceFormRenderer],
      providers: [provideZonelessChangeDetection()]
    });

    fixture = TestBed.createComponent(ResourceFormRenderer);
  });

  async function render(form: AnyForm, mode: 'create' | 'edit' = 'create', initial?: unknown): Promise<HTMLElement> {
    fixture.componentRef.setInput('form', form);
    fixture.componentRef.setInput('mode', mode);
    if (initial !== undefined) fixture.componentRef.setInput('initial', initial);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it.each(everyFormCase)('%s renders every leaf as the widget the mapping names', async (_title, form) => {
    const host = await render(form);
    const missing: string[] = [];

    for (const field of form.fields) {
      if (isGroup(field)) {
        const fieldset = [...host.querySelectorAll('fieldset')].find(
          f => f.querySelector('legend')?.textContent?.trim() === field.label
        );
        if (fieldset === undefined) missing.push(`${field.jsonPointer} (fieldset)`);
        continue;
      }

      const wrapper = host.querySelector(`[data-pointer="${field.jsonPointer}"]`);
      const widget = wrapper?.querySelector(widgetFor(field));

      if (widget === null || widget === undefined) missing.push(`${field.jsonPointer} (${widgetFor(field)})`);
      if (wrapper?.getAttribute('data-control') !== field.control) missing.push(`${field.jsonPointer} (data-control)`);
    }

    expect(missing).toEqual([]);
  });

  it.each(everyFormCase)('%s labels every leaf and wires its hint', async (_title, form) => {
    const host = await render(form);
    const unlabelled: string[] = [];

    for (const field of leavesOf(form)) {
      const wrapper = host.querySelector(`[data-pointer="${field.jsonPointer}"]`);
      const label = wrapper?.querySelector('label');
      const hint = wrapper?.querySelector('p[id$="-hint"]');

      if (label === null || label === undefined || !label.textContent?.includes(field.label))
        unlabelled.push(field.jsonPointer);
      if (hint === null || hint === undefined) unlabelled.push(`${field.jsonPointer} (hint)`);
      if (field.required && !(label?.textContent ?? '').includes('required'))
        unlabelled.push(`${field.jsonPointer} (required mark)`);
    }

    expect(unlabelled).toEqual([]);
  });

  it.each(everyFormCase)('%s has no WCAG 2.2 AA violations', async (_title, form) => {
    const host = await render(form);
    const results = await axe.run(host, WCAG_22_AA);
    const summary = results.violations.map(v => `${v.id} (${v.impact}): ${v.nodes.length} node(s) — ${v.help}`);

    expect(summary).toEqual([]);
  });

  it('refuses to submit an empty required field, and says which', async () => {
    const widget = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets') as AnyForm;
    const host = await render(widget);
    const submitted: unknown[] = [];
    fixture.componentInstance.submitted.subscribe(body => submitted.push(body));

    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(submitted).toEqual([]);

    const alerts = [...host.querySelectorAll('[role="alert"]')].map(a => a.textContent?.trim());
    expect(alerts).toContain('Location is required.');

    // The invalid input is announced as such, and the message is what describes it.
    const location = host.querySelector('[data-pointer="/location"] input');
    expect(location?.getAttribute('aria-invalid')).toBe('true');
    expect(location?.getAttribute('aria-describedby')).toBe('cc-f-location-error');
  });

  it('emits the body once every required field is filled', async () => {
    const widget = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets') as AnyForm;
    const body = validBodyFor(widget);
    const host = await render(widget, 'create');
    const submitted: Record<string, unknown>[] = [];
    fixture.componentInstance.submitted.subscribe(b => submitted.push(b));

    // Type into the real inputs rather than poking the model, so the value accessor path is the
    // one under test.
    for (const field of leavesOf(widget).filter(f => f.required)) {
      const input = host.querySelector<HTMLInputElement>(`[data-pointer="${field.jsonPointer}"] input`);
      if (input === null) continue;
      input.value = String(sampleFor(field));
      input.dispatchEvent(new Event('input'));
    }

    host.querySelector('form')?.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(submitted).toHaveLength(1);
    expect(submitted[0]['location']).toBe(body['location']);
    expect((submitted[0]['properties'] as Record<string, unknown>)['clusterId']).toBe(
      (body['properties'] as Record<string, unknown>)['clusterId']
    );
  });

  it('puts a server refusal on the field its target names, and the rest above the form', async () => {
    const widget = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets') as AnyForm;
    const host = await render(widget, 'edit', validBodyFor(widget));

    fixture.componentInstance.reject({
      code: 'SchemaInvalid',
      message: 'The body was refused.',
      details: [
        { code: 'SchemaInvalid', message: 'replicas is above the quota.', target: '/properties/replicas' },
        { code: 'QuotaExceeded', message: 'The subscription is out of widgets.', target: '/nothing/here' }
      ]
    });
    await fixture.whenStable();

    const onField = host.querySelector('[data-pointer="/properties/replicas"] [role="alert"]')?.textContent?.trim();
    expect(onField).toBe('replicas is above the quota.');

    const banner = host.querySelector('form > [role="alert"]')?.textContent?.trim();
    expect(banner).toBe('The subscription is out of widgets.');
    expect(banner).not.toContain('The body was refused.');
  });

  it('greys an immutable field in edit and names why', async () => {
    const widget = resourceForms.find(f => f.resourceType === 'CyberCloud.Sample/widgets') as AnyForm;
    const host = await render(widget, 'edit', validBodyFor(widget));

    const location = host.querySelector<HTMLInputElement>('[data-pointer="/location"] input');
    expect(location?.disabled).toBe(true);
    expect(location?.getAttribute('title')).toMatch(/cannot change after the resource is created/);
    expect(host.querySelector('[data-pointer="/location"] label')?.textContent).toContain('cannot change after create');
  });
});
