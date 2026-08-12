import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { IdentityApi, SignInResultResponse } from '../identity-api';
import { NAVIGATE } from '../navigate';
import { assertPasskey, passkeyUnavailableReason } from '../passkey';
import { sanitizeReturnUrl } from '../return-url';

/**
 * Which step of the form is showing.
 *
 * ⚠ `second-factor` is reached only from a server response that set `secondFactorRequired`, never
 * from anything this page decides. A page that could put itself into this step would be a page that
 * could skip the first factor.
 */
type Step = 'address' | 'credential' | 'second-factor';

/** Which second factor the user is presenting. */
type SecondFactor = 'totp' | 'recoveryCode';

/**
 * The sign-in page.
 *
 * docs/plan/11 § Effort: "Sign-up/in/reset/consent pages (Angular + xUI, SSR, on the identity
 * host)". ⚠ **On the identity host, not in the portal**, and that is a security boundary rather
 * than a filing preference — docs/plan/11 § Hosts puts the session cookie on this origin and only
 * this origin, and docs/plan/00 § Non-negotiables' "the portal has no privileged path" depends on
 * the portal never being the thing that holds a credential.
 *
 * **The shape of the form is the design, and it is not the obvious one.**
 *
 * 1. **Address first, credential second.** `IdentityEndpoints`' remarks call for it: the server
 *    answers which credentials to offer, in `CredentialKind` order, which puts a passkey first
 *    because docs/plan/11 § Credentials makes passkeys "the default offered credential at sign-up,
 *    not an upsell".
 * 2. **The offered list is identical for an address with no account**, so this page cannot
 *    enumerate on the platform's behalf even though it renders whatever it is told.
 * 3. **One failure message, rendered verbatim.** `UniformFailures.SignIn` is the answer to all six
 *    things that can go wrong. A page that translated it into something more helpful would undo the
 *    hardening the endpoint pays a dummy Argon2id hash for.
 *
 * ⚠ **No dialog, drawer or alert-dialog anywhere on this page, and that is deliberate.** `XuiDialog`,
 * `XuiDrawer` and `XuiAlertDialog` attach a CDK overlay from a plain constructor `effect()`, which
 * runs during server-side rendering and hits a non-iterable `body.children` spread — so any of them
 * would turn this SSR page into a 500. The error surface here is an inline `role="alert"` region,
 * which is better for a form anyway: a screen reader announces it without a focus trap to escape.
 */
@Component({
  selector: 'cc-sign-in',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, XuiButton, XuiInput],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.signIn.heading">Sign in</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.signIn.subheading">
        Use your Cyber Cloud account.
      </p>

      <!--
        ⚠ 'role="alert"' and 'aria-live="assertive"'. A failure that only changes colour is invisible
        to a screen reader, and WCAG 2.2 AA is a gate rather than a goal — docs/plan/20
        § Accessibility, i18n, theming.
      -->
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
          <label class="text-sm font-medium" for="cc-email" i18n="@@identity.signIn.emailLabel">
            Email address
          </label>
          <input
            xuiInput
            id="cc-email"
            name="email"
            type="email"
            autocomplete="username"
            inputmode="email"
            required
            [disabled]="step() !== 'address'"
            [(ngModel)]="email"
            [attr.aria-describedby]="step() === 'credential' ? 'cc-email-hint' : null"
          />
          @if (step() === 'credential') {
            <button
              xuiButton
              type="button"
              variant="link"
              size="sm"
              class="self-start px-0"
              id="cc-email-hint"
              (click)="onChangeAddress()"
              i18n="@@identity.signIn.changeAddress"
            >
              Use a different address
            </button>
          }
        </div>

        @if (step() === 'credential') {
          <!--
            ⚠ The passkey option comes first in the markup as well as in the offered list, because a
            keyboard user reaches it first and because it is the credential docs/plan/11
            § Credentials wants people to use.
          -->
          @if (offersPasskey()) {
            <div class="flex flex-col gap-1.5">
              @if (passkeyBlockedBy(); as reason) {
                <p class="text-foreground-muted text-sm">
                  @switch (reason) {
                    @case ('insecure-context') {
                      <span i18n="@@identity.signIn.passkeyInsecure">
                        Passkeys need a secure connection. Open this page over HTTPS to use one.
                      </span>
                    }
                    @default {
                      <span i18n="@@identity.signIn.passkeyUnsupported">
                        This browser cannot use passkeys. Use your password instead.
                      </span>
                    }
                  }
                </p>
              } @else {
                <button
                  xuiButton
                  type="button"
                  color="primary"
                  (click)="onUsePasskey()"
                  i18n="@@identity.signIn.usePasskey"
                >
                  Use a passkey
                </button>
              }
            </div>
          }

          @if (offersPassword()) {
            <div class="flex flex-col gap-1.5">
              <label
                class="text-sm font-medium"
                for="cc-password"
                i18n="@@identity.signIn.passwordLabel"
              >
                Password
              </label>
              <!--
                ⚠ 'autocomplete="current-password"' so a password manager fills it rather than the
                user typing it somewhere else, and 'name="password"' so the manager can find it.
              -->
              <input
                xuiInput
                id="cc-password"
                name="password"
                type="password"
                autocomplete="current-password"
                required
                [(ngModel)]="password"
              />
            </div>
          }
        }

        @if (step() === 'second-factor') {
          <div class="flex flex-col gap-1.5">
            <label class="text-sm font-medium" for="cc-code">
              @if (factor() === 'totp') {
                <span i18n="@@identity.signIn.totpLabel">Authenticator code</span>
              } @else {
                <span i18n="@@identity.signIn.recoveryCodeLabel">Recovery code</span>
              }
            </label>
            <!--
              ⚠ 'autocomplete="one-time-code"' is what lets a phone offer the code from its
              keyboard, and 'inputmode="numeric"' gives a digit pad. Neither is cosmetic: a
              six-digit code typed on a full keyboard under time pressure is where people fail.

              ⚠ No 'inputmode' on the recovery-code branch — those are alphanumeric, and forcing a
              digit pad makes them untypable.
            -->
            <input
              xuiInput
              id="cc-code"
              name="code"
              type="text"
              autocomplete="one-time-code"
              [attr.inputmode]="factor() === 'totp' ? 'numeric' : null"
              required
              [(ngModel)]="code"
            />
            <button
              xuiButton
              type="button"
              variant="link"
              size="sm"
              class="self-start px-0"
              [disabled]="busy()"
              (click)="onSwitchFactor()"
            >
              @if (factor() === 'totp') {
                <span i18n="@@identity.signIn.useRecoveryCode">Use a recovery code instead</span>
              } @else {
                <span i18n="@@identity.signIn.useAuthenticator">Use your authenticator instead</span>
              }
            </button>
          </div>
        }

        <button
          xuiButton
          type="submit"
          [color]="step() === 'address' ? 'primary' : 'secondary'"
          [loading]="busy()"
          [disabled]="busy()"
        >
          @switch (step()) {
            @case ('address') {
              <span i18n="@@identity.signIn.continue">Continue</span>
            }
            @case ('second-factor') {
              <span i18n="@@identity.signIn.verify">Verify</span>
            }
            @default {
              <span i18n="@@identity.signIn.submit">Sign in</span>
            }
          }
        </button>
      </form>

      <p class="text-foreground-muted mt-6 text-sm">
        <span i18n="@@identity.signIn.noAccount">No account yet?</span>
        <a
          class="text-primary ms-1 underline"
          [href]="signUpHref()"
          i18n="@@identity.signIn.createOne"
          >Create one</a
        >
      </p>
    </div>
  `,
})
export class SignInPage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);
  readonly #navigate = inject(NAVIGATE);

  /**
   * Where to go once the session exists.
   *
   * ⚠ **Sanitized on read, and the raw value is never stored.** This is the open-redirect defence
   * and it is the reason the field is a `computed` over the snapshot rather than a plain property
   * assigned in a constructor — there is no moment at which `this.returnUrl` holds the attacker's
   * string, so no later refactor can accidentally use it. See `return-url.ts` for why the rule is
   * an allow-list of one shape.
   */
  readonly returnUrl = computed(() =>
    sanitizeReturnUrl(this.#route.snapshot.queryParamMap.get('returnUrl')),
  );

  /** The address, bound to the field. */
  readonly email = signal('');

  /**
   * The password, bound to the field.
   *
   * ⚠ **This signal is the only place the password ever lives, and it lives in memory.** It is
   * never written to `sessionStorage` or `localStorage` (the workspace's ESLint config bans both
   * outright), never put in a query string, and never logged. It is also never read during
   * server-side rendering, which is what keeps it out of the transfer state Angular serializes into
   * the rendered document — the easy version of this mistake and the one
   * `credentials-never-leak.spec.ts` asserts against.
   */
  readonly password = signal('');

  /** Which step of the form is showing. */
  readonly step = signal<Step>('address');

  /**
   * The second-factor code, bound to the field.
   *
   * ⚠ Cleared on every failure and on every switch between factors, for the same reason
   * `password` is: a stale value resubmitted against a different account or a different factor is a
   * failed attempt the user did not make, and failed attempts drive the lockout ladder.
   */
  readonly code = signal('');

  /** Which second factor is being presented. */
  readonly factor = signal<SecondFactor>('totp');

  /** Whether a request is in flight. */
  readonly busy = signal(false);

  /** The uniform failure message, rendered exactly as the server sent it. */
  readonly error = signal<string | null>(null);

  /** The credential kinds the server offered for this address. */
  readonly offered = signal<readonly string[]>([]);

  /**
   * ⚠ Compared with `includes` against the exact camelCase spelling the contract uses. The
   * `recoveryCode`-versus-`recoverycode` class of bug is the one docs/plan/11's `servicePrincipal`
   * trap is an instance of: a one-character casing difference matches nothing, every branch falls
   * through, and it reads as "the server never offers a passkey" rather than as a typo.
   */
  readonly offersPasskey = computed(() => this.offered().includes('passkey'));

  /** Whether the server offered a password for this address. */
  readonly offersPassword = computed(() => this.offered().includes('password'));

  /**
   * Why a passkey cannot be used here, or `null` when it can.
   *
   * ⚠ A `computed` over a function that is safe on the server. Reading `navigator.credentials` in a
   * field initialiser or a constructor `effect()` would run during SSR, where `navigator` does not
   * exist, and take the whole render down with a `ReferenceError`.
   */
  readonly passkeyBlockedBy = computed(() => passkeyUnavailableReason());

  /** The sign-up link, carrying the same sanitized return URL. */
  readonly signUpHref = computed(
    () => `/signup?returnUrl=${encodeURIComponent(this.returnUrl())}`,
  );

  /** Goes back to the address step, clearing whatever was typed into the credential fields. */
  onChangeAddress(): void {
    this.step.set('address');
    this.offered.set([]);
    this.error.set(null);
    // ⚠ Clearing the password is not tidiness. Leaving it set means a user who corrects a typo in
    // their address submits the previous account's password to the new one.
    this.password.set('');
    // ⚠ And the code, for the same reason one step further along: a stale code submitted against a
    // freshly-typed address is a failed attempt the user did not make, and failed attempts drive
    // the lockout ladder.
    this.code.set('');
    this.factor.set('totp');
  }

  /** Switches between the authenticator and a recovery code. */
  onSwitchFactor(): void {
    this.factor.set(this.factor() === 'totp' ? 'recoveryCode' : 'totp');
    this.code.set('');
    this.error.set(null);
  }

  /** Submits whichever step is showing. */
  onSubmit(): void {
    if (this.busy()) {
      return;
    }

    switch (this.step()) {
      case 'address':
        this.#begin();
        return;
      case 'second-factor':
        this.#verifySecondFactor();
        return;
      default:
        this.#signInWithPassword();
    }
  }

  /**
   * Runs the WebAuthn assertion.
   *
   * ⚠ **Three requests' worth of state and none of it is the challenge.** The challenge lives in a
   * protected HttpOnly cookie the server sets on `begin` and consumes on `complete`, so this page
   * cannot hold it, cannot forward it, and cannot substitute one. That is deliberate: an assertion
   * verified against options the client supplied is a signature over data the client chose, which
   * authenticates nobody.
   *
   * ⚠ **A cancelled prompt is not a failure.** `assertPasskey` answers `null` for both "the user
   * dismissed it" and "the authenticator refused", because WebAuthn does not distinguish them — so
   * this clears the busy state and says nothing rather than accusing the user's key of failing.
   */
  onUsePasskey(): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.#api.beginPasskey(this.email()).subscribe({
      next: (challenge) => {
        // An empty options string means the server could not build a challenge — a relying-party
        // misconfiguration, never an answer about the address. The password field is still there.
        if (challenge.optionsJson.length === 0) {
          this.busy.set(false);
          this.error.set(this.#uniformFailure());
          return;
        }

        assertPasskey(challenge.optionsJson).then(
          (assertion) => {
            if (assertion === null) {
              this.busy.set(false);
              return;
            }

            this.#api.completePasskey(assertion, this.returnUrl()).subscribe({
              next: (result) => this.#complete(result),
              error: () => this.#failed(),
            });
          },
          () => this.#failed(),
        );
      },
      error: () => this.#failed(),
    });
  }

  #begin(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.begin(this.email()).subscribe({
      next: (response) => {
        this.offered.set(response.offered);
        this.step.set('credential');
        this.busy.set(false);
      },
      error: () => this.#failed(),
    });
  }

  #signInWithPassword(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signInWithPassword(this.email(), this.password(), this.returnUrl()).subscribe({
      next: (result) => this.#complete(result),
      error: () => this.#failed(),
    });
  }

  #verifySecondFactor(): void {
    this.busy.set(true);
    this.error.set(null);

    const request =
      this.factor() === 'totp'
        ? this.#api.verifyTotp(this.code(), this.returnUrl())
        : this.#api.redeemRecoveryCode(this.code(), this.returnUrl());

    request.subscribe({
      next: (result) => this.#complete(result),
      error: () => this.#failed(),
    });
  }

  /**
   * Handles a credential response — the one place any of them is interpreted.
   *
   * ⚠ **`secondFactorRequired` is checked before the navigation and not after.** A password
   * sign-in succeeds and still owes a factor; navigating on `succeeded` alone would send the user
   * to `/authorize` holding a cookie the server marks `2fa=pending`, which bounces them straight
   * back to this page with no explanation. That is a loop rather than a hole — the server fails
   * closed — but it is indistinguishable from a broken sign-in to the person in front of it.
   */
  #complete(result: SignInResultResponse): void {
    this.busy.set(false);

    if (!result.succeeded) {
      this.error.set(result.message);
      this.password.set('');
      this.code.set('');
      return;
    }

    if (result.secondFactorRequired) {
      // ⚠ The password is cleared on the way into this step. It is not needed again, and a signal
      // still holding it while the user types a code is a credential kept alive for no reason.
      this.password.set('');
      this.code.set('');
      this.step.set('second-factor');
      return;
    }

    // ⚠ Sanitized again inside NAVIGATE, on a value the server already sanitized. Belt and braces
    // on the control whose failure mode is a credible phishing page — and the two checks guard
    // different things, because a compromised or simply wrong server response is exactly the case
    // where the client-side check is the only one left.
    this.#navigate(result.returnUrl);
  }

  /**
   * The transport-failure branch, shared by every request this page makes.
   *
   * ⚠ The same message a rejected credential produces. A distinct "something went wrong" for a
   * transport failure is a side channel: an attacker who can tell a 500 from a rejection learns
   * which addresses reach a code path that touches a grain.
   */
  #failed(): void {
    this.error.set(this.#uniformFailure());
    this.password.set('');
    this.code.set('');
    this.busy.set(false);
  }

  #uniformFailure(): string {
    return $localize`:@@identity.signIn.uniformFailure:The email address or credential is incorrect, or the account cannot sign in right now.`;
  }
}
