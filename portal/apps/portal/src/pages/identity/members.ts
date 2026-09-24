import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiInput } from '@xui/input';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { XuiTag } from '@xui/tag';
import { IdentityAdminApi, Invitation, Member } from '../../app/api/identity-admin';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { IdentityNav, messageOf, when } from './identity-shared';

/** What the last action did, shown once and replaced by the next. */
type Outcome =
  | { readonly kind: 'invited' | 'resent' | 'revoked' | 'removed'; readonly subject: string }
  | { readonly kind: 'failed'; readonly subject: string; readonly message: string };

/** A deliberately loose check — the invitation grain normalizes and judges the address. */
const EMAIL = /^[^\s@]+@[^\s@]+$/;

/**
 * The members page: who is in the organisation, who has been invited, and the invite form —
 * docs/plan/20 § The pages that are not generated, "Identity admin". Issue #41, over #43's
 * invitation.
 *
 * ⚠ **Owner only, and the page doesn't guess.** Every call needs `assignRole` on the tenant; a
 * reader gets a 403 and anybody else a 404, which `cc-page-status` renders as it does on every
 * page. Hiding the page from non-owners would need the caller's roles, which are ReBAC's and not
 * in the token (docs/plan/11 § Protocol) — so the answer is the API's.
 *
 * ⚠ **An invitation grants no role.** The member arrives able to sign in and see nothing; a role is
 * the access page's grant, `reader-user-{userId}` on a scope. The table shows the user id for that
 * reason.
 *
 * ⚠ **Every `xui-th` and `xui-td` on the three identity pages carries its ARIA role by hand.**
 * `@xui/table` 3.0.0 gives the table and the row their roles and the cells none, so axe fails
 * `aria-required-children` on any rendered row, which `identity-pages.spec.ts` measured.
 * docs/plan/20 records the library gap as owed.
 *
 * ⚠ **Removing and revoking ask twice.** Removing ends every session, clears every credential and
 * deletes every role the member holds; revoking kills a link somebody may be about to open. A
 * resend is one click: it kills the old link too, but it sends a new one in the same breath.
 */
@Component({
  selector: 'cc-identity-members',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IdentityNav,
    NeedsTenant,
    PageStatus,
    XuiButton,
    XuiCallout,
    XuiInput,
    XuiNonIdealState,
    XuiTable,
    XuiTr,
    XuiTh,
    XuiTd,
    XuiTag
  ],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" i18n="@@identity.members.heading">Members</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.members.subheading">
        The people in this organisation. Inviting somebody makes them a member with no role — grant one on the access
        page of the scope they need.
      </p>
      <cc-identity-nav />

      <section class="border-border mt-4 rounded-md border p-4" aria-labelledby="cc-invite-heading">
        <h2 class="text-sm font-semibold" id="cc-invite-heading" i18n="@@identity.members.inviteHeading">
          Invite a colleague
        </h2>
        <form class="mt-3 flex flex-wrap items-start gap-3" (submit)="onInvite($event)">
          <div class="flex min-w-72 flex-1 flex-col gap-1.5">
            <label class="text-sm font-medium" for="cc-invite-email" i18n="@@identity.members.email"
              >Email address</label
            >
            <input
              xuiInput
              id="cc-invite-email"
              type="email"
              autocomplete="off"
              [value]="email()"
              (input)="onEmail($event)"
              [attr.aria-invalid]="emailInvalid() ? 'true' : null"
              [attr.aria-describedby]="emailInvalid() ? 'cc-invite-email-error' : 'cc-invite-email-hint'"
            />
            @if (emailInvalid()) {
              <p
                class="text-error text-xs"
                id="cc-invite-email-error"
                role="alert"
                i18n="@@identity.members.emailInvalid"
              >
                That doesn't look like an email address.
              </p>
            } @else {
              <p class="text-foreground-muted text-xs" id="cc-invite-email-hint" i18n="@@identity.members.emailHint">
                They get a link that works once and expires in seven days.
              </p>
            }
          </div>
          <button
            xuiButton
            color="primary"
            size="sm"
            type="submit"
            class="mt-6"
            [disabled]="busy() !== null"
            [loading]="busy() === 'invite'"
            i18n="@@identity.members.invite"
          >
            Send invitation
          </button>
        </form>
      </section>

      @if (outcome(); as outcome) {
        <xui-callout
          class="mt-4"
          [color]="outcome.kind === 'failed' ? 'error' : 'success'"
          [attr.data-outcome]="outcome.kind"
        >
          @switch (outcome.kind) {
            @case ('invited') {
              <p i18n="@@identity.members.outcome.invited">An invitation is on its way to {{ outcome.subject }}.</p>
            }
            @case ('resent') {
              <p i18n="@@identity.members.outcome.resent">
                A new link went to {{ outcome.subject }}. The one they had before no longer works.
              </p>
            }
            @case ('revoked') {
              <p i18n="@@identity.members.outcome.revoked">The invitation to {{ outcome.subject }} is withdrawn.</p>
            }
            @case ('removed') {
              <p i18n="@@identity.members.outcome.removed">
                {{ outcome.subject }} is removed: signed out everywhere, and every role they held is gone.
              </p>
            }
            @case ('failed') {
              <p>{{ outcome.message }}</p>
            }
          }
        </xui-callout>
      }

      <section class="mt-6" aria-labelledby="cc-pending-heading">
        <h2 class="text-sm font-semibold" id="cc-pending-heading" i18n="@@identity.members.pendingHeading">
          Pending invitations
        </h2>
        <cc-page-status [state]="invitations()" />
        @if (invitations().kind === 'ready') {
          @if (pending().length === 0) {
            <p class="text-foreground-muted mt-2 text-sm" i18n="@@identity.members.noPending">
              Nobody is waiting on a link.
            </p>
          } @else {
            <xui-table class="mt-3" striped aria-labelledby="cc-pending-heading">
              <xui-tr>
                <xui-th role="columnheader" class="flex-1" i18n="@@identity.members.col.email">Email</xui-th>
                <xui-th role="columnheader" class="w-28" i18n="@@identity.members.col.status">Status</xui-th>
                <xui-th role="columnheader" class="w-48" i18n="@@identity.members.col.expires">Link expires</xui-th>
                <xui-th role="columnheader" class="w-64"
                  ><span class="sr-only" i18n="@@identity.members.col.actions">Actions</span></xui-th
                >
              </xui-tr>
              @for (invitation of pending(); track invitation.name) {
                <xui-tr [attr.data-invitation]="invitation.name">
                  <xui-td role="cell" class="flex-1" truncate>{{ invitation.properties.email }}</xui-td>
                  <xui-td role="cell" class="w-28"
                    ><xui-tag minimal>{{ invitation.properties.status }}</xui-tag></xui-td
                  >
                  <xui-td role="cell" class="w-48">{{ when(invitation.properties.expiresAt) }}</xui-td>
                  <xui-td role="cell" class="w-64">
                    @if (confirming() === 'revoke:' + invitation.name) {
                      <span class="flex flex-wrap items-center gap-2">
                        <button
                          xuiButton
                          color="error"
                          size="sm"
                          type="button"
                          [disabled]="busy() !== null"
                          (click)="revoke(invitation)"
                          i18n="@@identity.members.revokeConfirm"
                        >
                          Withdraw it
                        </button>
                        <button
                          xuiButton
                          variant="ghost"
                          size="sm"
                          type="button"
                          (click)="confirming.set(null)"
                          i18n="@@identity.members.keep"
                        >
                          Keep it
                        </button>
                      </span>
                    } @else {
                      <span class="flex flex-wrap gap-2">
                        <button
                          xuiButton
                          variant="outline"
                          size="sm"
                          type="button"
                          [disabled]="busy() !== null"
                          [attr.aria-label]="resendLabel(invitation)"
                          (click)="resend(invitation)"
                          i18n="@@identity.members.resend"
                        >
                          Resend
                        </button>
                        <button
                          xuiButton
                          variant="outline"
                          color="error"
                          size="sm"
                          type="button"
                          [disabled]="busy() !== null"
                          [attr.aria-label]="revokeLabel(invitation)"
                          (click)="confirming.set('revoke:' + invitation.name)"
                          i18n="@@identity.members.revoke"
                        >
                          Revoke
                        </button>
                      </span>
                    }
                  </xui-td>
                </xui-tr>
              }
            </xui-table>
          }
        }
      </section>

      <section class="mt-6" aria-labelledby="cc-members-heading">
        <h2 class="text-sm font-semibold" id="cc-members-heading" i18n="@@identity.members.membersHeading">Members</h2>
        <cc-page-status [state]="members()" />
        @if (members(); as state) {
          @if (state.kind === 'ready') {
            @if (state.value.length === 0) {
              <xui-non-ideal-state class="mt-6" [title]="emptyTitle" [description]="emptyDescription" />
            } @else {
              <xui-table class="mt-3" striped aria-labelledby="cc-members-heading">
                <xui-tr>
                  <xui-th role="columnheader" class="w-48" i18n="@@identity.members.col.name">Name</xui-th>
                  <xui-th role="columnheader" class="flex-1" i18n="@@identity.members.col.memberEmail">Email</xui-th>
                  <xui-th role="columnheader" class="w-28" i18n="@@identity.members.col.memberStatus">Status</xui-th>
                  <xui-th role="columnheader" class="w-72" i18n="@@identity.members.col.userId">User id</xui-th>
                  <xui-th role="columnheader" class="w-48"
                    ><span class="sr-only" i18n="@@identity.members.col.memberActions">Actions</span></xui-th
                  >
                </xui-tr>
                @for (member of state.value; track member.name) {
                  <xui-tr [attr.data-member]="member.name">
                    <xui-td role="cell" class="w-48" truncate>{{ member.properties.displayName || '—' }}</xui-td>
                    <xui-td role="cell" class="flex-1" truncate>{{ member.properties.email }}</xui-td>
                    <xui-td role="cell" class="w-28"
                      ><xui-tag minimal>{{ member.properties.status }}</xui-tag></xui-td
                    >
                    <xui-td role="cell" class="w-72" truncate
                      ><code class="text-xs">{{ member.name }}</code></xui-td
                    >
                    <xui-td role="cell" class="w-48">
                      @if (member.properties.status !== 'deprovisioned') {
                        @if (confirming() === 'remove:' + member.name) {
                          <span class="flex flex-wrap items-center gap-2">
                            <button
                              xuiButton
                              color="error"
                              size="sm"
                              type="button"
                              [disabled]="busy() !== null"
                              (click)="remove(member)"
                              i18n="@@identity.members.removeConfirm"
                            >
                              Remove
                            </button>
                            <button
                              xuiButton
                              variant="ghost"
                              size="sm"
                              type="button"
                              (click)="confirming.set(null)"
                              i18n="@@identity.members.cancel"
                            >
                              Cancel
                            </button>
                          </span>
                        } @else {
                          <button
                            xuiButton
                            variant="outline"
                            color="error"
                            size="sm"
                            type="button"
                            [disabled]="busy() !== null"
                            [attr.aria-label]="removeLabel(member)"
                            (click)="confirming.set('remove:' + member.name)"
                            i18n="@@identity.members.remove"
                          >
                            Remove…
                          </button>
                        }
                      }
                    </xui-td>
                  </xui-tr>
                }
              </xui-table>
            }
          }
        }
      </section>
    }
  `
})
export class IdentityMembers {
  private readonly api = inject(IdentityAdminApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly members = pageState<readonly Member[]>();
  protected readonly invitations = pageState<readonly Invitation[]>();

  /** Pending and expired — the two an owner can still act on. Accepted ones are members now. */
  protected readonly pending = computed(() => {
    const state = this.invitations();
    return state.kind === 'ready'
      ? state.value.filter(x => x.properties.status === 'pending' || x.properties.status === 'expired')
      : [];
  });

  protected readonly email = signal('');
  protected readonly touched = signal(false);
  protected readonly emailInvalid = computed(() => this.touched() && !EMAIL.test(this.email().trim()));

  protected readonly busy = signal<'invite' | 'row' | null>(null);
  protected readonly confirming = signal<string | null>(null);
  protected readonly outcome = signal<Outcome | null>(null);

  protected readonly when = when;
  protected readonly emptyTitle = $localize`:@@identity.members.emptyTitle:Nobody listed yet`;
  protected readonly emptyDescription = $localize`:@@identity.members.emptyDescription:Members appear here as they sign up or are invited. An organisation made before the directory index existed lists only the people added since.`;

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();

      untracked(() => {
        const route = links.identityMembers();
        this.blades.open({ id: route, title: $localize`:@@identity.members.blade:Members`, route });
        this.outcome.set(null);
        this.confirming.set(null);
        if (tenantId !== null) void this.refresh(tenantId);
      });
    });
  }

  protected resendLabel(invitation: Invitation): string {
    return $localize`:@@identity.members.resendLabel:Resend the invitation to ${invitation.properties.email}:email:`;
  }

  protected revokeLabel(invitation: Invitation): string {
    return $localize`:@@identity.members.revokeLabel:Revoke the invitation to ${invitation.properties.email}:email:`;
  }

  protected removeLabel(member: Member): string {
    return $localize`:@@identity.members.removeLabel:Remove ${member.properties.email}:email:`;
  }

  protected onEmail(event: Event): void {
    this.email.set((event.target as HTMLInputElement).value);
  }

  protected async onInvite(event: Event): Promise<void> {
    event.preventDefault();
    this.touched.set(true);
    const tenantId = this.tenantId();
    const email = this.email().trim();
    if (tenantId === null || !EMAIL.test(email) || this.busy() !== null) return;

    await this.act('invite', tenantId, email, async () => {
      await this.api.invite(tenantId, email);
      this.email.set('');
      this.touched.set(false);
      return 'invited';
    });
  }

  protected async resend(invitation: Invitation): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy() !== null) return;

    await this.act('row', tenantId, invitation.properties.email, async () => {
      await this.api.resendInvitation(tenantId, invitation.name);
      return 'resent';
    });
  }

  protected async revoke(invitation: Invitation): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy() !== null) return;

    await this.act('row', tenantId, invitation.properties.email, async () => {
      await this.api.revokeInvitation(tenantId, invitation.name);
      return 'revoked';
    });
  }

  protected async remove(member: Member): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy() !== null) return;

    await this.act('row', tenantId, member.properties.email, async () => {
      await this.api.removeMember(tenantId, member.name);
      return 'removed';
    });
  }

  /**
   * One action, then both lists again. ⚠ Re-read rather than patched: a resend changes the expiry,
   * a revoke the status, a removal the member's invitations too (they read `withdrawn`), and the
   * platform is the one that knows which.
   */
  private async act(
    busy: 'invite' | 'row',
    tenantId: string,
    subject: string,
    call: () => Promise<'invited' | 'resent' | 'revoked' | 'removed'>
  ): Promise<void> {
    this.busy.set(busy);
    this.outcome.set(null);

    try {
      const kind = await call();
      if (this.tenantId() !== tenantId) return;
      this.outcome.set({ kind, subject });
    } catch (error) {
      if (this.tenantId() === tenantId) this.outcome.set({ kind: 'failed', subject, message: messageOf(error) });
    } finally {
      this.busy.set(null);
      this.confirming.set(null);
    }

    if (this.tenantId() === tenantId) await this.refresh(tenantId);
  }

  private async refresh(tenantId: string): Promise<void> {
    const stale = () => this.tenantId() !== tenantId;

    await Promise.all([
      load(this.members, async () => (await this.api.listMembers(tenantId)).value.value, stale),
      load(this.invitations, async () => (await this.api.listInvitations(tenantId)).value.value, stale)
    ]);
  }
}
