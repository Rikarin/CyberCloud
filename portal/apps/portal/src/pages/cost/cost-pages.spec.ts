import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { CHART_ENGINE } from '@cybercloud/charts';
import { AccessTokenStore, TenantContextStore } from '@cybercloud/shell';
import axe from 'axe-core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { appRoutes } from '../../app/app.routes';
import { COST_NOW } from './cost-analysis';

/**
 * The cost pages, signed in and driven against a recorded platform — issue #41.
 *
 * `HttpTestingController` plays the gateway, answering in the shapes `CostQueryBody.Render`,
 * `InvoiceBody.Render` and the generated `showStatus` response write, and every request is asserted
 * by method, path and body. The chart gets a recording engine in place of ECharts: jsdom has no
 * canvas, and what the page owes the chart is the option it builds, which the recording keeps.
 */
const TENANT = 't-acme';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';
const GROUP = 'prod';
const SUB_PATH = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}`;
const GROUP_PATH = `${SUB_PATH}/resourceGroups/${GROUP}`;
const QUERY = '/providers/CyberCloud.CostManagement/query';
const BUDGETS = `${GROUP_PATH}/providers/CyberCloud.Billing/budgets`;
const INVOICES = `/api/tenants/${TENANT}/providers/CyberCloud.CostManagement/invoices`;
const NOW = new Date('2026-09-10T12:00:00Z');
const WEB = `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Compute/virtualMachines/web`;
const IP = `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Network/publicIpAddresses/ip`;

const forms = readFileSync(
  join(__dirname, '..', '..', '..', '..', '..', '..', 'generated', 'forms', `${apiVersion}.json`),
  'utf8'
);

const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  rules: { 'color-contrast': { enabled: false } }
};

@Component({ selector: 'cc-cost-host', imports: [RouterOutlet], template: '<main><router-outlet /></main>' })
class Host {}

/** The ECharts instance the page's chart is handed: records every option, draws nothing. */
class RecordingChart {
  readonly options: Record<string, unknown>[] = [];
  group = '';
  setOption(option: Record<string, unknown>): void {
    this.options.push(option);
  }
  on(): void {
    // No pointer in jsdom.
  }
  showLoading(): void {
    // Nothing to paint.
  }
  hideLoading(): void {
    // Nothing to paint.
  }
  resize(): void {
    // No layout in jsdom.
  }
  dispose(): void {
    // Nothing held.
  }
}

describe('the cost pages, signed in', () => {
  let fixture: ComponentFixture<Host>;
  let router: Router;
  let http: HttpTestingController;
  let charts: RecordingChart[];

  beforeEach(() => {
    charts = [];

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter(appRoutes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: COST_NOW, useValue: () => NOW },
        {
          provide: CHART_ENGINE,
          useValue: () => ({
            init: () => {
              const chart = new RecordingChart();
              charts.push(chart);
              return chart;
            },
            connect: () => undefined
          })
        }
      ]
    });

    router = TestBed.inject(Router);
    http = TestBed.inject(HttpTestingController);
    const context = TestBed.inject(TenantContextStore);
    context.load(
      [{ id: TENANT, displayName: 'Acme' }],
      [{ id: SUBSCRIPTION, tenantId: TENANT, displayName: 'Acme Production' }]
    );
    context.selectTenant(TENANT);
    TestBed.inject(AccessTokenStore).set('signed-in', Date.now() + 600_000);

    fixture = TestBed.createComponent(Host);
  });

  afterEach(() => http.verify());

  const host = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const text = (selector: string): string | undefined =>
    host().querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();

  async function settle(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  async function open(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await settle();
  }

  /** The one request matching, found even when it was sent a turn after the last settle. */
  async function expectOne(match: (r: TestRequest['request']) => boolean): Promise<TestRequest> {
    let found = http.match(match);
    for (let i = 0; i < 20 && found.length === 0; i++) {
      await settle();
      found = http.match(match);
    }
    expect(found).toHaveLength(1);
    return found[0];
  }

  /** The two cost queries a cost page makes: the period's, daily, and the forecast's, by day. */
  async function answerCost(
    path: string,
    period: object,
    forecast: object
  ): Promise<{ period: unknown; forecast: unknown }> {
    const daily = await expectOne(
      r => r.method === 'POST' && r.url === path + QUERY && r.body?.granularity === 'daily'
    );
    const byDay = await expectOne(r => r.method === 'POST' && r.url === path + QUERY && r.body?.groupBy === 'day');
    const bodies = { period: daily.request.body, forecast: byDay.request.body };

    daily.flush(period);
    byDay.flush(forecast);
    await settle();
    return bodies;
  }

  const forecastRows = {
    currency: 'EUR',
    groupBy: 'day',
    granularity: 'none',
    total: 35,
    filtered: false,
    rows: [
      { name: '2026-09-01', amount: 10 },
      { name: '2026-09-02', amount: 10 },
      ...['03', '04', '05', '06', '07', '08', '09'].map(d => ({ name: `2026-09-${d}`, amount: 2 })),
      { name: '2026-09-10', amount: 1 }
    ]
  };

  describe('cost analysis on a resource group', () => {
    it('asks for this month by resource, daily, and shows the total, the forecast, the chart and the table', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/cost?groupBy=resource`);

      const bodies = await answerCost(
        GROUP_PATH,
        {
          currency: 'EUR',
          groupBy: 'resource',
          granularity: 'daily',
          total: 2.75,
          filtered: false,
          rows: [
            { day: '2026-09-01', name: WEB, amount: 2.4 },
            { day: '2026-09-02', name: IP, amount: 0.12 },
            { day: '2026-09-02', name: WEB, amount: 0.1 },
            { day: '2026-09-03', name: IP, amount: 0.13 }
          ]
        },
        forecastRows
      );
      await answerBudgets([]);

      expect(bodies.period).toEqual({
        from: '2026-09-01T00:00:00.000Z',
        to: '2026-10-01T00:00:00.000Z',
        groupBy: 'resource',
        granularity: 'daily'
      });
      // This month and the seven days before today, up to now — the forecast's window.
      expect(bodies.forecast).toEqual({ from: '2026-09-01T00:00:00.000Z', to: NOW.toISOString(), groupBy: 'day' });

      expect(text('[data-figure="total"]')).toBe('€2.75');
      // 35 so far, and 15 over the trailing 180 hours for the 492 left: 76.
      expect(text('[data-figure="forecast"]')).toBe('€76.00');
      expect(text('[data-figure="forecast-method"]')).toContain('€2.00 a day');

      const rows = [...host().querySelectorAll('xui-table')[0].querySelectorAll('xui-tr')]
        .slice(1)
        .map(r => [...r.querySelectorAll('xui-td')].map(c => c.textContent?.trim()));
      expect(rows).toEqual([
        ['virtualMachines/web', '€2.50', '90.9 %'],
        ['publicIpAddresses/ip', '€0.25', '9.1 %']
      ]);

      // The chart was handed the same figures, one series per resource over all thirty days.
      const option = charts.at(-1)?.options.at(-1) as {
        xAxis: { data: string[] };
        series: { name: string; data: number[] }[];
      };
      expect(option.xAxis.data).toHaveLength(30);
      expect(option.series.map(s => s.name)).toEqual(['virtualMachines/web', 'publicIpAddresses/ip']);
      expect(option.series[0].data.slice(0, 3)).toEqual([2.4, 0.1, 0]);

      expect(host().querySelector('xui-echart')?.getAttribute('aria-label')).toContain(
        'The table below lists the same figures'
      );

      const results = await axe.run(host(), WCAG_22_AA);
      expect(
        results.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => n.html.slice(0, 160)).join(' | ')}`)
      ).toEqual([]);
    });

    it('lists the group’s budgets with the threshold state their status reports, and links New and Edit to the generated blades', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/cost`);
      await answerCost(GROUP_PATH, { currency: 'EUR', total: 0, filtered: false, rows: [] }, forecastRows);

      await answerBudgets([budget('monthly', 1.5, [50, 100], [100]), budget('fresh', 20, [80], [])], {
        monthly: {
          evaluated: true,
          currency: 'EUR',
          amount: 1.5,
          actual: 1,
          forecast: 2.1,
          lastEvaluatedAt: '2026-09-10T11:00:00.0000000+00:00',
          lastError: '',
          firedActual: [50],
          firedForecast: [100],
          alerts: []
        },
        fresh: { status: 409, body: { error: { code: 'Conflict', message: "Budget 'fresh' is not held yet." } } }
      });

      const monthly = host().querySelector('[data-budget="monthly"]');
      expect(monthly?.querySelector('[data-figure="actual"]')?.textContent?.trim()).toBe('€1.00');
      expect(monthly?.querySelector('[data-figure="forecast"]')?.textContent?.trim()).toBe('€2.10');
      expect(
        [...(monthly?.querySelectorAll('[data-threshold]') ?? [])].map(t => t.getAttribute('data-threshold'))
      ).toEqual(['actual:50:fired', 'actual:100', 'forecast:100:fired']);
      expect(monthly?.textContent).toContain('2026-09-10 11:00 UTC');

      // A status the platform refused is that row's, and says why.
      expect(host().querySelector('[data-budget="fresh"] [data-figure="not-evaluated"]')?.textContent?.trim()).toBe(
        "Budget 'fresh' is not held yet."
      );

      const hrefs = [...host().querySelectorAll('a')].map(a => a.getAttribute('href'));
      expect(hrefs).toContain(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/create/CyberCloud.Billing/budgets`
      );
      expect(hrefs).toContain(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Billing/budgets/monthly/edit`
      );

      const results = await axe.run(host(), WCAG_22_AA);
      expect(
        results.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => n.html.slice(0, 160)).join(' | ')}`)
      ).toEqual([]);
    });

    it('edits a budget through the generated blade: the whole body back through createOrUpdateBudget', async () => {
      await open(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Billing/budgets/monthly/edit`
      );
      for (const request of http.match(r => r.url === `/forms/${apiVersion}.json`)) request.flush(JSON.parse(forms));
      await settle();
      (await expectOne(r => r.method === 'GET' && r.url === `${BUDGETS}/monthly`)).flush(
        budget('monthly', 1.5, [50, 100], [100])
      );
      await settle();

      const amount = host().querySelector<HTMLInputElement>('[data-pointer="/properties/amount"] input');
      expect(amount?.value).toBe('1.5');
      amount!.value = '750';
      amount!.dispatchEvent(new Event('input'));
      amount!.dispatchEvent(new Event('blur'));
      host().querySelector('cc-resource-form form')?.dispatchEvent(new Event('submit'));
      await settle();

      const put = await expectOne(r => r.method === 'PUT' && r.url === `${BUDGETS}/monthly`);
      const body = put.request.body as { location: string; properties: Record<string, unknown> };
      expect(body.location).toBe('eu-central');
      expect(body.properties['amount']).toBe(750);
      expect(body.properties['thresholds']).toEqual({ actual: [50, 100], forecast: [100] });
      expect(body.properties['notification']).toEqual(budget('monthly', 0, [], []).properties.notification);

      put.flush(null, {
        status: 202,
        statusText: 'Accepted',
        headers: { 'Azure-AsyncOperation': 'https://api.example/operations/op-1' }
      });
      await settle();
      expect(router.url).toContain('/operations/op-1');
      for (const poll of http.match(r => r.url.startsWith('/api/operations/'))) poll.flush({ status: 'Succeeded' });
    });
  });

  describe('cost analysis on a subscription', () => {
    it('says when something was withheld, offers the group dimension, and sends budgets to the groups', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/cost?period=lastMonth&groupBy=resourceGroup`);

      const bodies = await answerCost(
        SUB_PATH,
        {
          currency: 'EUR',
          groupBy: 'resourceGroup',
          granularity: 'daily',
          total: 2.5,
          filtered: true,
          rows: [{ day: '2026-08-01', name: 'prod', amount: 2.5 }]
        },
        forecastRows
      );

      expect(bodies.period).toMatchObject({
        from: '2026-08-01T00:00:00.000Z',
        to: '2026-09-01T00:00:00.000Z',
        groupBy: 'resourceGroup'
      });
      expect(host().textContent).toContain(
        'Some cost in this scope belongs to resources you can’t read'.replace('’', "'")
      );
      expect(host().textContent).toContain('Budgets live in a resource group');
      expect(http.match(r => r.url.includes('CyberCloud.Billing/budgets'))).toEqual([]);
    });

    it('asks nothing for a custom period until both days make sense, then asks for the inclusive range', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}/cost?period=custom&from=2026-08-15&to=2026-08-01`);
      const forecast = await expectOne(r => r.method === 'POST' && r.body?.groupBy === 'day');
      forecast.flush(forecastRows);
      await settle();

      expect(http.match(r => r.method === 'POST')).toEqual([]);
      expect(host().textContent).toContain('Pick a first and a last day');

      await router.navigateByUrl(`/subscriptions/${SUBSCRIPTION}/cost?period=custom&from=2026-08-01&to=2026-08-15`);
      const daily = await expectOne(r => r.method === 'POST' && r.body?.granularity === 'daily');
      expect(daily.request.body).toMatchObject({ from: '2026-08-01T00:00:00.000Z', to: '2026-08-16T00:00:00.000Z' });
      daily.flush({ currency: 'EUR', total: 0, filtered: false, rows: [] });
      await settle();
      for (const again of http.match(r => r.body?.groupBy === 'day')) again.flush(forecastRows);

      expect(host().textContent).toContain('No cost in this period');
    });

    it('is reached from the subscription and group blades', async () => {
      await open(`/subscriptions/${SUBSCRIPTION}`);
      (await expectOne(r => r.url === SUB_PATH)).flush({
        id: 'x',
        name: 'Acme',
        type: 'CyberCloud.Resources/subscriptions'
      });
      await settle();
      expect([...host().querySelectorAll('a')].map(a => a.getAttribute('href'))).toContain(
        `/subscriptions/${SUBSCRIPTION}/cost`
      );

      await open(`/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}`);
      (await expectOne(r => r.url === GROUP_PATH)).flush({
        id: 'x',
        name: GROUP,
        type: 'CyberCloud.Resources/resourceGroups'
      });
      for (const request of http.match(r => r.url === `/forms/${apiVersion}.json`)) request.flush(JSON.parse(forms));
      await settle();
      for (const request of http.match(r => r.method === 'GET' && r.url.startsWith(GROUP_PATH)))
        request.flush({ value: [] });
      await settle();
      expect([...host().querySelectorAll('a')].map(a => a.getAttribute('href'))).toContain(
        `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/cost`
      );
    });
  });

  describe('invoices', () => {
    const august = {
      number: 'CC-INV-00000042',
      status: 'finalized',
      periodStart: '2026-08-01T00:00:00.0000000+00:00',
      periodEnd: '2026-09-01T00:00:00.0000000+00:00',
      finalizedAt: '2026-09-03T01:00:00.0000000+00:00',
      currency: 'EUR',
      subtotal: 2.5,
      total: 3.03,
      tax: { treatment: 'standard', ratePercent: 21, amount: 0.53, country: 'CZ', note: '' },
      issuer: { legalName: 'Cyber Cloud s.r.o.', country: 'CZ', vatId: 'CZ00000001' },
      customer: { legalName: 'Firma s.r.o.', country: 'CZ', vatId: '' },
      lines: [
        {
          subscriptionId: SUBSCRIPTION,
          meter: 'VCpuHours',
          description: 'Virtual CPU',
          unit: 'vCPU-hour',
          quantity: 100,
          amount: 2.5,
          declaredQuantity: false
        },
        {
          subscriptionId: SUBSCRIPTION,
          meter: 'StorageGbMonths',
          description: 'Storage',
          unit: 'GiB-month',
          quantity: 0.136986301369,
          amount: 0,
          declaredQuantity: true
        }
      ],
      notes: ['Storage is billed on the size a resource declares.']
    };

    it('lists the tenant’s invoices and opens one with its lines, printing the document’s own figures', async () => {
      await open('/invoices');
      (await expectOne(r => r.method === 'GET' && r.url === INVOICES)).flush({ value: [august] });
      await settle();

      const row = host().querySelector('[data-invoice="CC-INV-00000042"]');
      expect(row?.textContent?.replace(/\s+/g, ' ')).toContain('August 2026');
      expect(row?.querySelector('[data-figure="total"]')?.textContent?.trim()).toBe('€3.03');
      expect(row?.querySelector('a')?.getAttribute('href')).toBe('/invoices/CC-INV-00000042');

      let results = await axe.run(host(), WCAG_22_AA);
      expect(
        results.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => n.html.slice(0, 160)).join(' | ')}`)
      ).toEqual([]);

      await open('/invoices/CC-INV-00000042');
      (await expectOne(r => r.method === 'GET' && r.url === `${INVOICES}/CC-INV-00000042`)).flush(august);
      await settle();

      expect([...host().querySelectorAll('[data-line]')].map(l => l.getAttribute('data-line'))).toEqual([
        'VCpuHours',
        'StorageGbMonths'
      ]);
      expect(host().querySelector('[data-line="StorageGbMonths"]')?.textContent).toContain('(declared size)');
      expect(host().querySelector('[data-line="StorageGbMonths"]')?.textContent).toContain('0.137 GiB-month');
      expect(text('[data-figure="subtotal"]')).toBe('€2.50');
      expect(text('[data-figure="tax"]')).toBe('€0.53');
      // ⚠ The stored total, not the lines summed plus tax: the page adds nothing up.
      expect(text('[data-figure="total"]')).toBe('€3.03');
      expect(host().textContent).toContain('VAT 21 %');
      expect(host().textContent).toContain('Storage is billed on the size a resource declares.');

      results = await axe.run(host(), WCAG_22_AA);
      expect(
        results.violations.map(v => `${v.id}: ${v.help} ${v.nodes.map(n => n.html.slice(0, 160)).join(' | ')}`)
      ).toEqual([]);
    });

    it('says who can see invoices when the platform answers 404, and names an empty list honestly', async () => {
      await open('/invoices');
      (await expectOne(r => r.url === INVOICES)).flush(
        {
          error: {
            code: 'ResourceNotFound',
            message: `'/tenants/${TENANT}/providers/CyberCloud.CostManagement/invoices' does not exist.`
          }
        },
        { status: 404, statusText: 'Not Found' }
      );
      await settle();
      expect(host().textContent).toContain('Invoices are visible to owners, contributors and readers of the tenant');

      await open('/invoices/CC-INV-00000042');
      (await expectOne(r => r.url === `${INVOICES}/CC-INV-00000042`)).flush(
        { error: { code: 'ResourceNotFound', message: 'gone' } },
        { status: 404, statusText: 'Not Found' }
      );
      await settle();
      await open('/invoices');
      (await expectOne(r => r.url === INVOICES)).flush({ value: [] });
      await settle();
      expect(host().textContent).toContain('No invoices yet');
    });
  });

  /** Answers the budget list and each budget's `showStatus` — a status or a refusal per name. */
  async function answerBudgets(
    budgets: ReturnType<typeof budget>[],
    statuses: Record<string, object | { status: number; body: object }> = {}
  ): Promise<void> {
    (await expectOne(r => r.method === 'GET' && r.url === BUDGETS)).flush({ value: budgets });
    await settle();

    for (const b of budgets) {
      const request = await expectOne(r => r.method === 'POST' && r.url === `${BUDGETS}/${b.name}/showStatus`);
      const answer = statuses[b.name];
      if (answer !== undefined && 'status' in answer && typeof answer.status === 'number' && 'body' in answer)
        request.flush(answer.body, { status: answer.status, statusText: 'Refused' });
      else request.flush(answer ?? {});
    }
    await settle();
  }

  function budget(name: string, amount: number, actual: number[], forecast: number[]) {
    return {
      id: `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Billing/budgets/${name}`,
      name,
      type: 'CyberCloud.Billing/budgets' as const,
      provisioningState: 'Succeeded',
      etag: '"1"',
      location: 'eu-central',
      properties: {
        amount,
        enabled: true,
        period: 'monthly',
        scope: 'resourceGroup',
        thresholds: { actual, forecast },
        notification: {
          service: `/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Communication/services/alerts`,
          channel: 'email',
          recipients: ['finance@example.com']
        }
      }
    };
  }
});
