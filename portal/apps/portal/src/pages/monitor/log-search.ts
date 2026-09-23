import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HistogramBucket, LogHistogram, provideCharts } from '@cybercloud/charts';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiSelect } from '@xui/select';
import {
  LogAnswer,
  LogEstimate,
  LogQueryProblem,
  LogRow,
  MonitorQueriesApi,
  parseLogQuery
} from '../../app/api/monitor-queries';
import { ResourceAddress } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { TimeRange, Window, stamp, timeRanges, windowOf } from './time-range';

/**
 * How many rows a search may read before the page asks first — docs/plan/20 § The pages that are
 * not generated: "Needs a query cost preview or someone will run a 400-day scan".
 */
export const CONFIRM_ROWS = 10_000_000;

/**
 * Log search over one Monitor workspace: a query box, a window, a histogram and the newest rows,
 * each expandable to its attributes — docs/plan/20 § The pages that are not generated, "Log
 * search", over docs/plan/16 § Querying a workspace.
 *
 * ⚠ **The query box is a filter grammar, not a query language**, and the hint under it says so:
 * `severity:error service:api "timed out" http.method=GET`, parsed in the browser by
 * `parseLogQuery` into `searchLogs`' structured body. `MonitorQueries.SearchLogsRequest`'s remarks
 * record why the platform took a structured filter rather than #54's KQL: the window is a
 * required bound, every value is a bound parameter, and the translator's access filter is not a
 * thing a log row has. A token the grammar does not take is shown as a problem and nothing is
 * sent, rather than searched for as literal text.
 *
 * ⚠ **The cost preview is ClickHouse's own**, `EXPLAIN ESTIMATE` behind `searchLogs`' `estimate`
 * member: the rows in the parts and granules the window and filters could not prune. "Preview
 * cost" asks it on demand, and a search over more than a day asks it first and stops for a
 * confirmation when the answer is over `CONFIRM_ROWS` — the preview docs/plan/20 said this page
 * needs. The rows the search actually read come back with the answer and are shown under it.
 *
 * ⚠ **The histogram narrows, it does not page.** A click on a bar makes that bucket the window.
 * There is no "load older" — `searchLogs` answers the newest `top` in the window and says when
 * there were more — so reaching an older row is narrowing to it, which is also the cheap
 * direction. Paging within a window is owed in docs/plan/16.
 */
@Component({
  selector: 'cc-log-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiCallout, XuiInput, XuiSelect, LogHistogram, NeedsTenant, PageStatus],
  providers: [provideCharts()],
  host: { class: 'block p-6' },
  template: `
    @if (workspace() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" i18n="@@logs.heading">Log search</h1>
      <p class="text-foreground-muted mt-1 text-sm">
        <ng-container i18n="@@logs.workspace">Monitor workspace</ng-container>
        <a class="ml-1 underline" [routerLink]="workspaceLink()">{{ name() }}</a>
        · <a class="underline" [routerLink]="metricsLink()" i18n="@@logs.toMetrics">Metrics explorer</a>
      </p>

      <form class="border-border mt-4 flex flex-col gap-3 rounded-md border p-4" (submit)="onSearch($event)">
        <div class="flex flex-wrap items-end gap-4">
          <div class="flex min-w-80 flex-1 flex-col gap-1.5">
            <label class="text-sm font-medium" for="cc-logs-query" i18n="@@logs.query">Search</label>
            <input
              xuiInput
              id="cc-logs-query"
              autocomplete="off"
              class="font-mono"
              [value]="query()"
              (input)="onQuery($event)"
              [attr.aria-describedby]="'cc-logs-query-hint'"
              [attr.aria-invalid]="problems().length === 0 ? null : 'true'"
            />
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium" id="cc-logs-range-label" i18n="@@logs.range">Time range</span>
            <xui-select
              class="min-w-48"
              [items]="ranges"
              [itemText]="rangeText"
              [filterable]="false"
              [value]="range()"
              aria-labelledby="cc-logs-range-label"
              (valueChange)="onRange($event)"
            />
          </div>
        </div>

        <p class="text-foreground-muted text-xs" id="cc-logs-query-hint" i18n="@@logs.queryHint">
          severity:error (or error,fatal), service:name, trace:32-hex-digits, key=value for an attribute, and any other
          words or "quoted phrases" as text the message must contain.
        </p>

        @if (problems().length > 0) {
          <ul class="text-error text-xs" role="alert" data-problems>
            @for (problem of problems(); track problem.token) {
              <li>
                <code>{{ problem.token }}</code> — {{ problem.reason }}
              </li>
            }
          </ul>
        }

        @if (explicit(); as window) {
          <p class="text-sm" data-narrowed>
            <ng-container i18n="@@logs.narrowed">Narrowed to</ng-container>
            {{ window.from.toISOString() }} – {{ window.to.toISOString() }}
            <button xuiButton variant="ghost" size="sm" type="button" (click)="explicit.set(null)" i18n="@@logs.widen">
              Back to the range
            </button>
          </p>
        }

        <div class="flex flex-wrap gap-2">
          <button
            xuiButton
            color="primary"
            size="sm"
            type="submit"
            [disabled]="busy()"
            [loading]="result().kind === 'loading'"
            i18n="@@logs.search"
          >
            Search
          </button>
          <button
            xuiButton
            variant="outline"
            size="sm"
            type="button"
            [disabled]="busy()"
            [loading]="estimate().kind === 'loading'"
            (click)="onEstimate()"
            i18n="@@logs.preview"
          >
            Preview cost
          </button>
        </div>
      </form>

      <cc-page-status class="mt-4 block" [state]="estimateStatus()" />

      @if (estimated(); as preview) {
        <xui-callout
          class="mt-4"
          [color]="confirming() ? 'warning' : 'primary'"
          [title]="labels.estimateTitle"
          data-estimate
        >
          <p i18n="@@logs.estimate">
            This search would read up to {{ preview.rows.toLocaleString() }} rows in {{ preview.parts }} parts.
          </p>
          @if (confirming()) {
            <p class="mt-1" i18n="@@logs.confirm">
              That is a large scan. Narrow the window or the filters, or run it anyway.
            </p>
            <button
              xuiButton
              color="warning"
              size="sm"
              type="button"
              class="mt-2"
              (click)="onConfirm()"
              i18n="@@logs.runAnyway"
            >
              Run anyway
            </button>
          }
        </xui-callout>
      }

      <cc-page-status class="mt-4 block" [state]="result()" />

      @if (answer(); as answer) {
        @if (answer.note.length > 0) {
          <xui-callout class="mt-4" [title]="labels.noteTitle" data-note>
            <p>{{ answer.note }}</p>
          </xui-callout>
        }

        @if (buckets().length > 0) {
          <cc-log-histogram
            class="mt-4 block"
            [buckets]="buckets()"
            [label]="labels.histogram"
            (bucketClick)="onBucket($event)"
          />
        }

        <p class="text-foreground-muted mt-2 text-xs" data-statistics>
          {{ summary() }}
        </p>

        @if (answer.truncated) {
          <p class="text-warning mt-1 text-xs" data-truncated i18n="@@logs.truncated">
            More records matched than are shown — these are the newest. Narrow the window to see older ones.
          </p>
        }

        <ol class="border-border mt-3 divide-y rounded-md border" [attr.aria-label]="labels.rows">
          @for (row of answer.rows; track $index; let i = $index) {
            <li [attr.data-row]="i">
              <button
                type="button"
                class="hover:bg-surface-hover flex w-full items-start gap-3 px-3 py-2 text-left text-sm"
                [attr.aria-expanded]="expanded() === i"
                [attr.aria-controls]="'cc-logs-row-' + i"
                (click)="toggle(i)"
              >
                <time class="text-foreground-muted w-56 shrink-0 font-mono text-xs" [attr.datetime]="row.timestamp">{{
                  row.timestamp
                }}</time>
                <span class="w-16 shrink-0 text-xs font-semibold uppercase" [attr.data-severity]="row.severity">{{
                  row.severity
                }}</span>
                <span class="text-foreground-muted w-28 shrink-0 truncate text-xs">{{ row.service }}</span>
                <span class="min-w-0 flex-1 break-words">{{ row.body }}</span>
              </button>
              @if (expanded() === i) {
                <dl
                  class="bg-surface-sunken grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 px-3 py-2 text-xs"
                  [id]="'cc-logs-row-' + i"
                  data-detail
                >
                  @for (entry of detail(row); track entry[0]) {
                    <dt class="text-foreground-muted">{{ entry[0] }}</dt>
                    <dd class="font-mono break-all">{{ entry[1] }}</dd>
                  }
                </dl>
              }
            </li>
          } @empty {
            <li class="text-foreground-muted px-3 py-6 text-center text-sm" data-empty i18n="@@logs.empty">
              No records matched in this window.
            </li>
          }
        </ol>
      }
    }
  `
})
export class LogSearch {
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();
  readonly name = input.required<string>();

  private readonly api = inject(MonitorQueriesApi);
  protected readonly tenantId = activeTenantId();

  protected readonly workspace = computed<ResourceAddress | null>(() => {
    const tenantId = this.tenantId();
    return tenantId === null
      ? null
      : {
          tenantId,
          subscriptionId: this.subscriptionId(),
          resourceGroup: this.resourceGroup(),
          parents: [],
          name: this.name()
        };
  });

  protected readonly workspaceLink = computed(() =>
    links.resource(
      {
        tenantId: '',
        subscriptionId: this.subscriptionId(),
        resourceGroup: this.resourceGroup(),
        parents: [],
        name: this.name()
      },
      'CyberCloud.Monitor/workspaces'
    )
  );
  protected readonly metricsLink = computed(() =>
    links.workspaceMetrics(this.subscriptionId(), this.resourceGroup(), this.name())
  );

  protected readonly ranges = timeRanges;
  protected readonly range = signal<TimeRange>(timeRanges[1]);
  protected readonly rangeText = (range: TimeRange): string => range.label;
  /** A window a histogram click narrowed to, which wins over the range until it is cleared. */
  protected readonly explicit = signal<Window | null>(null);

  protected readonly query = signal('');
  private readonly parsed = computed(() => parseLogQuery(this.query()));
  protected readonly problems = computed<readonly LogQueryProblem[]>(() => this.parsed().problems);

  protected readonly result = pageState<LogAnswer>();
  protected readonly estimate = pageState<LogEstimate>();
  protected readonly confirming = signal(false);
  protected readonly expanded = signal<number | null>(null);

  protected readonly busy = computed(() => this.result().kind === 'loading' || this.estimate().kind === 'loading');

  protected readonly answer = computed(() => {
    const state = this.result();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly estimated = computed(() => {
    const state = this.estimate();
    return state.kind === 'ready' ? state.value : null;
  });

  /** The estimate's failure, shown; its success is the callout. */
  protected readonly estimateStatus = computed(() => {
    const state = this.estimate();
    return state.kind === 'failed' ? state : { kind: 'idle' as const };
  });

  protected readonly buckets = computed<readonly HistogramBucket[]>(() =>
    (this.answer()?.histogram.buckets ?? []).map(b => ({ start: Date.parse(b.start), bySeverity: b.bySeverity }))
  );

  protected readonly summary = computed(() => {
    const answer = this.answer();
    if (answer === null) return '';
    const total = answer.histogram.buckets.reduce((sum, b) => sum + b.total, 0);
    return $localize`:@@logs.summary:${total}:total: records in the window, ${answer.rows.length}:shown: shown; ${answer.statistics.rowsRead.toLocaleString()}:read: rows read.`;
  });

  protected readonly labels = {
    estimateTitle: $localize`:@@logs.estimateTitle:Cost preview`,
    noteTitle: $localize`:@@logs.noteTitle:Nothing to search yet`,
    histogram: $localize`:@@logs.histogram:Records per interval, by severity. Select a bar to narrow the window to it.`,
    rows: $localize`:@@logs.rows:Log records, newest first`
  };

  private generation = 0;

  constructor() {
    effect(() => {
      this.workspace();

      untracked(() => {
        this.generation++;
        this.result.set({ kind: 'idle' });
        this.estimate.set({ kind: 'idle' });
        this.confirming.set(false);
        this.expanded.set(null);
        this.explicit.set(null);
      });
    });
  }

  protected onQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  protected onRange(range: TimeRange | null): void {
    if (range === null) return;
    this.range.set(range);
    this.explicit.set(null);
  }

  protected toggle(index: number): void {
    this.expanded.set(this.expanded() === index ? null : index);
  }

  protected detail(row: LogRow): readonly (readonly [string, string])[] {
    const entries: [string, string][] = [
      ['severityText', row.severityText],
      ['service', row.service],
      ['traceId', row.traceId],
      ['spanId', row.spanId]
    ];

    for (const [key, value] of Object.entries(row.attributes)) entries.push([key, value]);
    for (const [key, value] of Object.entries(row.resource)) entries.push([`resource.${key}`, value]);

    return entries.filter(([, value]) => value.length > 0);
  }

  protected onBucket(start: number): void {
    const answer = this.answer();
    if (answer === null) return;

    const width = answer.histogram.bucketSeconds * 1000;
    this.explicit.set({ from: new Date(start), to: new Date(start + width) });
    void this.search(false);
  }

  protected async onSearch(event: Event): Promise<void> {
    event.preventDefault();
    await this.search(true);
  }

  protected async onEstimate(): Promise<void> {
    const content = this.content();
    const workspace = this.workspace();
    if (content === null || workspace === null) return;

    this.confirming.set(false);
    const generation = this.generation;
    await load(
      this.estimate,
      () => this.api.estimateLogs(workspace, content),
      () => generation !== this.generation
    );
  }

  protected async onConfirm(): Promise<void> {
    this.confirming.set(false);
    await this.search(false);
  }

  /**
   * Runs the search; over a window longer than a day it asks for the estimate first and stops
   * for a confirmation when the scan would be large.
   */
  private async search(preview: boolean): Promise<void> {
    const content = this.content();
    const workspace = this.workspace();
    if (content === null || workspace === null) return;

    const generation = this.generation;
    const window = Date.parse(content.to) - Date.parse(content.from);

    if (preview && window > 24 * 60 * 60_000) {
      await this.onEstimate();
      const estimated = this.estimated();
      if (generation !== this.generation || estimated === null) return;

      if (estimated.rows > CONFIRM_ROWS) {
        this.confirming.set(true);
        return;
      }
    }

    this.expanded.set(null);
    await load(
      this.result,
      () => this.api.searchLogs(workspace, content),
      () => generation !== this.generation
    );
  }

  /** The body the query box and the window describe, or `null` while the box has a problem. */
  private content() {
    const { filter, problems } = this.parsed();
    if (problems.length > 0) return null;

    const { from, to } = this.explicit() ?? windowOf(this.range(), new Date());

    return {
      from: stamp(from),
      to: stamp(to),
      ...(filter.text.length > 0 ? { text: filter.text } : {}),
      ...(filter.severities.length > 0 ? { severities: [...filter.severities] } : {}),
      ...(filter.service.length > 0 ? { service: filter.service } : {}),
      ...(filter.attributes.length > 0 ? { attributes: [...filter.attributes] } : {}),
      ...(filter.traceId.length > 0 ? { traceId: filter.traceId } : {})
    };
  }
}
