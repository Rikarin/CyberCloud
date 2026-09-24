import { Injectable, inject } from '@angular/core';
import { HttpApiTransport } from './http-transport';
import { scopePath } from './role-assignments';

/** `CostQueryBody.GroupingValues` — how a cost answer's rows are keyed. */
export const costGroupings = ['resource', 'resourceGroup', 'resourceType', 'meter', 'day'] as const;
export type CostGrouping = (typeof costGroupings)[number];

/** `CostQueryBody.GranularityValues` — `daily` splits every row by UTC day as well. */
export type CostGranularity = 'none' | 'daily';

/** Where a cost question is asked: a subscription or one of its resource groups, never wider. */
export type CostScope =
  | { readonly kind: 'subscription'; readonly tenantId: string; readonly subscriptionId: string }
  | {
      readonly kind: 'resourceGroup';
      readonly tenantId: string;
      readonly subscriptionId: string;
      readonly resourceGroup: string;
    };

/** One row of a cost answer, as `CostQueryBody.Render` writes it. */
export interface CostRow {
  /** The UTC day, `yyyy-MM-dd` — present on a `daily` answer only. */
  readonly day?: string;
  /** A resource path, a group name, a type, a meter name, or a day. */
  readonly name: string;
  /** Rounded for display. ⚠ The rows need not sum to `total`, which is the unrounded sum rounded once. */
  readonly amount: number;
  /** For `meter` rows only; units do not add across meters. */
  readonly quantity?: number;
}

/** A cost answer. */
export interface CostAnswer {
  readonly currency: string;
  /** The period answered, widened to whole hours. */
  readonly from: string;
  readonly to: string;
  readonly groupBy: CostGrouping;
  readonly granularity: CostGranularity;
  readonly total: number;
  /**
   * Whether the caller may read less than the whole scope, so the answer may leave usage out. ⚠ About
   * the caller's access, never about the usage: it's the same whether or not anything they can't read
   * was used, or it would report exactly that.
   */
  readonly filtered: boolean;
  readonly rows: readonly CostRow[];
}

/** One line of an invoice: one meter in one subscription for the month. */
export interface InvoiceLine {
  readonly subscriptionId: string;
  readonly meter: string;
  readonly description: string;
  readonly unit: string;
  readonly quantity: number;
  /** Rounded once, when the invoice was finalized. */
  readonly amount: number;
  /** The quantity is the size a resource declared, not one anything measured — the invoice's notes say why. */
  readonly declaredQuantity: boolean;
}

/** A finalized invoice, as `InvoiceBody.Render` writes it. */
export interface Invoice {
  readonly number: string;
  readonly status: 'finalized' | 'draft';
  readonly periodStart: string;
  readonly periodEnd: string;
  readonly finalizedAt?: string;
  readonly currency: string;
  readonly subtotal: number;
  readonly total: number;
  readonly tax: {
    readonly treatment: 'standard' | 'reverseCharge' | 'outOfScope' | 'unknown';
    readonly ratePercent: number;
    readonly amount: number;
    readonly country: string;
    readonly note: string;
  };
  readonly issuer: { readonly legalName: string; readonly country: string; readonly vatId: string };
  readonly customer: { readonly legalName: string; readonly country: string; readonly vatId: string };
  readonly lines: readonly InvoiceLine[];
  readonly notes: readonly string[];
}

/** `InvoiceAddress.Suffix` — the tenant's invoices, under the cost query's reserved namespace. */
export const INVOICES_SUFFIX = '/providers/CyberCloud.CostManagement/invoices';

/** `CostQueryAddress.Suffix`. */
export const COST_QUERY_SUFFIX = '/providers/CyberCloud.CostManagement/query';

/**
 * The cost query and the tenant's invoices — docs/plan/22 § Cost visibility, issue #41.
 *
 * ⚠ **Not in the generated client, on purpose**, for `HubTicketsApi`'s reason: the generator emits
 * the provider registry, and neither address is a resource type — both live under the reserved
 * `CyberCloud.CostManagement` namespace the registry refuses. Budgets *are* a resource type and go
 * through `PlatformApi`. This class goes through the same `HttpApiTransport`, so the token, the
 * api-version and the error mapping stay in one place.
 *
 * ⚠ **The platform filters, the page does not.** A reader of one group asking the subscription gets
 * that group's rows and `filtered: true`; a caller who may read nothing gets a 404. Nothing here
 * narrows or second-guesses an answer.
 */
@Injectable({ providedIn: 'root' })
export class CostManagementApi {
  private readonly transport = inject(HttpApiTransport);

  /**
   * Prices a period of usage. `to` is exclusive, and the platform widens both ends to whole hours
   * and refuses a period over 366 days.
   */
  async query(
    scope: CostScope,
    from: Date,
    to: Date,
    groupBy: CostGrouping,
    granularity: CostGranularity = 'none'
  ): Promise<CostAnswer> {
    const body: Record<string, string> = { from: from.toISOString(), to: to.toISOString(), groupBy };
    if (granularity !== 'none') body['granularity'] = granularity;

    return (await this.transport.send<CostAnswer>({ method: 'POST', path: scopePath(scope) + COST_QUERY_SUFFIX, body }))
      .value;
  }

  /** The tenant's finalized invoices, newest first. A caller who may not read the tenant gets a 404. */
  async invoices(tenantId: string): Promise<readonly Invoice[]> {
    return (
      await this.transport.send<{ value: readonly Invoice[] }>({
        method: 'GET',
        path: scopePath({ kind: 'tenant', tenantId }) + INVOICES_SUFFIX
      })
    ).value.value;
  }

  /** One finalized invoice, by the number printed on it. */
  async invoice(tenantId: string, number: string): Promise<Invoice> {
    return (
      await this.transport.send<Invoice>({
        method: 'GET',
        path: `${scopePath({ kind: 'tenant', tenantId })}${INVOICES_SUFFIX}/${encodeURIComponent(number)}`
      })
    ).value;
  }
}
