import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { IdentityApi } from '../identity-api';
import { passkeyUnavailableReason } from '../passkey';
import { sanitizeReturnUrl } from '../return-url';

/** Which half of the two-step form is showing. */
type Step = 'address' | 'credential';

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
            [disabled]="step() === 'credential'"
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

        <button
          xuiButton
          type="submit"
          [color]="step() === 'address' ? 'primary' : 'secondary'"
          [loading]="busy()"
          [disabled]="busy()"
        >
          @if (step() === 'address') {
            <span i18n="@@identity.signIn.continue">Continue</span>
          } @else {
            <span i18n="@@identity.signIn.submit">Sign in</span>
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

  /** Which half of the form is showing. */
  readonly step = signal<Step>('address');

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
  }

  /** Submits whichever step is showing. */
  onSubmit(): void {
    if (this.busy()) {
      return;
    }

    if (this.step() === 'address') {
      this.#begin();
      return;
    }

    this.#signInWithPassword();
  }

  /**
   * Starts the WebAuthn ceremony.
   *
   * ⚠ Not implemented, and it says so rather than pretending. The assertion endpoint
   * (`/api/signin/passkey/*`) is not built — see the report accompanying this change — and a button
   * that silently did nothing would read as a broken authenticator.
   */
  onUsePasskey(): void {
    this.error.set(
      $localize`:@@identity.signIn.passkeyNotWired:Passkey sign-in is not available yet. Use your password.`,
    );
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
      error: () => {
        // ⚠ The same message a rejected credential produces. A distinct "something went wrong" for
        // a transport failure is a side channel: an attacker who can tell a 500 from a rejection
        // learns which addresses reach a code path that touches a grain.
        this.error.set(
          $localize`:@@identity.signIn.uniformFailure:The email address or credential is incorrect, or the account cannot sign in right now.`,
        );
        this.busy.set(false);
      },
    });
  }

  #signInWithPassword(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signInWithPassword(this.email(), this.password(), this.returnUrl()).subscribe({
      next: (result) => {
        this.busy.set(false);

        if (!result.succeeded) {
          this.error.set(result.message);
          this.password.set('');
          return;
        }

        // ⚠ Sanitized again, on a value the server already sanitized. Belt and braces on the
        // control whose failure mode is a credible phishing page — and the two checks guard
        // different things, because a compromised or simply wrong server response is exactly the
        // case where the client-side check is the only one left.
        window.location.assign(sanitizeReturnUrl(result.returnUrl));
      },
      error: () => {
        this.error.set(
          $localize`:@@identity.signIn.uniformFailure:The email address or credential is incorrect, or the account cannot sign in right now.`,
        );
        this.password.set('');
        this.busy.set(false);
      },
    });
  }
}
