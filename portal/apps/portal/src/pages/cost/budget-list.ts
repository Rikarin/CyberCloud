import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BillingBudgetsResource, BillingBudgetsShowStatusResult, Page } from '@cybercloud/api';
import { XuiButton } from '@xui/button';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiProgressBar } from '@xui/progress-bar';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { XuiTag } from '@xui/tag';
import { ApiCallError } from '../../app/api/http-transport';
import { skipTokenOf } from '../../app/api/paging';
import { PlatformApi } from '../../app/api/platform-api';
import { ResourceAddress } from '../../app/api/resource-verbs';
import { links } from '../../app/routes/portal-links';
import { PageStatus, load, pageState } from '../shared/page-state';
import { money } from './cost-model';

/** `CyberCloud.Billing/budgets` — the type the generated create and edit blades are opened for. */
export const BUDGET_TYPE = 'CyberCloud.Billing/budgets';

/** One threshold as the list shows it: which kind, what percentage, and whether it has fired this period. */
export interface ThresholdState {
  readonly kind: 'actual' | 'forecast';
  readonly percent: number;
  readonly fired: boolean;
}

/** A budget and what its grain holds, or why the page could not ask. */
export interface BudgetRow {
  readonly budget: BillingBudgetsResource;
  /** `null` when `showStatus` refused — the reason is in {@link statusError}. */
  readonly status: BillingBudgetsShowStatusResult | null;
  readonly statusError: string;
}

/**
 * Every threshold the body declares, each marked fired or not from the status — the budget's
 * "threshold state". A fired percentage the body no longer declares (the threshold was edited out
 * this period) is still shown, because it did page someone.
 */
export function thresholdsOf(row: BudgetRow): ThresholdState[] {
  const declared = row.budget.properties?.thresholds ?? {};
  const states: ThresholdState[] = [];

  for (const kind of ['actual', 'forecast'] as const) {
    const fired = new Set(kind === 'actual' ? (row.status?.firedActual ?? []) : (row.status?.firedForecast ?? []));
    const percents = new Set([...(declared[kind] ?? []), ...fired]);

    for (const percent of [...percents].sort((a, b) => a - b))
      states.push({ kind, percent, fired: fired.has(percent) });
  }

  return states;
}

/**
 * A resource group's budgets with their threshold state — the budgets half of docs/plan/20's cost
 * analysis row. Issue #41.
 *
 * ⚠ **Everything goes through the generated client.** The list is `listBudget`, each figure is the
 * `showStatus` action (`showStatusBudget`), and New and Edit open the generated create and edit
 * blades for `CyberCloud.Billing/budgets`, which `PUT` through `createOrUpdateBudget` from the
 * type's published form — so a budget written here is checked against the same schema the CLI's is.
 *
 * ⚠ **One status call per budget, and a refusal is the row's, not the page's.** A budget whose first
 * reconcile hasn't written it answers `409` until it has; one row saying so is better than a page
 * that shows none. A budget answers `403` to a caller who can't read what it covers, the group or
 * with `scope: subscription` the subscription, because its figures are that scope's spend. The
 * figures are the last hourly evaluation's, and the row says when that was.
 */
@Component({
  selector: 'cc-budget-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiNonIdealState, XuiProgressBar, XuiTable, XuiTr, XuiTh, XuiTd, XuiTag, PageStatus],
  host: { class: 'block' },
  template: `
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h2 class="text-base font-semibold" id="cc-budgets-heading" i18n="@@budgets.heading">Budgets</h2>
      <a xuiButton color="primary" size="sm" [routerLink]="createLink()" i18n="@@budgets.create">New budget</a>
    </div>

    <cc-page-status [state]="state()" />

    @if (rows(); as rows) {
      @if (rows.length === 0) {
        <xui-non-ideal-state class="mt-6" [title]="emptyTitle" [description]="emptyDescription" />
      } @else {
        <!-- Cell roles by hand, for the reason cost-analysis.ts gives. -->
        <xui-table class="mt-3" striped aria-labelledby="cc-budgets-heading">
          <xui-tr>
            <xui-th role="columnheader" class="w-48" i18n="@@budgets.col.name">Name</xui-th>
            <xui-th role="columnheader" class="w-40" i18n="@@budgets.col.amount">Amount</xui-th>
            <xui-th role="columnheader" class="w-56" i18n="@@budgets.col.used">Spent</xui-th>
            <xui-th role="columnheader" class="w-32" i18n="@@budgets.col.forecast">Forecast</xui-th>
            <xui-th role="columnheader" class="flex-1" i18n="@@budgets.col.thresholds">Thresholds</xui-th>
            <xui-th role="columnheader" class="w-20"
              ><span class="sr-only" i18n="@@budgets.col.actions">Actions</span></xui-th
            >
          </xui-tr>
          @for (row of rows; track row.budget.id) {
            <xui-tr [attr.data-budget]="row.budget.name">
              <xui-td role="cell" class="w-48" truncate
                ><a class="underline" [routerLink]="budgetLink(row)">{{ row.budget.name }}</a></xui-td
              >
              <xui-td role="cell" class="w-40"
                >{{ amountOf(row) }} <span class="text-foreground-muted text-xs">{{ periodOf(row) }}</span></xui-td
              >
              <xui-td role="cell" class="w-56">
                @if (figures(row); as status) {
                  <div class="flex w-full flex-col gap-1">
                    <span class="text-sm" data-figure="actual">{{ money(status.actual, status.currency) }}</span>
                    <xui-progress-bar
                      size="sm"
                      [value]="fraction(status)"
                      [color]="fraction(status) >= 1 ? 'error' : fraction(status) >= 0.8 ? 'warning' : 'primary'"
                      [aria-label]="usedLabel(row)"
                    />
                  </div>
                } @else {
                  <span class="text-foreground-muted text-xs" data-figure="not-evaluated">{{ notEvaluated(row) }}</span>
                }
              </xui-td>
              <xui-td role="cell" class="w-32">
                @if (figures(row); as status) {
                  <span data-figure="forecast">{{ money(status.forecast, status.currency) }}</span>
                } @else {
                  —
                }
              </xui-td>
              <xui-td role="cell" class="flex-1">
                <div class="flex flex-wrap gap-1">
                  @for (threshold of thresholds(row); track threshold.kind + threshold.percent) {
                    <xui-tag
                      minimal
                      [color]="threshold.fired ? 'error' : 'none'"
                      [attr.data-threshold]="
                        threshold.kind + ':' + threshold.percent + (threshold.fired ? ':fired' : '')
                      "
                      >{{ thresholdText(threshold) }}</xui-tag
                    >
                  }
                </div>
                @if (row.status?.lastError; as error) {
                  <p class="text-warning mt-1 text-xs">{{ error }}</p>
                }
                @if (row.status?.lastEvaluatedAt; as at) {
                  <p class="text-foreground-muted mt-1 text-xs">
                    <ng-container i18n="@@budgets.asOf">As of</ng-container> {{ stamp(at) }}
                  </p>
                }
              </xui-td>
              <xui-td role="cell" class="w-20"
                ><a class="underline" [routerLink]="editLink(row)" i18n="@@budgets.edit">Edit</a></xui-td
              >
            </xui-tr>
          }
        </xui-table>
      }
    }
  `
})
export class BudgetList {
  readonly tenantId = input.required<string>();
  readonly subscriptionId = input.required<string>();
  readonly resourceGroup = input.required<string>();

  private readonly api = inject(PlatformApi);

  protected readonly state = pageState<readonly BudgetRow[]>();
  protected readonly rows = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly money = money;
  protected readonly thresholds = thresholdsOf;
  protected readonly createLink = computed(() =>
    links.create(this.subscriptionId(), this.resourceGroup(), BUDGET_TYPE)
  );

  protected readonly emptyTitle = $localize`:@@budgets.emptyTitle:No budgets`;
  protected readonly emptyDescription = $localize`:@@budgets.emptyDescription:A budget alerts when this group's cost, or its forecast, passes a percentage of an amount you set.`;

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();
      const subscriptionId = this.subscriptionId();
      const resourceGroup = this.resourceGroup();

      untracked(
        () =>
          void load(
            this.state,
            () => this.loadAll(tenantId, subscriptionId, resourceGroup),
            () =>
              this.tenantId() !== tenantId ||
              this.subscriptionId() !== subscriptionId ||
              this.resourceGroup() !== resourceGroup
          )
      );
    });
  }

  /** Every page of the group's budgets, then each one's status, in parallel. */
  private async loadAll(tenantId: string, subscriptionId: string, resourceGroup: string): Promise<BudgetRow[]> {
    const budgets: BillingBudgetsResource[] = [];
    let skipToken: string | null = null;

    do {
      const page: Page<BillingBudgetsResource> = (
        await this.api.listBudget(tenantId, subscriptionId, resourceGroup, skipToken === null ? {} : { skipToken })
      ).value;
      budgets.push(...page.value);
      skipToken = page.nextLink === undefined ? null : skipTokenOf(page.nextLink);
    } while (skipToken !== null);

    return Promise.all(
      budgets.map(async budget => {
        try {
          const status = (await this.api.showStatusBudget(tenantId, subscriptionId, resourceGroup, budget.name)).value;
          return { budget, status, statusError: '' };
        } catch (error) {
          return {
            budget,
            status: null,
            statusError: error instanceof ApiCallError ? error.error.message : String(error)
          };
        }
      })
    );
  }

  protected address(row: BudgetRow): ResourceAddress {
    return {
      tenantId: this.tenantId(),
      subscriptionId: this.subscriptionId(),
      resourceGroup: this.resourceGroup(),
      parents: [],
      name: row.budget.name
    };
  }

  protected budgetLink(row: BudgetRow): string {
    return links.resource(this.address(row), BUDGET_TYPE);
  }

  protected editLink(row: BudgetRow): string {
    return links.edit(this.address(row), BUDGET_TYPE);
  }

  protected amountOf(row: BudgetRow): string {
    return money(row.budget.properties?.amount ?? 0, row.status?.currency ?? '');
  }

  protected periodOf(row: BudgetRow): string {
    switch (row.budget.properties?.period ?? 'monthly') {
      case 'quarterly':
        return $localize`:@@budgets.period.quarterly:a quarter`;
      case 'annually':
        return $localize`:@@budgets.period.annually:a year`;
      default:
        return $localize`:@@budgets.period.monthly:a month`;
    }
  }

  /** The status, when it carries figures — an evaluated, enabled budget's. */
  protected figures(row: BudgetRow): BillingBudgetsShowStatusResult | null {
    return row.status?.evaluated === true ? row.status : null;
  }

  /** An instant as `yyyy-MM-dd HH:mm UTC` — the evaluation is hourly, so minutes are the finest that means anything. */
  protected stamp(at: string): string {
    const parsed = new Date(at);
    return Number.isNaN(parsed.getTime()) ? at : `${parsed.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
  }

  /** Spent over amount, 0 when there is no amount to divide by. The bar clamps anything past 1. */
  protected fraction(status: BillingBudgetsShowStatusResult): number {
    return status.amount > 0 ? status.actual / status.amount : 0;
  }

  protected usedLabel(row: BudgetRow): string {
    return $localize`:@@budgets.usedLabel:Share of ${row.budget.name}:name: spent`;
  }

  protected notEvaluated(row: BudgetRow): string {
    if (row.status === null) return row.statusError;
    if (row.budget.properties?.enabled === false) return $localize`:@@budgets.disabled:Disabled`;
    return $localize`:@@budgets.notEvaluated:Not evaluated yet — budgets are evaluated hourly.`;
  }

  protected thresholdText(threshold: ThresholdState): string {
    return threshold.kind === 'actual'
      ? $localize`:@@budgets.threshold.actual:${threshold.percent}:percent: %`
      : $localize`:@@budgets.threshold.forecast:${threshold.percent}:percent: % forecast`;
  }
}
