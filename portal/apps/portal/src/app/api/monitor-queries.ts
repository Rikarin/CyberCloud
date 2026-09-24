import { Injectable, inject } from '@angular/core';
import type {
  MonitorWorkspacesListMetricLabelsContent,
  MonitorWorkspacesQueryMetricsContent,
  MonitorWorkspacesSearchLogsContent
} from '@cybercloud/api';
import { PlatformApi } from './platform-api';
import { ResourceAddress } from './resource-verbs';

/**
 * The metrics explorer's and log search's three reads, typed.
 *
 * The three actions are generated — `queryMetricsMonitorWorkspace`, `listMetricLabelsMonitorWorkspace`,
 * `searchLogsMonitorWorkspace` — and this goes through them rather than around them. What it adds
 * is the response type for two of them: docs/plan/16 § Querying a workspace declares a request and
 * **no response** for `queryMetrics` and `searchLogs`, because a list of series and a list of rows
 * are arrays of objects the registry's schema cannot state, so the generated client types both
 * `unknown`. The shapes are here, written from `MonitorWorkspaceQueryMetricsHandler.Render` and
 * `MonitorWorkspaceSearchLogsHandler.Render`, and each response is checked against them before a page
 * sees it — a platform that changed the shape fails loudly here rather than drawing an empty chart.
 * `charts/managed/monitor-workspace/conformance.yaml § owed`, `query-responses-are-undeclared`, is
 * what closes it, and the day it does these interfaces become imports.
 */

/** One series: its label set and its points, `[epoch seconds, value | null]`. */
export interface MetricsSeries {
  readonly labels: Readonly<Record<string, string>>;
  readonly points: readonly (readonly [number, number | null])[];
}

/** What `queryMetrics` answers. */
export interface MetricsAnswer {
  readonly resultType: string;
  readonly seriesTotal: number;
  readonly truncated: boolean;
  readonly series: readonly MetricsSeries[];
  readonly start?: string;
  readonly end?: string;
  readonly stepSeconds?: number;
  readonly time?: string;
}

/** One log record. */
export interface LogRow {
  readonly timestamp: string;
  readonly severity: string;
  readonly severityText: string;
  readonly service: string;
  readonly body: string;
  readonly traceId: string;
  readonly spanId: string;
  readonly attributes: Readonly<Record<string, string>>;
  readonly resource: Readonly<Record<string, string>>;
}

/** One histogram bucket, as the platform sends it. */
export interface LogBucket {
  readonly start: string;
  readonly total: number;
  readonly bySeverity: Readonly<Record<string, number>>;
}

/** What `searchLogs` answers. */
export interface LogAnswer {
  readonly from: string;
  readonly to: string;
  readonly rows: readonly LogRow[];
  readonly truncated: boolean;
  readonly histogram: { readonly bucketSeconds: number; readonly buckets: readonly LogBucket[] };
  readonly statistics: { readonly rowsRead: number; readonly bytesRead: number };
  readonly note: string;
}

/** What `searchLogs` with `estimate` answers: ClickHouse's own reading of what the search would scan. */
export interface LogEstimate {
  readonly rows: number;
  readonly parts: number;
  readonly marks: number;
}

/** The platform answered a shape this page does not know. */
export class UnexpectedAnswer extends Error {
  constructor(what: string) {
    super(
      $localize`:@@monitor.unexpected:The platform answered ${what}:what: in a shape this page does not recognise.`
    );
    this.name = 'UnexpectedAnswer';
  }
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const isStringMap = (value: unknown): value is Record<string, string> =>
  isRecord(value) && Object.values(value).every(v => typeof v === 'string');

/** Checks a `queryMetrics` answer. */
export function asMetricsAnswer(value: unknown): MetricsAnswer {
  if (
    !isRecord(value) ||
    typeof value['resultType'] !== 'string' ||
    typeof value['truncated'] !== 'boolean' ||
    typeof value['seriesTotal'] !== 'number' ||
    !Array.isArray(value['series'])
  ) {
    throw new UnexpectedAnswer('queryMetrics');
  }

  for (const series of value['series'] as unknown[]) {
    if (!isRecord(series) || !isStringMap(series['labels']) || !Array.isArray(series['points'])) {
      throw new UnexpectedAnswer('queryMetrics');
    }

    for (const point of series['points'] as unknown[]) {
      if (
        !Array.isArray(point) ||
        typeof point[0] !== 'number' ||
        !(point[1] === null || typeof point[1] === 'number')
      ) {
        throw new UnexpectedAnswer('queryMetrics');
      }
    }
  }

  return value as unknown as MetricsAnswer;
}

/** Checks a `searchLogs` answer. */
export function asLogAnswer(value: unknown): LogAnswer {
  if (
    !isRecord(value) ||
    !Array.isArray(value['rows']) ||
    typeof value['truncated'] !== 'boolean' ||
    !isRecord(value['histogram']) ||
    !Array.isArray(value['histogram']['buckets']) ||
    !isRecord(value['statistics'])
  ) {
    throw new UnexpectedAnswer('searchLogs');
  }

  for (const row of value['rows'] as unknown[]) {
    if (
      !isRecord(row) ||
      typeof row['timestamp'] !== 'string' ||
      typeof row['body'] !== 'string' ||
      !isStringMap(row['attributes']) ||
      !isStringMap(row['resource'])
    ) {
      throw new UnexpectedAnswer('searchLogs');
    }
  }

  return value as unknown as LogAnswer;
}

/** Checks a `searchLogs` estimate. */
export function asLogEstimate(value: unknown): LogEstimate {
  const estimate = isRecord(value) ? value['estimate'] : undefined;

  if (!isRecord(estimate) || typeof estimate['rows'] !== 'number') throw new UnexpectedAnswer('searchLogs');

  return estimate as unknown as LogEstimate;
}

@Injectable({ providedIn: 'root' })
export class MonitorQueriesApi {
  private readonly api = inject(PlatformApi);

  async queryMetrics(
    workspace: ResourceAddress,
    content: MonitorWorkspacesQueryMetricsContent
  ): Promise<MetricsAnswer> {
    const { tenantId, subscriptionId, resourceGroup, name } = workspace;
    const response = await this.api.queryMetricsMonitorWorkspace(
      tenantId,
      subscriptionId,
      resourceGroup,
      name,
      content
    );
    return asMetricsAnswer(response.value);
  }

  async listMetricLabels(
    workspace: ResourceAddress,
    content: MonitorWorkspacesListMetricLabelsContent
  ): Promise<readonly string[]> {
    const { tenantId, subscriptionId, resourceGroup, name } = workspace;
    const response = await this.api.listMetricLabelsMonitorWorkspace(
      tenantId,
      subscriptionId,
      resourceGroup,
      name,
      content
    );
    return response.value.values;
  }

  async searchLogs(workspace: ResourceAddress, content: MonitorWorkspacesSearchLogsContent): Promise<LogAnswer> {
    const { tenantId, subscriptionId, resourceGroup, name } = workspace;
    const response = await this.api.searchLogsMonitorWorkspace(tenantId, subscriptionId, resourceGroup, name, content);
    return asLogAnswer(response.value);
  }

  async estimateLogs(workspace: ResourceAddress, content: MonitorWorkspacesSearchLogsContent): Promise<LogEstimate> {
    const { tenantId, subscriptionId, resourceGroup, name } = workspace;
    const response = await this.api.searchLogsMonitorWorkspace(tenantId, subscriptionId, resourceGroup, name, {
      ...content,
      estimate: true
    });
    return asLogEstimate(response.value);
  }
}

// ── The metrics query builder ─────────────────────────────────────────────────────────────────

/** A Prometheus label name — `MonitorQueries.LabelNamePattern`. */
export const LABEL_NAME = /^[a-zA-Z_][a-zA-Z0-9_]*$/;

/** A Prometheus metric name. */
export const METRIC_NAME = /^[a-zA-Z_:][a-zA-Z0-9_:]*$/;

export const matchers = ['=', '!=', '=~', '!~'] as const;
export type Matcher = (typeof matchers)[number];

export const aggregations = ['none', 'sum', 'avg', 'min', 'max', 'count'] as const;
export type Aggregation = (typeof aggregations)[number];

export interface LabelFilter {
  readonly label: string;
  readonly matcher: Matcher;
  readonly value: string;
}

/** What the builder's controls hold. */
export interface MetricsQueryModel {
  readonly metric: string;
  readonly filters: readonly LabelFilter[];
  /** Apply `rate(…[window])` — for a counter, which is what a `_total` metric is. */
  readonly rate: boolean;
  readonly aggregation: Aggregation;
  /** The labels `by (…)` keeps. Ignored with no aggregation. */
  readonly by: readonly string[];
}

/** The points the platform cuts a range query into when it names no step — `MonitorQueries.DefaultPoints`. */
export const DEFAULT_POINTS = 240;

/**
 * The `rate` window for a range query over `rangeMs`: the step the platform will choose, and never
 * less than five minutes.
 *
 * ⚠ **A fixed `[5m]` samples five minutes of each step.** Over 30 days the step is three hours, so a
 * five-minute window reads one thirty-sixth of the data and draws whatever those minutes did. The
 * window grows with the step, which the platform derives from the window over `DEFAULT_POINTS`, so
 * every sample counts once; five minutes stays the floor, because a window narrower than two scrapes
 * has no rate at all.
 */
export function rateWindowFor(rangeMs: number): string {
  const seconds = Math.max(300, Math.ceil(rangeMs / 1000 / DEFAULT_POINTS));

  if (seconds % 3600 === 0) return `${seconds / 3600}h`;
  if (seconds % 60 === 0) return `${seconds / 60}m`;
  return `${seconds}s`;
}

/** A label value as a PromQL string literal: `\` and `"` escaped, a newline spelled. */
export function quote(value: string): string {
  return `"${value.replaceAll('\\', '\\\\').replaceAll('"', '\\"').replaceAll('\n', '\\n')}"`;
}

/**
 * The PromQL the builder's controls describe, or `null` while they describe none.
 *
 * ⚠ **Names are checked and values are quoted, so no control can write syntax.** A metric or a
 * label name that is not a Prometheus name is refused rather than spelled, and every value passes
 * through `quote`. The expression still runs under the workspace's `accountID` whatever it says —
 * the builder protects the person from a query they did not mean, not the platform from one.
 */
export function buildPromQL(model: MetricsQueryModel, rateWindow = '5m'): string | null {
  if (!METRIC_NAME.test(model.metric)) return null;

  const filters = model.filters.filter(f => f.label.length > 0);
  if (filters.some(f => !LABEL_NAME.test(f.label))) return null;

  const selector =
    model.metric +
    (filters.length === 0 ? '' : `{${filters.map(f => `${f.label}${f.matcher}${quote(f.value)}`).join(', ')}}`);

  const inner = model.rate ? `rate(${selector}[${rateWindow}])` : selector;

  if (model.aggregation === 'none') return inner;

  const by = model.by.filter(label => LABEL_NAME.test(label));

  return by.length === 0 ? `${model.aggregation}(${inner})` : `${model.aggregation} by (${by.join(', ')}) (${inner})`;
}

/** A series' label set, spelled the way PromQL spells a selector: `metric{a="1", b="2"}`. */
export function seriesName(labels: Readonly<Record<string, string>>): string {
  const { __name__: name = '', ...rest } = labels;
  const pairs = Object.keys(rest)
    .sort()
    .map(key => `${key}=${quote(rest[key] ?? '')}`);

  return pairs.length === 0 ? name || '{}' : `${name}{${pairs.join(', ')}}`;
}

// ── The log search's query box ────────────────────────────────────────────────────────────────

export const severities = ['trace', 'debug', 'info', 'warn', 'error', 'fatal'] as const;
export type Severity = (typeof severities)[number];

/** What the query box parses into — `searchLogs`' filter members, minus the window. */
export interface LogFilter {
  readonly text: string;
  readonly severities: readonly Severity[];
  readonly service: string;
  readonly attributes: readonly string[];
  readonly traceId: string;
}

/** A query box token the grammar did not accept, with the reason. */
export interface LogQueryProblem {
  readonly token: string;
  readonly reason: string;
}

/**
 * Parses the log search's query box into a structured filter.
 *
 * The grammar, which the box's hint states:
 *
 * - `severity:error` (repeatable, or `severity:error,fatal`) — a severity class;
 * - `service:api` — the `service.name`;
 * - `trace:0af76519…` — a trace id, 32 hex digits;
 * - `key=value` — an attribute of the record or its resource;
 * - bare words, or one `"quoted phrase"` — ONE phrase the body must contain, words joined with
 *   single spaces.
 *
 * ⚠ **The text is one phrase, because `searchLogs` searches one substring.** `"timed out" billing`
 * used to become the phrase "timed out billing", which is not what the quotes asked for; a quoted
 * phrase beside any other text is now a problem rather than a search for something else. Bare
 * words stay one phrase, which is what a person typing `connection refused` means.
 *
 * ⚠ **A structured filter, not a query language**, and the reason is recorded in
 * `MonitorQueries.SearchLogsRequest`'s remarks: the platform binds every value as a parameter and
 * requires the window, so this box can only ever narrow a search. A token it does not accept is
 * returned as a problem rather than silently read as text, because `severity:critical` searched as
 * the words "severity:critical" finds nothing and says nothing.
 */
export function parseLogQuery(input: string): { filter: LogFilter; problems: readonly LogQueryProblem[] } {
  const text: string[] = [];
  const found: Severity[] = [];
  const attributes: string[] = [];
  const problems: LogQueryProblem[] = [];
  let service = '';
  let traceId = '';

  const tokens = input.match(/"[^"]*"|\S+/g) ?? [];
  let quoted = 0;

  for (const token of tokens) {
    if (token.startsWith('"')) {
      const phrase = token.slice(1, -1).trim();
      if (phrase.length > 0) {
        text.push(phrase);
        quoted++;
      }
      continue;
    }

    const colon = /^(severity|service|trace):(.*)$/.exec(token);

    if (colon !== null) {
      const [, key, value] = colon as unknown as [string, string, string];

      if (key === 'severity') {
        for (const one of value.split(',').filter(v => v.length > 0)) {
          if ((severities as readonly string[]).includes(one)) {
            if (!found.includes(one as Severity)) found.push(one as Severity);
          } else {
            problems.push({
              token,
              reason: $localize`:@@logs.problem.severity:"${one}:value:" is not one of ${severities.join(', ')}:list:.`
            });
          }
        }
      } else if (key === 'service') {
        service = value;
      } else if (/^[0-9a-fA-F]{32}$/.test(value)) {
        traceId = value.toLowerCase();
      } else {
        problems.push({ token, reason: $localize`:@@logs.problem.trace:A trace id is 32 hexadecimal digits.` });
      }

      continue;
    }

    const equals = token.indexOf('=');

    if (equals > 0) {
      attributes.push(token);
      continue;
    }

    if (equals === 0) {
      problems.push({ token, reason: $localize`:@@logs.problem.attribute:An attribute filter is key=value.` });
      continue;
    }

    text.push(token);
  }

  if (quoted > 0 && text.length > 1) {
    problems.push({
      token: text.map(t => `"${t}"`).join(' '),
      reason: $localize`:@@logs.problem.phrases:The search looks for one phrase. Put all the text in one pair of quotes, or search for each piece in turn.`
    });
  }

  return { filter: { text: text.join(' '), severities: found, service, attributes, traceId }, problems };
}
