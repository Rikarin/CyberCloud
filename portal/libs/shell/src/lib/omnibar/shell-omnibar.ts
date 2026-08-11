import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { XuiOmnibar } from '@xui/omnibar';
import { OmnibarRegistry, OmnibarResult } from './omnibar-registry';

/**
 * The omnibar — `Ctrl/⌘ K`.
 *
 * ⚠ docs/plan/20 § Information architecture calls this "**The primary navigation**", not an
 * accessory to it, and gives the reason: "Deep hierarchies are unnavigable by clicking and everyone
 * who uses a cloud daily uses the search box."
 *
 * That is why this component is mounted by `ShellLayout` at the top of the tree rather than by any
 * route. The hotkey has to work on a blank dashboard, on a 404, and while a lazy route is still
 * loading — all of which are moments when clicking has nothing to offer.
 *
 * `@xui/omnibar` owns the hard parts: the modal overlay layer, the focus trap, `aria-activedescendant`
 * so the query field keeps focus while the selection moves, virtualisation past a threshold, and
 * the `mod+k` binding itself. What is left for the portal is what the rows mean.
 */
@Component({
  selector: 'cc-omnibar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiOmnibar],
  template: `
    <xui-omnibar
      hotkey="mod+k"
      [itemsProvider]="provider"
      [itemText]="itemText"
      [itemGroup]="itemGroup"
      [recentItems]="registry.recent()"
      [ariaLabel]="ariaLabel"
      [placeholder]="placeholder"
      [noResultsText]="noResultsText"
      [recentLabel]="recentLabel"
      (itemSelected)="choose($event)"
    />
  `,
})
export class ShellOmnibar {
  protected readonly registry = inject(OmnibarRegistry);
  private readonly router = inject(Router);

  protected readonly ariaLabel = $localize`:@@shell.omnibar.label:Search resources, actions and documentation`;
  protected readonly placeholder = $localize`:@@shell.omnibar.placeholder:Search resources, actions, docs…`;
  protected readonly noResultsText = $localize`:@@shell.omnibar.empty:Nothing matched`;
  protected readonly recentLabel = $localize`:@@shell.omnibar.recent:Recent`;

  /**
   * `@xui/omnibar` debounces the query and keeps only the newest answer, so this does not have to.
   * The `AbortSignal` is still threaded through to the sources: a superseded resource-graph query
   * is a request the gateway should not finish paying for.
   */
  protected readonly provider = (query: string): Promise<readonly OmnibarResult[]> =>
    this.registry.search(query, new AbortController().signal);

  protected readonly itemText = (item: OmnibarResult): string => item.label;

  /**
   * Grouping is by kind rather than by relevance. `@xui/omnibar` buckets in first-seen order and
   * the arrow keys walk that same order, so "the next item" means the same thing to the keyboard
   * as it does to the eye — which is what makes the palette usable without looking at it.
   */
  protected readonly itemGroup = (item: OmnibarResult): string => {
    switch (item.kind) {
      case 'resource':
        return $localize`:@@shell.omnibar.group.resources:Resources`;
      case 'action':
        return $localize`:@@shell.omnibar.group.actions:Actions`;
      case 'tenant':
        return $localize`:@@shell.omnibar.group.tenants:Tenants`;
      case 'subscription':
        return $localize`:@@shell.omnibar.group.subscriptions:Subscriptions`;
      case 'doc':
        return $localize`:@@shell.omnibar.group.docs:Documentation`;
    }
  };

  protected choose(item: OmnibarResult): void {
    this.registry.remember(item);

    if (item.run !== undefined) {
      item.run();
      return;
    }

    if (item.route !== undefined) void this.router.navigateByUrl(item.route);
  }
}
