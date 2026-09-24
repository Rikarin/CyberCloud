import { ChangeDetectionStrategy, Component, afterNextRender, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { DevicePageResponse, IdentityApi } from '../identity-api';
import { NAVIGATE } from '../navigate';

/**
 * The device page — RFC 8628 § 3.3's verification page, for `cyc login` on a headless box (#43).
 *
 * The device printed a short code and `/device/verify`; the host redirected here, with the code in
 * the query when the link carried one. Three steps, in the order the person meets them:
 *
 * 1. **Enter the code.** Posted to `/api/device/lookup`, which answers whether a sign-in is
 *    waiting for it — one sentence for every wrong code — and whether this browser is signed in.
 * 2. **Sign in, if not already.** A full-page navigation to the sign-in page with this page as the
 *    return URL. The device named no tenant, so the sign-in page asks for the organisation; the
 *    person lands back here with the code still in the query.
 * 3. **Allow or deny.** What the device asked for, the account it will act as, and two buttons —
 *    `/api/device/decision`. After that the page says to return to the device, which is polling.
 *
 * ⚠ **Nothing rendered comes from the query but the code itself.** The client's name is the one the
 * server resolved from the registration, and the account is the cookie's. The flow's known weakness
 * is somebody else's device whose code the person is talked into typing; the one defence a page
 * has is to say plainly which account is being lent and to what, and it says it from the server.
 *
 * ⚠ **The code is typed however it comes out** — lower case, no dash — and the server normalizes it.
 */
@Component({
  selector: 'cc-device-code',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, XuiButton, XuiInput],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.device.heading">Sign in a device</h1>

      @if (message(); as text) {
        <p
          class="mt-4 rounded-md px-3 py-2 text-sm"
          [class.bg-error-muted]="!done()"
          [class.text-error-foreground]="!done()"
          [class.bg-surface]="done()"
          role="alert"
          aria-live="assertive"
        >
          {{ text }}
        </p>
      }

      @if (waiting(); as request) {
        <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.device.subheading">
          <strong class="text-foreground">{{ request.clientName }}</strong> on another device wants to sign in as
          <strong class="text-foreground">{{ request.account }}</strong
          >. It will be able to:
        </p>

        <ul
          class="mt-4 flex flex-col gap-2 text-sm"
          aria-label="Permissions requested"
          i18n-aria-label="@@identity.device.scopesLabel"
        >
          @for (scope of request.scopes; track scope) {
            <li class="flex items-start gap-2">
              <span class="text-primary" aria-hidden="true">&#10003;</span>
              <span>{{ describe(scope) }}</span>
            </li>
          }
        </ul>

        <p class="text-foreground-muted mt-4 text-sm" i18n="@@identity.device.warning">
          Only continue if you started this sign-in yourself and the device shows the code
          <strong class="text-foreground">{{ request.userCode }}</strong
          >.
        </p>

        <div class="mt-6 flex flex-col gap-3">
          <button
            xuiButton
            type="button"
            color="primary"
            [disabled]="busy()"
            (click)="onDecide('allow')"
            i18n="@@identity.device.allow"
          >
            Allow
          </button>
          <button
            xuiButton
            type="button"
            variant="outline"
            [disabled]="busy()"
            (click)="onDecide('deny')"
            i18n="@@identity.device.deny"
          >
            Deny
          </button>
        </div>
      } @else if (!done()) {
        <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.device.enterCode">
          Enter the code your device is showing.
        </p>

        <form class="mt-5 flex flex-col gap-4" (ngSubmit)="onLookup()">
          <div class="flex flex-col gap-1.5">
            <label class="text-sm font-medium" for="cc-user-code" i18n="@@identity.device.codeLabel">Code</label>
            <input
              xuiInput
              id="cc-user-code"
              name="userCode"
              type="text"
              autocomplete="one-time-code"
              [attr.autocapitalize]="'characters'"
              spellcheck="false"
              placeholder="BCDF-GHJK"
              i18n-placeholder="@@identity.device.userCodePlaceholder"
              required
              [(ngModel)]="userCode"
            />
          </div>

          <button xuiButton type="submit" color="primary" [disabled]="busy()" i18n="@@identity.device.continue">
            Continue
          </button>
        </form>
      }
    </div>
  `
})
export class DeviceCodePage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);
  readonly #navigate = inject(NAVIGATE);

  /** What the person typed — or the code the verification link carried. */
  readonly userCode = signal(this.#route.snapshot.queryParamMap.get('user_code') ?? '');

  /** The request waiting for an answer, once the server has described it to a signed-in person. */
  readonly waiting = signal<DevicePageResponse | null>(null);

  /** The sentence to show: a refusal, or the outcome after an answer. */
  readonly message = signal<string | null>(null);

  /** Whether the person has answered — the page then only says so. */
  readonly done = signal(false);

  /** A request in flight. */
  readonly busy = signal(false);

  /** Where the sign-in page returns to: this page, with the code, so the person resumes here. */
  readonly returnHere = computed(() => `/device-code?user_code=${encodeURIComponent(this.userCode().trim())}`);

  constructor() {
    // ⚠ Browser only, for the reason the consent page gives: a server-side call would run without
    // the cookie and serialize its answer into the document.
    afterNextRender(() => {
      if (this.userCode().trim()) {
        this.onLookup();
      }
    });
  }

  /** Step one: ask what is waiting for this code, and whether this browser is signed in. */
  onLookup(): void {
    this.busy.set(true);
    this.message.set(null);

    this.#api.lookupDevice(this.userCode()).subscribe({
      next: response => {
        this.busy.set(false);

        if (!response.found) {
          this.message.set(response.message);
          return;
        }

        if (!response.signedIn) {
          // Step two: sign in, and come back here with the code.
          this.#navigate(`/signin?returnUrl=${encodeURIComponent(this.returnHere())}`);
          return;
        }

        if (response.status !== 'pending') {
          this.message.set(response.message);
          return;
        }

        this.waiting.set(response);
      },
      error: () => this.#failed()
    });
  }

  /** Step three: the answer. */
  onDecide(decision: 'allow' | 'deny'): void {
    const request = this.waiting();

    if (request === null) {
      return;
    }

    this.busy.set(true);

    this.#api.decideDevice(request.userCode, decision).subscribe({
      next: response => {
        this.busy.set(false);
        this.waiting.set(null);
        this.done.set(response.found);
        this.message.set(response.message);
      },
      error: () => this.#failed()
    });
  }

  /** What a scope means to a person, in a sentence — the consent page's wording. */
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

  #failed(): void {
    this.busy.set(false);
    this.message.set($localize`:@@identity.device.failed:Something went wrong. Try the code again.`);
  }
}
