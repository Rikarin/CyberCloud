import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { IdentityApi, SignUpCompleteResponse, SignUpCredential, signUpIsClosed } from '../identity-api';
import { NAVIGATE } from '../navigate';
import { passkeyUnavailableReason, registerPasskey } from '../passkey';
import { sanitizeReturnUrl } from '../return-url';

/** Which of the three steps is showing. */
type Step = 'address' | 'code' | 'details';

/**
 * The self-serve sign-up page — three steps on one route.
 *
 * docs/plan/11 § Sign-up and tenant creation gives the self-serve path as "Email + passkey → verify
 * → create tenant → create default subscription and resource group → seed ReBAC". The three steps
 * here are that sentence's first three words, and `POST /api/signup/complete` is the rest of it:
 *
 * 1. **The address.** `POST /api/signup/begin` answers `sent: true` whether the address was free,
 *    taken or malformed — the same enumeration rule the sign-in page lives under — and sets the
 *    ticket cookie every later call is authenticated by. The page then says where the code is,
 *    and on a development run that is the silo's console in the Aspire dashboard rather than a
 *    mailbox, because there is no MTA (#93). The page says that too, because a person staring at
 *    an empty inbox has no other way to learn it.
 * 2. **The code.** `POST /api/signup/verify` answers one `false` for a wrong, expired or burnt
 *    code; after five wrong answers the challenge is gone and the page offers a fresh one.
 * 3. **The details and the credential.** A display name, an organisation name, and — as
 *    `color="primary"`, because docs/plan/11 § Credentials makes passkeys "the **default** offered
 *    credential at sign-up, not an upsell" — a passkey; "use a password instead" is a
 *    `variant="link"` beneath it. Either ends in `POST /api/signup/complete`, and on `succeeded`
 *    the page leaves, full-page, for the response's `returnUrl`: the original `/authorize` request
 *    with `tenant=<new tenant>` set, which the identity host resumes with the cookie the
 *    completion just issued.
 *
 * ⚠ **Failures on step three are rendered verbatim and are distinguishable on purpose.** "That
 * organisation name is taken" and the naming rule's sentence are answers about the person's own
 * input, given to somebody who has proven an address — not to a stranger. A step failure leaves
 * the ticket valid and the page on step three, because `complete` is re-drivable: the server skips
 * what it already created.
 *
 * ⚠ **No dialog and no drawer**, for the same SSR reason as the sign-in page: `XuiDialog`,
 * `XuiDrawer` and `XuiAlertDialog` attach a CDK overlay from a constructor `effect()` that runs
 * server-side and throws. Every state here is inline.
 */
@Component({
  selector: 'cc-sign-up',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, XuiButton, XuiInput],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.signUp.heading">Create an account</h1>
      <p class="text-foreground-muted mt-1 text-sm">
        @switch (step()) {
          @case ('address') {
            <span i18n="@@identity.signUp.subheading">Start with your email address. We will send you a code.</span>
          }
          @case ('code') {
            <span i18n="@@identity.signUp.codeSubheading">Check {{ email() }} for a 6-digit code.</span>
            <span class="block" i18n="@@identity.signUp.codeDevHint">
              On a development run there is no mail: the code is in the silo's console in the Aspire dashboard.
            </span>
          }
          @default {
            <span i18n="@@identity.signUp.detailsSubheading">Choose what to call you and your organisation.</span>
          }
        }
      </p>

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
        @switch (step()) {
          @case ('address') {
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-signup-email" i18n="@@identity.signUp.emailLabel">
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

            <button
              xuiButton
              type="submit"
              color="primary"
              [loading]="busy()"
              [disabled]="busy()"
              i18n="@@identity.signUp.continue"
            >
              Continue
            </button>
          }

          @case ('code') {
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-signup-code" i18n="@@identity.signUp.codeLabel">
                6-digit code
              </label>
              <!--
                ⚠ 'autocomplete="one-time-code"' so a browser that saw the code arrive can offer it,
                and 'inputmode="numeric"' so a phone shows digits. The value is a string end to
                end: 042317 is a legal code, and a number field would eat the zero.
              -->
              <input
                xuiInput
                id="cc-signup-code"
                name="code"
                type="text"
                autocomplete="one-time-code"
                inputmode="numeric"
                pattern="[0-9]{6}"
                maxlength="6"
                required
                [class.cc-shake]="shake()"
                [(ngModel)]="code"
              />
            </div>

            <button
              xuiButton
              type="submit"
              color="primary"
              [loading]="busy()"
              [disabled]="busy()"
              i18n="@@identity.signUp.verify"
            >
              Verify
            </button>

            <div class="flex flex-wrap gap-3">
              <button
                xuiButton
                type="button"
                variant="link"
                size="sm"
                [disabled]="busy()"
                (click)="onResend()"
                i18n="@@identity.signUp.resend"
              >
                Send a new code
              </button>
              <button
                xuiButton
                type="button"
                variant="link"
                size="sm"
                [disabled]="busy()"
                (click)="onChangeAddress()"
                i18n="@@identity.signUp.changeAddress"
              >
                Use a different address
              </button>
            </div>
          }

          @default {
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-signup-name" i18n="@@identity.signUp.displayNameLabel">
                Your name
              </label>
              <input
                xuiInput
                id="cc-signup-name"
                name="displayName"
                type="text"
                autocomplete="name"
                required
                [(ngModel)]="displayName"
              />
            </div>

            <div class="flex flex-col gap-1.5">
              <label
                class="text-sm font-medium"
                for="cc-signup-organisation"
                i18n="@@identity.signUp.organisationLabel"
              >
                Organisation
              </label>
              <input
                xuiInput
                id="cc-signup-organisation"
                name="organizationName"
                type="text"
                autocomplete="organization"
                required
                [(ngModel)]="organizationName"
              />
            </div>

            @if (usePassword()) {
              <div class="flex flex-col gap-1.5">
                <label class="text-sm font-medium" for="cc-signup-password" i18n="@@identity.signUp.passwordLabel">
                  Password
                </label>
                <!--
                  ⚠ 'autocomplete="new-password"' so a password manager offers to generate one, and
                  the value only ever travels in a request body — never a query string.
                -->
                <input
                  xuiInput
                  id="cc-signup-password"
                  name="password"
                  type="password"
                  autocomplete="new-password"
                  required
                  [(ngModel)]="password"
                />
              </div>

              <button
                xuiButton
                type="submit"
                color="primary"
                [loading]="busy()"
                [disabled]="busy()"
                i18n="@@identity.signUp.createWithPassword"
              >
                Create
              </button>
            } @else {
              <!--
                The primary action creates a passkey. docs/plan/11 § Credentials makes this the
                default rather than an upsell, so it is the button with the weight.
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
                        This browser cannot create passkeys. You can still continue with a password.
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
                  Create with a passkey
                </button>
              }

              <button
                xuiButton
                type="button"
                variant="link"
                size="sm"
                [disabled]="busy()"
                (click)="onUsePassword()"
                i18n="@@identity.signUp.withPassword"
              >
                Use a password instead
              </button>
            }
          }
        }
      </form>

      <p class="text-foreground-muted mt-6 text-sm">
        <span i18n="@@identity.signUp.haveAccount">Already have an account?</span>
        <a class="text-primary ms-1 underline" [href]="signInHref()" i18n="@@identity.signUp.signIn">Sign in</a>
      </p>
    </div>
  `,
  styles: `
    @keyframes cc-shake {
      0%,
      100% {
        transform: translateX(0);
      }
      25%,
      75% {
        transform: translateX(-4px);
      }
      50% {
        transform: translateX(4px);
      }
    }

    .cc-shake {
      animation: cc-shake 240ms ease-in-out;
    }
  `
})
export class SignUpPage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);
  readonly #navigate = inject(NAVIGATE);

  /** Sanitized on read; the raw query value is never stored. See `sign-in.ts` for why. */
  readonly returnUrl = computed(() => sanitizeReturnUrl(this.#route.snapshot.queryParamMap.get('returnUrl')));

  /** Which step is showing. */
  readonly step = signal<Step>('address');

  /** The address, bound to the field. */
  readonly email = signal('');

  /** The six digits, bound to the field. */
  readonly code = signal('');

  /** What to call the person. */
  readonly displayName = signal('');

  /** The organisation's display name. Its slug is the server's to derive. */
  readonly organizationName = signal('');

  /** The password, when the person chose one. Cleared the moment it has been sent. */
  readonly password = signal('');

  /** Whether step three is showing the password field rather than the passkey button. */
  readonly usePassword = signal(false);

  /** Whether a request is in flight. */
  readonly busy = signal(false);

  /** Whether the code field is shaking — a wrong code, just now. */
  readonly shake = signal(false);

  /** What to render in the alert region, or `null`. */
  readonly error = signal<string | null>(null);

  /** Why a passkey cannot be created here, or `null` when it can. SSR-safe. */
  readonly passkeyBlockedBy = computed(() => passkeyUnavailableReason());

  /** The sign-in link, carrying the same sanitized return URL. */
  readonly signInHref = computed(() => `/signin?returnUrl=${encodeURIComponent(this.returnUrl())}`);

  /** Submits whichever step is showing. */
  onSubmit(): void {
    if (this.busy()) {
      return;
    }

    switch (this.step()) {
      case 'address':
        this.#begin();
        return;
      case 'code':
        this.#verify();
        return;
      default:
        if (this.usePassword()) {
          this.#complete({ kind: 'password', password: this.password() });
        } else {
          this.#completeWithPasskey();
        }
    }
  }

  /** Asks for another code, on the same sign-up. */
  onResend(): void {
    if (this.busy()) {
      return;
    }

    this.code.set('');
    this.#begin();
  }

  /** Goes back to the address step. The server keeps the sign-up; a new address restarts its code. */
  onChangeAddress(): void {
    this.code.set('');
    this.error.set(null);
    this.step.set('address');
  }

  /** Switches step three from the passkey to a password. */
  onUsePassword(): void {
    this.usePassword.set(true);
    this.error.set(null);
  }

  #begin(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signUpBegin(this.email(), this.returnUrl()).subscribe({
      next: response => {
        this.busy.set(false);

        if (signUpIsClosed(response)) {
          // ⚠ Rendered verbatim: "Sign-up is not open on this deployment." There is nothing else
          // to do on this page, so the person is told rather than sent round the steps.
          this.error.set(response.message);
          return;
        }

        this.step.set('code');
      },
      error: () => this.#failed()
    });
  }

  #verify(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signUpVerify(this.code()).subscribe({
      next: response => {
        this.busy.set(false);

        if (signUpIsClosed(response)) {
          this.error.set(response.message);
          return;
        }

        if (!response.verified) {
          // ⚠ One sentence for a wrong code, an expired one and a burnt one, because the server
          // answers one `false` for all three. The resend link is the way out of the last two.
          this.code.set('');
          this.error.set(
            $localize`:@@identity.signUp.wrongCode:That code did not work. Check it and try again, or send a new one.`
          );
          this.shake.set(true);
          setTimeout(() => this.shake.set(false), 300);
          return;
        }

        this.step.set('details');
      },
      error: () => this.#failed()
    });
  }

  /**
   * The passkey ceremony: a registration challenge, the authenticator, then `complete`.
   *
   * ⚠ A cancelled prompt is not a failure — `registerPasskey` answers `null` for it — and an empty
   * options string is the server saying it could not build a challenge (unverified sign-up, or a
   * relying-party misconfiguration), which the password path still answers.
   */
  #completeWithPasskey(): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signUpPasskeyBegin(this.displayName()).subscribe({
      next: response => {
        if (signUpIsClosed(response)) {
          this.busy.set(false);
          this.error.set(response.message);
          return;
        }

        if (response.optionsJson.length === 0) {
          this.busy.set(false);
          this.error.set(
            $localize`:@@identity.signUp.passkeyUnavailable:A passkey could not be created just now. Use a password instead.`
          );
          return;
        }

        registerPasskey(response.optionsJson).then(
          attestation => {
            if (attestation === null) {
              this.busy.set(false);
              return;
            }

            this.#complete({ kind: 'passkey', attestationJson: attestation });
          },
          () => this.#failed()
        );
      },
      error: () => this.#failed()
    });
  }

  #complete(credential: SignUpCredential): void {
    this.busy.set(true);
    this.error.set(null);

    this.#api.signUpComplete(this.displayName(), this.organizationName(), credential, this.returnUrl()).subscribe({
      next: result => this.#completed(result),
      error: () => this.#failed()
    });
  }

  #completed(result: SignUpCompleteResponse): void {
    this.busy.set(false);

    // ⚠ The password is not needed again either way: on success the session cookie is set, on
    // failure the person retypes it. A signal still holding it is a credential kept alive for no
    // reason — credentials-never-leak.spec.ts covers where it must never go.
    this.password.set('');

    if (!result.succeeded) {
      // ⚠ Rendered verbatim. The server chose the sentence, and each of its five is an answer
      // about this person's own input; the page stays on step three so they can change it.
      this.error.set(result.message);
      return;
    }

    // ⚠ Sanitized again inside NAVIGATE, on a value the server already sanitized — the same belt
    // and braces the sign-in page wears. A full-page navigation, because the destination is the
    // /authorize request that started this and it has to carry the cookie.
    this.#navigate(result.returnUrl);
  }

  /**
   * The transport-failure branch, shared by every request this page makes.
   *
   * ⚠ Phrased as a transport failure and nothing more specific — on step one, in particular, a
   * message that differed from the success case would be the enumeration oracle the server's
   * uniform answer exists to remove.
   */
  #failed(): void {
    this.busy.set(false);
    this.password.set('');
    this.error.set(
      $localize`:@@identity.signUp.failed:We could not reach the sign-up service just now. Try again in a moment.`
    );
  }
}
