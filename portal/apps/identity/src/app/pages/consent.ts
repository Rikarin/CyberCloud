import { ChangeDetectionStrategy, Component, afterNextRender, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { IdentityApi } from '../identity-api';
import { sanitizeReturnUrl } from '../return-url';

/** One query pair of the `/authorize` request, posted back as a hidden field. */
export interface RequestPair {
  name: string;
  value: string;
}

/**
 * The consent page — the third of docs/plan/11 § Effort's four pages.
 *
 * A tenant-registered client asked for a code; the person is signed in; this page asks whether the
 * client may have it, and for what. Two facts about its shape are the design:
 *
 * 1. **It renders the registered name and nothing from the URL.** The `/authorize` request that
 *    sent the person here is a link anybody could have composed. `GET /api/consent` resolves the
 *    client the way `/authorize` does and answers its registered display name; the only thing this
 *    page reads out of the query itself is the list of pairs it posts back, and it prints none of
 *    them.
 * 2. **The answer is a full-page form post to `/authorize`, not a `fetch`.** The server's reply to
 *    an answer is a redirect to the client — a code, or `access_denied` — in the response mode the
 *    client asked for. A `fetch` would swallow it. So the form's action is the return URL, its
 *    hidden fields are the request's own pairs, and its two buttons are named `consent` with the
 *    values `allow` and `deny`. The server honours the answer only on a POST from this page's
 *    origin; a `consent=allow` in a GET link is ignored — `IdentityEndpoints.MapAuthorize`.
 *
 * ⚠ **The description is fetched after the first render, in the browser only.** A server-side
 * `HttpClient` call would run without the person's cookie, and if it succeeded its response would
 * be serialized into the document's transfer state — the leak `ssr-identity.test.mjs` exists to
 * catch. `afterNextRender` runs on the client and never on the server.
 *
 * ⚠ **No `FormsModule` here, on purpose.** It would take over the `<form>`'s submit event, and this
 * form has to submit natively for the browser to follow the redirect.
 */
@Component({
  selector: 'cc-consent',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButton],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.consent.heading">Allow access?</h1>

      @if (error(); as message) {
        <p
          class="bg-error-muted text-error-foreground mt-4 rounded-md px-3 py-2 text-sm"
          role="alert"
          aria-live="assertive"
        >
          {{ message }}
        </p>
      } @else if (clientName(); as name) {
        <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.consent.subheading">
          <strong class="text-foreground">{{ name }}</strong> wants to:
        </p>

        <ul
          class="mt-4 flex flex-col gap-2 text-sm"
          aria-label="Permissions requested"
          i18n-aria-label="@@identity.consent.scopesLabel"
        >
          @for (scope of scopes(); track scope) {
            <li class="flex items-start gap-2">
              <span class="text-primary" aria-hidden="true">&#10003;</span>
              <span>{{ describe(scope) }}</span>
            </li>
          }
        </ul>

        <!--
          ⚠ A native form. The action is the sanitized /authorize path; every hidden field is one
          pair of that request, posted back unchanged; the two buttons carry the answer.
        -->
        <form class="mt-6 flex flex-col gap-3" method="post" [attr.action]="action()">
          @for (pair of pairs(); track $index) {
            <input type="hidden" [name]="pair.name" [value]="pair.value" />
          }

          <button xuiButton type="submit" name="consent" value="allow" color="primary" i18n="@@identity.consent.allow">
            Allow
          </button>
          <button xuiButton type="submit" name="consent" value="deny" variant="outline" i18n="@@identity.consent.deny">
            Deny
          </button>
        </form>
      } @else {
        <p class="text-foreground-muted mt-1 text-sm" role="status" i18n="@@identity.consent.loading">
          Checking the request…
        </p>
      }
    </div>
  `
})
export class ConsentPage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);

  /** Sanitized on read; the raw query value is never stored. See `sign-in.ts` for why. */
  readonly returnUrl = computed(() => sanitizeReturnUrl(this.#route.snapshot.queryParamMap.get('returnUrl')));

  /** The client's registered display name, once the server has answered. */
  readonly clientName = signal<string | null>(null);

  /** The scopes the request asks for, as the server cut them. */
  readonly scopes = signal<string[]>([]);

  /** What to render instead of the form, or `null`. */
  readonly error = signal<string | null>(null);

  /** The form's action — the return URL the SERVER answered, sanitized again here. */
  readonly action = signal<string>('/');

  /** The request's pairs, posted back as hidden fields. Derived from the action, not from the route. */
  readonly pairs = computed<RequestPair[]>(() => {
    const question = this.action().indexOf('?');

    if (question < 0) {
      return [];
    }

    return [...new URLSearchParams(this.action().slice(question + 1)).entries()]
      .filter(([name]) => name !== 'consent')
      .map(([name, value]) => ({ name, value }));
  });

  constructor() {
    afterNextRender(() => this.#load());
  }

  /** Asks the server what to render. Browser only — the constructor schedules it after the first render. */
  #load(): void {
    this.#api.describeConsent(this.returnUrl()).subscribe({
      next: response => {
        if (!response.ready) {
          this.error.set(response.message);
          return;
        }

        this.action.set(sanitizeReturnUrl(response.returnUrl));
        this.scopes.set(response.scopes);
        this.clientName.set(response.clientName);
      },
      error: () =>
        this.error.set(
          $localize`:@@identity.consent.failed:Something went wrong. Go back to the application and try again.`
        )
    });
  }

  /** What a scope means to a person, in a sentence. Unknown scopes are shown by name. */
  describe(scope: string): string {
    switch (scope) {
      case 'openid':
        return $localize`:@@identity.consent.scope.openid:Sign you in and know who you are`;
      case 'profile':
        return $localize`:@@identity.consent.scope.profile:See your name and email address`;
      case 'offline_access':
        return $localize`:@@identity.consent.scope.offlineAccess:Stay signed in without asking again`;
      case 'cyc.api':
        return $localize`:@@identity.consent.scope.api:Manage your Cyber Cloud resources on your behalf`;
      default:
        return scope;
    }
  }
}
