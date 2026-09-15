import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { NotificationsStore, TenantContextStore } from '@cybercloud/shell';
import axe from 'axe-core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { appRoutes } from '../app/app.routes';
import { OPERATION_POLL_MS } from './operations/operation-view';

/**
 * The pages, driven end to end against a recorded platform.
 *
 * `a11y.spec.ts` walks every route signed out; this signs in — a tenant in `TenantContextStore`,
 * the real form document served at `/forms/{apiVersion}.json`, and `HttpTestingController`
 * playing the gateway — and exercises what each page does: the create blade's `PUT` and its
 * hand-off to the operation view, the operation view's poll to `Succeeded`, the blade's read and
 * its two-step delete, the list's `skipToken` paging, and the two scope creates. Each request is
 * asserted by method, path and body, because a page that sends the right verb to the wrong path
 * is the failure the generated client's per-type methods exist to prevent.
 */
const TENANT = 't-acme';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';
const GROUP = 'example-rg';
const OPERATION = '9c1d2b6f-8a7e-5d43-0f9a-1c2e4b7d4e3a';
const WIDGET_PATH = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets`;
const V = `api-version=${apiVersion}`;

const document = readFileSync(
  join(__dirname, '..', '..', '..', '..', '..', 'generated', 'forms', `${apiVersion}.json`),
  'utf8'
);

const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  rules: { 'color-contrast': { enabled: false } }
};

@Component({
  selector: 'cc-pages-host',
  imports: [RouterOutlet],
  template: '<main><router-outlet /></main>'
})
class Host {}

describe('the portal pages, signed in', () => {
  let fixture: ComponentFixture<Host>;
  let router: Router;
  let http: HttpTestingController;
  let context: TenantContextStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(appRoutes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: OPERATION_POLL_MS, useValue: 5 }
      ]
    });

    router = TestBed.inject(Router);
    http = TestBed.inject(HttpTestingController);
    context = TestBed.inject(TenantContextStore);
    context.load(
      [{ id: TENANT, displayName: 'Acme' }],
      [{ id: SUBSCRIPTION, tenantId: TENANT, displayName: 'Acme Production' }]
    );
    context.selectTenant(TENANT);

    fixture = TestBed.createComponent(Host);
  });

  afterEach(() => http.verify());

  const host = (): HTMLElement => fixture.nativeElement as HTMLElement;

  /**
   * Lets a flushed response reach the page. `flush` resolves the request synchronously, but the
   * page's `await` and the signal it sets are a microtask chain, and the zoneless scheduler paints
   * on a macrotask — so one turn of the event loop, then stability.
   */
  async function settle(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  async function open(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await settle();
  }

  /** Answers the form-document fetch, if the page made one. */
  async function serveForms(): Promise<void> {
    for (const request of http.match(r => r.url === `/forms/${apiVersion}.json`)) request.flush(JSON.parse(document));
    await settle();
  }

  /** Waits for the next poll of the operation view and answers it. */
  async function answerPoll(body: object): Promise<void> {
    // ⚠ `match` removes what it finds, so the found request is the one flushed.
    let found = http.match(r => r.url === `/api/operations/${OPERATION}`);
    for (let i = 0; i < 40 && found.length === 0; i++) {
      await new Promise(resolve => setTimeout(resolve, 5));
      found = http.match(r => r.url === `/api/operations/${OPERATION}`);
    }
    expect(found).toHaveLength(1);
    found[0].flush(body);
    await settle();
  }

  /** Fills the widget form's three required fields. */
  function fillWidget(): void {
    type('[data-pointer="/location"] input', 'eu-central');
    type('[data-pointer="/properties/clusterId"] input', '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
    type('[data-pointer="/properties/message"] input', 'hello');
  }

  function type(selector: string, value: string): void {
    const input = host().querySelector<HTMLInputElement>(selector);
    if (input === null) throw new Error(`no input at ${selector}`);
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function click(text: string): void {
    const button = [...host().querySelectorAll<HTMLElement>('button, a')].find(b => b.textContent?.trim() === text);
    if (button === undefined) throw new Error(`no button "${text}"`);
    button.click();
  }

  describe('the create blade', () => {
    it('renders the generated form for the type, and passes the accessibility gate', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/create/CyberCloud.Sample/widgets`);
      await serveForms();

      expect(host().querySelector('h1')?.textContent?.trim()).toBe('Create Widget');
      expect(host().querySelector('[data-pointer="/location"] input')).not.toBeNull();
      expect(host().querySelector('[data-pointer="/properties/clusterId"] input')).not.toBeNull();

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('PUTs through the generated client and hands the 202 to the operation view', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/create/CyberCloud.Sample/widgets`);
      await serveForms();

      type('#cc-resource-name', 'w1');
      fillWidget();
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = http.expectOne(r => r.method === 'PUT' && r.url === `${WIDGET_PATH}/w1`);
      expect(put.request.params.get('api-version')).toBe(apiVersion);
      const body = put.request.body as { location: string; properties: Record<string, unknown> };
      expect(body.location).toBe('eu-central');
      expect(body.properties['clusterId']).toBe('0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');

      put.flush(null, {
        status: 202,
        statusText: 'Accepted',
        headers: { 'Azure-AsyncOperation': `https://api.example/operations/${OPERATION}?${V}`, 'Retry-After': '2' }
      });
      await settle();

      expect(router.url).toBe(
        `/operations/${OPERATION}?then=${encodeURIComponent(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`)}`
      );

      // The operation view polls straight away; answer it so nothing is left in flight.
      await answerPoll({ status: 'Succeeded' });
    });

    it('puts a refusal on the field the platform named', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/create/CyberCloud.Sample/widgets`);
      await serveForms();

      type('#cc-resource-name', 'w1');
      fillWidget();
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      http
        .expectOne(r => r.method === 'PUT')
        .flush(
          {
            error: {
              code: 'SchemaInvalid',
              message: 'refused',
              details: [{ code: 'SchemaInvalid', message: 'no such region', target: '/location' }]
            }
          },
          { status: 400, statusText: 'Bad Request' }
        );
      await settle();

      expect(host().querySelector('[data-pointer="/location"] [role="alert"]')?.textContent?.trim()).toBe(
        'no such region'
      );
      expect(router.url).toContain('/create/');
    });

    it('shows a name conflict on the name field', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/create/CyberCloud.Sample/widgets`);
      await serveForms();

      type('#cc-resource-name', 'w1');
      fillWidget();
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      http
        .expectOne(r => r.method === 'PUT')
        .flush(
          { error: { code: 'ResourceAlreadyExists', message: 'w1 exists' } },
          { status: 409, statusText: 'Conflict' }
        );
      await settle();

      expect(host().querySelector('#cc-resource-name-error')?.textContent?.trim()).toBe('w1 exists');
    });
  });

  describe('the operation view', () => {
    it('polls until terminal, shows the steps, and feeds the tray', async () => {
      await open(
        `/operations/${OPERATION}?then=/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`
      );

      await answerPoll({
        status: 'Running',
        percentComplete: 40,
        progress: [{ at: '2026-09-15T08:00:00Z', step: 'applying', message: '2 of 5 objects', percentComplete: 40 }]
      });

      expect(host().querySelector('[data-status]')?.getAttribute('data-status')).toBe('Running');
      expect(host().querySelectorAll('xui-timeline-item')).toHaveLength(1);
      expect([...host().querySelectorAll('a')].some(a => a.textContent?.includes('Open the resource'))).toBe(false);

      const tray = TestBed.inject(NotificationsStore);
      expect(tray.items()[0]).toEqual(
        expect.objectContaining({ id: OPERATION, status: 'running', percentComplete: 40 })
      );

      await answerPoll({
        status: 'Succeeded',
        percentComplete: 100,
        progress: [
          { at: '2026-09-15T08:00:00Z', step: 'applying', message: '2 of 5 objects', percentComplete: 40 },
          { at: '2026-09-15T08:00:05Z', step: 'ready', percentComplete: 100 }
        ]
      });

      expect(host().querySelector('[data-status]')?.getAttribute('data-status')).toBe('Succeeded');
      expect(host().querySelectorAll('xui-timeline-item')).toHaveLength(2);
      const link = [...host().querySelectorAll('a')].find(a => a.textContent?.includes('Open the resource'));
      expect(link?.getAttribute('href')).toBe(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`
      );
      expect(tray.items()[0]).toEqual(expect.objectContaining({ id: OPERATION, status: 'succeeded' }));

      // Terminal: no further poll is scheduled.
      await new Promise(resolve => setTimeout(resolve, 30));
      http.expectNone(r => r.url === `/api/operations/${OPERATION}`);
    });

    it('shows the platform error on Failed and never offers the resource', async () => {
      await open(`/operations/${OPERATION}?then=/somewhere`);
      await answerPoll({ status: 'Failed', error: { code: 'QuotaExceeded', message: 'Out of widgets.' } });

      expect(host().textContent).toContain('Failed (QuotaExceeded)');
      expect(host().textContent).toContain('Out of widgets.');
      expect([...host().querySelectorAll('a')].some(a => a.textContent?.includes('Open the resource'))).toBe(false);
    });

    it('refuses a foreign `then`', async () => {
      await open(`/operations/${OPERATION}?then=https://evil.example/`);
      await answerPoll({ status: 'Succeeded' });

      expect([...host().querySelectorAll('a')].some(a => a.textContent?.includes('Open the resource'))).toBe(false);
    });
  });

  describe('the resource blade', () => {
    const served = {
      id: `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`,
      name: 'w1',
      type: 'CyberCloud.Sample/widgets',
      location: 'eu-central',
      provisioningState: 'Succeeded',
      etag: '"7"',
      // Issue #72's shape, as the gateway serves it today.
      properties: {
        location: 'eu-central',
        properties: {
          clusterId: '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43',
          message: 'hello',
          replicas: 2,
          tier: 'Standard'
        }
      },
      tags: { env: 'prod' }
    };

    it('reads the resource, labels its properties by the schema, and passes the accessibility gate', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`);
      await serveForms();
      http.expectOne(r => r.method === 'GET' && r.url === `${WIDGET_PATH}/w1`).flush(served);
      await settle();

      expect(host().querySelector('[data-provisioning-state]')?.textContent?.trim()).toBe('Succeeded');
      expect(host().textContent).toContain('env=prod');

      // The inner object is what the schema's pointers apply to; the raw panel shows the truth.
      const rows = [...host().querySelectorAll('dl dt')].map(dt => dt.textContent?.trim());
      expect(rows).toEqual(expect.arrayContaining(['Cluster id', 'Message', 'Replicas', 'Tier']));
      expect(host().querySelector('pre')?.textContent).toContain('"properties": {');

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('deletes only once the name is typed back, then hands off to the operation view', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`);
      await serveForms();
      http.expectOne(r => r.method === 'GET' && r.url === `${WIDGET_PATH}/w1`).flush(served);
      await settle();

      click('Delete');
      await settle();

      const confirm = [...host().querySelectorAll<HTMLButtonElement>('button')].find(
        b => b.textContent?.trim() === 'Delete' && b.closest('xui-callout') !== null
      );
      expect(confirm?.disabled).toBe(true);
      http.expectNone(r => r.method === 'DELETE');

      type('#cc-delete-confirm', 'w1');
      await settle();
      expect(confirm?.disabled).toBe(false);
      confirm?.click();
      await settle();

      http
        .expectOne(r => r.method === 'DELETE' && r.url === `${WIDGET_PATH}/w1`)
        .flush(null, {
          status: 202,
          statusText: 'Accepted',
          headers: { 'Azure-AsyncOperation': `https://api.example/operations/${OPERATION}?${V}` }
        });
      await settle();

      expect(router.url).toBe(
        `/operations/${OPERATION}?then=${encodeURIComponent(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`)}`
      );
      await answerPoll({ status: 'Succeeded' });
    });

    it('says not found rather than rendering nothing', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/missing`);
      await serveForms();
      http
        .expectOne(r => r.method === 'GET')
        .flush(
          { error: { code: 'ResourceNotFound', message: 'No widget named missing.' } },
          { status: 404, statusText: 'Not Found' }
        );
      await settle();

      expect(host().textContent).toContain('Not found');
      expect(host().textContent).toContain('No widget named missing.');
    });
  });

  describe('the edit blade', () => {
    it('loads the body into the form, locks what cannot change, and PUTs the whole thing back', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1/edit`);
      await serveForms();
      http
        .expectOne(r => r.method === 'GET' && r.url === `${WIDGET_PATH}/w1`)
        .flush({
          id: 'x',
          name: 'w1',
          type: 'CyberCloud.Sample/widgets',
          location: 'eu-central',
          provisioningState: 'Succeeded',
          etag: '"7"',
          properties: { clusterId: '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43', message: 'hello', replicas: 2 },
          tags: { env: 'prod' }
        });
      await settle();

      const location = host().querySelector<HTMLInputElement>('[data-pointer="/location"] input');
      expect(location?.value).toBe('eu-central');
      expect(location?.disabled).toBe(true);

      type('[data-pointer="/properties/message"] input', 'changed');
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = http.expectOne(r => r.method === 'PUT' && r.url === `${WIDGET_PATH}/w1`);
      const body = put.request.body as {
        location: string;
        properties: Record<string, unknown>;
        tags: Record<string, string>;
      };
      expect(body.location).toBe('eu-central');
      expect(body.properties['message']).toBe('changed');
      expect(body.properties['replicas']).toBe(2);
      expect(body.tags).toEqual({ env: 'prod' });
      expect(body).not.toHaveProperty('etag');
      expect(body).not.toHaveProperty('provisioningState');

      put.flush(null, {
        status: 202,
        statusText: 'Accepted',
        headers: { 'Azure-AsyncOperation': `https://api.example/operations/${OPERATION}` }
      });
      await settle();
      expect(router.url).toContain(`/operations/${OPERATION}`);
      await answerPoll({ status: 'Succeeded' });
    });
  });

  describe('the resource list', () => {
    it('lists one type per group and pages by skipToken, never by nextLink', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/resources?type=CyberCloud.Sample%2Fwidgets`);
      await serveForms();

      const first = http.expectOne(r => r.method === 'GET' && r.url === WIDGET_PATH);
      expect(first.request.params.has('$skipToken')).toBe(false);
      first.flush({
        value: [
          {
            id: '/…/w1',
            name: 'w1',
            type: 'CyberCloud.Sample/widgets',
            location: 'eu-central',
            provisioningState: 'Succeeded',
            tags: { env: 'prod' }
          }
        ],
        nextLink: `https://api.example${WIDGET_PATH.slice(4)}?${V}&$skipToken=w1`
      });
      await settle();

      expect(host().querySelectorAll('xui-tr')).toHaveLength(2);
      expect(host().textContent).toContain('env=prod');

      click('Load more');
      await settle();

      const second = http.expectOne(r => r.method === 'GET' && r.url === WIDGET_PATH);
      expect(second.request.params.get('$skipToken')).toBe('w1');
      second.flush({
        value: [{ id: '/…/w2', name: 'w2', type: 'CyberCloud.Sample/widgets', provisioningState: 'Running' }]
      });
      await settle();

      expect(host().querySelectorAll('xui-tr')).toHaveLength(3);
      expect(host().textContent).toContain('No further pages.');
      expect([...host().querySelectorAll('button')].some(b => b.textContent?.includes('Load more'))).toBe(false);
    });

    it('names the empty state honestly', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/resources?type=CyberCloud.Sample%2Fwidgets`);
      await serveForms();
      http.expectOne(r => r.url === WIDGET_PATH).flush({ value: [] });
      await settle();

      expect(host().textContent).toContain('No Widgets here');
    });
  });

  describe('the scope pages', () => {
    it('creates a subscription with the generated scope form and lands on its blade', async () => {
      await open('/subscriptions');
      click('New subscription');
      await settle();
      await serveForms();

      type('#cc-sub-id', '1f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
      type('[data-pointer="/displayName"] input', 'Acme Staging');
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = http.expectOne(
        r => r.method === 'PUT' && r.url === `/api/tenants/${TENANT}/subscriptions/1f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43`
      );
      expect(put.request.body).toEqual({ displayName: 'Acme Staging' });
      put.flush(
        {
          id: '/tenants/t-acme/subscriptions/1f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43',
          name: 'Acme Staging',
          type: 'CyberCloud.Resources/subscriptions'
        },
        { status: 201, statusText: 'Created' }
      );
      await settle();

      expect(router.url).toBe('/subscriptions/1f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
      expect(context.subscriptions().map(s => s.displayName)).toContain('Acme Staging');

      http
        .expectOne(
          r =>
            r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions/1f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43`
        )
        .flush({
          id: 'x',
          name: 'Acme Staging',
          type: 'CyberCloud.Resources/subscriptions'
        });
      await settle();
      expect(host().querySelector('h1')?.textContent?.trim()).toBe('Acme Staging');
    });

    it('creates a resource group and says there is no list endpoint', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups`);
      expect(host().textContent).toContain('No list endpoint yet');

      click('New resource group');
      await settle();
      await serveForms();

      type('#cc-rg-name', 'new-rg');
      type('[data-pointer="/location"] input', 'eu-central');
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = http.expectOne(
        r =>
          r.method === 'PUT' && r.url === `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/new-rg`
      );
      expect(put.request.body).toEqual({ location: 'eu-central' });
      put.flush(
        { id: 'x', name: 'new-rg', type: 'CyberCloud.Resources/subscriptions/resourceGroups', location: 'eu-central' },
        { status: 201, statusText: 'Created' }
      );
      await settle();

      expect(router.url).toBe(`/subscriptions/${SUBSCRIPTION}/resourceGroups/new-rg`);
      await serveForms();
      http
        .expectOne(
          r =>
            r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/new-rg`
        )
        .flush({
          id: 'x',
          name: 'new-rg',
          type: 'CyberCloud.Resources/subscriptions/resourceGroups',
          location: 'eu-central'
        });
      await settle();

      // The group blade offers every top-level type, and no child type.
      const offered = [...host().querySelectorAll('li span')].map(s => s.textContent?.trim());
      expect(offered).toContain('CyberCloud.Sample/widgets');
      expect(offered).not.toContain('CyberCloud.ContainerService/managedClusters/agentPools');
    });
  });
});
