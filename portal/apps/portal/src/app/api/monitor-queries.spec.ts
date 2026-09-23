import { asLogAnswer, asMetricsAnswer, buildPromQL, parseLogQuery, quote, seriesName } from './monitor-queries';
import { asGraphPage, graphPath, skipTokenOf } from './resource-graph';

describe('the metrics query builder', () => {
  it('writes a selector, a rate and an aggregation in the order PromQL reads them', () => {
    expect(
      buildPromQL({
        metric: 'http_requests_total',
        filters: [
          { label: 'job', matcher: '=', value: 'api' },
          { label: 'code', matcher: '=~', value: '5..' }
        ],
        rate: true,
        aggregation: 'sum',
        by: ['route']
      })
    ).toBe('sum by (route) (rate(http_requests_total{job="api", code=~"5.."}[5m]))');

    expect(buildPromQL({ metric: 'up', filters: [], rate: false, aggregation: 'none', by: ['ignored'] })).toBe('up');
    expect(buildPromQL({ metric: 'up', filters: [], rate: false, aggregation: 'count', by: [] })).toBe('count(up)');
  });

  it('quotes every value, so no value can write syntax', () => {
    expect(quote('a"b\\c\nd')).toBe('"a\\"b\\\\c\\nd"');
    expect(
      buildPromQL({
        metric: 'up',
        filters: [{ label: 'job', matcher: '=', value: '"} or vector(1) or up{job="' }],
        rate: false,
        aggregation: 'none',
        by: []
      })
    ).toBe('up{job="\\"} or vector(1) or up{job=\\""}');
  });

  it('refuses a name that is not a Prometheus name rather than spelling it', () => {
    const base = { filters: [], rate: false, aggregation: 'none' as const, by: [] };
    expect(buildPromQL({ ...base, metric: '' })).toBeNull();
    expect(buildPromQL({ ...base, metric: 'up) or (down' })).toBeNull();
    expect(buildPromQL({ ...base, metric: 'up', filters: [{ label: 'a-b', matcher: '=', value: 'x' }] })).toBeNull();
    // An empty filter row is one the person has not filled in yet, not a refusal.
    expect(buildPromQL({ ...base, metric: 'up', filters: [{ label: '', matcher: '=', value: '' }] })).toBe('up');
    // A `by` label that is not a name is dropped, never spelled.
    expect(buildPromQL({ ...base, metric: 'up', aggregation: 'sum', by: ['job', 'x)'] })).toBe('sum by (job) (up)');
  });

  it('spells a series the way a selector does, name first and labels sorted', () => {
    expect(seriesName({ __name__: 'up', job: 'api', instance: 'a:9100' })).toBe('up{instance="a:9100", job="api"}');
    expect(seriesName({ job: 'api' })).toBe('{job="api"}');
    expect(seriesName({})).toBe('{}');
  });
});

describe('the log search query box', () => {
  it('reads severities, a service, a trace, attributes, and the rest as text', () => {
    const { filter, problems } = parseLogQuery(
      'severity:error,fatal service:api "timed out" upstream http.method=GET trace:0AF7651916CD43DD8448EB211C80319C'
    );

    expect(problems).toEqual([]);
    expect(filter).toEqual({
      text: 'timed out upstream',
      severities: ['error', 'fatal'],
      service: 'api',
      attributes: ['http.method=GET'],
      traceId: '0af7651916cd43dd8448eb211c80319c'
    });
  });

  it('names a token it will not take instead of searching for it as text', () => {
    const { filter, problems } = parseLogQuery('severity:critical trace:xyz =value hello');

    expect(problems.map(p => p.token)).toEqual(['severity:critical', 'trace:xyz', '=value']);
    expect(filter.text).toBe('hello');
    expect(filter.severities).toEqual([]);
  });

  it('keeps a value with an equals sign whole, and repeats a severity once', () => {
    const { filter } = parseLogQuery('query=a=b severity:warn severity:warn');
    expect(filter.attributes).toEqual(['query=a=b']);
    expect(filter.severities).toEqual(['warn']);
  });
});

describe('the undeclared answers', () => {
  it('accepts the shapes the handlers render and refuses anything else', () => {
    const metrics = {
      resultType: 'matrix',
      seriesTotal: 1,
      truncated: false,
      series: [
        {
          labels: { __name__: 'up' },
          points: [
            [1790000000, 1],
            [1790000060, null]
          ]
        }
      ]
    };
    expect(asMetricsAnswer(metrics)).toBe(metrics);
    expect(() => asMetricsAnswer({ ...metrics, series: [{ labels: {}, points: [['x', 1]] }] })).toThrow();
    expect(() => asMetricsAnswer({ status: 'success', data: {} })).toThrow();

    const logs = {
      from: 'a',
      to: 'b',
      rows: [
        {
          timestamp: 't',
          severity: 'info',
          severityText: 'INFO',
          service: 's',
          body: 'b',
          traceId: '',
          spanId: '',
          attributes: {},
          resource: {}
        }
      ],
      truncated: false,
      histogram: { bucketSeconds: 60, buckets: [] },
      statistics: { rowsRead: 1, bytesRead: 1 },
      note: ''
    };
    expect(asLogAnswer(logs)).toBe(logs);
    expect(() =>
      asLogAnswer({ ...logs, rows: [{ timestamp: 't', body: 'b', attributes: { n: 1 }, resource: {} }] })
    ).toThrow();
  });
});

describe('the resource graph address', () => {
  it('builds the one address, and reads the next page token off a link without following it', () => {
    expect(graphPath('t/1')).toBe('/tenants/t%2F1/providers/CyberCloud.ResourceGraph/resources');
    expect(
      skipTokenOf(
        'https://evil.example/tenants/t/providers/CyberCloud.ResourceGraph/resources?$top=50&$skipToken=abc%3D'
      )
    ).toBe('abc=');
    expect(skipTokenOf(undefined)).toBeNull();
    expect(skipTokenOf('')).toBeNull();

    const page = asGraphPage({
      columns: [{ name: 'name', type: 'string' }],
      value: [{ name: 'a' }],
      nextLink: '?$skipToken=t'
    });
    expect(page).toEqual({ columns: [{ name: 'name', type: 'string' }], rows: [{ name: 'a' }], skipToken: 't' });
    expect(() => asGraphPage({ value: [] })).toThrow();
  });
});
