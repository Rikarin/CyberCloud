import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiDescriptions, XuiDescriptionsItem } from '@xui/descriptions';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { CostManagementApi, Invoice, InvoiceLine } from '../../app/api/cost-management';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { money } from './cost-model';
import { monthOf } from './invoices';

/**
 * One invoice with its lines — what a link from the invoice email opens. Issue #41.
 *
 * ⚠ **Printed as stored.** Every amount is the finalized document's, rounded once when it was issued
 * (`MoneyRounding`, rule 2), and the page adds nothing up: the subtotal, the tax and the total are
 * the invoice's own fields. A page that summed the lines would print a total the customer was not
 * charged whenever a credit note or a rounding rule made the two differ.
 *
 * ⚠ **A line whose quantity was declared, not measured, is marked** and the invoice's notes say why
 * (docs/plan/22 § What is owed, `storage-is-declared-not-observed`).
 */
@Component({
  selector: 'cc-invoice-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiDescriptions, XuiDescriptionsItem, XuiTable, XuiTr, XuiTh, XuiTd, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold">
        <ng-container i18n="@@invoice.heading">Invoice</ng-container> {{ number() }}
      </h1>
      <p class="text-foreground-muted mt-1 text-sm">
        <a class="underline" [routerLink]="backLink" i18n="@@invoice.back">All invoices</a>
      </p>

      <cc-page-status [state]="state()" [retryLink]="backLink" />

      @if (invoice(); as invoice) {
        <xui-descriptions class="mt-4" [column]="2" bordered [title]="overviewTitle">
          <xui-descriptions-item [label]="labels.period">{{ monthOf(invoice) }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.finalized">{{ invoice.finalizedAt ?? '—' }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.issuer"
            >{{ invoice.issuer.legalName }} · {{ invoice.issuer.vatId }}</xui-descriptions-item
          >
          <xui-descriptions-item [label]="labels.customer"
            >{{ invoice.customer.legalName }}{{ invoice.customer.vatId ? ' · ' + invoice.customer.vatId : '' }}
          </xui-descriptions-item>
        </xui-descriptions>

        <h2 class="mt-6 text-base font-semibold" id="cc-invoice-lines" i18n="@@invoice.lines">Lines</h2>
        <!-- Cell roles by hand, for the reason cost-analysis.ts gives. -->
        <xui-table class="mt-2" striped aria-labelledby="cc-invoice-lines">
          <xui-tr>
            <xui-th role="columnheader" class="flex-1" i18n="@@invoice.col.description">Description</xui-th>
            <xui-th role="columnheader" class="w-72" i18n="@@invoice.col.subscription">Subscription</xui-th>
            <xui-th role="columnheader" class="w-48" i18n="@@invoice.col.quantity">Quantity</xui-th>
            <xui-th role="columnheader" class="w-32" i18n="@@invoice.col.amount">Amount</xui-th>
          </xui-tr>
          @for (line of invoice.lines; track $index) {
            <xui-tr [attr.data-line]="line.meter">
              <xui-td role="cell" class="flex-1">
                {{ line.description || line.meter }}
                @if (line.declaredQuantity) {
                  <span class="text-foreground-muted text-xs" i18n="@@invoice.declared">(declared size)</span>
                }
              </xui-td>
              <xui-td role="cell" class="w-72" truncate
                ><a class="underline" [routerLink]="subscriptionLink(line)">{{ line.subscriptionId }}</a></xui-td
              >
              <xui-td role="cell" class="w-48">{{ quantity(line) }}</xui-td>
              <xui-td role="cell" class="w-32">{{ money(line.amount, invoice.currency) }}</xui-td>
            </xui-tr>
          }
        </xui-table>

        <dl class="mt-4 ml-auto grid max-w-sm grid-cols-2 gap-x-6 gap-y-1 text-sm">
          <dt class="text-foreground-muted" i18n="@@invoice.subtotal">Subtotal</dt>
          <dd class="text-right" data-figure="subtotal">{{ money(invoice.subtotal, invoice.currency) }}</dd>
          <dt class="text-foreground-muted">{{ taxLabel(invoice) }}</dt>
          <dd class="text-right" data-figure="tax">{{ money(invoice.tax.amount, invoice.currency) }}</dd>
          <dt class="font-semibold" i18n="@@invoice.total">Total</dt>
          <dd class="text-right font-semibold" data-figure="total">{{ money(invoice.total, invoice.currency) }}</dd>
        </dl>

        @if (invoice.notes.length > 0) {
          <ul class="text-foreground-muted mt-4 list-disc pl-5 text-xs">
            @for (note of invoice.notes; track $index) {
              <li>{{ note }}</li>
            }
          </ul>
        }
      }
    }
  `
})
export class InvoiceView {
  /** The route's `:number` — the number printed on the invoice. */
  readonly number = input.required<string>();

  private readonly api = inject(CostManagementApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly state = pageState<Invoice>();
  protected readonly invoice = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly money = money;
  protected readonly monthOf = monthOf;
  protected readonly backLink = links.invoices();
  protected readonly overviewTitle = $localize`:@@invoice.overview:Overview`;
  protected readonly labels = {
    period: $localize`:@@invoice.period:Period`,
    finalized: $localize`:@@invoice.finalized:Issued`,
    issuer: $localize`:@@invoice.issuer:From`,
    customer: $localize`:@@invoice.customer:To`
  };

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();
      const number = this.number();

      untracked(() => {
        const route = links.invoice(number);
        this.blades.open({ id: route, title: number, route });

        if (tenantId === null) return;
        void load(
          this.state,
          () => this.api.invoice(tenantId, number),
          () => this.tenantId() !== tenantId || this.number() !== number
        );
      });
    });
  }

  protected subscriptionLink(line: InvoiceLine): string {
    return links.subscription(line.subscriptionId);
  }

  /** The quantity to the precision a person reads — the meter reports twelve places. */
  protected quantity(line: InvoiceLine): string {
    return `${Number(line.quantity.toFixed(4))} ${line.unit}`;
  }

  protected taxLabel(invoice: Invoice): string {
    switch (invoice.tax.treatment) {
      case 'reverseCharge':
        return $localize`:@@invoice.tax.reverseCharge:VAT, reverse charge`;
      case 'outOfScope':
        return $localize`:@@invoice.tax.outOfScope:VAT, out of scope`;
      default:
        return $localize`:@@invoice.tax.standard:VAT ${invoice.tax.ratePercent}:rate: %`;
    }
  }
}
