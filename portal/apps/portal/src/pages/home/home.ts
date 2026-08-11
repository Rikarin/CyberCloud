import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The dashboard placeholder.
 *
 * The real one is bespoke — docs/plan/20 § The pages that are not generated, "Dashboard / home:
 * Cost, health, recent, quick-create. Nothing generic about it", 0.4 EM — and is not part of the
 * M1 shell. What this holds is the shape: a lazily-loaded route, so the dashboard's eventual chart
 * dependencies land in their own chunk rather than the initial bundle.
 */
@Component({
  selector: 'cc-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block p-6' },
  template: `
    <h1 class="text-lg font-semibold" i18n="@@home.heading">Home</h1>
    <p class="text-foreground-muted mt-2 text-sm" i18n="@@home.body">
      The dashboard is a bespoke page and is not part of the M1 shell. Press Ctrl+K to search.
    </p>
  `,
})
export class Home {}
