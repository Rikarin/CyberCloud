import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ScopeResource } from '@cybercloud/api';
import { BladeStackStore, TenantContextStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiDescriptions, XuiDescriptionsItem } from '@xui/descriptions';
import { PlatformApi } from '../../app/api/platform-api';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';

/**
 * The subscription blade: one subscription, read by id.
 *
 * A deep link — `/subscriptions/{id}` — is enough on its own, which is what makes a pasted link
 * work: the tenant comes from the token and the id from the URL. Opening it also selects the
 * subscription in the context bar when the store knows it, so the bar and the blade agree;
 * when the store does not know it (a fresh portal, an id from a link) the blade still renders
 * and the bar stays as it was, because `selectSubscription` refuses an id outside the active
 * tenant and this page will not argue with it.
 *
 * ⚠ No `provisioningState` and no `etag`, and the absence is the contract — `ScopeResource`'s
 * own doc: "a scope converges before the call returns."
 */
@Component({
  selector: 'cc-subscription-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, XuiButton, XuiDescriptions, XuiDescriptionsItem, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-lg font-semibold">{{ scope()?.name ?? subscriptionId() }}</h1>
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@subscriptionBlade.subheading">Subscription</p>
        </div>
        <div class="flex gap-2">
          <a xuiButton variant="outline" size="sm" [routerLink]="accessLink()" i18n="@@subscriptionBlade.access"
            >Access</a
          >
          <a xuiButton variant="outline" size="sm" [routerLink]="costLink()" i18n="@@subscriptionBlade.cost"
            >Cost analysis</a
          >
          <a xuiButton color="primary" size="sm" [routerLink]="groupsLink()" i18n="@@subscriptionBlade.groups"
            >Resource groups</a
          >
        </div>
      </div>

      <cc-page-status [state]="state()" [retryLink]="backLink" />

      @if (scope(); as scope) {
        <xui-descriptions class="mt-4" [column]="2" bordered [title]="overviewTitle">
          <xui-descriptions-item [label]="labels.displayName">{{ scope.name }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.type">{{ scope.type }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.location">{{ scope.location ?? '—' }}</xui-descriptions-item>
          <xui-descriptions-item [label]="labels.id" [span]="2"
            ><code class="text-xs break-all">{{ scope.id }}</code></xui-descriptions-item
          >
        </xui-descriptions>
      }
    }
  `
})
export class SubscriptionBlade {
  readonly subscriptionId = input.required<string>();

  private readonly api = inject(PlatformApi);
  private readonly context = inject(TenantContextStore);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly state = pageState<ScopeResource>();
  protected readonly scope = computed(() => {
    const state = this.state();
    return state.kind === 'ready' ? state.value : null;
  });

  protected readonly groupsLink = computed(() => links.resourceGroups(this.subscriptionId()));
  protected readonly accessLink = computed(() => links.subscriptionAccess(this.subscriptionId()));
  protected readonly costLink = computed(() => links.cost(this.subscriptionId()));
  protected readonly backLink = links.subscriptions();
  protected readonly overviewTitle = $localize`:@@subscriptionBlade.overview:Overview`;
  protected readonly labels = {
    displayName: $localize`:@@subscriptionBlade.displayName:Display name`,
    type: $localize`:@@subscriptionBlade.type:Type`,
    location: $localize`:@@subscriptionBlade.location:Home region`,
    id: $localize`:@@subscriptionBlade.id:Id`
  };

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();
      const subscriptionId = this.subscriptionId();

      untracked(() => {
        const route = links.subscription(subscriptionId);
        this.blades.open({ id: route, title: subscriptionId, route });

        if (tenantId === null) return;

        if (this.context.subscriptions().some(s => s.id === subscriptionId))
          this.context.selectSubscription(subscriptionId);

        void load(
          this.state,
          async () => (await this.api.getSubscription(tenantId, subscriptionId)).value,
          () => this.subscriptionId() !== subscriptionId
        );
      });
    });
  }
}
