import { ChangeDetectionStrategy, Component, computed, effect, inject, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { CostManagementApi, Invoice } from '../../app/api/cost-management';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { money } from './cost-model';

/** An invoice's month as a person names it — `August 2026`. */
export function monthOf(invoice: Pick<Invoice, 'periodStart'>): string {
  const start = new Date(invoice.periodStart);
  return Number.isNaN(start.getTime())
    ? invoice.periodStart
    : start.toLocaleDateString('en', { month: 'long', year: 'numeric', timeZone: 'UTC' });
}

/**
 * The tenant's invoices — docs/plan/22 § What is owed, `billing-http-surface`, the list half. Issue #41.
 *
 * ⚠ **A tenant's, and only a tenant reader's.** An invoice carries every attached subscription's
 * lines and the tenant's legal profile, so the platform answers it to `read` on the tenant and a
 * `404` to anyone else — a subscription owner included. The page says who can see them when it gets
 * the `404`, because "not found" alone reads as "you have no invoices".
 *
 * ⚠ **Finalized invoices only**, newest first. The running month is a draft the platform rates on
 * every read; its figure is on the cost analysis page, and a draft over HTTP is owed
 * (docs/plan/20 § What is owed, `invoice-draft-over-http`).
 */
@Component({
  selector: 'cc-invoices',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiNonIdealState, XuiTable, XuiTr, XuiTh, XuiTd, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" id="cc-invoices-heading" i18n="@@invoices.heading">Invoices</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@invoices.subheading">
        Issued to this tenant once a month closes, two days into the next.
      </p>

      <cc-page-status [state]="state()" />
      @if (state().kind === 'failed' && notFound()) {
        <p class="text-foreground-muted mt-2 text-sm" i18n="@@invoices.whoCanSee">
          Invoices are visible to owners, contributors and readers of the tenant. A role on a subscription or a resource
          group doesn't include them.
        </p>
      }

      @if (invoices(); as invoices) {
        @if (invoices.length === 0) {
          <xui-non-ideal-state class="mt-10" [title]="emptyTitle" [description]="emptyDescription" />
        } @else {
          <!-- Cell roles by hand, for the reason cost-analysis.ts gives. -->
          <xui-table class="mt-4" striped aria-labelledby="cc-invoices-heading">
            <xui-tr>
              <xui-th role="columnheader" class="w-56" i18n="@@invoices.col.number">Number</xui-th>
              <xui-th role="columnheader" class="w-44" i18n="@@invoices.col.period">Period</xui-th>
              <xui-th role="columnheader" class="w-24" i18n="@@invoices.col.lines">Lines</xui-th>
              <xui-th role="columnheader" class="w-36" i18n="@@invoices.col.tax">Tax</xui-th>
              <xui-th role="columnheader" class="flex-1" i18n="@@invoices.col.total">Total</xui-th>
            </xui-tr>
            @for (invoice of invoices; track invoice.number) {
              <xui-tr [attr.data-invoice]="invoice.number">
                <xui-td role="cell" class="w-56"
                  ><a class="underline" [routerLink]="link(invoice)">{{ invoice.number }}</a></xui-td
                >
                <xui-td role="cell" class="w-44">{{ monthOf(invoice) }}</xui-td>
                <xui-td role="cell" class="w-24">{{ invoice.lines.length }}</xui-td>
                <xui-td role="cell" class="w-36">{{ money(invoice.tax.amount, invoice.currency) }}</xui-td>
                <xui-td role="cell" class="flex-1" data-figure="total">{{
                  money(invoice.total, invoice.currency)
                }}</xui-td>
              </xui-tr>
            }
          </xui-table>
        }
      }
    }
  `
})
export class Invoices {
  private readonly api = inject(CostManagementApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly state = pageState<readonly Invoice[]>();
  protected readonly invoices = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });
  protected readonly notFound = computed(() => {
    const state = this.state();
    return state.kind === 'failed' && state.status === 404;
  });

  protected readonly money = money;
  protected readonly monthOf = monthOf;
  protected readonly emptyTitle = $localize`:@@invoices.emptyTitle:No invoices yet`;
  protected readonly emptyDescription = $localize`:@@invoices.emptyDescription:An invoice is issued for each month once it closes. Until then, the month's cost is on each subscription's cost analysis.`;

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();

      untracked(() => {
        const route = links.invoices();
        this.blades.open({ id: route, title: $localize`:@@invoices.blade:Invoices`, route });

        if (tenantId === null) return;
        void load(
          this.state,
          () => this.api.invoices(tenantId),
          () => this.tenantId() !== tenantId
        );
      });
    });
  }

  protected link(invoice: Invoice): string {
    return links.invoice(invoice.number);
  }
}
