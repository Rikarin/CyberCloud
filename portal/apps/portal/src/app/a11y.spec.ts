import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, RouterOutlet, withComponentInputBinding } from '@angular/router';
import axe from 'axe-core';
import { appRoutes } from './app.routes';
import { ContextBar, NotificationsTray, TenantContextStore } from '@cybercloud/shell';

/**
 * ⚠ **The accessibility gate.**
 *
 * docs/plan/20 § Accessibility, i18n, theming: "**WCAG 2.2 AA is a gate, not a goal.** …axe runs in
 * CI on every route. Cloud portals are used all day by people who navigate by keyboard, and a modal
 * that traps focus wrongly is a bug that stops work."
 *
 * "On every route" is taken literally: the suite iterates `appRoutes` rather than naming pages, so
 * a route added without an accessibility pass fails here rather than shipping. There is no
 * allow-list of routes to skip.
 *
 * The rule set is `wcag2a`, `wcag2aa`, `wcag21aa` and `wcag22aa` — 2.2 AA and everything it
 * subsumes, matching the doc's wording exactly rather than axe's looser default.
 */
const WCAG_22_AA = {
  runOnly: { type: 'tag' as const, values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
  rules: {
    // ⚠ The one rule that cannot be checked here, and it is not being waived.
    //
    // `color-contrast` measures *rendered* pixels: axe rasterises text through a canvas to work out
    // the effective foreground and background. jsdom has neither a layout engine nor a canvas, so
    // the rule cannot return a verdict — it returns "incomplete" and logs a not-implemented error
    // for every node. Leaving it on would mean a rule that never passes and never fails, which is
    // worse than an explicit gap because it looks like coverage.
    //
    // Contrast is a property of the token layer rather than of any one component
    // (docs/plan/20 § Accessibility, i18n, theming — "Theming is tokens"), so the place to check it
    // is once per theme against real rendered colour, in the browser-driven suite that lands with
    // the e2e layer. Until then it is a known, named gap and not a silent one.
    'color-contrast': { enabled: false },
  },
};

@Component({
  selector: 'cc-a11y-host',
  imports: [RouterOutlet],
  template: '<main><router-outlet /></main>',
})
class A11yHost {}

/** Every path in the route table that can be reached without a wildcard match. */
const routePaths = appRoutes
  .map((r) => r.path ?? '')
  .filter((p) => p !== '**')
  .map((p) => `/${p}`.replace('/:provider/:type/:name', '/net/vpc/example').replace(/^\/\//, '/'));

describe('Accessibility — docs/plan/20 § Accessibility, i18n, theming', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        // `withComponentInputBinding()` mirrors `appConfig`. Without it a route whose component
        // takes required inputs from the URL throws NG0950 here but works in the app, which would
        // make this suite fail for a reason that has nothing to do with accessibility.
        provideRouter(appRoutes, withComponentInputBinding()),
      ],
    });
  });

  it.each([...routePaths, '/definitely-not-a-route'])('%s has no WCAG 2.2 AA violations', async (path) => {
    const harness = TestBed.createComponent(A11yHost);
    await TestBed.inject(Router).navigateByUrl(path);
    await harness.whenStable();

    const results = await axe.run(harness.nativeElement as HTMLElement, WCAG_22_AA);

    // Named in the failure message, because "3 violations" sends someone to the axe docs while
    // "aria-required-children on xui-select" sends them to the component.
    const summary = results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) — ${v.help}`);

    expect(summary).toEqual([]);
  });

  it('the context bar is reachable and labelled', async () => {
    // The context bar is the one piece of chrome docs/plan/20 § Information architecture requires
    // to be "always visible", so it gets its own pass rather than only being covered by whichever
    // route happens to render it.
    TestBed.inject(TenantContextStore).load(
      [{ id: 't-1', displayName: 'Acme Corporation' }],
      [{ id: 's-1', tenantId: 't-1', displayName: 'Acme Production' }],
    );
    TestBed.inject(TenantContextStore).selectTenant('t-1');

    const harness = TestBed.createComponent(ContextBar);
    await harness.whenStable();

    const results = await axe.run(harness.nativeElement as HTMLElement, WCAG_22_AA);
    expect(results.violations.map((v) => v.id)).toEqual([]);

    // An `aria-live` region, so a switch is announced rather than only shown.
    expect((harness.nativeElement as HTMLElement).querySelector('[aria-live="polite"]')).not.toBeNull();
  });

  it('the notifications tray announces its unread count', async () => {
    const harness = TestBed.createComponent(NotificationsTray);
    await harness.whenStable();

    const results = await axe.run(harness.nativeElement as HTMLElement, WCAG_22_AA);
    expect(results.violations.map((v) => v.id)).toEqual([]);

    const trigger = (harness.nativeElement as HTMLElement).querySelector('button');
    expect(trigger?.getAttribute('aria-label')).toBeTruthy();
  });
});
