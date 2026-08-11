import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/**
 * The application root: a centred card on an empty page, and nothing else.
 *
 * ⚠ **No navigation, no context bar, no shell.** The portal's `ShellLayout` exists to move a
 * signed-in user around a tenant; this origin has no tenant and no signed-in user yet. Importing it
 * would pull the portal's API client and its token store onto the cookie origin, which is the
 * coupling docs/plan/11 § Hosts separates the two hosts to prevent.
 */
@Component({
  selector: 'cc-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet],
  template: `
    <a class="skip-link" href="#cc-main" i18n>Skip to the form</a>
    <div class="flex min-h-svh flex-col items-center justify-center px-4 py-10">
      <main id="cc-main" class="w-full max-w-sm" tabindex="-1">
        <router-outlet />
      </main>
    </div>
  `,
})
export class App {}
