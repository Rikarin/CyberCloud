import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { XuiDockManagerImports, XuiDockManagerLayout } from '@xui/dock-manager';
import { XuiPanel, XuiPanelStack } from '@xui/panel-stack';
import { BladeStackStore } from '../blades/blade-stack.store';
import { ShellBreadcrumbs } from '../breadcrumbs/shell-breadcrumbs';
import { ContextBar } from '../context-bar/context-bar';
import { NotificationsTray } from '../notifications/notifications-tray';
import { ShellOmnibar } from '../omnibar/shell-omnibar';

/**
 * The portal shell.
 *
 * docs/plan/20 § Information architecture is copied from Azure's portal "because it is a good
 * design that a million people already know, and because deviating costs onboarding for no
 * benefit". This component is the frame that holds the six elements that section names, and the
 * layout decisions here are mostly about *where each one lives in the tree* rather than how it
 * looks.
 *
 * ⚠ **Everything chrome-like is outside the router outlet.** The context bar, breadcrumbs, omnibar
 * and notifications tray are siblings of `<router-outlet>`, not children of any route. Two
 * consequences, both required:
 *
 * - The context bar is "always visible" (docs/plan/20 § Information architecture) including during
 *   a navigation and while a lazy route chunk is still in flight. A context bar that blinks out
 *   mid-navigation is a context bar nobody trusts.
 * - The omnibar's `mod+k` works before any route has loaded. docs/plan/20 calls it "**The primary
 *   navigation**"; primary navigation that requires a loaded page is not primary.
 *
 * ⚠ **`@xui/dock-manager` and `@xui/panel-stack` do different jobs and are not alternatives.**
 * docs/plan/20 names both for blades. The dock manager owns the *workspace* — the resource list
 * next to the detail area, the cloud terminal docked at the bottom, panes the user can float and
 * unpin, and a layout that serialises so it survives a reload. The panel stack owns the *drill-down
 * inside one pane* — Overview → Access → a role assignment — which is a back-navigating stack, not
 * a dockable surface. Using the dock manager for a drill-down would let a user float step three of
 * a wizard away from step two.
 *
 * ⚠ **No `ChangeDetectorRef`, here or anywhere.** docs/plan/20 § Live updates: "a
 * `ChangeDetectorRef` in portal code is a code-review failure". The layout is `OnPush` over signals
 * and the app is zoneless; the lint config makes this a build failure rather than a review one.
 */
@Component({
  selector: 'cc-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    ContextBar,
    ShellBreadcrumbs,
    ShellOmnibar,
    NotificationsTray,
    XuiPanelStack,
    XuiDockManagerImports,
  ],
  host: { class: 'flex h-svh flex-col bg-background text-foreground' },
  template: `
    <!--
      Visible only once focused. A keyboard user reaching the resource list should not have to walk
      the context bar and the blade headers first — docs/plan/20 § Accessibility, i18n, theming:
      "Cloud portals are used all day by people who navigate by keyboard".
    -->
    <a class="skip-link" href="#cc-main" i18n="@@shell.skipLink">Skip to main content</a>

    <header class="border-border flex shrink-0 items-center justify-between border-b">
      <cc-context-bar class="grow" />
      <cc-notifications-tray class="px-2" />
    </header>

    <cc-breadcrumbs />

    <main id="cc-main" class="min-h-0 grow" tabindex="-1">
      <xui-dock-manager class="h-full" [(layout)]="layout">
        <!--
          The workspace pane. Its body is the routed view, so route-level code splitting
          (docs/plan/20 § Performance budget) still governs what actually loads — the dock manager
          holds a template, not a bundle.
        -->
        <ng-template xuiDockContent="workspace">
          <router-outlet />
        </ng-template>

        <!--
          The blade drill-down. xui-panel-stack renders only the top panel and animates the push
          and pop, which is the Azure blade behaviour docs/plan/20 § Information architecture asks
          for. Its initialPanel is the root and cannot be popped, so there is always something here.
        -->
        <ng-template xuiDockContent="blades">
          <xui-panel-stack [initialPanel]="rootPanel()" />
        </ng-template>
      </xui-dock-manager>
    </main>

    <cc-omnibar />
  `,
})
export class ShellLayout {
  private readonly blades = inject(BladeStackStore);

  /**
   * The dock layout is a `ModelSignal`, so the user's drags write straight back here. It is plain
   * serialisable data by `@xui/dock-manager`'s design — "Nothing in the tree holds a DOM reference
   * or an Angular type, which is what makes a layout safe to persist and restore" — which is what
   * will let a saved workspace layout be a user preference later.
   *
   * ⚠ It is per-instance state, and `ShellLayout` is instantiated per application injector, so it
   * is per-request under SSR. See `TenantContextStore` for why that distinction is the whole
   * ballgame.
   */
  readonly layout = signal<XuiDockManagerLayout>({
    rootPane: {
      type: 'splitPane',
      orientation: 'horizontal',
      panes: [
        {
          type: 'contentPane',
          contentId: 'workspace',
          header: $localize`:@@shell.pane.workspace:Workspace`,
          size: 3,
          allowClose: false,
          allowFloating: false,
        },
        {
          type: 'contentPane',
          contentId: 'blades',
          header: $localize`:@@shell.pane.blades:Details`,
          size: 2,
          allowClose: false,
        },
      ],
    },
  });

  /**
   * The bottom of the blade stack. `@xui/panel-stack` requires an `initialPanel` that cannot be
   * popped; with no blades open that is a placeholder, and once a blade is open it is the first
   * one. The panels above it are pushed by the blade routes themselves through
   * `XUI_PANEL_STACK`.
   */
  protected readonly rootPanel = computed<XuiPanel<unknown>>(() => {
    const first = this.blades.blades()[0];

    return {
      title: first?.title ?? $localize`:@@shell.blades.empty:No resource selected`,
      content: EmptyBlade,
    };
  });
}

/**
 * Placeholder content for the root panel. Deliberately trivial: the real blade bodies are lazily
 * routed, and pulling one into the shell's own chunk would put a resource type's form in the
 * initial bundle — which docs/plan/20 § Performance budget forbids by name.
 */
@Component({
  selector: 'cc-empty-blade',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="text-foreground-muted p-6 text-sm" i18n="@@shell.blades.emptyHint">
      Select a resource, or press Ctrl+K to search.
    </p>
  `,
})
export class EmptyBlade {}
