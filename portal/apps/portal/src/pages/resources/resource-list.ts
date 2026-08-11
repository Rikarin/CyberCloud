import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TenantContextStore } from '@cybercloud/shell';

/**
 * The resource list placeholder.
 *
 * The real one is `@xui/data-table` over the resource-graph projection — docs/plan/20
 * § Information architecture: "virtual scroll, server-side filter/sort, column chooser, saved
 * views, CSV export" — and needs `libs/api`, which the generator owns and has not emitted yet.
 *
 * It is here at M1 for one reason: it gives the route table a second lazy route, so
 * `scripts/bundle-budget.mjs` has a real route chunk to measure and the "route-level code splitting
 * is mandatory" assertion in docs/plan/20 § Performance budget is actually being tested rather than
 * asserted about a build that has only one route.
 */
@Component({
  selector: 'cc-resource-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block p-6' },
  template: `
    <h1 class="text-lg font-semibold" i18n="@@resources.heading">Resources</h1>

    @if (context.activeSubscription(); as subscription) {
      <p class="text-foreground-muted mt-2 text-sm">{{ subscription.displayName }}</p>
    } @else {
      <p class="text-foreground-muted mt-2 text-sm" i18n="@@resources.noSubscription">
        Select a subscription to list resources.
      </p>
    }
  `,
})
export class ResourceList {
  protected readonly context = inject(TenantContextStore);
}
