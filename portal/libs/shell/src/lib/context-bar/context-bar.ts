import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { XuiSelect } from '@xui/select';
import { SubscriptionRef, TenantContextStore, TenantRef } from '../context/tenant-context';

/**
 * The tenant + subscription switcher.
 *
 * docs/plan/20 § Information architecture: "Context bar — Tenant + subscription switcher, always
 * visible. Getting this wrong means people act in the wrong subscription, which is a real and
 * expensive class of mistake."
 *
 * ⚠ "Always visible" is a layout requirement, and it is met by the shell rendering this outside the
 * router outlet — see `ShellLayout`. A context bar inside a route disappears during a navigation,
 * and the moment it disappears is exactly the moment someone is unsure which subscription they are
 * in.
 *
 * Three deliberate choices beyond that:
 *
 * - Both selects are `filterable`. A tenant with two hundred subscriptions is normal, and a
 *   scroll-to-find picker is how the wrong one gets chosen.
 * - The active pair is announced through `aria-live="polite"` as well as shown. A switch that is
 *   only visible is a switch a screen-reader user makes blind, and docs/plan/20 § Accessibility,
 *   i18n, theming makes WCAG 2.2 AA a gate.
 * - Nothing here is optimistic. The selects reflect `TenantContextStore`, which is set from what
 *   the API returned; there is no local "assume it worked" path.
 */
@Component({
  selector: 'cc-context-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiSelect],
  host: {
    class: 'flex items-center gap-3 px-4 h-12 border-b border-border bg-surface shrink-0',
    role: 'region',
    '[attr.aria-label]': 'regionLabel',
  },
  template: `
    <span class="text-foreground-muted text-xs font-medium" i18n="@@shell.contextBar.tenant">
      Tenant
    </span>

    <xui-select
      class="min-w-56"
      [items]="store.tenants()"
      [itemText]="tenantText"
      [value]="store.activeTenant()"
      [filterable]="true"
      [disabled]="!store.resolved()"
      [aria-label]="tenantLabel"
      (valueChange)="onTenant($event)"
    />

    <span class="bg-border h-6 w-px" aria-hidden="true"></span>

    <span class="text-foreground-muted text-xs font-medium" i18n="@@shell.contextBar.subscription">
      Subscription
    </span>

    <xui-select
      class="min-w-64"
      [items]="store.subscriptions()"
      [itemText]="subscriptionText"
      [value]="store.activeSubscription()"
      [filterable]="true"
      [disabled]="store.subscriptions().length === 0"
      [aria-label]="subscriptionLabel"
      (valueChange)="onSubscription($event)"
    />

    <!--
      The switch is announced, not just rendered. Kept out of the visual flow because the selects
      already show it; this exists for the users who cannot see them.
    -->
    <span class="sr-only" aria-live="polite">{{ announcement() }}</span>
  `,
})
export class ContextBar {
  protected readonly store = inject(TenantContextStore);

  protected readonly regionLabel = $localize`:@@shell.contextBar.region:Tenant and subscription context`;
  protected readonly tenantLabel = $localize`:@@shell.contextBar.tenantLabel:Select tenant`;
  protected readonly subscriptionLabel = $localize`:@@shell.contextBar.subscriptionLabel:Select subscription`;

  protected readonly tenantText = (t: TenantRef): string => t.displayName;
  protected readonly subscriptionText = (s: SubscriptionRef): string => s.displayName;

  protected announcement(): string {
    const tenant = this.store.activeTenant();
    const subscription = this.store.activeSubscription();

    if (tenant === null) return $localize`:@@shell.contextBar.resolving:Resolving your context`;

    return subscription === null
      ? $localize`:@@shell.contextBar.tenantOnly:Acting in tenant ${tenant.displayName}:tenant:, no subscription selected`
      : $localize`:@@shell.contextBar.acting:Acting in tenant ${tenant.displayName}:tenant:, subscription ${subscription.displayName}:subscription:`;
  }

  protected onTenant(tenant: TenantRef | null): void {
    if (tenant !== null) this.store.selectTenant(tenant.id);
  }

  protected onSubscription(subscription: SubscriptionRef | null): void {
    if (subscription !== null) this.store.selectSubscription(subscription.id);
  }
}
