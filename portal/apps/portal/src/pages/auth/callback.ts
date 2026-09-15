import { isPlatformBrowser } from '@angular/common';
import { ChangeDetectionStrategy, Component, PLATFORM_ID, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthFlow } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiSpinner } from '@xui/spinner';

/** What the page is doing. `working` is also what the server renders. */
type CallbackState =
  { readonly kind: 'working' } | { readonly kind: 'failed'; readonly message: string; readonly returnTo: string };

/**
 * `/auth/callback` — where the identity host sends the browser back with an authorization code.
 *
 * The page does one thing, through `AuthFlow.completeCallback`: check the `state`, trade the code
 * for tokens, load the tenant, and go where the person was heading before the sign-in. It is the
 * one route without `authGuard`, because it is where the token the guard wants comes from.
 *
 * ⚠ **On the server it renders the "signing in" state and touches nothing.** docs/plan/20 § SSR:
 * "The SSR process holds no tokens". The code in the query string is a credential (RFC 6749
 * § 4.1.2) and the exchange needs the PKCE cookie, which the server never sees — so on the server
 * this component reads no query, sets no cookie, and makes no request. The browser's instance does
 * the work after hydration. `scripts/ssr-isolation.test.mjs` renders this route with a code and a
 * state and asserts that neither reaches the output.
 *
 * Failures render inline with a retry, rather than throwing: a person who lands here with a
 * refused code cannot open the console, and the retry is a fresh `/authorize` — the stale PKCE
 * cookie was consumed on the way in.
 */
@Component({
  selector: 'cc-auth-callback',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButton, XuiCallout, XuiSpinner],
  host: { class: 'block p-6' },
  template: `
    @switch (state().kind) {
      @case ('working') {
        <div class="text-foreground-muted flex items-center gap-2 py-6 text-sm" role="status" aria-live="polite">
          <xui-spinner size="sm" />
          <span i18n="@@auth.callback.working">Signing you in…</span>
        </div>
      }
      @case ('failed') {
        <xui-callout color="error" [title]="failedTitle">
          <p>{{ failedMessage() }}</p>
          <button
            xuiButton
            variant="outline"
            size="sm"
            class="mt-2"
            type="button"
            (click)="retry()"
            i18n="@@auth.callback.retry"
          >
            Try again
          </button>
        </xui-callout>
      }
    }
  `
})
export class AuthCallback {
  private readonly auth = inject(AuthFlow);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly state = signal<CallbackState>({ kind: 'working' });
  protected readonly failedTitle = $localize`:@@auth.callback.failedTitle:Sign-in could not be completed`;

  constructor() {
    if (this.browser) void this.complete();
  }

  protected failedMessage(): string {
    const state = this.state();
    return state.kind === 'failed' ? state.message : '';
  }

  protected retry(): void {
    const state = this.state();
    void this.auth.beginSignIn(state.kind === 'failed' ? state.returnTo : '/');
  }

  private async complete(): Promise<void> {
    const query = new URLSearchParams();
    const params = this.route.snapshot.queryParamMap;
    for (const key of params.keys) {
      const value = params.get(key);
      if (value !== null) query.set(key, value);
    }

    const outcome = await this.auth.completeCallback(query);

    if (outcome.kind === 'failed') {
      this.state.set(outcome);
      return;
    }

    await this.router.navigateByUrl(outcome.returnTo, { replaceUrl: true });
  }
}
