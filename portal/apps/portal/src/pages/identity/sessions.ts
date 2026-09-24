import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { BladeStackStore } from '@cybercloud/shell';
import { XuiButton } from '@xui/button';
import { XuiCallout } from '@xui/callout';
import { XuiNonIdealState } from '@xui/non-ideal-state';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { XuiTag } from '@xui/tag';
import { IdentityAdminApi, Session } from '../../app/api/identity-admin';
import { links } from '../../app/routes/portal-links';
import { NeedsTenant, PageStatus, activeTenantId, load, pageState } from '../shared/page-state';
import { IdentityNav, messageOf, when } from './identity-shared';

/**
 * My sessions: every browser and device signed in as the person using the portal, and a way to
 * sign one out. Issue #41, over #94's refresh-token sessions.
 *
 * ⚠ **The person's own, and nobody else's.** The address names no user — the platform reads the
 * caller off the token — so this page needs no role, and there is no way to point it at a
 * colleague. Ending a colleague's sessions is suspending them, which is the members page's.
 *
 * ⚠ **Signing out is for the refresh chain, not the ten minutes.** The session is revoked at once
 * and its refresh token dies with it, but an access token already issued lives out its ten
 * minutes (docs/plan/11 § Sessions and revocation). The row for this very session says so,
 * because signing it out here signs the portal out at its next refresh.
 */
@Component({
  selector: 'cc-identity-sessions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    IdentityNav,
    NeedsTenant,
    PageStatus,
    XuiButton,
    XuiCallout,
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
      <h1 class="text-lg font-semibold" i18n="@@identity.sessions.heading">My sessions</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@identity.sessions.subheading">
        Everywhere you are signed in. Signing one out ends it at its next refresh; a token it already holds works for up
        to ten minutes more.
      </p>
      <cc-identity-nav />

      @if (failure(); as message) {
        <xui-callout class="mt-4" color="error" [title]="refusedTitle"
          ><p>{{ message }}</p></xui-callout
        >
      }

      <section class="mt-6" aria-labelledby="cc-sessions-heading">
        <h2 class="text-sm font-semibold" id="cc-sessions-heading" i18n="@@identity.sessions.listHeading">Signed in</h2>
        <cc-page-status [state]="sessions()" />
        @if (sessions(); as state) {
          @if (state.kind === 'ready') {
            @if (state.value.length === 0) {
              <xui-non-ideal-state class="mt-6" [title]="emptyTitle" [description]="emptyDescription" />
            } @else {
              <xui-table class="mt-3" striped aria-labelledby="cc-sessions-heading">
                <xui-tr>
                  <xui-th role="columnheader" class="flex-1" i18n="@@identity.sessions.col.device">Device</xui-th>
                  <xui-th role="columnheader" class="w-40" i18n="@@identity.sessions.col.client">Signed in to</xui-th>
                  <xui-th role="columnheader" class="w-48" i18n="@@identity.sessions.col.since">Since</xui-th>
                  <xui-th role="columnheader" class="w-48" i18n="@@identity.sessions.col.lastUsed">Last used</xui-th>
                  <xui-th role="columnheader" class="w-56"
                    ><span class="sr-only" i18n="@@identity.sessions.col.actions">Actions</span></xui-th
                  >
                </xui-tr>
                @for (session of state.value; track session.name) {
                  <xui-tr [attr.data-session]="session.name">
                    <xui-td role="cell" class="flex-1" truncate>
                      {{ session.properties.deviceLabel || unknownDevice }}
                      @if (session.properties.current) {
                        <xui-tag minimal class="ml-2" data-current>{{ thisSession }}</xui-tag>
                      }
                    </xui-td>
                    <xui-td role="cell" class="w-40" truncate
                      ><code class="text-xs">{{ session.properties.clientId }}</code></xui-td
                    >
                    <xui-td role="cell" class="w-48">{{ when(session.properties.createdAt) }}</xui-td>
                    <xui-td role="cell" class="w-48">{{ when(session.properties.lastUsedAt) }}</xui-td>
                    <xui-td role="cell" class="w-56">
                      @if (confirming() === session.name) {
                        <span class="flex flex-wrap items-center gap-2">
                          <button
                            xuiButton
                            color="error"
                            size="sm"
                            type="button"
                            [disabled]="busy()"
                            (click)="signOut(session)"
                            i18n="@@identity.sessions.confirm"
                          >
                            Sign it out
                          </button>
                          <button
                            xuiButton
                            variant="ghost"
                            size="sm"
                            type="button"
                            (click)="confirming.set(null)"
                            i18n="@@identity.sessions.keep"
                          >
                            Keep it
                          </button>
                        </span>
                      } @else {
                        <button
                          xuiButton
                          variant="outline"
                          color="error"
                          size="sm"
                          type="button"
                          [disabled]="busy()"
                          [attr.aria-label]="signOutLabel(session)"
                          (click)="confirming.set(session.name)"
                          i18n="@@identity.sessions.signOut"
                        >
                          Sign out
                        </button>
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
export class IdentitySessions {
  private readonly api = inject(IdentityAdminApi);
  private readonly blades = inject(BladeStackStore);

  protected readonly tenantId = activeTenantId();
  protected readonly sessions = pageState<readonly Session[]>();
  protected readonly busy = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly failure = signal<string | null>(null);

  protected readonly when = when;
  protected readonly thisSession = $localize`:@@identity.sessions.current:This session`;
  protected readonly unknownDevice = $localize`:@@identity.sessions.unknownDevice:Unknown device`;
  protected readonly refusedTitle = $localize`:@@identity.sessions.refused:The platform refused the request`;
  protected readonly emptyTitle = $localize`:@@identity.sessions.emptyTitle:No other sessions`;
  protected readonly emptyDescription = $localize`:@@identity.sessions.emptyDescription:Nothing else is signed in as you.`;

  constructor() {
    effect(() => {
      const tenantId = this.tenantId();

      untracked(() => {
        const route = links.identitySessions();
        this.blades.open({ id: route, title: $localize`:@@identity.sessions.blade:My sessions`, route });
        this.failure.set(null);
        this.confirming.set(null);
        if (tenantId !== null) void this.refresh(tenantId);
      });
    });
  }

  protected signOutLabel(session: Session): string {
    return session.properties.current
      ? $localize`:@@identity.sessions.signOutCurrent:Sign out this session — the portal signs in again at its next refresh`
      : $localize`:@@identity.sessions.signOutLabel:Sign out ${session.properties.deviceLabel}:device:`;
  }

  protected async signOut(session: Session): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || this.busy()) return;

    this.busy.set(true);
    this.failure.set(null);

    try {
      await this.api.revokeSession(tenantId, session.name);
    } catch (error) {
      if (this.tenantId() === tenantId) this.failure.set(messageOf(error));
    } finally {
      this.busy.set(false);
      this.confirming.set(null);
    }

    if (this.tenantId() === tenantId) await this.refresh(tenantId);
  }

  private async refresh(tenantId: string): Promise<void> {
    await load(
      this.sessions,
      async () => (await this.api.listSessions(tenantId)).value.value,
      () => this.tenantId() !== tenantId
    );
  }
}
