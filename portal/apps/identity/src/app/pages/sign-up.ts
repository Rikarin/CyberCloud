import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { IdentityApi } from '../identity-api';
import { passkeyUnavailableReason } from '../passkey';
import { sanitizeReturnUrl } from '../return-url';

/**
 * The self-serve sign-up page.
 *
 * docs/plan/11 § Sign-up and tenant creation gives the self-serve path as "Email + passkey → verify
 * → create tenant → …". Two things follow for this page specifically:
 *
 * 1. **A passkey is the primary action and a password is the secondary one**, on the same screen.
 *    docs/plan/11 § Credentials: passkeys are "the **default** offered credential at sign-up, not an
 *    upsell. A platform starting in 2026 that leads with passwords is choosing the worse security
 *    posture on purpose." So the passkey button is `color="primary"` and the password path is a
 *    `variant="link"` underneath it — not a tab, not a disclosure the user has to find.
 * 2. **The answer never says whether the address was free.** `UniformFailures.SignUp` — "Check that
 *    address for a message telling you what to do next" — is returned either way, and the mail that
 *    follows is what differs. A page that rendered "that address is already registered" would be a
 *    tenant-membership oracle on an unauthenticated endpoint.
 *
 * ⚠ **No dialog and no drawer**, for the same SSR reason as the sign-in page: `XuiDialog`,
 * `XuiDrawer` and `XuiAlertDialog` attach a CDK overlay from a constructor `effect()` that runs
 * server-side and throws. The confirmation is an inline region instead.
 */
@Component({
  selector: 'cc-sign-up',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, XuiButton, XuiInput],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.signUp.heading">Create an account</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.signUp.subheading">
        Start with your email address. We will send you what to do next.
      </p>

      @if (sent()) {
        <!--
          ⚠ The confirmation is identical whether or not the address already had an account, and it
          is phrased so that it stays true in both cases.
        -->
        <p
          class="bg-surface text-foreground mt-5 rounded-md px-3 py-3 text-sm"
          role="status"
          aria-live="polite"
        >
          {{ sent() }}
        </p>

        <p class="text-foreground-muted mt-6 text-sm">
          <a
            class="text-primary underline"
            [href]="signInHref()"
            i18n="@@identity.signUp.backToSignIn"
            >Back to sign in</a
          >
        </p>
      } @else {
        @if (error(); as message) {
          <p
            class="bg-error-muted text-error-foreground mt-4 rounded-md px-3 py-2 text-sm"
            role="alert"
            aria-live="assertive"
          >
            {{ message }}
          </p>
        }

        <form class="mt-5 flex flex-col gap-4" (ngSubmit)="onSubmit()">
          <div class="flex flex-col gap-1.5">
            <label
              class="text-sm font-medium"
              for="cc-signup-email"
              i18n="@@identity.signUp.emailLabel"
            >
              Email address
            </label>
            <input
              xuiInput
              id="cc-signup-email"
              name="email"
              type="email"
              autocomplete="username"
              inputmode="email"
              required
              [(ngModel)]="email"
            />
          </div>

          <!--
            The primary action creates a passkey. docs/plan/11 § Credentials makes this the default
            rather than an upsell, so it is the button with the weight.
          -->
          @if (passkeyBlockedBy(); as reason) {
            <p class="text-foreground-muted text-sm">
              @switch (reason) {
                @case ('insecure-context') {
                  <span i18n="@@identity.signUp.passkeyInsecure">
                    Passkeys need a secure connection. Open this page over HTTPS to create one.
                  </span>
                }
                @default {
                  <span i18n="@@identity.signUp.passkeyUnsupported">
                    This browser cannot create passkeys. You can still continue with email.
                  </span>
                }
              }
            </p>
          } @else {
            <button
              xuiButton
              type="submit"
              color="primary"
              [loading]="busy()"
              [disabled]="busy()"
              i18n="@@identity.signUp.withPasskey"
            >
              Continue with a passkey
            </button>
          }

          <button
            xuiButton
            type="button"
            variant="link"
            size="sm"
            [disabled]="busy()"
            (click)="onSubmit()"
            i18n="@@identity.signUp.withPassword"
          >
            Set up a password instead
          </button>
        </form>
      }

      <p class="text-foreground-muted mt-6 text-sm">
        <span i18n="@@identity.signUp.haveAccount">Already have an account?</span>
        <a
          class="text-primary ms-1 underline"
          [href]="signInHref()"
          i18n="@@identity.signUp.signIn"
          >Sign in</a
        >
      </p>
    </div>
  `,
})
export class SignUpPage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);

  /** Sanitized on read; the raw query value is never stored. See `sign-in.ts` for why. */
  readonly returnUrl = computed(() =>
    sanitizeReturnUrl(this.#route.snapshot.queryParamMap.get('returnUrl')),
  );

  /** The address, bound to the field. */
  readonly email = signal('');

  /** Whether a request is in flight. */
  readonly busy = signal(false);

  /** The uniform confirmation, once the request has been accepted. */
  readonly sent = signal<string | null>(null);

  /** A transport failure, phrased so it cannot be read as an answer about the address. */
  readonly error = signal<string | null>(null);

  /** Why a passkey cannot be created here, or `null` when it can. SSR-safe. */
  readonly passkeyBlockedBy = computed(() => passkeyUnavailableReason());

  /** The sign-in link, carrying the same sanitized return URL. */
  readonly signInHref = computed(
    () => `/signin?returnUrl=${encodeURIComponent(this.returnUrl())}`,
  );

  /** Submits the address. */
  onSubmit(): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.#api.signUp(this.email(), this.returnUrl()).subscribe({
      next: (result) => {
        this.busy.set(false);
        // ⚠ Rendered verbatim. `result.message` is `UniformFailures.SignUp` whether the address was
        // free or taken; deriving our own copy here would be a second source of truth for a string
        // whose sameness is the security property.
        this.sent.set(result.message);
      },
      error: () => {
        this.busy.set(false);
        this.error.set(
          $localize`:@@identity.signUp.failed:We could not start that just now. Try again in a moment.`,
        );
      },
    });
  }
}
