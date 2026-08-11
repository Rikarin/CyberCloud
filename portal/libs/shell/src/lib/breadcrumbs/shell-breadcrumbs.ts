import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { XuiBreadcrumbData, XuiBreadcrumbs } from '@xui/breadcrumb';
import { BladeStackStore } from '../blades/blade-stack.store';
import { TenantContextStore } from '../context/tenant-context';

/**
 * The breadcrumb trail.
 *
 * docs/plan/20 § Information architecture: "Breadcrumbs — tenant → subscription → resource group →
 * resource, each clickable."
 *
 * The trail is *derived*, never stored. Its first two crumbs come from `TenantContextStore` and the
 * rest from `BladeStackStore`, so it cannot disagree with the context bar or with the blade stack —
 * a breadcrumb that says one subscription while the context bar says another is worse than no
 * breadcrumb, because it is trusted.
 *
 * ⚠ "each clickable" is doing real work in a deep hierarchy: clicking a crumb pops the blade stack
 * back to it rather than navigating forward to a fresh copy, which is what keeps the back-and-forth
 * between a resource and its resource group from growing the stack without bound.
 *
 * ⚠ **Known upstream defect — `@xui/overflow-list` is not SSR-safe at 2.2.0.** `xui-breadcrumbs`
 * composes `xui-overflow-list`, whose width measurement does `[...ruler.nativeElement.children]`.
 * A spread needs an iterator, and the DOM implementation Angular renders with on the server returns
 * a plain array-like for `children`, so every server render logs
 * `TypeError: this.ruler.nativeElement.children is not iterable`.
 *
 * It is **not fatal** — the measurement bails, the trail renders uncollapsed, and the document is
 * correct — but it is an error per request in production logs. The fix is one call in xUI:
 * `Array.from(...)` instead of `[...]`, which accepts an array-like and needs no iterator. Per
 * docs/plan/02 § ADR-017 ("Where the portal needs a component xUI does not have, it is built in
 * xUI and released there") this is xUI's to fix, and no workaround is applied here: the available
 * ones all mean patching DOM semantics process-wide on the server to paper over one library's
 * spread, which is a worse trade than a logged error.
 */
@Component({
  selector: 'cc-breadcrumbs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiBreadcrumbs],
  host: { class: 'flex items-center px-4 h-9 border-b border-border bg-surface-sunken shrink-0' },
  template: `
    <xui-breadcrumbs
      [items]="crumbs()"
      [aria-label]="ariaLabel"
      [minVisibleItems]="2"
      (itemClick)="navigate($event)"
    />
  `,
})
export class ShellBreadcrumbs {
  private readonly context = inject(TenantContextStore);
  private readonly blades = inject(BladeStackStore);

  protected readonly ariaLabel = $localize`:@@shell.breadcrumbs.label:Breadcrumb`;

  protected readonly crumbs = computed<readonly CrumbData[]>(() => {
    const trail: CrumbData[] = [];
    const tenant = this.context.activeTenant();
    const subscription = this.context.activeSubscription();

    if (tenant !== null) trail.push({ text: tenant.displayName, icon: 'matBusiness' });
    if (subscription !== null) trail.push({ text: subscription.displayName, icon: 'matCreditCard' });

    for (const blade of this.blades.blades()) trail.push({ text: blade.title, bladeId: blade.id });

    return trail;
  });

  protected navigate(crumb: CrumbData): void {
    if (crumb.bladeId !== undefined) this.blades.popTo(crumb.bladeId);
  }
}

/**
 * `XuiBreadcrumbData` plus the blade the crumb points at. `xui-breadcrumbs` is generic over its
 * item type and hands the whole item back on `itemClick`, so the extra field survives the round
 * trip without a lookup table.
 */
interface CrumbData extends XuiBreadcrumbData {
  bladeId?: string;
}
