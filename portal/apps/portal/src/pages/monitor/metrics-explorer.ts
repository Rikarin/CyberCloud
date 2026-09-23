import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MetricSeries, TimeSeriesChart, formatValue, provideCharts } from '@cybercloud/charts';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiSelect } from '@xui/select';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import {
  Aggregation,
  LabelFilter,
  Matcher,
  MetricsAnswer,
  MonitorQueriesApi,
  aggregations,
  buildPromQL,
  matchers,
  rateWindowFor,
  seriesName
} from '../../app/api/monitor-queries';
import { ResourceAddress } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, failure, load, pageState } from '../shared/page-state';
import { TimeRange, stamp, timeRanges, windowOf } from './time-range';

/** One row of the series table: what the chart draws, as numbers a screen reader can read. */
interface SeriesRow {
  readonly name: string;
  readonly last: string;
  readonly min: string;
  readonly max: string;
}

/**
 * The metrics explorer: a query builder over one Monitor workspace's metrics, a chart, and the
 * same numbers as a table — docs/plan/20 § The pages that are not generated, "Metrics explorer",
 * over docs/plan/16 § Querying a workspace.
 *
 * The builder is the four things a PromQL query is made of in practice — a metric, label
 * filters, `rate` for a counter, an aggregation `by` some labels — and it writes the expression
 * it runs where the person can read it (`buildPromQL`). The PromQL box beside it takes over for
 * anything the builder cannot say. The metric names, a metric's label names and a label's values
 * come from `listMetricLabels`, the workspace's own series API, so the picker offers what this
 * workspace has rather than what a platform-wide list guesses.
 *
 * ⚠ **The chart is `@cybercloud/charts` over `@xui/echarts`, and the engine loads on first draw.**
 * `provideCharts()` is on this component rather than the app, so no other route's graph reaches
 * `@xui/echarts`, and ECharts itself is a lazy chunk of its own — `libs/charts` says why.
 *
 * ⚠ **What this page does not do**, each owed in docs/plan/16: pin a chart to a dashboard (there
 * are no dashboards to pin to — the portal embeds none, ADR-011), save a query, and draw more than
 * one query at once.
 */
@Component({
  selector: 'cc-metrics-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    XuiButton,
    XuiCallout,
    XuiInput,
    XuiSelect,
    XuiTable,
    XuiTr,
    XuiTh,
    XuiTd,
    TimeSeriesChart,
    NeedsTenant,
    PageStatus
  ],
  providers: [provideCharts()],
  host: { class: 'block p-6' },
  template: `
    @if (workspace() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" i18n="@@metrics.heading">Metrics explorer</h1>
      <p class="text-foreground-muted mt-1 text-sm">
        <ng-container i18n="@@metrics.workspace">Monitor workspace</ng-container>
        <a class="ml-1 underline" [routerLink]="workspaceLink()">{{ name() }}</a>
        · <a class="underline" [routerLink]="logsLink()" i18n="@@metrics.toLogs">Log search</a>
      </p>

      <form class="border-border mt-4 flex flex-col gap-4 rounded-md border p-4" (submit)="onRun($event)">
        <div class="flex flex-wrap items-end gap-4">
          <div class="flex min-w-72 flex-1 flex-col gap-1.5">
            <label class="text-sm font-medium" for="cc-metrics-metric" i18n="@@metrics.metric">Metric</label>
            <input
              xuiInput
              id="cc-metrics-metric"
              list="cc-metrics-names"
              autocomplete="off"
              [value]="metric()"
              (input)="onMetric($event)"
              (change)="loadLabelNames()"
              [attr.aria-describedby]="'cc-metrics-metric-hint'"
            />
            <datalist id="cc-metrics-names">
              @for (item of metricNames(); track item) {
                <option [value]="item"></option>
              }
            </datalist>
            <p class="text-foreground-muted text-xs" id="cc-metrics-metric-hint">{{ metricHint() }}</p>
          </div>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium" id="cc-metrics-range-label" i18n="@@metrics.range">Time range</span>
            <xui-select
              class="min-w-48"
              [items]="ranges"
              [itemText]="rangeText"
              [filterable]="false"
              [value]="range()"
              aria-labelledby="cc-metrics-range-label"
              (valueChange)="onRange($event)"
            />
          </div>
        </div>

        <fieldset class="flex flex-col gap-2">
          <legend class="text-sm font-medium" i18n="@@metrics.filters">Label filters</legend>
          <datalist id="cc-metrics-label-names">
            @for (item of labelNames(); track item) {
              <option [value]="item"></option>
            }
          </datalist>
          @for (filter of filters(); track $index; let i = $index) {
            <div class="flex flex-wrap items-center gap-2" [attr.data-filter]="i">
              <input
                xuiInput
                class="w-48"
                list="cc-metrics-label-names"
                autocomplete="off"
                [attr.aria-label]="labels.filterLabel"
                [value]="filter.label"
                (input)="onFilter(i, 'label', $event)"
                (change)="loadLabelValues(i)"
              />
              <xui-select
                class="w-24"
                [items]="matcherItems"
                [itemText]="matcherText"
                [filterable]="false"
                [value]="filter.matcher"
                [ariaLabel]="labels.filterMatcher"
                (valueChange)="onMatcher(i, $event)"
              />
              <input
                xuiInput
                class="w-64"
                autocomplete="off"
                [attr.list]="'cc-metrics-values-' + i"
                [attr.aria-label]="labels.filterValue"
                [value]="filter.value"
                (input)="onFilter(i, 'value', $event)"
              />
              <datalist [id]="'cc-metrics-values-' + i">
                @for (item of valuesFor(i); track item) {
                  <option [value]="item"></option>
                }
              </datalist>
              <button
                xuiButton
                variant="ghost"
                size="sm"
                type="button"
                [attr.aria-label]="labels.removeFilter"
                (click)="removeFilter(i)"
                i18n="@@metrics.removeFilter"
              >
                Remove
              </button>
            </div>
          }
          <div>
            <button
              xuiButton
              variant="outline"
              size="sm"
              type="button"
              (click)="addFilter()"
              i18n="@@metrics.addFilter"
            >
              Add filter
            </button>
          </div>
        </fieldset>

        <div class="flex flex-wrap items-end gap-4">
          <label class="flex items-center gap-2 text-sm">
            <input type="checkbox" [checked]="rate()" (change)="rate.set(!rate())" />
            <span i18n="@@metrics.rate"
              >Per-second rate over each point's interval, at least 5 minutes — for a counter</span
            >
          </label>

          <div class="flex flex-col gap-1.5">
            <span class="text-sm font-medium" id="cc-metrics-aggregation-label" i18n="@@metrics.aggregation"
              >Aggregation</span
            >
            <xui-select
              class="min-w-36"
              [items]="aggregationItems"
              [itemText]="aggregationText"
              [filterable]="false"
              [value]="aggregation()"
              aria-labelledby="cc-metrics-aggregation-label"
              (valueChange)="onAggregation($event)"
            />
          </div>

          @if (aggregation() !== 'none' && labelNames().length > 0) {
            <fieldset class="flex flex-wrap items-center gap-3">
              <legend class="text-sm font-medium" i18n="@@metrics.by">By</legend>
              @for (item of groupable(); track item) {
                <label class="flex items-center gap-1 text-sm">
                  <input type="checkbox" [checked]="by().includes(item)" (change)="toggleBy(item)" />
                  <span>{{ item }}</span>
                </label>
              }
            </fieldset>
          }
        </div>

        <div class="flex flex-col gap-1.5">
          <div class="flex items-center gap-3">
            <label class="text-sm font-medium" for="cc-metrics-promql" i18n="@@metrics.promql">PromQL</label>
            <label class="flex items-center gap-1 text-xs">
              <input type="checkbox" [checked]="custom()" (change)="onCustom()" />
              <span i18n="@@metrics.edit">Edit by hand</span>
            </label>
          </div>
          <textarea
            xuiInput
            id="cc-metrics-promql"
            rows="2"
            class="font-mono text-xs"
            [readOnly]="!custom()"
            [value]="expression() ?? ''"
            (input)="onExpression($event)"
          ></textarea>
        </div>

        <div>
          <button
            xuiButton
            color="primary"
            size="sm"
            type="submit"
            [disabled]="expression() === null || result().kind === 'loading'"
            [loading]="result().kind === 'loading'"
            i18n="@@metrics.run"
          >
            Run query
          </button>
        </div>
      </form>

      <cc-page-status class="mt-4 block" [state]="result()" />

      @if (answer(); as answer) {
        @if (answer.truncated) {
          <xui-callout class="mt-4" color="warning" [title]="labels.truncatedTitle" data-truncated>
            <p i18n="@@metrics.truncated">
              The query matched {{ answer.seriesTotal }} series and the first {{ answer.series.length }} are shown.
              Narrow it with a label filter or aggregate it.
            </p>
          </xui-callout>
        }

        @if (answer.series.length === 0) {
          <p class="text-foreground-muted mt-4 text-sm" data-empty i18n="@@metrics.empty">
            No series matched in this window.
          </p>
        } @else {
          <cc-time-series-chart class="mt-4 block" [series]="chartSeries()" [label]="chartLabel()" />

          <xui-table class="mt-4" striped [attr.aria-label]="labels.table">
            <xui-tr>
              <xui-th role="columnheader" class="flex-1" i18n="@@metrics.col.series">Series</xui-th>
              <xui-th role="columnheader" class="w-28" i18n="@@metrics.col.last">Last</xui-th>
              <xui-th role="columnheader" class="w-28" i18n="@@metrics.col.min">Min</xui-th>
              <xui-th role="columnheader" class="w-28" i18n="@@metrics.col.max">Max</xui-th>
            </xui-tr>
            @for (row of rows(); track row.name) {
              <xui-tr [attr.data-series]="row.name">
                <xui-td role="cell" class="flex-1" truncate
                  ><code class="text-xs">{{ row.name }}</code></xui-td
                >
                <xui-td role="cell" class="w-28">{{ row.last }}</xui-td>
                <xui-td role="cell" class="w-28">{{ row.min }}</xui-td>
                <xui-td role="cell" class="w-28">{{ row.max }}</xui-td>
              </xui-tr>
            }
          </xui-table>
        }
      }
    }
  `
})
export class MetricsExplorer {
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
  protected readonly logsLink = computed(() =>
    links.workspaceLogs(this.subscriptionId(), this.resourceGroup(), this.name())
  );

  protected readonly ranges = timeRanges;
  protected readonly range = signal<TimeRange>(timeRanges[1]);
  protected readonly rangeText = (range: TimeRange): string => range.label;

  protected readonly metric = signal('');
  protected readonly filters = signal<readonly LabelFilter[]>([]);
  protected readonly rate = signal(false);
  protected readonly aggregation = signal<Aggregation>('none');
  protected readonly by = signal<readonly string[]>([]);

  protected readonly custom = signal(false);
  private readonly typed = signal('');

  protected readonly metricNames = signal<readonly string[]>([]);
  protected readonly labelNames = signal<readonly string[]>([]);
  private readonly labelValues = signal<Readonly<Record<number, readonly string[]>>>({});
  protected readonly metricHint = signal(
    $localize`:@@metrics.metricHint.loading:Loading this workspace's metric names…`
  );

  protected readonly groupable = computed(() => this.labelNames().filter(label => label !== '__name__'));

  /** What runs: the builder's expression, or the hand-edited text once the box is unlocked. */
  protected readonly expression = computed(() => {
    if (this.custom()) {
      const text = this.typed().trim();
      return text.length === 0 ? null : text;
    }

    // The rate window follows the range, so a 30-day chart's three-hour step reads three hours.
    return buildPromQL(
      {
        metric: this.metric().trim(),
        filters: this.filters(),
        rate: this.rate(),
        aggregation: this.aggregation(),
        by: this.by()
      },
      rateWindowFor(this.range().ms)
    );
  });

  protected readonly result = pageState<MetricsAnswer>();
  protected readonly answer = computed(() => {
    const state = this.result();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly chartSeries = computed<readonly MetricSeries[]>(() =>
    (this.answer()?.series ?? []).map(s => ({
      name: seriesName(s.labels),
      timestamps: s.points.map(p => p[0] * 1000),
      values: s.points.map(p => p[1]),
      unit: ''
    }))
  );

  protected readonly rows = computed<readonly SeriesRow[]>(() =>
    (this.answer()?.series ?? []).map(s => {
      const values = s.points.map(p => p[1]).filter((v): v is number => v !== null);
      return {
        name: seriesName(s.labels),
        last: formatValue(s.points.at(-1)?.[1] ?? null),
        min: values.length === 0 ? '—' : formatValue(Math.min(...values)),
        max: values.length === 0 ? '—' : formatValue(Math.max(...values))
      };
    })
  );

  protected readonly chartLabel = computed(
    () => $localize`:@@metrics.chartLabel:${this.expression() ?? ''}:expression: over ${this.range().label}:range:`
  );

  protected readonly matcherItems = matchers;
  protected readonly matcherText = (matcher: Matcher): string => matcher;
  protected readonly aggregationItems = aggregations;
  protected readonly aggregationText = (aggregation: Aggregation): string =>
    aggregation === 'none' ? $localize`:@@metrics.aggregation.none:None` : aggregation;

  protected readonly labels = {
    filterLabel: $localize`:@@metrics.filter.label:Label`,
    filterMatcher: $localize`:@@metrics.filter.matcher:Match`,
    filterValue: $localize`:@@metrics.filter.value:Value`,
    removeFilter: $localize`:@@metrics.filter.remove:Remove this filter`,
    truncatedTitle: $localize`:@@metrics.truncatedTitle:Not every series is shown`,
    table: $localize`:@@metrics.table:The series, as numbers`
  };

  private generation = 0;

  constructor() {
    effect(() => {
      const workspace = this.workspace();

      untracked(() => {
        this.generation++;
        this.result.set({ kind: 'idle' });
        this.metricNames.set([]);
        this.labelNames.set([]);
        if (workspace !== null) void this.loadMetricNames(workspace);
      });
    });
  }

  protected onMetric(event: Event): void {
    this.metric.set((event.target as HTMLInputElement).value);
  }

  protected onRange(range: TimeRange | null): void {
    if (range !== null) this.range.set(range);
  }

  protected onMatcher(index: number, matcher: Matcher | null): void {
    if (matcher !== null) this.updateFilter(index, { matcher });
  }

  protected onFilter(index: number, part: 'label' | 'value', event: Event): void {
    this.updateFilter(index, { [part]: (event.target as HTMLInputElement).value });
  }

  protected onAggregation(aggregation: Aggregation | null): void {
    if (aggregation !== null) this.aggregation.set(aggregation);
  }

  protected onCustom(): void {
    // Unlocking starts from what the builder says, so nothing typed so far is lost.
    if (!this.custom()) this.typed.set(this.expression() ?? '');
    this.custom.set(!this.custom());
  }

  protected onExpression(event: Event): void {
    this.typed.set((event.target as HTMLTextAreaElement).value);
  }

  protected addFilter(): void {
    this.filters.set([...this.filters(), { label: '', matcher: '=', value: '' }]);
  }

  protected removeFilter(index: number): void {
    this.filters.set(this.filters().filter((_, i) => i !== index));
    this.labelValues.set({});
  }

  protected toggleBy(label: string): void {
    const by = this.by();
    this.by.set(by.includes(label) ? by.filter(x => x !== label) : [...by, label]);
  }

  protected valuesFor(index: number): readonly string[] {
    return this.labelValues()[index] ?? [];
  }

  protected async loadLabelNames(): Promise<void> {
    const workspace = this.workspace();
    const metric = this.metric().trim();
    if (workspace === null || metric.length === 0) return;

    try {
      this.labelNames.set(await this.api.listMetricLabels(workspace, { match: metric, ...this.labelWindow() }));
    } catch {
      this.labelNames.set([]);
    }
  }

  protected async loadLabelValues(index: number): Promise<void> {
    const workspace = this.workspace();
    const label = this.filters()[index]?.label.trim() ?? '';
    const metric = this.metric().trim();
    if (workspace === null || !/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(label)) return;

    try {
      const values = await this.api.listMetricLabels(workspace, {
        label,
        ...(metric.length > 0 ? { match: metric } : {}),
        ...this.labelWindow()
      });
      this.labelValues.set({ ...this.labelValues(), [index]: values });
    } catch {
      // A value list is a convenience; the field still takes whatever is typed.
    }
  }

  protected async onRun(event: Event): Promise<void> {
    event.preventDefault();

    const workspace = this.workspace();
    const query = this.expression();
    if (workspace === null || query === null) return;

    const { from, to } = windowOf(this.range(), new Date());
    const generation = this.generation;

    await load(
      this.result,
      () => this.api.queryMetrics(workspace, { query, start: stamp(from), end: stamp(to) }),
      () => generation !== this.generation
    );
  }

  private async loadMetricNames(workspace: ResourceAddress): Promise<void> {
    const generation = this.generation;

    try {
      const names = await this.api.listMetricLabels(workspace, { label: '__name__', ...this.labelWindow() });
      if (generation !== this.generation) return;

      this.metricNames.set(names);
      this.metricHint.set(
        names.length === 0
          ? $localize`:@@metrics.metricHint.none:This workspace has received no metrics in the last day.`
          : $localize`:@@metrics.metricHint.some:${names.length}:count: metric names received in the last day. Type to pick one.`
      );
    } catch (error) {
      if (generation !== this.generation) return;
      const state = failure(error);
      this.metricHint.set(state.kind === 'failed' ? state.message : '');
    }
  }

  /** The window the pickers read — the last day, whatever the chart shows. */
  private labelWindow(): { start: string; end: string } {
    const now = new Date();
    return { start: stamp(new Date(now.getTime() - 24 * 60 * 60_000)), end: stamp(now) };
  }

  private updateFilter(index: number, change: Partial<LabelFilter>): void {
    this.filters.set(this.filters().map((filter, i) => (i === index ? { ...filter, ...change } : filter)));
  }
}
