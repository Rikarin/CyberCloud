import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'cc-not-found',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block p-6' },
  template: `
    <h1 class="text-lg font-semibold" i18n="@@notFound.heading">Not found</h1>
    <p class="text-foreground-muted mt-2 text-sm" i18n="@@notFound.body">
      That page does not exist. Press Ctrl+K to search for a resource.
    </p>
  `,
})
export class NotFound {}
