import {
  ChangeDetectionStrategy,
  Component,
  InjectionToken,
  computed,
  effect,
  inject,
  input,
  untracked
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { daysBetween, provideCharts, stackByDay, stackedBarOption } from '@cybercloud/charts';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiEChart } from '@xui/echarts';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiSelect } from '@xui/select';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { CostAnswer, CostGrouping, CostManagementApi, CostScope } from '../../app/api/cost-management';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { BudgetList } from './budget-list';
import {
  Forecast,
  Period,
  breakdown,
  displayName,
  forecastRange,
  isPeriod,
  linearForecast,
  money,
  rangeOf,
  startOfMonth
} from './cost-model';

/**
 * What "now" is for the cost pages. A token so a spec can hold the month still: the default period,
 * the forecast and the chart's days all hang off it.
 */
export const COST_NOW = new InjectionToken<() => Date>('COST_NOW', {
  providedIn: 'root',
  factory: () => () => new Date()
});

/** The groupings the picker offers, in the order it lists them. `day` is the chart's axis, not a choice. */
const dimensions = ['resourceType', 'resourceGroup', 'resource', 'meter'] as const satisfies readonly CostGrouping[];
type Dimension = (typeof dimensions)[number];

function isDimension(value: string | undefined): value is Dimension {
  return value !== undefined && (dimensions as readonly string[]).includes(value);
}

/**
 * Cost analysis for a subscription or one of its resource groups — docs/plan/20 § The pages that are
 * not generated, "Cost analysis: `@xui/echarts` — breakdowns by tag, resource group, service, day.
 * Forecast, budgets". Issue #41.
 *
 * One cost query answers the chart and the table: `granularity: daily`, grouped by the chosen
 * dimension, over the chosen period — a stacked bar per day, and the same rows summed per key
 * beside it. A second query, `groupBy: day` over this month and the week before, answers the
 * forecast (`linearForecast` states the method). On a resource group the group's budgets follow.
 *
 * ⚠ **The URL is the state.** Period, custom dates and grouping are query parameters, so a link to
 * "last month by resource group" is that page for whoever opens it — the reason docs/plan/20 gives
 * for deep links. The pickers navigate; they never hold a value the URL doesn't.
 *
 * ⚠ **The platform decides what is visible.** A reader of one group opening the subscription sees
 * that group's cost and a note that something was withheld; nothing here filters or explains
 * further, because the answer says nothing more about what was hidden and neither may the page.
 *
 * ⚠ **The chart is a picture of the table, never the only copy.** A canvas says nothing to a screen
 * reader, so the breakdown table carries every figure the chart draws, and the chart carries a label
 * naming what it shows.
 *
 * ⚠ **By tag is not offered**, because the platform cannot answer it: the usage ledger carries no
 * tags (docs/plan/22 § What is owed, `cost-by-tag-and-export`).
 */
@Component({
  selector: 'cc-cost-analysis',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    XuiButton,
    XuiCallout,
    XuiEChart,
    XuiInput,
    XuiNonIdealState,
    XuiSelect,
    XuiTable,
    XuiTr,
    XuiTh,
    XuiTd,
    NeedsTenant,
    PageStatus,
    BudgetList
  ],
  providers: [provideCharts()],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId(); as tenantId) {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold" i18n="@@cost.heading">Cost analysis</h1>
          <p class="text-foreground-muted mt-1 text-sm">
            <a class="underline" [routerLink]="scopeLink()">{{ scopeName() }}</a>
          </p>
        </div>
        <a xuiButton variant="outline" size="sm" [routerLink]="invoicesLink" i18n="@@cost.invoices">Invoices</a>
      </div>

      <div class="mt-4 flex flex-wrap items-end gap-3">
        <div class="flex flex-col gap-1">
          <span class="text-foreground-muted text-xs font-medium" id="cc-cost-period-label" i18n="@@cost.period"
            >Period</span
          >
          <xui-select
            class="min-w-48"
            [items]="periodChoices"
            [itemText]="periodText"
            [filterable]="false"
            [value]="period$()"
            aria-labelledby="cc-cost-period-label"
            (valueChange)="onPeriod($event)"
          />
        </div>

        @if (period$() === 'custom') {
          <div class="flex flex-col gap-1">
            <label class="text-foreground-muted text-xs font-medium" for="cc-cost-from" i18n="@@cost.from">From</label>
            <input xuiInput id="cc-cost-from" type="date" [value]="from() ?? ''" (change)="onDate('from', $event)" />
          </div>
          <div class="flex flex-col gap-1">
            <label class="text-foreground-muted text-xs font-medium" for="cc-cost-to" i18n="@@cost.to"
              >To, inclusive</label
            >
            <input xuiInput id="cc-cost-to" type="date" [value]="to() ?? ''" (change)="onDate('to', $event)" />
          </div>
        }

        <div class="flex flex-col gap-1">
          <span class="text-foreground-muted text-xs font-medium" id="cc-cost-dimension-label" i18n="@@cost.dimension"
            >Group by</span
          >
          <xui-select
            class="min-w-48"
            [items]="dimensionChoices()"
            [itemText]="dimensionText"
            [filterable]="false"
            [value]="dimension()"
            aria-labelledby="cc-cost-dimension-label"
            (valueChange)="onDimension($event)"
          />
        </div>
      </div>

      @if (range() === null) {
        <xui-callout class="mt-4" color="warning" [title]="customTitle">
          <p i18n="@@cost.customBody">Pick a first and a last day, the last on or after the first.</p>
        </xui-callout>
      } @else {
        <cc-page-status [state]="state()" [retryLink]="scopeLink()" />
      }

      @if (answer(); as answer) {
        <section class="mt-4 grid gap-3 sm:grid-cols-2" aria-label="Totals" i18n-aria-label="@@cost.totals">
          <div class="border-border rounded-md border p-4">
            <p class="text-foreground-muted text-xs font-medium" i18n="@@cost.total">Total for the period</p>
            <p class="mt-1 text-2xl font-semibold" data-figure="total">{{ money(answer.total, answer.currency) }}</p>
          </div>
          <div class="border-border rounded-md border p-4">
            <p class="text-foreground-muted text-xs font-medium">
              <ng-container i18n="@@cost.forecast">Forecast for</ng-container> {{ monthName() }}
            </p>
            @if (forecast(); as forecast) {
              <p class="mt-1 text-2xl font-semibold" data-figure="forecast">
                {{ money(forecast.forecast, answer.currency) }}
              </p>
              <p class="text-foreground-muted mt-1 text-xs" data-figure="forecast-method">
                <ng-container i18n="@@cost.forecastMethod"
                  >An estimate: this month so far, {{ money(forecast.actual, answer.currency) }}, plus the last seven
                  days' rate of {{ money(forecast.perDay, answer.currency) }} a day for the days left.</ng-container
                >
              </p>
            } @else {
              <p class="text-foreground-muted mt-1 text-sm" i18n="@@cost.forecastPending">Not available.</p>
            }
          </div>
        </section>

        @if (answer.filtered) {
          <xui-callout class="mt-4" color="info" [title]="filteredTitle">
            <p i18n="@@cost.filteredBody">
              You can read only part of this scope, so these figures cover what you can read. Any cost elsewhere in it
              isn't shown, and this page can't tell you whether there is any.
            </p>
          </xui-callout>
        }

        @if (answer.rows.length === 0) {
          <xui-non-ideal-state class="mt-10" [title]="emptyTitle" [description]="emptyDescription" />
        } @else {
          <xui-echart class="mt-6 h-80" [option]="chart()" [aria-label]="chartLabel()" />

          <!--
            ⚠ role="columnheader" and role="cell" by hand: @xui/table 3.0.0 gives xui-tr role="row" and its
            cells no role at all, so axe fails every row with aria-required-children. docs/plan/20
            § What is owed, xui-table-cell-roles — the fix is xUI's, and these attributes go when it ships.
          -->
          <xui-table class="mt-4" striped [attr.aria-label]="tableLabel()">
            <xui-tr>
              <xui-th role="columnheader" class="flex-1">{{ dimensionText(dimension()) }}</xui-th>
              <xui-th role="columnheader" class="w-40" i18n="@@cost.col.amount">Cost</xui-th>
              <xui-th role="columnheader" class="w-24" i18n="@@cost.col.share">Share</xui-th>
            </xui-tr>
            @for (row of table(); track row.name) {
              <xui-tr>
                <xui-td role="cell" class="flex-1" truncate [attr.title]="row.name">{{ label(row.name) }}</xui-td>
                <xui-td role="cell" class="w-40">{{ money(row.amount, answer.currency) }}</xui-td>
                <xui-td role="cell" class="w-24">{{ row.share }} %</xui-td>
              </xui-tr>
            }
          </xui-table>
        }
      }

      @if (resourceGroup(); as group) {
        <cc-budget-list
          class="mt-8"
          [tenantId]="tenantId"
          [subscriptionId]="subscriptionId()"
          [resourceGroup]="group"
        />
      } @else {
        <p class="text-foreground-muted mt-8 text-sm" i18n="@@cost.budgetsElsewhere">
          Budgets live in a resource group. Open a group's cost analysis to see and set its budgets.
        </p>
      }
    } @else {
      <cc-needs-tenant />
    }
  `
})
export class CostAnalysis {
  readonly subscriptionId = input.required<string>();
  /** Absent on the subscription's route. */
  readonly resourceGroup = input<string>();
  /** Bound from `?period=`; anything but a known period is this month. */
  readonly period = input<string>();
  /** Bound from `?from=` and `?to=`, `yyyy-MM-dd`, for a custom period. */
  readonly from = input<string>();
  readonly to = input<string>();
  /** Bound from `?groupBy=`. */
  readonly groupBy = input<string>();

  private readonly api = inject(CostManagementApi);
  private readonly router = inject(Router);
  private readonly blades = inject(BladeStackStore);
  private readonly now = inject(COST_NOW);

  protected readonly tenantId = activeTenantId();
  protected readonly state = pageState<CostAnswer>();
  protected readonly forecastState = pageState<Forecast>();
  protected readonly money = money;

  protected readonly scope = computed((): CostScope | null => {
    const tenantId = this.tenantId();
    if (tenantId === null) return null;

    const group = this.resourceGroup();
    return group === undefined
      ? { kind: 'subscription', tenantId, subscriptionId: this.subscriptionId() }
      : { kind: 'resourceGroup', tenantId, subscriptionId: this.subscriptionId(), resourceGroup: group };
  });

  protected readonly period$ = computed((): Period => {
    const period = this.period();
    return isPeriod(period) ? period : 'month';
  });

  protected readonly range = computed(() => rangeOf(this.period$(), this.now(), this.from(), this.to()));

  protected readonly dimensionChoices = computed((): readonly Dimension[] =>
    this.resourceGroup() === undefined ? dimensions : dimensions.filter(d => d !== 'resourceGroup')
  );

  protected readonly dimension = computed((): Dimension => {
    const wanted = this.groupBy();
    return isDimension(wanted) && this.dimensionChoices().includes(wanted) ? wanted : 'resourceType';
  });

  protected readonly answer = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly forecast = computed(() => {
    const state = this.forecastState();
    return state.kind === 'ready' ? state.value : null;
  });

  /** Short names for the rows, falling back to the full name wherever two would read the same. */
  private readonly labels = computed(() => {
    const names = [...new Set((this.answer()?.rows ?? []).map(r => r.name))];
    const short = names.map(displayName);
    return new Map(
      names.map((name, i) => [name, short.indexOf(short[i]) === short.lastIndexOf(short[i]) ? short[i] : name])
    );
  });

  protected readonly table = computed(() => breakdown(this.answer()?.rows ?? []));

  protected readonly chart = computed(() => {
    const answer = this.answer();
    const range = this.range();
    if (answer === null || range === null) return {};

    const labels = this.labels();
    const rows = answer.rows.map(r => ({ day: r.day ?? '', name: labels.get(r.name) ?? r.name, amount: r.amount }));
    return stackedBarOption(stackByDay(rows, daysBetween(range.from, range.to)), answer.currency);
  });

  protected readonly scopeLink = computed(() => {
    const group = this.resourceGroup();
    return group === undefined
      ? links.subscription(this.subscriptionId())
      : links.resourceGroup(this.subscriptionId(), group);
  });

  protected readonly scopeName = computed(() => this.resourceGroup() ?? this.subscriptionId());
  protected readonly monthName = computed(() =>
    startOfMonth(this.now()).toLocaleDateString('en', { month: 'long', year: 'numeric', timeZone: 'UTC' })
  );

  protected readonly invoicesLink = links.invoices();
  protected readonly periodChoices: readonly Period[] = ['month', 'lastMonth', '30d', '90d', 'custom'];
  protected readonly customTitle = $localize`:@@cost.customTitle:Choose the days`;
  protected readonly filteredTitle = $localize`:@@cost.filteredTitle:Only what you can read`;
  protected readonly emptyTitle = $localize`:@@cost.emptyTitle:No cost in this period`;
  protected readonly emptyDescription = $localize`:@@cost.emptyDescription:Nothing you can read in this scope was metered in this period.`;

  protected readonly periodText = (period: Period): string =>
    ({
      month: $localize`:@@cost.period.month:This month`,
      lastMonth: $localize`:@@cost.period.lastMonth:Last month`,
      '30d': $localize`:@@cost.period.30d:Last 30 days`,
      '90d': $localize`:@@cost.period.90d:Last 90 days`,
      custom: $localize`:@@cost.period.custom:Custom`
    })[period];

  protected readonly dimensionText = (dimension: Dimension): string =>
    ({
      resourceType: $localize`:@@cost.dimension.resourceType:Service`,
      resourceGroup: $localize`:@@cost.dimension.resourceGroup:Resource group`,
      resource: $localize`:@@cost.dimension.resource:Resource`,
      meter: $localize`:@@cost.dimension.meter:Meter`
    })[dimension];

  protected readonly chartLabel = computed(
    () =>
      $localize`:@@cost.chartLabel:Daily cost by ${this.dimensionText(this.dimension())}:dimension:, stacked. The table below lists the same figures.`
  );

  protected readonly tableLabel = computed(
    () => $localize`:@@cost.tableLabel:Cost by ${this.dimensionText(this.dimension())}:dimension: for the period`
  );

  constructor() {
    effect(() => {
      const scope = this.scope();
      const range = this.range();
      const dimension = this.dimension();

      untracked(() => {
        const route = this.scopeLink() + '/cost';
        this.blades.open({ id: route, title: $localize`:@@cost.blade:Cost analysis`, route });

        if (scope === null || range === null) {
          this.state.set({ kind: 'idle' });
          return;
        }

        void load(
          this.state,
          () => this.api.query(scope, range.from, range.to, dimension, 'daily'),
          () => this.scope() !== scope || this.range() !== range || this.dimension() !== dimension
        );
      });
    });

    // The forecast is this month's whatever period is on screen, so it reloads with the scope only.
    effect(() => {
      const scope = this.scope();

      untracked(() => {
        if (scope === null) {
          this.forecastState.set({ kind: 'idle' });
          return;
        }

        const now = this.now();
        const window = forecastRange(now);
        void load(
          this.forecastState,
          async () => linearForecast((await this.api.query(scope, window.from, window.to, 'day')).rows, now),
          () => this.scope() !== scope
        );
      });
    });
  }

  protected label(name: string): string {
    return this.labels().get(name) ?? name;
  }

  protected onPeriod(period: Period | null): void {
    void this.router.navigate([], { queryParams: { period: period ?? null }, queryParamsHandling: 'merge' });
  }

  protected onDimension(dimension: Dimension | null): void {
    void this.router.navigate([], { queryParams: { groupBy: dimension ?? null }, queryParamsHandling: 'merge' });
  }

  protected onDate(which: 'from' | 'to', event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    void this.router.navigate([], { queryParams: { [which]: value || null }, queryParamsHandling: 'merge' });
  }
}
