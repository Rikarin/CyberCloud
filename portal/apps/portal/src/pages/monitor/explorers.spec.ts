import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, RouterOutlet, provideRouter, withComponentInputBinding } from '@angular/router';
import { apiVersion } from '@cybercloud/api';
import { CHART_ENGINE } from '@cybercloud/charts';
import { AccessTokenStore, TenantContextStore } from '@cybercloud/shell';
import { XuiSelect } from '@xui/select';
import axe from 'axe-core';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { appRoutes } from '../../app/app.routes';
import { CONFIRM_ROWS, localTime } from './log-search';
import { timeRanges } from './time-range';

/**
 * The three explorers of #41, driven against a recorded platform: the metrics explorer's picker
 * and query, the log search's query box, histogram, expansion and cost preview, and the resource
 * graph's KQL box and paging.
 *
 * ⚠ **The chart engine is a recorder.** jsdom has no canvas, and what these pages owe the chart is
 * the option they hand it — so `CHART_ENGINE` is replaced by one whose `init` returns a chart that
 * keeps every `setOption` and every handler, and a test can click a bar by calling the handler the
 * page registered. The real engine is `echarts-engine.ts`, measured by `scripts/bundle-budget.mjs`.
 */
const TENANT = 't-acme';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';
const GROUP = 'example-rg';
const WORKSPACE = `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Monitor/workspaces/telemetry`;
const PAGE = `/subscriptions/${SUBSCRIPTION}/resourceGroups/${GROUP}/providers/CyberCloud.Monitor/workspaces/telemetry`;
const GRAPH = `/api/tenants/${TENANT}/providers/CyberCloud.ResourceGraph/resources`;

const document = readFileSync(
  join(__dirname, '..', '..', '..', '..', '..', '..', 'generated', 'forms', `${apiVersion}.json`),
  'utf8'
);

const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  rules: { 'color-contrast': { enabled: false } }
};

@Component({
  selector: 'cc-explorers-host',
  imports: [RouterOutlet],
  template: '<main><router-outlet /></main>'
})
class Host {}

/** A chart that records what it is told, in place of ECharts. */
class RecordingChart {
  readonly options: Record<string, unknown>[] = [];
  readonly handlers = new Map<string, (event: unknown) => void>();
  group = '';
  setOption(option: Record<string, unknown>): void {
    this.options.push(option);
  }
  on(name: string, handler: (event: unknown) => void): void {
    this.handlers.set(name, handler);
  }
  resize(): void {
    // Nothing to lay out in jsdom.
  }
  dispose(): void {
    // Nothing to release.
  }
  showLoading(): void {
    // The spinner is ECharts' to draw.
  }
  hideLoading(): void {
    // Likewise.
  }
  last(): Record<string, unknown> {
    return this.options.at(-1) ?? {};
  }
}

describe('the explorers, signed in', () => {
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
        {
          provide: CHART_ENGINE,
          useValue: () =>
            Promise.resolve({
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

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      await new Promise(resolve => setTimeout(resolve, 0));
      await fixture.whenStable();
    }
  }

  async function open(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await settle();
  }

  function type(selector: string, value: string, change = false): void {
    const input = host().querySelector<HTMLInputElement | HTMLTextAreaElement>(selector);
    if (input === null) throw new Error(`no input at ${selector}`);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    if (change) input.dispatchEvent(new Event('change'));
  }

  function click(text: string): void {
    const button = [...host().querySelectorAll<HTMLElement>('button, a')].find(b => b.textContent?.trim() === text);
    if (button === undefined) throw new Error(`no button "${text}"`);
    button.click();
  }

  function submit(): void {
    host().querySelector('form')?.dispatchEvent(new Event('submit'));
  }

  /** The one outstanding POST to a workspace action, checked for `api-version`. */
  function action(name: string): TestRequest {
    const request = http.expectOne(r => r.method === 'POST' && r.url === `${WORKSPACE}/${name}`);
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    return request;
  }

  describe('the metrics explorer', () => {
    it('offers the workspace’s metric names, builds the query it runs, and draws what comes back', async () => {
      await open(`${PAGE}/metrics`);

      const names = action('listMetricLabels');
      expect(names.request.body).toMatchObject({ label: '__name__' });
      names.flush({ values: ['http_requests_total', 'up'], truncated: false });
      await settle();

      expect([...host().querySelectorAll('#cc-metrics-names option')].map(o => o.getAttribute('value'))).toEqual([
        'http_requests_total',
        'up'
      ]);

      // Picking a metric loads its label names, for the filter rows' suggestions.
      type('#cc-metrics-metric', 'http_requests_total', true);
      await settle();
      const labels = action('listMetricLabels');
      expect(labels.request.body).toMatchObject({ match: 'http_requests_total' });
      expect(labels.request.body).not.toHaveProperty('label');
      labels.flush({ values: ['__name__', 'job', 'route'], truncated: false });
      await settle();

      click('Add filter');
      await settle();
      type('[data-filter="0"] input[list="cc-metrics-label-names"]', 'job', true);
      await settle();
      const values = action('listMetricLabels');
      expect(values.request.body).toMatchObject({ label: 'job', match: 'http_requests_total' });
      values.flush({ values: ['api', 'worker'], truncated: false });
      await settle();
      type('[data-filter="0"] input[list^="cc-metrics-values-"]', 'api');
      await settle();

      expect(host().querySelector<HTMLTextAreaElement>('#cc-metrics-promql')?.value).toBe(
        'http_requests_total{job="api"}'
      );

      submit();
      await settle();

      const query = action('queryMetrics');
      const body = query.request.body as { query: string; start: string; end: string };
      expect(body.query).toBe('http_requests_total{job="api"}');
      expect(Date.parse(body.end) - Date.parse(body.start)).toBe(60 * 60_000);
      query.flush({
        resultType: 'matrix',
        seriesTotal: 2,
        truncated: false,
        start: body.start,
        end: body.end,
        stepSeconds: 15,
        series: [
          {
            labels: { __name__: 'http_requests_total', job: 'api', route: '/a' },
            points: [
              [1790000000, 1],
              [1790000015, null],
              [1790000030, 3]
            ]
          },
          { labels: { __name__: 'http_requests_total', job: 'api', route: '/b' }, points: [[1790000000, 5]] }
        ]
      });
      await settle();

      // The chart got the series, in milliseconds, with the gap kept a gap.
      expect(charts).toHaveLength(1);
      const series = charts[0]?.last()['series'] as { name: string; data: unknown[] }[];
      expect(series.map(s => s.name)).toEqual([
        'http_requests_total{job="api", route="/a"}',
        'http_requests_total{job="api", route="/b"}'
      ]);
      expect(series[0]?.data).toEqual([
        [1790000000000, 1],
        [1790000015000, null],
        [1790000030000, 3]
      ]);

      // And the same numbers are a table.
      const row = host().querySelector('[data-series="http_requests_total{job=\\"api\\", route=\\"/a\\"}"]');
      expect(row?.textContent).toContain('3');

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help} at ${v.nodes.map(n => n.html).join(' | ')}`)).toEqual([]);
    });

    it('says when the store truncated the answer, and shows its refusal as it came', async () => {
      await open(`${PAGE}/metrics`);
      action('listMetricLabels').flush({ values: [], truncated: false });
      await settle();

      // Edit by hand: the box takes over from the builder.
      const edit = [...host().querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].at(-1);
      edit?.dispatchEvent(new Event('change'));
      await settle();
      type('#cc-metrics-promql', 'sum(rate(x[5m]');
      submit();
      await settle();

      action('queryMetrics').flush(
        { error: { code: 'InvalidRequestBody', message: 'The metrics store refused the query: unclosed parenthesis' } },
        { status: 400, statusText: 'Bad Request' }
      );
      await settle();
      expect(host().textContent).toContain('unclosed parenthesis');

      type('#cc-metrics-promql', 'up');
      submit();
      await settle();
      action('queryMetrics').flush({
        resultType: 'vector',
        seriesTotal: 900,
        truncated: true,
        time: '2026-09-23T12:00:00.000Z',
        series: [{ labels: { __name__: 'up' }, points: [[1790000000, 1]] }]
      });
      await settle();

      expect(host().querySelector('[data-truncated]')?.textContent).toContain('900');
    });
  });

  describe('the log search', () => {
    const answer = {
      from: '2026-09-23T11:00:00.000Z',
      to: '2026-09-23T12:00:00.000Z',
      rows: [
        {
          timestamp: '2026-09-23T11:47:00.123000000Z',
          severity: 'error',
          severityText: 'ERROR',
          service: 'api',
          body: 'upstream timed out',
          traceId: '0af7651916cd43dd8448eb211c80319c',
          spanId: '',
          attributes: { 'http.method': 'GET' },
          resource: { 'deployment.environment': 'prod' }
        },
        {
          timestamp: '2026-09-23T11:02:00.123000000Z',
          severity: 'info',
          severityText: 'INFO',
          service: 'api',
          body: 'request served',
          traceId: '',
          spanId: '',
          attributes: {},
          resource: {}
        }
      ],
      truncated: true,
      histogram: {
        bucketSeconds: 600,
        buckets: [
          { start: '2026-09-23T11:00:00.000Z', total: 1, bySeverity: { info: 1 } },
          { start: '2026-09-23T11:40:00.000Z', total: 1, bySeverity: { error: 1 } }
        ]
      },
      statistics: { rowsRead: 7, bytesRead: 700 },
      note: ''
    };

    it("shows a record's nanosecond timestamp as the viewer's local time to the millisecond", () => {
      const expected = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        fractionalSecondDigits: 3
      }).format(Date.UTC(2026, 8, 23, 11, 47, 0, 123));

      expect(localTime('2026-09-23T11:47:00.123456789Z')).toBe(expected);
      expect(localTime('2026-09-23T11:47:00.123Z')).toBe(expected);
      expect(localTime('not a time')).toBe('not a time');
    });

    it('turns the query box into the structured body, expands a row, and narrows to a clicked bar', async () => {
      await open(`${PAGE}/logs`);

      type('#cc-logs-query', 'severity:error service:api "timed out" http.method=GET');
      submit();
      await settle();

      const search = action('searchLogs');
      const body = search.request.body as Record<string, unknown>;
      expect(body).toMatchObject({
        text: 'timed out',
        severities: ['error'],
        service: 'api',
        attributes: ['http.method=GET']
      });
      expect(body).not.toHaveProperty('estimate');
      expect(Date.parse(body['to'] as string) - Date.parse(body['from'] as string)).toBe(60 * 60_000);
      search.flush(answer);
      await settle();

      expect([...host().querySelectorAll('[data-row]')].map(r => r.querySelector('[data-body]')?.textContent)).toEqual([
        'upstream timed out',
        'request served'
      ]);
      expect(host().querySelectorAll('[data-rows] [role="columnheader"]')).toHaveLength(4);

      // The time is the viewer's, to the millisecond; the store's nine digits stay on the element.
      const time = host().querySelector('[data-row="0"] time');
      expect(time?.getAttribute('datetime')).toBe('2026-09-23T11:47:00.123000000Z');
      expect(time?.textContent).toBe(localTime('2026-09-23T11:47:00.123000000Z'));
      expect(host().querySelector('[data-truncated]')).not.toBeNull();
      expect(host().querySelector('[data-statistics]')?.textContent).toContain('2 records in the window');

      // Expanding shows the record's own attributes and its resource's, and nothing empty.
      host().querySelector<HTMLButtonElement>('[data-row="0"] button')?.click();
      await settle();
      const detail = host().querySelector('[data-detail]')?.textContent ?? '';
      expect(detail).toContain('http.method');
      expect(detail).toContain('resource.deployment.environment');
      expect(detail).toContain('0af7651916cd43dd8448eb211c80319c');
      expect(detail).not.toContain('spanId');
      expect(detail).toContain('2026-09-23T11:47:00.123000000Z');

      // The histogram was handed both buckets, stacked by severity.
      const chart = charts.at(-1);
      expect((chart?.last()['series'] as { name: string }[]).map(s => s.name)).toEqual(['info', 'error']);

      // A click on the second bar narrows the window to that bucket and searches again.
      chart?.handlers.get('click')?.({ value: [Date.parse('2026-09-23T11:40:00.000Z'), 1] });
      await settle();
      const narrowed = action('searchLogs');
      expect(narrowed.request.body).toMatchObject({ from: '2026-09-23T11:40:00.000Z', to: '2026-09-23T11:50:00.000Z' });
      narrowed.flush({ ...answer, rows: [answer.rows[0]], truncated: false });
      await settle();
      expect(host().querySelector('[data-narrowed]')).not.toBeNull();

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help} at ${v.nodes.map(n => n.html).join(' | ')}`)).toEqual([]);
    });

    it('sends nothing while the box has a token it does not take, and says which', async () => {
      await open(`${PAGE}/logs`);

      type('#cc-logs-query', 'severity:critical oops');
      submit();
      await settle();

      expect(host().querySelector('[data-problems]')?.textContent).toContain('severity:critical');
      http.expectNone(r => r.url.startsWith(WORKSPACE));
    });

    it('previews the cost on a long window and asks before a large scan', async () => {
      await open(`${PAGE}/logs`);

      // The range picker is a select; its model takes the value the way a pick would set it.
      const select = fixture.debugElement.query(By.directive(XuiSelect)).componentInstance as XuiSelect<unknown>;
      select.value.set(timeRanges.find(r => r.key === '7d') ?? null);
      await settle();

      submit();
      await settle();

      const estimate = action('searchLogs');
      expect(estimate.request.body).toMatchObject({ estimate: true });
      estimate.flush({ from: 'a', to: 'b', estimate: { rows: CONFIRM_ROWS + 1, parts: 40, marks: 9000 } });
      await settle();

      // Nothing was searched: the page asks first.
      http.expectNone(r => r.url === `${WORKSPACE}/searchLogs`);
      expect(host().querySelector('[data-estimate]')?.textContent).toContain('40 parts');

      click('Run anyway');
      await settle();
      const search = action('searchLogs');
      expect(search.request.body).not.toHaveProperty('estimate');
      search.flush({
        ...answer,
        rows: [],
        truncated: false,
        histogram: { bucketSeconds: 600, buckets: [] },
        note: 'No logs have arrived.'
      });
      await settle();

      expect(host().querySelector('[data-note]')?.textContent).toContain('No logs have arrived.');
      expect(host().querySelector('[data-empty]')).not.toBeNull();
    });
  });

  describe('the resource graph', () => {
    it('POSTs the KQL to the one address, draws the columns it names, and pages by token', async () => {
      await open('/graph');

      type('#cc-graph-query', 'resources | project name, type');
      submit();
      await settle();

      const first = http.expectOne(r => r.method === 'POST' && r.url === GRAPH);
      expect(first.request.body).toEqual({ query: 'resources | project name, type', $top: 50 });
      first.flush({
        columns: [
          { name: 'name', type: 'string' },
          { name: 'type', type: 'string' }
        ],
        value: [{ name: 'pg-main', type: 'CyberCloud.DBforPostgreSQL/servers' }],
        nextLink: `https://evil.example${GRAPH}?$top=50&$skipToken=page-2`
      });
      await settle();

      expect([...host().querySelectorAll('[data-column]')].map(c => c.getAttribute('data-column'))).toEqual([
        'name',
        'type'
      ]);
      expect(host().textContent).toContain('pg-main');

      click('Load more');
      await settle();

      // ⚠ The same address, never the link's origin, with the token in the body.
      const second = http.expectOne(r => r.method === 'POST' && r.url === GRAPH);
      expect(second.request.body).toEqual({ query: 'resources | project name, type', $top: 50, $skipToken: 'page-2' });
      second.flush({
        columns: [
          { name: 'name', type: 'string' },
          { name: 'type', type: 'string' }
        ],
        value: [{ name: 'pg-replica', type: 'CyberCloud.DBforPostgreSQL/servers' }]
      });
      await settle();

      expect(host().textContent).toContain('pg-replica');
      expect(host().querySelector('[data-count]')?.textContent).toContain('2 rows');

      const results = await axe.run(host(), WCAG_22_AA);
      expect(results.violations.map(v => `${v.id}: ${v.help} at ${v.nodes.map(n => n.html).join(' | ')}`)).toEqual([]);
    });

    it('shows the subset’s refusal as the platform wrote it', async () => {
      await open('/graph');

      type('#cc-graph-query', 'resources | where createdAt > ago(1d)');
      submit();
      await settle();

      http
        .expectOne(r => r.url === GRAPH)
        .flush(
          {
            error: {
              code: 'InvalidRequestBody',
              message: "'ago' is not supported. The resource graph's KQL subset is: …"
            }
          },
          { status: 400, statusText: 'Bad Request' }
        );
      await settle();

      expect(host().textContent).toContain("'ago' is not supported");
    });
  });

  it('reaches both workspace explorers from the workspace’s blade', async () => {
    await open(PAGE);

    for (const request of http.match(r => r.url === `/forms/${apiVersion}.json`)) request.flush(JSON.parse(document));
    await settle();
    http
      .expectOne(r => r.method === 'GET' && r.url === WORKSPACE)
      .flush({
        id: WORKSPACE.slice('/api'.length),
        name: 'telemetry',
        type: 'CyberCloud.Monitor/workspaces',
        location: 'eu-central',
        provisioningState: 'Succeeded',
        etag: '"1"',
        properties: {}
      });
    await settle();

    const hrefs = [...host().querySelectorAll('a')].map(a => a.getAttribute('href'));
    expect(hrefs).toContain(`${PAGE}/metrics`);
    expect(hrefs).toContain(`${PAGE}/logs`);
  });
});
