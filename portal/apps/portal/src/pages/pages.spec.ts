import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { AccessTokenStore, NotificationsStore, TenantContextStore } from '@cybercloud/shell';
import axe from 'axe-core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { appRoutes } from '../app/app.routes';
import { TERMINAL_HUB_FACTORY, TerminalHubConnection } from '../app/terminal/terminal-hub';
import { TERMINAL_SCREEN_FACTORY, TerminalScreen } from '../app/terminal/terminal-pane';
import { toBase64 } from '../app/terminal/terminal-session';
import { OPERATION_POLL_MS } from './operations/operation-view';

/**
 * The pages, driven end to end against a recorded platform.
 *
 * `a11y.spec.ts` walks every route with a token and no tenant; this signs in — a tenant in `TenantContextStore`,
 * the real form document served at `/forms/{apiVersion}.json`, and `HttpTestingController`
 * playing the gateway — and exercises what each page does: the create blade's `PUT` and its
 * hand-off to the operation view, the operation view's poll to `Succeeded`, the blade's read and
 * its two-step delete, the list's `skipToken` paging, the two scope creates, the access page's
 * grant, check and revoke over the one hand-written address, and the cloud shell's list → connect
 * → ticket → hub, with a scripted hub and a recording screen in place of SignalR and xterm. Each
 * request is asserted by method, path and body, because a page that sends the right verb to the
 * wrong path is the failure the generated client's per-type methods exist to prevent — and, for
 * the access page and the ticket, the failure nothing generated can prevent.
 */
const TENANT = 't-acme';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';
const GROUP = 'example-rg';
const OPERATION = '9c1d2b6f-8a7e-5d43-0f9a-1c2e4b7d4e3a';
const WIDGET_PATH = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets`;
const ROLE_ASSIGNMENTS = '/providers/CyberCloud.Authorization/roleAssignments';
const RITA = '7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d';
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

/** The hub the terminal page reaches, scripted: every method is recorded, and answered as told. */
class ScriptedHub implements TerminalHubConnection {
  readonly invocations: { method: string; args: unknown[] }[] = [];
  private readonly handlers = new Map<string, (...args: unknown[]) => void>();
  stopped = false;

  constructor(
    readonly url: string,
    readonly refuse: string | null
  ) {}

  start(): Promise<void> {
    return Promise.resolve();
  }
  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }
  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invocations.push({ method, args });
    return this.refuse === null ? Promise.resolve(undefined) : Promise.reject(new Error(this.refuse));
  }
  on(method: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(method, handler);
  }
  onclose(): void {
    // The scripted socket never drops; `terminal-session.spec.ts` covers the ladder.
  }
  output(text: string): void {
    this.handlers.get('Output')?.(toBase64(new TextEncoder().encode(text)));
  }
}

/** The screen the pane mounts, recording what it is told to paint and typing on request. */
class RecordingScreen implements TerminalScreen {
  readonly painted: string[] = [];
  private typed: ((data: string) => void) | null = null;
  open(): void {
    // Nothing to lay out in jsdom.
  }
  write(data: Uint8Array | string): void {
    this.painted.push(typeof data === 'string' ? data : new TextDecoder().decode(data));
  }
  notice(text: string): void {
    this.painted.push(`— ${text} —`);
  }
  onData(handler: (data: string) => void): void {
    this.typed = handler;
  }
  fit(): { cols: number; rows: number } {
    return { cols: 132, rows: 43 };
  }
  focus(): void {
    // Nothing to focus in jsdom.
  }
  dispose(): void {
    this.typed = null;
  }
  type(data: string): void {
    this.typed?.(data);
  }
}

describe('the portal pages, signed in', () => {
  let fixture: ComponentFixture<Host>;
  let router: Router;
  let http: HttpTestingController;
  let context: TenantContextStore;
  let hubs: ScriptedHub[];
  let hubRefusal: string | null;
  let screens: RecordingScreen[];

  beforeEach(() => {
    hubs = [];
    hubRefusal = null;
    screens = [];

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(appRoutes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: OPERATION_POLL_MS, useValue: 5 },
        {
          provide: TERMINAL_HUB_FACTORY,
          useValue: (url: string) => {
            const hub = new ScriptedHub(url, hubRefusal);
            hubs.push(hub);
            return Promise.resolve(hub);
          }
        },
        {
          provide: TERMINAL_SCREEN_FACTORY,
          useValue: () => {
            const screen = new RecordingScreen();
            screens.push(screen);
            return Promise.resolve(screen);
          }
        }
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
    // `authGuard` wants a token in memory; the context above is what the sign-in would have loaded.
    TestBed.inject(AccessTokenStore).set('signed-in', Date.now() + 600_000);

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

  describe('the access page', () => {
    const GROUP_SCOPE = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`;

    /** What `ResponseBodies.RoleAssignment` writes for a name at a scope. */
    function served(scope: string, name: string) {
      const [role, principalType, ...id] = name.split('-');
      return {
        id: `${scope}${ROLE_ASSIGNMENTS}/${name}`,
        name,
        type: 'CyberCloud.Authorization/roleAssignments',
        properties: { scope, principalId: id.join('-'), principalType, roleDefinitionId: role }
      };
    }

    const rows = (): string[] =>
      [...host().querySelectorAll<HTMLElement>('xui-tr[data-assignment]')].map(
        tr => tr.getAttribute('data-assignment') ?? ''
      );
    const outcome = (): string | null => host().querySelector('[data-outcome]')?.getAttribute('data-outcome') ?? null;

    it('derives the name from the three choices, and passes the accessibility gate', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);

      expect(host().querySelector('h1')?.textContent?.trim()).toBe('Access control');
      expect(host().textContent).toContain('No list endpoint yet');
      expect(host().textContent).toContain('issue #86');
      expect(host().querySelector('[data-assignment-name]')?.getAttribute('data-assignment-name')).toBe('');

      type('#cc-access-principal-id', RITA);
      await settle();
      expect(host().querySelector('[data-assignment-name]')?.getAttribute('data-assignment-name')).toBe(
        `reader-user-${RITA}`
      );

      // The role is a radio; `-` in the id belongs to the id and never to the split.
      host().querySelector<HTMLInputElement>('input[type=radio][value=contributor]')?.click();
      type('#cc-access-principal-id', 'eng-platform');
      await settle();
      expect(host().querySelector('[data-assignment-name]')?.getAttribute('data-assignment-name')).toBe(
        'contributor-user-eng-platform'
      );

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('refuses to send an id the platform would refuse, and says why on the field', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);

      type('#cc-access-principal-id', 'Rita');
      click('Assign role');
      await settle();

      http.expectNone(r => r.method === 'PUT');
      expect(host().querySelector('#cc-access-principal-id-error')?.textContent).toContain('1–63 lowercase');
    });

    it('grants on a resource group: PUT to the derived address, 201, a row, and 200 on a repeat', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);

      type('#cc-access-principal-id', RITA);
      click('Assign role');
      await settle();

      const name = `reader-user-${RITA}`;
      const put = http.expectOne(r => r.method === 'PUT' && r.url === `${GROUP_SCOPE}${ROLE_ASSIGNMENTS}/${name}`);
      expect(put.request.params.get('api-version')).toBe(apiVersion);
      expect(put.request.body).toEqual({ principalId: RITA, principalType: 'user', roleDefinitionId: 'reader' });
      put.flush(served(GROUP_SCOPE.slice(4), name), { status: 201, statusText: 'Created' });
      await settle();

      expect(outcome()).toBe('granted');
      expect(rows()).toEqual([name]);
      expect(router.url).toContain('/access');

      click('Assign role');
      await settle();
      http
        .expectOne(r => r.method === 'PUT' && r.url === `${GROUP_SCOPE}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(served(GROUP_SCOPE.slice(4), name), { status: 200, statusText: 'OK' });
      await settle();

      expect(outcome()).toBe('repeated');
      // One row: the same name is the same assignment.
      expect(rows()).toEqual([name]);
    });

    it('checks by name: a 404 is "not assigned", a 200 is a row', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/access`);
      const scope = `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}`;
      const name = `reader-user-${RITA}`;

      type('#cc-access-principal-id', RITA);
      click('Check');
      await settle();

      http
        .expectOne(r => r.method === 'GET' && r.url === `/api${scope}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(
          { error: { code: 'ResourceNotFound', message: `'${scope}${ROLE_ASSIGNMENTS}/${name}' does not exist.` } },
          { status: 404, statusText: 'Not Found' }
        );
      await settle();

      expect(outcome()).toBe('absent');
      expect(host().textContent).toContain('Not assigned');
      expect(rows()).toEqual([]);

      click('Check');
      await settle();
      http
        .expectOne(r => r.method === 'GET' && r.url === `/api${scope}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(served(scope, name));
      await settle();

      expect(outcome()).toBe('assigned');
      expect(rows()).toEqual([name]);
    });

    it('revokes only after a second click, on a child resource with the parent in the address', async () => {
      await open(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.ContainerService/managedClusters/c1/agentPools/p1/access`
      );
      const scope = `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.ContainerService/managedClusters/c1/agentPools/p1`;
      const name = `reader-user-${RITA}`;

      type('#cc-access-principal-id', RITA);
      click('Assign role');
      await settle();
      http
        .expectOne(r => r.method === 'PUT' && r.url === `/api${scope}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(served(scope, name), { status: 201, statusText: 'Created' });
      await settle();
      expect(rows()).toEqual([name]);

      click('Remove');
      await settle();
      http.expectNone(r => r.method === 'DELETE');
      expect(host().textContent).toContain('Revoke this role?');

      click('Keep it');
      await settle();
      expect(host().textContent).not.toContain('Revoke this role?');

      click('Remove');
      await settle();
      click('Revoke');
      await settle();

      http
        .expectOne(r => r.method === 'DELETE' && r.url === `/api${scope}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      expect(outcome()).toBe('revoked');
      expect(rows()).toEqual([]);
      expect(host().textContent).toContain('No list endpoint yet');
    });

    it('shows the platform refusal on a grant, and keeps no row for it', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);

      type('#cc-access-principal-id', RITA);
      click('Assign role');
      await settle();
      http
        .expectOne(r => r.method === 'PUT')
        .flush(
          { error: { code: 'Forbidden', message: 'assignRole is not held on this scope.' } },
          { status: 403, statusText: 'Forbidden' }
        );
      await settle();

      expect(outcome()).toBe('failed');
      expect(host().textContent).toContain('assignRole is not held on this scope.');
      expect(rows()).toEqual([]);
    });

    it('forgets what it knows when the tenant switches, and drops an answer that lands after one', async () => {
      context.load(
        [
          { id: TENANT, displayName: 'Acme' },
          { id: 't-other', displayName: 'Other' }
        ],
        [{ id: SUBSCRIPTION, tenantId: TENANT, displayName: 'Acme Production' }]
      );
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);
      const name = `reader-user-${RITA}`;
      const otherScope = `/tenants/t-other/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`;

      type('#cc-access-principal-id', RITA);
      click('Assign role');
      await settle();
      http
        .expectOne(r => r.method === 'PUT' && r.url === `${GROUP_SCOPE}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(served(GROUP_SCOPE.slice(4), name), { status: 201, statusText: 'Created' });
      await settle();
      expect(rows()).toEqual([name]);
      expect(outcome()).toBe('granted');

      // The context bar's switch moves the scope without moving the route. What was granted on
      // Acme is not a row on Other, and a Remove here would have sent Acme's name down Other's path.
      context.selectTenant('t-other');
      await settle();
      expect(router.url).toContain('/access');
      expect(rows()).toEqual([]);
      expect(outcome()).toBeNull();
      expect(host().textContent).toContain('No list endpoint yet');

      // A check that leaves for Other and is answered after a switch back to Acme is Other's answer.
      click('Check');
      await settle();
      const pending = http.expectOne(
        r => r.method === 'GET' && r.url === `/api${otherScope}${ROLE_ASSIGNMENTS}/${name}`
      );
      context.selectTenant(TENANT);
      await settle();
      pending.flush(served(otherScope, name));
      await settle();
      expect(rows()).toEqual([]);
      expect(outcome()).toBeNull();

      // And the page is usable again: the next grant goes to Acme's path and lands.
      click('Assign role');
      await settle();
      http
        .expectOne(r => r.method === 'PUT' && r.url === `${GROUP_SCOPE}${ROLE_ASSIGNMENTS}/${name}`)
        .flush(served(GROUP_SCOPE.slice(4), name), { status: 201, statusText: 'Created' });
      await settle();
      expect(rows()).toEqual([name]);
    });

    it('is reached from the three blades', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}`);
      http
        .expectOne(r => r.method === 'GET')
        .flush({ id: 'x', name: 'Acme', type: 'CyberCloud.Resources/subscriptions' });
      await settle();
      click('Access');
      await settle();
      expect(router.url).toBe(`/subscriptions/${SUBSCRIPTION}/access`);

      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`);
      await serveForms();
      http
        .expectOne(r => r.method === 'GET')
        .flush({ id: 'x', name: GROUP, type: 'CyberCloud.Resources/subscriptions/resourceGroups' });
      await settle();
      click('Access');
      await settle();
      expect(router.url).toBe(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/access`);

      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1`);
      await serveForms();
      http
        .expectOne(r => r.method === 'GET' && r.url === `${WIDGET_PATH}/w1`)
        .flush({ id: 'x', name: 'w1', type: 'CyberCloud.Sample/widgets', properties: {} });
      await settle();
      click('Access');
      await settle();
      expect(router.url).toBe(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Sample/widgets/w1/access`
      );
      expect(host().textContent).toContain('Resource');
      expect(host().querySelector('h1')?.textContent?.trim()).toBe('Access control');
    });
  });

  describe('the scope pages', () => {
    const SUBSCRIPTIONS = `/api/tenants/${TENANT}/subscriptions`;
    const GROUPS = `${SUBSCRIPTIONS}/${SUBSCRIPTION}/resourceGroups`;

    it('lists the subscriptions the collection answers, by display name, linking each by its id', async () => {
      await open('/subscriptions');

      // The scope collection — GET only, `{ value, nextLink }`, filtered to what the caller may
      // read (the contract's § 6). The id is the address's last segment; `name` is what a person reads.
      http
        .expectOne(r => r.method === 'GET' && r.url === SUBSCRIPTIONS)
        .flush({
          value: [
            {
              id: `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}`,
              name: 'Acme Production',
              type: 'CyberCloud.Resources/subscriptions'
            },
            {
              id: `/tenants/${TENANT}/subscriptions/2f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43`,
              name: '2f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43',
              type: 'CyberCloud.Resources/subscriptions',
              properties: { displayName: 'Default' }
            }
          ]
        });
      await settle();

      const rows = [...host().querySelectorAll('li a')].map(a => [a.textContent?.trim(), a.getAttribute('href')]);
      expect(rows).toEqual([
        ['Acme Production', `/subscriptions/${SUBSCRIPTION}`],
        ['Default', '/subscriptions/2f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43']
      ]);
    });

    it('says so when the collection is empty, and shows the platform’s refusal when it is one', async () => {
      await open('/subscriptions');
      http.expectOne(r => r.method === 'GET' && r.url === SUBSCRIPTIONS).flush({ value: [] });
      await settle();
      expect(host().textContent).toContain('No subscriptions');

      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups`);
      http
        .expectOne(r => r.method === 'GET' && r.url === GROUPS)
        .flush(
          { error: { code: 'ResourceNotFound', message: 'The subscription was not found.' } },
          { status: 404, statusText: 'Not Found' }
        );
      await settle();
      expect(host().textContent).toContain('The subscription was not found.');
    });

    it('creates a subscription with the generated scope form and lands on its blade', async () => {
      await open('/subscriptions');
      http.expectOne(r => r.method === 'GET' && r.url === SUBSCRIPTIONS).flush({ value: [] });
      await settle();
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

    it('lists the resource groups the collection answers and creates one', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups`);
      http
        .expectOne(r => r.method === 'GET' && r.url === GROUPS)
        .flush({
          value: [
            {
              id: `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`,
              name: GROUP,
              type: 'CyberCloud.Resources/subscriptions/resourceGroups',
              location: 'local'
            }
          ]
        });
      await settle();

      const rows = [...host().querySelectorAll('li a')].map(a => [a.textContent?.trim(), a.getAttribute('href')]);
      expect(rows).toEqual([[GROUP, `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`]]);

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
  describe('the cloud shell', () => {
    const CONSOLES = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Terminal/consoles`;
    const TICKET = '/api/hubs/terminal/ticket';
    const PAGE = `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/terminal`;

    const aConsole = (name: string) => ({
      id: `${CONSOLES.slice(4)}/${name}`,
      name,
      type: 'CyberCloud.Terminal/consoles',
      provisioningState: 'Succeeded',
      etag: '"1"',
      location: 'eu-central',
      properties: { clusterId: SUBSCRIPTION, home: { size: '5Gi' }, identity: { principalId: RITA } }
    });

    const connected = (sessionId: string, recording = false) => ({
      sessionId,
      hub: '/hubs/terminal',
      state: 'Ready',
      idleTimeoutSeconds: 1200,
      maxDurationSeconds: 28_800,
      recording
    });

    const sessionState = (): string | null =>
      host().querySelector('[data-session-state]')?.getAttribute('data-session-state') ?? null;

    /** Lists the group's consoles, then answers the connect and the ticket the page sends for one. */
    async function openWith(consoles: object[], session = connected('pod-uid-1'), page = PAGE): Promise<void> {
      await open(page);
      http.expectOne(r => r.method === 'GET' && r.url === CONSOLES).flush({ value: consoles });
      await settle();

      if (consoles.length === 0) return;

      const connect = http.expectOne(r => r.method === 'POST' && r.url.endsWith('/connect'));
      connect.flush(session);
      await settle();
      const ticket = http.expectOne(r => r.method === 'POST' && r.url === TICKET);
      // ⚠ The mint is authenticated the way every call is — the interceptor is not in this
      // TestBed, so what is asserted is that nothing else carries a credential: the socket URL.
      ticket.flush({ ticket: 'tk-1', hub: '/hubs/terminal', expiresAt: '2026-08-11T12:00:30Z' });
      await settle();
      await settle();
    }

    it('lists the consoles, opens the first, and reaches the hub with a ticket rather than the token', async () => {
      await openWith([aConsole('shell'), aConsole('other')]);

      expect([...host().querySelectorAll('[data-console]')].map(b => b.getAttribute('data-console'))).toEqual([
        'shell',
        'other'
      ]);
      expect(host().querySelector('[data-console="shell"]')?.getAttribute('aria-pressed')).toBe('true');

      expect(hubs).toHaveLength(1);
      expect(hubs[0].url).toBe('ws://localhost/api/hubs/terminal?ticket=tk-1');
      expect(hubs[0].url).not.toContain('signed-in');
      // The pane's size, as the screen reported it after mount.
      expect(hubs[0].invocations).toEqual([{ method: 'Attach', args: ['pod-uid-1', 132, 43] }]);
      expect(sessionState()).toBe('attached');
      expect(host().textContent).toContain('Connected');
      expect(host().textContent).toContain('Reclaimed after 20 min idle');
      expect(host().querySelector('[data-recording]')).toBeNull();

      // Output lands on the screen; keystrokes go to the hub.
      hubs[0].output('$ ');
      expect(screens[0].painted).toContain('$ ');
      screens[0].type('ls\r');
      expect(hubs[0].invocations[1]).toEqual({
        method: 'Send',
        args: ['pod-uid-1', toBase64(new TextEncoder().encode('ls\r'))]
      });

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('opens the console the URL names, and switching consoles is a new connect', async () => {
      await openWith([aConsole('shell'), aConsole('other')], connected('pod-other'), `${PAGE}?console=other`);

      expect(host().querySelector('[data-console="other"]')?.getAttribute('aria-pressed')).toBe('true');
      expect(hubs[0].invocations[0]).toEqual({ method: 'Attach', args: ['pod-other', 132, 43] });

      click('shell');
      await settle();
      expect(router.url).toBe(`${PAGE}?console=shell`);
      http.expectOne(r => r.method === 'POST' && r.url === `${CONSOLES}/shell/connect`).flush(connected('pod-shell'));
      await settle();
      http
        .expectOne(r => r.method === 'POST' && r.url === TICKET)
        .flush({ ticket: 'tk-2', hub: '/hubs/terminal', expiresAt: '' });
      await settle();
      await settle();

      expect(hubs).toHaveLength(2);
      expect(hubs[0].stopped).toBe(true);
      expect(hubs[1].url).toBe('ws://localhost/api/hubs/terminal?ticket=tk-2');
    });

    it('is loud about a recorded session — docs/plan/19 § Auditing', async () => {
      await openWith([aConsole('shell')], connected('pod-uid-1', true));

      expect(host().querySelector('[data-recording]')?.textContent).toContain('being recorded');
    });

    it('shows the hub refusing the session in place, with Reconnect, and retries nothing on its own', async () => {
      hubRefusal =
        "There is no terminal session 'pod-uid-1' for this caller. Call connect on the console for a session id.";
      await openWith([aConsole('shell')]);

      expect(sessionState()).toBe('refused');
      expect(host().querySelector('[data-problem]')?.textContent).toContain('no terminal session');
      expect(hubs[0].stopped).toBe(true);
      expect([...host().querySelectorAll('button')].map(b => b.textContent?.trim())).toContain('Reconnect');

      // Reconnect is a whole new connect and a whole new ticket.
      hubRefusal = null;
      click('Reconnect');
      await settle();
      http.expectOne(r => r.method === 'POST' && r.url === `${CONSOLES}/shell/connect`).flush(connected('pod-uid-1'));
      await settle();
      http
        .expectOne(r => r.method === 'POST' && r.url === TICKET)
        .flush({ ticket: 'tk-2', hub: '/hubs/terminal', expiresAt: '' });
      await settle();
      await settle();
      expect(sessionState()).toBe('attached');
    });

    it('terminates in two clicks, through the terminate action, and closes the pane', async () => {
      await openWith([aConsole('shell')]);

      click('Terminate');
      await settle();
      http.expectNone(r => r.url.endsWith('/terminate'));
      expect(host().textContent).toContain('Stop the shell?');

      click('Stop it');
      await settle();
      http.expectOne(r => r.method === 'POST' && r.url === `${CONSOLES}/shell/terminate`).flush({ terminated: true });
      await settle();

      expect(host().querySelector('[data-outcome]')?.getAttribute('data-outcome')).toBe('terminated');
      expect(hubs[0].stopped).toBe(true);
      expect(sessionState()).toBe('closed');
    });

    it('renders the generated form when the group has no console, and creates one through the PUT', async () => {
      await openWith([]);
      await serveForms();

      expect(host().textContent).toContain('No console in this group');
      expect(host().querySelector('[data-pointer="/properties/clusterId"] input')).not.toBeNull();
      expect(hubs).toHaveLength(0);

      type('#cc-terminal-name', 'shell');
      type('[data-pointer="/location"] input', 'eu-central');
      type('[data-pointer="/properties/clusterId"] input', SUBSCRIPTION);
      type('[data-pointer="/properties/identity/principalId"] input', '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = http.expectOne(r => r.method === 'PUT' && r.url === `${CONSOLES}/shell`);
      const body = put.request.body as { location: string; properties: { clusterId: string; home: { size: string } } };
      expect(body.location).toBe('eu-central');
      expect(body.properties.clusterId).toBe(SUBSCRIPTION);
      expect(body.properties.home.size).toBe('5Gi');

      put.flush(null, {
        status: 202,
        statusText: 'Accepted',
        headers: { 'Azure-AsyncOperation': `https://api.example/operations/${OPERATION}?${V}`, 'Retry-After': '2' }
      });
      await settle();

      // The operation view, then back here with the new console named.
      expect(router.url).toBe(`/operations/${OPERATION}?then=${encodeURIComponent(`${PAGE}?console=shell`)}`);
      await answerPoll({ status: 'Succeeded' });

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help}`)).toEqual([]);
    });

    it('is reached from the resource group blade', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`);
      await serveForms();
      http
        .expectOne(r => r.method === 'GET')
        .flush({ id: 'x', name: GROUP, type: 'CyberCloud.Resources/subscriptions/resourceGroups' });
      await settle();

      click('Cloud shell');
      await settle();
      expect(router.url).toBe(PAGE);
      http.expectOne(r => r.method === 'GET' && r.url === CONSOLES).flush({ value: [] });
      await settle();
      await serveForms();
      expect(host().querySelector('h1')?.textContent?.trim()).toBe('Cloud shell');
    });
  });
});
