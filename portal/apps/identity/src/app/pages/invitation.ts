import { ChangeDetectionStrategy, Component, afterNextRender, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { IdentityApi, InvitationPageResponse } from '../identity-api';

/**
 * The invitation page — where the link in an invitation mail lands (#43, step 7).
 *
 * The link carries three things in its query: the tenant, the invitation and a one-time secret. The
 * page asks the host what the link is (`/api/invitations/describe`), and for a pending one asks the
 * person for a name and a password for this organisation (`/api/invitations/accept`); the host makes
 * them a member, signs them in, and answers where the portal is.
 *
 * ⚠ **The same page for somebody new and for somebody with an account elsewhere.** A user belongs
 * to one organisation (docs/plan/11 § Sign-up and tenant creation), so a colleague who already uses
 * Cyber Cloud gets a separate sign-in here — the page says so rather than offering a "sign in with
 * your existing account" that would sign them into the wrong organisation.
 *
 * ⚠ **What is rendered comes from the host.** The address and the organisation's name are what the
 * invitation holds, never the query; the query's three values are only posted back.
 *
 * ⚠ **The password is posted in the body and cleared as soon as it is sent.**
 */
@Component({
  selector: 'cc-invitation',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, XuiButton, XuiInput],
  template: `
    <div class="bg-surface-raised border-border rounded-lg border p-6 shadow-sm">
      <h1 class="text-xl font-semibold" i18n="@@identity.invitation.heading">Join an organisation</h1>

      @if (message(); as text) {
        <p
          class="mt-4 rounded-md px-3 py-2 text-sm"
          [class.bg-error-muted]="!accepted()"
          [class.text-error-foreground]="!accepted()"
          role="alert"
          aria-live="assertive"
        >
          {{ text }}
        </p>
      }

      @if (accepted()) {
        @if (portalUrl(); as url) {
          <a xuiButton color="primary" class="mt-6" [attr.href]="url" i18n="@@identity.invitation.toPortal">
            Continue to the portal
          </a>
        }
      } @else if (invitation(); as found) {
        @if (found.status === 'pending') {
          <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.invitation.subheading">
            You have been invited to <strong class="text-foreground">{{ found.tenantName }}</strong> as
            <strong class="text-foreground">{{ found.email }}</strong
            >.
          </p>
          <p class="text-foreground-muted mt-2 text-sm" i18n="@@identity.invitation.separate">
            Choose a name and a password for this organisation. If you already use Cyber Cloud elsewhere, that sign-in
            is not changed — each organisation has its own.
          </p>

          <form class="mt-5 flex flex-col gap-4" (ngSubmit)="onAccept()">
            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-display-name" i18n="@@identity.invitation.nameLabel">
                Your name
              </label>
              <input
                xuiInput
                id="cc-display-name"
                name="displayName"
                type="text"
                autocomplete="name"
                required
                [(ngModel)]="displayName"
              />
            </div>

            <div class="flex flex-col gap-1.5">
              <label class="text-sm font-medium" for="cc-new-password" i18n="@@identity.invitation.passwordLabel">
                Password
              </label>
              <input
                xuiInput
                id="cc-new-password"
                name="password"
                type="password"
                autocomplete="new-password"
                required
                [(ngModel)]="password"
              />
            </div>

            <button xuiButton type="submit" color="primary" [disabled]="busy()" i18n="@@identity.invitation.accept">
              Accept the invitation
            </button>
          </form>
        }
      } @else if (!message()) {
        <p class="text-foreground-muted mt-1 text-sm" role="status" i18n="@@identity.invitation.loading">
          Checking the invitation…
        </p>
      }
    </div>
  `
})
export class InvitationPage {
  readonly #api = inject(IdentityApi);
  readonly #route = inject(ActivatedRoute);

  /** The link's three parts — posted back, never rendered. */
  readonly #link = computed(() => {
    const query = this.#route.snapshot.queryParamMap;

    return {
      tenant: query.get('tenant') ?? '',
      invitation: query.get('invitation') ?? '',
      token: query.get('token') ?? ''
    };
  });

  /** What the host said the link is. */
  readonly invitation = signal<InvitationPageResponse | null>(null);

  /** The sentence to show. */
  readonly message = signal<string | null>(null);

  /** Whether the person is a member now. */
  readonly accepted = signal(false);

  /** Where the portal is, once accepted — only an http(s) URL is rendered as a link. */
  readonly portalUrl = signal<string | null>(null);

  readonly displayName = signal('');
  readonly password = signal('');
  readonly busy = signal(false);

  constructor() {
    // ⚠ Browser only, for the reason the consent page gives.
    afterNextRender(() => this.#describe());
  }

  /** Accepts the invitation with the name and password typed. */
  onAccept(): void {
    this.busy.set(true);

    const password = this.password();
    this.password.set('');

    this.#api.acceptInvitation(this.#link(), this.displayName().trim(), password).subscribe({
      next: response => {
        this.busy.set(false);
        this.message.set(response.message);

        if (response.succeeded) {
          this.accepted.set(true);
          this.portalUrl.set(/^https?:\/\//.test(response.portalUrl) ? response.portalUrl : null);
        }
      },
      error: () => this.#failed()
    });
  }

  #describe(): void {
    this.#api.describeInvitation(this.#link()).subscribe({
      next: response => {
        if (!response.found || response.status !== 'pending') {
          this.message.set(response.message);
        }

        if (response.found) {
          this.invitation.set(response);
        }
      },
      error: () => this.#failed()
    });
  }

  #failed(): void {
    this.busy.set(false);
    this.message.set($localize`:@@identity.invitation.failed:Something went wrong. Open the link from the mail again.`);
  }
}
