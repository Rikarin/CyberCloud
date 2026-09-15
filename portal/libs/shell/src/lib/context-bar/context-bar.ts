import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiButton } from '@xui/button';
import { XuiPopover } from '@xui/popover';
import { XuiSelect } from '@xui/select';
import { AuthFlow } from '../auth/auth-flow';
import { AuthSession } from '../auth/auth-session';
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
 *
 * The account menu at the end of the bar reads `AuthSession`: the name and address are the
 * id_token's claims — labels, not authority, see `decodeJwtPayload` — and "Sign out" is
 * `AuthFlow.signOut`, a full-page trip to the identity host's `/logout`. It renders only once a
 * token has been accepted, so the server render carries no account at all (docs/plan/20 § SSR).
 */
@Component({
  selector: 'cc-context-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiSelect, XuiButton, XuiPopover],
  host: {
    class: 'flex items-center gap-3 px-4 h-12 border-b border-border bg-surface shrink-0',
    role: 'region',
    '[attr.aria-label]': 'regionLabel'
  },
  template: `
    <span class="text-foreground-muted text-xs font-medium" i18n="@@shell.contextBar.tenant"> Tenant </span>

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

    <span class="text-foreground-muted text-xs font-medium" i18n="@@shell.contextBar.subscription"> Subscription </span>

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

    @if (session.account(); as account) {
      <button
        xuiButton
        class="ms-auto"
        type="button"
        variant="ghost"
        size="sm"
        interactionKind="click"
        [xuiPopover]="accountMenu"
        [attr.aria-label]="accountLabel()"
      >
        {{ account.name ?? account.email ?? account.subjectId }}
      </button>

      <ng-template #accountMenu>
        <div class="w-72 max-w-[90vw]" role="group" [attr.aria-label]="accountPanelLabel">
          <div class="border-border border-b px-3 py-2">
            <p class="text-sm font-medium">{{ account.name ?? account.subjectId }}</p>
            @if (account.email; as email) {
              <p class="text-foreground-muted text-xs">{{ email }}</p>
            }
          </div>
          <div class="p-2">
            <button
              xuiButton
              class="w-full"
              type="button"
              variant="outline"
              size="sm"
              (click)="onSignOut()"
              i18n="@@shell.account.signOut"
            >
              Sign out
            </button>
          </div>
        </div>
      </ng-template>
    }
  `
})
export class ContextBar {
  protected readonly store = inject(TenantContextStore);
  protected readonly session = inject(AuthSession);
  private readonly auth = inject(AuthFlow);

  protected readonly accountPanelLabel = $localize`:@@shell.account.panel:Account`;
  protected readonly accountLabel = computed(() => {
    const account = this.session.account();
    const who = account?.name ?? account?.email ?? account?.subjectId ?? '';
    return $localize`:@@shell.account.trigger:Account menu, ${who}:who:`;
  });

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

  protected onSignOut(): void {
    this.auth.signOut();
  }
}
